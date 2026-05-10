using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Cvh;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Shikimori;
using YummyKodik.Tasks;
using YummyKodik.Util;
using YummyKodik.Web;
using YummyKodik.Yummy;
if (args.Length > 0 &&
    string.Equals(args[0], "--live-alloha-probe", StringComparison.OrdinalIgnoreCase))
{
    await RunLiveAllohaProbe();
    return;
}

if (args.Length >= 7 &&
    string.Equals(args[0], "--live-alloha-source-probe", StringComparison.OrdinalIgnoreCase))
{
    var source = new YummyAllohaSource
    {
        MovieToken = args[1],
        RequestToken = args[2],
        TranslationId = int.Parse(args[3]),
        SeasonNumber = int.Parse(args[4]),
        EpisodeNumber = int.Parse(args[5]),
        RefererUrl = args[6],
        Hidden = args.Length >= 8 ? args[7] : string.Empty
    };

    var preferredVoiceName = args.Length >= 9 ? args[8] : null;
    await RunLiveAllohaProbeForSource(source, preferredVoiceName);
    return;
}

var tests = new (string Name, Action Run)[]
{
    ("KodikClient_GetAnimeInfoAsync_TracksExplicitEpisodeCoverageFromSearch", KodikClient_GetAnimeInfoAsync_TracksExplicitEpisodeCoverageFromSearch),
    ("KodikClient_GetEpisodeTimingsAsync_UsesEpisodeLevelSearchLink", KodikClient_GetEpisodeTimingsAsync_UsesEpisodeLevelSearchLink),
    ("KodikClient_GetEpisodeLinkAsync_FallsBackToSeasonLinkWhenEpisodeLinkIsBroken", KodikClient_GetEpisodeLinkAsync_FallsBackToSeasonLinkWhenEpisodeLinkIsBroken),
    ("KodikClient_GetAnimeInfoAsync_FallsBackToHtmlFindPlayerResponse", KodikClient_GetAnimeInfoAsync_FallsBackToHtmlFindPlayerResponse),
    ("NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes", NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes),
    ("NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist", NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist),
    ("LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount", LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount),
    ("GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags", GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags),
    ("LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags", LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags),
    ("LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown", LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown),
    ("ResolveSeasonNumber_UsesViewingOrderIndex", ResolveSeasonNumber_UsesViewingOrderIndex),
    ("ResolveSeasonNumber_IgnoresCalendarSeasonField", ResolveSeasonNumber_IgnoresCalendarSeasonField),
    ("ResolveSeasonNumber_ParsesSeasonWithPartSuffix", ResolveSeasonNumber_ParsesSeasonWithPartSuffix),
    ("ResolveSeasonNumber_PrefersExplicitSeasonSuffixOverViewingOrderIndex", ResolveSeasonNumber_PrefersExplicitSeasonSuffixOverViewingOrderIndex),
    ("ResolveSeasonNumber_UsesShikimoriLayoutFallback", ResolveSeasonNumber_UsesShikimoriLayoutFallback),
    ("ResolveSeasonNumber_DoesNotTreatSpecialAsSecondSeason", ResolveSeasonNumber_DoesNotTreatSpecialAsSecondSeason),
    ("ResolveSeriesTitle_UsesViewingOrderBaseTitle", ResolveSeriesTitle_UsesViewingOrderBaseTitle),
    ("ResolveSeriesTitle_UsesViewingOrderBaseTitleForSpecial", ResolveSeriesTitle_UsesViewingOrderBaseTitleForSpecial),
    ("ResolveSeriesTitle_StripsTrailingSeasonSuffix", ResolveSeriesTitle_StripsTrailingSeasonSuffix),
    ("ResolveSeriesTitle_StripsSeasonWithPartSuffix", ResolveSeriesTitle_StripsSeasonWithPartSuffix),
    ("ResolveSeriesTitle_PrefersStrippedExplicitSeasonSuffixOverViewingOrderBaseTitle", ResolveSeriesTitle_PrefersStrippedExplicitSeasonSuffixOverViewingOrderBaseTitle),
    ("ResolveSeriesTitle_UsesShikimoriLayoutFallback", ResolveSeriesTitle_UsesShikimoriLayoutFallback),
    ("ShouldCreateSeasonDirectory_ForAnnouncedSecondSeason", ShouldCreateSeasonDirectory_ForAnnouncedSecondSeason),
    ("ShikimoriSeriesLayoutResolver_TreatsPartAsSameSeason", ShikimoriSeriesLayoutResolver_TreatsPartAsSameSeason),
    ("ShikimoriSeriesLayoutResolver_DoesNotTreatSpecialAsNewSeason", ShikimoriSeriesLayoutResolver_DoesNotTreatSpecialAsNewSeason),
    ("NfoBuilder_BuildSeriesNfo_ProducesXml", NfoBuilder_BuildSeriesNfo_ProducesXml),
    ("NfoBuilder_BuildEpisodeNfo_ProducesXml", NfoBuilder_BuildEpisodeNfo_ProducesXml),
    ("RewriteEpisodeFileSeasonPrefix_UsesSeasonZeroForSpecials", RewriteEpisodeFileSeasonPrefix_UsesSeasonZeroForSpecials),
    ("BuildSeriesFolderName_UsesProviderCompatibleShikimoriTag", BuildSeriesFolderName_UsesProviderCompatibleShikimoriTag),
    ("PrepareSeasonDirectory_MovesSeasonOneArtifactsOutOfMistakenSeasonTwoFolder", PrepareSeasonDirectory_MovesSeasonOneArtifactsOutOfMistakenSeasonTwoFolder),
    ("PrepareSeasonDirectory_MovesSeasonTwoArtifactsOutOfLegacySeasonOneFolder", PrepareSeasonDirectory_MovesSeasonTwoArtifactsOutOfLegacySeasonOneFolder),
    ("PrepareSeasonDirectory_DoesNotMoveActualSeasonOneArtifactsWhenPreparingSeasonTwo", PrepareSeasonDirectory_DoesNotMoveActualSeasonOneArtifactsWhenPreparingSeasonTwo),
    ("PrepareSeasonDirectory_MovesSpecialArtifactsIntoSeasonZeroFolder", PrepareSeasonDirectory_MovesSpecialArtifactsIntoSeasonZeroFolder),
    ("YummyVideoCatalog_ParsesCvhProviders", YummyVideoCatalog_ParsesCvhProviders),
    ("YummyVideoCatalog_DecodesCvhDubbingCodePlusAsSpace", YummyVideoCatalog_DecodesCvhDubbingCodePlusAsSpace),
    ("CvhClient_AddsBrowserHeadersAndParsesResponses", CvhClient_AddsBrowserHeadersAndParsesResponses),
    ("CvhClient_PrefersDubbingNameOverNumericDubbingCode", CvhClient_PrefersDubbingNameOverNumericDubbingCode),
    ("CvhClient_MatchesEquivalentVoiceKeys", CvhClient_MatchesEquivalentVoiceKeys),
    ("CvhClient_DoesNotFallbackToDifferentVoiceWhenPreferredVoiceIsMissing", CvhClient_DoesNotFallbackToDifferentVoiceWhenPreferredVoiceIsMissing),
    ("CvhClient_ThrowsMeaningfulErrorOnEmptyPlaylist", CvhClient_ThrowsMeaningfulErrorOnEmptyPlaylist),
    ("CvhClient_DownloadManifestAddsHeadersAndRewritesUrls", CvhClient_DownloadManifestAddsHeadersAndRewritesUrls),
    ("CvhClient_BuildManifestResponseBody_ProxiesNestedPlaylists", CvhClient_BuildManifestResponseBody_ProxiesNestedPlaylists),
    ("YummyVideoCatalog_ParsesAllohaProviders", YummyVideoCatalog_ParsesAllohaProviders),
    ("AllohaApiClient_ParsesSerialCatalogEntries", AllohaApiClient_ParsesSerialCatalogEntries),
    ("AllohaApiClient_ParsesEpisodesArrayCatalogEntries", AllohaApiClient_ParsesEpisodesArrayCatalogEntries),
    ("AllohaApiCatalogLoader_FilterEntriesForSeason_KeepsOnlyRequestedSeason", AllohaApiCatalogLoader_FilterEntriesForSeason_KeepsOnlyRequestedSeason),
    ("AllohaApiCatalogLoader_LoadEntriesAsync_CachesSuccessfulCatalogLoads", AllohaApiCatalogLoader_LoadEntriesAsync_CachesSuccessfulCatalogLoads),
    ("AllohaApiCatalogLoader_LoadEntriesAsync_DeduplicatesConcurrentCatalogLoads", AllohaApiCatalogLoader_LoadEntriesAsync_DeduplicatesConcurrentCatalogLoads),
    ("AllohaApiCatalogLoader_LoadEntriesAsync_CachesFailuresBriefly", AllohaApiCatalogLoader_LoadEntriesAsync_CachesFailuresBriefly),
    ("YummyVideoCatalog_MergesAdditionalAllohaEntriesWithoutOverwritingYummyData", YummyVideoCatalog_MergesAdditionalAllohaEntriesWithoutOverwritingYummyData),
    ("YummyVideoCatalog_KeepsAllohaEntriesFromDifferentSeasonsDistinct", YummyVideoCatalog_KeepsAllohaEntriesFromDifferentSeasonsDistinct),
    ("YummyVideoCatalog_CombinesCoverageAcrossProviders", YummyVideoCatalog_CombinesCoverageAcrossProviders),
    ("TranslationNameKeyNormalizer_UsesCuratedVoiceAliasGroups", TranslationNameKeyNormalizer_UsesCuratedVoiceAliasGroups),
    ("YummyVideoCatalog_MatchesEquivalentVoiceNames", YummyVideoCatalog_MatchesEquivalentVoiceNames),
    ("YummyVideoCatalog_MatchesCrossProviderVoiceAliases", YummyVideoCatalog_MatchesCrossProviderVoiceAliases),
    ("YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_FallsBackToOtherProvider", YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_FallsBackToOtherProvider),
    ("YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_PrefersRequestedProvider", YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_PrefersRequestedProvider),
    ("ResolveKodikAvailableEpisodeCount_UsesYummyHintWhenSeriesCountIsZero", ResolveKodikAvailableEpisodeCount_UsesYummyHintWhenSeriesCountIsZero),
    ("GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode", GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode),
    ("EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants", EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants),
    ("ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName", ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName),
    ("KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo", KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo),
    ("CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage", CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage),
    ("AllohaPlaybackService_BuildsExpectedBorthSuffix", AllohaPlaybackService_BuildsExpectedBorthSuffix),
    ("AllohaPlaybackService_CreatesSessionViaIframeAndBnsi", AllohaPlaybackService_CreatesSessionViaIframeAndBnsi),
    ("AllohaPlaybackService_UsesIframeOriginForMirroredHost", AllohaPlaybackService_UsesIframeOriginForMirroredHost),
    ("AllohaPlaybackService_PrefersRequestedVoiceWhenBnsiReturnsMultipleTracks", AllohaPlaybackService_PrefersRequestedVoiceWhenBnsiReturnsMultipleTracks),
    ("AllohaPlaybackService_UsesAlternateVoiceFieldWhenLabelIsOpaque", AllohaPlaybackService_UsesAlternateVoiceFieldWhenLabelIsOpaque),
    ("AllohaPlaybackService_MatchesShortAnilibAliasToAnilibria", AllohaPlaybackService_MatchesShortAnilibAliasToAnilibria),
    ("YummyKodikStreamController_AllowsSingleOpaqueAllohaTrackMarker", YummyKodikStreamController_AllowsSingleOpaqueAllohaTrackMarker),
    ("YummyKodikStreamController_RejectsSingleGenericRussianAllohaTrackMarker", YummyKodikStreamController_RejectsSingleGenericRussianAllohaTrackMarker),
    ("YummyKodikStreamController_RejectsMultipleOpaqueAllohaTrackMarkers", YummyKodikStreamController_RejectsMultipleOpaqueAllohaTrackMarkers),
    ("YummyKodikStreamController_OrdersYummyFallbackProviders", YummyKodikStreamController_OrdersYummyFallbackProviders),
    ("YummyKodikStreamController_FindsKodikFallbackVoiceByAlias", YummyKodikStreamController_FindsKodikFallbackVoiceByAlias),
    ("AllohaPlaybackService_RewritesManifestUrisToProxyUrls", AllohaPlaybackService_RewritesManifestUrisToProxyUrls),
    ("AllohaPlaybackService_DownloadProxyResourceRewritesNestedManifest", AllohaPlaybackService_DownloadProxyResourceRewritesNestedManifest),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSessionAfter403", AllohaPlaybackService_DownloadProxyResourceRefreshesSessionAfter403),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter403", AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter403),
    ("AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfterAlloha500", AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfterAlloha500),
    ("YummyKodikStreamUri_ParsesCvhRequest", YummyKodikStreamUri_ParsesCvhRequest),
    ("YummyKodikStreamUri_ParsesAllohaRequest", YummyKodikStreamUri_ParsesAllohaRequest),
    ("YummyKodikStreamUri_BuildsAllohaRequestWithEmbeddedSource", YummyKodikStreamUri_BuildsAllohaRequestWithEmbeddedSource),
    ("YummyKodikStreamUri_TrimsTrailingSlashFromProviderBaseUrl", YummyKodikStreamUri_TrimsTrailingSlashFromProviderBaseUrl),
    ("JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose", JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose),
    ("JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap", JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap),
    ("JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap", JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap)
};

var passed = 0;

foreach (var (name, run) in tests)
{
    run();
    Console.WriteLine($"PASS {name}");
    passed++;
}

Console.WriteLine($"Passed {passed}/{tests.Length} tests.");

static async Task RunLiveAllohaProbe()
{
    var source = new YummyAllohaSource
    {
        MovieToken = "9846b7afc8843cd0da8151b9cb4b10",
        RequestToken = "8b5512267a2a52e9de06d67d342e0c",
        TranslationId = 222,
        SeasonNumber = 1,
        EpisodeNumber = 3,
        Hidden = "translation,season,episode",
        RefererUrl = "https://alloha.yani.tv/?token_movie=9846b7afc8843cd0da8151b9cb4b10&translation=222&season=1&episode=3&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode"
    };

    await RunLiveAllohaProbeForSource(source, "РуАниме / DEEP");
}

