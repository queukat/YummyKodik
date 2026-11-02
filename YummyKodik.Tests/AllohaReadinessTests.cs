using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Alloha;

internal static class AllohaReadinessTests
{
    private const string Proxy = "http://localhost:8096/YummyKodik/alloha-proxy";

    public static void RejectsForbiddenVideo()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(HttpStatusCode.Forbidden, "denied"))));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
        var session = Session("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST");
        ExpectFailure(() => service.EnsurePlaybackReadyAsync(session, Proxy, CancellationToken.None).GetAwaiter().GetResult());
        Check(!session.PlaybackReady, "A successful playlist with forbidden video must fail before the gateway selects Alloha.");
    }

    public static void ChecksDependenciesAndReusesFirstFragment() => CheckDependenciesAndCache(Proxy);
    public static void ChecksRootRelativeProxyUrls() => CheckDependenciesAndCache("/base/YummyKodik/alloha-proxy");

    private static void CheckDependenciesAndCache(string proxy)
    {
        var calls = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            calls.Add(path);
            return Task.FromResult(path == "/video.m3u8"
                ? Reply(HttpStatusCode.OK, "#EXTM3U\n#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:6,\nfirst.m4s\n#EXTINF:6,\nsecond.m4s\n#EXT-X-ENDLIST", "application/vnd.apple.mpegurl")
                : Reply(HttpStatusCode.OK, "media bytes", "video/mp4"));
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
        var session = Session("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000000\nvideo.m3u8");
        service.EnsurePlaybackReadyAsync(session, proxy, CancellationToken.None).GetAwaiter().GetResult();
        Check(calls.SequenceEqual(new[] { "/video.m3u8", "/key.bin", "/init.mp4", "/first.m4s" }),
            "Readiness must verify nested playlist, key, init and one fragment only.");
        Check(session.ProxyResourcePrefetches.IsEmpty, "Readiness must not prefetch two minutes of an unselected source.");
        service.EnsurePlaybackReadyAsync(session, proxy, CancellationToken.None).GetAwaiter().GetResult();
        Check(calls.Count == 4, "Repeated gateway requests must reuse readiness.");
        var fragment = session.ProxyResources.Single(pair => pair.Value.EndsWith("/first.m4s", StringComparison.Ordinal));
        // Avoid unrelated prefetch in this assertion; the cache itself must serve the normal proxy.
        session.ProxyResourceMediaPlaylists.Clear();
        var response = service.DownloadProxyResourceAsync(session, fragment.Key, fragment.Value, proxy, CancellationToken.None).GetAwaiter().GetResult();
        Check(calls.Count == 4 && Encoding.UTF8.GetString(response.Content) == "media bytes", "The initial probe must seed the playback cache.");
    }

    public static void RecoversVideoWithFreshToken()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((request, _) =>
        {
            calls++;
            return Task.FromResult(request.Headers.TryGetValues("Accepts-Controls", out var values) && values.Single() == "fresh-token"
                ? Reply(HttpStatusCode.OK, "video", "video/mp2t")
                : Reply(HttpStatusCode.Forbidden, "denied"));
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http,
            (_, _) => Task.FromResult<string?>("fresh-token"));
        var session = Session("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST");
        session.Source = new YummyKodik.Yummy.YummyAllohaSource { MovieToken = "test", RequestToken = "test", TranslationId = 1,
            SeasonNumber = 1, EpisodeNumber = 1, RefererUrl = "https://alloha.yani.tv/?test=1" };
        service.EnsurePlaybackReadyAsync(session, Proxy, CancellationToken.None).GetAwaiter().GetResult();
        Check(session.PlaybackReady && calls == 2, "A recoverable fragment must remain on the same provider after one successful retry.");
        Check(session.ProxyResourceRefreshBlockedUntilUtcTicks == 0, "A successful recovery must stay usable.");
    }

    public static void FailedRetryStartsCooldown() => CheckFailedRetry(networkFailure: false);
    public static void FailedNetworkRetryStartsCooldown() => CheckFailedRetry(networkFailure: true);

    private static void CheckFailedRetry(bool networkFailure)
    {
        var calls = 0;
        var tokens = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (networkFailure) throw new HttpRequestException("connection closed");
            return Task.FromResult(Reply(HttpStatusCode.Forbidden, "denied"));
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http,
            (_, _) => { tokens++; return Task.FromResult<string?>("still-rejected-token"); });
        var session = Session("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST");
        AllohaPlaybackService.BuildManifestResponseBody(session, Proxy);
        var segment = session.ProxyResources.Single();
        ExpectFailure(() => service.DownloadProxyResourceAsync(session, segment.Key, segment.Value, Proxy, CancellationToken.None).GetAwaiter().GetResult());
        Check(session.ProxyResourceRefreshBlockedUntilUtcTicks > DateTime.UtcNow.Ticks,
            "A refreshed token followed by another failed fragment must start cooldown.");
        ExpectFailure(() => service.DownloadProxyResourceAsync(session, segment.Key, segment.Value, Proxy, CancellationToken.None).GetAwaiter().GetResult());
        Check(calls == 2 && tokens == 1, "The next fragment must not start another upstream recovery cycle.");
        // Once the cooldown expires, the session must be allowed a fresh recovery attempt.
        session.ProxyResourceRefreshBlockedUntilUtcTicks = DateTime.UtcNow.AddSeconds(-1).Ticks;
        ExpectFailure(() => service.DownloadProxyResourceAsync(session, segment.Key, segment.Value, Proxy, CancellationToken.None).GetAwaiter().GetResult());
        Check(calls == 4 && tokens == 2, "Cooldown must not permanently disable a session.");
    }

    public static void CancellationDoesNotPoisonSession()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new Handler((_, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
        var session = Session("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST");
        try
        {
            service.EnsurePlaybackReadyAsync(session, Proxy, cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("Expected caller cancellation.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        Check(!session.PlaybackReady && session.ProxyResourceRefreshBlockedUntilUtcTicks == 0,
            "Caller cancellation must not become provider failure or successful readiness.");
    }

    public static void BoundsPlaylistTraversal()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Reply(HttpStatusCode.OK, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nloop.m3u8", "application/vnd.apple.mpegurl"));
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http);
        ExpectFailure(() => service.EnsurePlaybackReadyAsync(Session("#EXTM3U\nloop.m3u8"), Proxy, CancellationToken.None).GetAwaiter().GetResult());
        Check(calls == 4, "Cyclic playlists must stop at the nesting bound.");
    }

    public static void RenewalCancellationDoesNotRetryToken() => CheckRenewalCancellation(true);
    public static void RenewalDeadlineStopsFailedRecovery() => CheckRenewalCancellation(false);

    public static void RenewalDeadlineIncludesTokenProof()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            if (++calls == 1) return Reply(HttpStatusCode.Forbidden, "expired");
            await Task.Delay(Timeout.Infinite, ct);
            return Reply(HttpStatusCode.OK, "video", "video/mp2t");
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http,
            (_, _) => Task.FromResult<string?>("fresh-token")) { ResolutionTimeout = TimeSpan.FromMilliseconds(100) };
        var session = Session("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST");
        ExpectFailure(() => service.EnsurePlaybackReadyAsync(session, Proxy, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult());
        Check(calls == 2 && !session.PlaybackReady && session.ProxyResourceRefreshBlockedUntilUtcTicks > DateTime.UtcNow.Ticks,
            "The token proof must share the recovery deadline and not publish readiness.");
    }

    private static void CheckRenewalCancellation(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        var tokens = 0;
        using var http = new HttpClient(new Handler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "alloha.yani.tv")
            {
                if (cancelCaller) caller.Cancel();
                await Task.Delay(Timeout.Infinite, ct);
            }
            return Reply(HttpStatusCode.Forbidden, "denied");
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http,
            (_, _) => { tokens++; return Task.FromResult<string?>(null); })
            { ResolutionTimeout = TimeSpan.FromMilliseconds(100) };
        var session = Session("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST");
        session.Source = new YummyKodik.Yummy.YummyAllohaSource { MovieToken = "test", RequestToken = "test", TranslationId = 1,
            SeasonNumber = 1, EpisodeNumber = 1, RefererUrl = "https://alloha.yani.tv/?test=1" };
        try
        {
            service.EnsurePlaybackReadyAsync(session, Proxy, caller.Token).WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            throw new Exception("Failed renewal must not be marked ready.");
        }
        catch (InvalidOperationException) when (!cancelCaller) { Check(tokens == 1 && !session.PlaybackReady, "Bounded renewal must fail after the token attempt."); }
        catch (OperationCanceledException) when (cancelCaller && caller.IsCancellationRequested)
        {
            Check(tokens == 1 && !session.PlaybackReady && session.ProxyResourceRefreshBlockedUntilUtcTicks == 0,
                "Cancelled renewal must not retry a token or poison the session.");
        }
    }

    private static AllohaPlaybackSession Session(string manifest) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        ManifestUrl = "https://media.example/master.m3u8",
        ManifestText = manifest,
        IframeUrl = "https://player.example/",
        ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
    };

    private static HttpResponseMessage Reply(HttpStatusCode status, string body, string type = "text/html")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, type) };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException) { return; }
        throw new Exception("Expected an upstream failure.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }
}
