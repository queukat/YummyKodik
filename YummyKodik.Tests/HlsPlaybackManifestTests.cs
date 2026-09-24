using System.Net;
using YummyKodik.Media;

internal static class HlsPlaybackManifestTests
{
    public static void FallbackDurationAndPlaybackShareOneSession()
    {
        const string gateway = "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=22919&ep=12";
        const string pinned = "http://localhost:8096/YummyKodik/kodik-proxy/manifest.m3u8?sessionId=selected";
        using var handler = new Handler((request, call) =>
        {
            if (call != 1 || request.RequestUri!.AbsoluteUri != gateway) throw new Exception("Unexpected extra resolution.");
            return Response(request, "#EXTM3U\n#EXTINF:1430,\npart1.ts\n#EXTINF:6.656,\npart2.ts\n#EXT-X-ENDLIST", pinned);
        });
        using var client = new HttpClient(handler);
        var result = HlsPlaybackManifestResolver.ResolveAsync(client, gateway, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Url != pinned || result.RunTimeTicks != TimeSpan.FromSeconds(1436.656).Ticks)
            throw new Exception("Fallback must replace the 1430-second catalog duration and bind the same selected session.");
    }

    public static void MasterResolvesOnlyPlaylistsAndPreservesPinnedMaster()
    {
        const string gateway = "http://localhost:8096/YummyKodik/stream?provider=alloha";
        const string master = "http://localhost:8096/YummyKodik/alloha-proxy/master.m3u8?sessionId=one";
        using var handler = new Handler((request, call) => call switch
        {
            1 => Response(request, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nvideo.m3u8?sessionId=one", master),
            2 when request.RequestUri!.AbsolutePath.EndsWith("/video.m3u8", StringComparison.Ordinal) =>
                Response(request, "#EXTM3U\n#EXTINF:1422,\none.m4s\n#EXTINF:8.02,\ntwo.m4s\n#EXT-X-ENDLIST", null),
            _ => throw new Exception("Resolver must not fetch segments or resolve the provider again.")
        });
        using var client = new HttpClient(handler);
        var result = HlsPlaybackManifestResolver.ResolveAsync(client, gateway, CancellationToken.None).GetAwaiter().GetResult();
        if (result.Url != master || result.RunTimeTicks != TimeSpan.FromSeconds(1430.02).Ticks)
            throw new Exception("Master URL and fractional final segment duration must survive resolution.");
    }

    public static void RejectsUnstableIncompleteAndInvalidManifests()
    {
        foreach (var (body, location) in new[]
        {
            ("#EXTM3U\n#EXTINF:6,\na.ts", "http://localhost:8096/YummyKodik/cvh-proxy/root.m3u8"),
            ("#EXTM3U\n#EXTINF:NaN,\na.ts\n#EXT-X-ENDLIST", "http://localhost:8096/YummyKodik/cvh-proxy/root.m3u8"),
            ("#EXTM3U\n#EXTINF:6,\na.ts\n#EXT-X-ENDLIST", "https://external.invalid/secret"),
            ("#EXTM3U\n#EXTINF:6,\na.ts\n#EXT-X-ENDLIST", "http://localhost:8096/YummyKodik/stream")
        })
        {
            using var handler = new Handler((request, _) => Response(request, body, location));
            using var client = new HttpClient(handler);
            try
            {
                HlsPlaybackManifestResolver.ResolveAsync(client, "http://localhost:8096/YummyKodik/stream", CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (InvalidOperationException) { continue; }
            throw new Exception("Unproven runtime or unbound source must not be published.");
        }
    }

    private static HttpResponseMessage Response(HttpRequestMessage request, string body, string? location)
    {
        var result = new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new StringContent(body) };
        if (location != null) result.Content.Headers.ContentLocation = new Uri(location);
        return result;
    }

    private sealed class Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request, ++_calls));
    }
}