static async Task RunLiveAllohaProbeForSource(
    YummyAllohaSource source,
    string? preferredVoiceName)
{
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";

    Console.WriteLine("LIVE ALLOHA PROBE");
    Console.WriteLine("source=" + source.RefererUrl);

    var logger = NullLogger<AllohaPlaybackService>.Instance;
    var resolverFactory = typeof(AllohaPlaybackService).GetMethod(
        "CreateDefaultStreamTokenResolver",
        BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(resolverFactory is not null, "Live probe should be able to create the default stream token resolver.");

    var resolver = resolverFactory!.Invoke(null, new object[] { logger })!;

    using var http = new HttpClient(new RecordingPassThroughHandler(
        new HttpClientHandler(),
        message => Console.WriteLine("HTTP " + message)));

    var ctor = typeof(AllohaPlaybackService)
        .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
        .SingleOrDefault(x => x.GetParameters().Length == 3);
    AssertTrue(ctor is not null, "Live probe should be able to create the internal Alloha playback service.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { logger, http, resolver });
    await PrintLiveAllohaSourceMetadata(service, source);

    var session = await service.CreateSessionAsync(
        source,
        preferredQuality: 1080,
        preferredVoiceName,
        CancellationToken.None);

    Console.WriteLine("manifestUrl=" + session.ManifestUrl);
    Console.WriteLine("selectedVoice=" + session.SelectedVoiceName);
    Console.WriteLine("availableVoices=" + string.Join(", ", session.AvailableVoiceNames));
    Console.WriteLine("audioTrackId=" + session.AudioTrackId);
    Console.WriteLine("streamTokenReady=" + (!string.IsNullOrWhiteSpace(session.StreamToken)));

    var masterManifest = AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);
    Console.WriteLine("masterManifest:");
    Console.WriteLine(masterManifest);
    Console.WriteLine("masterProxyResourceCount=" + session.ProxyResources.Count);

    var initialNestedResources = session.ProxyResources
        .Where(x => x.Value.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Value, StringComparer.Ordinal)
        .ToArray();

    foreach (var resource in initialNestedResources)
    {
        Console.WriteLine("nestedPlaylist:id=" + resource.Key + " url=" + resource.Value);
        try
        {
            var nested = await service.DownloadProxyResourceAsync(
                session,
                resource.Key,
                resource.Value,
                proxyBaseUrl,
                CancellationToken.None);

            Console.WriteLine("nestedPlaylist:contentType=" + nested.ContentType);
            Console.WriteLine(Encoding.UTF8.GetString(nested.Content));
        }
        catch (Exception ex)
        {
            Console.WriteLine("nestedPlaylist:FAILED");
            Console.WriteLine(ex.ToString());
            throw;
        }
    }

    var segmentResources = session.ProxyResources
        .Where(x =>
            x.Value.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
            x.Value.EndsWith(".ts", StringComparison.OrdinalIgnoreCase) ||
            x.Value.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        .OrderBy(x => x.Value, StringComparer.Ordinal)
        .Take(4)
        .ToArray();

    Console.WriteLine("segmentProbeCount=" + segmentResources.Length);
    foreach (var resource in segmentResources)
    {
        Console.WriteLine("segment:id=" + resource.Key + " url=" + resource.Value);
        try
        {
            var segment = await service.DownloadProxyResourceAsync(
                session,
                resource.Key,
                resource.Value,
                proxyBaseUrl,
                CancellationToken.None);

            Console.WriteLine("segment:contentType=" + segment.ContentType + " bytes=" + segment.Content.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine("segment:FAILED");
            Console.WriteLine(ex.ToString());
            throw;
        }
    }

    Console.WriteLine("LIVE ALLOHA PROBE OK");
}

static async Task PrintLiveAllohaSourceMetadata(AllohaPlaybackService service, YummyAllohaSource source)
{
    try
    {
        var serviceType = typeof(AllohaPlaybackService);
        var iframeUrl = InvokeStatic<string>(serviceType, "BuildIframeRequestUrl", source.RefererUrl);
        var iframeOrigin = InvokeStatic<string>(serviceType, "ResolveIframeOrigin", iframeUrl, source.RefererUrl);
        var iframeHtml = await InvokeInstanceTask<string>(
            service,
            "DownloadIframeHtmlAsync",
            iframeUrl,
            CancellationToken.None);
        var bootstrap = InvokeStatic<object>(serviceType, "ParseBootstrapPayload", iframeHtml, source);
        var viewporti = GetProperty<string>(bootstrap, "Viewporti");
        var fileId = GetProperty<long>(bootstrap, "FileId");
        var borth = InvokeStatic<string>(serviceType, "BuildBorthHeader", viewporti);
        var playlist = await InvokeInstanceTask<object>(
            service,
            "RequestBnsiPayloadAsync",
            source,
            iframeOrigin,
            fileId,
            borth,
            CancellationToken.None);

        Console.WriteLine("iframeUrl=" + iframeUrl);
        Console.WriteLine("iframeOrigin=" + iframeOrigin);
        Console.WriteLine("fileId=" + fileId);

        var voices = GetEnumerableProperty(playlist, "AvailableVoiceNames")
            .Select(x => x?.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        Console.WriteLine("bnsiAvailableVoices=" + string.Join(", ", voices));

        var candidates = GetEnumerableProperty(playlist, "ManifestCandidates")
            .Where(x => x is not null)
            .Cast<object>()
            .ToArray();
        Console.WriteLine("bnsiCandidateCount=" + candidates.Length);
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            Console.WriteLine(
                $"bnsiCandidate[{i}]: quality={GetProperty<object?>(candidate, "Quality")} " +
                $"audioTrackId={GetProperty<string>(candidate, "AudioTrackId")} " +
                $"voice={GetProperty<string>(candidate, "VoiceName")} " +
                $"url={TrimForProbe(GetProperty<string>(candidate, "Url"), 180)}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("bnsiProbe:FAILED");
        Console.WriteLine(ex.ToString());
        throw;
    }
}

static T InvokeStatic<T>(Type type, string methodName, params object?[] args)
{
    var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
    AssertTrue(method is not null, methodName + " should exist.");
    return (T)method!.Invoke(null, args)!;
}

static async Task<T> InvokeInstanceTask<T>(object instance, string methodName, params object?[] args)
{
    var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
    AssertTrue(method is not null, methodName + " should exist.");
    var task = (Task)method!.Invoke(instance, args)!;
    await task.ConfigureAwait(false);
    return (T)task.GetType().GetProperty("Result")!.GetValue(task)!;
}

static T GetProperty<T>(object instance, string propertyName)
{
    var property = instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    AssertTrue(property is not null, propertyName + " should exist.");
    return (T)property!.GetValue(instance)!;
}

static IEnumerable<object?> GetEnumerableProperty(object instance, string propertyName)
{
    var value = GetProperty<object?>(instance, propertyName);
    if (value is not System.Collections.IEnumerable enumerable)
    {
        yield break;
    }

    foreach (var item in enumerable)
    {
        yield return item;
    }
}

static string TrimForProbe(string? value, int maxLength)
{
    var normalized = (value ?? string.Empty).Trim();
    if (normalized.Length <= maxLength)
    {
        return normalized;
    }

    return normalized.Substring(0, maxLength) + "...";
}

static void ResolveSeasonNumber_UsesViewingOrderIndex()
{
    var anime = BuildSecondSeasonAnime();

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(2, season, "Season must come from viewing_order for sequels.");
}

static void LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 1,
            Count = 12
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            }
        }
    };

    var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, new[] { 1 });

    AssertEqual(1, limited.Length, "Provider coverage should be capped to the aired episode count.");
    AssertEqual(1, limited[0], "Only the first aired episode should remain available.");
}

static void GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 1,
            Count = 0
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            },
            new()
            {
                Number = "2"
            }
        }
    };

    var expected = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime);

    AssertEqual(2, expected, "Explicit video episode numbers should override stale aired metadata when providers are already ahead.");
}

static void LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 1,
            Count = 0
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            }
        }
    };

    var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, new[] { 1, 2 });

    AssertEqual(2, limited.Length, "Provider-reported episode coverage should be preserved when aired lags behind live data.");
    AssertEqual(1, limited[0], "Episode ordering should stay normalized.");
    AssertEqual(2, limited[1], "The second explicitly supported episode should not be cut off by stale aired metadata.");
}

static void LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown()
{
    var anime = new YummyAnimeResponse
    {
        Episodes = new YummyEpisodesInfo
        {
            Aired = 0,
            Count = 0
        },
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1"
            }
        }
    };

    var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, new[] { 3, 1, 2, 2 });

    AssertEqual(3, limited.Length, "When Yummy does not know aired count, existing provider episodes should be preserved.");
    AssertEqual(1, limited[0], "Episodes should be normalized and sorted.");
    AssertEqual(2, limited[1], "Duplicate provider episodes should be removed.");
    AssertEqual(3, limited[2], "Episode ordering should remain stable after normalization.");
}

static void ResolveSeasonNumber_IgnoresCalendarSeasonField()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Похоже, сильнейшая профессия — это не герой или мудрец, а (временный) инспектор?",
        AnimeId = 24394,
        AnimeUrl = "pohozhe-silneyshaya-professiya-eto-ne-geroy-ili-mudrec-a-vremennyy-inspektor",
        Season = 2
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(1, season, "Calendar season field must not be treated as a sequel season number.");
}

static void ResolveSeasonNumber_ParsesSeasonWithPartSuffix()
{
    var anime = new YummyAnimeResponse
    {
        Title = "О моём перерождении в слизь 2. Часть 2"
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(2, season, "Season parser should keep the numbered season even when the title has a part suffix.");
}

static void ResolveSeasonNumber_PrefersExplicitSeasonSuffixOverViewingOrderIndex()
{
    var anime = BuildSlimeFourthSeasonAnime();

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(4, season, "Explicit numeric sequel suffix should win over viewing_order position.");
}

static void ResolveSeasonNumber_UsesShikimoriLayoutFallback()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Атака титанов: Финал"
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(
        anime,
        anime.Title,
        new ShikimoriSeriesLayoutInfo
        {
            SeasonNumber = 4,
            BaseTitle = "Атака титанов"
        });

    AssertEqual(4, season, "Shikimori fallback should provide sequel season numbers when the title has no explicit number.");
}

static void ResolveSeasonNumber_DoesNotTreatSpecialAsSecondSeason()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
        AnimeId = 11307,
        AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
        Type = new YummyAnimeType
        {
            Alias = "special"
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 10350,
                AnimeUrl = "chiterskiy-navyk-iz-drugogo-mira",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
                Data = new YummyViewingOrderData
                {
                    Index = 0
                }
            },
            new()
            {
                AnimeId = 11307,
                AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
                Data = new YummyViewingOrderData
                {
                    Index = 1
                }
            }
        }
    };

    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
    AssertEqual(0, season, "Jellyfin specials should use season zero instead of becoming regular seasons.");
}

static void ResolveSeriesTitle_UsesViewingOrderBaseTitle()
{
    var anime = BuildSecondSeasonAnime();
    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, season);
    AssertEqual("Фермерская жизнь в ином мире", title, "Base title should come from viewing_order for sequels.");
}

static void ResolveSeriesTitle_StripsTrailingSeasonSuffix()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Тестовый сериал 3",
        Season = 3
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, 3);
    AssertEqual("Тестовый сериал", title, "Trailing season number should be removed from series title.");
}

static void ResolveSeriesTitle_StripsSeasonWithPartSuffix()
{
    var anime = new YummyAnimeResponse
    {
        Title = "О моём перерождении в слизь 2. Часть 2"
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, 2);
    AssertEqual("О моём перерождении в слизь", title, "Season title stripping should keep the franchise base title for split cours.");
}

static void ResolveSeriesTitle_PrefersStrippedExplicitSeasonSuffixOverViewingOrderBaseTitle()
{
    var anime = BuildSlimeFourthSeasonAnime();
    var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, season);
    AssertEqual("О моём перерождении в слизь", title, "Explicit season suffix should be stripped before viewing_order root picks a recap title.");
}

static void ResolveSeriesTitle_UsesShikimoriLayoutFallback()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Атака титанов: Финал"
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(
        anime,
        anime.Title,
        seasonNumber: 4,
        shikimoriLayout: new ShikimoriSeriesLayoutInfo
        {
            SeasonNumber = 4,
            BaseTitle = "Атака титанов"
        });

    AssertEqual("Атака титанов", title, "Shikimori fallback should provide the root franchise title when the sequel title has no explicit numeric suffix.");
}

static void ResolveSeriesTitle_UsesViewingOrderBaseTitleForSpecial()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
        AnimeId = 11307,
        AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
        Type = new YummyAnimeType
        {
            Alias = "special"
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 10350,
                AnimeUrl = "chiterskiy-navyk-iz-drugogo-mira",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
                Data = new YummyViewingOrderData
                {
                    Index = 0
                }
            },
            new()
            {
                AnimeId = 11307,
                AnimeUrl = "isekai-de-cheat-skill-wo-te-ni-shita-ore-wa-genjitsu-sekai-wo-mo-musou-suru-level-up-wa-jinsei-wo-kaeta-shin-anime",
                Title = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
                Data = new YummyViewingOrderData
                {
                    Index = 1
                }
            }
        }
    };

    var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, anime.Title, seasonNumber: 0);
    AssertEqual(
        "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
        title,
        "Specials should still reuse the base franchise title for the series folder.");
}

static void ShouldCreateSeasonDirectory_ForAnnouncedSecondSeason()
{
    var anime = BuildSecondSeasonAnime();
    var shouldCreate = YummySeriesLayoutResolver.ShouldCreateSeasonDirectory(anime, 2);
    AssertTrue(shouldCreate, "Second-season TV announcements must still get a Season folder.");
}

static void ShikimoriSeriesLayoutResolver_TreatsPartAsSameSeason()
{
    var layout = ShikimoriSeriesLayoutResolver.BuildFromMainlineChain(
        new[]
        {
            new ShikimoriSeriesLayoutNode
            {
                Id = 37430,
                RussianTitle = "О моём перерождении в слизь",
                Name = "Tensei shitara Slime Datta Ken",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 39551,
                RussianTitle = "О моём перерождении в слизь 2",
                Name = "Tensei shitara Slime Datta Ken 2nd Season",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 41487,
                RussianTitle = "О моём перерождении в слизь 2. Часть 2",
                Name = "Tensei shitara Slime Datta Ken 2nd Season Part 2",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 53580,
                RussianTitle = "О моём перерождении в слизь 3",
                Name = "Tensei shitara Slime Datta Ken 3rd Season",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 59970,
                RussianTitle = "О моём перерождении в слизь 4",
                Name = "Tensei shitara Slime Datta Ken 4th Season",
                Kind = "tv"
            }
        });

    AssertEqual("О моём перерождении в слизь", layout?.BaseTitle ?? string.Empty, "Root franchise title should come from the earliest mainline entry.");
    AssertEqual(4, layout?.SeasonNumber ?? 0, "Split-cour TV entries should not bump the sequel season number.");
}

