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
    static void ResolveSeasonNumber_UsesViewingOrderIndex()
    {
        var anime = BuildSecondSeasonAnime();

        var season = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, anime.Title);
        AssertEqual(2, season, "Season must come from viewing_order for sequels.");
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

    static void ShikimoriGraphQlClient_DeduplicatesConcurrentSameIdRequests()
    {
        var requestCount = 0;
        using var http = new HttpClient(new AsyncDelegatingTestHandler(async (_, cancellationToken) =>
        {
            Interlocked.Increment(ref requestCount);
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {
                      "data": {
                        "animes": [
                          {
                            "id": "52991",
                            "name": "Sousou no Frieren",
                            "russian": "Провожающая в последний путь Фрирен",
                            "kind": "tv",
                            "related": []
                          }
                        ]
                      }
                    }
                    """)
            };
        }));
        var client = new ShikimoriGraphQlClient(http, "https://shikimori.test/graphql");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => client.TryResolveSeriesLayoutAsync(52991, CancellationToken.None))
            .ToArray();
        Task.WhenAll(tasks).GetAwaiter().GetResult();

        AssertEqual(1, requestCount, "Concurrent same-id Shikimori lookups should share one cached task.");
        AssertTrue(tasks.All(task => task.Result?.SeasonNumber == 1), "All concurrent Shikimori lookups should receive the resolved layout.");
    }
}
