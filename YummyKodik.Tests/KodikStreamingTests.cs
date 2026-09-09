using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Kodik;

internal static class KodikStreamingTests
{
    private const string ProxyBaseUrl = "/base/YummyKodik/kodik-proxy";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(5);

    public static void FirstChunkBeforeEofAndCompletedCache() => FirstChunkBeforeEofAndCompletedCacheAsync().GetAwaiter().GetResult();
    public static void PartialReadFailureDoesNotRetryOrCache() => PartialReadFailureDoesNotRetryOrCacheAsync().GetAwaiter().GetResult();
    public static void DownstreamWriteFailureDoesNotRetryOrCache() => DownstreamWriteFailureDoesNotRetryOrCacheAsync().GetAwaiter().GetResult();
    public static void CancellationDoesNotRetryOrCache() => CancellationDoesNotRetryOrCacheAsync().GetAwaiter().GetResult();
    public static void SniffedManifestIsFullyRewritten() => SniffedManifestIsFullyRewrittenAsync().GetAwaiter().GetResult();
    public static void OversizedResourceStreamsWithoutCaching() => OversizedResourceStreamsWithoutCachingAsync().GetAwaiter().GetResult();
    public static void TruncatedContentLengthDoesNotCache() => TruncatedContentLengthDoesNotCacheAsync().GetAwaiter().GetResult();

    private static async Task FirstChunkBeforeEofAndCompletedCacheAsync()
    {
        var first = BinaryBytes(2048);
        var last = BinaryBytes(1024);
        var releaseTail = NewSignal();
        var receivedFirst = NewSignal();
        var stream = new ChunkStream([first, last], releaseTail.Task);
        using var fixture = new Fixture(_ => Response(stream, first.Length + last.Length));
        using var deadline = new CancellationTokenSource(TestTimeout);
        using var destination = new MemoryStream();
        var task = fixture.DownloadAsync(async (contentType, bytes, token) =>
        {
            Check(contentType == "video/mp2t", "Streaming must retain the playable TS content type.");
            await destination.WriteAsync(bytes, token);
            receivedFirst.TrySetResult();
        }, deadline.Token);

        try
        {
            await receivedFirst.Task.WaitAsync(TestTimeout);
            Check(!task.IsCompleted, "The downstream must receive bytes before the upstream reaches EOF.");
            Check(destination.Length > 0, "The callback must receive a nonempty first chunk.");
            releaseTail.TrySetResult();
            await task.WaitAsync(TestTimeout);
        }
        finally
        {
            releaseTail.TrySetResult();
            deadline.Cancel();
            await ObserveCompletionAsync(task);
        }

        var expected = first.Concat(last).ToArray();
        Check(destination.ToArray().SequenceEqual(expected), "Streaming must preserve every byte in order.");
        Check(stream.WasDisposed, "The completed upstream stream must be disposed.");
        using var replay = new MemoryStream();
        await fixture.DownloadAsync((_, bytes, token) => replay.WriteAsync(bytes, token).AsTask()).WaitAsync(TestTimeout);
        Check(fixture.RequestCount == 1, "A completed segment must replay from cache without another upstream request.");
        Check(replay.ToArray().SequenceEqual(expected), "Cache replay must invoke the callback with the complete payload.");
    }

    private static async Task PartialReadFailureDoesNotRetryOrCacheAsync()
    {
        var first = BinaryBytes(2048);
        var good = BinaryBytes(3072);
        var failed = new ChunkStream([first], failureAfterFirstChunk: new HttpRequestException("Simulated body reset."));
        using var fixture = new Fixture(attempt => attempt == 1
            ? Response(failed, good.Length)
            : Response(new ChunkStream([good]), good.Length));
        using var destination = new MemoryStream();
        var error = await CaptureFailureAsync(fixture.DownloadAsync((_, bytes, token) => destination.WriteAsync(bytes, token).AsTask()));
        Check(error is not null, "A body reset after partial delivery must fail the request.");
        Check(destination.Length == first.Length, "The successfully read prefix must have been forwarded before the body reset.");
        Check(fixture.RequestCount == 1 && fixture.RetryCount == 0, "Partial downstream delivery must disable upstream retries.");
        Check(failed.WasDisposed, "A failed upstream stream must be disposed.");
        await AssertFreshCompleteDownloadAsync(fixture, good);
    }