static void ShikimoriSeriesLayoutResolver_DoesNotTreatSpecialAsNewSeason()
{
    var layout = ShikimoriSeriesLayoutResolver.BuildFromMainlineChain(
        new[]
        {
            new ShikimoriSeriesLayoutNode
            {
                Id = 52830,
                RussianTitle = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь",
                Name = "Isekai de Cheat Skill wo Te ni Shita Ore wa, Genjitsu Sekai wo mo Musou Suru: Level Up wa Jinsei wo Kaeta",
                Kind = "tv"
            },
            new ShikimoriSeriesLayoutNode
            {
                Id = 56906,
                RussianTitle = "Я получил читерские способности в другом мире и стал экстраординарным в реальном мире: История о том, как повышение уровня изменило мою жизнь — Спецвыпуск",
                Name = "Isekai de Cheat Skill wo Te ni Shita Ore wa, Genjitsu Sekai wo mo Musou Suru: Level Up wa Jinsei wo Kaeta (TV Special)",
                Kind = "special"
            }
        });

    AssertEqual(1, layout?.SeasonNumber ?? 0, "A special should keep the mainline season number instead of becoming a fake season two.");
}

static void NfoBuilder_BuildSeriesNfo_ProducesXml()
{
    var xml = NfoBuilder.BuildSeriesNfo("Test series", "Plot");

    AssertTrue(!string.IsNullOrWhiteSpace(xml), "Series NFO should not be empty.");
    AssertTrue(xml.Contains("<tvshow>", StringComparison.Ordinal), "Series NFO should contain the tvshow root element.");
    AssertTrue(xml.Contains("<title>Test series</title>", StringComparison.Ordinal), "Series NFO should contain the title.");
}

static void NfoBuilder_BuildEpisodeNfo_ProducesXml()
{
    var xml = NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 2, seriesTitle: "Test series", description: "Plot");

    AssertTrue(!string.IsNullOrWhiteSpace(xml), "Episode NFO should not be empty.");
    AssertTrue(xml.Contains("<episodedetails>", StringComparison.Ordinal), "Episode NFO should contain the episodedetails root element.");
    AssertTrue(xml.Contains("<season>2</season>", StringComparison.Ordinal), "Episode NFO should contain the requested season number.");
}

static void RewriteEpisodeFileSeasonPrefix_UsesSeasonZeroForSpecials()
{
    var helperType = typeof(NfoBuilder).Assembly.GetType("YummyKodik.Tasks.SeasonDirectoryMaintenance");
    AssertTrue(helperType != null, "SeasonDirectoryMaintenance helper should exist for season-zero regression coverage.");

    var method = helperType!.GetMethod(
        "RewriteEpisodeFileSeasonPrefix",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Season prefix rewrite helper should be reachable via reflection.");

    var rewritten = (string)method!.Invoke(null, new object[] { "S01E01 - Special", 0 })!;
    AssertEqual("S00E01 - Special", rewritten, "Jellyfin specials should keep the season zero prefix in generated filenames.");
}

static void BuildSeriesFolderName_UsesProviderCompatibleShikimoriTag()
{
    var anime = BuildSecondSeasonAnime();
    var value = YummyProviderTagFormatter.BuildBestIdTag(anime);
    AssertEqual("[shikimori-62146]", value, "Folder tag should match the Shikimori provider filename format.");
}

static void PrepareSeasonDirectory_MovesSeasonOneArtifactsOutOfMistakenSeasonTwoFolder()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var mistakenSeasonDir = Path.Combine(seriesRoot, "Season 02");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 01");

    try
    {
        Directory.CreateDirectory(mistakenSeasonDir);
        File.WriteAllText(Path.Combine(mistakenSeasonDir, "S02E01.strm"), "https://example.test/season1/ep1");
        File.WriteAllText(
            Path.Combine(mistakenSeasonDir, "S02E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 1, seriesTitle: "Test", description: "desc"));

        var preparedDir = InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 1);

        AssertEqual(targetSeasonDir, preparedDir, "Season preparation should return the canonical season one directory.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S01E01.strm")), "Season one STRM should be moved into Season 01.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S01E01.nfo")), "Season one NFO should be moved into Season 01.");
        AssertFalse(Directory.Exists(mistakenSeasonDir), "Mistaken Season 02 directory should be removed once empty.");

        var nfo = File.ReadAllText(Path.Combine(targetSeasonDir, "S01E01.nfo"));
        AssertTrue(nfo.Contains("<season>1</season>", StringComparison.Ordinal), "Moved NFO should keep season one metadata.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void PrepareSeasonDirectory_MovesSeasonTwoArtifactsOutOfLegacySeasonOneFolder()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 02");

    try
    {
        Directory.CreateDirectory(legacySeasonDir);
        File.WriteAllText(Path.Combine(legacySeasonDir, "S01E01.strm"), "https://example.test/season2/ep1");
        File.WriteAllText(
            Path.Combine(legacySeasonDir, "S01E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 2, seriesTitle: "Test", description: "desc"));

        var preparedDir = InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 2);

        AssertEqual(targetSeasonDir, preparedDir, "Season preparation should return the canonical sequel season directory.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S02E01.strm")), "Season two STRM should be moved into Season 02.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S02E01.nfo")), "Season two NFO should be moved into Season 02.");
        AssertFalse(Directory.Exists(legacySeasonDir), "Legacy Season 01 directory should be removed once the sequel artifacts are moved out.");

        var nfo = File.ReadAllText(Path.Combine(targetSeasonDir, "S02E01.nfo"));
        AssertTrue(nfo.Contains("<season>2</season>", StringComparison.Ordinal), "Moved NFO should keep sequel metadata.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void PrepareSeasonDirectory_DoesNotMoveActualSeasonOneArtifactsWhenPreparingSeasonTwo()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var seasonOneDir = Path.Combine(seriesRoot, "Season 01");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 02");

    try
    {
        Directory.CreateDirectory(seasonOneDir);
        File.WriteAllText(Path.Combine(seasonOneDir, "S01E01.strm"), "https://example.test/season1/ep1");
        File.WriteAllText(
            Path.Combine(seasonOneDir, "S01E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 1, seriesTitle: "Test", description: "desc"));

        InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 2);

        AssertTrue(File.Exists(Path.Combine(seasonOneDir, "S01E01.strm")), "Preparing season two must not steal real season one STRM files.");
        AssertTrue(File.Exists(Path.Combine(seasonOneDir, "S01E01.nfo")), "Preparing season two must not steal real season one NFO files.");
        AssertFalse(File.Exists(Path.Combine(targetSeasonDir, "S02E01.strm")), "Season two folder should stay empty when the source artifacts belong to season one.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void PrepareSeasonDirectory_MovesSpecialArtifactsIntoSeasonZeroFolder()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seriesRoot = Path.Combine(tempRoot, "Series");
    var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");
    var targetSeasonDir = Path.Combine(seriesRoot, "Season 00");

    try
    {
        Directory.CreateDirectory(legacySeasonDir);
        File.WriteAllText(Path.Combine(legacySeasonDir, "S01E01.strm"), "https://example.test/special/ep1");
        File.WriteAllText(
            Path.Combine(legacySeasonDir, "S01E01.nfo"),
            NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 1, seriesTitle: "Test", description: "desc"));

        var preparedDir = InvokePrepareSeasonDirectory(seriesRoot, targetSeasonDir, seasonNumber: 0);

        AssertEqual(targetSeasonDir, preparedDir, "Special preparation should return the Season 00 directory.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S00E01.strm")), "Special STRM should move into Season 00.");
        AssertTrue(File.Exists(Path.Combine(targetSeasonDir, "S00E01.nfo")), "Special NFO should move into Season 00.");
        AssertFalse(Directory.Exists(legacySeasonDir), "Legacy Season 01 special directory should be removed once empty.");

        var nfo = File.ReadAllText(Path.Combine(targetSeasonDir, "S00E01.nfo"));
        AssertTrue(nfo.Contains("<season>0</season>", StringComparison.Ordinal), "Moved special NFO should be rewritten to season zero.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void YummyVideoCatalog_ParsesCvhProviders()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 4861,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                Duration = 1080,
                IframeUrl = "https://play.example/player?anime_id=4861&episode=1&dubbing_code=158&dubbing=%D0%9E%D0%B7%D0%B2%D1%83%D1%87%D0%BA%D0%B0%20AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Озвучка AniStar"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 90,
                        Length = 60
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);

    AssertTrue(catalog.HasAnyCvhEpisodes, "CVH entries should be detected from Yummy videos.");
    AssertEqual(1, catalog.GetSupportedEpisodeNumbers().Single(), "Episode list should include the parsed CVH episode.");
    AssertEqual("AniStar", catalog.GetSupportedVoiceNames(1).Single(), "Voice name should be normalized.");
    AssertEqual(4861L, catalog.FindPreferredPlayableEntry(1, "AniStar")?.Cvh?.AnimeId ?? 0, "CVH source anime id should come from the iframe payload.");

    var chosenVoice = catalog.PickPreferredVoiceName(1, explicitVoiceName: string.Empty, savedVoiceName: "AniStar", preferredFilter: string.Empty, out var reason);
    AssertEqual("AniStar", chosenVoice, "Saved voice should be selected when available.");
    AssertEqual("saved", reason, "Reason should explain why the voice was picked.");
}

static void YummyVideoCatalog_DecodesCvhDubbingCodePlusAsSpace()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 62825,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "2",
                Duration = 1440,
                IframeUrl = "https://play.example/player?anime_id=62825&episode=2&dubbing_code=Dream+Cast&dubbing=%D0%9E%D0%B7%D0%B2%D1%83%D1%87%D0%BA%D0%B0+Dream+Cast",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Озвучка Dream Cast"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredPlayableEntry(2, "Dream Cast");

    AssertTrue(entry?.Cvh != null, "CVH source should be created for plus-encoded query parameters.");
    AssertEqual("Dream Cast", entry!.Cvh!.DubbingCode, "dubbing_code should be decoded with spaces.");
    AssertEqual("Dream Cast", entry.Cvh.DubbingName, "dubbing should stay normalized after query decoding.");
}

static void CvhClient_AddsBrowserHeadersAndParsesResponses()
{
    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "AniStar",
                          "episode": 11,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.RequestUri!.AbsoluteUri.EndsWith("/player/sv/video/12624919026235", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "duration": 1424,
                      "sources": {
                        "hlsUrl": "https://ok6-7.vkuser.net/video.m3u8"
                      }
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "AniStar",
        DubbingName = "AniStar"
    };

    var resolved = client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://ok6-7.vkuser.net/video.m3u8", resolved.StreamUrl, "CVH hls url should be returned from the video payload.");
    AssertEqual("AniStar", resolved.VoiceName, "Resolved voice should come from the matching playlist item.");
    AssertEqual(2, requests.Count, "CVH client should perform playlist and video requests.");

    foreach (var request in requests)
    {
        AssertEqual("https://ru.yummyani.me", request.Headers.GetValues("Origin").Single(), "CVH requests must carry the Yummy origin.");
        AssertTrue(request.Headers.Referrer is not null, "CVH requests must carry a Yummy iframe referer.");
        AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("anime_id=61549", StringComparison.Ordinal), "Referer should include the CVH anime id.");
        AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("episode=11", StringComparison.Ordinal), "Referer should include the requested episode.");
        AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("dubbing_code=AniStar", StringComparison.Ordinal), "Referer should include the dubbing code.");
    }
}

