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
        var proxyBaseUrl = "/base/YummyKodik/alloha-proxy";
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
        AssertTrue(manifestBody.Contains($"{proxyBaseUrl}/", StringComparison.Ordinal), "Alloha manifest should retain the Jellyfin base path in root-relative proxy urls.");
        AssertFalse(manifestBody.Contains("://", StringComparison.Ordinal), "Alloha proxy urls embedded in manifests must remain root-relative.");
        AssertTrue(manifestBody.Contains($".ts?sessionId={session.SessionId}&resource=", StringComparison.Ordinal), "Alloha segment proxy urls should keep a playable media extension.");
        AssertTrue(session.ProxyResources.Values.Contains("https://stream-balancer-alloha.example/serial/segment-001.ts"), "Relative Alloha manifest urls should be registered as proxy resources.");
        AssertTrue(session.ProxyResources.Values.Contains("https://cdn.example/segment-002.ts"), "Absolute Alloha manifest urls should be registered as proxy resources.");
        AssertEqual(3, requests.Count, "Alloha browserless flow should perform iframe, bnsi, and manifest requests.");
    }

    static void AllohaPlaybackService_SharesOnlyInFlightResolution()
    {
        const string iframeUrl = "https://alloha.yani.tv/?token_movie=single-flight-movie&translation=410&season=1&episode=7&token=single-flight-token";
        const string bnsiUrl = "https://alloha.yani.tv/bnsi/movies/1410007";
        const string manifestBaseUrl = "https://stream-balancer-alloha.example/single-flight";
        const string viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
        var iframeRequests = 0;
        var bnsiRequests = 0;
        var manifestRequests = 0;
        var firstResolutionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstResolution = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Get &&
                url.StartsWith("https://alloha.yani.tv/?token_movie=single-flight-movie", StringComparison.Ordinal))
            {
                var requestNumber = Interlocked.Increment(ref iframeRequests);
                if (requestNumber == 1)
                {
                    firstResolutionStarted.TrySetResult(true);
                    await releaseFirstResolution.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<!DOCTYPE html><html><head>" +
                        $"<meta name=\"viewporti\" content=\"{viewporti}\">" +
                        "</head><body><script>" +
                        "const fileList = JSON.parse('{\"active\":{\"id\":1410007,\"seasons\":1,\"episode\":7,\"id_translation\":410},\"all\":{\"t410\":{\"file\":{\"1\":{\"7\":{\"id\":1410007}}}}}}');" +
                        "</script></body></html>")
                };
            }

            if (request.Method == HttpMethod.Post && url == bnsiUrl)
            {
                var requestNumber = Interlocked.Increment(ref bnsiRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        {
                          "hlsSource": [
                            {
                              "quality": {
                                "1080": "{{manifestBaseUrl}}/master-{{requestNumber}}.m3u8"
                              }
                            }
                          ]
                        }
                        """)
                };
            }

            if (request.Method == HttpMethod.Get && url.StartsWith(manifestBaseUrl + "/master-", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref manifestRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXTINF:3.0,\nsegment-001.ts\n#EXT-X-ENDLIST")
                };
            }

            throw new InvalidOperationException("Unexpected Alloha single-flight test request: " + request.RequestUri);
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
        var source = new YummyAllohaSource
        {
            MovieToken = "single-flight-movie",
            RequestToken = "single-flight-token",
            TranslationId = 410,
            SeasonNumber = 1,
            EpisodeNumber = 7,
            RefererUrl = iframeUrl
        };

        var firstSessionTask = service.CreateSessionAsync(source, 1080, CancellationToken.None);
        AssertTrue(firstResolutionStarted.Task.Wait(1000), "The first Alloha resolution should reach the upstream iframe request.");
        var concurrentSessionTask = service.CreateSessionAsync(source, 1080, CancellationToken.None);
        releaseFirstResolution.TrySetResult(true);
        var concurrentSessions = Task.WhenAll(firstSessionTask, concurrentSessionTask).GetAwaiter().GetResult();

        AssertEqual(1, iframeRequests, "Concurrent Alloha sessions should share one in-flight iframe request.");
        AssertEqual(1, bnsiRequests, "Concurrent Alloha sessions should share one in-flight BNSI request.");
        AssertEqual(1, manifestRequests, "Concurrent Alloha sessions should share one in-flight manifest request.");
        AssertEqual(concurrentSessions[0].ManifestUrl, concurrentSessions[1].ManifestUrl, "Concurrent sessions should use the same freshly resolved payload.");

        var sequentialSession = service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(2, iframeRequests, "A sequential Alloha session must resolve a fresh volatile payload.");
        AssertEqual(2, bnsiRequests, "A sequential Alloha session must not reuse the previous BNSI payload.");
        AssertEqual(2, manifestRequests, "A sequential Alloha session must validate a fresh manifest.");
        AssertTrue(
            !string.Equals(concurrentSessions[0].ManifestUrl, sequentialSession.ManifestUrl, StringComparison.Ordinal),
            "A sequential session must not inherit the previous session's token-bearing manifest.");
    }

    static void AllohaPlaybackService_DoesNotResolveDynamicTokenAfterSuccessfulManifest()
    {
        var resolverCalls = 0;
        var iframeUrl = "https://alloha.yani.tv/?token_movie=success-movie&translation=340&season=4&episode=12&token=req-token";
        var manifestUrl = "https://e1-72-f3-r402.vkvideo.cloud/success/master.m3u8";
        var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

        var handler = new DelegatingTestHandler(request =>
        {
            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=success-movie", StringComparison.Ordinal))
            {
                var iframeHtml =
                    "<!DOCTYPE html>\n" +
                    "<html>\n" +
                    "<head>\n" +
                    $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                    "</head>\n" +
                    "<body>\n" +
                    "<script>\n" +
                    "const fileList = JSON.parse('{\"active\":{\"id\":1191500,\"seasons\":4,\"episode\":12,\"id_translation\":340},\"all\":{\"t340\":{\"file\":{\"4\":{\"12\":{\"id\":1191500}}}}}}');\n" +
                    "</script>\n" +
                    "</body>\n" +
                    "</html>";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(iframeHtml)
                };
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191500")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        {
                          "pnr": "wss://alloha-ws.example/socket",
                          "pnk": "ws-session-success",
                          "hlsSource": [
                            {
                              "label": "AniMaunt",
                              "audioId": "2",
                              "quality": {
                                "1080": "{{manifestUrl}}"
                              }
                            }
                          ]
                        }
                        """)
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri == manifestUrl)
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

            throw new InvalidOperationException("Unexpected Alloha successful-manifest test request: " + request.RequestUri);
        });

        using var http = new HttpClient(handler);
        Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> resolver = (_, _) =>
        {
            resolverCalls++;
            return Task.FromResult<string?>("unexpected-token");
        };
        var ctor = typeof(AllohaPlaybackService)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(x => x.GetParameters().Length == 3);
        AssertTrue(ctor is not null, "Alloha internal test constructor should allow injecting stream token resolver.");

        var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http, resolver });
        var source = new YummyAllohaSource
        {
            MovieToken = "success-movie",
            RequestToken = "req-token",
            TranslationId = 340,
            SeasonNumber = 4,
            EpisodeNumber = 12,
            RefererUrl = iframeUrl
        };

        var session = service.CreateSessionAsync(source, 1080, "AniMaunt", CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(manifestUrl, session.ManifestUrl, "Alloha should use the successful manifest response.");
        AssertEqual(string.Empty, session.StreamToken, "Successful manifest setup should not wait for a dynamic stream token.");
        AssertEqual(0, resolverCalls, "Successful manifest setup should not call the optional dynamic token resolver.");
    }

    static void AllohaPlaybackService_RetriesManifestWithDynamicStreamTokenAfter403()
    {
        var requests = new List<HttpRequestMessage>();
        var resolverRequests = new List<AllohaStreamTokenRequest>();
        var iframeUrl = "https://alloha.yani.tv/?token_movie=retry-movie&translation=163&season=1&episode=1&token=retry-token";
        var manifestUrl = "https://e1-72-f3-r402.vkvideo.cloud/demo/master.m3u8";
        var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";

        var handler = new DelegatingTestHandler(request =>
        {
            requests.Add(CloneRequest(request));

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri.StartsWith("https://alloha.yani.tv/?token_movie=retry-movie", StringComparison.Ordinal))
            {
                var iframeHtml =
                    "<!DOCTYPE html>\n" +
                    "<html>\n" +
                    "<head>\n" +
                    $"    <meta name=\"viewporti\" content=\"{viewporti}\">\n" +
                    "</head>\n" +
                    "<body>\n" +
                    "<script>\n" +
                    "const fileList = JSON.parse('{\"active\":{\"id\":1191400,\"seasons\":1,\"episode\":1,\"id_translation\":163},\"all\":{\"t163\":{\"file\":{\"1\":{\"1\":{\"id\":1191400}}}}}}');\n" +
                    "</script>\n" +
                    "</body>\n" +
                    "</html>";

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(iframeHtml)
                };
            }

            if (request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsoluteUri == "https://alloha.yani.tv/bnsi/movies/1191400")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        {
                          "pnr": "wss://alloha-ws.example/socket",
                          "pnk": "ws-session-1",
                          "hlsSource": [
                            {
                              "label": "AniMaunt",
                              "audioId": "7",
                              "quality": {
                                "1080": "{{manifestUrl}}"
                              }
                            }
                          ]
                        }
                        """)
                };
            }

            if (request.Method == HttpMethod.Get &&
                request.RequestUri!.AbsoluteUri == manifestUrl)
            {
                var acceptsControls = request.Headers.TryGetValues("Accepts-Controls", out var values)
                    ? values.Single()
                    : string.Empty;

                if (acceptsControls == "dynamic-edge-token")
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

                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<html><body>403 Forbidden</body></html>")
                };
            }

            throw new InvalidOperationException("Unexpected Alloha retry test request: " + request.RequestUri);
        });

        using var http = new HttpClient(handler);
        Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> resolver = (request, _) =>
        {
            resolverRequests.Add(request);
            return Task.FromResult<string?>("dynamic-edge-token");
        };
        var ctor = typeof(AllohaPlaybackService)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .SingleOrDefault(x => x.GetParameters().Length == 3);
        AssertTrue(ctor is not null, "Alloha internal test constructor should allow injecting stream token resolver.");

        var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http, resolver });
        var source = new YummyAllohaSource
        {
            MovieToken = "retry-movie",
            RequestToken = "retry-token",
            TranslationId = 163,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            RefererUrl = iframeUrl
        };

        var session = service.CreateSessionAsync(source, 1080, "AniMaunt", CancellationToken.None).GetAwaiter().GetResult();
        var manifestRequests = requests
            .Where(x => x.Method == HttpMethod.Get && x.RequestUri!.AbsoluteUri == manifestUrl)
            .ToArray();

        AssertEqual(manifestUrl, session.ManifestUrl, "Alloha should keep the manifest selected before retry.");
        AssertEqual("dynamic-edge-token", session.StreamToken, "Successful 403 recovery should keep the dynamic stream token for proxied resources.");
        AssertEqual(2, manifestRequests.Length, "Alloha manifest download should retry once after upstream 403.");
        AssertEqual(
            "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9",
            manifestRequests[0].Headers.GetValues("Accepts-Controls").Single(),
            "Initial Alloha manifest request should keep the static guard header.");
        AssertEqual(
            "dynamic-edge-token",
            manifestRequests[1].Headers.GetValues("Accepts-Controls").Single(),
            "Retried Alloha manifest request should use the dynamic stream token.");
        AssertEqual(1, resolverRequests.Count, "Alloha should resolve the dynamic stream token once after manifest 403.");
        AssertEqual("wss://alloha-ws.example/socket", resolverRequests[0].WebSocketBaseUrl, "Token request should include the BNSI websocket endpoint.");
        AssertEqual("ws-session-1", resolverRequests[0].WebSocketSessionId, "Token request should include the BNSI websocket session id.");
        AssertEqual("7", resolverRequests[0].AudioTrackId, "Token request should include the selected audio track.");
        AssertEqual(1080, resolverRequests[0].SelectedQuality, "Token request should include the selected manifest quality.");
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

    static void AllohaPlaybackService_RejectsUntrustedIframeUrl()
    {
        var handler = new DelegatingTestHandler(_ =>
        {
            throw new InvalidOperationException("Untrusted Alloha iframe URLs must be rejected before sending a request.");
        });

        using var http = new HttpClient(handler);
        var ctor = typeof(AllohaPlaybackService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
            modifiers: null);
        AssertTrue(ctor is not null, "Alloha internal test constructor should exist for URL validation scenarios.");

        var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
        var source = new YummyAllohaSource
        {
            MovieToken = "movie",
            RequestToken = "request",
            TranslationId = 215,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            RefererUrl = "https://127.0.0.1:9443/private?token_movie=movie&translation=215&season=1&episode=1&token=request"
        };

        var ex = AssertThrows<InvalidOperationException>(
            () => service.CreateSessionAsync(source, 1080, CancellationToken.None).GetAwaiter().GetResult(),
            "Alloha should reject iframe URLs outside the known upstream hosts.");
        AssertTrue(ex.Message.Contains("Alloha iframe URL is not allowed", StringComparison.Ordinal), "Exception should explain that the iframe URL is not allowed.");
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

    static void AllohaPlaybackService_RewritesManifestUrisToProxyUrls()
    {
        const string proxyBaseUrl = "/base/YummyKodik/alloha-proxy";
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

        AssertTrue(manifestBody.Contains($"{proxyBaseUrl}/", StringComparison.Ordinal), "Master manifest should retain the Jellyfin base path in root-relative proxy urls.");
        AssertFalse(manifestBody.Contains("://", StringComparison.Ordinal), "Alloha master manifest proxy urls must remain root-relative.");
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

    static void AllohaPlaybackService_BuffersUpcomingMediaSegments()
    {
        const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
        const string playlistResourceId = "playlist-buffer";
        const string parentManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8";
        const string playlistUrl = "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8";
        const string segment1Url = "https://stream-balancer-alloha.example/serial/segment-001.m4s";
        const string segment2Url = "https://stream-balancer-alloha.example/serial/segment-002.m4s";
        const string segment3Url = "https://stream-balancer-alloha.example/serial/segment-003.m4s";
        const string segment4Url = "https://stream-balancer-alloha.example/serial/segment-004.m4s";
        var segmentBodies = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [segment1Url] = new byte[] { 1, 1, 1 },
            [segment2Url] = new byte[] { 2, 2, 2 },
            [segment3Url] = new byte[] { 3, 3, 3 },
            [segment4Url] = new byte[] { 4, 4, 4 }
        };
        var segmentRequestCounts = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        var handler = new DelegatingTestHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Get && url == playlistUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        #EXTM3U
                        #EXT-X-TARGETDURATION:60
                        #EXTINF:60.000,
                        segment-001.m4s
                        #EXTINF:60.000,
                        segment-002.m4s
                        #EXTINF:60.000,
                        segment-003.m4s
                        #EXTINF:60.000,
                        segment-004.m4s
                        #EXT-X-ENDLIST
                        """)
                };
            }

            if (request.Method == HttpMethod.Get && segmentBodies.TryGetValue(url, out var body))
            {
                var requestCount = segmentRequestCounts.AddOrUpdate(url, 1, (_, count) => count + 1);
                if (requestCount > 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = new StringContent("<html><body>403 Forbidden</body></html>")
                    };
                }

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(body)
                };
                response.Content.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue("video/iso.segment");
                return response;
            }

            throw new InvalidOperationException("Unexpected Alloha buffer test request: " + request.RequestUri);
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
            SessionId = "session-buffer-1",
            ManifestUrl = parentManifestUrl,
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
        session.ProxyResources[playlistResourceId] = playlistUrl;
        session.ProxyResourceReferers[playlistResourceId] = parentManifestUrl;

        _ = service.DownloadProxyResourceAsync(
                session,
                playlistResourceId,
                playlistUrl,
                proxyBaseUrl,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var segment1ResourceId = session.ProxyResources.Single(x => x.Value == segment1Url).Key;
        var segment2ResourceId = session.ProxyResources.Single(x => x.Value == segment2Url).Key;
        var segment3ResourceId = session.ProxyResources.Single(x => x.Value == segment3Url).Key;

        var firstSegment = service.DownloadProxyResourceAsync(
                session,
                segment1ResourceId,
                segment1Url,
                proxyBaseUrl,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual("video/iso.segment", firstSegment.ContentType, "Foreground Alloha segment should keep the upstream media type.");
        AssertEqual("1,1,1", string.Join(",", firstSegment.Content), "Foreground Alloha segment should return the upstream bytes.");

        AssertTrue(
            WaitUntil(() =>
                session.BufferedProxyResources.ContainsKey(segment2ResourceId) &&
                session.BufferedProxyResources.ContainsKey(segment3ResourceId),
                timeoutMs: 3000),
            "Alloha proxy should prefetch roughly two minutes of upcoming media segments.");

        AssertEqual(0, GetRequestCount(segment4Url), "Initial Alloha prefetch should stop near the two-minute target instead of fetching the whole playlist.");

        var segment2RequestsBefore = GetRequestCount(segment2Url);
        var secondSegment = service.DownloadProxyResourceAsync(
                session,
                segment2ResourceId,
                segment2Url,
                proxyBaseUrl,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertEqual("2,2,2", string.Join(",", secondSegment.Content), "Prefetched Alloha segment should be served from the local buffer.");
        AssertEqual(segment2RequestsBefore, GetRequestCount(segment2Url), "Buffered Alloha segment should not hit upstream again.");

        int GetRequestCount(string url)
        {
            return segmentRequestCounts.TryGetValue(url, out var count) ? count : 0;
        }
    }

    static void AllohaPlaybackService_PrefetchSurvivesCompletedSegmentRequest()
    {
        const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
        const string playlistResourceId = "playlist-session-prefetch";
        const string parentManifestUrl = "https://stream-balancer-alloha.example/serial/master.m3u8";
        const string playlistUrl = "https://stream-balancer-alloha.example/serial/index-f1-v1.m3u8";
        const string segment1Url = "https://stream-balancer-alloha.example/serial/segment-001.m4s";
        const string segment2Url = "https://stream-balancer-alloha.example/serial/segment-002.m4s";
        const string segment3Url = "https://stream-balancer-alloha.example/serial/segment-003.m4s";
        var prefetchStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePrefetch = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var http = new HttpClient(new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Get && url == playlistUrl)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        #EXTM3U
                        #EXT-X-TARGETDURATION:60
                        #EXTINF:60.000,
                        segment-001.m4s
                        #EXTINF:60.000,
                        segment-002.m4s
                        #EXTINF:60.000,
                        segment-003.m4s
                        #EXT-X-ENDLIST
                        """)
                };
            }

            if (request.Method == HttpMethod.Get && url == segment1Url)
            {
                return CreateSegmentResponse(1);
            }

            if (request.Method == HttpMethod.Get && (url == segment2Url || url == segment3Url))
            {
                prefetchStarted.TrySetResult(true);
                await releasePrefetch.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return CreateSegmentResponse((byte)(url == segment2Url ? 2 : 3));
            }

            throw new InvalidOperationException("Unexpected Alloha session-prefetch test request: " + request.RequestUri);
        }));

        var ctor = typeof(AllohaPlaybackService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(Microsoft.Extensions.Logging.ILogger<AllohaPlaybackService>), typeof(HttpClient) },
            modifiers: null);
        AssertTrue(ctor is not null, "Alloha internal test constructor should exist.");

        var service = (AllohaPlaybackService)ctor!.Invoke(new object[] { NullLogger<AllohaPlaybackService>.Instance, http });
        var session = new AllohaPlaybackSession
        {
            SessionId = "session-prefetch-request-lifetime",
            ManifestUrl = parentManifestUrl,
            RefererUrl = "https://alloha.yani.tv/?token_movie=demo",
            RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(10)
        };
        session.ProxyResources[playlistResourceId] = playlistUrl;
        session.ProxyResourceReferers[playlistResourceId] = parentManifestUrl;

        _ = service.DownloadProxyResourceAsync(
                session,
                playlistResourceId,
                playlistUrl,
                proxyBaseUrl,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        var segment1ResourceId = session.ProxyResources.Single(x => x.Value == segment1Url).Key;
        var segment2ResourceId = session.ProxyResources.Single(x => x.Value == segment2Url).Key;
        var segment3ResourceId = session.ProxyResources.Single(x => x.Value == segment3Url).Key;

        using var requestCancellation = new CancellationTokenSource();
        _ = service.DownloadProxyResourceAsync(
                session,
                segment1ResourceId,
                segment1Url,
                proxyBaseUrl,
                requestCancellation.Token)
            .GetAwaiter()
            .GetResult();

        AssertTrue(prefetchStarted.Task.Wait(1000), "Alloha should start background prefetch after serving the foreground segment.");
        requestCancellation.Cancel();
        releasePrefetch.TrySetResult(true);

        AssertTrue(
            WaitUntil(
                () => session.BufferedProxyResources.ContainsKey(segment2ResourceId) &&
                      session.BufferedProxyResources.ContainsKey(segment3ResourceId),
                timeoutMs: 3000),
            "Alloha prefetch should remain session-scoped after the foreground HTTP request is complete.");

        static HttpResponseMessage CreateSegmentResponse(byte marker)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new[] { marker, marker, marker })
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("video/iso.segment");
            return response;
        }
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

    static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter502()
    {
        AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
            HttpStatusCode.BadGateway,
            "Bad Gateway",
            "502");
    }

    static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentAfterNetworkFailure()
    {
        AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
            HttpStatusCode.OK,
            string.Empty,
            "network failure",
            new HttpRequestException("The upstream connection was reset."));
    }

    static void AllohaPlaybackService_SuppressesConcurrentFailedRefreshStorm()
    {
        const string proxyBaseUrl = "http://localhost:8096/YummyKodik/alloha-proxy";
        const string iframeUrl = "https://alloha.yani.tv/?token_movie=movie-token&translation=222&season=1&episode=3&token=req-token&hidden=translation,season,episode";
        const string oldMasterUrl = "https://stream-balancer-alloha-old.example/serial/master.m3u8";
        const string failedMasterUrl = "https://stream-balancer-alloha-failed.example/serial/master.m3u8";
        const string segment1Url = "https://stream-balancer-alloha-old.example/serial/segment-001.m4s";
        const string segment2Url = "https://stream-balancer-alloha-old.example/serial/segment-002.m4s";
        const string segment3Url = "https://stream-balancer-alloha-old.example/serial/segment-003.m4s";
        var viewporti = "yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT";
        var bothInitialResourcesStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialResourceRequests = 0;
        var iframeRequests = 0;
        var bnsiRequests = 0;
        var failedManifestRequests = 0;

        var handler = new AsyncDelegatingTestHandler(async (request, cancellationToken) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url is segment1Url or segment2Url or segment3Url)
            {
                if (Interlocked.Increment(ref initialResourceRequests) >= 2)
                {
                    bothInitialResourcesStarted.TrySetResult(true);
                }

                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<HTML>Error</HTML>")
                };
            }

            if (request.Method == HttpMethod.Get &&
                url.StartsWith("https://alloha.yani.tv/?token_movie=movie-token", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref iframeRequests);
                await bothInitialResourcesStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
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
                Interlocked.Increment(ref bnsiRequests);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent($$"""
                        {
                          "hlsSource": [
                            {
                              "label": "РуАниме / DEEP",
                              "audioId": "1",
                              "quality": {
                                "1080": "{{failedMasterUrl}}"
                              }
                            }
                          ]
                        }
                        """)
                };
            }

            if (request.Method == HttpMethod.Get && url == failedMasterUrl)
            {
                Interlocked.Increment(ref failedManifestRequests);
                return new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<HTML>Error</HTML>")
                };
            }

            throw new InvalidOperationException("Unexpected Alloha failed-refresh test request: " + request.RequestUri);
        });

        using var http = new HttpClient(handler);
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
        var blockedSession = CreateSession("failed-refresh-session");

        var firstFailure = CaptureFailureAsync(service.DownloadProxyResourceAsync(
            blockedSession,
            "segment-1",
            segment1Url,
            proxyBaseUrl,
            CancellationToken.None));
        var secondFailure = CaptureFailureAsync(service.DownloadProxyResourceAsync(
            blockedSession,
            "segment-2",
            segment2Url,
            proxyBaseUrl,
            CancellationToken.None));
        var concurrentFailures = Task.WhenAll(firstFailure, secondFailure).GetAwaiter().GetResult();

        AssertTrue(
            concurrentFailures.All(error => error is InvalidOperationException),
            "Both queued segment requests should surface the failed Alloha recovery.");
        AssertEqual(2, initialResourceRequests, "Both already-started segment requests may observe the original 403.");
        AssertEqual(1, iframeRequests, "Concurrent segment failures should share one Alloha session refresh attempt.");
        AssertEqual(1, bnsiRequests, "Concurrent segment failures should not duplicate Alloha BNSI resolution.");
        AssertEqual(1, failedManifestRequests, "Concurrent segment failures should not duplicate the failing manifest probe.");
        AssertTrue(
            Volatile.Read(ref blockedSession.ProxyResourceRefreshBlockedUntilUtcTicks) > DateTime.UtcNow.Ticks,
            "A failed recovery should start the per-session cooldown.");

        AssertThrows<InvalidOperationException>(() => service.DownloadProxyResourceAsync(
                blockedSession,
                "segment-3",
                segment3Url,
                proxyBaseUrl,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult(),
            "A new segment request in the failed session should fail before repeating upstream work.");

        AssertEqual(2, initialResourceRequests, "Cooldown should reject new segment requests before another upstream resource call.");
        AssertEqual(1, iframeRequests, "Cooldown should suppress another session refresh attempt.");

        var freshSession = CreateSession("fresh-refresh-session");
        AssertThrows<InvalidOperationException>(() => service.DownloadProxyResourceAsync(
                freshSession,
                "segment-3",
                segment3Url,
                proxyBaseUrl,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult(),
            "A fresh playback session should retain its independent recovery attempt.");

        AssertEqual(3, initialResourceRequests, "A fresh session should not inherit another session's failed-resource cooldown.");
        AssertEqual(2, iframeRequests, "A fresh session should be allowed one independent Alloha refresh attempt.");
        AssertEqual(2, bnsiRequests, "A fresh session should independently resolve its Alloha source.");
        AssertEqual(2, failedManifestRequests, "A fresh session should independently probe the resolved manifest.");

        AllohaPlaybackSession CreateSession(string sessionId)
        {
            var session = new AllohaPlaybackSession
            {
                SessionId = sessionId,
                ManifestUrl = oldMasterUrl,
                ManifestText = "#EXTM3U",
                RefererUrl = iframeUrl,
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

            session.ProxyResources["segment-1"] = segment1Url;
            session.ProxyResources["segment-2"] = segment2Url;
            session.ProxyResources["segment-3"] = segment3Url;
            session.ProxyResourceReferers["segment-1"] = oldMasterUrl;
            session.ProxyResourceReferers["segment-2"] = oldMasterUrl;
            session.ProxyResourceReferers["segment-3"] = oldMasterUrl;
            return session;
        }

        static async Task<Exception?> CaptureFailureAsync(Task<AllohaProxyResource> request)
        {
            try
            {
                await request.ConfigureAwait(false);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
    }

    static void AllohaPlaybackService_DownloadProxyResourceRefreshesSegmentUsingParentChainAfter(
        HttpStatusCode initialStatusCode,
        string initialBody,
        string recoveryLabel,
        Exception? initialException = null)
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
                if (initialException != null)
                {
                    throw initialException;
                }

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
}
