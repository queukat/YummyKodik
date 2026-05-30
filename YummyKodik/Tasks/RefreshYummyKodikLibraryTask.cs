// File: Tasks/RefreshYummyKodikLibraryTask.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Shikimori;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks
{
    /// <summary>
    /// Scheduled task that refreshes Yummy/Kodik-backed STRM library.
    /// </summary>
    public sealed class RefreshYummyKodikLibraryTask : IScheduledTask
    {
        private const int MaxRefreshParallelism = 2;

        private readonly ILogger<RefreshYummyKodikLibraryTask> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SemaphoreSlim _runGate = new(1, 1);
        private readonly SeriesRootLockProvider _seriesRootLockProvider = new();

        public RefreshYummyKodikLibraryTask(
            ILogger<RefreshYummyKodikLibraryTask> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = new YummyKodikLogger<RefreshYummyKodikLibraryTask>(logger);
            _httpClientFactory = httpClientFactory;
        }

        public string Key => "YummyKodikRefresh";

        public string Name => "YummyKodik library refresh";

        public string Description => "Creates/updates YummyAnime based anime series and Kodik backed STRM episodes.";

        public string Category => "YummyKodik";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance.Configuration;

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(cfg.RefreshIntervalMinutes <= 0 ? 360 : cfg.RefreshIntervalMinutes).Ticks
            };
        }

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return ExecuteInternalAsync(progress, cancellationToken);
        }

        private async Task ExecuteInternalAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            await RunWithRunGateAsync(
                    _runGate,
                    _logger,
                    () => ExecuteRefreshBodyAsync(progress, cancellationToken))
                .ConfigureAwait(false);
        }

        private async Task ExecuteRefreshBodyAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var cfg = plugin.Configuration;

            if (!ValidateConfiguration(cfg))
            {
                return;
            }

            var root = cfg.OutputRootPath;
            Directory.CreateDirectory(root);

            var refreshClients = CreateRefreshClients(cfg, cancellationToken);
            var titleKeySource = new RefreshTitleKeySource(_logger, plugin.SaveConfiguration);
            var allKeys = await titleKeySource.BuildAsync(cfg, refreshClients.Yummy, cancellationToken).ConfigureAwait(false);
            if (allKeys.Count == 0)
            {
                _logger.LogInformation("[YummyKodik] No slugs or list items configured, nothing to refresh.");
                return;
            }

            var titleService = CreateTitleService(plugin.Logger);
            await ProcessKeysInParallelAsync(
                    allKeys,
                    (key, tokenForKey) => titleService.RefreshAsync(key, root, cfg, refreshClients, tokenForKey),
                    progress,
                    _logger,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private bool ValidateConfiguration(PluginConfiguration cfg)
        {
            if (string.IsNullOrWhiteSpace(cfg.YummyClientId))
            {
                _logger.LogWarning("[YummyKodik] YummyClientId is not configured, skipping refresh.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(cfg.OutputRootPath))
            {
                _logger.LogWarning("[YummyKodik] OutputRootPath is not configured, skipping refresh.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(cfg.ServerBaseUrl))
            {
                _logger.LogWarning("[YummyKodik] ServerBaseUrl is not configured, skipping refresh.");
                return false;
            }

            return true;
        }

        private RefreshClients CreateRefreshClients(PluginConfiguration cfg, CancellationToken cancellationToken)
        {
            var yummyHttp = _httpClientFactory.CreateClient(HttpClientNames.Yummy);
            var yummyClient = new YummyClient(yummyHttp, cfg.YummyClientId, cfg.YummyApiBaseUrl);
            var shikimoriHttp = _httpClientFactory.CreateClient(HttpClientNames.Shikimori);
            var shikimoriClient = new ShikimoriGraphQlClient(shikimoriHttp);
            var kodikClients = CreateSharedLazyTask(() => CreateKodikClientsAsync(cancellationToken));
            return new RefreshClients(yummyClient, shikimoriClient, yummyHttp, kodikClients);
        }

        private RefreshTitleService CreateTitleService(ILogger titleLogger)
        {
            var artifactWriter = new EpisodeArtifactWriter();
            return new RefreshTitleService(
                titleLogger,
                new YummyRefreshInfoLoader(_httpClientFactory),
                _seriesRootLockProvider,
                new SeriesMetadataWriter(),
                new SeasonFilePreparer(),
                new YummyEpisodeArtifactGenerator(artifactWriter),
                new KodikSupplementService(artifactWriter),
                new RefreshStateService(),
                artifactWriter);
        }

        private async Task<RefreshKodikClients> CreateKodikClientsAsync(CancellationToken cancellationToken)
        {
            var kodikHttp = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
            var token = await KodikTokenProvider.GetTokenAsync(kodikHttp, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RefreshKodikClients(new KodikClient(kodikHttp, token), kodikHttp, token);
        }

        private static Lazy<Task<T>> CreateSharedLazyTask<T>(Func<Task<T>> factory)
        {
            ArgumentNullException.ThrowIfNull(factory);
            return new Lazy<Task<T>>(factory, LazyThreadSafetyMode.ExecutionAndPublication);
        }

        private static async Task<bool> RunWithRunGateAsync(
            SemaphoreSlim runGate,
            ILogger logger,
            Func<Task> action)
        {
            if (!await runGate.WaitAsync(0).ConfigureAwait(false))
            {
                logger.LogInformation("[YummyKodik] Refresh is already running, skipping this run.");
                return false;
            }

            try
            {
                await action().ConfigureAwait(false);
                return true;
            }
            finally
            {
                runGate.Release();
            }
        }

        private static async Task ProcessKeysInParallelAsync(
            IReadOnlyList<string> allKeys,
            Func<string, CancellationToken, Task> refreshKeyAsync,
            IProgress<double>? progress,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var perItemStep = 100.0 / allKeys.Count;
            var completed = 0;
            var progressGate = new object();

            progress?.Report(0);

            await Parallel.ForEachAsync(
                    allKeys,
                    new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = MaxRefreshParallelism
                    },
                    async (key, tokenForKey) =>
                    {
                        try
                        {
                            await refreshKeyAsync(key, tokenForKey).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!tokenForKey.IsCancellationRequested)
                        {
                            logger.LogError(ex, "[YummyKodik] Failed to refresh key '{Key}': {Message}", key, ex.Message);
                        }
                        finally
                        {
                            var currentCompleted = Interlocked.Increment(ref completed);
                            lock (progressGate)
                            {
                                progress?.Report(Math.Min(100.0, perItemStep * currentCompleted));
                            }
                        }
                    })
                .ConfigureAwait(false);
        }
    }
}
