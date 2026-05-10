// File: Tasks/RefreshYummyKodikLibraryTask.cs

using System;
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

        private readonly ILogger<RefreshYummyKodikLibraryTask> _logger;
        private readonly IHttpClientFactory _httpClientFactory;

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

            using var http = new HttpClient();

            var yummyClient = new YummyClient(http, cfg.YummyClientId, cfg.YummyApiBaseUrl);

            var token = await KodikTokenProvider.GetTokenAsync(http, cancellationToken).ConfigureAwait(false);
            var kodikClient = new KodikClient(http, token);
            var shikimoriHttp = _httpClientFactory.CreateClient(HttpClientNames.Shikimori);
            var shikimoriClient = new ShikimoriGraphQlClient(shikimoriHttp);

            var allKeys = await BuildAnimeKeysAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
            if (allKeys.Count == 0)
            {
                _logger.LogInformation("[YummyKodik] No slugs or list items configured, nothing to refresh.");
                return;
            }

            var perItemStep = 100.0 / allKeys.Count;
            var idx = 0;

            foreach (var key in allKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                idx++;

                progress?.Report(perItemStep * (idx - 1));

                try
                {
                    await RefreshSingleAnimeAsync(
                            key,
                            root,
                            yummyClient,
                            kodikClient,
                            shikimoriClient,
                            cfg.PreferredQuality,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[YummyKodik] Failed to refresh key '{Key}': {Message}", key, ex.Message);
                }

                progress?.Report(perItemStep * idx);
            }
        }

        private async Task<List<string>> BuildAnimeKeysAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            CancellationToken cancellationToken)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (cfg.Slugs != null)
            {
                foreach (var s in cfg.Slugs)
                {
                    var v = NormalizeKey(s);
                    if (!string.IsNullOrEmpty(v))
                    {
                        set.Add(v);
                    }
                }
            }

            if (cfg.UseUserListSubscription)
            {
                if (cfg.YummyUserId <= 0)
                {
                    _logger.LogWarning("[YummyKodik] UseUserListSubscription enabled, but YummyUserId is not set.");
                    return set.ToList();
                }

                var listId = cfg.YummyUserListId < 0 ? 0 : cfg.YummyUserListId;

                try
                {
                    await EnsureAuthenticatedAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);

                    IReadOnlyList<YummyUserListItem> items;

                    try
                    {
                        items = await yummyClient
                            .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (!string.IsNullOrWhiteSpace(yummyClient.GetAccessToken()))
                        {
                            _logger.LogWarning("[YummyKodik] User list unauthorized, trying token refresh and retry.");
                            await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

                            items = await yummyClient
                                .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            throw;
                        }
                    }

                    _logger.LogInformation(
                        "[YummyKodik] User list fetched. userId={UserId} listId={ListId} items={Count}",
                        cfg.YummyUserId,
                        listId,
                        items.Count);

                    foreach (var item in items)
                    {
                        var k = NormalizeKey(item.AnimeUrl);
                        if (!string.IsNullOrEmpty(k))
                        {
                            set.Add(k);
                            continue;
                        }

                        if (item.AnimeId > 0)
                        {
                            set.Add(item.AnimeId.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[YummyKodik] Failed to fetch user list, falling back to manual slugs only.");
                }
            }

            return set.ToList();
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
            YummyClient yummy,
            KodikClient kodik,
            ShikimoriGraphQlClient shikimori,
            int preferredQuality,
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
                YummyAnimeResponse anime;
                using (perf.Measure("stage.yummy.fetch"))
                {
                    anime = await yummy.GetAnimeAsync(cleanKey, includeVideos: true, cancellationToken).ConfigureAwait(false);
                }

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

                summaryTitle = title;

                if (seasonNumber != 1 || !string.Equals(rawTitle, title, StringComparison.Ordinal))
                {
                    logger.LogInformation(
                        "[YummyKodik] Series layout resolved. rawTitle='{RawTitle}' title='{Title}' season={Season} apiSeason={ApiSeason}",
                        rawTitle,
                        title,
                        seasonNumber,
                        anime.Season);
                }

                var allohaApiHttp = _httpClientFactory.CreateClient(HttpClientNames.AllohaApi);
                IReadOnlyList<YummyVideoEntry> allohaApiEntries;
                using (perf.Measure("stage.alloha.catalog"))
                {
                    allohaApiEntries = await AllohaApiCatalogLoader
                        .LoadEntriesAsync(cfg, anime, allohaApiHttp, logger, cancellationToken)
                        .ConfigureAwait(false);
                }

                allohaApiEntries = AllohaApiCatalogLoader.FilterEntriesForSeason(allohaApiEntries, seasonNumber);
                var videoCatalog = YummyVideoCatalog.Create(anime, allohaApiEntries);

                var folderName = BuildSeriesFolderName(title, anime);
                var safeFolderName = SafeFilename(folderName);

                var seriesRoot = ResolveSeriesRoot(
                    logger,
                    Path.Combine(root, safeFolderName),
                    GetLegacySeriesRoots(root, rawTitle, title, anime));

                Directory.CreateDirectory(seriesRoot);

                // Create/update the card from Yummy metadata first so Kodik outages do not hide the title.
                using (perf.Measure("stage.series.nfo"))
                {
                    await EnsureTvShowNfoAsync(logger, title, anime, seriesRoot, perf, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    using (perf.Measure("stage.poster"))
                    {
                        await EnsurePosterAsync(anime, seriesRoot, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    logger.LogWarning(
                        ex,
                        "[YummyKodik] Failed to update poster for '{Title}'. Series card metadata will still be kept.",
                        title);
                }

                var seasonDirName = $"Season {seasonNumber:00}";
                var seasonDir = Path.Combine(seriesRoot, seasonDirName);
                var seasonDirPrepared = false;
                if (YummySeriesLayoutResolver.ShouldCreateSeasonDirectory(anime, seasonNumber))
                {
                    using (perf.Measure("stage.prepare.season.dir"))
                    {
                        seasonDir = PrepareSeasonDirectory(logger, seriesRoot, seasonDir, seasonNumber);
                    }

                    seasonDirPrepared = true;
                }

                var baseUrl = (cfg.ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
                var generatedEpisodeNumbers = new HashSet<int>();
                var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>();
                var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();
                Dictionary<int, Dictionary<string, string>> existingEpisodeTranslationFileBaseNames = new();
                var knownSupportedEpisodes = anime.AnimeId > 0
                    ? videoCatalog.GetSupportedEpisodeNumbersAcrossProviders(PreferredYummyProviderOrder)
                    : Array.Empty<int>();
                var expectedAvailableEpisodes = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime, knownSupportedEpisodes);
                var allohaSupportedEpisodes = anime.AnimeId > 0
                    ? YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
                        anime,
                        videoCatalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Alloha))
                    : Array.Empty<int>();
                var cvhSupportedEpisodes = anime.AnimeId > 0
                    ? YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
                        anime,
                        videoCatalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Cvh))
                    : Array.Empty<int>();
                var yummySupportedEpisodes = anime.AnimeId > 0
                    ? YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
                        anime,
                        videoCatalog.GetSupportedEpisodeNumbersAcrossProviders(PreferredYummyProviderOrder))
                    : Array.Empty<int>();

                if (yummySupportedEpisodes.Length > 0)
                {
                    if (string.IsNullOrEmpty(baseUrl))
                    {
                        logger.LogWarning("[YummyKodik] ServerBaseUrl is empty, skipping Yummy-backed STRM generation for '{Title}'.", title);
                        return;
                    }

                    if (!seasonDirPrepared)
                    {
                        using (perf.Measure("stage.prepare.season.dir"))
                        {
                            seasonDir = PrepareSeasonDirectory(logger, seriesRoot, seasonDir, seasonNumber);
                        }

                        seasonDirPrepared = true;
                        if (cfg.CreateStrmPerVoiceTranslation)
                        {
                            using (perf.Measure("stage.scan.translation.files"))
                            {
                                existingEpisodeTranslationFileBaseNames = BuildExistingEpisodeTranslationFileBaseNames(seasonDir, seasonNumber);
                            }
                        }
                    }

                    using (perf.Measure("stage.generate.yummy.files"))
                    {
                        await GeneratePreferredProviderEpisodeFilesAsync(
                                logger,
                                anime,
                                videoCatalog,
                                yummySupportedEpisodes,
                                seasonDir,
                                seasonNumber,
                                title,
                                baseUrl,
                                cfg.PreferredTranslationFilter,
                                cfg.CreateStrmPerVoiceTranslation,
                                existingEpisodeTranslationFileBaseNames,
                                expectedEpisodeFileBaseNames,
                                expectedEpisodeTranslationKeys,
                                perf,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }

                    generatedEpisodeNumbers.UnionWith(yummySupportedEpisodes);

                    logger.LogInformation(
                        "[YummyKodik] Generated mixed Yummy-backed episode files for '{Title}'. episodes={EpisodeCount} allohaEpisodes={AllohaEpisodes} cvhEpisodes={CvhEpisodes} availableEpisodes={AvailableEpisodes}",
                        title,
                        yummySupportedEpisodes.Length,
                        allohaSupportedEpisodes.Length,
                        cvhSupportedEpisodes.Length,
                        expectedAvailableEpisodes);
                }

                var needsKodikEpisodeSupplement = YummyEpisodeAvailability.NeedsKodikSupplement(anime, generatedEpisodeNumbers, knownSupportedEpisodes);
                var needsKodikTranslationSupplement = cfg.CreateStrmPerVoiceTranslation && expectedAvailableEpisodes > 0;

                if (!needsKodikEpisodeSupplement && !needsKodikTranslationSupplement)
                {
                    if (seasonDirPrepared && expectedAvailableEpisodes > 0)
                    {
                        using (perf.Measure("stage.cleanup.artifacts"))
                        {
                            CleanupUnexpectedEpisodeArtifacts(
                                logger,
                                seasonDir,
                                seasonNumber,
                                expectedEpisodeFileBaseNames,
                                expectedAvailableEpisodes,
                                perf);
                        }
                    }

                    if (expectedAvailableEpisodes <= 0)
                    {
                        logger.LogInformation(
                            "[YummyKodik] No episodes are available yet for '{Title}'. Series card, poster, and season folders were created/updated; Kodik lookup skipped.",
                            title);
                    }
                    else
                    {
                        logger.LogInformation(
                            "[YummyKodik] Done refreshing '{Title}' using Yummy-backed coverage only. episodes={EpisodeCount}",
                            title,
                            generatedEpisodeNumbers.Count);
                    }
                    return;
                }

                KodikIdType idType;
                string id;
                KodikAnimeInfo info;

                try
                {
                    if (TryPickKodikIdFromRemoteIds(anime.RemoteIds, out idType, out id))
                    {
                        logger.LogInformation(
                            "[YummyKodik] Using remote id from Yummy. title='{Title}' idType={IdType} id={Id}",
                            title, idType, id);
                    }
                    else
                    {
                        logger.LogWarning(
                            "[YummyKodik] remote_ids are missing for '{Title}' (key='{Key}'). Falling back to Kodik title search.",
                            rawTitle, cleanKey);

                        using (perf.Measure("stage.kodik.resolve.title"))
                        {
                            var resolved = await KodikTitleResolver.ResolveIdAsync(
                                    cleanKey,
                                    rawTitle,
                                    kodik,
                                    cancellationToken)
                                .ConfigureAwait(false);

                            idType = resolved.IdType;
                            id = resolved.Id;
                        }
                    }

                    using (perf.Measure("stage.kodik.info"))
                    {
                        info = await kodik.GetAnimeInfoAsync(id, idType, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is KodikException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    logger.LogWarning(
                        ex,
                        "[YummyKodik] Kodik metadata is unavailable for '{Title}'. Series card was created/updated, STRM generation is skipped for now.",
                        title);
                    return;
                }

                var kodikAvailableEpisodes = YummyEpisodeAvailability.ResolveKodikAvailableEpisodeCount(
                    info.SeriesCount,
                    expectedAvailableEpisodes);
                if (kodikAvailableEpisodes <= 0)
                {
                    if (generatedEpisodeNumbers.Count > 0)
                    {
                        logger.LogInformation(
                            "[YummyKodik] Kodik has no additional episodes for '{Title}'. Kept {EpisodeCount} Yummy-backed episode files.",
                            title,
                            generatedEpisodeNumbers.Count);
                    }
                    else
                    {
                        logger.LogInformation(
                            "[YummyKodik] No episodes are available yet for '{Title}'. Series card was created/updated, STRM generation skipped.",
                            title);
                    }
                    return;
                }

                if (info.SeriesCount <= 0)
                {
                    logger.LogInformation(
                        "[YummyKodik] Kodik search returned zero seriesCount for '{Title}', using Yummy hinted coverage of {EpisodeCount} episode(s).",
                        title,
                        kodikAvailableEpisodes);
                }

                if (string.IsNullOrEmpty(baseUrl))
                {
                    logger.LogWarning("[YummyKodik] ServerBaseUrl is empty, skipping refresh for '{Title}'.", title);
                    return;
                }

                if (!seasonDirPrepared)
                {
                    using (perf.Measure("stage.prepare.season.dir"))
                    {
                        seasonDir = PrepareSeasonDirectory(logger, seriesRoot, seasonDir, seasonNumber);
                    }

                    seasonDirPrepared = true;
                    if (cfg.CreateStrmPerVoiceTranslation)
                    {
                        using (perf.Measure("stage.scan.translation.files"))
                        {
                            existingEpisodeTranslationFileBaseNames = BuildExistingEpisodeTranslationFileBaseNames(seasonDir, seasonNumber);
                        }
                    }
                }

                var missingEpisodes = Enumerable.Range(1, kodikAvailableEpisodes)
                    .Where(ep => !generatedEpisodeNumbers.Contains(ep))
                    .ToArray();

                var kodikEpisodesToProcess = cfg.CreateStrmPerVoiceTranslation
                    ? Enumerable.Range(1, kodikAvailableEpisodes).ToArray()
                    : missingEpisodes;

                if (kodikEpisodesToProcess.Length == 0)
                {
                    using (perf.Measure("stage.cleanup.artifacts"))
                    {
                        CleanupUnexpectedEpisodeArtifacts(
                            logger,
                            seasonDir,
                            seasonNumber,
                            expectedEpisodeFileBaseNames,
                            kodikAvailableEpisodes,
                            perf);
                    }

                    logger.LogInformation(
                        "[YummyKodik] Done refreshing '{Title}'. Yummy-backed providers already cover all {SeriesCount} currently available episodes.",
                        title,
                        info.SeriesCount);
                    return;
                }

                EpisodeArtifactGenerationResult kodikGeneration;
                using (perf.Measure("stage.generate.kodik.files"))
                {
                    kodikGeneration = await GenerateKodikEpisodeFilesAsync(
                            logger,
                            anime,
                            kodik,
                            info,
                            idType,
                            id,
                            seasonDir,
                            seasonNumber,
                            title,
                            baseUrl,
                            cfg.CreateStrmPerVoiceTranslation,
                            existingEpisodeTranslationFileBaseNames,
                            kodikEpisodesToProcess,
                            expectedEpisodeFileBaseNames,
                            expectedEpisodeTranslationKeys,
                            perf,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                using (perf.Measure("stage.cleanup.artifacts"))
                {
                    CleanupUnexpectedEpisodeArtifacts(
                        logger,
                        seasonDir,
                        seasonNumber,
                        expectedEpisodeFileBaseNames,
                        kodikAvailableEpisodes,
                        perf);
                }

                if (missingEpisodes.Length == 0 && kodikGeneration.FilesWritten == 0)
                {
                    logger.LogInformation(
                        "[YummyKodik] Done refreshing '{Title}'. Yummy-backed providers already cover all currently available episodes and translations.",
                        title);
                    return;
                }

                logger.LogInformation(
                    "[YummyKodik] Done refreshing '{Title}'. SeriesCount: {SeriesCount}, translations: {Translations}, supplementedEpisodes: {SupplementedEpisodes}, supplementedFiles: {SupplementedFiles}.",
                    title,
                    info.SeriesCount,
                    info.Translations.Count,
                    kodikGeneration.EpisodesWritten,
                    kodikGeneration.FilesWritten);
            }
            finally
            {
                perf.LogSummary(logger, summaryTitle, cleanKey);
            }
        }

        private static async Task GeneratePreferredProviderEpisodeFilesAsync(
            ILogger logger,
            YummyAnimeResponse anime,
            YummyVideoCatalog videoCatalog,
            IReadOnlyCollection<int> supportedEpisodes,
            string seasonDir,
            int seasonNumber,
            string title,
            string baseUrl,
            string? preferredTranslationFilter,
            bool createStrmPerVoiceTranslation,
            IDictionary<int, Dictionary<string, string>> existingEpisodeTranslationFileBaseNames,
            IDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
            IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            foreach (var ep in supportedEpisodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var baseName = BuildEpisodeBaseName(seasonDir, seasonNumber, ep);

                if (!createStrmPerVoiceTranslation)
                {
                    var provider = videoCatalog.PickPreferredProvider(ep, preferredFilter: preferredTranslationFilter, providers: PreferredYummyProviderOrder);
                    if (!provider.HasValue)
                    {
                        continue;
                    }

                    var url = BuildProviderStreamUrl(baseUrl, provider.Value, anime.AnimeId, ep) + "&format=hls";
                    await WriteEpisodeArtifactsAsync(logger, seasonDir, baseName, url, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                        .ConfigureAwait(false);
                    TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, baseName);
                    continue;
                }

                var voiceNames = videoCatalog.GetSupportedVoiceNamesAcrossProviders(ep, PreferredYummyProviderOrder);
                if (voiceNames.Count == 0)
                {
                    var provider = videoCatalog.PickPreferredProvider(ep, preferredFilter: preferredTranslationFilter, providers: PreferredYummyProviderOrder);
                    if (!provider.HasValue)
                    {
                        continue;
                    }

                    var url = BuildProviderStreamUrl(baseUrl, provider.Value, anime.AnimeId, ep) + "&format=hls";
                    var fileBaseName = baseName + " - Auto";
                    await WriteEpisodeArtifactsAsync(logger, seasonDir, fileBaseName, url, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                        .ConfigureAwait(false);
                    TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                    TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, "Auto");
                    continue;
                }

                foreach (var voiceName in voiceNames)
                {
                    var provider = videoCatalog.PickPreferredProvider(ep, explicitVoiceName: voiceName, providers: PreferredYummyProviderOrder);
                    if (!provider.HasValue)
                    {
                        continue;
                    }

                    var chosenEntry = videoCatalog.FindPreferredPlayableEntry(provider.Value, ep, voiceName);
                    if (chosenEntry == null)
                    {
                        continue;
                    }

                    var suffix = SafeFilename(voiceName);
                    if (string.IsNullOrWhiteSpace(suffix))
                    {
                        suffix = "Voice";
                    }

                    var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(
                        existingEpisodeTranslationFileBaseNames,
                        ep,
                        baseName,
                        suffix);
                    var url = BuildProviderStreamUrl(baseUrl, provider.Value, anime.AnimeId, ep, voiceName, chosenEntry) + "&format=hls";
                    await WriteEpisodeArtifactsAsync(
                            logger,
                            seasonDir,
                            fileBaseName,
                            url,
                            ep,
                            seasonNumber,
                            title,
                            anime.Description,
                            perf,
                            cancellationToken)
                        .ConfigureAwait(false);
                    TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                    TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, suffix);
                }
            }

            var expectedAvailableEpisodes = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime);
            if (expectedAvailableEpisodes > supportedEpisodes.Count)
            {
                logger.LogInformation(
                    "[YummyKodik] Yummy-backed providers currently cover {CoveredEpisodes}/{TotalEpisodes} episodes for '{Title}'.",
                    supportedEpisodes.Count,
                    expectedAvailableEpisodes,
                    title);
            }
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
            IReadOnlyList<KodikTranslation> fileTranslations = createStrmPerVoiceTranslation
                ? PickTranslationsForFileMode(info.Translations)
                : Array.Empty<KodikTranslation>();
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

                var baseName = BuildEpisodeBaseName(seasonDir, seasonNumber, ep);

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

                    var strmPath = Path.Combine(seasonDir, fileBaseName + ".strm");
                    var nfoPath = Path.Combine(seasonDir, fileBaseName + ".nfo");

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
            IReadOnlyList<KodikTranslation> translations,
            IReadOnlyCollection<int> episodes,
            RefreshPerformanceMetrics? perf,
            CancellationToken cancellationToken)
        {
            var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
            if (translations.Count == 0 || episodes.Count == 0)
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
            var strmPath = Path.Combine(seasonDir, fileBaseName + ".strm");
            var nfoPath = Path.Combine(seasonDir, fileBaseName + ".nfo");

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

        private static string BuildEpisodeBaseName(string seasonDir, int seasonNumber, int episodeNumber)
        {
            var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            return $"S{effectiveSeasonNumber:00}E{episodeNumber:00}";
        }

        private static void TryMigrateIncorrectCalendarSeasonFolder(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            if (seasonNumber != 1 || string.IsNullOrWhiteSpace(seriesRoot) || string.IsNullOrWhiteSpace(seasonDir))
            {
                return;
            }

            if (!Directory.Exists(seriesRoot))
            {
                return;
            }

            try
            {
                var mistakenDirs = Directory.EnumerateDirectories(seriesRoot, "Season *", SearchOption.TopDirectoryOnly)
                    .Where(path => !string.Equals(path, seasonDir, StringComparison.OrdinalIgnoreCase))
                    .Select(path => new
                    {
                        Path = path,
                        Match = Regex.Match(
                            Path.GetFileName(path) ?? string.Empty,
                            @"^Season (?<season>\d{2})$",
                            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                            matchTimeout: TimeSpan.FromSeconds(1))
                    })
                    .Where(x => x.Match.Success)
                    .Select(x => new
                    {
                        x.Path,
                        Season = int.Parse(x.Match.Groups["season"].Value)
                    })
                    .Where(x => x.Season > 1)
                    .ToList();

                foreach (var mistakenDir in mistakenDirs)
                {
                    var movedCount = MoveSeasonArtifactsForSeason(logger, mistakenDir.Path, seasonDir, seasonNumber);
                    if (movedCount <= 0)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "[YummyKodik] Reconciled {Count} season {Season} artifact(s) from '{Old}' into '{New}'.",
                        movedCount,
                        seasonNumber,
                        mistakenDir.Path,
                        seasonDir);

                    TryDeleteEmptySeasonDirectory(logger, mistakenDir.Path);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[YummyKodik] Failed to reconcile mistaken season folder under '{SeriesRoot}'.", seriesRoot);
            }
        }


        private static void MigrateLegacySeasonFolder(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            if (seasonNumber == 1)
            {
                return;
            }

            var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");

            if (!Directory.Exists(legacySeasonDir))
            {
                return;
            }

            try
            {
                var movedCount = MoveSeasonArtifactsForSeason(logger, legacySeasonDir, seasonDir, seasonNumber);
                if (movedCount <= 0)
                {
                    return;
                }

                logger.LogInformation(
                    "[YummyKodik] Reconciled {Count} season {Season} artifact(s) from legacy season folder '{Old}' into '{New}'.",
                    movedCount,
                    seasonNumber,
                    legacySeasonDir,
                    seasonDir);

                TryDeleteEmptySeasonDirectory(logger, legacySeasonDir);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[YummyKodik] Failed to reconcile legacy season folder '{Old}' -> '{New}'.", legacySeasonDir, seasonDir);
            }
        }

        private static void MigrateLegacyEpisodeFileNames(ILogger logger, string seasonDir, int seasonNumber)
        {
            if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
            {
                return;
            }

            var normalizedSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            string newSeasonPrefix = "S" + normalizedSeasonNumber.ToString("00");

            try
            {
                var files = Directory.EnumerateFiles(seasonDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var ext = Path.GetExtension(p);
                        return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                foreach (var path in files)
                {
                    var fileName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                    if (!TryGetEpisodeFileSeasonPrefix(nameNoExt, out var currentSeasonPrefix))
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(path);
                    if (string.Equals(currentSeasonPrefix, newSeasonPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                        {
                            TryUpdateEpisodeNfoSeason(logger, path, normalizedSeasonNumber);
                        }

                        continue;
                    }

                    var renamedNoExt = newSeasonPrefix + nameNoExt.Substring(3);
                    var target = Path.Combine(seasonDir, renamedNoExt + ext);

                    if (File.Exists(target))
                    {
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "[YummyKodik] Failed to delete legacy file '{Path}'.", path);
                        }

                        continue;
                    }

                    try
                    {
                        File.Move(path, target);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[YummyKodik] Failed to rename file '{Old}' -> '{New}'.", path, target);
                        continue;
                    }

                    if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                    {
                        TryUpdateEpisodeNfoSeason(logger, target, normalizedSeasonNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Season file migration failed. seasonDir='{SeasonDir}' season={Season}", seasonDir, seasonNumber);
            }
        }

        private static int MoveSeasonArtifactsForSeason(ILogger logger, string sourceDir, string targetDir, int seasonNumber)
        {
            if (string.IsNullOrWhiteSpace(sourceDir) ||
                string.IsNullOrWhiteSpace(targetDir) ||
                !Directory.Exists(sourceDir) ||
                string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            Directory.CreateDirectory(targetDir);

            var movedCount = 0;
            var files = Directory.EnumerateFiles(sourceDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(p =>
                {
                    var ext = Path.GetExtension(p);
                    return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            foreach (var group in files
                         .GroupBy(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                         .Where(g => !string.IsNullOrWhiteSpace(g.Key)))
            {
                var detectedSeason = DetectSeasonNumberFromArtifacts(group);
                if (detectedSeason != seasonNumber)
                {
                    continue;
                }

                foreach (var path in group)
                {
                    var ext = Path.GetExtension(path);
                    var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
                    var targetNameNoExt = RewriteEpisodeFileSeasonPrefix(nameNoExt, seasonNumber);
                    var targetPath = Path.Combine(targetDir, targetNameNoExt + ext);

                    try
                    {
                        if (!string.Equals(path, targetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            if (File.Exists(targetPath))
                            {
                                File.Delete(path);
                            }
                            else
                            {
                                File.Move(path, targetPath);
                            }
                        }

                        if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                        {
                            TryUpdateEpisodeNfoSeason(logger, targetPath, seasonNumber);
                        }

                        movedCount++;
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[YummyKodik] Failed to move season artifact '{Path}' -> '{TargetPath}'.", path, targetPath);
                    }
                }
            }

            return movedCount;
        }

        private static int? DetectSeasonNumberFromArtifacts(IEnumerable<string> paths)
        {
            var artifactPaths = paths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (artifactPaths == null || artifactPaths.Count == 0)
            {
                return null;
            }

            var nfoSeasonNumbers = artifactPaths
                .Where(path => Path.GetExtension(path).Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                .Select(TryReadEpisodeSeasonFromNfo)
                .Where(season => season.HasValue && season.Value > 0)
                .Select(season => season!.Value)
                .Distinct()
                .ToList();

            if (nfoSeasonNumbers.Count == 1)
            {
                return nfoSeasonNumbers[0];
            }

            if (nfoSeasonNumbers.Count > 1)
            {
                return null;
            }

            var fileNameSeasonNumbers = artifactPaths
                .Select(path => TryReadEpisodeSeasonFromFileName(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                .Where(season => season.HasValue && season.Value > 0)
                .Select(season => season!.Value)
                .Distinct()
                .ToList();

            return fileNameSeasonNumbers.Count == 1 ? fileNameSeasonNumbers[0] : null;
        }

        private static int? TryReadEpisodeSeasonFromNfo(string nfoPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath))
                {
                    return null;
                }

                var xml = File.ReadAllText(nfoPath);
                if (string.IsNullOrWhiteSpace(xml))
                {
                    return null;
                }

                var match = Regex.Match(
                    xml,
                    @"<season>\s*(?<season>\d+)\s*</season>",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    matchTimeout: TimeSpan.FromSeconds(1));

                if (!match.Success ||
                    !int.TryParse(match.Groups["season"].Value, out var seasonNumber) ||
                    seasonNumber <= 0)
                {
                    return null;
                }

                return seasonNumber;
            }
            catch
            {
                return null;
            }
        }

        private static int? TryReadEpisodeSeasonFromFileName(string fileNameWithoutExtension)
        {
            return TryGetEpisodeFileSeasonPrefix(fileNameWithoutExtension, out var seasonPrefix) &&
                   int.TryParse(seasonPrefix.AsSpan(1), out var seasonNumber) &&
                   seasonNumber > 0
                ? seasonNumber
                : null;
        }

        private static bool TryGetEpisodeFileSeasonPrefix(string fileNameWithoutExtension, out string seasonPrefix)
        {
            seasonPrefix = string.Empty;

            if (string.IsNullOrWhiteSpace(fileNameWithoutExtension) ||
                fileNameWithoutExtension.Length < 4 ||
                fileNameWithoutExtension[0] != 'S' ||
                !char.IsDigit(fileNameWithoutExtension[1]) ||
                !char.IsDigit(fileNameWithoutExtension[2]) ||
                char.ToUpperInvariant(fileNameWithoutExtension[3]) != 'E')
            {
                return false;
            }

            seasonPrefix = fileNameWithoutExtension.Substring(0, 3);
            return true;
        }

        private static string RewriteEpisodeFileSeasonPrefix(string fileNameWithoutExtension, int seasonNumber)
        {
            if (!TryGetEpisodeFileSeasonPrefix(fileNameWithoutExtension, out _))
            {
                return fileNameWithoutExtension;
            }

            var normalizedSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
            return "S" + normalizedSeasonNumber.ToString("00") + fileNameWithoutExtension.Substring(3);
        }

        private static void TryDeleteEmptySeasonDirectory(ILogger logger, string directoryPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath))
                {
                    return;
                }

                if (Directory.EnumerateFileSystemEntries(directoryPath).Any())
                {
                    return;
                }

                Directory.Delete(directoryPath);
                logger.LogInformation("[YummyKodik] Removed empty legacy season folder '{Path}'.", directoryPath);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to delete empty season folder '{Path}'.", directoryPath);
            }
        }

        private static void TryUpdateEpisodeNfoSeason(ILogger logger, string nfoPath, int seasonNumber)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath))
                {
                    return;
                }

                var xml = File.ReadAllText(nfoPath);
                if (!IsValidXmlContent(xml))
                {
                    return;
                }

                // Replace first <season>...</season> with correct value.
                var updated = Regex.Replace(
                    xml,
                    @"<season>\s*\d+\s*</season>",
                    "<season>" + seasonNumber + "</season>",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    matchTimeout: TimeSpan.FromSeconds(1));

                if (!string.Equals(xml, updated, StringComparison.Ordinal))
                {
                    WriteTextAtomically(nfoPath, updated);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to update season number inside nfo '{Path}'.", nfoPath);
            }
        }

        private static IReadOnlyList<KodikTranslation> PickTranslationsForFileMode(IReadOnlyList<KodikTranslation> translations)
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

            using var http = new HttpClient();
            var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var fs = File.OpenWrite(posterPath);
            await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
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

        private static TextWriteOutcome WriteTextAtomically(
            string path,
            string content,
            RefreshPerformanceMetrics? perf = null,
            string artifactKind = "text")
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
                var existingContent = File.ReadAllText(path);
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
                File.WriteAllText(tempPath, content);

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
