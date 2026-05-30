using System.Text.Json;
using Microsoft.Extensions.Logging;
using YummyKodik.Kodik;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class KodikSupplementService
{
    private readonly EpisodeArtifactWriter _artifactWriter;

    public KodikSupplementService(EpisodeArtifactWriter artifactWriter)
    {
        _artifactWriter = artifactWriter;
    }

    public async Task<KodikLookupResult?> TryResolveKodikInfoAsync(
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

    public bool CompleteIfKodikHasNoEpisodes(
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

    public void LogKodikZeroSeriesCountIfNeeded(
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

    public int[] ResolveEpisodesToProcess(
        bool createStrmPerVoiceTranslation,
        int kodikAvailableEpisodes,
        int[] missingEpisodes)
    {
        return createStrmPerVoiceTranslation
            ? Enumerable.Range(1, kodikAvailableEpisodes).ToArray()
            : missingEpisodes;
    }

    public Task<EpisodeArtifactGenerationResult> GenerateEpisodeFilesAsync(
        IEnumerable<int> episodes,
        KodikEpisodeGenerationContext context,
        CancellationToken cancellationToken)
    {
        return GenerateEpisodeFilesAsync(
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

    private async Task<EpisodeArtifactGenerationResult> GenerateEpisodeFilesAsync(
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

            var baseName = RefreshPathUtilities.BuildEpisodeBaseName(seasonNumber, ep);

            var streamBase =
                $"{baseUrl}/YummyKodik/stream?type={idType.ToString().ToLowerInvariant()}" +
                $"&id={Uri.EscapeDataString(id)}&ep={ep}";

            if (!createStrmPerVoiceTranslation)
            {
                var url = streamBase + "&format=hls";
                await _artifactWriter.WriteEpisodeArtifactsAsync(logger, seasonDir, baseName, url, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                    .ConfigureAwait(false);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, baseName);
                writtenEpisodes.Add(ep);
                filesWritten++;
                continue;
            }

            if (fileTranslations.Count == 0)
            {
                if (EpisodeArtifactMaintenance.HasExpectedEpisodeArtifacts(expectedEpisodeFileBaseNames, ep))
                {
                    continue;
                }

                var url = streamBase + "&format=hls";
                var fileBaseName = baseName + " - Auto";
                await _artifactWriter.WriteEpisodeArtifactsAsync(logger, seasonDir, fileBaseName, url, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                    .ConfigureAwait(false);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, "Auto");
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
                var suffix = RefreshPathUtilities.SafeFilename(suffixRaw);
                if (string.IsNullOrWhiteSpace(suffix))
                {
                    suffix = "Translation_" + trId;
                }

                if (EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, suffix))
                {
                    continue;
                }

                var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(
                    existingEpisodeTranslationFileBaseNames,
                    ep,
                    baseName,
                    suffix);

                var strmPath = Path.Combine(seasonDir, fileBaseName + RefreshConstants.StrmExtension);
                var nfoPath = Path.Combine(seasonDir, fileBaseName + RefreshConstants.NfoExtension);

                if (!tr.CoversEpisode(ep))
                {
                    _artifactWriter.TryDeleteFile(logger, strmPath, perf);
                    _artifactWriter.TryDeleteFile(logger, nfoPath, perf);
                    continue;
                }

                if (resolvedTranslationEpisodes.TryGetValue(trId, out var playableEpisodes) &&
                    !playableEpisodes.Contains(ep))
                {
                    _artifactWriter.TryDeleteFile(logger, strmPath, perf);
                    _artifactWriter.TryDeleteFile(logger, nfoPath, perf);
                    continue;
                }

                var url = streamBase + $"&tr={Uri.EscapeDataString(trId)}&format=hls";

                await RefreshFileWriter.WriteTextAtomicallyAsync(strmPath, url + Environment.NewLine, perf, "strm", cancellationToken).ConfigureAwait(false);
                await _artifactWriter.EnsureEpisodeNfoAsync(logger, nfoPath, ep, seasonNumber, title, anime.Description, perf, cancellationToken)
                    .ConfigureAwait(false);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, suffix);
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
}
