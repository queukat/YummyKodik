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

    static void KodikClient_RunSearchCacheSharesInflightAndEvictsFaults()
    {
        const string playerUrl = "https://kodikplayer.com/seria/1592110/episode/720p";
        const string searchJson =
            """
            {
              "results": [
                {
                  "link": "//kodikplayer.com/serial/74203/series/720p",
                  "episodes_count": 1,
                  "last_episode": 1,
                  "seasons": { "1": { "episodes": { "1": { "link": "//kodikplayer.com/seria/1592110/episode/720p" } } } },
                  "translation": { "id": 610, "title": "AniLibria.TV", "type": "voice" }
                }
              ]
            }
            """;

        var searchRequests = 0;
        var metrics = new ConcurrentDictionary<string, long>(StringComparer.Ordinal);
        using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref searchRequests);
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
            }

            throw new InvalidOperationException("Unexpected cached search request: " + request.RequestUri);
        }));
        var client = new KodikClient(
            http,
            "test-token",
            (key, delta) => metrics.AddOrUpdate(key, delta, (_, current) => current + delta));

        Task.WhenAll(
                Enumerable.Range(0, 5)
                    .Select(_ => client.GetAnimeInfoAsync("59970", KodikIdType.Shikimori, CancellationToken.None)))
            .GetAwaiter()
            .GetResult();
        client.GetAnimeInfoAsync("59970", KodikIdType.Shikimori, CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(1, searchRequests, "Concurrent and sequential same-key searches should share one upstream request.");
        client.GetAnimeInfoAsync("59971", KodikIdType.Shikimori, CancellationToken.None).GetAwaiter().GetResult();
        client.GetAnimeInfoAsync("59970", KodikIdType.Kinopoisk, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(3, searchRequests, "Search cache key must include both id type and id.");
        AssertTrue(metrics.TryGetValue("kodik.search.cache_hits", out var hits) && hits >= 5, "Shared searches should report cache hits.");

        var freshClient = new KodikClient(http, "test-token", static (_, _) => { });
        freshClient.GetAnimeInfoAsync("59970", KodikIdType.Shikimori, CancellationToken.None).GetAwaiter().GetResult();
        AssertEqual(4, searchRequests, "A fresh Kodik client must not inherit a previous refresh run's cache.");

        var faultSearchRequests = 0;
        using var faultHttp = new HttpClient(new DelegatingTestHandler(request =>
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsoluteUri.StartsWith("https://kodik-api.com/search?", StringComparison.Ordinal))
            {
                faultSearchRequests++;
                return faultSearchRequests == 1
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("upstream failed") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(searchJson) };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == playerUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body></body></html>") };
            }

            throw new InvalidOperationException("Unexpected faulted search request: " + request.RequestUri);
        }));
        var faultClient = new KodikClient(faultHttp, "test-token", static (_, _) => { });

        AssertThrows<KodikException>(
            () => faultClient.GetEpisodeTimingsAsync("59970", KodikIdType.Shikimori, 1, "610", CancellationToken.None)
                .GetAwaiter()
                .GetResult(),
            "A real search failure should be surfaced to the caller.");
        faultClient.GetEpisodeTimingsAsync("59970", KodikIdType.Shikimori, 1, "610", CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        AssertEqual(2, faultSearchRequests, "A faulted search entry should be evicted so the next real call retries upstream.");
    }

    static void KodikClient_RunScriptCacheSharesSuccessAndEvictsAfterPostFailure()
    {
        const string playerUrl = "https://kodikplayer.com/seria/1599999/demo/720p";
        const string scriptUrl = "https://kodikplayer.com/assets/app.player_single.js";
        const string playerHtml =
            """
            <html><head>
              <script>
                var urlParams = '{"d":"kodik.cc","d_sign":"d-sign","pd":"kodikplayer.com","pd_sign":"pd-sign","ref":"","ref_sign":"ref-sign"}';
                player.type = 'seria'; player.hash = '0123456789abcdef0123456789abcdef'; player.id = '1599999';
              </script>
              <script src="/assets/app.player_single.js"></script>
            </head><body></body></html>
            """;
        const string scriptBody = """$.ajax({type:"POST",url:atob("L2Z0b3I="),cache:!1,dataType:"json"})""";
        const string linksJson =
            """
            { "links": { "720": [ { "src": "https://cloud.kodik-storage.example/useruploads/demo/720.mp4:hls:manifest.m3u8" } ] } }
            """;

        var scriptGets = 0;
        var videoPosts = 0;
        using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == playerUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerHtml) };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == scriptUrl)
            {
                Interlocked.Increment(ref scriptGets);
                await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scriptBody) };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor")
            {
                Interlocked.Increment(ref videoPosts);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(linksJson) };
            }

            throw new InvalidOperationException("Unexpected cached script request: " + request.RequestUri);
        }));
        var client = new KodikClient(http, "test-token", static (_, _) => { });
        Task.WhenAll(
                client.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None),
                client.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None))
            .GetAwaiter()
            .GetResult();

        AssertEqual(1, scriptGets, "Concurrent successful script decoding should be single-flight for the current refresh run.");
        AssertEqual(2, videoPosts, "Only script bootstrap should be cached; each player payload must still be resolved.");

        var faultScriptGets = 0;
        var faultVideoPosts = 0;
        using var faultHttp = new HttpClient(new DelegatingTestHandler(request =>
        {
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == playerUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(playerHtml) };
            }

            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == scriptUrl)
            {
                faultScriptGets++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(scriptBody) };
            }

            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://kodikplayer.com/ftor")
            {
                faultVideoPosts++;
                return faultVideoPosts == 1
                    ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("upstream failed") }
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(linksJson) };
            }

            throw new InvalidOperationException("Unexpected invalidated script request: " + request.RequestUri);
        }));
        var faultClient = new KodikClient(faultHttp, "test-token", static (_, _) => { });
        AssertThrows<KodikException>(
            () => faultClient.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None).GetAwaiter().GetResult(),
            "A real video-links POST failure should be surfaced.");
        faultClient.GetPlayerLinkAsync(playerUrl, 1, CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(2, faultScriptGets, "A post-path mapping should be reloaded only after the mapping produced a real POST failure.");
        AssertEqual(2, faultVideoPosts, "The next real caller should retry the failed video-links operation once.");
    }

    static void KodikSupplement_RuntimeProbeUsesDeepValidatedLink()
    {
        const string manifestUrl = "https://cdn.kodik.example/useruploads/demo/720.mp4:hls:manifest.m3u8";
        var requests = new List<HttpRequestMessage>();
        using var http = new HttpClient(new DelegatingTestHandler(request =>
        {
            requests.Add(CloneRequest(request));
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == manifestUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXTINF:6.0,\nsegment.ts\n#EXT-X-ENDLIST\n")
                };
            }

            throw new InvalidOperationException("Runtime reuse attempted an unexpected link-resolution request: " + request.RequestUri);
        }));
        var client = new KodikClient(http, "test-token", NullLogger.Instance, static () => false);
        var translations = new[]
        {
            new KodikTranslation { Id = "610", Name = "AniLibria.TV", Type = "voice", MaxEpisode = 1 }
        };
        var playable = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal)
        {
            ["610"] = new HashSet<int> { 1 }
        };
        var links = new Dictionary<string, Dictionary<int, KodikLinkInfo>>(StringComparer.Ordinal)
        {
            ["610"] = new Dictionary<int, KodikLinkInfo>
            {
                [1] = new KodikLinkInfo("//cdn.kodik.example/useruploads/demo/", 720)
            }
        };

        var seconds = KodikSupplementService.ResolveKodikDurationSecondsAsync(
                NullLogger.Instance,
                client,
                KodikIdType.Shikimori,
                "59970",
                translations,
                playable,
                links,
                1,
                perf: null,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual(6, seconds!.Value, "Runtime probe should parse the manifest reached through the deep-validated link.");
        AssertEqual(1, requests.Count, "Reusing KodikLinkInfo should require only the manifest request.");
    }

    static void KodikPlaybackService_ProxiesHlsResourcesAndCachesSegments()
    {
        const string proxyBaseUrl = "/base/YummyKodik/kodik-proxy";
        const string manifestUrl = "https://cdn.kodik.example/useruploads/demo/720.mp4:hls:manifest.m3u8";
        const string segmentUrl = "https://cdn.kodik.example/useruploads/demo/segment-001.ts";
        const string manifestText =
            """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXTINF:6.000,
            segment-001.ts
            #EXT-X-ENDLIST
            """;
        var segmentBody = new byte[] { 1, 2, 3, 4 };
        var requests = new List<HttpRequestMessage>();

        var handler = new DelegatingTestHandler(request =>
        {
            requests.Add(CloneRequest(request));

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri == manifestUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(manifestText, Encoding.UTF8, "application/vnd.apple.mpegurl")
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri == segmentUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(segmentBody)
                };
            }

            throw new InvalidOperationException("Unexpected Kodik proxy test request: " + request.RequestUri);
        });

        using var http = new HttpClient(handler);
        var service = new KodikPlaybackService(http, NullLogger.Instance);
        var session = KodikPlaybackService.CreateSession(new KodikLinkInfo("//cdn.kodik.example/useruploads/demo/", 720), 720);

        var manifest = service.DownloadProxyResourceAsync(session, "manifest", session.ManifestUrl, proxyBaseUrl, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var manifestBody = Encoding.UTF8.GetString(manifest.Content);

        AssertEqual("application/vnd.apple.mpegurl", manifest.ContentType, "Kodik proxy should preserve HLS manifest content type.");
        AssertTrue(manifestBody.Contains(proxyBaseUrl + "/", StringComparison.Ordinal), "Kodik manifest should retain the Jellyfin base path in root-relative proxy urls.");
        AssertFalse(manifestBody.Contains("://", StringComparison.Ordinal), "Kodik proxy urls embedded in manifests must remain root-relative.");
        AssertTrue(manifestBody.Contains(".ts?sessionId=" + session.SessionId, StringComparison.Ordinal), "Kodik segment proxy urls should keep a playable media extension and session id.");

        var segmentResource = session.ProxyResources.Single(x => x.Value == segmentUrl).Key;
        var firstSegment = service.DownloadProxyResourceAsync(session, segmentResource, segmentUrl, proxyBaseUrl, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        var secondSegment = service.DownloadProxyResourceAsync(session, segmentResource, segmentUrl, proxyBaseUrl, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual("video/mp2t", firstSegment.ContentType, "Kodik TS segments should get a playable content type when upstream omits one.");
        AssertEqual(string.Join(",", segmentBody), string.Join(",", firstSegment.Content), "Kodik proxy should return the segment body.");
        AssertEqual(string.Join(",", segmentBody), string.Join(",", secondSegment.Content), "Kodik proxy should return cached segment bytes on repeat requests.");
        AssertEqual(1, requests.Count(x => x.RequestUri!.AbsoluteUri == segmentUrl), "Repeated Kodik segment requests should hit the one-minute proxy cache.");
    }

    static void KodikPlaybackService_RetriesTransientSegmentFailures()
    {
        var resourceUrl = $"https://cdn.kodik.example/useruploads/retry-{Guid.NewGuid():N}/segment-001.ts";
        var expectedBody = new byte[] { 5, 6, 7, 8 };
        var requestCount = 0;
        var retryDelays = new List<TimeSpan>();

        var handler = new DelegatingTestHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                throw new HttpRequestException("Simulated connection reset.");
            }

            if (requestCount == 2)
            {
                return new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("temporary upstream failure")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBody)
            };
        });

        using var http = new HttpClient(handler);
        var service = new KodikPlaybackService(
            http,
            NullLogger.Instance,
            (delay, _) =>
            {
                retryDelays.Add(delay);
                return Task.CompletedTask;
            });
        var session = KodikPlaybackService.CreateSession(
            new KodikLinkInfo("//cdn.kodik.example/useruploads/retry/", 720),
            720);

        var result = service.DownloadProxyResourceAsync(
                session,
                "retry-segment",
                resourceUrl,
                "/YummyKodik/kodik-proxy",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual(3, requestCount, "Kodik proxy should retry a transport reset and a transient 502.");
        AssertEqual(
            "150,6000",
            string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
            "Kodik proxy should use a short delay after transport failure and the extended delay only after a received 502.");
        AssertEqual(
            string.Join(",", expectedBody),
            string.Join(",", result.Content),
            "Kodik proxy should return the successful retry payload.");
    }

    static void KodikPlaybackService_UsesShortBackoffForTransportFailures()
    {
        var resourceUrl = $"https://cdn.kodik.example/useruploads/transport-{Guid.NewGuid():N}/segment-001.ts";
        var expectedBody = new byte[] { 13, 14, 15, 16 };
        var requestCount = 0;
        var retryDelays = new List<TimeSpan>();
        var handler = new DelegatingTestHandler(_ =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                throw new HttpRequestException("Simulated connection reset.");
            }

            if (requestCount == 2)
            {
                throw new TaskCanceledException("Simulated upstream timeout.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBody)
            };
        });

        using var http = new HttpClient(handler);
        var service = new KodikPlaybackService(
            http,
            NullLogger.Instance,
            (delay, _) =>
            {
                retryDelays.Add(delay);
                return Task.CompletedTask;
            });
        var session = KodikPlaybackService.CreateSession(
            new KodikLinkInfo("//cdn.kodik.example/useruploads/transport/", 720),
            720);

        var result = service.DownloadProxyResourceAsync(
                session,
                "transport-segment",
                resourceUrl,
                "/YummyKodik/kodik-proxy",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual(3, requestCount, "Kodik proxy should preserve its three-request cap for transport failures.");
        AssertEqual(
            "150,300",
            string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
            "Transport resets and timeouts should keep the short retry schedule because the failed request already consumed wait time.");
        AssertEqual(
            string.Join(",", expectedBody),
            string.Join(",", result.Content),
            "Kodik proxy should return the payload after transport recovery.");
    }

    static void KodikPlaybackService_RetriesTransientNotFoundSegments()
    {
        var resourceUrl = $"https://cdn.kodik.example/useruploads/not-found-{Guid.NewGuid():N}/segment-001.ts";
        var expectedBody = new byte[] { 9, 10, 11, 12 };
        var requestCount = 0;
        var retryDelays = new List<TimeSpan>();

        var handler = new DelegatingTestHandler(_ =>
        {
            requestCount++;
            if (requestCount < 3)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expectedBody)
            };
        });

        using var http = new HttpClient(handler);
        var service = new KodikPlaybackService(
            http,
            NullLogger.Instance,
            (delay, _) =>
            {
                retryDelays.Add(delay);
                return Task.CompletedTask;
            });
        var session = KodikPlaybackService.CreateSession(
            new KodikLinkInfo("//cdn.kodik.example/useruploads/not-found/", 720),
            720);

        var result = service.DownloadProxyResourceAsync(
                session,
                "not-found-segment",
                resourceUrl,
                "/YummyKodik/kodik-proxy",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual(3, requestCount, "Kodik proxy should retry a CDN segment that is temporarily unavailable with 404.");
        AssertEqual(
            "2000,6000",
            string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
            "Received transient 404 responses should keep the extended bounded recovery window.");
        AssertEqual(
            string.Join(",", expectedBody),
            string.Join(",", result.Content),
            "Kodik proxy should return a segment after CDN propagation completes.");
    }

    static void KodikPlaybackService_BoundsPersistentTransientFailures()
    {
        var resourceUrl = $"https://cdn.kodik.example/useruploads/persistent-{Guid.NewGuid():N}/manifest.m3u8";
        var requestCount = 0;
        var retryDelays = new List<TimeSpan>();
        var handler = new DelegatingTestHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("persistent upstream failure")
            };
        });

        using var http = new HttpClient(handler);
        var service = new KodikPlaybackService(
            http,
            NullLogger.Instance,
            (delay, _) =>
            {
                retryDelays.Add(delay);
                return Task.CompletedTask;
            });
        var session = KodikPlaybackService.CreateSession(
            new KodikLinkInfo("//cdn.kodik.example/useruploads/persistent/", 720),
            720);

        AssertThrows<InvalidOperationException>(() => service.DownloadProxyResourceAsync(
                session,
                "manifest",
                resourceUrl,
                "/YummyKodik/kodik-proxy",
                CancellationToken.None)
            .GetAwaiter()
            .GetResult(),
            "Persistent Kodik CDN failures should surface after the bounded retry window.");

        AssertEqual(3, requestCount, "Persistent Kodik CDN failures must remain capped at three HTTP requests.");
        AssertEqual(
            "2000,6000",
            string.Join(",", retryDelays.Select(delay => delay.TotalMilliseconds)),
            "Persistent failures should stop after the bounded eight-second recovery window.");
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

    static void KodikTokenResolver_DecodesOnlineModPayload()
    {
        const string expectedToken = "resolved-token";
        var numbers = EncodeKodikSecret(expectedToken, "kodik");
        var script =
            "const endpoint = 'https://kodik-api.com/search';\n" +
            "var token = Utils.decodeSecret([" + string.Join(",", numbers) + "]);";

        var handler = new DelegatingTestHandler(request =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri == KodikTokenResolver.OnlineModUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(script)
                };
            }

            throw new InvalidOperationException("Kodik token resolver should not use fallback when online_mod.js contains a decodable token.");
        });

        using var http = new HttpClient(handler);
        var token = KodikTokenResolver.ResolveTokenAsync(http, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual(expectedToken, token, "Kodik token resolver should decode the online_mod.js payload.");
    }

    static void KodikTitleResolver_NormalizesUnicodeWords()
    {
        var normalized = InvokeStatic<string>(
            typeof(KodikTitleResolver),
            "Normalize",
            " Блич: Thousand-Year Blood War 2!! ");

        AssertEqual("бличthousandyearbloodwar2", normalized, "Kodik title normalization should keep letters and digits across alphabets.");
    }

    static int[] EncodeKodikSecret(string token, string decodeKey)
    {
        var hash = InvokeStatic<string>(typeof(KodikTokenResolver), "Salt", "123456789" + decodeKey);
        var hashBuilder = new StringBuilder(hash);
        while (hashBuilder.Length < token.Length)
        {
            hashBuilder.Append(hash);
        }

        var expandedHash = hashBuilder.ToString();
        return token
            .Select((ch, index) => ch ^ expandedHash[index])
            .ToArray();
    }

    static void KodikClient_PreferredQualityFallsBackToAvailableMaximum()
    {
        var link = new KodikLinkInfo("//cloud.kodik-storage.example/useruploads/demo/", 720);

        var hlsUrl = KodikClient.BuildHlsUrl(link, 1080);
        var mp4Url = KodikClient.BuildMp4Url(link, 1080);

        AssertTrue(hlsUrl.EndsWith("/720.mp4:hls:manifest.m3u8", StringComparison.Ordinal), "Kodik HLS should keep the stream and use its best available quality when 1080p is requested.");
        AssertTrue(mp4Url.EndsWith("/720.mp4", StringComparison.Ordinal), "Kodik MP4 should keep the stream and use its best available quality when 1080p is requested.");
    }

    static void KodikPlaybackSelector_ResolvesSavedVoiceNameToTranslationId()
    {
        var translations = new[]
        {
            new KodikTranslation
            {
                Id = "729",
                Type = "voice",
                Name = "СВ-Дубль",
                AvailableEpisodes = new[] { 41, 42 }
            },
            new KodikTranslation
            {
                Id = "825",
                Type = "voice",
                Name = "AniMaunt",
                AvailableEpisodes = new[] { 41, 42 }
            }
        };

        var selection = KodikPlaybackSelector.PickTranslationForPlayback(
            translations,
            Array.Empty<string>(),
            "СВ-Дубль",
            string.Empty,
            42);

        AssertEqual("729", selection.TranslationId, "A canonical widget voice name should resolve back to the Kodik provider id.");
        AssertEqual("saved-name", selection.Reason, "Kodik selection should report the voice-name compatibility path.");
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
}