static void CvhClient_ThrowsMeaningfulErrorOnEmptyPlaylist()
{
    var handler = new DelegatingTestHandler(_ => new HttpResponseMessage(HttpStatusCode.NoContent)
    {
        Content = new StringContent(string.Empty)
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "AniStar",
        DubbingName = "AniStar"
    };

    var ex = AssertThrows<InvalidOperationException>(
        () => client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult(),
        "Empty CVH payloads should throw a meaningful upstream error.");

    AssertTrue(ex.Message.Contains("empty playlist animeId=61549 response", StringComparison.OrdinalIgnoreCase), "Exception should explain that CVH returned an empty playlist response.");
}

static void CvhClient_PrefersDubbingNameOverNumericDubbingCode()
{
    var handler = new DelegatingTestHandler(request =>
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "AniStar",
                          "episode": 11,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.RequestUri!.AbsoluteUri.EndsWith("/player/sv/video/12624919026235", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "duration": 1424,
                      "sources": {
                        "hlsUrl": "https://ok6-7.vkuser.net/video.m3u8"
                      }
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "158",
        DubbingName = "AniStar"
    };

    var resolved = client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("AniStar", resolved.VoiceName, "Human-readable dubbing name should be used to select the matching playlist item.");
}

static void CvhClient_MatchesEquivalentVoiceKeys()
{
    var handler = new DelegatingTestHandler(request =>
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "2х2",
                          "episode": 11,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.RequestUri!.AbsoluteUri.EndsWith("/player/sv/video/12624919026235", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "duration": 1424,
                      "sources": {
                        "hlsUrl": "https://ok6-7.vkuser.net/video.m3u8"
                      }
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61549,
        EpisodeNumber = 11,
        DubbingCode = "2x2",
        DubbingName = "2x2"
    };

    var resolved = client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("2х2", resolved.VoiceName, "Equivalent provider keys should match even when Yummy and upstream use different x/х forms.");
}

static void CvhClient_DoesNotFallbackToDifferentVoiceWhenPreferredVoiceIsMissing()
{
    var handler = new DelegatingTestHandler(request =>
    {
        if (request.RequestUri!.AbsoluteUri.Contains("/player/sv/playlist?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "items": [
                        {
                          "vkId": "12624919026235",
                          "voiceStudio": "AniLiberty",
                          "episode": 2,
                          "season": 1
                        }
                      ]
                    }
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected CVH test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 62825,
        EpisodeNumber = 2,
        DubbingCode = "Dream Cast",
        DubbingName = "Dream Cast"
    };

    var ex = AssertThrows<InvalidOperationException>(
        () => client.ResolveEpisodeStreamAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult(),
        "CVH should not silently fallback to another translation when the preferred voice is missing.");

    AssertTrue(ex.Message.Contains("CVH episode 2 is not available", StringComparison.OrdinalIgnoreCase), "Exception should explain that the requested episode/voice is unavailable.");
}

static void CvhClient_DownloadManifestAddsHeadersAndRewritesUrls()
{
    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                #EXTM3U
                #EXT-X-VERSION:3
                segment-001.ts
                https://cdn.example/segment-002.ts
                """)
        };
    });

    using var http = new HttpClient(handler);
    var client = new CvhClient(http);
    var source = new YummyCvhSource
    {
        AnimeId = 61207,
        EpisodeNumber = 1,
        DubbingCode = "AniStar",
        DubbingName = "AniStar"
    };

    var manifest = client.DownloadManifestAsync(
        "https://vd744.okcdn.ru/path/master.m3u8",
        source,
        CancellationToken.None).GetAwaiter().GetResult();

    AssertTrue(manifest.Contains("https://vd744.okcdn.ru/path/segment-001.ts", StringComparison.Ordinal), "Relative CVH segment urls should be rewritten to absolute urls.");
    AssertTrue(manifest.Contains("https://cdn.example/segment-002.ts", StringComparison.Ordinal), "Absolute CVH segment urls should be preserved.");
    AssertEqual(1, requests.Count, "CVH manifest download should perform a single HTTP request.");

    var request = requests.Single();
    AssertEqual("https://ru.yummyani.me", request.Headers.GetValues("Origin").Single(), "CVH manifest request must carry the Yummy origin.");
    AssertTrue(request.Headers.Referrer is not null, "CVH manifest request must carry a Yummy iframe referer.");
    AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("anime_id=61207", StringComparison.Ordinal), "Manifest referer should include the CVH anime id.");
    AssertTrue(request.Headers.Referrer!.AbsoluteUri.Contains("episode=1", StringComparison.Ordinal), "Manifest referer should include the requested episode.");
    AssertTrue(request.Headers.TryGetValues("Accept-Language", out var acceptLanguageValues), "CVH manifest request should include Accept-Language.");
    var acceptLanguage = string.Join(",", acceptLanguageValues ?? Array.Empty<string>());
    AssertTrue(acceptLanguage.Contains("ru-RU", StringComparison.Ordinal), "CVH manifest request should include the primary browser locale.");
    AssertTrue(acceptLanguage.Contains("en-US", StringComparison.Ordinal), "CVH manifest request should keep the browser-like locale fallback.");
    AssertTrue(request.Headers.UserAgent.ToString().Contains("Mozilla/5.0", StringComparison.Ordinal), "CVH manifest request should use a browser-like User-Agent.");
}

static void NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Анонс сериала",
        AnimeStatus = new YummyAnimeStatus
        {
            Alias = "anons",
            Title = "Анонс"
        },
        Episodes = new YummyEpisodesInfo
        {
            Count = 12,
            Aired = 0
        },
        Videos = new List<YummyVideoItem>()
    };

    var result = YummyEpisodeAvailability.NeedsKodikSupplement(anime, Array.Empty<int>());
    AssertFalse(result, "Announcements without aired/provider episodes should skip Kodik probing.");
}

static void NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist()
{
    var anime = new YummyAnimeResponse
    {
        Title = "Уже выходит",
        Episodes = new YummyEpisodesInfo
        {
            Count = 12,
            Aired = 3
        },
        Videos = new List<YummyVideoItem>()
    };

    var result = YummyEpisodeAvailability.NeedsKodikSupplement(anime, Array.Empty<int>());
    AssertTrue(result, "Titles with aired episodes should still probe Kodik when Yummy has not generated any files.");
}

static void KodikClient_GetAnimeInfoAsync_TracksExplicitEpisodeCoverageFromSearch()
{
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "https://kodikplayer.com/serial/1/test?season=1&episode=2",
              "last_season": 1,
              "episodes_count": 2,
              "last_episode": 2,
              "seasons": {
                "1": {
                  "episodes": {
                    "2": {
                      "link": "https://kodikplayer.com/seria/22/hash/720p"
                    }
                  }
                }
              },
              "translation": {
                "id": 610,
                "title": "AniLibria.TV",
                "type": "voice"
              }
            }
          ]
        }
        """;

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson)
            };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    var info = client.GetAnimeInfoAsync("59970", YummyKodik.Kodik.KodikIdType.Shikimori, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(2, info.SeriesCount, "Search response should keep the highest explicit episode number as the available series count.");
    AssertEqual(1, info.Translations.Count, "Search response should keep the translation entry.");
    AssertEqual("610", info.Translations[0].Id, "Translation id should come from the search response.");
    AssertEqual("2", string.Join(",", info.Translations[0].AvailableEpisodes), "Per-translation episode coverage should keep only explicitly reported episodes.");
    AssertFalse(info.Translations[0].CoversEpisode(1), "Translation should not claim episode one when Kodik only reports episode two.");
    AssertTrue(info.Translations[0].CoversEpisode(2), "Translation should cover the explicitly reported episode.");
}

static void KodikClient_GetEpisodeTimingsAsync_UsesEpisodeLevelSearchLink()
{
    const string episodePlayerUrl = "https://kodikplayer.com/seria/1592110/4dbef1171292a4620dfd5baab0c36311/720p";
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "//kodikplayer.com/serial/74203/a9a3259396843a9efba7e849922bbefb/720p",
              "last_season": 1,
              "episodes_count": 4,
              "last_episode": 4,
              "seasons": {
                "1": {
                  "episodes": {
                    "1": { "link": "//kodikplayer.com/seria/1589871/f89448a3c557915b4a69c647c9a2697f/720p" },
                    "2": { "link": "//kodikplayer.com/seria/1592110/4dbef1171292a4620dfd5baab0c36311/720p" },
                    "3": { "link": "//kodikplayer.com/seria/1593869/f738ffa1d917a449b7ef8494a465ba53/720p" },
                    "4": { "link": "//kodikplayer.com/seria/1596123/30b90fe73a99c3097c47c3ad9c2027f0/720p" }
                  }
                }
              },
              "translation": {
                "id": 609,
                "title": "AniDUB",
                "type": "voice"
              }
            }
          ]
        }
        """;

    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == episodePlayerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body></body></html>")
            };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    client.GetEpisodeTimingsAsync("63376", YummyKodik.Kodik.KodikIdType.Shikimori, 2, "609", CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(2, requests.Count, "Resolving Kodik timings should perform search and then fetch one player page.");
    AssertEqual(episodePlayerUrl, requests[1].RequestUri!.AbsoluteUri, "Kodik should use the episode-specific player link from search data instead of the generic serial link.");
}

static void KodikClient_GetEpisodeLinkAsync_FallsBackToSeasonLinkWhenEpisodeLinkIsBroken()
{
    const string episodePlayerUrl = "https://kodikplayer.com/seria/1599999/broken/720p";
    const string seasonPlayerUrl = "https://kodikplayer.com/season/117971/8af7e0f0ebf3aa3d14b230717a2fbc8e/720p?episode=5&season=1&first_url=false&min_age=16";
    const string scriptUrl = "https://kodikplayer.com/assets/app.player_single.js";
    const string searchJson =
        """
        {
          "results": [
            {
              "link": "//kodikplayer.com/season/117971/8af7e0f0ebf3aa3d14b230717a2fbc8e/720p",
              "last_season": 1,
              "episodes_count": 5,
              "last_episode": 5,
              "seasons": {
                "1": {
                  "episodes": {
                    "5": { "link": "//kodikplayer.com/seria/1599999/broken/720p" }
                  }
                }
              },
              "translation": {
                "id": 610,
                "title": "AniLibria.TV",
                "type": "voice"
              }
            }
          ]
        }
        """;
    const string playerHtml =
        """
        <html>
        <head>
          <script>
            var urlParams = '{"d":"kodik.cc","d_sign":"d-sign","pd":"kodikplayer.com","pd_sign":"pd-sign","ref":"","ref_sign":"ref-sign"}';
            player.type = 'seria';
            player.hash = '0123456789abcdef0123456789abcdef';
            player.id = '1599999';
          </script>
          <script src="/assets/app.player_single.js"></script>
        </head>
        <body></body>
        </html>
        """;
    const string scriptBody = """$.ajax({type:"POST",url:atob("L2Z0b3I="),cache:!1,dataType:"json"})""";
    const string linksJson =
        """
        {
          "links": {
            "720": [
              {
                "src": "https://cloud.kodik-storage.example/useruploads/demo/720.mp4:hls:manifest.m3u8"
              }
            ]
          }
        }
        """;

    var requests = new List<HttpRequestMessage>();
    var postCount = 0;
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(searchJson)
            };
        }

        if (request.Method == HttpMethod.Get &&
            (request.RequestUri!.AbsoluteUri == episodePlayerUrl ||
             request.RequestUri!.AbsoluteUri == seasonPlayerUrl))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playerHtml)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == scriptUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(scriptBody)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor")
        {
            postCount++;
            return postCount == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("<html>Error code: upstream</html>")
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(linksJson)
                };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    var link = client.GetEpisodeLinkAsync("59970", YummyKodik.Kodik.KodikIdType.Shikimori, 5, "610", CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("//cloud.kodik-storage.example/useruploads/demo/", link.BasePath, "Fallback season player link should produce the resolved base path.");
    AssertEqual(720, link.MaxQuality, "Fallback season player link should preserve max quality.");
    AssertEqual(2, requests.Count(x => x.Method == HttpMethod.Post && x.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor"), "Kodik should retry video link resolution with the season player link after the episode link fails.");
    AssertTrue(
        requests.Any(x => x.Method == HttpMethod.Get && x.RequestUri!.AbsoluteUri == seasonPlayerUrl),
        "Kodik should fetch the generic season player link with the requested episode as fallback.");
}

static void KodikClient_GetAnimeInfoAsync_FallsBackToHtmlFindPlayerResponse()
{
    const string finalPlayerUrl = "https://kodik.info/serial/12345/abcdef?episode=1&season=1";
    const string playerHtml =
        """
        <!DOCTYPE html>
        <html>
        <body>
            <div class="serial-series-box">
                <select>
                    <option value="1">1</option>
                    <option value="2">2</option>
                    <option value="3">3</option>
                </select>
            </div>
            <div class="serial-translations-box">
                <select>
                    <option value="110" data-translation-type="voice">AniLibria</option>
                    <option value="111" data-translation-type="subtitles">Субтитры</option>
                </select>
            </div>
        </body>
        </html>
        """;

    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"results":[]}""")
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://kodikplayer.com/find-player?", StringComparison.Ordinal))
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playerHtml)
                {
                    Headers =
                    {
                        ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/html")
                    }
                },
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalPlayerUrl)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == finalPlayerUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(playerHtml)
            };
        }

        throw new InvalidOperationException("Unexpected Kodik test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(YummyKodik.Kodik.KodikClient).GetConstructor(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(HttpClient), typeof(string), typeof(Microsoft.Extensions.Logging.ILogger), typeof(Func<bool>) },
        modifiers: null);
    AssertTrue(ctor is not null, "Kodik test constructor should exist.");

    var client = (YummyKodik.Kodik.KodikClient)ctor!.Invoke(new object[] { http, "test-token", NullLogger.Instance, (Func<bool>)(() => false) });

    var info = client.GetAnimeInfoAsync("61931", YummyKodik.Kodik.KodikIdType.Shikimori, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(3, info.SeriesCount, "HTML find-player fallback should keep serial episode count.");
    AssertEqual(2, info.Translations.Count, "HTML find-player fallback should keep parsed translations.");
    AssertEqual("AniLibria", info.Translations[0].Name, "First translation should be parsed from the HTML fallback.");
    AssertEqual("110", info.Translations[0].Id, "Translation id should come from the HTML fallback select value.");
    AssertEqual(finalPlayerUrl, requests[2].RequestUri!.AbsoluteUri, "Fallback should follow the final player page url after find-player returns HTML.");
}

static void CvhClient_BuildManifestResponseBody_ProxiesNestedPlaylists()
{
    var session = new CvhPlaybackSession
    {
        SessionId = "cvh-session",
        ManifestUrl = "https://vd744.okcdn.ru/path/master.m3u8",
        ManifestText = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1560378
            https://ok6-7.vkuser.net/expires/1774215751644/srcIp/212.58.123.211/type/2/id/12369819011643/video/
            #EXT-X-STREAM-INF:BANDWIDTH=5602553
            https://ok6-7.vkuser.net/expires/1774215751644/srcIp/212.58.123.211/type/4/id/12369819011643/video/
            """
    };

    var manifest = CvhClient.BuildManifestResponseBody(session, "http://localhost:8096/YummyKodik/cvh-proxy");

    AssertTrue(manifest.Contains("/YummyKodik/cvh-proxy/", StringComparison.Ordinal), "CVH master manifest should proxy nested playlist urls through the local endpoint.");
    AssertTrue(manifest.Contains(".m3u8?sessionId=cvh-session", StringComparison.Ordinal), "Proxied CVH nested playlist urls should carry an HLS extension and session id.");
    AssertEqual(2, session.ProxyResources.Count, "Each nested CVH playlist url should be tracked as a proxy resource.");
}

static void YummyKodikStreamUri_ParsesCvhRequest()
{
    var uri = "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=4861&ep=1&voice=AniStar&format=hls";
    var parsed = YummyKodikStreamUri.TryParseRequest(uri, out var request);

    AssertTrue(parsed, "CVH HTTP uri should be parsed.");
    AssertEqual(YummyStreamProviderKind.Cvh, request.Provider, "Provider kind should be CVH.");
    AssertEqual(4861L, request.AnimeId, "Anime id should be parsed from query.");
    AssertEqual(1, request.Episode, "Episode should be parsed from query.");
    AssertEqual("AniStar", request.VoiceName, "Voice should be parsed from query.");
}

static void YummyVideoCatalog_ParsesAllohaProviders()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 19312,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                Duration = 1420,
                IframeUrl = "https://alloha.yani.tv/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Ending = new YummySkipSegment
                    {
                        Time = 1200,
                        Length = 90
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);

    AssertTrue(catalog.HasAnyAllohaEpisodes, "Alloha entries should be detected from Yummy videos.");
    AssertEqual(1, catalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Alloha).Single(), "Episode list should include the parsed Alloha episode.");
    AssertEqual("Dream Cast", catalog.GetSupportedVoiceNames(YummyVideoProviderKind.Alloha, 1).Single(), "Alloha voice name should be normalized.");
    AssertEqual(215, catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, 1, "Dream Cast")?.Alloha?.TranslationId ?? 0, "Alloha translation id should come from the iframe payload.");

    var chosenVoice = catalog.PickPreferredVoiceName(
        YummyVideoProviderKind.Alloha,
        1,
        explicitVoiceName: string.Empty,
        savedVoiceName: "Dream Cast",
        preferredFilter: string.Empty,
        out var reason);
    AssertEqual("Dream Cast", chosenVoice, "Saved Alloha voice should be selected when available.");
    AssertEqual("saved", reason, "Reason should explain why the Alloha voice was picked.");
}

static void AllohaApiClient_ParsesSerialCatalogEntries()
{
    var requests = new List<HttpRequestMessage>();
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                {
                  "status": "success",
                  "data": {
                    "seasons": {
                      "1": {
                        "season": 1,
                        "episodes": {
                          "1": {
                            "episode": 1,
                            "translation": {
                              "215": {
                                "translation": "Dream Cast",
                                "iframe": "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
                              },
                              "79": {
                                "translation": "Субтитры",
                                "iframe": "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=79&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
                              }
                            }
                          }
                        }
                      }
                    }
                  }
                }
                """)
        };
    });

    using var http = new HttpClient(handler);
    var client = new AllohaApiClient(http, "api-token-value");
    var entries = client.GetCatalogEntriesByKpAsync(10683417, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(2, entries.Count, "Alloha API should produce an entry per translation.");
    AssertEqual(1, entries.Select(x => x.EpisodeNumber).Distinct().Single(), "Alloha API entries should keep the episode number.");
    AssertTrue(entries.Any(x => string.Equals(x.DisplayVoiceName, "Dream Cast", StringComparison.Ordinal)), "Dream Cast must be parsed from Alloha API.");
    AssertTrue(entries.Any(x => string.Equals(x.DisplayVoiceName, "Субтитры", StringComparison.Ordinal)), "Subtitle translation must be parsed from Alloha API.");
    AssertEqual(215, entries.First(x => x.DisplayVoiceName == "Dream Cast").Alloha?.TranslationId ?? 0, "Alloha API entry should keep the translation id.");
    AssertEqual(1, requests.Count, "Alloha API client should perform a single request.");
    AssertTrue(requests.Single().RequestUri!.Query.Contains("token=api-token-value", StringComparison.Ordinal), "Alloha API request should include the configured token.");
    AssertTrue(requests.Single().RequestUri!.Query.Contains("kp=10683417", StringComparison.Ordinal), "Alloha API request should query by kp id.");
}

