using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Cvh;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Shikimori;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Web;
using YummyKodik.Yummy;
partial class Program
{
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

    static void YummyKodikMediaSourceProvider_RuntimePublicationPolicyRepairsMissingOrShortValues()
    {
        var policyType = typeof(YummyKodikStreamUri).Assembly.GetType("YummyKodik.Media.MediaRunTimePolicy");
        AssertTrue(policyType is not null, "Media-source runtime publication policy should exist.");
        var method = policyType!.GetMethod(
            "ShouldPublish",
            BindingFlags.Static | BindingFlags.Public);
        AssertTrue(method is not null, "Media-source runtime publication policy should exist.");

        bool ShouldPublish(long? current, long? resolved)
        {
            return (bool)method!.Invoke(null, new object?[] { current, resolved })!;
        }

        var episodeRunTime = TimeSpan.FromMinutes(24).Ticks;
        AssertTrue(ShouldPublish(null, episodeRunTime), "A resolved provider runtime should fill a missing Jellyfin item runtime.");
        AssertTrue(
            ShouldPublish(TimeSpan.FromSeconds(7).Ticks, episodeRunTime),
            "A resolved provider runtime should replace the implausibly short first-play runtime.");
        AssertFalse(
            ShouldPublish(TimeSpan.FromMinutes(23).Ticks, episodeRunTime),
            "An already plausible Jellyfin runtime should be preserved.");
        AssertFalse(ShouldPublish(null, null), "A missing provider runtime must not overwrite Jellyfin metadata.");
    }

    static void YummyKodikMediaSourceProvider_AuthoritativeRuntimeCorrectsPlausibleMismatch()
    {
        var staleNfoRuntime = TimeSpan.FromSeconds(1299).Ticks;
        var resolvedHlsRuntime = TimeSpan.FromMilliseconds(1256050).Ticks;

        AssertTrue(
            MediaRunTimePolicy.ShouldPublishAuthoritative(staleNfoRuntime, resolvedHlsRuntime),
            "An exact provider runtime should correct a plausible but materially wrong NFO runtime.");
        AssertFalse(
            MediaRunTimePolicy.ShouldPublishAuthoritative(
                resolvedHlsRuntime + TimeSpan.FromSeconds(1).Ticks,
                resolvedHlsRuntime),
            "Sub-second and rounding-level runtime differences should not rewrite Jellyfin metadata.");
        AssertFalse(
            MediaRunTimePolicy.ShouldPublishAuthoritative(staleNfoRuntime, null),
            "A missing provider runtime must not overwrite a plausible Jellyfin runtime.");
    }

    static void YummyKodikMediaSourceProvider_ResolvesPrimaryOnlyForExplicitSeriesSelection()
    {
        var configuration = new PluginConfiguration();
        configuration.SetUserSeriesPreferredTranslationId(
            Guid.Empty,
            "yummy:123",
            "СВ-Дубль");

        AssertTrue(
            MediaRunTimePolicy.HasExplicitSeriesSelection(
                configuration,
                new[] { "cvh:123", "yummy:123" }),
            "A mirrored widget selection should activate merged-primary source resolution.");
        AssertFalse(
            MediaRunTimePolicy.HasExplicitSeriesSelection(
                configuration,
                new[] { "yummy:456", "shikimori:456" }),
            "A selection for another series must not affect the requested episode.");

        configuration.SetUserSeriesPreferredTranslationId(Guid.Empty, "yummy:123", null);
        AssertFalse(
            MediaRunTimePolicy.HasExplicitSeriesSelection(
                configuration,
                new[] { "yummy:123" }),
            "Auto should disable forced primary source resolution and retain native version selection.");
    }