    private static async Task DownstreamWriteFailureDoesNotRetryOrCacheAsync()
    {
        var good = BinaryBytes(3072);
        var failed = new ChunkStream([good]);
        using var fixture = new Fixture(attempt => Response(attempt == 1 ? failed : new ChunkStream([good]), good.Length));
        var callbackAttempts = 0;
        var error = await CaptureFailureAsync(fixture.DownloadAsync((_, _, _) =>
        {
            callbackAttempts++;
            // A real socket write can publish some bytes before throwing.
            throw new HttpRequestException("Simulated partial downstream write.");
        }));
        Check(error is not null, "A failed downstream write must surface.");
        Check(callbackAttempts == 1 && fixture.RequestCount == 1 && fixture.RetryCount == 0,
            "The first callback attempt must prohibit retries even when it throws.");
        Check(failed.WasDisposed, "A downstream failure must dispose the upstream stream.");
        await AssertFreshCompleteDownloadAsync(fixture, good);
    }

    private static async Task CancellationDoesNotRetryOrCacheAsync()
    {
        var first = BinaryBytes(2048);
        var good = BinaryBytes(3072);
        var neverRelease = NewSignal();
        var receivedFirst = NewSignal();
        var failed = new ChunkStream([first, good], neverRelease.Task);
        using var fixture = new Fixture(attempt => attempt == 1
            ? Response(failed, first.Length + good.Length)
            : Response(new ChunkStream([good]), good.Length));
        using var cancellation = new CancellationTokenSource(TestTimeout);
        var task = fixture.DownloadAsync((_, _, _) =>
        {
            receivedFirst.TrySetResult();
            return Task.CompletedTask;
        }, cancellation.Token);
        Exception? error;
        try
        {
            await receivedFirst.Task.WaitAsync(TestTimeout);
            cancellation.Cancel();
            error = await CaptureFailureAsync(task);
        }
        finally
        {
            cancellation.Cancel();
            neverRelease.TrySetResult();
            await ObserveCompletionAsync(task);
        }

        Check(error is OperationCanceledException, "Client cancellation must propagate as cancellation.");
        Check(fixture.RequestCount == 1 && fixture.RetryCount == 0, "A canceled transfer must not retry.");
        Check(failed.WasDisposed, "Cancellation must dispose the upstream stream.");
        await AssertFreshCompleteDownloadAsync(fixture, good);
    }

    private static async Task SniffedManifestIsFullyRewrittenAsync()
    {
        const string manifest = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6.0,\nsegment-001.ts\n#EXT-X-ENDLIST\n";
        var source = Encoding.UTF8.GetBytes(manifest);
        // The extension and content type do not identify a playlist; every prefix read is split.
        var stream = new ChunkStream(source.Select(value => new[] { value }).ToArray());
        using var fixture = new Fixture(_ => Response(stream, source.Length, "application/octet-stream"), ".bin");
        using var destination = new MemoryStream();
        var callbacks = 0;
        await fixture.DownloadAsync((_, bytes, token) =>
        {
            callbacks++;
            return destination.WriteAsync(bytes, token).AsTask();
        }).WaitAsync(TestTimeout);

        var rewritten = Encoding.UTF8.GetString(destination.ToArray());
        Check(callbacks == 1, "A sniffed manifest must be rewritten completely before one downstream callback.");
        Check(rewritten.StartsWith("#EXTM3U", StringComparison.Ordinal), "The manifest header must be preserved.");
        Check(rewritten.Contains(ProxyBaseUrl + "/", StringComparison.Ordinal), "A sniffed manifest must rewrite segment URLs through the canonical proxy.");
        Check(rewritten.Contains("sessionId=" + fixture.Session.SessionId, StringComparison.Ordinal), "Rewritten URLs must retain the current session.");
        Check(!rewritten.Contains("\nsegment-001.ts\n", StringComparison.Ordinal), "A raw relative segment URL must not escape rewriting.");
        Check(fixture.Session.ProxyResources.Values.Contains(new Uri(new Uri(fixture.ResourceUrl), "segment-001.ts").AbsoluteUri),
            "The rewritten child resource must be registered in the playback session.");
        Check(stream.WasDisposed, "The manifest source stream must be disposed.");
    }

    private static async Task OversizedResourceStreamsWithoutCachingAsync()
    {
        // The established segment cache limit is 16 MiB; this transfer must exceed it.
        var body = BinaryBytes(17 * 1024 * 1024);
        var streams = new List<ChunkStream>();
        using var fixture = new Fixture(_ =>
        {
            var stream = new ChunkStream([body]);
            streams.Add(stream);
            // Unknown length exercises the incremental cache bound, not only header rejection.
            return Response(stream);
        });
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var forwarded = 0;
            await fixture.DownloadAsync((_, bytes, _) =>
            {
                Check(bytes.Span.SequenceEqual(body.AsSpan(forwarded, bytes.Length)), "Oversized streaming must retain payload order and bytes.");
                forwarded += bytes.Length;
                return Task.CompletedTask;
            }).WaitAsync(TestTimeout);
            Check(forwarded == body.Length, "Oversized resources must still stream completely.");
        }