static void AllohaApiClient_ParsesEpisodesArrayCatalogEntries()
{
    var handler = new DelegatingTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent("""
            {
              "status": "success",
              "data": {
                "seasons": {
                  "2": {
                    "season": 2,
                    "episodes": [
                      {
                        "episode": 1,
                        "translation": {
                          "215": {
                            "translation": "Dream Cast",
                            "iframe": "https://larkin-as.stloadi.live/?token_movie=movie-1&translation=215&season=2&episode=1&token=req-1"
                          }
                        }
                      },
                      {
                        "episode": 2,
                        "translation": {
                          "79": {
                            "translation": "Субтитры",
                            "iframe": "https://larkin-as.stloadi.live/?token_movie=movie-1&translation=79&season=2&episode=2&token=req-2"
                          }
                        }
                      }
                    ]
                  }
                }
              }
            }
            """)
    });

    using var http = new HttpClient(handler);
    var client = new AllohaApiClient(http, "api-token-value");
    var entries = client.GetCatalogEntriesByKpAsync(5235921, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(2, entries.Count, "Alloha API should tolerate episodes arrays and still emit entries.");
    AssertEqual("1,2", string.Join(",", entries.Select(x => x.EpisodeNumber).OrderBy(x => x)), "Episode numbers should be parsed from array items.");
    AssertEqual(2, entries.First(x => x.DisplayVoiceName == "Dream Cast").Alloha?.SeasonNumber ?? 0, "Season number should survive array parsing.");
    AssertEqual(79, entries.First(x => x.DisplayVoiceName == "Субтитры").Alloha?.TranslationId ?? 0, "Translation id should still come from the nested translation map.");
}

static void AllohaApiCatalogLoader_FilterEntriesForSeason_KeepsOnlyRequestedSeason()
{
    var entries = new[]
    {
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            DisplayVoiceName = "Season 1 voice",
            Alloha = new YummyAllohaSource
            {
                TranslationId = 215,
                SeasonNumber = 1,
                EpisodeNumber = 1
            }
        },
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            DisplayVoiceName = "Season 2 voice",
            Alloha = new YummyAllohaSource
            {
                TranslationId = 215,
                SeasonNumber = 2,
                EpisodeNumber = 1
            }
        }
    };

    var filtered = AllohaApiCatalogLoader.FilterEntriesForSeason(entries, seasonNumber: 2);

    AssertEqual(1, filtered.Count, "Season filtering should keep only entries from the requested season.");
    AssertEqual("Season 2 voice", filtered.Single().DisplayVoiceName, "Season filtering should retain the matching-season translation.");
}

static void AllohaApiCatalogLoader_LoadEntriesAsync_CachesSuccessfulCatalogLoads()
{
    var requests = 0;
    var handler = new DelegatingTestHandler(_ =>
    {
        Interlocked.Increment(ref requests);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildAllohaApiCatalogPayload())
        };
    });

    using var http = new HttpClient(handler);
    var cfg = BuildAllohaApiConfig("success-cache-token");
    var anime = BuildAllohaApiAnime(animeId: 41001, kpId: 910001);

    var first = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    var second = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(1, requests, "Successful Alloha API responses should be cached for repeated lookups.");
    AssertEqual(1, first.Count, "Cached Alloha API lookup should keep the parsed entry.");
    AssertEqual(1, second.Count, "Second Alloha API lookup should reuse the cached entry set.");
}

static void AllohaApiCatalogLoader_LoadEntriesAsync_DeduplicatesConcurrentCatalogLoads()
{
    var requests = 0;
    var handler = new DelegatingTestHandler(_ =>
    {
        Interlocked.Increment(ref requests);
        Thread.Sleep(75);
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(BuildAllohaApiCatalogPayload())
        };
    });

    using var http = new HttpClient(handler);
    var cfg = BuildAllohaApiConfig("inflight-cache-token");
    var anime = BuildAllohaApiAnime(animeId: 41002, kpId: 910002);

    var tasks = Enumerable.Range(0, 5)
        .Select(_ => Task.Run(() => AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)))
        .ToArray();

    var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

    AssertEqual(1, requests, "Concurrent Alloha API lookups for the same title should share one upstream request.");
    AssertTrue(results.All(x => x.Count == 1), "Concurrent Alloha API lookups should all receive the shared catalog result.");
}

