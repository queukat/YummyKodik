using System.Text.Json;
using Microsoft.Extensions.Logging;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class KodikSupplementService
{
    private const int RuntimeProbeQuality = 720;

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
        var runtimeTranslations = PickTranslationsForFileMode(info.Translations);
        var translationResolution = fileTranslations.Count > 0
            ? await ResolveDistinctKodikTranslationEpisodesAsync(
                    logger,
                    kodik,
                    idType,
                    id,
                    fileTranslations,
                    orderedEpisodes,
                    expectedEpisodeTranslationKeys,
                    perf,
                    cancellationToken)
                .ConfigureAwait(false)
            : new KodikTranslationEpisodeResolution(
                new Dictionary<string, HashSet<int>>(StringComparer.Ordinal),
                new Dictionary<string, Dictionary<int, KodikLinkInfo>>(StringComparer.Ordinal),
                true);
        var resolvedTranslationEpisodes = translationResolution.EpisodesByTranslationId;

        foreach (var ep in orderedEpisodes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var durationSeconds = await TryReadExistingDurationSecondsAsync(
                    seasonDir,
                    seasonNumber,
                    ep,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!durationSeconds.HasValue)
            {
                durationSeconds = await ResolveKodikDurationSecondsAsync(
                        logger,
                        kodik,
                        idType,
                        id,
                        runtimeTranslations,
                        resolvedTranslationEpisodes,
                        translationResolution.LinksByTranslationId,
                        ep,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var baseName = RefreshPathUtilities.BuildEpisodeBaseName(seasonNumber, ep);

            var streamBase =
                $"{baseUrl}/YummyKodik/stream?type={idType.ToString().ToLowerInvariant()}" +
                $"&id={Uri.EscapeDataString(id)}&ep={ep}";

            if (!createStrmPerVoiceTranslation)
            {
                var url = streamBase + "&format=hls";
                await _artifactWriter.WriteEpisodeArtifactsAsync(
                        logger,
                        seasonDir,
                        baseName,
                        url,
                        ep,
                        seasonNumber,
                        title,
                        anime.Description,
                        durationSeconds,
                        perf,
                        cancellationToken)
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
                await _artifactWriter.WriteEpisodeArtifactsAsync(
                        logger,
                        seasonDir,
                        fileBaseName,
                        url,
                        ep,
                        seasonNumber,
                        title,
                        anime.Description,
                        durationSeconds,
                        perf,
                        cancellationToken)
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
                await _artifactWriter.EnsureEpisodeNfoAsync(
                        logger,
                        nfoPath,
                        ep,
                        seasonNumber,
                        title,
                        anime.Description,
                        durationSeconds,
                        perf,
                        cancellationToken)
                    .ConfigureAwait(false);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(expectedEpisodeFileBaseNames, ep, fileBaseName);
                EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, ep, suffix);
                writtenEpisodes.Add(ep);
                filesWritten++;
            }
        }

        return new EpisodeArtifactGenerationResult(
            writtenEpisodes.Count,
            filesWritten,
            translationResolution.Completed);
    }

    private static async Task<int?> TryReadExistingDurationSecondsAsync(
        string seasonDir,
        int seasonNumber,
        int episode,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(seasonDir))
        {
            return null;
        }

        var episodeBaseName = RefreshPathUtilities.BuildEpisodeBaseName(seasonNumber, episode);
        foreach (var nfoPath in Directory.EnumerateFiles(
                     seasonDir,
                     episodeBaseName + "*" + RefreshConstants.NfoExtension,
                     SearchOption.TopDirectoryOnly))
        {
            var fileBaseName = Path.GetFileNameWithoutExtension(nfoPath);
            if (!string.Equals(fileBaseName, episodeBaseName, StringComparison.OrdinalIgnoreCase) &&
                !fileBaseName.StartsWith(episodeBaseName + " - ", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var xml = await File.ReadAllTextAsync(nfoPath, cancellationToken).ConfigureAwait(false);
            if (NfoBuilder.TryGetEpisodeRuntimeSeconds(xml, out var durationSeconds))
            {
                return durationSeconds;
            }
        }

        return null;
    }

    internal static async Task<int?> ResolveKodikDurationSecondsAsync(
        ILogger logger,
        KodikClient kodik,
        KodikIdType idType,
        string id,
        IReadOnlyList<KodikTranslation> translations,
        IReadOnlyDictionary<string, HashSet<int>> resolvedTranslationEpisodes,
        IReadOnlyDictionary<string, Dictionary<int, KodikLinkInfo>> resolvedLinksByTranslationId,
        int episode,
        RefreshPerformanceMetrics? perf,
        CancellationToken cancellationToken)
    {
        var translation = translations.FirstOrDefault(candidate =>
        {
            var translationId = (candidate.Id ?? string.Empty).Trim();
            return translationId.Length > 0 &&
                   !string.Equals(translationId, "0", StringComparison.Ordinal) &&
                   candidate.CoversEpisode(episode) &&
                   (!resolvedTranslationEpisodes.TryGetValue(translationId, out var playableEpisodes) ||
                    playableEpisodes.Contains(episode));
        });
        var translationId = (translation?.Id ?? string.Empty).Trim();
        if (translationId.Length == 0)
        {
            return null;
        }

        try
        {
            perf?.AddCount("kodik.runtime_probes");
            TimeSpan? runtime;
            using (perf?.Measure("stage.kodik.runtime") ?? default)
            {
                if (resolvedLinksByTranslationId.TryGetValue(translationId, out var linksByEpisode) &&
                    linksByEpisode.TryGetValue(episode, out var resolvedLink))
                {
                    perf?.AddCount("kodik.runtime_link_reused");
                    runtime = await kodik.GetHlsRuntimeAsync(resolvedLink, RuntimeProbeQuality, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    perf?.AddCount("kodik.runtime_link_resolved");
                    runtime = await kodik.GetEpisodeRuntimeAsync(
                            id,
                            idType,
                            episode,
                            translationId,
                            RuntimeProbeQuality,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            return runtime.HasValue && runtime.Value > TimeSpan.Zero
                ? (int)Math.Round(runtime.Value.TotalSeconds, MidpointRounding.AwayFromZero)
                : null;
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is KodikException or HttpRequestException or TaskCanceledException or JsonException)
        {
            perf?.AddCount("kodik.runtime_probe_failures");
            logger.LogDebug(
                ex,
                "[YummyKodik] Failed to resolve Kodik episode runtime. idType={IdType} id={Id} episode={Episode} translationId={TranslationId}",
                idType,
                id,
                episode,
                translationId);
            return null;
        }
    }

    private static async Task<KodikTranslationEpisodeResolution> ResolveDistinctKodikTranslationEpisodesAsync(
        ILogger logger,
        KodikClient kodik,
        KodikIdType idType,
        string id,
        List<KodikTranslation> translations,
        int[] episodes,
        IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
        RefreshPerformanceMetrics? perf,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var links = new Dictionary<string, Dictionary<int, KodikLinkInfo>>(StringComparer.Ordinal);
        if (translations.Count == 0 || episodes.Length == 0)
        {
            return new KodikTranslationEpisodeResolution(result, links, true);
        }

        var completed = true;

        foreach (var tr in translations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trId = (tr.Id ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trId))
            {
                continue;
            }

            var candidateEpisodes = ResolveEpisodesNeedingKodikTranslation(
                tr,
                episodes,
                expectedEpisodeTranslationKeys);

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
            var resolvedLinks = new Dictionary<int, KodikLinkInfo>();
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
                        resolvedLinks[episode] = link;
                    }
                }
                catch (Exception ex) when (ex is KodikException or HttpRequestException or TaskCanceledException or JsonException)
                {
                    completed = false;
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
            foreach (var removedEpisode in resolvedLinks.Keys.Where(ep => !distinctEpisodes.Contains(ep)).ToArray())
            {
                resolvedLinks.Remove(removedEpisode);
            }

            if (resolvedLinks.Count > 0)
            {
                links[trId] = resolvedLinks;
            }
        }

        return new KodikTranslationEpisodeResolution(result, links, completed);
    }

    internal static int[] ResolveEpisodesNeedingKodikTranslation(
        KodikTranslation translation,
        IEnumerable<int> episodes,
        IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys)
    {
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(expectedEpisodeTranslationKeys);

        var translationId = (translation.Id ?? string.Empty).Trim();
        var suffix = RefreshPathUtilities.SafeFilename(BuildTranslationFileSuffix(translation));
        if (string.IsNullOrWhiteSpace(suffix))
        {
            suffix = "Translation_" + translationId;
        }

        return episodes
            .Where(episode =>
                translation.CoversEpisode(episode) &&
                !EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(
                    expectedEpisodeTranslationKeys,
                    episode,
                    suffix))
            .Distinct()
            .OrderBy(episode => episode)
            .ToArray();
    }

    private sealed record KodikTranslationEpisodeResolution(
        Dictionary<string, HashSet<int>> EpisodesByTranslationId,
        Dictionary<string, Dictionary<int, KodikLinkInfo>> LinksByTranslationId,
        bool Completed);

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
