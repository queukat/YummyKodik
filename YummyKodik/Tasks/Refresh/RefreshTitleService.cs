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
        string internalBaseUrl,
        PluginConfiguration cfg,
        RefreshClients clients,
        CancellationToken cancellationToken)
    {
        var perf = new RefreshPerformanceMetrics(cfg.EnablePerformanceDebugLogging, clients.RunMetrics);
        var cleanKey = RefreshPathUtilities.NormalizeKey(key);
        _logger.LogInformation("[YummyKodik] Refreshing key '{Key}'.", cleanKey);
        var summaryTitle = cleanKey;

        try
        {
            YummyRefreshInfo refresh;
            try
            {
                refresh = await _infoLoader.LoadAsync(_logger, cfg, clients, cleanKey, root, internalBaseUrl, perf, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (CanTryExistingLibraryFallback(ex, cancellationToken))
            {
                if (await TryRefreshFromExistingLibraryFallbackAsync(
                        ex,
                        cleanKey,
                        root,
                        internalBaseUrl,
                        cfg,
                        clients,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                _logger.LogWarning(
                    ex,
                    "[YummyKodik] Failed to load Yummy metadata for key '{Key}' and no usable local fallback snapshot was found.",
                    cleanKey);
                return;
            }

            summaryTitle = refresh.TitleInfo.Title;

            var refreshStateInput = _stateService.BuildSeasonInput(cfg, refresh);
            var state = new EpisodeGenerationState();
            var seasonDir = refresh.Files.SeasonDir;
            var seasonDirPrepared = false;
            var needsKodikLookup = false;
            var deferYummyGenerationUntilKodik = cfg.CreateStrmPerVoiceTranslation &&
                                                 refresh.Availability.ExpectedAvailableEpisodes > 0;

            using (await _seriesRootLockProvider.AcquireAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
            {
                Directory.CreateDirectory(refresh.Files.SeriesRoot);

                await _seriesMetadataWriter.EnsureAsync(_logger, refresh, clients.YummyHttp, perf, cancellationToken).ConfigureAwait(false);

                if (await _stateService.TrySkipAsync(_logger, refresh, refreshStateInput, perf, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                if (await _stateService.TrySkipPerVoiceKodikLookupAsync(
                        _logger,
                        refresh,
                        refreshStateInput,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                if (!deferYummyGenerationUntilKodik && refresh.Availability.YummySupportedEpisodes.Length > 0)
                {
                    seasonDir = await GenerateYummyArtifactsAsync(
                            refresh,
                            state,
                            seasonDir,
                            cfg,
                            perf,
                            cancellationToken)
                        .ConfigureAwait(false);
                    seasonDirPrepared = true;
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
            var kodikCatalogSignature = RefreshStateService.BuildKodikCatalogSignature(kodikLookup);

            using (await _seriesRootLockProvider.AcquireAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
            {
                Directory.CreateDirectory(refresh.Files.SeriesRoot);

                var episodeCoverage = YummyEpisodeAvailability.ResolveProviderCoverage(
                    kodikLookup.Info.SeriesCount,
                    refresh.Availability.ExpectedAvailableEpisodes);
                var kodikAvailableEpisodes = episodeCoverage.KodikAvailableEpisodes;

                if (await _stateService.TrySkipPerVoiceDeepRefreshAsync(
                        _logger,
                        refresh,
                        refreshStateInput,
                        kodikCatalogSignature,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return;
                }

                if (deferYummyGenerationUntilKodik && refresh.Availability.YummySupportedEpisodes.Length > 0)
                {
                    seasonDir = await GenerateYummyArtifactsAsync(
                            refresh,
                            state,
                            seasonDir,
                            cfg,
                            perf,
                            cancellationToken)
                        .ConfigureAwait(false);
                    seasonDirPrepared = true;
                }

                if (_kodikSupplementService.CompleteIfKodikHasNoEpisodes(_logger, refresh, state, kodikAvailableEpisodes))
                {
                    await _stateService.WriteAsync(_logger, refresh, refreshStateInput, state, perf, cancellationToken).ConfigureAwait(false);
                    return;
                }

                _kodikSupplementService.LogKodikZeroSeriesCountIfNeeded(_logger, refresh, kodikLookup.Info, kodikAvailableEpisodes);

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
                    CleanupExpectedEpisodeArtifacts(refresh, state, seasonDir, episodeCoverage.OverallAvailableEpisodes, perf);
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

                CleanupExpectedEpisodeArtifacts(refresh, state, seasonDir, episodeCoverage.OverallAvailableEpisodes, perf);
                var kodikValidation = kodikGeneration.DeepValidationCompleted
                    ? new RefreshStateKodikValidation
                    {
                        CatalogSignature = kodikCatalogSignature,
                        DeepValidatedAtUtc = DateTimeOffset.UtcNow
                    }
                    : null;
                await _stateService.WriteAsync(
                        _logger,
                        refresh,
                        refreshStateInput,
                        state,
                        kodikValidation,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!kodikGeneration.DeepValidationCompleted)
                {
                    _logger.LogWarning(
                        "[YummyKodik] Kodik deep validation was incomplete for '{Title}'. Generated files were kept, but the next refresh will retry validation.",
                        refresh.TitleInfo.Title);
                }

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

    private async Task<string> GenerateYummyArtifactsAsync(
        YummyRefreshInfo refresh,
        EpisodeGenerationState state,
        string seasonDir,
        PluginConfiguration cfg,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        seasonDir = _seasonFilePreparer.PrepareForEpisodeGeneration(
            _logger,
            refresh,
            seasonDir,
            cfg.CreateStrmPerVoiceTranslation,
            state,
            perf);

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

        return seasonDir;
    }

    private async Task<bool> TryRefreshFromExistingLibraryFallbackAsync(
        Exception originalError,
        string cleanKey,
        string root,
        string internalBaseUrl,
        PluginConfiguration cfg,
        RefreshClients clients,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        var fallbackLoader = new ExistingLibraryFallbackRefreshInfoLoader();
        var refresh = fallbackLoader.TryLoad(_logger, cleanKey, root, internalBaseUrl);
        if (refresh == null)
        {
            return false;
        }

        var kodikLookup = await _kodikSupplementService
            .TryResolveKodikInfoAsync(_logger, refresh, cleanKey, clients, perf, cancellationToken)
            .ConfigureAwait(false);
        if (kodikLookup == null)
        {
            return true;
        }

        var kodikClients = await clients.KodikClients.Value.ConfigureAwait(false);

        using (await _seriesRootLockProvider.AcquireAsync(refresh.Files.SeriesRoot, cancellationToken).ConfigureAwait(false))
        {
            Directory.CreateDirectory(refresh.Files.SeriesRoot);
            Directory.CreateDirectory(refresh.Files.SeasonDir);

            var state = ExistingLibraryFallbackRefreshInfoLoader.BuildExistingEpisodeState(
                refresh.Files.SeasonDir,
                refresh.TitleInfo.SeasonNumber);
            var existingMaxEpisode = state.GeneratedEpisodeNumbers.Count > 0
                ? state.GeneratedEpisodeNumbers.Max()
                : 0;
            var kodikAvailableEpisodes = Math.Max(
                kodikLookup.Info.SeriesCount,
                existingMaxEpisode);

            if (_kodikSupplementService.CompleteIfKodikHasNoEpisodes(_logger, refresh, state, kodikAvailableEpisodes))
            {
                return true;
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
                _logger.LogInformation(
                    "[YummyKodik] Provider-only fallback refresh found no missing episodes for '{Title}'. Existing local files were kept.",
                    refresh.TitleInfo.Title);
                return true;
            }

            EpisodeArtifactGenerationResult kodikGeneration;
            using (perf.Measure("stage.generate.kodik.fallback.files"))
            {
                kodikGeneration = await _kodikSupplementService.GenerateEpisodeFilesAsync(
                        kodikEpisodesToProcess,
                        new KodikEpisodeGenerationContext(
                            _logger,
                            refresh,
                            kodikLookup,
                            kodikClients.Kodik,
                            state,
                            refresh.Files.SeasonDir,
                            cfg.CreateStrmPerVoiceTranslation,
                            perf),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            _logger.LogWarning(
                originalError,
                "[YummyKodik] Provider-only fallback refresh completed for '{Title}'. Kodik seriesCount={SeriesCount}, newOrUpdatedEpisodes={Episodes}, filesWritten={Files}. Existing Yummy-backed files were not cleaned while Yummy is unavailable.",
                refresh.TitleInfo.Title,
                kodikLookup.Info.SeriesCount,
                kodikGeneration.EpisodesWritten,
                kodikGeneration.FilesWritten);
        }

        return true;
    }

    private static bool CanTryExistingLibraryFallback(Exception ex, CancellationToken cancellationToken)
    {
        return !cancellationToken.IsCancellationRequested &&
               ex is not OperationCanceledException;
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
