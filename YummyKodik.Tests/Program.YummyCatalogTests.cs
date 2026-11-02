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

    static void YummyClient_GetUserListAsync_AcceptsValidEmptyResponse()
    {
        var handler = new DelegatingTestHandler(request =>
        {
            AssertEqual(HttpMethod.Get, request.Method, "Yummy user-list request should use GET.");
            AssertEqual("https://yummy.test/users/42/lists/0", request.RequestUri!.AbsoluteUri, "Yummy user-list request should target the requested user and list.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":[]}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var client = new YummyClient(http, "test-client", "https://yummy.test");
        var items = client.GetUserListAsync(42, 0, CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(0, items.Count, "A syntactically complete empty user list is a valid successful response.");
    }

    static void YummyClient_GetUserListAsync_RejectsMissingResponseArray()
    {
        var handler = new DelegatingTestHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });

        using var http = new HttpClient(handler);
        var client = new YummyClient(http, "test-client", "https://yummy.test");

        var error = AssertThrows<InvalidOperationException>(
            () => client.GetUserListAsync(42, 0, CancellationToken.None).GetAwaiter().GetResult(),
            "A 2xx user-list response without the response array must fail rather than open the cleanup gate.");
        AssertTrue(error.Message.Contains("response", StringComparison.OrdinalIgnoreCase), "Missing response-array error should identify the incomplete payload.");
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
        AssertEqual("AniStar,Dream Cast", string.Join(",", catalog.GetAllVoiceNamesAcrossProviders(providers)), "Combined voice picker list should include voices from all configured providers.");
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
        AssertEqual(
            "AniLibria.TV",
            TranslationNameKeyNormalizer.FindEquivalent(
                "AniLibria",
                new[] { "AnimeVost", "AniLibria.TV" }),
            "A saved canonical voice should resolve to the actual catalog option id used for widget highlighting.");
        AssertEqual(
            "РуАниме / DEEP",
            TranslationNameKeyNormalizer.FindEquivalent(
                "РуАниме _ DEEP",
                new[] { "РуАниме / DEEP", "AniStar & DEEP" }),
            "Provider punctuation variants should resolve to the actual catalog option id.");
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
}
