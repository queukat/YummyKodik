using System.Text.Json;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class RefreshStateService
{
    public async Task<bool> TrySkipAsync(
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
                var decision = await RefreshStateManager
                    .EvaluateSingleFileRefreshAsync(refresh.Files.SeriesRoot, refreshStateInput, cancellationToken)
                    .ConfigureAwait(false);
                ObserveDecision(perf, decision);
                if (!decision.ShouldSkip)
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

    public async Task WriteAsync(
        ILogger logger,
        YummyRefreshInfo refresh,
        RefreshStateSeasonInput refreshStateInput,
        EpisodeGenerationState state,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        await WriteAsync(
                logger,
                refresh,
                refreshStateInput,
                state,
                kodikValidation: null,
                perf,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteAsync(
        ILogger logger,
        YummyRefreshInfo refresh,
        RefreshStateSeasonInput refreshStateInput,
        EpisodeGenerationState state,
        RefreshStateKodikValidation? kodikValidation,
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
                        state.MediaSegmentEntriesByFileBaseName,
                        kodikValidation,
                        cancellationToken)
                    .ConfigureAwait(false);

                perf.AddCount(written ? "state.written" : "state.write_skipped_incomplete_files");
                if (written)
                {
                    perf.AddCount("io.state_updated");
                }
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

    public async Task<bool> TrySkipPerVoiceDeepRefreshAsync(
        ILogger logger,
        YummyRefreshInfo refresh,
        RefreshStateSeasonInput refreshStateInput,
        string kodikCatalogSignature,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        using (perf.Measure("stage.refresh.state.per_voice_check"))
        {
            try
            {
                var decision = await RefreshStateManager
                    .EvaluatePerVoiceDeepRefreshAsync(
                        refresh.Files.SeriesRoot,
                        refreshStateInput,
                        kodikCatalogSignature,
                        DateTimeOffset.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false);
                ObserveDecision(perf, decision);
                if (!decision.ShouldSkip)
                {
                    return false;
                }

                perf.AddCount("state.per_voice_deep_skip");
                logger.LogInformation(
                    "[YummyKodik] Skipping deep per-voice refresh for '{Title}' because Yummy inputs, Kodik catalog, and generated files are unchanged.",
                    refresh.TitleInfo.Title);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                perf.AddCount("state.per_voice_check_failed");
                logger.LogDebug(
                    ex,
                    "[YummyKodik] Per-voice refresh state check failed for '{Title}', running deep validation.",
                    refresh.TitleInfo.Title);
                return false;
            }
        }
    }

    public async Task<bool> TrySkipPerVoiceKodikLookupAsync(
        ILogger logger,
        YummyRefreshInfo refresh,
        RefreshStateSeasonInput refreshStateInput,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        using (perf.Measure("stage.refresh.state.per_voice_kodik_lookup_check"))
        {
            try
            {
                var decision = await RefreshStateManager
                    .EvaluatePerVoiceKodikLookupAsync(
                        refresh.Files.SeriesRoot,
                        refreshStateInput,
                        DateTimeOffset.UtcNow,
                        cancellationToken)
                    .ConfigureAwait(false);
                ObserveDecision(perf, decision);
                if (!decision.ShouldSkip)
                {
                    return false;
                }

                perf.AddCount("state.per_voice_kodik_lookup_skip");
                logger.LogInformation(
                    "[YummyKodik] Skipping '{Title}' before Kodik lookup because high-quality inputs and verified fallback files are unchanged.",
                    refresh.TitleInfo.Title);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                perf.AddCount("state.per_voice_kodik_lookup_check_failed");
                logger.LogDebug(
                    ex,
                    "[YummyKodik] Pre-Kodik per-voice state check failed for '{Title}', continuing refresh.",
                    refresh.TitleInfo.Title);
                return false;
            }
        }
    }

    public static string BuildKodikCatalogSignature(KodikLookupResult lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        return RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
        {
            IdType = lookup.IdType.ToString(),
            Id = lookup.Id,
            SeriesCount = lookup.Info.SeriesCount,
            Translations = lookup.Info.Translations
                .Select(BuildKodikTranslationInput)
                .ToArray()
        });
    }

    private static RefreshStateKodikTranslationInput BuildKodikTranslationInput(KodikTranslation translation)
    {
        return new RefreshStateKodikTranslationInput
        {
            Id = translation.Id,
            Type = translation.Type,
            Name = translation.Name,
            MaxEpisode = translation.MaxEpisode,
            AvailableEpisodes = translation.AvailableEpisodes
                .Where(episode => episode > 0)
                .Distinct()
                .OrderBy(episode => episode)
                .ToArray()
        };
    }

    private static void ObserveDecision(RefreshPerformanceMetrics perf, RefreshSkipDecision decision)
    {
        perf.AddCount($"skip.{decision.Path}.{decision.Reason}");
        perf.AddCount("skip.files_checked", decision.ManagedFilesChecked);
        perf.AddCount("skip.files_expected", decision.ManagedFilesExpected);
        perf.AddCount("skip.unexpected_artifacts", decision.UnexpectedArtifactCount);
    }

    public RefreshStateSeasonInput BuildSeasonInput(PluginConfiguration cfg, YummyRefreshInfo refresh)
    {
        var seasonKey = RefreshStateManager.BuildSeasonKey(refresh.TitleInfo.SeasonNumber);
        return new RefreshStateSeasonInput
        {
            SeasonKey = seasonKey,
            SeasonNumber = refresh.TitleInfo.SeasonNumber,
            CleanKey = refresh.TitleInfo.CleanKey,
            CreateStrmPerVoiceTranslation = cfg.CreateStrmPerVoiceTranslation,
            PreferredQuality = cfg.PreferredQuality > 0 ? cfg.PreferredQuality : 720,
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
            StreamGatewayBaseUrl = refresh.Files.BaseUrl,
            PreferredTranslationFilter = cfg.PreferredTranslationFilter,
            PreferredQuality = cfg.PreferredQuality > 0 ? cfg.PreferredQuality : 720,
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

    internal static IReadOnlyList<string> BuildProviderCoverageFingerprintItems(
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
                providers: RefreshConstants.PreferredYummyProviderOrder);
            items.Add($"ep:{episodeNumber}:preferred:{preferredProvider?.ToString() ?? "none"}");

            if (!cfg.CreateStrmPerVoiceTranslation)
            {
                continue;
            }

            foreach (var voiceName in refresh.VideoCatalog.GetSupportedVoiceNamesAcrossProviders(
                         episodeNumber,
                         RefreshConstants.PreferredYummyProviderOrder))
            {
                var voiceProvider = refresh.VideoCatalog.PickPreferredProvider(
                    episodeNumber,
                    explicitVoiceName: voiceName,
                    providers: RefreshConstants.PreferredYummyProviderOrder);
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
}
