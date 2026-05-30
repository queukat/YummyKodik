using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class RefreshTitleService
{
    private readonly ILogger _logger;
    private readonly YummyRefreshInfoLoader _infoLoader;
    private readonly SeriesRootLockProvider _seriesRootLockProvider;
    private readonly SeriesMetadataWriter _seriesMetadataWriter;
    private readonly SeasonFilePreparer _seasonFilePreparer;
    private readonly YummyEpisodeArtifactGenerator _yummyEpisodeGenerator;
    private readonly KodikSupplementService _kodikSupplementService;
    private readonly RefreshStateService _stateService;
    private readonly EpisodeArtifactWriter _artifactWriter;

    public RefreshTitleService(
        ILogger logger,
        YummyRefreshInfoLoader infoLoader,
        SeriesRootLockProvider seriesRootLockProvider,
        SeriesMetadataWriter seriesMetadataWriter,
        SeasonFilePreparer seasonFilePreparer,
        YummyEpisodeArtifactGenerator yummyEpisodeGenerator,
        KodikSupplementService kodikSupplementService,
        RefreshStateService stateService,
        EpisodeArtifactWriter artifactWriter)
    {
        _logger = logger;
        _infoLoader = infoLoader;
        _seriesRootLockProvider = seriesRootLockProvider;
        _seriesMetadataWriter = seriesMetadataWriter;
        _seasonFilePreparer = seasonFilePreparer;
        _yummyEpisodeGenerator = yummyEpisodeGenerator;
        _kodikSupplementService = kodikSupplementService;
        _stateService = stateService;
        _artifactWriter = artifactWriter;
    }

    public async Task RefreshAsync(
        string key,
        string root,
        PluginConfiguration cfg,
        RefreshClients clients,
        CancellationToken cancellationToken)
    {
        var perf = new RefreshPerformanceMetrics(cfg.EnablePerformanceDebugLogging);
        var cleanKey = RefreshPathUtilities.NormalizeKey(key);
        _logger.LogInformation("[YummyKodik] Refreshing key '{Key}'.", cleanKey);
        var summaryTitle = cleanKey;

        try
        {
            var refresh = await _infoLoader.LoadAsync(_logger, cfg, clients, cleanKey, root, perf, cancellationToken)
                .ConfigureAwait(false);
            summaryTitle = refresh.TitleInfo.Title;

            var refreshStateInput = _stateService.BuildSeasonInput(cfg, refresh);
            var state = new EpisodeGenerationState();
            var seasonDir = refresh.Files.SeasonDir;
            var seasonDirPrepared = false;
            var needsKodikLookup = false;

            using (await _seriesRootLockProvider.AcquireAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
            {
                Directory.CreateDirectory(refresh.Files.SeriesRoot);

                await _seriesMetadataWriter.EnsureAsync(_logger, refresh, clients.YummyHttp, perf, cancellationToken).ConfigureAwait(false);

                if (await _stateService.TrySkipAsync(_logger, refresh, refreshStateInput, perf, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (refresh.Availability.YummySupportedEpisodes.Length > 0)
                {
                    if (string.IsNullOrEmpty(refresh.Files.BaseUrl))
                    {
                        _logger.LogWarning(
                            "[YummyKodik] ServerBaseUrl is empty, skipping Yummy-backed STRM generation for '{Title}'.",
                            refresh.TitleInfo.Title);
                        return;
                    }

                    seasonDir = _seasonFilePreparer.PrepareForEpisodeGeneration(
                        _logger,
                        refresh,
                        seasonDir,
                        cfg.CreateStrmPerVoiceTranslation,
                        state,
                        perf);
                    seasonDirPrepared = true;

                    using (perf.Measure("stage.generate.yummy.files"))
                    {
                        await _yummyEpisodeGenerator.GeneratePreferredProviderEpisodeFilesAsync(
                                new YummyEpisodeGenerationContext(
                                    _logger,
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

                    _logger.LogInformation(
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
                    CompleteWithoutKodikSupplement(refresh, state, seasonDir, seasonDirPrepared, perf);
                    await _stateService.WriteAsync(_logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);
                    return;
                }

                needsKodikLookup = true;
            }

            if (!needsKodikLookup)
            {
                return;
            }

            var kodikLookup = await _kodikSupplementService.TryResolveKodikInfoAsync(_logger, refresh, cleanKey, clients, perf, cancellationToken)
                .ConfigureAwait(false);
            if (kodikLookup == null)
            {
                return;
            }

            var kodikClients = await clients.KodikClients.Value.ConfigureAwait(false);

            using (await _seriesRootLockProvider.AcquireAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
            {
                Directory.CreateDirectory(refresh.Files.SeriesRoot);

                var kodikAvailableEpisodes = YummyEpisodeAvailability.ResolveKodikAvailableEpisodeCount(
                    kodikLookup.Info.SeriesCount,
                    refresh.Availability.ExpectedAvailableEpisodes);
                if (_kodikSupplementService.CompleteIfKodikHasNoEpisodes(_logger, refresh, state, kodikAvailableEpisodes))
                {
                    await _stateService.WriteAsync(_logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _kodikSupplementService.LogKodikZeroSeriesCountIfNeeded(_logger, refresh, kodikLookup.Info, kodikAvailableEpisodes);

                if (string.IsNullOrEmpty(refresh.Files.BaseUrl))
                {
                    _logger.LogWarning("[YummyKodik] ServerBaseUrl is empty, skipping refresh for '{Title}'.", refresh.TitleInfo.Title);
                    return;
                }

                if (!seasonDirPrepared)
                {
                    seasonDir = _seasonFilePreparer.PrepareForEpisodeGeneration(
                        _logger,
                        refresh,
                        seasonDir,
                        cfg.CreateStrmPerVoiceTranslation,
                        state,
                        perf);
                }

                var missingEpisodes = Enumerable.Range(1, kodikAvailableEpisodes)
                    .Where(ep => !state.GeneratedEpisodeNumbers.Contains(ep))
                    .ToArray();
                var kodikEpisodesToProcess = _kodikSupplementService.ResolveEpisodesToProcess(
                    cfg.CreateStrmPerVoiceTranslation,
                    kodikAvailableEpisodes,
                    missingEpisodes);

                if (kodikEpisodesToProcess.Length == 0)
                {
                    CleanupExpectedEpisodeArtifacts(refresh, state, seasonDir, kodikAvailableEpisodes, perf);
                    await _stateService.WriteAsync(_logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);

                    _logger.LogInformation(
                        "[YummyKodik] Done refreshing '{Title}'. Yummy-backed providers already cover all {SeriesCount} currently available episodes.",
                        refresh.TitleInfo.Title,
                        kodikLookup.Info.SeriesCount);
                    return;
                }

                EpisodeArtifactGenerationResult kodikGeneration;
                using (perf.Measure("stage.generate.kodik.files"))
                {
                    kodikGeneration = await _kodikSupplementService.GenerateEpisodeFilesAsync(
                            kodikEpisodesToProcess,
                            new KodikEpisodeGenerationContext(
                                _logger,
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

                CleanupExpectedEpisodeArtifacts(refresh, state, seasonDir, kodikAvailableEpisodes, perf);
                await _stateService.WriteAsync(_logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);

                if (missingEpisodes.Length == 0 && kodikGeneration.FilesWritten == 0)
                {
                    _logger.LogInformation(
                        "[YummyKodik] Done refreshing '{Title}'. Yummy-backed providers already cover all currently available episodes and translations.",
                        refresh.TitleInfo.Title);
                    return;
                }

                _logger.LogInformation(
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
            perf.LogSummary(_logger, summaryTitle, cleanKey);
        }
    }

    private void CompleteWithoutKodikSupplement(
        YummyRefreshInfo refresh,
        EpisodeGenerationState state,
        string seasonDir,
        bool seasonDirPrepared,
        RefreshPerformanceMetrics perf)
    {
        if (seasonDirPrepared && refresh.Availability.ExpectedAvailableEpisodes > 0)
        {
            CleanupExpectedEpisodeArtifacts(
                refresh,
                state,
                seasonDir,
                refresh.Availability.ExpectedAvailableEpisodes,
                perf);
        }

        if (refresh.Availability.ExpectedAvailableEpisodes <= 0)
        {
            _logger.LogInformation(
                "[YummyKodik] No episodes are available yet for '{Title}'. Series card, poster, and season folders were created/updated; Kodik lookup skipped.",
                refresh.TitleInfo.Title);
            return;
        }

        _logger.LogInformation(
            "[YummyKodik] Done refreshing '{Title}' using Yummy-backed coverage only. episodes={EpisodeCount}",
            refresh.TitleInfo.Title,
            state.GeneratedEpisodeNumbers.Count);
    }

    private void CleanupExpectedEpisodeArtifacts(
        YummyRefreshInfo refresh,
        EpisodeGenerationState state,
        string seasonDir,
        int maxAvailableEpisodeNumber,
        RefreshPerformanceMetrics perf)
    {
        using (perf.Measure("stage.cleanup.artifacts"))
        {
            _artifactWriter.CleanupUnexpectedEpisodeArtifacts(
                _logger,
                seasonDir,
                refresh.TitleInfo.SeasonNumber,
                state.ExpectedEpisodeFileBaseNames,
                maxAvailableEpisodeNumber,
                perf);
        }
    }
}
