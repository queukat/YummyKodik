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

    static void CvhClient_RejectsInvalidSourceIdentifiers()
    {
        using var http = new HttpClient(new DelegatingTestHandler(_ =>
            throw new InvalidOperationException("CVH validation should fail before any HTTP request.")));
        var client = new CvhClient(http);

        var missingAnimeId = new YummyCvhSource
        {
            AnimeId = 0,
            EpisodeNumber = 1
        };

        var animeIdError = AssertThrows<ArgumentOutOfRangeException>(
            () => client.ResolveEpisodeStreamAsync(missingAnimeId, 720, CancellationToken.None).GetAwaiter().GetResult(),
            "CVH should reject non-positive anime ids.");
        AssertEqual("source", animeIdError.ParamName, "Exception should point to the invalid source object.");

        var missingEpisodeNumber = new YummyCvhSource
        {
            AnimeId = 61549,
            EpisodeNumber = 0
        };

        var episodeError = AssertThrows<ArgumentOutOfRangeException>(
            () => client.ResolveEpisodeStreamAsync(missingEpisodeNumber, 720, CancellationToken.None).GetAwaiter().GetResult(),
            "CVH should reject non-positive episode numbers.");
        AssertEqual("source", episodeError.ParamName, "Exception should point to the invalid source object.");
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

        const string proxyBaseUrl = "/base/YummyKodik/cvh-proxy";
        var manifest = CvhClient.BuildManifestResponseBody(session, proxyBaseUrl);

        AssertTrue(manifest.Contains(proxyBaseUrl + "/", StringComparison.Ordinal), "CVH master manifest should retain the Jellyfin base path in root-relative proxy urls.");
        AssertFalse(manifest.Contains("://", StringComparison.Ordinal), "CVH proxy urls embedded in manifests must remain root-relative.");
        AssertTrue(manifest.Contains(".m3u8?sessionId=cvh-session", StringComparison.Ordinal), "Proxied CVH nested playlist urls should carry an HLS extension and session id.");
        AssertEqual(2, session.ProxyResources.Count, "Each nested CVH playlist url should be tracked as a proxy resource.");
    }
}
