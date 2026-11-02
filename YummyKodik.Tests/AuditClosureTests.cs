using System.Net;
using System.Reflection;
using Microsoft.Extensions.Logging;
using YummyKodik;
using YummyKodik.Kodik;
using YummyKodik.Util;

internal static class AuditClosureTests
{
    public static void CallerCancellationAndResponseDisposal()
    {
        KodikTokenProvider.InvalidateCache();
        try
        {
            // Cancellation must win even when a stale token would otherwise be usable.
            typeof(KodikTokenProvider).GetField("_cachedToken", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, "cached-test-token");
            foreach (var useTry in new[] { false, true })
            {
                using var stop = new CancellationTokenSource();
                var requests = 0;
                using var http = new HttpClient(new Handler((_, _) =>
                {
                    requests++;
                    stop.Cancel();
                    throw new OperationCanceledException(stop.Token);
                }));
                try
                {
                    if (useTry) KodikTokenProvider.TryGetTokenAsync(http, forceRefresh: true, cancellationToken: stop.Token).GetAwaiter().GetResult();
                    else KodikTokenProvider.GetTokenAsync(http, forceRefresh: true, cancellationToken: stop.Token).GetAwaiter().GetResult();
                    throw new Exception("Caller cancellation was swallowed.");
                }
                catch (OperationCanceledException) { }
                Require(requests == 1, "Cancellation must not start fallback requests.");
            }

            var contents = new List<TrackedContent>();
            using var successHttp = new HttpClient(new Handler((_, _) =>
            {
                var body = new TrackedContent(contents.Count == 0 ? "invalid primary script" : "token=test-fallback-token");
                contents.Add(body);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = body };
            }));
            // Both failed primary decode and successful fallback must dispose their response bodies.
            KodikTokenResolver.ResolveTokenAsync(successHttp).GetAwaiter().GetResult();
            Require(contents.Count == 2 && contents.All(x => x.Disposed), "Token source responses must be disposed.");
        }
        finally { KodikTokenProvider.InvalidateCache(); }
    }

    public static void ProviderDiagnosticsDoNotExposePayloads()
    {
        const string secret = "SYNTHETIC_SECRET_471";
        var logger = new CaptureLogger();
        using var http = new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html>" + secret + "</html>")
        }));
        var client = new KodikClient(http, secret, logger, () => true);
        var fetch = typeof(KodikClient).GetMethod("GetStringAsync", BindingFlags.NonPublic | BindingFlags.Instance);
        Require(fetch != null, "Exercise the actual logged HTTP failure path.");
        try
        {
            ((Task<string>)fetch!.Invoke(client, ["https://provider.test/" + secret + "?signature=" + secret, CancellationToken.None])!).GetAwaiter().GetResult();
            throw new Exception("Expected upstream failure.");
        }
        catch (KodikServiceException ex)
        {
            Require(!ex.ToString().Contains(secret), "Exception must not expose signed URL or payload.");
        }
        Require(logger.Messages.Count >= 3, "Request, response and warning diagnostics must remain available.");
        Require(logger.Messages.All(x => !x.Contains(secret)), "No payload or opaque path/query credential may reach logs.");
        Require(logger.Messages.Any(x => x.Contains("502")), "Status remains actionable.");
        Require(!ProviderDiagnostics.DescribeUrl("https://user:" + secret + "@provider.test/" + secret).Contains(secret), "URL userinfo and paths must be omitted.");
        Require(!ProviderDiagnostics.DescribePayload(secret).Contains(secret), "Malformed bodies must also be safe.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request, token));
    }
    private sealed class TrackedContent(string value) : StringContent(value)
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class CaptureLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception) + exception);
    }
}