    static void YummyKodikMediaSourceProvider_FillsMissingSourceRuntimeFromSiblingOrItem()
    {
        var resolvedTicks = TimeSpan.FromMinutes(24).Ticks;
        var sources = new[]
        {
            new MediaSourceInfo { Name = "Missing", RunTimeTicks = null },
            new MediaSourceInfo { Name = "Resolved", RunTimeTicks = resolvedTicks },
            new MediaSourceInfo { Name = "Short", RunTimeTicks = TimeSpan.FromSeconds(7).Ticks }
        };

        var fallback = MediaRunTimePolicy.FillMissingSourceRunTimes(
            itemRunTimeTicks: TimeSpan.FromMinutes(23).Ticks,
            sources);

        AssertEqual(resolvedTicks, fallback!.Value, "A provider duration should be preferred as the source fallback.");
        AssertTrue(
            sources.All(source => source.RunTimeTicks == resolvedTicks),
            "Missing and implausibly short media-source runtimes should inherit a known sibling duration.");

        var itemOnlySource = new MediaSourceInfo { Name = "Item fallback" };
        var itemTicks = TimeSpan.FromMinutes(25).Ticks;
        MediaRunTimePolicy.FillMissingSourceRunTimes(
            itemTicks,
            new[] { itemOnlySource });
        AssertEqual(
            itemTicks,
            itemOnlySource.RunTimeTicks!.Value,
            "A dynamic media source should inherit a plausible Jellyfin item runtime when provider metadata is missing.");
    }

    static void YummyKodikMediaSourceProvider_SharesRuntimeOnlyWithinEpisodeVersionSet()
    {
        const string presentationKey = "episode-presentation-key";
        var seasonDir = Path.Combine("D:\\video\\YummyKodik", "Series", "Season 01");

        AssertTrue(
            MediaRunTimePolicy.IsSiblingEpisodeVersion(
                presentationKey,
                seasonDir,
                presentationKey,
                Path.Combine(seasonDir, "S01E03 - Voice.strm")),
            "A STRM in the same season directory with the same presentation key should receive the runtime.");
        AssertFalse(
            MediaRunTimePolicy.IsSiblingEpisodeVersion(
                presentationKey,
                seasonDir,
                presentationKey,
                Path.Combine("D:\\video\\OtherLibrary", "Season 01", "S01E03.strm")),
            "Runtime propagation must not cross library or season directories.");
        AssertFalse(
            MediaRunTimePolicy.IsSiblingEpisodeVersion(
                presentationKey,
                seasonDir,
                "different-episode",
                Path.Combine(seasonDir, "S01E04.strm")),
            "Runtime propagation must not leak into a different episode presentation key.");
    }

    static void YummyKodikMediaSourceFactory_CarriesRuntimeWithoutReflection()
    {
        var assembly = typeof(YummyKodikStreamUri).Assembly;
        var optionsType = assembly.GetType("YummyKodik.Media.MediaSourceBuildOptions");
        var factoryType = assembly.GetType("YummyKodik.Media.YummyKodikMediaSourceFactory");
        AssertTrue(optionsType is not null, "Media-source build options should exist.");
        AssertTrue(factoryType is not null, "Media-source factory should exist.");

        var options = Activator.CreateInstance(optionsType!, nonPublic: true);
        AssertTrue(options is not null, "Media-source build options should be constructible.");
        optionsType!.GetProperty("ItemId")!.SetValue(options, "item-1");
        optionsType.GetProperty("Episode")!.SetValue(options, 7);
        optionsType.GetProperty("Suffix")!.SetValue(options, "kodik-auto");
        optionsType.GetProperty("Name")!.SetValue(options, "Auto");
        optionsType.GetProperty("Url")!.SetValue(options, "http://127.0.0.1:8096/YummyKodik/stream?ep=7&format=hls");
        optionsType.GetProperty("Container")!.SetValue(options, "m3u8");
        optionsType.GetProperty("SupportsDirectPlay")!.SetValue(options, false);
        optionsType.GetProperty("RunTimeTicks")!.SetValue(options, TimeSpan.FromMinutes(24).Ticks);

        var build = factoryType!.GetMethod("Build", BindingFlags.Static | BindingFlags.Public);
        AssertTrue(build is not null, "Media-source factory Build method should exist.");
        var source = (MediaSourceInfo)build!.Invoke(null, new[] { options })!;

        AssertEqual(VideoType.VideoFile, source.VideoType!.Value, "Dynamic YummyKodik media sources should be classified as regular video.");
        AssertEqual(TimeSpan.FromMinutes(24).Ticks, source.RunTimeTicks!.Value, "Dynamic media source should carry the resolved total episode duration.");
        AssertEqual("m3u8", source.Container, "Dynamic media source should expose the resolved HLS container.");
        AssertFalse(source.SupportsDirectPlay, "Gateway HLS source should not be mistaken for the original static STRM source.");
    }

