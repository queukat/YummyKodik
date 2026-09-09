using System.Net;
using System.Text;
using YummyKodik.Cvh;
using YummyKodik.Yummy;

internal static class CvhStreamingTests
{
    private const string ResourceUrl = "https://cdn.cvh.example/video/segment.ts";
    private const string ProxyBaseUrl = "/base/YummyKodik/cvh-proxy";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void FirstChunkArrivesBeforeEof() => FirstChunkArrivesBeforeEofAsync().GetAwaiter().GetResult();
    public static void CookiesAndResourceHeadersArePreserved() => CookiesAndResourceHeadersArePreservedAsync().GetAwaiter().GetResult();
    public static void SniffedManifestIsRewrittenBeforeWriting() => SniffedManifestIsRewrittenBeforeWritingAsync().GetAwaiter().GetResult();
    public static void PartialFailureAndCancellationStopWrites() => PartialFailureAndCancellationStopWritesAsync().GetAwaiter().GetResult();

    private static async Task FirstChunkArrivesBeforeEofAsync()
    {
        var tail = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stream = new ScriptedStream([[0x47, 1, 2, 3], [4, 5, 6]], tail.Task);
        using var handler = new Handler(request => Response(request, stream, 7));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource(Timeout);
        using var received = new MemoryStream();
        var client = new CvhClient(http);
        var download = client.DownloadProxyResourceAsync(Session(), ResourceUrl, ProxyBaseUrl, cancellation.Token,
            async (_, bytes, token) =>
            {
                await received.WriteAsync(bytes, token);
                firstArrived.TrySetResult();
            });
        try
        {
            await firstArrived.Task.WaitAsync(Timeout);
            Check(!download.IsCompleted, "CVH must forward its first bytes before upstream EOF.");
            Check(received.Length == 4, "The initial CVH prefix must be forwarded immediately.");
            tail.TrySetResult();
            await download.WaitAsync(Timeout);
            Check(received.ToArray().SequenceEqual(new byte[] { 0x47, 1, 2, 3, 4, 5, 6 }), "CVH streaming must preserve the full payload without duplicate bytes.");
            Check(handler.Requests == 1 && stream.Disposed, "The CVH transfer must use one request and dispose its upstream stream.");
        }
        finally
        {
            cancellation.Cancel();
            tail.TrySetResult();
            await ObserveAsync(download);
        }
    }

