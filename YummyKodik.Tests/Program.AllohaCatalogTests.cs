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
}