        Check(fixture.RequestCount == 2, "A resource larger than the cache limit must be downloaded on each request.");
        Check(streams.All(stream => stream.WasDisposed), "Every oversized upstream stream must be disposed.");
    }

    private static async Task TruncatedContentLengthDoesNotCacheAsync()
    {
        var truncated = BinaryBytes(2048);
        var good = BinaryBytes(3072);
        var failed = new ChunkStream([truncated]);
        using var fixture = new Fixture(attempt => Response(attempt == 1 ? failed : new ChunkStream([good]), good.Length));
        using var destination = new MemoryStream();
        var error = await CaptureFailureAsync(fixture.DownloadAsync((_, bytes, token) => destination.WriteAsync(bytes, token).AsTask()));
        Check(error is not null, "EOF before the advertised Content-Length must fail rather than cache a truncated success.");
        Check(destination.Length == truncated.Length, "The available prefix must have been forwarded before premature EOF.");
        Check(fixture.RequestCount == 1 && fixture.RetryCount == 0, "Premature EOF after downstream delivery must not retry in the same response.");
        Check(failed.WasDisposed, "A truncated upstream stream must be disposed.");
        await AssertFreshCompleteDownloadAsync(fixture, good);
    }

    private static async Task AssertFreshCompleteDownloadAsync(Fixture fixture, byte[] expected)
    {
        using var destination = new MemoryStream();
        await fixture.DownloadAsync((_, bytes, token) => destination.WriteAsync(bytes, token).AsTask()).WaitAsync(TestTimeout);
        Check(fixture.RequestCount == 2, "An incomplete transfer must not enter the completed-resource cache.");
        Check(destination.ToArray().SequenceEqual(expected), "A later independent request must receive a fresh complete payload.");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TestTimeout);
            return null;
        }
        catch (TimeoutException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TestTimeout);
        }
        catch (Exception)
        {
            // Cleanup only: the main assertion path observes the operation's result.
        }
    }

    private static HttpResponseMessage Response(Stream stream, long? contentLength = null, string? mediaType = null)
    {
        var content = new StreamContent(stream);
        if (contentLength.HasValue)
        {
            content.Headers.ContentLength = contentLength;
        }

        if (mediaType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static byte[] BinaryBytes(int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(0x47 + i % 131);
        }

        return bytes;
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HttpClient _http;
        private readonly KodikPlaybackService _service;
        private readonly StubHandler _handler;

        public Fixture(Func<int, HttpResponseMessage> response, string extension = ".ts")
        {
            ResourceUrl = $"https://cdn.kodik.example/streaming-{Guid.NewGuid():N}/resource{extension}";
            _handler = new StubHandler(response);
            _http = new HttpClient(_handler) { Timeout = TestTimeout };
            _service = new KodikPlaybackService(_http, NullLogger.Instance, (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                RetryCount++;
                return Task.CompletedTask;
            });
            Session = KodikPlaybackService.CreateSession(new KodikLinkInfo("//cdn.kodik.example/streaming/", 720), 720);
        }

        public string ResourceUrl { get; }
        public KodikPlaybackSession Session { get; }
        public int RequestCount => _handler.RequestCount;
        public int RetryCount { get; private set; }

        public Task<KodikProxyResource> DownloadAsync(
            Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> callback,
            CancellationToken token = default) =>
            _service.DownloadProxyResourceAsync(Session, "streaming-resource", ResourceUrl, ProxyBaseUrl, token, callback);

        public void Dispose() => _http.Dispose();
    }

    private sealed class StubHandler(Func<int, HttpResponseMessage> response) : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(response(Interlocked.Increment(ref _requestCount)));
        }
    }

    private sealed class ChunkStream(
        byte[][] chunks,
        Task? waitAfterFirstChunk = null,
        Exception? failureAfterFirstChunk = null) : Stream
    {
        private int _chunkIndex;
        private int _chunkOffset;
        public bool WasDisposed { get; private set; }
        public override bool CanRead => !WasDisposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(WasDisposed, this);
            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_chunkIndex > 0)
            {
                if (waitAfterFirstChunk is not null)
                {
                    await waitAfterFirstChunk.WaitAsync(cancellationToken);
                }

                if (failureAfterFirstChunk is not null)
                {
                    throw failureAfterFirstChunk;
                }
            }

            if (_chunkIndex == chunks.Length)
            {
                return 0;
            }

            var chunk = chunks[_chunkIndex];
            var count = Math.Min(buffer.Length, chunk.Length - _chunkOffset);
            chunk.AsMemory(_chunkOffset, count).CopyTo(buffer);
            _chunkOffset += count;
            if (_chunkOffset == chunk.Length)
            {
                _chunkIndex++;
                _chunkOffset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("This test source requires asynchronous reads.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            base.Dispose(disposing);
        }
    }
}
