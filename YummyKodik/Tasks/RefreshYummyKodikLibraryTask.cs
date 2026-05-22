// File: Tasks/RefreshYummyKodikLibraryTask.cs

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Shikimori;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks
{
    /// <summary>
    /// Scheduled task that refreshes Yummy/Kodik-backed STRM library.
    /// </summary>
    public sealed class RefreshYummyKodikLibraryTask : IScheduledTask
    {
        private static readonly YummyVideoProviderKind[] PreferredYummyProviderOrder =
        {
            YummyVideoProviderKind.Alloha,
            YummyVideoProviderKind.Cvh
        };

        private const string HlsFormatQuerySuffix = "&format=hls";
        private const string StrmExtension = ".strm";
        private const string NfoExtension = ".nfo";
        private const int MaxRefreshParallelism = 2;

        private readonly ILogger<RefreshYummyKodikLibraryTask> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SemaphoreSlim _runGate = new(1, 1);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _seriesRootLocks = new(StringComparer.OrdinalIgnoreCase);

        public RefreshYummyKodikLibraryTask(
            ILogger<RefreshYummyKodikLibraryTask> logger,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
        }

        public string Key => "YummyKodikRefresh";

        public string Name => "YummyKodik library refresh";

        public string Description => "Creates/updates YummyAnime based anime series and Kodik backed STRM episodes.";

        public string Category => "YummyKodik";

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

            if (string.IsNullOrWhiteSpace(cfg.YummyClientId))
            {
                _logger.LogWarning("[YummyKodik] YummyClientId is not configured, skipping refresh.");
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.OutputRootPath))
            {
                _logger.LogWarning("[YummyKodik] OutputRootPath is not configured, skipping refresh.");
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.ServerBaseUrl))
            {
                _logger.LogWarning("[YummyKodik] ServerBaseUrl is not configured, skipping refresh.");
                return;
            }

            var root = cfg.OutputRootPath;
            Directory.CreateDirectory(root);

            var yummyHttp = _httpClientFactory.CreateClient(HttpClientNames.Yummy);

            var yummyClient = new YummyClient(yummyHttp, cfg.YummyClientId, cfg.YummyApiBaseUrl);
            var shikimoriHttp = _httpClientFactory.CreateClient(HttpClientNames.Shikimori);
            var shikimoriClient = new ShikimoriGraphQlClient(shikimoriHttp);
            var kodikClients = CreateSharedLazyTask(() => CreateKodikClientsAsync(cancellationToken));
            var refreshClients = new RefreshClients(yummyClient, shikimoriClient, yummyHttp, kodikClients);

            var allKeys = await BuildAnimeKeysAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
            if (allKeys.Count == 0)
            {
                _logger.LogInformation("[YummyKodik] No slugs or list items configured, nothing to refresh.");
                return;
            }

            await ProcessKeysInParallelAsync(
                    allKeys,
                    (key, tokenForKey) => RefreshSingleAnimeAsync(key, root, refreshClients, tokenForKey),
                    progress,
                    _logger,
                    cancellationToken)
                .ConfigureAwait(false);
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

        private async Task<List<string>> BuildAnimeKeysAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            CancellationToken cancellationToken)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            AddConfiguredAnimeKeys(cfg.Slugs, set);
            await AddUserListAnimeKeysAsync(cfg, yummyClient, set, cancellationToken).ConfigureAwait(false);

            return set.ToList();
        }

        private static void AddConfiguredAnimeKeys(IEnumerable<string>? slugs, HashSet<string> keys)
        {
            if (slugs == null)
            {
                return;
            }

            foreach (var slug in slugs)
            {
                var key = NormalizeKey(slug);
                if (!string.IsNullOrEmpty(key))
                {
                    keys.Add(key);
                }
            }
        }

        private async Task AddUserListAnimeKeysAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            HashSet<string> keys,
            CancellationToken cancellationToken)
        {
            if (!cfg.UseUserListSubscription)
            {
                return;
            }

            if (cfg.YummyUserId <= 0)
            {
                _logger.LogWarning("[YummyKodik] UseUserListSubscription enabled, but YummyUserId is not set.");
                return;
            }

            var listId = cfg.YummyUserListId < 0 ? 0 : cfg.YummyUserListId;

            try
            {
                await EnsureAuthenticatedAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
                var items = await FetchUserListWithRetryAsync(cfg, yummyClient, listId, cancellationToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "[YummyKodik] User list fetched. userId={UserId} listId={ListId} items={Count}",
                    cfg.YummyUserId,
                    listId,
                    items.Count);

                AddUserListItemKeys(items, keys);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[YummyKodik] Failed to fetch user list, falling back to manual slugs only.");
            }
        }

        private async Task<IReadOnlyList<YummyUserListItem>> FetchUserListWithRetryAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            int listId,
            CancellationToken cancellationToken)
        {
            try
            {
                return await yummyClient
                    .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex) when (!string.IsNullOrWhiteSpace(yummyClient.GetAccessToken()))
            {
                _logger.LogWarning(ex, "[YummyKodik] User list unauthorized, trying token refresh and retry.");
                await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

                return await yummyClient
                    .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static void AddUserListItemKeys(IEnumerable<YummyUserListItem> items, HashSet<string> keys)
        {
            foreach (var item in items)
            {
                var key = NormalizeKey(item.AnimeUrl);
                if (!string.IsNullOrEmpty(key))
                {
                    keys.Add(key);
                    continue;
                }

                if (item.AnimeId > 0)
                {
                    keys.Add(item.AnimeId.ToString());
                }
            }
        }

        private async Task EnsureAuthenticatedAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;

            if (!string.IsNullOrWhiteSpace(cfg.YummyAccessToken))
            {
                yummyClient.SetAccessToken(cfg.YummyAccessToken);

                try
                {
                    var refreshed = await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

                    if (!string.Equals(cfg.YummyAccessToken, refreshed, StringComparison.Ordinal))
                    {
                        cfg.YummyAccessToken = refreshed;
                        plugin.SaveConfiguration();
                        _logger.LogInformation("[YummyKodik] Yummy access token refreshed and saved.");
                    }

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[YummyKodik] Token refresh failed, will try login if credentials exist.");
                }
            }

            if (!string.IsNullOrWhiteSpace(cfg.YummyLogin) && !string.IsNullOrWhiteSpace(cfg.YummyPassword))
            {
                _logger.LogInformation("[YummyKodik] Logging in to Yummy to obtain user token.");

                var token = await yummyClient.LoginAsync(
                        cfg.YummyLogin.Trim(),
                        cfg.YummyPassword,
                        string.IsNullOrWhiteSpace(cfg.YummyRecaptchaResponse) ? null : cfg.YummyRecaptchaResponse.Trim(),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(token))
                {
                    cfg.YummyAccessToken = token;
                    plugin.SaveConfiguration();
                    _logger.LogInformation("[YummyKodik] Yummy access token obtained and saved.");
                }

                return;
            }

            _logger.LogWarning(
                "[YummyKodik] User list subscription enabled but no auth is configured. Set YummyAccessToken or YummyLogin and YummyPassword.");
        }

        private async Task<ShikimoriSeriesLayoutInfo?> TryResolveSeriesLayoutFromShikimoriAsync(
            YummyAnimeResponse anime,
            ShikimoriGraphQlClient shikimori,
            CancellationToken cancellationToken)
        {
            if (anime?.RemoteIds?.ShikimoriId is not long shikimoriId || shikimoriId <= 0)
            {
                return null;
            }

            try
            {
                return await shikimori.TryResolveSeriesLayoutAsync(shikimoriId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[YummyKodik] Failed to resolve Shikimori series layout for animeId={AnimeId} shikimoriId={ShikimoriId}. Falling back to Yummy metadata only.",
                    anime.AnimeId,
                    shikimoriId);
                return null;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance.Configuration;

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(cfg.RefreshIntervalMinutes <= 0 ? 360 : cfg.RefreshIntervalMinutes).Ticks
            };
        }

        private async Task RefreshSingleAnimeAsync(
            string key,
            string root,
            RefreshClients clients,
            CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var logger = plugin.Logger;
            var cfg = plugin.Configuration;
            var perf = new RefreshPerformanceMetrics(cfg.EnablePerformanceDebugLogging);

            var cleanKey = NormalizeKey(key);
            logger.LogInformation("[YummyKodik] Refreshing key '{Key}'.", cleanKey);
            var summaryTitle = cleanKey;

            try
            {
                var refresh = await LoadYummyRefreshInfoAsync(logger, cfg, clients, cleanKey, root, perf, cancellationToken)
                    .ConfigureAwait(false);
                summaryTitle = refresh.TitleInfo.Title;

                var refreshStateInput = BuildRefreshStateSeasonInput(cfg, refresh);
                var state = new EpisodeGenerationState();
                var seasonDir = refresh.Files.SeasonDir;
                var seasonDirPrepared = false;
                var needsKodikLookup = false;

                using (await AcquireSeriesRootLockAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
                {
                    Directory.CreateDirectory(refresh.Files.SeriesRoot);

                    await EnsureSeriesMetadataAsync(logger, refresh, clients.YummyHttp, perf, cancellationToken).ConfigureAwait(false);

                    if (await TrySkipRefreshFromStateAsync(logger, refresh, refreshStateInput, perf, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }

                    if (refresh.Availability.YummySupportedEpisodes.Length > 0)
                    {
                        if (string.IsNullOrEmpty(refresh.Files.BaseUrl))
                        {
                            logger.LogWarning(
                                "[YummyKodik] ServerBaseUrl is empty, skipping Yummy-backed STRM generation for '{Title}'.",
                                refresh.TitleInfo.Title);
                            return;
                        }

                        seasonDir = PrepareSeasonDirectoryForEpisodeGeneration(
                            logger,
                            refresh,
                            seasonDir,
                            cfg.CreateStrmPerVoiceTranslation,
                            state,
                            perf);
                        seasonDirPrepared = true;

                        using (perf.Measure("stage.generate.yummy.files"))
                        {
                            await GeneratePreferredProviderEpisodeFilesAsync(
                                    new YummyEpisodeGenerationContext(
                                        logger,
                                        refresh,
                                        state,
                                        seasonDir,
                                        cfg.CreateStrmPerVoiceTranslation,
                                        cfg.PreferredTranslationFilter,
                                        perf),
                                    refresh.Availability.YummySupportedEpisodes,
                                    cancellationToken)
                                .ConfigureAwait(false);
                        }

                        state.GeneratedEpisodeNumbers.UnionWith(refresh.Availability.YummySupportedEpisodes);

                        logger.LogInformation(
                            "[YummyKodik] Generated mixed Yummy-backed episode files for '{Title}'. episodes={EpisodeCount} allohaEpisodes={AllohaEpisodes} cvhEpisodes={CvhEpisodes} availableEpisodes={AvailableEpisodes}",
                            refresh.TitleInfo.Title,
                            refresh.Availability.YummySupportedEpisodes.Length,
                            refresh.Availability.AllohaSupportedEpisodes.Length,
                            refresh.Availability.CvhSupportedEpisodes.Length,
                            refresh.Availability.ExpectedAvailableEpisodes);
                    }

                    var needsKodikEpisodeSupplement = YummyEpisodeAvailability.NeedsKodikSupplement(
                        refresh.TitleInfo.Anime,
                        state.GeneratedEpisodeNumbers,
                        refresh.Availability.KnownSupportedEpisodes);
                    var needsKodikTranslationSupplement = cfg.CreateStrmPerVoiceTranslation &&
                                                         refresh.Availability.ExpectedAvailableEpisodes > 0;

                    if (!needsKodikEpisodeSupplement && !needsKodikTranslationSupplement)
                    {
                        CompleteWithoutKodikSupplement(logger, refresh, state, seasonDir, seasonDirPrepared, perf);
                        await WriteRefreshStateAsync(logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    needsKodikLookup = true;
                }

                if (!needsKodikLookup)
                {
                    return;
                }

                var kodikLookup = await TryResolveKodikInfoAsync(logger, refresh, cleanKey, clients, perf, cancellationToken)
                    .ConfigureAwait(false);
                if (kodikLookup == null)
                {
                    return;
                }

                var kodikClients = await clients.KodikClients.Value.ConfigureAwait(false);

                using (await AcquireSeriesRootLockAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
                {
                    Directory.CreateDirectory(refresh.Files.SeriesRoot);

                    var kodikAvailableEpisodes = YummyEpisodeAvailability.ResolveKodikAvailableEpisodeCount(
                        kodikLookup.Info.SeriesCount,
                        refresh.Availability.ExpectedAvailableEpisodes);
                    if (CompleteIfKodikHasNoEpisodes(logger, refresh, state, kodikAvailableEpisodes))
                    {
                        await WriteRefreshStateAsync(logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    LogKodikZeroSeriesCountIfNeeded(logger, refresh, kodikLookup.Info, kodikAvailableEpisodes);

                    if (string.IsNullOrEmpty(refresh.Files.BaseUrl))
                    {
                        logger.LogWarning("[YummyKodik] ServerBaseUrl is empty, skipping refresh for '{Title}'.", refresh.TitleInfo.Title);
                        return;
                    }

                    if (!seasonDirPrepared)
                    {
                        seasonDir = PrepareSeasonDirectoryForEpisodeGeneration(
                            logger,
                            refresh,
                            seasonDir,
                            cfg.CreateStrmPerVoiceTranslation,
                            state,
                            perf);
                    }

                    var missingEpisodes = Enumerable.Range(1, kodikAvailableEpisodes)
                        .Where(ep => !state.GeneratedEpisodeNumbers.Contains(ep))
                        .ToArray();
                    var kodikEpisodesToProcess = ResolveKodikEpisodesToProcess(
                        cfg.CreateStrmPerVoiceTranslation,
                        kodikAvailableEpisodes,
                        missingEpisodes);

                    if (kodikEpisodesToProcess.Length == 0)
                    {
                        CleanupExpectedEpisodeArtifacts(logger, refresh, state, seasonDir, kodikAvailableEpisodes, perf);
                        await WriteRefreshStateAsync(logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);

                        logger.LogInformation(
                            "[YummyKodik] Done refreshing '{Title}'. Yummy-backed providers already cover all {SeriesCount} currently available episodes.",
                            refresh.TitleInfo.Title,
                            kodikLookup.Info.SeriesCount);
                        return;
                    }

                    EpisodeArtifactGenerationResult kodikGeneration;
                    using (perf.Measure("stage.generate.kodik.files"))
                    {
                        kodikGeneration = await GenerateKodikEpisodeFilesAsync(
                                kodikEpisodesToProcess,
                                new KodikEpisodeGenerationContext(
                                    logger,
                                    refresh,
                                    kodikLookup,
                                    kodikClients.Kodik,
                                    state,
                                    seasonDir,
                                    cfg.CreateStrmPerVoiceTranslation,
                                    perf),
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    CleanupExpectedEpisodeArtifacts(logger, refresh, state, seasonDir, kodikAvailableEpisodes, perf);
                    await WriteRefreshStateAsync(logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);

                    if (missingEpisodes.Length == 0 && kodikGeneration.FilesWritten == 0)
                    {
                        logger.LogInformation(
                            "[YummyKodik] Done refreshing '{Title}'. Yummy-backed providers already cover all currently available episodes and translations.",
                            refresh.TitleInfo.Title);
                        return;
                    }

                    logger.LogInformation(
                        "[YummyKodik] Done refreshing '{Title}'. SeriesCount: {SeriesCount}, translations: {Translations}, supplementedEpisodes: {SupplementedEpisodes}, supplementedFiles: {SupplementedFiles}.",
                        refresh.TitleInfo.Title,
                        kodikLookup.Info.SeriesCount,
                        kodikLookup.Info.Translations.Count,
                        kodikGeneration.EpisodesWritten,
                        kodikGeneration.FilesWritten);
                }
            }
            finally
            {
                perf.LogSummary(logger, summaryTitle, cleanKey);
            }
        }

        private async Task<SeriesRootLockReleaser> AcquireSeriesRootLockAsync(
            string seriesRoot,
            CancellationToken cancellationToken)
        {
            var key = NormalizeSeriesRootLockKey(seriesRoot);
            var gate = _seriesRootLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new SeriesRootLockReleaser(gate);
        }

        private static string NormalizeSeriesRootLockKey(string seriesRoot)
        {
            return Path.GetFullPath(seriesRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        private static async Task<bool> TrySkipRefreshFromStateAsync(
            ILogger logger,
            YummyRefreshInfo refresh,
            RefreshStateSeasonInput refreshStateInput,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            using (perf.Measure("stage.refresh.state.check"))
            {
                try
                {
                    var canSkip = await RefreshStateManager
                        .CanSkipSingleFileRefreshAsync(refresh.Files.SeriesRoot, refreshStateInput, cancellationToken)
                        .ConfigureAwait(false);
                    if (!canSkip)
                    {
                        return false;
                    }

                    perf.AddCount("state.pre_skip");
                    logger.LogInformation(
                        "[YummyKodik] Skipping '{Title}' because refresh state matches generated files.",
                        refresh.TitleInfo.Title);
                    return true;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    perf.AddCount("state.check_failed");
                    logger.LogDebug(
                        ex,
                        "[YummyKodik] Refresh state check failed for '{Title}', running full refresh.",
                        refresh.TitleInfo.Title);
                    return false;
                }
            }
        }

        private static async Task WriteRefreshStateAsync(
            ILogger logger,
            YummyRefreshInfo refresh,
            RefreshStateSeasonInput refreshStateInput,
            EpisodeGenerationState state,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            using (perf.Measure("stage.refresh.state.write"))
            {
                try
                {
                    var written = await RefreshStateManager
                        .WriteSeasonStateAsync(
                            refresh.Files.SeriesRoot,
                            refreshStateInput,
                            state.ExpectedEpisodeFileBaseNames,
                            cancellationToken)
                        .ConfigureAwait(false);

                    perf.AddCount(written ? "state.written" : "state.write_skipped_incomplete_files");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    perf.AddCount("state.write_failed");
                    logger.LogDebug(
                        ex,
                        "[YummyKodik] Failed to write refresh state for '{Title}'.",
                        refresh.TitleInfo.Title);
                }
            }
        }

        private static RefreshStateSeasonInput BuildRefreshStateSeasonInput(
            PluginConfiguration cfg,
            YummyRefreshInfo refresh)
        {
            var seasonKey = RefreshStateManager.BuildSeasonKey(refresh.TitleInfo.SeasonNumber);
            return new RefreshStateSeasonInput
            {
                SeasonKey = seasonKey,
                SeasonNumber = refresh.TitleInfo.SeasonNumber,
                CleanKey = refresh.TitleInfo.CleanKey,
                CreateStrmPerVoiceTranslation = cfg.CreateStrmPerVoiceTranslation,
                Fingerprint = BuildRefreshFingerprint(cfg, refresh, seasonKey),
                ExpectedAvailableEpisodes = refresh.Availability.ExpectedAvailableEpisodes
            };
        }

        private static string BuildRefreshFingerprint(
            PluginConfiguration cfg,
            YummyRefreshInfo refresh,
            string seasonKey)
        {
            var remoteIds = refresh.TitleInfo.Anime.RemoteIds;
            return RefreshStateManager.BuildFingerprint(new RefreshStateFingerprintInput
            {
                Mode = BuildRefreshStateMode(cfg.CreateStrmPerVoiceTranslation),
                ServerBaseUrl = cfg.ServerBaseUrl,
                PreferredTranslationFilter = cfg.PreferredTranslationFilter,
                CleanKey = refresh.TitleInfo.CleanKey,
                RawTitle = refresh.TitleInfo.RawTitle,
                SeriesTitle = refresh.TitleInfo.Title,
                SeasonKey = seasonKey,
                SeasonNumber = refresh.TitleInfo.SeasonNumber,
                AnimeId = refresh.TitleInfo.Anime.AnimeId,
                AnimeUrl = refresh.TitleInfo.Anime.AnimeUrl,
                ShikimoriId = remoteIds?.ShikimoriId,
                KinopoiskId = remoteIds?.KpId,
                ImdbId = remoteIds?.ImdbId ?? string.Empty,
                ExpectedAvailableEpisodes = refresh.Availability.ExpectedAvailableEpisodes,
                KnownSupportedEpisodes = refresh.Availability.KnownSupportedEpisodes.ToArray(),
                AllohaSupportedEpisodes = refresh.Availability.AllohaSupportedEpisodes,
                CvhSupportedEpisodes = refresh.Availability.CvhSupportedEpisodes,
                YummySupportedEpisodes = refresh.Availability.YummySupportedEpisodes,
                ProviderCoverage = BuildProviderCoverageFingerprintItems(cfg, refresh),
                AllohaApiBaseUrl = cfg.AllohaApiBaseUrl,
                AllohaApiTokenHash = RefreshStateManager.HashSecret(cfg.AllohaApiToken)
            });
        }

        private static string BuildRefreshStateMode(bool createStrmPerVoiceTranslation)
        {
            return createStrmPerVoiceTranslation ? "per-voice" : "single-file";
        }

        private static IReadOnlyList<string> BuildProviderCoverageFingerprintItems(
            PluginConfiguration cfg,
            YummyRefreshInfo refresh)
        {
            var items = new List<string>();
            foreach (var episodeNumber in refresh.Availability.YummySupportedEpisodes
                         .Where(ep => ep > 0)
                         .Distinct()
                         .OrderBy(ep => ep))
            {
                var preferredProvider = refresh.VideoCatalog.PickPreferredProvider(
                    episodeNumber,
                    preferredFilter: cfg.PreferredTranslationFilter,
                    providers: PreferredYummyProviderOrder);
                items.Add($"ep:{episodeNumber}:preferred:{preferredProvider?.ToString() ?? "none"}");

                if (!cfg.CreateStrmPerVoiceTranslation)
                {
                    continue;
                }

                foreach (var voiceName in refresh.VideoCatalog.GetSupportedVoiceNamesAcrossProviders(
                             episodeNumber,
                             PreferredYummyProviderOrder))
                {
                    var voiceProvider = refresh.VideoCatalog.PickPreferredProvider(
                        episodeNumber,
                        explicitVoiceName: voiceName,
                        providers: PreferredYummyProviderOrder);
                    items.Add($"ep:{episodeNumber}:voice:{voiceName}:provider:{voiceProvider?.ToString() ?? "none"}");

                    if (!voiceProvider.HasValue)
                    {
                        continue;
                    }

                    var entry = refresh.VideoCatalog.FindPreferredPlayableEntry(
                        voiceProvider.Value,
                        episodeNumber,
                        voiceName);
                    AddProviderSourceFingerprintItems(items, episodeNumber, voiceName, voiceProvider.Value, entry);
                }
            }

            return items;
        }

        private static void AddProviderSourceFingerprintItems(
            List<string> items,
            int episodeNumber,
            string voiceName,
            YummyVideoProviderKind provider,
            YummyVideoEntry? entry)
        {
            var voiceKey = TranslationNameKeyNormalizer.Normalize(voiceName);
            switch (provider)
            {
                case YummyVideoProviderKind.Alloha when entry?.Alloha != null:
                    items.Add(
                        $"ep:{episodeNumber}:voiceKey:{voiceKey}:alloha:" +
                        $"translation:{entry.Alloha.TranslationId}:" +
                        $"season:{entry.Alloha.SeasonNumber}:" +
                        $"episode:{entry.Alloha.EpisodeNumber}:" +
                        $"hidden:{entry.Alloha.Hidden}:" +
                        $"movie:{RefreshStateManager.HashSecret(entry.Alloha.MovieToken)}:" +
                        $"request:{RefreshStateManager.HashSecret(entry.Alloha.RequestToken)}:" +
                        $"referer:{RefreshStateManager.HashSecret(entry.Alloha.RefererUrl)}");
                    break;
                case YummyVideoProviderKind.Cvh when entry?.Cvh != null:
                    items.Add(
                        $"ep:{episodeNumber}:voiceKey:{voiceKey}:cvh:" +
                        $"anime:{entry.Cvh.AnimeId}:" +
                        $"episode:{entry.Cvh.EpisodeNumber}:" +
                        $"dubbingCode:{entry.Cvh.DubbingCode}:" +
                        $"dubbingName:{entry.Cvh.DubbingName}:" +
                        $"aggregator:{entry.Cvh.Aggregator}:" +
                        $"publisher:{entry.Cvh.PublisherId}");
                    break;
            }
        }

        private async Task<YummyRefreshInfo> LoadYummyRefreshInfoAsync(
            ILogger logger,
            PluginConfiguration cfg,
            RefreshClients clients,
            string cleanKey,
            string root,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            YummyAnimeResponse anime;
            using (perf.Measure("stage.yummy.fetch"))
            {
                anime = await clients.Yummy.GetAnimeAsync(cleanKey, includeVideos: true, cancellationToken).ConfigureAwait(false);
            }

            var titleInfo = await ResolveYummyAnimeTitleInfoAsync(logger, anime, cleanKey, clients.Shikimori, perf, cancellationToken)
                .ConfigureAwait(false);
            var videoCatalog = await LoadYummyVideoCatalogAsync(logger, cfg, titleInfo, perf, cancellationToken).ConfigureAwait(false);
            var files = BuildSeriesFileInfo(logger, root, titleInfo, cfg);
            var availability = BuildEpisodeAvailabilityInfo(titleInfo.Anime, videoCatalog);

            return new YummyRefreshInfo(titleInfo, videoCatalog, files, availability);
        }

        private async Task<YummyAnimeTitleInfo> ResolveYummyAnimeTitleInfoAsync(
            ILogger logger,
            YummyAnimeResponse anime,
            string cleanKey,
            ShikimoriGraphQlClient shikimori,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            var rawTitle = string.IsNullOrWhiteSpace(anime.Title) ? cleanKey : anime.Title.Trim();
            ShikimoriSeriesLayoutInfo? shikimoriLayout = null;

            if (!YummySeriesLayoutResolver.HasExplicitSeasonNumber(rawTitle))
            {
                using (perf.Measure("stage.shikimori.layout"))
                {
                    shikimoriLayout = await TryResolveSeriesLayoutFromShikimoriAsync(anime, shikimori, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            var seasonNumber = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, rawTitle, shikimoriLayout);
            var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, rawTitle, seasonNumber, shikimoriLayout);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = rawTitle;
            }

            var titleInfo = new YummyAnimeTitleInfo(anime, cleanKey, rawTitle, title, seasonNumber);
            LogResolvedSeriesLayout(logger, titleInfo);
            return titleInfo;
        }

        private static void LogResolvedSeriesLayout(ILogger logger, YummyAnimeTitleInfo titleInfo)
        {
            if (titleInfo.SeasonNumber == 1 &&
                string.Equals(titleInfo.RawTitle, titleInfo.Title, StringComparison.Ordinal))
            {
                return;
            }

            logger.LogInformation(
                "[YummyKodik] Series layout resolved. rawTitle='{RawTitle}' title='{Title}' season={Season} apiSeason={ApiSeason}",
                titleInfo.RawTitle,
                titleInfo.Title,
                titleInfo.SeasonNumber,
                titleInfo.Anime.Season);
        }

        private async Task<YummyVideoCatalog> LoadYummyVideoCatalogAsync(
            ILogger logger,
            PluginConfiguration cfg,
            YummyAnimeTitleInfo titleInfo,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            var allohaApiHttp = _httpClientFactory.CreateClient(HttpClientNames.AllohaApi);
            IReadOnlyList<YummyVideoEntry> allohaApiEntries;
            using (perf.Measure("stage.alloha.catalog"))
            {
                allohaApiEntries = await AllohaApiCatalogLoader
                    .LoadEntriesAsync(cfg, titleInfo.Anime, allohaApiHttp, logger, cancellationToken)
                    .ConfigureAwait(false);
            }

            allohaApiEntries = AllohaApiCatalogLoader.FilterEntriesForSeason(allohaApiEntries, titleInfo.SeasonNumber);
            return YummyVideoCatalog.Create(titleInfo.Anime, allohaApiEntries);
        }

        private static SeriesFileInfo BuildSeriesFileInfo(
            ILogger logger,
            string root,
            YummyAnimeTitleInfo titleInfo,
            PluginConfiguration cfg)
        {
            var folderName = BuildSeriesFolderName(titleInfo.Title, titleInfo.Anime);
            var safeFolderName = SafeFilename(folderName);
            var seriesRoot = ResolveSeriesRoot(
                logger,
                Path.Combine(root, safeFolderName),
                GetLegacySeriesRoots(root, titleInfo.RawTitle, titleInfo.Title, titleInfo.Anime));

            var seasonDirName = $"Season {titleInfo.SeasonNumber:00}";
            var seasonDir = Path.Combine(seriesRoot, seasonDirName);
            var baseUrl = (cfg.ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            return new SeriesFileInfo(seriesRoot, seasonDir, baseUrl);
        }

        private static EpisodeAvailabilityInfo BuildEpisodeAvailabilityInfo(
            YummyAnimeResponse anime,
            YummyVideoCatalog videoCatalog)
        {
            if (anime.AnimeId <= 0)
            {
                var emptyEpisodes = Array.Empty<int>();
                return new EpisodeAvailabilityInfo(emptyEpisodes, 0, emptyEpisodes, emptyEpisodes, emptyEpisodes);
            }

            var knownSupportedEpisodes = videoCatalog.GetSupportedEpisodeNumbersAcrossProviders(PreferredYummyProviderOrder);
            var expectedAvailableEpisodes = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime, knownSupportedEpisodes);
            var allohaSupportedEpisodes = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
                anime,
                videoCatalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Alloha));
            var cvhSupportedEpisodes = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
                anime,
                videoCatalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Cvh));
            var yummySupportedEpisodes = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
                anime,
                videoCatalog.GetSupportedEpisodeNumbersAcrossProviders(PreferredYummyProviderOrder));

            return new EpisodeAvailabilityInfo(
                knownSupportedEpisodes,
                expectedAvailableEpisodes,
                allohaSupportedEpisodes,
                cvhSupportedEpisodes,
                yummySupportedEpisodes);
        }

        private static async Task EnsureSeriesMetadataAsync(
            ILogger logger,
            YummyRefreshInfo refresh,
            HttpClient posterHttp,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            // Create/update the card from Yummy metadata first so Kodik outages do not hide the title.
            using (perf.Measure("stage.series.nfo"))
            {
                await EnsureTvShowNfoAsync(
                        logger,
                        refresh.TitleInfo.Title,
                        refresh.TitleInfo.Anime,
                        refresh.Files.SeriesRoot,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                using (perf.Measure("stage.poster"))
                {
                    await EnsurePosterAsync(refresh.TitleInfo.Anime, refresh.Files.SeriesRoot, posterHttp, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                logger.LogWarning(
                    ex,
                    "[YummyKodik] Failed to update poster for '{Title}'. Series card metadata will still be kept.",
                    refresh.TitleInfo.Title);
            }
        }

        private static string PrepareSeasonDirectoryForEpisodeGeneration(
            ILogger logger,
            YummyRefreshInfo refresh,
            string seasonDir,
            bool createStrmPerVoiceTranslation,
            EpisodeGenerationState state,
            RefreshPerformanceMetrics perf)
        {
            using (perf.Measure("stage.prepare.season.dir"))
            {
                seasonDir = PrepareSeasonDirectory(
                    logger,
                    refresh.Files.SeriesRoot,
                    seasonDir,
                    refresh.TitleInfo.SeasonNumber);
            }

            if (createStrmPerVoiceTranslation)
            {
                using (perf.Measure("stage.scan.translation.files"))
                {
                    state.ExistingEpisodeTranslationFileBaseNames = BuildExistingEpisodeTranslationFileBaseNames(
                        seasonDir,
                        refresh.TitleInfo.SeasonNumber);
                }
            }

            return seasonDir;
        }

        private static void CompleteWithoutKodikSupplement(
            ILogger logger,
            YummyRefreshInfo refresh,
            EpisodeGenerationState state,
            string seasonDir,
            bool seasonDirPrepared,
            RefreshPerformanceMetrics perf)
        {
            if (seasonDirPrepared && refresh.Availability.ExpectedAvailableEpisodes > 0)
            {
                CleanupExpectedEpisodeArtifacts(
                    logger,
                    refresh,
                    state,
                    seasonDir,
                    refresh.Availability.ExpectedAvailableEpisodes,
                    perf);
            }

            if (refresh.Availability.ExpectedAvailableEpisodes <= 0)
            {
                logger.LogInformation(
                    "[YummyKodik] No episodes are available yet for '{Title}'. Series card, poster, and season folders were created/updated; Kodik lookup skipped.",
                    refresh.TitleInfo.Title);
                return;
            }

            logger.LogInformation(
                "[YummyKodik] Done refreshing '{Title}' using Yummy-backed coverage only. episodes={EpisodeCount}",
                refresh.TitleInfo.Title,
                state.GeneratedEpisodeNumbers.Count);
        }

        private static async Task<KodikLookupResult?> TryResolveKodikInfoAsync(
            ILogger logger,
            YummyRefreshInfo refresh,
            string cleanKey,
            RefreshClients clients,
            RefreshPerformanceMetrics perf,
            CancellationToken cancellationToken)
        {
            try
            {
                var kodikClients = await clients.KodikClients.Value.ConfigureAwait(false);
                KodikIdType idType;
                string id;

                if (TryPickKodikIdFromRemoteIds(refresh.TitleInfo.Anime.RemoteIds, out idType, out id))
                {
                    logger.LogInformation(
                        "[YummyKodik] Using remote id from Yummy. title='{Title}' idType={IdType} id={Id}",
                        refresh.TitleInfo.Title,
                        idType,
                        id);
                }
                else
                {
                    logger.LogWarning(
                        "[YummyKodik] remote_ids are missing for '{Title}' (key='{Key}'). Falling back to Kodik title search.",
                        refresh.TitleInfo.RawTitle,
                        cleanKey);

                    using (perf.Measure("stage.kodik.resolve.title"))
                    {
                        var resolved = await KodikTitleResolver.ResolveIdAsync(
                                cleanKey,
                                refresh.TitleInfo.RawTitle,
                                kodikClients.KodikHttp,
                                kodikClients.KodikToken,
                                cancellationToken)
                            .ConfigureAwait(false);

                        idType = resolved.IdType;
                        id = resolved.Id;
                    }
                }

                KodikAnimeInfo info;
                using (perf.Measure("stage.kodik.info"))
                {
                    info = await kodikClients.Kodik.GetAnimeInfoAsync(id, idType, cancellationToken).ConfigureAwait(false);
                }

                return new KodikLookupResult(info, idType, id);
            }
            catch (Exception ex) when (ex is KodikException or HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning(
                    ex,
                    "[YummyKodik] Kodik metadata is unavailable for '{Title}'. Series card was created/updated, STRM generation is skipped for now.",
                    refresh.TitleInfo.Title);
                return null;
            }
        }

        private static bool CompleteIfKodikHasNoEpisodes(
            ILogger logger,
            YummyRefreshInfo refresh,
            EpisodeGenerationState state,
            int kodikAvailableEpisodes)
        {
            if (kodikAvailableEpisodes > 0)
            {
                return false;
            }

            if (state.GeneratedEpisodeNumbers.Count > 0)
            {
                logger.LogInformation(
                    "[YummyKodik] Kodik has no additional episodes for '{Title}'. Kept {EpisodeCount} Yummy-backed episode files.",
                    refresh.TitleInfo.Title,
                    state.GeneratedEpisodeNumbers.Count);
                return true;
            }

            logger.LogInformation(
                "[YummyKodik] No episodes are available yet for '{Title}'. Series card was created/updated, STRM generation skipped.",
                refresh.TitleInfo.Title);
            return true;
        }

        private static void LogKodikZeroSeriesCountIfNeeded(
            ILogger logger,
            YummyRefreshInfo refresh,
            KodikAnimeInfo info,
            int kodikAvailableEpisodes)
        {
            if (info.SeriesCount > 0)
            {
                return;
            }

            logger.LogInformation(
                "[YummyKodik] Kodik search returned zero seriesCount for '{Title}', using Yummy hinted coverage of {EpisodeCount} episode(s).",
                refresh.TitleInfo.Title,
                kodikAvailableEpisodes);
        }

        private static int[] ResolveKodikEpisodesToProcess(
            bool createStrmPerVoiceTranslation,
            int kodikAvailableEpisodes,
            int[] missingEpisodes)
        {
            return createStrmPerVoiceTranslation
                ? Enumerable.Range(1, kodikAvailableEpisodes).ToArray()
                : missingEpisodes;
        }

        private static void CleanupExpectedEpisodeArtifacts(
            ILogger logger,
            YummyRefreshInfo refresh,
            EpisodeGenerationState state,
            string seasonDir,
            int maxAvailableEpisodeNumber,
            RefreshPerformanceMetrics perf)
        {
            using (perf.Measure("stage.cleanup.artifacts"))
            {
                CleanupUnexpectedEpisodeArtifacts(
                    logger,
                    seasonDir,
                    refresh.TitleInfo.SeasonNumber,
                    state.ExpectedEpisodeFileBaseNames,
                    maxAvailableEpisodeNumber,
                    perf);
            }
        }

        private static async Task GeneratePreferredProviderEpisodeFilesAsync(
            YummyEpisodeGenerationContext context,
            int[] supportedEpisodes,
            CancellationToken cancellationToken)
        {
            foreach (var ep in supportedEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await GeneratePreferredProviderEpisodeFileAsync(context, ep, cancellationToken).ConfigureAwait(false);
            }

            LogIncompleteYummyCoverage(context, supportedEpisodes.Length);
        }

        private static async Task GeneratePreferredProviderEpisodeFileAsync(
            YummyEpisodeGenerationContext context,
            int episodeNumber,
            CancellationToken cancellationToken)
        {
            var baseName = BuildEpisodeBaseName(context.Refresh.TitleInfo.SeasonNumber, episodeNumber);
            var writeContext = CreateEpisodeArtifactWriteContext(context);

            if (!context.CreateStrmPerVoiceTranslation)
            {
                await TryWritePreferredProviderEpisodeFileAsync(context, writeContext, episodeNumber, baseName, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var voiceNames = context.Refresh.VideoCatalog.GetSupportedVoiceNamesAcrossProviders(
                episodeNumber,
                PreferredYummyProviderOrder);
            if (voiceNames.Count == 0)
            {
                await TryWriteAutomaticYummyEpisodeFileAsync(context, writeContext, episodeNumber, baseName, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            await WriteYummyVoiceEpisodeFilesAsync(context, writeContext, episodeNumber, baseName, voiceNames, cancellationToken)
                .ConfigureAwait(false);
        }

        private static EpisodeArtifactWriteContext CreateEpisodeArtifactWriteContext(YummyEpisodeGenerationContext context)
        {
            return new EpisodeArtifactWriteContext(
                context.Logger,
                context.SeasonDir,
                context.Refresh.TitleInfo.SeasonNumber,
                context.Refresh.TitleInfo.Title,
                context.Refresh.TitleInfo.Anime.Description,
                context.Perf);
        }

        private static async Task<bool> TryWritePreferredProviderEpisodeFileAsync(
            YummyEpisodeGenerationContext context,
            EpisodeArtifactWriteContext writeContext,
            int episodeNumber,
            string baseName,
            CancellationToken cancellationToken)
        {
            var provider = context.Refresh.VideoCatalog.PickPreferredProvider(
                episodeNumber,
                preferredFilter: context.PreferredTranslationFilter,
                providers: PreferredYummyProviderOrder);
            if (!provider.HasValue)
            {
                return false;
            }

            var url = BuildProviderStreamUrl(
                context.Refresh.Files.BaseUrl,
                provider.Value,
                context.Refresh.TitleInfo.Anime.AnimeId,
                episodeNumber) + HlsFormatQuerySuffix;
            await WriteEpisodeArtifactsAsync(writeContext, baseName, url, episodeNumber, cancellationToken)
                .ConfigureAwait(false);
            TrackExpectedEpisodeArtifact(context.State.ExpectedEpisodeFileBaseNames, episodeNumber, baseName);
            return true;
        }

        private static async Task<bool> TryWriteAutomaticYummyEpisodeFileAsync(
            YummyEpisodeGenerationContext context,
            EpisodeArtifactWriteContext writeContext,
            int episodeNumber,
            string baseName,
            CancellationToken cancellationToken)
        {
            var provider = context.Refresh.VideoCatalog.PickPreferredProvider(
                episodeNumber,
                preferredFilter: context.PreferredTranslationFilter,
                providers: PreferredYummyProviderOrder);
            if (!provider.HasValue)
            {
                return false;
            }

            var url = BuildProviderStreamUrl(
                context.Refresh.Files.BaseUrl,
                provider.Value,
                context.Refresh.TitleInfo.Anime.AnimeId,
                episodeNumber) + HlsFormatQuerySuffix;
            var fileBaseName = baseName + " - Auto";
            await WriteEpisodeArtifactsAsync(writeContext, fileBaseName, url, episodeNumber, cancellationToken)
                .ConfigureAwait(false);
            TrackExpectedEpisodeArtifact(context.State.ExpectedEpisodeFileBaseNames, episodeNumber, fileBaseName);
            TrackExpectedEpisodeTranslation(context.State.ExpectedEpisodeTranslationKeys, episodeNumber, "Auto");
            return true;
        }

        private static async Task WriteYummyVoiceEpisodeFilesAsync(
            YummyEpisodeGenerationContext context,
            EpisodeArtifactWriteContext writeContext,
            int episodeNumber,
            string baseName,
            IEnumerable<string> voiceNames,
            CancellationToken cancellationToken)
        {
            foreach (var voiceName in voiceNames)
            {
                await TryWriteYummyVoiceEpisodeFileAsync(
                        context,
                        writeContext,
                        episodeNumber,
                        baseName,
                        voiceName,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private static async Task<bool> TryWriteYummyVoiceEpisodeFileAsync(
            YummyEpisodeGenerationContext context,
            EpisodeArtifactWriteContext writeContext,
            int episodeNumber,
            string baseName,
            string voiceName,
            CancellationToken cancellationToken)
        {
            var provider = context.Refresh.VideoCatalog.PickPreferredProvider(
                episodeNumber,
                explicitVoiceName: voiceName,
                providers: PreferredYummyProviderOrder);
            if (!provider.HasValue)
            {
                return false;
            }

            var chosenEntry = context.Refresh.VideoCatalog.FindPreferredPlayableEntry(provider.Value, episodeNumber, voiceName);
            if (chosenEntry == null)
            {
                return false;
            }

            var suffix = BuildSafeVoiceSuffix(voiceName);
            var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(
                context.State.ExistingEpisodeTranslationFileBaseNames,
                episodeNumber,
                baseName,
                suffix);
            var url = BuildProviderStreamUrl(
                context.Refresh.Files.BaseUrl,
                provider.Value,
                context.Refresh.TitleInfo.Anime.AnimeId,
                episodeNumber,
                voiceName,
                chosenEntry) + HlsFormatQuerySuffix;

            await WriteEpisodeArtifactsAsync(writeContext, fileBaseName, url, episodeNumber, cancellationToken)
                .ConfigureAwait(false);
            TrackExpectedEpisodeArtifact(context.State.ExpectedEpisodeFileBaseNames, episodeNumber, fileBaseName);
            TrackExpectedEpisodeTranslation(context.State.ExpectedEpisodeTranslationKeys, episodeNumber, suffix);
            return true;
        }

        private static string BuildSafeVoiceSuffix(string voiceName)
        {
            var suffix = SafeFilename(voiceName);
            return string.IsNullOrWhiteSpace(suffix) ? "Voice" : suffix;
        }

        private static void LogIncompleteYummyCoverage(YummyEpisodeGenerationContext context, int coveredEpisodeCount)
        {
            var expectedAvailableEpisodes = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(context.Refresh.TitleInfo.Anime);
            if (expectedAvailableEpisodes > coveredEpisodeCount)
            {
                context.Logger.LogInformation(
                    "[YummyKodik] Yummy-backed providers currently cover {CoveredEpisodes}/{TotalEpisodes} episodes for '{Title}'.",
                    coveredEpisodeCount,
                    expectedAvailableEpisodes,
                    context.Refresh.TitleInfo.Title);
            }
        }

        private static Task<EpisodeArtifactGenerationResult> GenerateKodikEpisodeFilesAsync(
            IEnumerable<int> episodes,
            KodikEpisodeGenerationContext context,
            CancellationToken cancellationToken)
        {
            return GenerateKodikEpisodeFilesAsync(
                context.Logger,
                context.Refresh.TitleInfo.Anime,
                context.Kodik,
                context.Lookup.Info,
                context.Lookup.IdType,
                context.Lookup.Id,
                context.SeasonDir,
                context.Refresh.TitleInfo.SeasonNumber,
                context.Refresh.TitleInfo.Title,
                context.Refresh.Files.BaseUrl,
                context.CreateStrmPerVoiceTranslation,
                context.State.ExistingEpisodeTranslationFileBaseNames,
                episodes,
                context.State.ExpectedEpisodeFileBaseNames,
                context.State.ExpectedEpisodeTranslationKeys,
                context.Perf,
                cancellationToken);
        }

        private static async Task<EpisodeArtifactGenerationResult> GenerateKodikEpisodeFilesAsync(
            ILogger logger,
            YummyAnimeResponse anime,
            KodikClient kodik,
            KodikAnimeInfo info,
            KodikIdType idType,
            string id,
            string seasonDir,
            int seasonNumber,
            string title,
            string baseUrl,
            bool createStrmPerVoiceTranslation,
            IDictionary<int, Dictionary<string, string>> existingEpisodeTranslationFileBaseNames,
            IEnumerable<int> episodes,
            IDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
            IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            var filesWritten = 0;
            var writtenEpisodes = new HashSet<int>();
            var orderedEpisodes = episodes
                .Distinct()
                .OrderBy(x => x)
                .ToArray();
            List<KodikTranslation> fileTranslations = createStrmPerVoiceTranslation
                ? PickTranslationsForFileMode(info.Translations)
                : new List<KodikTranslation>();
            var resolvedTranslationEpisodes = fileTranslations.Count > 0
                ? await ResolveDistinctKodikTranslationEpisodesAsync(
                        logger,
                        kodik,
                        idType,
                        id,
                        fileTranslations,
                        orderedEpisodes,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false)
                : new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

            foreach (var ep in orderedEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var baseName = BuildEpisodeBaseName(seasonNumber, ep);

                var streamBase =
                    $"{baseUrl}/YummyKodik/stream?type={idType.ToString().ToLowerInvariant()}" +
                    $"&id={Uri.EscapeDataString(id)}&ep={ep}";

                if (!createStrmPerVoiceTranslation)
                {
                    var url = streamBase + "&format=hls";
                    await WriteEpisodeArtifactsAsync(logger, seasonDir, baseName, url, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                        .ConfigureAwait(false);
                    TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, baseName);
                    writtenEpisodes.Add(ep);
                    filesWritten++;
                    continue;
                }

                if (fileTranslations.Count == 0)
                {
                    if (HasExpectedEpisodeArtifacts(expectedEpisodeFileBaseNames, ep))
                    {
                        continue;
                    }

                    var url = streamBase + "&format=hls";
                    var fileBaseName = baseName + " - Auto";
                    await WriteEpisodeArtifactsAsync(logger, seasonDir, fileBaseName, url, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                        .ConfigureAwait(false);
                    TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                    TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, "Auto");
                    writtenEpisodes.Add(ep);
                    filesWritten++;
                    continue;
                }

                foreach (var tr in fileTranslations)
                {
                    var trId = (tr.Id ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(trId))
                    {
                        continue;
                    }

                    var suffixRaw = BuildTranslationFileSuffix(tr);
                    var suffix = SafeFilename(suffixRaw);
                    if (string.IsNullOrWhiteSpace(suffix))
                    {
                        suffix = "Translation_" + trId;
                    }

                    if (HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, suffix))
                    {
                        continue;
                    }

                    var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(
                        existingEpisodeTranslationFileBaseNames,
                        ep,
                        baseName,
                        suffix);

                    var strmPath = Path.Combine(seasonDir, fileBaseName + StrmExtension);
                    var nfoPath = Path.Combine(seasonDir, fileBaseName + NfoExtension);

                    if (!tr.CoversEpisode(ep))
                    {
                        TryDeleteFile(logger, strmPath, perf);
                        TryDeleteFile(logger, nfoPath, perf);
                        continue;
                    }

                    if (resolvedTranslationEpisodes.TryGetValue(trId, out var playableEpisodes) &&
                        !playableEpisodes.Contains(ep))
                    {
                        TryDeleteFile(logger, strmPath, perf);
                        TryDeleteFile(logger, nfoPath, perf);
                        continue;
                    }

                    var url = streamBase + $"&tr={Uri.EscapeDataString(trId)}&format=hls";

                    await WriteTextAtomicallyAsync(strmPath, url + Environment.NewLine, perf, "strm", cancellationToken).ConfigureAwait(false);
                    await EnsureEpisodeNfoAsync(logger, nfoPath, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                        .ConfigureAwait(false);
                    TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                    TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, suffix);
                    writtenEpisodes.Add(ep);
                    filesWritten++;
                }
            }

            return new EpisodeArtifactGenerationResult(writtenEpisodes.Count, filesWritten);
        }

        private static async Task<Dictionary<string, HashSet<int>>> ResolveDistinctKodikTranslationEpisodesAsync(
            ILogger logger,
            KodikClient kodik,
            KodikIdType idType,
            string id,
            List<KodikTranslation> translations,
            int[] episodes,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            if (translations.Count == 0 || episodes.Length == 0)
            {
                return result;
            }

            foreach (var tr in translations)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var trId = (tr.Id ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trId))
                {
                    continue;
                }

                var candidateEpisodes = episodes
                    .Where(ep => tr.CoversEpisode(ep))
                    .Distinct()
                    .OrderBy(ep => ep)
                    .ToArray();

                if (candidateEpisodes.Length == 0)
                {
                    continue;
                }

                if (candidateEpisodes.Length == 1)
                {
                    result[trId] = new HashSet<int>(candidateEpisodes);
                    continue;
                }

                var resolvedBasePaths = new Dictionary<int, string>();
                foreach (var episode in candidateEpisodes)
                {
                    try
                    {
                        perf?.AddCount("kodik.translation_link_checks");
                        KodikLinkInfo link;
                        using (perf?.Measure("stage.kodik.translation.links") ?? default)
                        {
                            link = await kodik.GetEpisodeLinkAsync(id, idType, episode, trId, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        var basePath = (link.BasePath ?? string.Empty).Trim();
                        if (basePath.Length > 0)
                        {
                            resolvedBasePaths[episode] = basePath;
                        }
                    }
                    catch (Exception ex) when (ex is KodikException or HttpRequestException or TaskCanceledException or JsonException)
                    {
                        perf?.AddCount("kodik.translation_link_failures");
                        logger.LogDebug(
                            ex,
                            "[YummyKodik] Failed to validate Kodik translation episode link. translationId={TranslationId} episode={Episode}",
                            trId,
                            episode);
                    }
                }

                var distinctEpisodes = KodikEpisodeLinkDeduper.KeepLatestEpisodePerResolvedLink(candidateEpisodes, resolvedBasePaths);
                if (distinctEpisodes.Count < candidateEpisodes.Length)
                {
                    var removedEpisodes = candidateEpisodes
                        .Where(ep => !distinctEpisodes.Contains(ep))
                        .OrderBy(ep => ep);
                    logger.LogInformation(
                        "[YummyKodik] Kodik translation dedupe. translation={Translation} translationId={TranslationId} removedEpisodes={RemovedEpisodes} keptEpisodes={KeptEpisodes}",
                        tr.Name ?? trId,
                        trId,
                        string.Join(", ", removedEpisodes),
                        string.Join(", ", distinctEpisodes.OrderBy(ep => ep)));
                }

                result[trId] = distinctEpisodes;
            }

            return result;
        }

        private static string BuildProviderStreamUrl(
            string baseUrl,
            YummyVideoProviderKind provider,
            long animeId,
            int episode,
            string? voiceName = null,
            YummyVideoEntry? entry = null)
        {
            return provider switch
            {
                YummyVideoProviderKind.Alloha => YummyKodikStreamUri.BuildAllohaHttpUrl(baseUrl, animeId, episode, voiceName, entry?.Alloha),
                _ => YummyKodikStreamUri.BuildCvhHttpUrl(baseUrl, animeId, episode, voiceName)
            };
        }

        private static Dictionary<int, Dictionary<string, string>> BuildExistingEpisodeTranslationFileBaseNames(
            string seasonDir,
            int seasonNumber)
        {
            var result = new Dictionary<int, Dictionary<string, string>>();
            if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
            {
                return result;
            }

            var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            var filePattern = new Regex(
                @"^S(?<season>\d{2})E(?<episode>\d{2})(?: - (?<suffix>.+))?$",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1));

            foreach (var path in Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly))
            {
                var fileBaseName = Path.GetFileNameWithoutExtension(path);
                if (string.IsNullOrWhiteSpace(fileBaseName))
                {
                    continue;
                }

                var match = filePattern.Match(fileBaseName);
                if (!match.Success ||
                    !int.TryParse(match.Groups["season"].Value, out var parsedSeason) ||
                    parsedSeason != effectiveSeasonNumber ||
                    !int.TryParse(match.Groups["episode"].Value, out var episodeNumber))
                {
                    continue;
                }

                var suffix = match.Groups["suffix"].Success
                    ? (match.Groups["suffix"].Value ?? string.Empty).Trim()
                    : string.Empty;
                var normalizedKey = NormalizeEpisodeTranslationKey(suffix);
                if (normalizedKey.Length == 0)
                {
                    continue;
                }

                if (!result.TryGetValue(episodeNumber, out var aliases))
                {
                    aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    result[episodeNumber] = aliases;
                }

                aliases.TryAdd(normalizedKey, fileBaseName);
            }

            return result;
        }

        private static async Task WriteEpisodeArtifactsAsync(
            EpisodeArtifactWriteContext context,
            string fileBaseName,
            string url,
            int episodeNumber,
            CancellationToken cancellationToken)
        {
            await WriteEpisodeArtifactsAsync(
                    context.Logger,
                    context.SeasonDir,
                    fileBaseName,
                    url,
                    episodeNumber,
                    context.SeasonNumber,
                    context.Title,
                    context.Description,
                    context.Perf,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static async Task WriteEpisodeArtifactsAsync(
            ILogger logger,
            string seasonDir,
            string fileBaseName,
            string url,
            int episodeNumber,
            int seasonNumber,
            string title,
            string? description,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            var strmPath = Path.Combine(seasonDir, fileBaseName + StrmExtension);
            var nfoPath = Path.Combine(seasonDir, fileBaseName + NfoExtension);

            await WriteTextAtomicallyAsync(strmPath, url + Environment.NewLine, perf, "strm", cancellationToken).ConfigureAwait(false);
            await EnsureEpisodeNfoAsync(logger, nfoPath, episodeNumber, seasonNumber, title, description, perf, cancellationToken)
                .ConfigureAwait(false);
        }

        private static void TrackExpectedEpisodeArtifact(
            IDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
            int episodeNumber,
            string fileBaseName)
        {
            EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, episodeNumber, fileBaseName);
        }

        private static void TrackExpectedEpisodeTranslation(
            IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
            int episodeNumber,
            string translationSuffix)
        {
            EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, episodeNumber, translationSuffix);
        }

        private static bool HasExpectedEpisodeArtifacts(
            IDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
            int episodeNumber)
        {
            return EpisodeArtifactMaintenance.HasExpectedEpisodeArtifacts(expectedEpisodeFileBaseNames, episodeNumber);
        }

        private static bool HasExpectedEpisodeTranslation(
            IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
            int episodeNumber,
            string translationSuffix)
        {
            return EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, episodeNumber, translationSuffix);
        }

        private static string NormalizeEpisodeTranslationKey(string? translationSuffix)
        {
            return EpisodeArtifactMaintenance.NormalizeEpisodeTranslationKey(translationSuffix);
        }

        private static void CleanupUnexpectedEpisodeArtifacts(
            ILogger logger,
            string seasonDir,
            int seasonNumber,
            IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
            int maxAvailableEpisodeNumber,
            RefreshPerformanceMetrics? perf)
        {
            EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
                logger,
                seasonDir,
                seasonNumber,
                expectedEpisodeFileBaseNames,
                maxAvailableEpisodeNumber,
                path => TryDeleteFile(logger, path, perf));
        }

        private static void TryDeleteFile(ILogger logger, string path, RefreshPerformanceMetrics? perf = null)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                    perf?.AddCount("io.file_deleted");
                    logger.LogDebug("[YummyKodik] Deleted stale placeholder file '{Path}'.", path);
                }
            }
            catch (Exception ex)
            {
                perf?.AddCount("io.delete_failures");
                logger.LogDebug(ex, "[YummyKodik] Failed to delete file '{Path}'.", path);
            }
        }

        private readonly record struct EpisodeArtifactGenerationResult(int EpisodesWritten, int FilesWritten);

        private readonly struct SeriesRootLockReleaser : IDisposable
        {
            private readonly SemaphoreSlim _gate;

            public SeriesRootLockReleaser(SemaphoreSlim gate)
            {
                _gate = gate;
            }

            public void Dispose()
            {
                _gate.Release();
            }
        }

        private sealed record RefreshClients(
            YummyClient Yummy,
            ShikimoriGraphQlClient Shikimori,
            HttpClient YummyHttp,
            Lazy<Task<RefreshKodikClients>> KodikClients);

        private sealed record RefreshKodikClients(
            KodikClient Kodik,
            HttpClient KodikHttp,
            string KodikToken);

        private sealed record YummyAnimeTitleInfo(
            YummyAnimeResponse Anime,
            string CleanKey,
            string RawTitle,
            string Title,
            int SeasonNumber);

        private sealed record SeriesFileInfo(
            string SeriesRoot,
            string SeasonDir,
            string BaseUrl);

        private sealed record EpisodeAvailabilityInfo(
            IReadOnlyList<int> KnownSupportedEpisodes,
            int ExpectedAvailableEpisodes,
            int[] AllohaSupportedEpisodes,
            int[] CvhSupportedEpisodes,
            int[] YummySupportedEpisodes);

        private sealed record YummyRefreshInfo(
            YummyAnimeTitleInfo TitleInfo,
            YummyVideoCatalog VideoCatalog,
            SeriesFileInfo Files,
            EpisodeAvailabilityInfo Availability);

        private sealed class EpisodeGenerationState
        {
            public HashSet<int> GeneratedEpisodeNumbers { get; } = new();

            public Dictionary<int, HashSet<string>> ExpectedEpisodeFileBaseNames { get; } = new();

            public Dictionary<int, HashSet<string>> ExpectedEpisodeTranslationKeys { get; } = new();

            public Dictionary<int, Dictionary<string, string>> ExistingEpisodeTranslationFileBaseNames { get; set; } = new();
        }

        private sealed record KodikLookupResult(
            KodikAnimeInfo Info,
            KodikIdType IdType,
            string Id);

        private sealed record YummyEpisodeGenerationContext(
            ILogger Logger,
            YummyRefreshInfo Refresh,
            EpisodeGenerationState State,
            string SeasonDir,
            bool CreateStrmPerVoiceTranslation,
            string? PreferredTranslationFilter,
            RefreshPerformanceMetrics? Perf);

        private sealed record KodikEpisodeGenerationContext(
            ILogger Logger,
            YummyRefreshInfo Refresh,
            KodikLookupResult Lookup,
            KodikClient Kodik,
            EpisodeGenerationState State,
            string SeasonDir,
            bool CreateStrmPerVoiceTranslation,
            RefreshPerformanceMetrics? Perf);

        private sealed record EpisodeArtifactWriteContext(
            ILogger Logger,
            string SeasonDir,
            int SeasonNumber,
            string Title,
            string? Description,
            RefreshPerformanceMetrics? Perf);

        private static IEnumerable<string> GetLegacySeriesRoots(string root, string rawTitle, string resolvedTitle, YummyAnimeResponse anime)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(rawTitle))
            {
                yield return Path.Combine(root, SafeFilename(rawTitle));
                yield return Path.Combine(root, SafeFilename(BuildLegacySeriesFolderName(rawTitle, anime)));
            }

            if (!string.IsNullOrWhiteSpace(resolvedTitle))
            {
                yield return Path.Combine(root, SafeFilename(resolvedTitle));
                yield return Path.Combine(root, SafeFilename(BuildLegacySeriesFolderName(resolvedTitle, anime)));
            }

            if (!string.IsNullOrWhiteSpace(rawTitle))
            {
                yield return Path.Combine(root, SafeFilename(BuildSeriesFolderName(rawTitle, anime)));
            }
        }

        private static string ResolveSeriesRoot(ILogger logger, string seriesRoot, IEnumerable<string> legacyRoots)
        {
            if (string.IsNullOrWhiteSpace(seriesRoot))
            {
                return seriesRoot;
            }

            if (Directory.Exists(seriesRoot))
            {
                return seriesRoot;
            }

            foreach (var legacyRoot in legacyRoots
                         .Where(x => !string.IsNullOrWhiteSpace(x))
                         .Where(x => !string.Equals(x, seriesRoot, StringComparison.OrdinalIgnoreCase))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(legacyRoot))
                {
                    continue;
                }

                logger.LogInformation(
                    "[YummyKodik] Using existing legacy series folder '{Legacy}' instead of renaming to '{Canonical}' to keep Jellyfin item ids stable.",
                    legacyRoot,
                    seriesRoot);

                return legacyRoot;
            }

            return seriesRoot;
        }

        private static string PrepareSeasonDirectory(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            return SeasonDirectoryMaintenance.PrepareSeasonDirectory(logger, seriesRoot, seasonDir, seasonNumber);
        }

        private static string BuildEpisodeBaseName(int seasonNumber, int episodeNumber)
        {
            var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            return $"S{effectiveSeasonNumber:00}E{episodeNumber:00}";
        }

        private static List<KodikTranslation> PickTranslationsForFileMode(IReadOnlyList<KodikTranslation> translations)
        {
            translations ??= Array.Empty<KodikTranslation>();

            var voice = translations
                .Where(t => string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase))
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .Where(t => !string.Equals(t.Id.Trim(), "0", StringComparison.Ordinal))
                .ToList();

            if (voice.Count > 0)
            {
                return voice;
            }

            return translations
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .Where(t => !string.Equals(t.Id.Trim(), "0", StringComparison.Ordinal))
                .ToList();
        }

        private static string BuildTranslationFileSuffix(KodikTranslation t)
        {
            var name = (t.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                var tid = (t.Id ?? string.Empty).Trim();
                name = string.IsNullOrWhiteSpace(tid) ? "Translation" : ("tr" + tid);
            }

            var type = (t.Type ?? string.Empty).Trim();
            if (type.Length == 0 || type.Equals("voice", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            if (type.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
            {
                return name + " [subs]";
            }

            return name + " [" + type + "]";
        }

        private static string BuildSeriesFolderName(string title, YummyAnimeResponse? anime)
        {
            var baseTitle = (title ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(baseTitle))
            {
                baseTitle = NormalizeKey(anime?.AnimeUrl);
            }

            var tag = YummyProviderTagFormatter.BuildBestIdTag(anime);
            return string.IsNullOrEmpty(tag) ? baseTitle : $"{baseTitle} {tag}";
        }

        private static string BuildLegacySeriesFolderName(string title, YummyAnimeResponse? anime)
        {
            var baseTitle = (title ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(baseTitle))
            {
                baseTitle = NormalizeKey(anime?.AnimeUrl);
            }

            var tag = YummyProviderTagFormatter.BuildLegacyBestIdTag(anime);
            return string.IsNullOrEmpty(tag) ? baseTitle : $"{baseTitle} {tag}";
        }

        private static bool TryPickKodikIdFromRemoteIds(
            YummyRemoteIds? remoteIds,
            out KodikIdType idType,
            out string id)
        {
            idType = KodikIdType.Shikimori;
            id = string.Empty;

            if (remoteIds == null)
            {
                return false;
            }

            if (remoteIds.ShikimoriId.HasValue && remoteIds.ShikimoriId.Value > 0)
            {
                idType = KodikIdType.Shikimori;
                id = remoteIds.ShikimoriId.Value.ToString();
                return true;
            }

            if (remoteIds.KpId.HasValue && remoteIds.KpId.Value > 0)
            {
                idType = KodikIdType.Kinopoisk;
                id = remoteIds.KpId.Value.ToString();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(remoteIds.ImdbId))
            {
                idType = KodikIdType.Imdb;
                id = remoteIds.ImdbId.Trim();
                return true;
            }

            return false;
        }

        private static string SafeFilename(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Trim();
        }

        private static async Task EnsurePosterAsync(
            YummyAnimeResponse anime,
            string seriesRoot,
            HttpClient http,
            CancellationToken cancellationToken)
        {
            var posterPath = Path.Combine(seriesRoot, "poster.jpg");
            if (File.Exists(posterPath))
            {
                return;
            }

            var url = YummyClient.PickBestPosterUrl(anime);
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            Directory.CreateDirectory(seriesRoot);
            var tempPath = Path.Combine(
                seriesRoot,
                Path.GetFileName(posterPath) + ".tmp." + Guid.NewGuid().ToString("N"));

            try
            {
                await using (var fs = new FileStream(
                                 tempPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 81920,
                                 useAsync: true))
                {
                    await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }

                File.Move(tempPath, posterPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // Best-effort cleanup for interrupted poster writes.
                    }
                }
            }
        }

        private static async Task EnsureTvShowNfoAsync(
            ILogger logger,
            string seriesTitle,
            YummyAnimeResponse anime,
            string seriesRoot,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            var nfoPath = Path.Combine(seriesRoot, "tvshow.nfo");
            var xml = NfoBuilder.BuildSeriesNfo(seriesTitle, anime.Description ?? string.Empty);

            if (File.Exists(nfoPath))
            {
                if (await IsValidXmlFileAsync(nfoPath, cancellationToken).ConfigureAwait(false))
                {
                    perf?.AddCount("io.nfo_unchanged");
                    return;
                }

                perf?.AddCount("io.nfo_invalid_rebuilt");
                logger.LogWarning(
                    "[YummyKodik] Existing tvshow.nfo is empty or invalid XML, recreating it atomically. path='{Path}'",
                    nfoPath);
            }

            await WriteTextAtomicallyAsync(nfoPath, xml, perf, "nfo", cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureEpisodeNfoAsync(
            ILogger logger,
            string nfoPath,
            int episodeNumber,
            int seasonNumber,
            string seriesTitle,
            string? description,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            var xml = NfoBuilder.BuildEpisodeNfo(
                episodeNumber,
                season: seasonNumber,
                seriesTitle: seriesTitle,
                description: description ?? string.Empty);

            if (File.Exists(nfoPath))
            {
                if (await IsValidXmlFileAsync(nfoPath, cancellationToken).ConfigureAwait(false))
                {
                    perf?.AddCount("io.nfo_unchanged");
                    return;
                }

                perf?.AddCount("io.nfo_invalid_rebuilt");
                logger.LogInformation(
                    "[YummyKodik] Existing episode nfo is empty or invalid XML, recreating it atomically. path='{Path}'",
                    nfoPath);
            }

            await WriteTextAtomicallyAsync(nfoPath, xml, perf, "nfo", cancellationToken).ConfigureAwait(false);
        }

        private static async Task<bool> IsValidXmlFileAsync(string path, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return IsValidXmlContent(content);
        }

        private static async Task<TextWriteOutcome> WriteTextAtomicallyAsync(
            string path,
            string content,
            RefreshPerformanceMetrics? perf,
            string artifactKind,
            CancellationToken cancellationToken)
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException($"Failed to determine directory for path '{path}'.");
            }

            Directory.CreateDirectory(directory);

            var hasExistingFile = File.Exists(path);
            if (hasExistingFile)
            {
                var existingContent = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existingContent, content, StringComparison.Ordinal))
                {
                    perf?.AddCount($"io.{artifactKind}_unchanged");
                    return TextWriteOutcome.Unchanged;
                }
            }

            var tempPath = Path.Combine(
                directory,
                Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));

            try
            {
                await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);

                if (File.Exists(path))
                {
                    File.Move(tempPath, path, overwrite: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
            }
            finally
            {
                if (File.Exists(tempPath))
                {
                    try
                    {
                        File.Delete(tempPath);
                    }
                    catch
                    {
                        // ignore temp cleanup failures
                    }
                }
            }

            var outcome = hasExistingFile ? TextWriteOutcome.Updated : TextWriteOutcome.Created;
            perf?.AddCount($"io.{artifactKind}_{GetMetricSuffix(outcome)}");
            return outcome;
        }

        private static bool IsValidXmlContent(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return false;
            }

            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };

                using var reader = XmlReader.Create(new StringReader(content), settings);
                return reader.MoveToContent() == XmlNodeType.Element;
            }
            catch (XmlException)
            {
                return false;
            }
        }

        private enum TextWriteOutcome
        {
            Created,
            Updated,
            Unchanged
        }

        private static string GetMetricSuffix(TextWriteOutcome outcome)
        {
            return outcome switch
            {
                TextWriteOutcome.Created => "created",
                TextWriteOutcome.Updated => "updated",
                _ => "unchanged"
            };
        }

        private sealed class RefreshPerformanceMetrics
        {
            private readonly Stopwatch _total = Stopwatch.StartNew();
            private readonly Dictionary<string, long> _durationsMs = new(StringComparer.Ordinal);
            private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);

            public RefreshPerformanceMetrics(bool enabled)
            {
                Enabled = enabled;
            }

            public bool Enabled { get; }

            public MeasureScope Measure(string key)
            {
                return Enabled ? new MeasureScope(this, key) : default;
            }

            public void AddDuration(string key, TimeSpan elapsed)
            {
                if (!Enabled || string.IsNullOrWhiteSpace(key))
                {
                    return;
                }

                var elapsedMs = Math.Max(0L, (long)Math.Round(elapsed.TotalMilliseconds));
                if (_durationsMs.TryGetValue(key, out var current))
                {
                    _durationsMs[key] = current + elapsedMs;
                    return;
                }

                _durationsMs[key] = elapsedMs;
            }

            public void AddCount(string key, int delta = 1)
            {
                if (!Enabled || string.IsNullOrWhiteSpace(key) || delta == 0)
                {
                    return;
                }

                if (_counts.TryGetValue(key, out var current))
                {
                    _counts[key] = current + delta;
                    return;
                }

                _counts[key] = delta;
            }

            public void LogSummary(ILogger logger, string title, string key)
            {
                if (!Enabled)
                {
                    return;
                }

                var stageSummary = _durationsMs.Count == 0
                    ? "-"
                    : string.Join(
                        ", ",
                        _durationsMs
                            .OrderByDescending(x => x.Value)
                            .Select(x => $"{x.Key}={x.Value}ms"));

                var countSummary = _counts.Count == 0
                    ? "-"
                    : string.Join(
                        ", ",
                        _counts
                            .OrderBy(x => x.Key, StringComparer.Ordinal)
                            .Select(x => $"{x.Key}={x.Value}"));

                logger.LogInformation(
                    "[YummyKodik][perf] Refresh '{Title}' (key='{Key}') took {ElapsedMs}ms. stages: {Stages}. counts: {Counts}",
                    title,
                    key,
                    _total.ElapsedMilliseconds,
                    stageSummary,
                    countSummary);
            }

            public readonly struct MeasureScope : IDisposable
            {
                private readonly RefreshPerformanceMetrics? _owner;
                private readonly string? _key;
                private readonly long _startedAt;

                public MeasureScope(RefreshPerformanceMetrics owner, string key)
                {
                    _owner = owner;
                    _key = key;
                    _startedAt = Stopwatch.GetTimestamp();
                }

                public void Dispose()
                {
                    if (_owner == null || string.IsNullOrWhiteSpace(_key))
                    {
                        return;
                    }

                    var elapsed = Stopwatch.GetElapsedTime(_startedAt);
                    _owner.AddDuration(_key, elapsed);
                }
            }
        }

        private static string NormalizeKey(string? s)
        {
            var v = (s ?? string.Empty).Trim();

            // remove wrapping quotes if any
            v = v.Trim().Trim('"', '\'', '“', '”');

            // remove trailing slashes
            v = v.Trim('/');

            return v;
        }
    }
}