static void AllohaApiCatalogLoader_LoadEntriesAsync_CachesFailuresBriefly()
{
    var requests = 0;
    var handler = new DelegatingTestHandler(_ =>
    {
        Interlocked.Increment(ref requests);
        return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""
                <html>
                <head><title>503 Service Unavailable</title></head>
                <body><center><h1>503 Service Unavailable</h1></center></body>
                </html>
                """)
        };
    });

    using var http = new HttpClient(handler);
    var cfg = BuildAllohaApiConfig("failure-cache-token");
    var anime = BuildAllohaApiAnime(animeId: 41003, kpId: 910003);

    var first = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();
    var second = AllohaApiCatalogLoader.LoadEntriesAsync(cfg, anime, http, NullLogger.Instance, CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual(0, first.Count, "Failed Alloha API lookups should keep the existing empty fallback behavior.");
    AssertEqual(0, second.Count, "Repeated failed Alloha API lookups should continue returning the empty fallback.");
    AssertEqual(1, requests, "Failed Alloha API lookups should be negative-cached briefly to avoid hammering the upstream.");
}

static void YummyVideoCatalog_MergesAdditionalAllohaEntriesWithoutOverwritingYummyData()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 19312,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                Duration = 1420,
                IframeUrl = "https://alloha.yani.tv/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка AniLibria"
                }
            }
        }
    };

    var additionalEntries = new[]
    {
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Dream Cast",
            DisplayVoiceName = "Dream Cast",
            DurationSeconds = 0,
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=d317441359e505c343c2063edc97e7",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "321272ebdc58c94adce7628d4a6017",
                RequestToken = "d317441359e505c343c2063edc97e7",
                TranslationId = 215,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=215&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
            }
        },
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Субтитры",
            DisplayVoiceName = "Субтитры",
            DurationSeconds = 0,
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=79&season=1&episode=1&token=d317441359e505c343c2063edc97e7",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "321272ebdc58c94adce7628d4a6017",
                RequestToken = "d317441359e505c343c2063edc97e7",
                TranslationId = 79,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=321272ebdc58c94adce7628d4a6017&translation=79&season=1&episode=1&token=d317441359e505c343c2063edc97e7"
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime, additionalEntries);
    var voiceNames = catalog.GetSupportedVoiceNames(YummyVideoProviderKind.Alloha, 1);

    AssertEqual(2, voiceNames.Count, "Merged Alloha catalog should include the extra translation.");
    AssertTrue(voiceNames.Contains("Dream Cast"), "Alloha API should be able to replace a misleading Yummy label for the same source.");
    AssertTrue(voiceNames.Contains("Субтитры"), "Extra Alloha API translation should be added.");
    AssertEqual(1420, catalog.GetDurationSeconds(YummyVideoProviderKind.Alloha, 1, "Dream Cast") ?? 0, "Existing Yummy entry should keep its richer duration data.");
    AssertEqual(
        "8b5512267a2a52e9de06d67d342e0c",
        catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, 1, "Dream Cast")?.Alloha?.RequestToken ?? string.Empty,
        "Duplicate Alloha merges should keep the original playable source fields when they already exist.");
}

static void YummyVideoCatalog_KeepsAllohaEntriesFromDifferentSeasonsDistinct()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 19312
    };

    var additionalEntries = new[]
    {
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Dream Cast S1",
            DisplayVoiceName = "Dream Cast S1",
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=1&episode=1&token=req-1",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "movie",
                RequestToken = "req-1",
                TranslationId = 215,
                SeasonNumber = 1,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=1&episode=1&token=req-1"
            }
        },
        new YummyVideoEntry
        {
            EpisodeNumber = 1,
            Provider = YummyVideoProviderKind.Alloha,
            RawDubbing = "Dream Cast S2",
            DisplayVoiceName = "Dream Cast S2",
            IframeUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=2&episode=1&token=req-2",
            Alloha = new YummyAllohaSource
            {
                MovieToken = "movie",
                RequestToken = "req-2",
                TranslationId = 215,
                SeasonNumber = 2,
                EpisodeNumber = 1,
                RefererUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=2&episode=1&token=req-2"
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime, additionalEntries);
    var voiceNames = catalog.GetSupportedVoiceNames(YummyVideoProviderKind.Alloha, 1);

    AssertEqual(2, voiceNames.Count, "Alloha entries from different seasons should not collapse into one dedupe key.");
    AssertTrue(voiceNames.Contains("Dream Cast S1"), "Season one Alloha translation should remain distinct.");
    AssertTrue(voiceNames.Contains("Dream Cast S2"), "Season two Alloha translation should remain distinct.");
}

static void YummyVideoCatalog_CombinesCoverageAcrossProviders()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=1&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                }
            },
            new()
            {
                Number = "2",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=2&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            },
            new()
            {
                Number = "2",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=2&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            },
            new()
            {
                Number = "3",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=3&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            },
            new()
            {
                Number = "4",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=4&dubbing_code=158&dubbing=AniStar",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniStar"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var providers = new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh };

    AssertEqual("1,2,3,4", string.Join(",", catalog.GetSupportedEpisodeNumbersAcrossProviders(providers)), "Combined episode coverage should use the union of provider episode lists.");
    AssertEqual("AniStar,Dream Cast", string.Join(",", catalog.GetSupportedVoiceNamesAcrossProviders(1, providers)), "Combined voice list should include voices from all providers for the episode.");
    AssertEqual(YummyVideoProviderKind.Cvh, catalog.PickPreferredProvider(1, explicitVoiceName: "AniStar", providers: providers) ?? YummyVideoProviderKind.Unknown, "AniStar should resolve to CVH when Alloha does not have that voice.");
    AssertEqual(YummyVideoProviderKind.Alloha, catalog.PickPreferredProvider(2, providers: providers) ?? YummyVideoProviderKind.Unknown, "When both providers have the episode, Alloha should keep higher priority.");
    AssertEqual(YummyVideoProviderKind.Cvh, catalog.PickPreferredProvider(4, providers: providers) ?? YummyVideoProviderKind.Unknown, "Episodes missing in Alloha should fall back to CVH.");
}

static void YummyVideoCatalog_MatchesEquivalentVoiceNames()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=AniLibria",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniLibria"
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=377&dubbing=AniLeague",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "AniLeague"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLibria.TV");
    var compactTvAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AnilibriaTV");
    var aliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLiberty (AniLibria)");
    var shortAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLiberty");
    var compactAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "Anilib");
    var directAliasEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLibria");
    var dottedTvSuffixEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "AniLeague.TV");

    AssertTrue(entry is not null, "Equivalent voice names should resolve to the same Yummy-backed entry.");
    AssertTrue(compactTvAliasEntry is not null, "Compact provider codes like AnilibriaTV should resolve to the AniLibria voice.");
    AssertTrue(aliasEntry is not null, "Parenthetical provider aliases should resolve to the same Yummy-backed entry.");
    AssertTrue(shortAliasEntry is not null, "Short AniLiberty aliases should resolve to the same Yummy-backed entry.");
    AssertTrue(compactAliasEntry is not null, "Compact Anilib aliases should resolve to the same Yummy-backed entry.");
    AssertTrue(directAliasEntry is not null, "Direct canonical names should still resolve after provider-key normalization changes.");
    AssertTrue(dottedTvSuffixEntry is not null, "Provider names with dotted .TV suffixes should resolve to the transport-free Yummy voice.");
    AssertEqual("AniLibria", entry!.DisplayVoiceName, "Canonical Yummy voice should still be returned.");
    AssertEqual("AniLibria", compactTvAliasEntry!.DisplayVoiceName, "Compact CVH-style AniLibria codes should canonicalize to the AniLibria voice.");
    AssertEqual("AniLibria", aliasEntry!.DisplayVoiceName, "Alias-heavy provider names should still resolve to the canonical Yummy voice.");
    AssertEqual("AniLibria", shortAliasEntry!.DisplayVoiceName, "AniLiberty aliases should canonicalize to the AniLibria voice.");
    AssertEqual("AniLibria", compactAliasEntry!.DisplayVoiceName, "Short Alloha-style Anilib aliases should canonicalize to the AniLibria voice.");
    AssertEqual("AniLibria", directAliasEntry!.DisplayVoiceName, "Canonical names should continue resolving normally.");
    AssertEqual("AniLeague", dottedTvSuffixEntry!.DisplayVoiceName, "Trailing .TV transport suffixes should canonicalize to the same Yummy voice.");
}

static void TranslationNameKeyNormalizer_UsesCuratedVoiceAliasGroups()
{
    AssertEqual("2x2", TranslationNameKeyNormalizer.Normalize("2х2"), "Curated numeral aliases should normalize to the ASCII canonical key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("Anilib"), "Short Alloha-style AniLibria aliases should normalize to the canonical key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("AniLiberty"), "CVH-style AniLiberty aliases should normalize to the canonical key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("AniLiberty (AniLibria)"), "Parenthetical provider aliases should normalize to the canonical AniLibria key.");
    AssertEqual("anilibria", TranslationNameKeyNormalizer.Normalize("AnilibriaTV"), "Compact .TV provider variants should normalize to the AniLibria key.");
    AssertEqual("studioband", TranslationNameKeyNormalizer.Normalize("Студийная Банда"), "Cross-script StudioBand aliases should normalize to the canonical key.");
    AssertEqual("studioband", TranslationNameKeyNormalizer.Normalize("Studio Band"), "Spacing-only StudioBand variants should normalize to the canonical key.");
    AssertEqual("anileague", TranslationNameKeyNormalizer.Normalize("AniLeague.TV"), "Dotted .TV suffixes should normalize to the transport-free canonical key.");
    AssertEqual("shizaproject", TranslationNameKeyNormalizer.Normalize("SHIZA Project"), "Stable provider names without aliases should keep their direct canonical key.");
}

static void YummyVideoCatalog_MatchesCrossProviderVoiceAliases()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21009,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21009&episode=1&dubbing_code=StudioBand&dubbing=StudioBand",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "StudioBand"
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "Студийная Банда");
    var spacedEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, 1, "Studio Band");

    AssertTrue(entry is not null, "Cross-provider aliases discovered from anchor titles should resolve to the same voice.");
    AssertTrue(spacedEntry is not null, "Provider formatting variants with spaces should resolve to the same voice.");
    AssertEqual("StudioBand", entry!.DisplayVoiceName, "Provider-native aliases should canonicalize to the StudioBand voice.");
    AssertEqual("StudioBand", spacedEntry!.DisplayVoiceName, "Provider formatting variants should preserve the canonical StudioBand voice.");
}

static void YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_FallsBackToOtherProvider()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=1&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                },
                Skips = null
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=Dream Cast",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 90,
                        Length = 90
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredEntryWithSkipsAcrossProviders(
        1,
        "Dream Cast",
        new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh });

    AssertTrue(entry is not null, "Cross-provider skip lookup should find a usable entry.");
    AssertEqual(YummyVideoProviderKind.Cvh, entry!.Provider, "Cross-provider skip lookup should fall back to CVH when the requested Alloha entry has no skips.");
    AssertEqual(90, entry.Skips?.Opening?.Time ?? 0, "Cross-provider skip lookup should keep the fallback skip timings.");
}

static void YummyVideoCatalog_FindPreferredEntryWithSkipsAcrossProviders_PrefersRequestedProvider()
{
    var anime = new YummyAnimeResponse
    {
        AnimeId = 21008,
        Videos = new List<YummyVideoItem>
        {
            new()
            {
                Number = "1",
                IframeUrl = "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=1&token=request",
                Data = new YummyVideoData
                {
                    PlayerId = 2,
                    Dubbing = "Озвучка Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 12,
                        Length = 88
                    }
                }
            },
            new()
            {
                Number = "1",
                IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=Dream Cast",
                Data = new YummyVideoData
                {
                    PlayerId = 3,
                    Dubbing = "Dream Cast"
                },
                Skips = new YummyVideoSkips
                {
                    Opening = new YummySkipSegment
                    {
                        Time = 90,
                        Length = 90
                    }
                }
            }
        }
    };

    var catalog = YummyVideoCatalog.Create(anime);
    var entry = catalog.FindPreferredEntryWithSkipsAcrossProviders(
        1,
        "Dream Cast",
        new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh });

    AssertTrue(entry is not null, "Cross-provider skip lookup should return the requested provider when it already has skips.");
    AssertEqual(YummyVideoProviderKind.Alloha, entry!.Provider, "Cross-provider skip lookup should preserve the requested provider priority when it has usable skips.");
    AssertEqual(12, entry.Skips?.Opening?.Time ?? 0, "Cross-provider skip lookup should keep the requested provider's own skip timings.");
}

static void GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode()
{
    var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();

    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "Dream Cast");
    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AnimeVost");

    var missing = new[]
    {
        "Dream Cast",
        "AnimeVost",
        "AniLibria"
    }
        .Where(x => !EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, x))
        .ToArray();

    AssertEqual(1, missing.Length, "Only translations absent from Yummy/CVH coverage should remain for Kodik supplementation.");
    AssertEqual("AniLibria", missing[0], "Kodik supplement should only add the missing translation.");
}

static void EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants()
{
    var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();
    EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria");

    AssertTrue(
        EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria.TV"),
        "Equivalent translation suffixes should collapse to the same key so Yummy can replace stale Kodik files.");
    AssertTrue(
        EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLiberty (AniLibria)"),
        "Parenthetical provider aliases should collapse to the same key so equivalent voices keep stable files.");
}

static void ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName()
{
    var aliases = new Dictionary<int, Dictionary<string, string>>
    {
        [1] = new(StringComparer.OrdinalIgnoreCase)
        {
            [EpisodeArtifactMaintenance.NormalizeEpisodeTranslationKey("AniLibria.TV")] = "S01E01 - AniLibria.TV"
        }
    };

    var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(aliases, 1, "S01E01", "AniLibria");

    AssertEqual("S01E01 - AniLibria.TV", fileBaseName, "Refresh should reuse the existing equivalent file name to keep Jellyfin item ids stable.");
}

static void ResolveKodikAvailableEpisodeCount_UsesYummyHintWhenSeriesCountIsZero()
{
    var count = YummyEpisodeAvailability.ResolveKodikAvailableEpisodeCount(0, 1);
    AssertEqual(1, count, "When Kodik search returns zero seriesCount but Yummy knows episode 1 exists, refresh should still generate files.");
}

static void KeepLatestEpisodePerResolvedLink_DropsEarlierEpisodesWhenKodikReusesSameVideo()
{
    var candidateEpisodes = new[] { 1, 2 };
    var resolvedBasePaths = new Dictionary<int, string>
    {
        [1] = "//cloud.solodcdn.com/useruploads/shared/",
        [2] = "//cloud.solodcdn.com/useruploads/shared/"
    };

    var result = KodikEpisodeLinkDeduper.KeepLatestEpisodePerResolvedLink(candidateEpisodes, resolvedBasePaths);
    AssertEqual("2", string.Join(",", result.OrderBy(x => x)), "When Kodik resolves multiple episodes to the same video, only the latest episode should remain.");
}

static void CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage()
{
    var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
    var seasonDir = Path.Combine(tempRoot, "Season 02");
    Directory.CreateDirectory(seasonDir);

    try
    {
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Dream Cast.strm"), "keep");
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Dream Cast.nfo"), "<ok />");
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Old Voice.strm"), "delete");
        File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Old Voice.nfo"), "<delete />");
        File.WriteAllText(Path.Combine(seasonDir, "S02E02 - Old Voice.strm"), "delete");
        File.WriteAllText(Path.Combine(seasonDir, "S02E02 - Old Voice.nfo"), "<delete />");

        var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>
        {
            [1] = new(StringComparer.OrdinalIgnoreCase)
            {
                "S02E01 - Dream Cast"
            }
        };

        EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
            NullLogger.Instance,
            seasonDir,
            2,
            expectedEpisodeFileBaseNames,
            1);

        AssertTrue(File.Exists(Path.Combine(seasonDir, "S02E01 - Dream Cast.strm")), "Expected translation STRM should remain after cleanup.");
        AssertTrue(File.Exists(Path.Combine(seasonDir, "S02E01 - Dream Cast.nfo")), "Expected translation NFO should remain after cleanup.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E01 - Old Voice.strm")), "Unexpected translation STRM should be removed.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E01 - Old Voice.nfo")), "Unexpected translation NFO should be removed.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E02 - Old Voice.strm")), "Episodes beyond the currently available range should be removed.");
        AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E02 - Old Voice.nfo")), "NFO for unavailable future episodes should be removed.");
    }
    finally
    {
        TryDeleteDirectory(tempRoot);
    }
}

static void AllohaPlaybackService_BuildsExpectedBorthSuffix()
{
    var buildBorthSuffix = typeof(AllohaPlaybackService).GetMethod(
        "BuildBorthSuffix",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(buildBorthSuffix is not null, "Alloha Borth helper should exist.");

    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
    var suffix = buildBorthSuffix!.Invoke(null, new object[] { viewporti }) as string;
    AssertEqual(
        "SmdyTmVwVmFnQWd8MTc3NDEwNjgwNnxNVVZ1N09oSmNUdFZxUU11RlJqTkFXVjRFU1g0SXpTSGZDRFdyUXdsQ29Z",
        suffix,
        "Borth suffix must match the live Alloha viewporti transform.");
}

static void AllohaPlaybackService_CreatesSessionViaIframeAndBnsi()
{
    var requests = new List<HttpRequestMessage>();
    var iframeUrl = "https://alloha.yani.tv/?token_movie=6ab5db4ce142f2103d0bed3e641380&translation=215&season=1&episode=2&token=8b5512267a2a52e9de06d67d342e0c&hidden=translation,season,episode";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
    var expectedBorth = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9|SmdyTmVwVmFnQWd8MTc3NDEwNjgwNnxNVVZ1N09oSmNUdFZxUU11RlJqTkFXVjRFU1g0SXpTSGZDRFdyUXdsQ29Z";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=6ab5db4ce142f2103d0bed3e641380", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":2,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"2\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            AssertEqual(expectedBorth, request.Headers.GetValues("Borth").Single(), "Alloha bnsi request should use the computed Borth header.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "skipTime": 90,
                      "removeTime": 1200,
                      "hlsSource": [
                        {
                          "quality": {
                            "720": "https://stream-balancer-alloha.example/serial/playlist-720.m3u8",
                            "1080": "https://stream-balancer-alloha.example/serial/master.m3u8 or https://backup.example/serial/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/serial/master.m3u8")
        {
            AssertEqual("9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9", request.Headers.GetValues("Accepts-Controls").Single(), "Alloha manifest request should use Accepts-Controls.");
            AssertTrue(request.Headers.GetValues("Authorizations").Single().StartsWith("Bearer ", StringComparison.Ordinal), "Alloha manifest request should carry the guard token.");
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Alloha manifest request should keep the iframe referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    https://cdn.example/segment-002.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    var source = new YummyAllohaSource
    {
        MovieToken = "6ab5db4ce142f2103d0bed3e641380",
        RequestToken = "8b5512267a2a52e9de06d67d342e0c",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 2,
        Hidden = "translation,season,episode",
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();
    var manifestBody = AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);

    AssertEqual("https://stream-balancer-alloha.example/serial/master.m3u8", session.ManifestUrl, "Alloha should choose the requested quality manifest.");
    AssertTrue(manifestBody.Contains($"{proxyBaseUrl}/", StringComparison.Ordinal), "Alloha manifest should rewrite entries to local proxy urls.");
    AssertTrue(manifestBody.Contains($".ts?sessionId={session.SessionId}&resource=", StringComparison.Ordinal), "Alloha segment proxy urls should keep a playable media extension.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/segment-001.ts"), "Relative Alloha manifest urls should be registered as proxy resources.");
    AssertTrue(session.ProxyResources.Values.Contains("https://cdn.example/segment-002.ts"), "Absolute Alloha manifest urls should be registered as proxy resources.");
    AssertEqual(3, requests.Count, "Alloha browserless flow should perform iframe, bnsi, and manifest requests.");
}

static void AllohaPlaybackService_UsesIframeOriginForMirroredHost()
{
    var iframeUrl = "https://larkin-as.stloadi.live/?token_movie=mirror-movie&translation=222&season=2&episode=1&token=mirror-token";
    var viewporti = "1RhgM1dv3ztOKcE92RUowlxB2DSE3W1WkFFOcFIxd41UpsZ481aatG5NZMdWVH0EFTzjH0UTcdacSTdSNMMZSMdY";
    const string expectedOrigin = "https://larkin-as.stloadi.live";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://larkin-as.stloadi.live/?token_movie=mirror-movie", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"type\":\"serial\",\"active\":{\"id\":844166,\"seasons\":2,\"episode\":1,\"id_translation\":222},\"all\":{\"2\":{\"1\":{\"t222\":{\"id\":844166}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://larkin-as.stloadi.live/bnsi/movies/844166")
        {
            AssertEqual(expectedOrigin, request.Headers.GetValues("Origin").Single(), "Alloha bnsi request should target the iframe origin for mirrored hosts.");
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Alloha bnsi request should keep the mirrored iframe referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/mirror/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/mirror/master.m3u8")
        {
            AssertEqual(expectedOrigin, request.Headers.GetValues("Origin").Single(), "Alloha manifest request should reuse the iframe origin for mirrored hosts.");
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Alloha manifest request should keep the mirrored iframe referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    mirror-segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected mirrored Alloha test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist for mirrored host scenarios.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "mirror-movie",
        RequestToken = "mirror-token",
        TranslationId = 222,
        SeasonNumber = 2,
        EpisodeNumber = 1,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual(expectedOrigin, session.RequiredHttpHeaders["Origin"], "Resolved mirrored sessions should keep the iframe origin in required headers.");
    AssertEqual(iframeUrl, session.RequiredHttpHeaders["Referer"], "Resolved mirrored sessions should keep the iframe referer in required headers.");
}

static void AllohaPlaybackService_PrefersRequestedVoiceWhenBnsiReturnsMultipleTracks()
{
    var iframeUrl = "https://alloha.yani.tv/?token_movie=voice-movie&translation=215&season=1&episode=2&token=req-token";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=voice-movie", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":2,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"2\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "label": "(Russian) DUB | DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/deep/master.m3u8"
                          }
                        },
                        {
                          "label": "(Russian) AniLibria.TV",
                          "audioId": "2",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/anilibria/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/anilibria/master.m3u8")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha preferred-voice test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "voice-movie",
        RequestToken = "req-token",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 2,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, "AniLiberty", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://stream-balancer-alloha.example/anilibria/master.m3u8", session.ManifestUrl, "Alloha should prefer the manifest candidate matching the requested voice.");
    AssertEqual("AniLibria.TV", session.SelectedVoiceName, "Selected Alloha voice should match the requested track rather than the first quality candidate.");
}

static void AllohaPlaybackService_UsesAlternateVoiceFieldWhenLabelIsOpaque()
{
    var iframeUrl = "https://alloha.yani.tv/?token_movie=voice-movie-opaque&translation=215&season=1&episode=4&token=req-token";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=voice-movie-opaque", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191330,\"seasons\":1,\"episode\":4,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"4\":{\"id\":1191330}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191330")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "label": "1",
                          "voice": "AniDUB",
                          "audioId": "1",
                          "quality": {
                            "720": "https://stream-balancer-alloha.example/anidub-opaque/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/anidub-opaque/master.m3u8")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha opaque-label test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "voice-movie-opaque",
        RequestToken = "req-token",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 4,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 720, "AniDUB", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://stream-balancer-alloha.example/anidub-opaque/master.m3u8", session.ManifestUrl, "Alloha should keep the only manifest candidate when the readable voice lives outside the label field.");
    AssertEqual("AniDUB", session.SelectedVoiceName, "Alloha should prefer the alternate upstream voice field over an opaque numeric label.");
}

static void AllohaPlaybackService_MatchesShortAnilibAliasToAnilibria()
{
    var iframeUrl = "https://alloha.yani.tv/?token_movie=voice-movie-short&translation=215&season=1&episode=3&token=req-token";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=voice-movie-short", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191329,\"seasons\":1,\"episode\":3,\"id_translation\":215},\"all\":{\"t215\":{\"file\":{\"1\":{\"3\":{\"id\":1191329}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191329")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "hlsSource": [
                        {
                          "label": "(Russian) DUB | DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/deep-short/master.m3u8"
                          }
                        },
                        {
                          "label": "(Russian) Anilib",
                          "audioId": "2",
                          "quality": {
                            "1080": "https://stream-balancer-alloha.example/anilib-short/master.m3u8"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get &&
            request.RequestUri!.AbsoluteUri == "https://stream-balancer-alloha.example/anilib-short/master.m3u8")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-VERSION:3
                    segment-001.ts
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha short-alias test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var source = new YummyAllohaSource
    {
        MovieToken = "voice-movie-short",
        RequestToken = "req-token",
        TranslationId = 215,
        SeasonNumber = 1,
        EpisodeNumber = 3,
        RefererUrl = iframeUrl
    };

    var session = service.CreateSessionAsync(source, 1080, "Anilibria", CancellationToken.None).GetAwaiter().GetResult();

    AssertEqual("https://stream-balancer-alloha.example/anilib-short/master.m3u8", session.ManifestUrl, "Alloha should treat short Anilib labels as AniLibria when matching the requested voice.");
    AssertEqual("Anilib", session.SelectedVoiceName, "Selected Alloha voice should preserve the upstream short alias label after matching.");
}

static void YummyKodikStreamController_AllowsSingleOpaqueAllohaTrackMarker()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "AllohaSessionSupportsRequestedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Alloha voice validation helper should exist.");

    var session = new AllohaPlaybackSession
    {
        AudioTrackId = "1",
        SelectedVoiceName = "1",
        AvailableVoiceNames = new[] { "1" }
    };

    var supported = (bool)method!.Invoke(null, new object[] { session, "AniDUB" })!;
    AssertTrue(supported, "A single opaque Alloha track marker should not block playback when the translation was already chosen upstream.");
}

static void YummyKodikStreamController_RejectsSingleGenericRussianAllohaTrackMarker()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "AllohaSessionSupportsRequestedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Alloha voice validation helper should exist.");

    var session = new AllohaPlaybackSession
    {
        AudioTrackId = "1",
        SelectedVoiceName = "Русский",
        AvailableVoiceNames = new[] { "Русский" }
    };

    var supported = (bool)method!.Invoke(null, new object[] { session, "AnimeVost" })!;
    AssertFalse(supported, "A generic Russian Alloha track marker is not enough evidence that an explicit named voice was selected.");
}

static void YummyKodikStreamController_RejectsMultipleOpaqueAllohaTrackMarkers()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "AllohaSessionSupportsRequestedVoice",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Alloha voice validation helper should exist.");

    var session = new AllohaPlaybackSession
    {
        AudioTrackId = "1",
        SelectedVoiceName = "1",
        AvailableVoiceNames = new[] { "1", "2" }
    };

    var supported = (bool)method!.Invoke(null, new object[] { session, "AniDUB" })!;
    AssertFalse(supported, "Opaque multi-track Alloha sessions should still be rejected until we can match a real voice name.");
}

static void YummyKodikStreamController_OrdersYummyFallbackProviders()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "GetFallbackProviderOrder",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Yummy fallback provider ordering helper should exist.");

    var allohaFallback = ((IEnumerable<YummyVideoProviderKind>)method!.Invoke(null, new object[] { YummyStreamProviderKind.Alloha })!)
        .ToArray();
    var cvhFallback = ((IEnumerable<YummyVideoProviderKind>)method.Invoke(null, new object[] { YummyStreamProviderKind.Cvh })!)
        .ToArray();

    AssertEqual("Cvh", string.Join(",", allohaFallback), "Alloha failures should first compensate through CVH.");
    AssertEqual("Alloha", string.Join(",", cvhFallback), "CVH failures should first compensate through Alloha.");
}

static void YummyKodikStreamController_FindsKodikFallbackVoiceByAlias()
{
    var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
        "FindKodikTranslationByVoiceName",
        BindingFlags.NonPublic | BindingFlags.Static);
    AssertTrue(method is not null, "Kodik fallback voice matcher should exist.");

    var translations = new[]
    {
        new KodikTranslation
        {
            Id = "10",
            Type = "voice",
            Name = "AniLiberty (AniLibria)",
            AvailableEpisodes = new[] { 4 }
        },
        new KodikTranslation
        {
            Id = "11",
            Type = "voice",
            Name = "Other",
            AvailableEpisodes = new[] { 4 }
        }
    };

    var match = (KodikTranslation?)method!.Invoke(null, new object[] { translations, "AnilibriaTV", 4 });
    AssertEqual("10", match?.Id ?? string.Empty, "Kodik fallback should use the same cross-provider voice alias matching as Yummy providers.");
}

static void AllohaPlaybackService_RewritesManifestUrisToProxyUrls()
{
    var proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-1",
        ManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8",
        ManifestText = """
            #EXTM3U
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",NAME="AniStar",URI="index-f1-a1.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=5309735
            index-f1-v1.m3u8
            """,
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };

    var manifestBody = AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);

    AssertTrue(manifestBody.Contains($"{proxyBaseUrl}/", StringComparison.Ordinal), "Master manifest should point nested resources at the local proxy.");
    AssertTrue(manifestBody.Contains(".m3u8?sessionId=session-1&resource=", StringComparison.Ordinal), "Nested Alloha playlists should keep a playlist extension in proxy urls.");
    AssertTrue(!manifestBody.Contains("URI=\"index-f1-a1.m3u8\"", StringComparison.Ordinal), "Directive URI attributes should not keep raw relative Alloha urls.");
    AssertTrue(!manifestBody.Contains("\nindex-f1-v1.m3u8", StringComparison.Ordinal), "Variant playlist lines should not keep raw relative Alloha urls.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/index-f1-a1.m3u8"), "Audio playlist should be registered as a proxy resource.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8"), "Variant playlist should be registered as a proxy resource.");
}

static void AllohaPlaybackService_DownloadProxyResourceRewritesNestedManifest()
{
    var requests = new List<HttpRequestMessage>();
    const string resourceId = "resource-1";
    const string parentManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8";
    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                #EXTM3U
                #EXT-X-MAP:URI="init.mp4"
                #EXTINF:3.003,
                segment-001.m4s
                """)
        };
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-2",
        RefererUrl = "https://alloha.yani.tv/?token_movie=demo",
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = "https://alloha.yani.tv/?token_movie=demo"
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };
    session.ProxyResources[resourceId] = "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8";
    session.ProxyResourceReferers[resourceId] = parentManifestUrl;

    var resource = service.DownloadProxyResourceAsync(
            session,
            resourceId,
            "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8",
            "http://localhost:8096/YummyKodik/alloha-proxy",
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var manifestBody = System.Text.Encoding.UTF8.GetString(resource.Content);
    AssertEqual("application/vnd.apple.mpegurl", resource.ContentType, "Nested Alloha playlists should be served back as HLS manifests.");
    AssertTrue(manifestBody.Contains("http://localhost:8096/YummyKodik/alloha-proxy/", StringComparison.Ordinal), "Nested Alloha playlists should be rewritten to local proxy urls.");
    AssertTrue(manifestBody.Contains(".m4s?sessionId=session-2&resource=", StringComparison.Ordinal), "Nested Alloha segments should keep a playable media extension in proxy urls.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/init.mp4"), "Nested manifest map URI should be registered as a proxy resource.");
    AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/segment-001.m4s"), "Nested manifest segment should be registered as a proxy resource.");

    var request = requests.Single();
    AssertEqual("application/vnd.apple.mpegurl, application/x-mpegURL, */*", string.Join(", ", request.Headers.GetValues("Accept")), "Nested Alloha playlists should keep an HLS manifest Accept header.");
    AssertEqual("9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9", request.Headers.GetValues("Accepts-Controls").Single(), "Proxy resource request should keep Alloha Accepts-Controls.");
    AssertEqual("Bearer test-guard-token", request.Headers.GetValues("Authorizations").Single(), "Proxy resource request should keep the Alloha guard token.");
    AssertEqual("https://alloha.yani.tv/?token_movie=demo", request.Headers.Referrer!.AbsoluteUri, "Nested Alloha playlists should keep the iframe page as referer.");
    AssertEqual("https://alloha.yani.tv", string.Join(", ", request.Headers.GetValues("Origin")), "Nested Alloha playlists should keep the iframe page origin.");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSessionAfter403()
{
    var requests = new List<HttpRequestMessage>();
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string iframeUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token&hidden=translation,season,episode";
    const string oldMasterUrl = "https://stream-balancer-alloha-old.example/serial/master.m3u8";
    const string oldNestedUrl = "https://stream-balancer-alloha-old.example/serial/index-f1-v1.m3u8";
    const string newMasterUrl = "https://stream-balancer-alloha-fresh.example/serial/master.m3u8";
    const string newNestedUrl = "https://stream-balancer-alloha-fresh.example/serial/index-f1-v1.m3u8";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        var url = request.RequestUri!.AbsoluteUri;

        if (request.Method == HttpMethod.Get && url == oldNestedUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("<HTML>Error</HTML>")
            };
        }

        if (request.Method == HttpMethod.Get &&
            url.StartsWith("https://alloha.yani.tv/?token_movie=movie-token", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":3,\"id_translation\":222},\"all\":{\"t222\":{\"file\":{\"1\":{\"3\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            url == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "hlsSource": [
                        {
                          "label": "РуАниме / DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "{{newMasterUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newMasterUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-STREAM-INF:BANDWIDTH=5309735
                    index-f1-v1.m3u8
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newNestedUrl)
        {
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Refreshed nested playlist should keep the Alloha iframe page as referer.");

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXTINF:3.003,
                    segment-001.m4s
                    """)
            };
        }

        throw new InvalidOperationException("Unexpected Alloha refresh test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-refresh-1",
        ManifestUrl = oldMasterUrl,
        ManifestText = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5309735
            index-f1-v1.m3u8
            """,
        RefererUrl = iframeUrl,
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = iframeUrl
        },
        IframeUrl = iframeUrl,
        SelectedVoiceName = "РуАниме / DEEP",
        SelectedQuality = 1080,
        Source = new YummyAllohaSource
        {
            MovieToken = "movie-token",
            RequestToken = "req-token",
            TranslationId = 222,
            SeasonNumber = 1,
            EpisodeNumber = 3,
            Hidden = "translation,season,episode",
            RefererUrl = iframeUrl
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };

    AllohaPlaybackService.BuildManifestResponseBody(session, proxyBaseUrl);
    var resourceId = session.ProxyResources.Single(x => x.Value == oldNestedUrl).Key;

    var resource = service.DownloadProxyResourceAsync(
            session,
            resourceId,
            oldNestedUrl,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    var manifestBody = System.Text.Encoding.UTF8.GetString(resource.Content);
    AssertEqual("application/vnd.apple.mpegurl", resource.ContentType, "Refreshed nested playlist should still be served as an HLS manifest.");
    AssertEqual(newMasterUrl, session.ManifestUrl, "403 recovery should refresh the session manifest url.");
    AssertEqual(newNestedUrl, session.ProxyResources[resourceId], "Old proxy resource id should be rebound to the refreshed nested playlist url.");
    AssertEqual(newMasterUrl, session.ProxyResourceReferers[resourceId], "Rebound nested playlist should keep the refreshed master manifest as referer.");
    AssertTrue(manifestBody.Contains(".m4s?sessionId=session-refresh-1&resource=", StringComparison.Ordinal), "Refreshed nested manifest should still be rewritten to local proxy segment urls.");

    var requestUrls = requests.Select(x => x.RequestUri!.AbsoluteUri).ToArray();
    AssertEqual(oldNestedUrl, requestUrls[0], "Initial attempt should hit the stale nested playlist first.");
    AssertTrue(requestUrls.Contains(newMasterUrl), "403 recovery should re-resolve a fresh master manifest.");
    AssertEqual(newNestedUrl, requestUrls[^1], "Retry should hit the rebound nested playlist url after refresh.");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter403()
{
    AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode.Forbidden,
        "<HTML>Error</HTML>",
        "403");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfterAlloha500()
{
    AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode.InternalServerError,
        """
        <HTML>
        <BODY>
        Description: Could not process this request.
        </BODY>
        </HTML>
        """,
        "Alloha 500");
}

static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
    HttpStatusCode initialStatusCode,
    string initialBody,
    string recoveryLabel)
{
    var requests = new List<HttpRequestMessage>();
    const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
    const string iframeUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token&hidden=translation,season,episode";
    const string oldMasterUrl = "https://stream-balancer-alloha-old.example/serial/master.m3u8";
    const string oldNestedUrl = "https://stream-balancer-alloha-old.example/serial/index-f1-v1.m3u8";
    const string oldSegmentUrl = "https://stream-balancer-alloha-old.example/serial/segment-001.m4s";
    const string newMasterUrl = "https://stream-balancer-alloha-fresh.example/serial/master.m3u8";
    const string newSegmentUrl = "https://stream-balancer-alloha-fresh.example/serial/segment-001.m4s";
    const string expectedNestedUrl = "https://stream-balancer-alloha-fresh.example/serial/index-f1-v1.m3u8";
    var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

    var handler = new DelegatingTestHandler(request =>
    {
        requests.Add(CloneRequest(request));
        var url = request.RequestUri!.AbsoluteUri;

        if (request.Method == HttpMethod.Get && url == oldSegmentUrl)
        {
            return new HttpResponseMessage(initialStatusCode)
            {
                Content = new StringContent(initialBody)
            };
        }

        if (request.Method == HttpMethod.Get &&
            url.StartsWith("https://alloha.yani.tv/?token_movie=movie-token", StringComparison.Ordinal))
        {
            var iframeHtml =
                "<!DOCTYPE html>\n" +
                "<html>\n" +
                "<head>\n" +
                $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                "</head>\n" +
                "<body>\n" +
                "<script>\n" +
                "const fileList = JSON.parse('{\"active\":{\"id\":1191328,\"seasons\":1,\"episode\":3,\"id_translation\":222},\"all\":{\"t222\":{\"file\":{\"1\":{\"3\":{\"id\":1191328}}}}}}');\n" +
                "</script>\n" +
                "</body>\n" +
                "</html>";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(iframeHtml)
            };
        }

        if (request.Method == HttpMethod.Post &&
            url == "https://alloha.yani.tv/bnsi/movies/1191328")
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {
                      "hlsSource": [
                        {
                          "label": "РуАниме / DEEP",
                          "audioId": "1",
                          "quality": {
                            "1080": "{{newMasterUrl}}"
                          }
                        }
                      ]
                    }
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newMasterUrl)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    #EXTM3U
                    #EXT-X-STREAM-INF:BANDWIDTH=5309735
                    index-f1-v1.m3u8
                    """)
            };
        }

        if (request.Method == HttpMethod.Get && url == newSegmentUrl)
        {
            AssertEqual(iframeUrl, request.Headers.Referrer!.AbsoluteUri, "Refreshed segment should keep the Alloha iframe page as referer.");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1, 2, 3, 4 })
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp2t");
            return response;
        }

        throw new InvalidOperationException("Unexpected Alloha segment-refresh test request: " + request.RequestUri);
    });

    using var http = new HttpClient(handler);
    var ctor = typeof(AllohaPlaybackService).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
        modifiers: null);
    AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

    var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
    var session = new AllohaPlaybackSession
    {
        SessionId = "session-refresh-2",
        ManifestUrl = oldMasterUrl,
        ManifestText = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5309735
            index-f1-v1.m3u8
            """,
        RefererUrl = iframeUrl,
        RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            ["Authorizations"] = "Bearer test-guard-token",
            ["Origin"] = "https://alloha.yani.tv",
            ["Referer"] = iframeUrl
        },
        IframeUrl = iframeUrl,
        SelectedVoiceName = "РуАниме / DEEP",
        SelectedQuality = 1080,
        Source = new YummyAllohaSource
        {
            MovieToken = "movie-token",
            RequestToken = "req-token",
            TranslationId = 222,
            SeasonNumber = 1,
            EpisodeNumber = 3,
            Hidden = "translation,season,episode",
            RefererUrl = iframeUrl
        },
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
    };

    session.ProxyResources["playlist-old"] = oldNestedUrl;
    session.ProxyResourceReferers["playlist-old"] = oldMasterUrl;
    session.ProxyResourceOriginalReferences["playlist-old"] = "index-f1-v1.m3u8";
    session.ProxyResources["segment-old"] = oldSegmentUrl;
    session.ProxyResourceReferers["segment-old"] = oldNestedUrl;
    session.ProxyResourceOriginalReferences["segment-old"] = "segment-001.m4s";
    session.ProxyResourceParentIds["segment-old"] = "playlist-old";

    var resource = service.DownloadProxyResourceAsync(
            session,
            "segment-old",
            oldSegmentUrl,
            proxyBaseUrl,
            CancellationToken.None)
        .GetAwaiter()
        .GetResult();

    AssertEqual("video/mp2t", resource.ContentType, "Refreshed segment retry should keep the upstream binary media type.");
    AssertEqual(newSegmentUrl, session.ProxyResources["segment-old"], $"Segment {recoveryLabel} recovery should rebind the old segment key to the refreshed segment url.");
    AssertEqual(expectedNestedUrl, session.ProxyResourceReferers["segment-old"], "Segment rebinding should carry the refreshed nested playlist as referer.");
    AssertTrue(!requests.Any(x => x.RequestUri!.AbsoluteUri == expectedNestedUrl), "Parent-chain rebinding should not need to download the refreshed nested playlist just to derive the segment url.");

    var requestUrls = requests.Select(x => x.RequestUri!.AbsoluteUri).ToArray();
    AssertEqual(oldSegmentUrl, requestUrls[0], "Initial segment attempt should hit the stale segment url first.");
    AssertTrue(requestUrls.Contains(newMasterUrl), $"Segment {recoveryLabel} recovery should still refresh the master manifest.");
    AssertEqual(newSegmentUrl, requestUrls[^1], "Segment retry should use the rebound url derived from the refreshed parent chain.");
}

static void YummyKodikStreamUri_ParsesAllohaRequest()
{
    var uri = "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=19312&ep=1&voice=Dream%20Cast&format=hls";
    var parsed = YummyKodikStreamUri.TryParseRequest(uri, out var request);

    AssertTrue(parsed, "Alloha HTTP uri should be parsed.");
    AssertEqual(YummyStreamProviderKind.Alloha, request.Provider, "Provider kind should be Alloha.");
    AssertEqual(19312L, request.AnimeId, "Anime id should be parsed from query.");
    AssertEqual(1, request.Episode, "Episode should be parsed from query.");
    AssertEqual("Dream Cast", request.VoiceName, "Voice should be parsed from query.");
}

static void YummyKodikStreamUri_BuildsAllohaRequestWithEmbeddedSource()
{
    var source = new YummyAllohaSource
    {
        MovieToken = "movie-token",
        RequestToken = "request-token",
        TranslationId = 215,
        SeasonNumber = 4,
        EpisodeNumber = 1,
        Hidden = "translation,season,episode",
        RefererUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=215&season=4&episode=1&token=request-token"
    };

    var uri = YummyKodikStreamUri.BuildAllohaHttpUrl(
        "http://localhost:8096",
        animeId: 15066,
        episode: 1,
        voiceName: "AniLibria",
        source: source);

    AssertTrue(uri.Contains("provider=alloha", StringComparison.Ordinal), "Alloha stream url should target the Alloha provider.");
    AssertTrue(uri.Contains("animeId=15066", StringComparison.Ordinal), "Alloha stream url should keep anime id.");
    AssertTrue(uri.Contains("voice=AniLibria", StringComparison.Ordinal), "Alloha stream url should keep the display voice.");
    AssertTrue(uri.Contains("allohaMovieToken=movie-token", StringComparison.Ordinal), "Alloha stream url should embed the movie token.");
    AssertTrue(uri.Contains("allohaRequestToken=request-token", StringComparison.Ordinal), "Alloha stream url should embed the request token.");
    AssertTrue(uri.Contains("allohaTranslationId=215", StringComparison.Ordinal), "Alloha stream url should embed the translation id.");
    AssertTrue(uri.Contains("allohaSeason=4", StringComparison.Ordinal), "Alloha stream url should embed the season number.");
    AssertTrue(uri.Contains("allohaHidden=translation%2Cseason%2Cepisode", StringComparison.Ordinal), "Alloha stream url should embed the hidden flags.");
    AssertTrue(uri.Contains("allohaRefererUrl=", StringComparison.Ordinal), "Alloha stream url should embed the referer url.");
}

static void YummyKodikStreamUri_TrimsTrailingSlashFromProviderBaseUrl()
{
    var cvh = YummyKodikStreamUri.BuildCvhHttpUrl(
        "http://localhost:8099/",
        animeId: 4861,
        episode: 1,
        voiceName: "AniStar");

    var alloha = YummyKodikStreamUri.BuildAllohaHttpUrl(
        "http://localhost:8099/",
        animeId: 19312,
        episode: 1,
        voiceName: "Dream Cast");

    AssertTrue(cvh.StartsWith("http://localhost:8099/YummyKodik/stream?", StringComparison.Ordinal), "CVH url should not contain a double slash before the plugin endpoint.");
    AssertTrue(alloha.StartsWith("http://localhost:8099/YummyKodik/stream?", StringComparison.Ordinal), "Alloha url should not contain a double slash before the plugin endpoint.");
    AssertFalse(cvh.Contains("8099//YummyKodik", StringComparison.Ordinal), "CVH url should trim a trailing base-url slash.");
    AssertFalse(alloha.Contains("8099//YummyKodik", StringComparison.Ordinal), "Alloha url should trim a trailing base-url slash.");
}

static void JellyfinWebIndexPatcher_InsertsManagedBootstrapBeforeHeadClose()
{
    const string html = "<!doctype html>\r\n<html><head><title>Test</title></head><body></body></html>";
    const string scriptUrl = "/web/ConfigurationPage?name=seriesTranslation.js&v=1.0.0.0";

    var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(html, scriptUrl, out var patchedHtml);

    AssertTrue(changed, "Bootstrap script should be injected into a plain Jellyfin index.html.");
    AssertTrue(patchedHtml.Contains(JellyfinWebIndexPatcher.StartMarker, StringComparison.Ordinal), "Managed bootstrap start marker should be present.");
    AssertTrue(patchedHtml.Contains("ConfigurationPage?name=seriesTranslation.js", StringComparison.Ordinal), "Managed bootstrap should point to the translation widget script.");
    AssertTrue(patchedHtml.Contains("v=1.0.0.0", StringComparison.Ordinal), "Managed bootstrap should carry the cache-busting version.");
    AssertTrue(patchedHtml.IndexOf(JellyfinWebIndexPatcher.StartMarker, StringComparison.Ordinal) < patchedHtml.IndexOf("</head>", StringComparison.OrdinalIgnoreCase), "Managed bootstrap should be inserted before </head>.");
}

static void JellyfinWebIndexPatcher_ReplacesExistingManagedBootstrap()
{
    var original = string.Join(
        "\n",
        "<html><head>",
        JellyfinWebIndexPatcher.BuildManagedSnippet("/web/ConfigurationPage?name=seriesTranslation.js&v=old"),
        "</head><body></body></html>");

    var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
        original,
        "/web/ConfigurationPage?name=seriesTranslation.js&v=new",
        out var patchedHtml);

    AssertTrue(changed, "Managed bootstrap should be updated when the script URL changes.");
    AssertTrue(patchedHtml.Contains("v=new", StringComparison.Ordinal), "Updated bootstrap should contain the new version.");
    AssertFalse(patchedHtml.Contains("v=old", StringComparison.Ordinal), "Updated bootstrap should replace the old version.");
}