    static void YummyKodikMediaSegmentProvider_ClonesCachedSegmentsPerItem()
    {
        var templateItemId = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        IReadOnlyList<MediaSegmentDto> template = new[]
        {
            new MediaSegmentDto
            {
                Id = templateId,
                ItemId = templateItemId,
                Type = MediaSegmentType.Intro,
                StartTicks = TimeSpan.FromSeconds(12).Ticks,
                EndTicks = TimeSpan.FromSeconds(88).Ticks
            }
        };

        var cloneA = YummyKodikMediaSegmentCache.CloneSegmentsForItem(template, itemA).Single();
        var cloneB = YummyKodikMediaSegmentCache.CloneSegmentsForItem(template, itemB).Single();

        AssertEqual(itemA, cloneA.ItemId, "Cached segment clones must target the requested item.");
        AssertEqual(itemB, cloneB.ItemId, "Cached segment clones must target each requested item independently.");
        AssertFalse(cloneA.Id == templateId, "Cached segment clone must not reuse the template segment id.");
        AssertFalse(cloneB.Id == templateId, "Cached segment clone must not reuse the template segment id.");
        AssertFalse(cloneA.Id == cloneB.Id, "Cached segment clones for different items must not collide.");
        AssertEqual(template[0].Type, cloneA.Type, "Cached segment clone should preserve segment type.");
        AssertEqual(template[0].StartTicks, cloneA.StartTicks, "Cached segment clone should preserve start ticks.");
        AssertEqual(template[0].EndTicks, cloneA.EndTicks, "Cached segment clone should preserve end ticks.");
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

    static void YummyKodikStreamController_OrdersGatewayFallbackProviders()
    {
        var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
            "GetFallbackProviderOrder",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(method is not null, "Gateway fallback provider ordering helper should exist.");

        var allohaFallback = ((System.Collections.IEnumerable)method!.Invoke(null, new object[] { YummyStreamProviderKind.Alloha })!)
            .Cast<object>()
            .Select(x => x.ToString())
            .ToArray();
        var cvhFallback = ((System.Collections.IEnumerable)method.Invoke(null, new object[] { YummyStreamProviderKind.Cvh })!)
            .Cast<object>()
            .Select(x => x.ToString())
            .ToArray();

        AssertEqual("Kodik,Cvh", string.Join(",", allohaFallback), "Alloha failures should try Kodik before the less reliable CVH transport.");
        AssertEqual("Alloha,Kodik", string.Join(",", cvhFallback), "CVH failures should first try the preferred Yummy provider and then Kodik.");
    }

    static void YummyKodikStreamController_PrioritizesKodikForVoiceMissingFromYummyCatalog()
    {
        var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
            "GetVoiceAwareFallbackProviderOrder",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(method is not null, "Voice-aware gateway fallback ordering helper should exist.");

        var catalog = YummyVideoCatalog.Create(new YummyAnimeResponse
        {
            AnimeId = 12852,
            Videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "42",
                    IframeUrl = "https://play.example/player?anime_id=12852&episode=42&dubbing=AnimeVost",
                    Data = new YummyVideoData
                    {
                        PlayerId = (int)YummyVideoProviderKind.Cvh,
                        Dubbing = "AnimeVost"
                    }
                }
            }
        });

