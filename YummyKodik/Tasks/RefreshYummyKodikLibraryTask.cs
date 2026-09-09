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
using YummyKodik.Media;
using YummyKodik.Shikimori;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Versioning;
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
        private readonly IInternalJellyfinUrlProvider _internalJellyfinUrlProvider;
        private readonly YummyKodikPostRefreshMergeBarrier _postRefreshMergeBarrier;
        private readonly SemaphoreSlim _runGate = new(1, 1);
        private readonly SeriesRootLockProvider _seriesRootLockProvider = new();

        public RefreshYummyKodikLibraryTask(
            ILogger<RefreshYummyKodikLibraryTask> logger,
            IHttpClientFactory httpClientFactory,
            IInternalJellyfinUrlProvider internalJellyfinUrlProvider,
            YummyKodikPostRefreshMergeBarrier postRefreshMergeBarrier)
        {
            _logger = new YummyKodikLogger<RefreshYummyKodikLibraryTask>(logger);
            _httpClientFactory = httpClientFactory;
            _internalJellyfinUrlProvider = internalJellyfinUrlProvider;
            _postRefreshMergeBarrier = postRefreshMergeBarrier;
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

            var runMetrics = new RefreshRunMetrics(cfg.EnablePerformanceDebugLogging);
            var runOutcome = "completed";
            try
            {
                var root = cfg.OutputRootPath;
                Directory.CreateDirectory(root);
                var internalBaseUrl = _internalJellyfinUrlProvider.GetBaseUrl();
                var refreshStartedUtc = DateTime.UtcNow;
                IDisposable mergeBatch;
                using (runMetrics.Measure("phase.merge_batch.begin"))
                {
                    mergeBatch = await _postRefreshMergeBarrier
                        .BeginRefreshBatchAsync(cancellationToken)
                        .ConfigureAwait(false);
                }

                using (mergeBatch)
                {
                    IReadOnlyCollection<string> initialStrmPaths;
                    using (runMetrics.Measure("phase.strm_snapshot"))
                    {
                        initialStrmPaths = _postRefreshMergeBarrier.CaptureCurrentStrmPaths(root);
                    }

                    runMetrics.AddCount("files.strm_snapshot", initialStrmPaths.Count);

                    using (runMetrics.Measure("phase.runtime_backfill.pre"))
                    {
                        var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var updated = await EpisodeRuntimeBackfillService
                            .BackfillMissingAsync(root, _logger, cancellationToken, updatedPaths)
                            .ConfigureAwait(false);
                        runMetrics.AddCount("runtime.backfill_updated", updated);
                        var stateHashesUpdated = await ReconcileRuntimeBackfillStateAsync(
                                root,
                                updatedPaths,
                                cancellationToken)
                            .ConfigureAwait(false);
                        runMetrics.AddCount("runtime.state_hashes_updated", stateHashesUpdated);
                    }

                    using (runMetrics.Measure("phase.runtime_publish.pre"))
                    {
                        await _postRefreshMergeBarrier
                            .ApplyAvailableEpisodeRunTimesAsync(root, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    var refreshClients = CreateRefreshClients(cfg, runMetrics, cancellationToken);
                    var titleKeySource = new RefreshTitleKeySource(_logger, plugin.SaveConfiguration);
                    List<string> allKeys;
                    using (runMetrics.Measure("phase.title_keys"))
                    {
                        allKeys = await titleKeySource.BuildAsync(cfg, refreshClients.Yummy, cancellationToken).ConfigureAwait(false);
                        AddExistingLibraryKeysAfterUserListFailure(root, titleKeySource, allKeys);
                    }

                    runMetrics.AddCount("titles.total", allKeys.Count);
                    using (runMetrics.Measure("phase.stale_cleanup"))
                    {
                        await CleanupStaleReleasesAfterSuccessfulUserListFetchAsync(
                                root,
                                cfg,
                                titleKeySource,
                                allKeys,
                                runMetrics,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    if (allKeys.Count == 0)
                    {
                        _logger.LogInformation("[YummyKodik] No slugs or list items configured, nothing to refresh.");
                    }
                    else
                    {
                        var titleService = CreateTitleService(plugin.Logger);
                        using (runMetrics.Measure("phase.titles"))
                        {
                            await ProcessKeysInParallelAsync(
                                    allKeys,
                                    (key, tokenForKey) => titleService.RefreshAsync(key, root, internalBaseUrl, cfg, refreshClients, tokenForKey),
                                    progress,
                                    _logger,
                                    runMetrics,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }

                    using (runMetrics.Measure("phase.runtime_backfill.post"))
                    {
                        var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        var updated = await EpisodeRuntimeBackfillService
                            .BackfillMissingAsync(root, _logger, cancellationToken, updatedPaths)
                            .ConfigureAwait(false);
                        runMetrics.AddCount("runtime.backfill_updated", updated);
                        var stateHashesUpdated = await ReconcileRuntimeBackfillStateAsync(
                                root,
                                updatedPaths,
                                cancellationToken)
                            .ConfigureAwait(false);
                        runMetrics.AddCount("runtime.state_hashes_updated", stateHashesUpdated);
                    }

                    using (runMetrics.Measure("phase.readiness_merge"))
                    {
                        await _postRefreshMergeBarrier
                            .WaitForEpisodesThenMergeAsync(root, refreshStartedUtc, initialStrmPaths, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                runOutcome = "cancelled";
                throw;
            }
            catch
            {
                runOutcome = "failed";
                throw;
            }
            finally
            {
                runMetrics.LogSummary(_logger, runOutcome);
            }
        }

        private async Task CleanupStaleReleasesAfterSuccessfulUserListFetchAsync(
            string root,
            PluginConfiguration cfg,
            RefreshTitleKeySource titleKeySource,
            IReadOnlyList<string> allKeys,
            RefreshRunMetrics runMetrics,
            CancellationToken cancellationToken)
        {
            if (!cfg.DeleteReleasesNotInYummyList)
            {
                return;
            }

            if (!cfg.UseUserListSubscription || !titleKeySource.UserListFetchSucceeded)
            {
                _logger.LogWarning(
                    "[YummyKodik] DeleteReleasesNotInYummyList is enabled, but stale-release cleanup was skipped because the current Yummy user list was not fetched successfully.");
                return;
            }

            var normalizedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in allKeys)
            {
                var normalized = RefreshPathUtilities.NormalizeKey(key);
                if (normalized.Length > 0)
                {
                    normalizedKeys.Add(normalized);
                }
            }

            var result = await StaleReleaseCleanupService
                .CleanupAsync(root, normalizedKeys, _logger, cancellationToken)
                .ConfigureAwait(false);

            runMetrics.AddCount("cleanup.examined", result.ExaminedDirectoryCount);
            runMetrics.AddCount("cleanup.deleted_series", result.DeletedSeriesDirectoryCount);
            runMetrics.AddCount("cleanup.deleted_seasons", result.DeletedSeasonDirectoryCount);
            runMetrics.AddCount("cleanup.skipped", result.SkippedDirectoryCount);

            _logger.LogInformation(
                "[YummyKodik] Stale-release cleanup completed. examined={Examined} retained={Retained} deletedSeries={DeletedSeries} deletedSeasons={DeletedSeasons} skipped={Skipped} deleteFailed={DeleteFailed} stateWriteFailed={StateWriteFailed}",
                result.ExaminedDirectoryCount,
                result.RetainedDirectoryCount,
                result.DeletedSeriesDirectoryCount,
                result.DeletedSeasonDirectoryCount,
                result.SkippedDirectoryCount,
                result.DeleteFailureCount,
                result.StateWriteFailureCount);
        }

        private void AddExistingLibraryKeysAfterUserListFailure(
            string root,
            RefreshTitleKeySource titleKeySource,
            List<string> allKeys)
        {
            if (!titleKeySource.UserListFetchFailed)
            {
                return;
            }

            var existingKeys = ExistingLibraryFallbackRefreshInfoLoader.FindExistingCleanKeys(root);
            if (existingKeys.Count == 0)
            {
                return;
            }

            var before = allKeys.Count;
            var set = new HashSet<string>(allKeys, StringComparer.OrdinalIgnoreCase);
            foreach (var key in existingKeys)
            {
                if (set.Add(key))
                {
                    allKeys.Add(key);
                }
            }

            var added = allKeys.Count - before;
            if (added > 0)
            {
                _logger.LogWarning(
                    "[YummyKodik] Yummy user list is unavailable; added {Count} existing local title key(s) for provider-only fallback refresh.",
                    added);
            }
        }

        private async Task<int> ReconcileRuntimeBackfillStateAsync(
            string outputRoot,
            IReadOnlyCollection<string> updatedPaths,
            CancellationToken cancellationToken)
        {
            if (updatedPaths.Count == 0)
            {
                return 0;
            }

            try
            {
                return await RefreshStateManager
                    .ReconcileManagedFileHashesAsync(outputRoot, updatedPaths, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                !cancellationToken.IsCancellationRequested &&
                ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger.LogWarning(
                    ex,
                    "[YummyKodik] Runtime NFO backfill succeeded, but its managed state hashes could not be reconciled. The next refresh will safely revalidate those titles.");
                return 0;
            }
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

            return true;
        }

        private RefreshClients CreateRefreshClients(
            PluginConfiguration cfg,
            RefreshRunMetrics runMetrics,
            CancellationToken cancellationToken)
        {
            var yummyHttp = _httpClientFactory.CreateClient(HttpClientNames.Yummy);
            var yummyClient = new YummyClient(yummyHttp, cfg.YummyClientId, cfg.YummyApiBaseUrl);
            var shikimoriHttp = _httpClientFactory.CreateClient(HttpClientNames.Shikimori);
            var shikimoriClient = new ShikimoriGraphQlClient(shikimoriHttp);
            var kodikClients = CreateSharedLazyTask(() => CreateKodikClientsAsync(runMetrics, cancellationToken));
            return new RefreshClients(yummyClient, shikimoriClient, yummyHttp, kodikClients, runMetrics);
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

        private async Task<RefreshKodikClients> CreateKodikClientsAsync(
            RefreshRunMetrics runMetrics,
            CancellationToken cancellationToken)
        {
            var kodikHttp = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
            var token = await KodikTokenProvider.GetTokenAsync(kodikHttp, cancellationToken: cancellationToken).ConfigureAwait(false);
            return new RefreshKodikClients(
                new KodikClient(kodikHttp, token, runMetrics.AddCount, cancellationToken, _logger),
                kodikHttp,
                token);
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
            RefreshRunMetrics? runMetrics,
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
                            runMetrics?.AddCount("titles.attempted");
                            await refreshKeyAsync(key, tokenForKey).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (!tokenForKey.IsCancellationRequested)
                        {
                            runMetrics?.AddCount("titles.unhandled_failed");
                            logger.LogError(ex, "[YummyKodik] Failed to refresh key '{Key}': {Message}", key, ex.Message);
                        }
                        finally
                        {
                            runMetrics?.AddCount("titles.finished");
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