static void JellyfinWebIndexPatcher_DoesNotDuplicateBootstrap()
{
    var original = string.Join(
        "\n",
        "<html><head>",
        JellyfinWebIndexPatcher.BuildManagedSnippet("/web/ConfigurationPage?name=seriesTranslation.js&v=stable"),
        "</head><body></body></html>");

    var changed = JellyfinWebIndexPatcher.TryInjectSeriesTranslationScript(
        original,
        "/web/ConfigurationPage?name=seriesTranslation.js&v=stable",
        out var patchedHtml);

    AssertFalse(changed, "Managed bootstrap should not be duplicated when it is already current.");
    AssertEqual(original, patchedHtml, "Unchanged bootstrap should leave index.html intact.");
}

static YummyAnimeResponse BuildSecondSeasonAnime()
{
    return new YummyAnimeResponse
    {
        Title = "Фермерская жизнь в ином мире 2",
        AnimeId = 22446,
        AnimeUrl = "fermerskaya-zhizn-v-inom-mire-2",
        Season = 2,
        RemoteIds = new YummyRemoteIds
        {
            ShikimoriId = 62146
        },
        Type = new YummyAnimeType
        {
            Alias = "tv",
            Name = "Сериал",
            ShortName = "ТВ",
            Value = 1
        },
        Episodes = new YummyEpisodesInfo
        {
            Count = 0,
            Aired = 0
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 4785,
                AnimeUrl = "fermerskaya-zhizn-v-inom-mire",
                Title = "Фермерская жизнь в ином мире",
                Data = new YummyViewingOrderData
                {
                    Id = 3119,
                    Index = 0,
                    Text = "адаптация ранобэ"
                }
            },
            new()
            {
                AnimeId = 22446,
                AnimeUrl = "fermerskaya-zhizn-v-inom-mire-2",
                Title = "Фермерская жизнь в ином мире 2",
                Data = new YummyViewingOrderData
                {
                    Id = 3119,
                    Index = 1,
                    Text = "продолжение"
                }
            }
        }
    };
}

