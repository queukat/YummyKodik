using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Alloha;
using YummyKodik.Yummy;

internal static class AllohaBrowserIdentityTests
{
    private const string Stable = "{\"releases\":[{\"version\":\"154.0.1.2\"}]}";
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Reply(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body) };

    public static void CoalescesAndPersistsVerifiedIdentity() => RunCoalescingAsync().GetAwaiter().GetResult();
    private static async Task RunCoalescingAsync()
    {
        var path = Path.Combine(Path.GetTempPath(), "alloha-identity-" + Guid.NewGuid().ToString("N"), "major.txt");
        try
        {
            var fetches = 0;
            using var http = new HttpClient(new Handler((_, _) => { Interlocked.Increment(ref fetches); return Task.FromResult(Reply(Stable)); }));
            var identity = new AllohaBrowserIdentity(http, path);
            var before = identity.Current;
            var verifying = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proofs = 0;
            async Task Verify(string _, CancellationToken ct)
            {
                Interlocked.Increment(ref proofs);
                verifying.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
            var first = identity.TryRecoverAsync(before, Verify, CancellationToken.None);
            await verifying.Task;
            Check(identity.Current == before && !File.Exists(path), "A candidate must not be committed before actual video proof.");
            var peers = Enumerable.Range(0, 12).Select(_ => identity.TryRecoverAsync(before, Verify, CancellationToken.None)).ToArray();
            release.SetResult();
            await Task.WhenAll(peers.Append(first));
            Check(fetches == 1 && proofs == 1, "Concurrent rejections must share one lookup and proof.");
            Check(File.ReadAllText(path) == "154", "Persist only a verified version number.");
            Check(new AllohaBrowserIdentity(http, path).Current == identity.Current, "Reuse verified identity after restart.");
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1, "Atomic write must leave no temporary file.");
        }
        finally { if (Directory.Exists(Path.GetDirectoryName(path))) Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    public static void RejectsBadMetadataAndBacksOff()
    {
        foreach (var body in new[] { "not json", "{}", "{\"releases\":[]}", "{\"releases\":[{\"version\":146}]}", "{\"releases\":[{\"version\":\"146.0.0.0\"}]}", "{\"releases\":[{\"version\":\"99999.0.0.0\"}]}" })
        {
            var calls = 0;
            var now = DateTimeOffset.UtcNow;
            using var http = new HttpClient(new Handler((_, _) => { calls++; return Task.FromResult(Reply(body)); }));
            var identity = new AllohaBrowserIdentity(http, utcNow: () => now);
            var old = identity.Current;
            Task Verify(string _, CancellationToken __) => throw new Exception("Invalid candidate reached video verification.");
            Check(identity.TryRecoverAsync(old, Verify, CancellationToken.None).GetAwaiter().GetResult() == null, "Invalid metadata must be ignored.");
            identity.TryRecoverAsync(old, Verify, CancellationToken.None).GetAwaiter().GetResult();
            Check(calls == 1 && identity.Current == old, "Do not repeatedly query metadata after failure.");
            now = now.AddHours(2);
            identity.TryRecoverAsync(old, Verify, CancellationToken.None).GetAwaiter().GetResult();
            Check(calls == 2, "Metadata failure must not permanently disable recovery.");
        }
    }

    public static void FailedVideoAndCancellationKeepCurrent()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(Stable))));
        var identity = new AllohaBrowserIdentity(http);
        var old = identity.Current;
        var result = identity.TryRecoverAsync(old, (_, _) => throw new HttpRequestException("Video403"), CancellationToken.None).GetAwaiter().GetResult();
        Check(result == null && identity.Current == old, "Playlist success alone must not promote an unusable identity.");
        Check(identity.TryRecoverAsync(old, (_, _) => throw new Exception("Failed proof must retain its cooldown."), CancellationToken.None).GetAwaiter().GetResult() == null,
            "An actual video failure must still back off.");
        identity = new AllohaBrowserIdentity(http);
        using var cancellation = new CancellationTokenSource();
        try
        {
            identity.TryRecoverAsync(old, (_, ct) => { cancellation.Cancel(); return Task.FromCanceled(ct); }, cancellation.Token).GetAwaiter().GetResult();
            throw new Exception("Caller cancellation was swallowed.");
        }
        catch (OperationCanceledException) { }
        Check(identity.Current == old, "Cancelled verification must not promote a candidate.");
        var retried = identity.TryRecoverAsync(old, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
        Check(retried == AllohaBrowserIdentity.ForMajor(154) && identity.Current == retried,
            "Cancelled candidate proof must allow immediate recovery on the next playback.");

        using var bootstrapCancellation = new CancellationTokenSource();
        try
        {
            identity.TryRecoverAsync(retried!, (candidate, ct) =>
            {
                Check(candidate == old, "A rejected newer identity must try the bootstrap.");
                bootstrapCancellation.Cancel();
                return Task.FromCanceled(ct);
            }, bootstrapCancellation.Token).GetAwaiter().GetResult();
            throw new Exception("Bootstrap cancellation was swallowed.");
        }
        catch (OperationCanceledException) { }
        Check(identity.Current == retried, "Cancelled bootstrap proof must retain the current identity.");
        var recovered = identity.TryRecoverAsync(retried!, (candidate, _) =>
        {
            Check(candidate == old, "Cancelled bootstrap proof must be retried immediately.");
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Check(recovered == old && identity.Current == old,
            "Caller cancellation must not impose a bootstrap cooldown on the next playback.");
    }

    public static void BoundsMetadataTimeout()
    {
        using var http = new HttpClient(new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Reply(Stable); }));
        var identity = new AllohaBrowserIdentity(http);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Check(identity.TryRecoverAsync(identity.Current, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult() == null,
            "Unavailable version service must keep the existing identity.");
        Check(watch.Elapsed < TimeSpan.FromSeconds(8), "Metadata outage must not hold playback indefinitely.");
    }

    public static void IgnoresCorruptCache()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var http = new HttpClient(new Handler((_, _) => throw new Exception("Reading cache must not access the network.")));
            foreach (var value in new[] { "broken", "146", "99999", "154\r\nInjected: header", new string('1', 100) })
            {
                File.WriteAllText(path, value);
                Check(new AllohaBrowserIdentity(http, path).Current == AllohaPlaybackService.BrowserUserAgent,
                    "Invalid, obsolete or oversized cached identity must fall back safely.");
            }
        }
        finally { File.Delete(path); }
    }

    public static void RecoversRejectedCachedIdentity()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "156");
            using var http = new HttpClient(new Handler((_, _) => throw new Exception("Working bootstrap must not need version lookup.")));
            var identity = new AllohaBrowserIdentity(http, path);
            var rejected = identity.Current;
            var proofs = 0;
            var recovered = identity.TryRecoverAsync(rejected, (candidate, _) =>
            {
                Check(identity.Current == rejected && File.ReadAllText(path) == "156", "Keep cached identity until video is proven.");
                Check(candidate == AllohaBrowserIdentity.ForMajor(153), "Try known bootstrap after cached identity fails.");
                proofs++;
                return Task.CompletedTask;
            }, CancellationToken.None).GetAwaiter().GetResult();
            Check(recovered == identity.Current && File.ReadAllText(path) == "153" && proofs == 1, "Persist the verified recovery.");
            identity.TryRecoverAsync(rejected, (_, _) => throw new Exception("Concurrent old rejection must reuse recovery."), CancellationToken.None).GetAwaiter().GetResult();
            Check(identity.TryRecoverAsync(identity.Current, (_, _) => throw new Exception("Must not oscillate during cooldown."), CancellationToken.None).GetAwaiter().GetResult() == null,
                "Recovery must preserve lookup cooldown.");
        }
        finally { File.Delete(path); }
    }

    public static void FailedBootstrapDoesNotReplaceCache()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "156");
            using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(Stable))));
            var identity = new AllohaBrowserIdentity(http, path);
            var original = identity.Current;
            Check(identity.TryRecoverAsync(original, (_, _) => throw new HttpRequestException("Fragment403"), CancellationToken.None).GetAwaiter().GetResult() == null,
                "Rejected bootstrap must not be selected.");
            Check(identity.Current == original && File.ReadAllText(path) == "156", "Failed proof must retain prior identity.");
        }
        finally { File.Delete(path); }
    }

    public static void RecoversImmediatelyAfterPromotion()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Reply(Stable))));
        var identity = new AllohaBrowserIdentity(http);
        var bootstrap = identity.Current;
        identity.TryRecoverAsync(bootstrap, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
        Check(identity.Current != bootstrap, "Fixture must promote identity first.");
        var recovered = identity.TryRecoverAsync(identity.Current, (_, _) => Task.CompletedTask, CancellationToken.None).GetAwaiter().GetResult();
        Check(recovered == bootstrap && identity.Current == bootstrap, "Metadata cooldown must not block video-proven bootstrap recovery.");
    }

    public static void Repairs403AndPinsExistingSessions()
    {
        var lookups = 0;
        using var metadata = new HttpClient(new Handler((_, _) => { lookups++; return Task.FromResult(Reply(Stable)); }));
        var identity = new AllohaBrowserIdentity(metadata);
        var original = identity.Current;
        var upgraded = AllohaBrowserIdentity.ForMajor(154);
        var headers = new List<(string Path, string UserAgent)>();
        var tokenIdentities = new List<string>();
        using var http = new HttpClient(new Handler((request, _) =>
        {
            var uri = request.RequestUri!;
            var ua = request.Headers.UserAgent.ToString();
            headers.Add((uri.AbsolutePath, ua));
            if (uri.Host == "alloha.yani.tv" && request.Method == HttpMethod.Get)
            {
                var episode = uri.Query.Contains("episode=2", StringComparison.Ordinal) ? 2 : 1;
                return Task.FromResult(Reply("<meta name=\"viewporti\" content=\"yZFgNFZy3110sc1dwXZnDgUdFUlkj1XGFX2SVdEx9ZJRpd9w8wNoxqFTSSRUQmmmWTDjnV0mNSdURTVUNNNVQMVT\">" +
                    $"<script>const fileList = JSON.parse('{{\"active\":{{\"id\":{episode},\"seasons\":1,\"episode\":{episode},\"id_translation\":158}}}}');</script>"));
            }
            if (request.Method == HttpMethod.Post)
            {
                var episode = uri.Segments.Last();
                return Task.FromResult(Reply($"{{\"hlsSource\":[{{\"audioId\":\"1\",\"quality\":{{\"720\":\"https://media.example/{episode}/master.m3u8\"}}}}]}}"));
            }
            if (uri.AbsolutePath == "/2/master.m3u8" && ua == original)
                return Task.FromResult(Reply("denied", HttpStatusCode.Forbidden));
            if (uri.AbsolutePath.EndsWith(".m3u8", StringComparison.Ordinal))
                return Task.FromResult(Reply("#EXTM3U\n#EXTINF:6,\nfirst.ts\n#EXT-X-ENDLIST"));
            if (uri.AbsolutePath == "/1/first.ts" && !request.Headers.GetValues("Accepts-Controls").Contains("renewed"))
                return Task.FromResult(Reply("denied", HttpStatusCode.Forbidden));
            var response = Reply("video");
            response.Content.Headers.ContentType = new("video/mp2t");
            return Task.FromResult(response);
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http,
            (request, _) => { tokenIdentities.Add(request.UserAgent); return Task.FromResult<string?>(request.UserAgent == original ? "renewed" : null); }, identity);
        YummyAllohaSource Source(int episode) => new()
        {
            MovieToken = "identity-test", RequestToken = "test", TranslationId = 158, SeasonNumber = 1, EpisodeNumber = episode,
            RefererUrl = $"https://alloha.yani.tv/?token_movie=identity-test&token=test&translation=158&season=1&episode={episode}"
        };
        var first = service.CreateSessionAsync(Source(1), 720, CancellationToken.None).GetAwaiter().GetResult();
        Check(lookups == 0, "Successful playback must not query Chrome versions.");
        var second = service.CreateSessionAsync(Source(2), 720, CancellationToken.None).GetAwaiter().GetResult();
        Check(lookups == 1 && identity.Current == upgraded && second.UserAgent == upgraded, "Recover after manifest403 using a verified identity.");
        Check(headers.Any(x => x.Path == "/2/first.ts" && x.UserAgent == upgraded), "Upgrade must verify video with the new identity.");
        first.Source = null; // Exercise token-only recovery of the already issued session.
        service.EnsurePlaybackReadyAsync(first, "http://localhost/YummyKodik/alloha-proxy", CancellationToken.None).GetAwaiter().GetResult();
        Check(first.UserAgent == original && headers.Where(x => x.Path == "/1/first.ts").All(x => x.UserAgent == original),
            "Existing sessions must retain their identity after a global upgrade.");
        Check(tokenIdentities.Last() == original, "Token renewal must use the session identity, not the newly promoted global one.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