    private static async Task CookiesAndResourceHeadersArePreservedAsync()
    {
        var session = Session();
        using (var seed = new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, ResourceUrl)
        })
        {
            seed.Headers.TryAddWithoutValidation("Set-Cookie", "session=before; Path=/");
            session.Cookies.Capture(seed);
        }

        using var handler = new Handler(request =>
        {
            Check(request.Headers.GetValues("Cookie").Single().Contains("session=before", StringComparison.Ordinal), "Proxy requests must retain the session cookie.");
            Check(request.Headers.Referrer?.AbsoluteUri.Contains("iframeCVH.html?anime_id=123&episode=5", StringComparison.Ordinal) == true, "The original CVH iframe referer must be retained.");
            Check(request.Headers.Accept.ToString() == "*/*", "CVH binary resource Accept headers must remain minimal.");
            Check(!request.Headers.Contains("Origin") && !request.Headers.Contains("Sec-Fetch-Mode") && !request.Headers.Contains("Sec-Fetch-Site"), "Binary proxy requests must not gain playlist-only browser headers.");
            Check(request.Headers.UserAgent.ToString().Contains("Mozilla", StringComparison.Ordinal) && request.Headers.Contains("Accept-Language"), "User-Agent and language headers must survive streaming.");
            var response = Response(request, new ScriptedStream([[0x47, 2, 3]]), 3);
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("video/mp2t");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "session=after; Path=/");
            return response;
        });
        using var http = new HttpClient(handler);
        var writes = 0;
        await new CvhClient(http).DownloadProxyResourceAsync(session, ResourceUrl, ProxyBaseUrl, CancellationToken.None,
            (contentType, bytes, _) =>
            {
                Check(contentType == "video/mp2t", "Upstream binary content type must reach the downstream writer.");
                Check(session.Cookies.GetCookieHeader(new Uri(ResourceUrl)).Contains("session=after", StringComparison.Ordinal), "Response cookies must be captured before streaming the body.");
                Check(bytes.Span.SequenceEqual(new byte[] { 0x47, 2, 3 }), "Cookie handling must not alter the segment payload.");
                writes++;
                return Task.CompletedTask;
            }).WaitAsync(Timeout);
        Check(writes == 1 && handler.Requests == 1, "A small successful segment must be written exactly once without retries.");
    }

    private static async Task SniffedManifestIsRewrittenBeforeWritingAsync()
    {
        const string manifest = " \n#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nchild.ts\n#EXT-X-ENDLIST\n";
        var bytes = Encoding.UTF8.GetBytes(manifest);
        var stream = new ScriptedStream(bytes.Select(value => new[] { value }).ToArray());
        using var handler = new Handler(request => Response(request, stream, bytes.Length));
        using var http = new HttpClient(handler);
        using var received = new MemoryStream();
        var session = Session();
        var writes = 0;
        await new CvhClient(http).DownloadProxyResourceAsync(session, "https://cdn.cvh.example/video/opaque.bin", ProxyBaseUrl, CancellationToken.None,
            (_, content, token) =>
            {
                writes++;
                return received.WriteAsync(content, token).AsTask();
            }).WaitAsync(Timeout);
        var rewritten = Encoding.UTF8.GetString(received.ToArray());
        Check(writes == 1, "A manifest split across prefix reads must be rewritten fully before the first write.");
        Check(rewritten.Contains(ProxyBaseUrl + "/", StringComparison.Ordinal) && rewritten.Contains("sessionId=" + session.SessionId, StringComparison.Ordinal), "CVH child URLs must use the current session and canonical proxy path.");
        Check(!rewritten.Contains("\nchild.ts\n", StringComparison.Ordinal), "A sniffed manifest must not leak its original child URL.");
        Check(session.ProxyResources.Values.Contains("https://cdn.cvh.example/video/child.ts") && stream.Disposed, "Rewritten resources must be registered and the upstream disposed.");
    }

    private static async Task PartialFailureAndCancellationStopWritesAsync()
    {
        foreach (var cancel in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource(Timeout);
            var stream = new ScriptedStream([[0x47, 1, 2, 3], [4, 5, 6]],
                failureAfterFirst: cancel ? null : new IOException("Simulated upstream reset."));
            using var handler = new Handler(request => Response(request, stream, 7));
            using var http = new HttpClient(handler);
            var writes = 0;
            var download = new CvhClient(http).DownloadProxyResourceAsync(Session(), ResourceUrl, ProxyBaseUrl, cancellation.Token,
                (_, bytes, _) =>
                {
                    writes++;
                    Check(bytes.Length == 4, "Only the prefix preceding the failure may be written.");
                    if (cancel) cancellation.Cancel();
                    return Task.CompletedTask;
                });
            var error = await CaptureFailureAsync(download);
            Check(cancel ? error is OperationCanceledException : error is IOException, "CVH must propagate cancellation or the upstream body failure.");
            Check(writes == 1 && handler.Requests == 1 && stream.Disposed, "A partial or canceled CVH transfer must stop writing, avoid retries and dispose the upstream.");
        }
    }

    private static CvhPlaybackSession Session() => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        Source = new YummyCvhSource { AnimeId = 123, EpisodeNumber = 5, DubbingName = "AnimeVost" }
    };

    private static HttpResponseMessage Response(HttpRequestMessage request, Stream stream, long length)
    {
        var content = new StreamContent(stream);
        content.Headers.ContentLength = length;
        return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = content };
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); return null; }
        catch (TimeoutException) { throw; }
        catch (Exception error) { return error; }
    }

    private static async Task ObserveAsync(Task task)
    {
        try { await task.WaitAsync(Timeout); }
        catch (Exception) { /* Cleanup; the assertion path observes failures. */ }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult(response(request));
        }
    }

    private sealed class ScriptedStream(byte[][] chunks, Task? tailGate = null, Exception? failureAfterFirst = null) : Stream
    {
        private int _index;
        private int _offset;
        public bool Disposed { get; private set; }
        public override bool CanRead => !Disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (destination.Length == 0) return 0;
            if (_index > 0)
            {
                if (tailGate is not null) await tailGate.WaitAsync(cancellationToken);
                if (failureAfterFirst is not null) throw failureAfterFirst;
            }
            if (_index == chunks.Length) return 0;
            var chunk = chunks[_index];
            var count = Math.Min(destination.Length, chunk.Length - _offset);
            chunk.AsMemory(_offset, count).CopyTo(destination);
            _offset += count;
            if (_offset == chunk.Length) { _index++; _offset = 0; }
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