static PluginConfiguration BuildAllohaApiConfig(string apiToken)
{
    return new PluginConfiguration
    {
        AllohaApiToken = apiToken,
        AllohaApiBaseUrl = "https://api.alloha.tv"
    };
}

static YummyAnimeResponse BuildAllohaApiAnime(long animeId, long kpId)
{
    return new YummyAnimeResponse
    {
        AnimeId = animeId,
        RemoteIds = new YummyRemoteIds
        {
            KpId = kpId
        }
    };
}

static string BuildAllohaApiCatalogPayload()
{
    return """
        {
          "status": "success",
          "data": {
            "seasons": {
              "1": {
                "season": 1,
                "episodes": {
                  "1": {
                    "episode": 1,
                    "translation": {
                      "215": {
                        "translation": "Dream Cast",
                        "iframe": "https://larkin-as.stloadi.live/?token_movie=movie-1&translation=215&season=1&episode=1&token=req-1"
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;
}

static YummyAnimeResponse BuildSlimeFourthSeasonAnime()
{
    return new YummyAnimeResponse
    {
        Title = "О моём перерождении в слизь 4",
        AnimeId = 15066,
        AnimeUrl = "o-moem-pererozhdenii-v-sliz-4",
        Season = 2,
        Type = new YummyAnimeType
        {
            Alias = "tv",
            Name = "Сериал",
            ShortName = "ТВ",
            Value = 1
        },
        ViewingOrder = new List<YummyViewingOrderItem>
        {
            new()
            {
                AnimeId = 1012,
                AnimeUrl = "o-moem-pererozhdenii-v-sliz-hinata-sakaguchi",
                Title = "О моём перерождении в слизь: Хината Сакагучи",
                Data = new YummyViewingOrderData
                {
                    Id = 3467,
                    Index = 0,
                    Text = "компиляция сериала"
                }
            },
            new()
            {
                AnimeId = 1009,
                AnimeUrl = "o-moem-pererozhdenii-v-sliz",
                Title = "О моём перерождении в слизь",
                Data = new YummyViewingOrderData
                {
                    Id = 3467,
                    Index = 1,
                    Text = "адаптация ранобэ"
                }
            },
            new()
            {
                AnimeId = 15066,
                AnimeUrl = "o-moem-pererozhdenii-v-sliz-4",
                Title = "О моём перерождении в слизь 4",
                Data = new YummyViewingOrderData
                {
                    Id = 3467,
                    Index = 16,
                    Text = "продолжение"
                }
            }
        }
    };
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message} Expected '{expected}', got '{actual}'.");
    }
}

static void AssertTrue(bool value, string message)
{
    if (!value)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertFalse(bool value, string message)
{
    if (value)
    {
        throw new InvalidOperationException(message);
    }
}

static TException AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException ex)
    {
        return ex;
    }

    throw new InvalidOperationException(message);
}

static HttpRequestMessage CloneRequest(HttpRequestMessage request)
{
    var clone = new HttpRequestMessage(request.Method, request.RequestUri);
    foreach (var header in request.Headers)
    {
        clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    return clone;
}

static string InvokePrepareSeasonDirectory(string seriesRoot, string seasonDir, int seasonNumber)
{
    var helperType = typeof(NfoBuilder).Assembly.GetType("YummyKodik.Tasks.SeasonDirectoryMaintenance");
    AssertTrue(helperType != null, "SeasonDirectoryMaintenance helper should exist for season regression coverage.");

    var method = helperType!.GetMethod(
        "PrepareSeasonDirectory",
        BindingFlags.NonPublic | BindingFlags.Static);

    AssertTrue(method != null, "PrepareSeasonDirectory should be reachable via reflection for regression coverage.");

    return (string)method!.Invoke(null, new object[] { NullLogger.Instance, seriesRoot, seasonDir, seasonNumber })!;
}

static void TryDeleteDirectory(string path)
{
    try
    {
        if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
    catch
    {
    }
}

sealed class DelegatingTestHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

    public DelegatingTestHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        _handler = handler;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return Task.FromResult(_handler(request));
    }
}

sealed class RecordingPassThroughHandler : DelegatingHandler
{
    private readonly Action<string> _log;

    public RecordingPassThroughHandler(HttpMessageHandler innerHandler, Action<string> log)
        : base(innerHandler)
    {
        _log = log;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        _log(
            "request " +
            request.Method +
            " " +
            request.RequestUri +
            " | Accepts-Controls=" +
            JoinHeader(request, "Accepts-Controls") +
            " | Authorizations=" +
            JoinHeader(request, "Authorizations") +
            " | Origin=" +
            JoinHeader(request, "Origin") +
            " | Referer=" +
            request.Headers.Referrer?.AbsoluteUri);

        var response = await base.SendAsync(request, cancellationToken);
        _log(
            "response " +
            (int)response.StatusCode +
            " " +
            request.RequestUri +
            " | Content-Type=" +
            response.Content.Headers.ContentType?.MediaType);
        return response;
    }

    private static string JoinHeader(HttpRequestMessage request, string name)
    {
        return request.Headers.TryGetValues(name, out var values)
            ? string.Join(", ", values)
            : string.Empty;
    }
}