        var fallback = ((System.Collections.IEnumerable)method!.Invoke(
                null,
                new object[] { YummyStreamProviderKind.Cvh, catalog, 42, "СВ-Дубль" })!)
            .Cast<object>()
            .Select(x => x.ToString())
            .ToArray();

        AssertEqual(
            "Kodik,Alloha",
            string.Join(",", fallback),
            "A voice absent from both Yummy-backed providers should go directly to Kodik instead of warning once per provider.");
    }

    static void YummyKodikStreamController_UsesSharedYummyVoicePreferenceAcrossProviders()
    {
        var setMethod = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
            "SetLibraryWideTranslationPreference",
            BindingFlags.NonPublic | BindingFlags.Static);
        var getMethod = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
            "GetSavedYummyVoiceName",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(setMethod is not null, "Shared Yummy voice setter should exist.");
        AssertTrue(getMethod is not null, "Shared Yummy voice getter should exist.");

        var cfg = new PluginConfiguration();
        var userId = Guid.NewGuid();

        var changed = (bool)setMethod!.Invoke(null, new object[] { cfg, new[] { "yummy:21008", "alloha:21008", "cvh:21008" }, "AniStar" })!;
        AssertTrue(changed, "Saving a Yummy voice should change the configuration.");

        var savedForCvh = (string?)getMethod!.Invoke(null, new object[] { cfg, userId, 21008L, YummyStreamProviderKind.Cvh });
        AssertEqual("AniStar", savedForCvh ?? string.Empty, "A voice selected from an Alloha-backed page should be visible to CVH-backed episode URLs.");
        AssertEqual("AniStar", cfg.GetUserSeriesPreferredTranslationId(userId, "yummy:21008") ?? string.Empty, "Shared Yummy preference key should be populated.");
        AssertEqual("AniStar", cfg.GetUserSeriesPreferredTranslationId(userId, "alloha:21008") ?? string.Empty, "Legacy provider key should be populated for compatibility.");

        changed = (bool)setMethod.Invoke(null, new object[] { cfg, new[] { "yummy:21008", "alloha:21008", "cvh:21008" }, string.Empty })!;
        AssertTrue(changed, "Clearing a Yummy voice should remove stored provider preferences.");
        AssertEqual(string.Empty, cfg.GetUserSeriesPreferredTranslationId(userId, "yummy:21008") ?? string.Empty, "Shared Yummy preference key should be cleared.");
        AssertEqual(string.Empty, cfg.GetUserSeriesPreferredTranslationId(userId, "alloha:21008") ?? string.Empty, "Legacy Alloha key should be cleared.");
        AssertEqual(string.Empty, cfg.GetUserSeriesPreferredTranslationId(userId, "cvh:21008") ?? string.Empty, "Legacy CVH key should be cleared.");
    }

    static void YummyKodikStreamController_DetectsSavedVoiceOnNeighborProvider()
    {
        var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
            "ShouldUseDifferentYummyProviderForSavedVoice",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(method is not null, "Saved-voice provider routing helper should exist.");

        var anime = new YummyAnimeResponse
        {
            AnimeId = 21008,
            Videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "1",
                    IframeUrl = "https://larkin-as.stloadi.live/?token_movie=movie&translation=215&season=1&episode=1&token=req",
                    Data = new YummyVideoData
                    {
                        PlayerId = (int)YummyVideoProviderKind.Alloha,
                        Dubbing = "Dream Cast"
                    }
                },
                new()
                {
                    Number = "1",
                    IframeUrl = "https://play.example/player?anime_id=21008&episode=1&dubbing_code=158&dubbing=AniStar",
                    Data = new YummyVideoData
                    {
                        PlayerId = (int)YummyVideoProviderKind.Cvh,
                        Dubbing = "AniStar"
                    }
                }
            }
        };

        var catalog = YummyVideoCatalog.Create(anime);
        var args = new object[] { catalog, YummyVideoProviderKind.Alloha, 1, "AniStar", YummyVideoProviderKind.Unknown };

        var shouldReroute = (bool)method!.Invoke(null, args)!;
        AssertTrue(shouldReroute, "Alloha playback should defer to a neighboring provider when only that provider has the saved voice.");
        AssertEqual(YummyVideoProviderKind.Cvh, (YummyVideoProviderKind)args[4], "Saved voice should resolve to the CVH provider.");
    }

    static void YummyKodikStreamController_NormalizesKnownManagedVoices()
    {
        var options = InvokeStatic<object>(
            typeof(YummyKodik.Api.YummyKodikStreamController),
            "BuildTranslationOptions",
            (IEnumerable<string>)new[] { "AnimeVost", "AniStar", "AniMaunt", "AnimeVost", "СВ-Дубль" });

        var names = ((System.Collections.IEnumerable)options)
            .Cast<object>()
            .Select(x => GetProperty<string>(x, "Name"))
            .ToArray();

        AssertEqual(
            "AnimeVost,AniStar,AniMaunt,СВ-Дубль",
            string.Join(",", names),
            "The widget should expose known managed voices without duplicates.");
    }

    static void YummyKodikStreamController_MirrorsWidgetVoiceAcrossMixedProviderKeys()
    {
        var controllerType = typeof(YummyKodik.Api.YummyKodikStreamController);
        var cfg = new PluginConfiguration();
        var firstUser = Guid.NewGuid();
        var secondUser = Guid.NewGuid();
        var keys = new[] { "yummy:12852", "alloha:12852", "cvh:12852", "shikimori:56215" };

        foreach (var key in keys)
        {
            cfg.SetSeriesPreferredTranslationId(key, "AnimeVost");
        }

        cfg.SetUserSeriesPreferredTranslationId(firstUser, "cvh:12852", "AniStar");
        cfg.SetUserSeriesPreferredTranslationId(secondUser, "shikimori:56215", "729");

        var changed = InvokeStatic<bool>(
            controllerType,
            "SetLibraryWideTranslationPreference",
            cfg,
            (IEnumerable<string>)keys,
            "СВ-Дубль");

        AssertTrue(changed, "Widget selection should update the mixed-provider preference group.");
        foreach (var key in keys)
        {
            AssertEqual(
                "СВ-Дубль",
                cfg.GetSeriesPreferredTranslationId(key) ?? string.Empty,
                "Every provider key should carry the same library-wide widget voice.");
        }

        AssertFalse(
            cfg.UserSeriesPreferredTranslations.Any(x => keys.Contains(x.SeriesKey, StringComparer.OrdinalIgnoreCase)),
            "Stale per-user values for the same library-global primary must be removed.");

        changed = InvokeStatic<bool>(
            controllerType,
            "SetLibraryWideTranslationPreference",
            cfg,
            (IEnumerable<string>)keys,
            string.Empty);

        AssertTrue(changed, "Auto should clear the library-wide voice lock.");
        foreach (var key in keys)
        {
            AssertEqual(
                string.Empty,
                cfg.GetSeriesPreferredTranslationId(key) ?? string.Empty,
                "Auto should clear every provider key in the mixed group.");
        }
    }

    static void YummyKodikStreamController_ExplicitPerVoiceSourceOverridesSavedDefault()
    {
        var controllerType = typeof(YummyKodik.Api.YummyKodikStreamController);

        var cvhVoice = InvokeStatic<string>(
            controllerType,
            "PickGatewayVoice",
            "Dream Cast",
            "AnimeVost");
        AssertEqual(
            "Dream Cast",
            cvhVoice,
            "An explicit per-voice CVH/Alloha STRM must override a stale saved default.");

        var translations = new[]
        {
            new KodikTranslation { Id = "923", Name = "AnimeVost", Type = "voice", AvailableEpisodes = new[] { 7 } },
            new KodikTranslation { Id = "1978", Name = "Dream Cast", Type = "voice", AvailableEpisodes = new[] { 7 } }
        };
        var kodikSelection = KodikPlaybackSelector.PickTranslationForPlayback(
            translations,
            Array.Empty<string>(),
            savedTranslationId: "923",
            explicitTranslationId: "1978",
            episode: 7);
        AssertEqual(
            "1978",
            kodikSelection.TranslationId,
            "An explicit per-voice Kodik STRM must override a stale saved default.");
        AssertEqual("explicit", kodikSelection.Reason, "Kodik selection should report explicit-source precedence.");

        var defaultVoice = InvokeStatic<string>(
            controllerType,
            "PickGatewayVoice",
            string.Empty,
            "AnimeVost");
        AssertEqual("AnimeVost", defaultVoice, "A saved voice should remain the default when a source has no explicit voice.");
    }

    static void PostRefreshMergeBarrier_MissingOrUnindexedEpisodeIsUnresolved()
    {
        var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
            @"C:\YummyKodik\Series\Season 01\S01E01.strm",
            refreshStartedUtc.AddSeconds(4));

        var missing = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            new[] { artifact },
            Array.Empty<YummyKodik.Versioning.EpisodeReadinessSnapshot>(),
            refreshStartedUtc);
        AssertFalse(missing.IsReady, "A refreshed STRM without a Jellyfin Episode must keep the post-refresh merge barrier unresolved.");
        AssertEqual(1, missing.UnresolvedPaths.Count, "Missing Episode should be reported as one unresolved artifact.");

        var unindexed = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            new[] { artifact },
            new[]
            {
                new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                    artifact.Path,
                    IndexNumber: null,
                    DateCreatedUtc: refreshStartedUtc,
                    DateLastRefreshedUtc: refreshStartedUtc.AddSeconds(5))
            },
            refreshStartedUtc);
        AssertFalse(unindexed.IsReady, "An Episode without IndexNumber must not release the post-refresh merge barrier.");
        AssertEqual(1, unindexed.UnresolvedPaths.Count, "Unindexed Episode should be reported as unresolved.");
    }

    static void PostRefreshMergeBarrier_NewEpisodeBeforeArtifactRefreshIsUnresolved()
    {
        var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
            @"C:\YummyKodik\Series\Season 01\S01E01.strm",
            refreshStartedUtc.AddSeconds(10));

        var readiness = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            new[] { artifact },
            new[]
            {
                new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                    artifact.Path,
                    IndexNumber: 1,
                    DateCreatedUtc: refreshStartedUtc.AddSeconds(1),
                    DateLastRefreshedUtc: refreshStartedUtc.AddSeconds(5))
            },
            refreshStartedUtc);

        AssertFalse(readiness.IsReady, "A newly materialized Episode whose refresh predates the generated STRM/NFO timestamp must remain unresolved.");
        AssertEqual(1, readiness.UnresolvedPaths.Count, "Early refreshed Episode should keep its artifact unresolved.");
    }

    static void PostRefreshMergeBarrier_LateRefreshedEpisodeIsReady()
    {
        var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
            @"C:\YummyKodik\Series\Season 01\S01E01.strm",
            refreshStartedUtc.AddSeconds(10));

        var readiness = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            new[] { artifact },
            new[]
            {
                new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                    artifact.Path,
                    IndexNumber: 1,
                    DateCreatedUtc: refreshStartedUtc.AddSeconds(1),
                    DateLastRefreshedUtc: refreshStartedUtc.AddSeconds(12))
            },
            refreshStartedUtc);

        AssertTrue(readiness.IsReady, "The late Episode refresh after generated artifact timestamps must release the barrier for one final merge.");
        AssertEqual(0, readiness.UnresolvedPaths.Count, "Ready Episode should leave no unresolved artifact paths.");
    }

    static void PostRefreshMergeBarrier_PreExistingEpisodeDoesNotBlockReadiness()
    {
        var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        var artifact = new YummyKodik.Versioning.ExpectedEpisodeArtifact(
            @"C:\YummyKodik\Series\Season 01\S01E01.strm",
            refreshStartedUtc.AddSeconds(10));

        var readiness = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            new[] { artifact },
            new[]
            {
                new YummyKodik.Versioning.EpisodeReadinessSnapshot(
                    artifact.Path,
                    IndexNumber: 1,
                    DateCreatedUtc: refreshStartedUtc.AddMinutes(-30),
                    DateLastRefreshedUtc: refreshStartedUtc.AddMinutes(-30))
            },
            refreshStartedUtc);

        AssertTrue(readiness.IsReady, "A pre-existing indexed Episode must not block the post-refresh merge barrier merely because its prior refresh timestamp is old.");
    }

    static void PostRefreshMergeBarrier_DeletedEpisodeMustDisappear()
    {
        var refreshStartedUtc = new DateTime(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);
        const string deletedPath = @"C:\YummyKodik\Series\Season 01\S01E01 - Removed.strm";
        var staleSnapshot = new YummyKodik.Versioning.EpisodeReadinessSnapshot(
            deletedPath,
            IndexNumber: 1,
            DateCreatedUtc: refreshStartedUtc.AddDays(-1),
            DateLastRefreshedUtc: refreshStartedUtc.AddDays(-1));

        var stillPresent = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            Array.Empty<YummyKodik.Versioning.ExpectedEpisodeArtifact>(),
            new[] { staleSnapshot },
            refreshStartedUtc,
            new[] { deletedPath });
        AssertFalse(stillPresent.IsReady, "A deleted STRM still present in Jellyfin must keep the final merge barrier unresolved.");

        var removed = YummyKodik.Versioning.YummyKodikPostRefreshMergeBarrier.EvaluatePostRefreshReadiness(
            Array.Empty<YummyKodik.Versioning.ExpectedEpisodeArtifact>(),
            Array.Empty<YummyKodik.Versioning.EpisodeReadinessSnapshot>(),
            refreshStartedUtc,
            new[] { deletedPath });
        AssertTrue(removed.IsReady, "The barrier should release after Jellyfin removes the deleted Episode item.");
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

    static void YummyKodikStreamController_KodikFallbackUsesDefaultWhenRequestedVoiceMissing()
    {
        var method = typeof(YummyKodik.Api.YummyKodikStreamController).GetMethod(
            "PickKodikFallbackSelection",
            BindingFlags.NonPublic | BindingFlags.Static);
        AssertTrue(method is not null, "Kodik fallback selector should exist.");

        var translations = new[]
        {
            new KodikTranslation
            {
                Id = "610",
                Type = "voice",
                Name = "Dream Cast",
                AvailableEpisodes = new[] { 12 }
            }
        };

        var selection = method!.Invoke(
            null,
            new object[] { translations, Array.Empty<string>(), null!, "AniLibria", string.Empty, 12 });
        if (selection == null)
        {
            throw new InvalidOperationException("Kodik fallback should select an available translation when the requested Yummy voice is missing.");
        }

        var translationId = selection.GetType().GetProperty("TranslationId")?.GetValue(selection)?.ToString() ?? string.Empty;
        var reason = selection.GetType().GetProperty("Reason")?.GetValue(selection)?.ToString() ?? string.Empty;

        AssertEqual("610", translationId, "Missing cross-provider voice match should not block fallback to an available Kodik translation.");
        AssertTrue(!string.Equals("fallback-explicit-voice", reason, StringComparison.Ordinal), "Fallback reason should not claim an explicit voice match when none exists.");
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

    static void EpisodeVersionsMerge_UsesSavedYummyVoicePreferenceForPrimary()
    {
        var serviceType = typeof(YummyKodik.Versioning.YummyKodikEpisodeVersionsMergeHostedService);
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var dreamPath = Path.Combine(tempRoot, "S01E01 - Dream Cast.strm");
            var aniPath = Path.Combine(tempRoot, "S01E01 - AniLibria.strm");
            File.WriteAllText(dreamPath, "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=21008&ep=1&voice=Dream%20Cast");
            File.WriteAllText(aniPath, "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=21008&ep=1&voice=AniLibria");

            var cfg = new PluginConfiguration();
            var userId = Guid.NewGuid();
            AssertTrue(
                cfg.SetUserSeriesPreferredTranslationId(userId, "yummy:21008", "Dream Cast"),
                "Saved Yummy voice should be stored for the shared series key.");

            var tokens = InvokeStatic<string[]>(
                serviceType,
                "BuildSavedPreferenceTokensForPaths",
                (IEnumerable<string>)new[] { dreamPath, aniPath },
                cfg);

            AssertEqual("Dream Cast", string.Join(",", tokens), "Version merge should find the saved Yummy voice for this group.");

            var needleSafe = InvokeStatic<string>(serviceType, "NormalizeTokenForFilename", "Dream Cast");
            var matchesDream = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", dreamPath, "Dream Cast", needleSafe);
            var matchesAni = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", aniPath, "Dream Cast", needleSafe);

            AssertTrue(matchesDream, "Saved Yummy voice should match the corresponding voice STRM.");
            AssertFalse(matchesAni, "Saved Yummy voice should not match a different voice STRM.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void EpisodeVersionsMerge_MatchesSavedKodikTranslationIdFromStrm()
    {
        var serviceType = typeof(YummyKodik.Versioning.YummyKodikEpisodeVersionsMergeHostedService);
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(tempRoot);
            var kodikPath = Path.Combine(tempRoot, "S01E01 - AniLibria.strm");
            var otherPath = Path.Combine(tempRoot, "S01E01 - Other.strm");
            File.WriteAllText(kodikPath, "http://localhost:8096/YummyKodik/stream?type=shikimori&id=52991&ep=1&tr=610");
            File.WriteAllText(otherPath, "http://localhost:8096/YummyKodik/stream?type=shikimori&id=52991&ep=1&tr=611");

            var cfg = new PluginConfiguration();
            var userId = Guid.NewGuid();
            AssertTrue(
                cfg.SetUserSeriesPreferredTranslationId(userId, "shikimori:52991", "610"),
                "Saved Kodik translation id should be stored for the series key.");

            var tokens = InvokeStatic<string[]>(
                serviceType,
                "BuildSavedPreferenceTokensForPaths",
                (IEnumerable<string>)new[] { kodikPath, otherPath },
                cfg);

            AssertEqual("610", string.Join(",", tokens), "Version merge should find the saved Kodik translation id for this group.");

            var matchesSaved = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", kodikPath, "610", "610");
            var matchesOther = InvokeStatic<bool>(serviceType, "PathMatchesPreferredToken", otherPath, "610", "610");

            AssertTrue(matchesSaved, "Saved Kodik translation id should match the STRM tr query.");
            AssertFalse(matchesOther, "Saved Kodik translation id should not match another tr query.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void EpisodeVersionsMerge_ComparesJellyfin12LinksByItemId()
    {
        var serviceType = typeof(YummyKodik.Versioning.YummyKodikEpisodeVersionsMergeHostedService);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var existing = new[]
        {
            new LinkedChild { ItemId = firstId, Type = LinkedChildType.LinkedAlternateVersion },
            new LinkedChild { ItemId = secondId, Type = LinkedChildType.LinkedAlternateVersion }
        };
        var reordered = new[]
        {
            new LinkedChild { ItemId = secondId, Type = LinkedChildType.LinkedAlternateVersion },
            new LinkedChild { ItemId = firstId, Type = LinkedChildType.LinkedAlternateVersion }
        };
        var different = new[]
        {
            new LinkedChild { ItemId = firstId, Type = LinkedChildType.LinkedAlternateVersion },
            new LinkedChild { ItemId = Guid.NewGuid(), Type = LinkedChildType.LinkedAlternateVersion }
        };

        AssertTrue(
            InvokeStatic<bool>(serviceType, "LinkedChildrenSetEquals", existing, reordered),
            "Jellyfin 12 linked children should compare by ItemId regardless of order.");
        AssertFalse(
            InvokeStatic<bool>(serviceType, "LinkedChildrenSetEquals", existing, different),
            "Different Jellyfin 12 linked child ids must not compare as the same version set.");
    }
}
