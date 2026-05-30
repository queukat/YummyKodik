using System.Text.Json;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
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

    public async Task WriteAsync(
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

    public RefreshStateSeasonInput BuildSeasonInput(PluginConfiguration cfg, YummyRefreshInfo refresh)
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
