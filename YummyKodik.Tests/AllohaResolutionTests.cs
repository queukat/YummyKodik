using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Alloha;
using YummyKodik.Yummy;

internal static class AllohaResolutionTests
{
    public static void BoundsAndCoalescesFailedResolution() => RunAsync().GetAwaiter().GetResult();

    private static async Task RunAsync()
    {
        var calls = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (_, ct) =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        var service = new AllohaPlaybackService(NullLogger<AllohaPlaybackService>.Instance, http)
            { ResolutionTimeout = TimeSpan.FromMilliseconds(200) };
        var source = new YummyAllohaSource { MovieToken = Guid.NewGuid().ToString("N"), RequestToken = "test", TranslationId = 1,
            SeasonNumber = 1, EpisodeNumber = 1, RefererUrl = "https://alloha.yani.tv/?test=1" };
        using var caller = new CancellationTokenSource();
        var first = service.CreateSessionAsync(source, 1080, "AniLibria", caller.Token);
        await entered.Task;
        var peer = service.CreateSessionAsync(source, 1080, "AniLibria", CancellationToken.None);
        caller.Cancel();
        try { await first; throw new Exception("Expected caller cancellation."); }
        catch (OperationCanceledException) when (caller.IsCancellationRequested) { }
        await Fails(peer);
        await Fails(service.CreateSessionAsync(source, 1080, "AniLibria", CancellationToken.None));
        if (calls != 1) throw new Exception("Concurrent calls and immediate retries must not repeat the failed source.");
        await Fails(service.CreateSessionAsync(source, 720, "AniLibria", CancellationToken.None));
        await Fails(service.CreateSessionAsync(source, 1080, "AniDUB", CancellationToken.None));
        if (calls != 3) throw new Exception("Cooldown must not suppress another quality or voice.");
    }

    private static async Task Fails(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Expected bounded source failure.");
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
