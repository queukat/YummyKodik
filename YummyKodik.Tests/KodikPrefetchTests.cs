using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Kodik;

internal static class KodikPrefetchTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static void PrefetchHasTwoBackgroundWorkers() => PrefetchHasTwoBackgroundWorkersAsync().GetAwaiter().GetResult();
    public static void ForegroundJoinsPrefetchWithoutDuplicateDownload() => ForegroundJoinsPrefetchWithoutDuplicateDownloadAsync().GetAwaiter().GetResult();
    public static void CanceledConsumerPreservesSharedDownload() => CanceledConsumerPreservesSharedDownloadAsync().GetAwaiter().GetResult();
    public static void SeekThenConsumerCancellationStopsObsoleteDownload() => SeekThenConsumerCancellationStopsObsoleteDownloadAsync().GetAwaiter().GetResult();
    public static void IneligibleRegisteredResourcesStreamDirectly() => IneligibleRegisteredResourcesStreamDirectlyAsync().GetAwaiter().GetResult();

    private static async Task PrefetchHasTwoBackgroundWorkersAsync()
    {
        using var fixture = new Fixture(5);
        await fixture.StartAsync();
        await Task.WhenAll(fixture.Segments[1].WaitingForTail.Task, fixture.Segments[2].WaitingForTail.Task).WaitAsync(Timeout);
        Check(fixture.Segments[1].Requests == 1 && fixture.Segments[2].Requests == 1,
            "The two nearest segments must begin prefetching before the player requests them.");
        Check(fixture.Segments[3].Requests == 0 && fixture.Segments[4].Requests == 0,
            "Later segments must wait while both background workers are occupied.");

        fixture.Segments[1].ReleaseTail.TrySetResult();
        await fixture.Segments[3].WaitingForTail.Task.WaitAsync(Timeout);
        Check(fixture.Segments[2].Requests == 1, "Starting the next candidate must not duplicate the other active transfer.");
        fixture.ReleaseAll();
        await Task.WhenAll(fixture.Segments.Skip(1).Select(segment => segment.Completed.Task)).WaitAsync(Timeout);
        Check(fixture.PeakBackgroundRequests == 2, "The whole prefetch window must remain limited to two concurrent background transfers.");
        Check(fixture.Segments.All(segment => segment.Requests == 1), "Each completed segment in the window must have exactly one upstream request.");
    }

    private static async Task ForegroundJoinsPrefetchWithoutDuplicateDownloadAsync()
    {
        using var fixture = new Fixture(2);
        await fixture.StartAsync();
        var segment = fixture.Segments[1];
        await segment.WaitingForTail.Task.WaitAsync(Timeout);
        using var received = new MemoryStream();
        var prefixArrived = Signal();
        var download = fixture.DownloadAsync(1, async (_, bytes, token) =>
        {
            await received.WriteAsync(bytes, token);
            if (bytes.Length > 0)
            {
                prefixArrived.TrySetResult();
            }
        });

        await prefixArrived.Task.WaitAsync(Timeout);
        Check(!download.IsCompleted, "Joining foreground must receive a prefix while the shared upstream still waits for its tail.");
        Check(segment.Requests == 1, "Foreground must join the existing prefetch instead of issuing another HTTP request.");
        segment.ReleaseTail.TrySetResult();
        await download.WaitAsync(Timeout);
        Check(received.ToArray().SequenceEqual(segment.Body), "Prefix plus completed suffix must equal the original segment exactly, without overlap or gaps.");
        Check(segment.Requests == 1, "Completion must not trigger a duplicate upstream download.");
    }

    private static async Task CanceledConsumerPreservesSharedDownloadAsync()
    {
        using var fixture = new Fixture(2);
        await fixture.StartAsync();
        var segment = fixture.Segments[1];
        await segment.WaitingForTail.Task.WaitAsync(Timeout);
        using var consumerCancellation = new CancellationTokenSource(Timeout);
        var firstPrefix = Signal();
        var first = fixture.DownloadAsync(1, (_, bytes, _) =>
        {
            if (bytes.Length > 0)
            {
                firstPrefix.TrySetResult();
            }

            return Task.CompletedTask;
        }, consumerCancellation.Token);
        await firstPrefix.Task.WaitAsync(Timeout);
        consumerCancellation.Cancel();
        var error = await CaptureFailureAsync(first);
        Check(error is OperationCanceledException, "Canceled foreground must detach promptly as cancellation.");
        Check(segment.CanceledReads == 0 && !segment.Completed.Task.IsCompleted,
            "Canceling a consumer must leave the prefetched upstream transfer alive.");

        using var received = new MemoryStream();
        var nextPrefix = Signal();
        var next = fixture.DownloadAsync(1, async (_, bytes, token) =>
        {
            await received.WriteAsync(bytes, token);
            if (bytes.Length > 0)
            {
                nextPrefix.TrySetResult();
            }
        });
        await nextPrefix.Task.WaitAsync(Timeout);
        Check(segment.Requests == 1, "A replacement consumer must replay retained progress without restarting upstream.");
        segment.ReleaseTail.TrySetResult();
        await next.WaitAsync(Timeout);
        Check(received.ToArray().SequenceEqual(segment.Body), "The replacement consumer must receive the complete segment.");
        Check(segment.Requests == 1 && segment.CanceledReads == 0, "Consumer cancellation must not discard or duplicate shared download progress.");
    }

    private static async Task SeekThenConsumerCancellationStopsObsoleteDownloadAsync()
    {
        using var fixture = new Fixture(30);
        await fixture.StartAsync();
        var oldSegment = fixture.Segments[1];
        await oldSegment.WaitingForTail.Task.WaitAsync(Timeout);
        using var oldConsumerCancellation = new CancellationTokenSource(Timeout);
        using var nextConsumerCancellation = new CancellationTokenSource(Timeout);
        var oldPrefix = Signal();
        var oldDownload = fixture.DownloadAsync(1, (_, bytes, _) =>
        {
            if (bytes.Length > 0)
            {
                oldPrefix.TrySetResult();
            }

            return Task.CompletedTask;
        }, oldConsumerCancellation.Token);
        await oldPrefix.Task.WaitAsync(Timeout);

        // Segment 25 is beyond the original two-minute lookahead window.
        var nextSegment = fixture.Segments[25];
        var nextPrefix = Signal();
        using var received = new MemoryStream();
        var nextDownload = fixture.DownloadAsync(25, async (_, bytes, token) =>
        {
            await received.WriteAsync(bytes, token);
            if (bytes.Length > 0)
            {
                nextPrefix.TrySetResult();
            }
        }, nextConsumerCancellation.Token);
        await nextPrefix.Task.WaitAsync(Timeout);
        Check(oldSegment.CanceledReads == 0 && !oldSegment.Completed.Task.IsCompleted,
            "Changing the window must preserve an obsolete transfer while its foreground consumer remains attached.");

        oldConsumerCancellation.Cancel();
        var error = await CaptureFailureAsync(oldDownload);
        Check(error is OperationCanceledException, "The old foreground request must detach on cancellation.");
        await oldSegment.Completed.Task.WaitAsync(Timeout);
        Check(oldSegment.CanceledReads == 1 && oldSegment.CompletedBodies == 0,
            "Detaching the last consumer after seek must cancel the obsolete upstream without waiting for its tail.");
        Check(nextSegment.CanceledReads == 0 && !nextDownload.IsCompleted,
            "Canceling the obsolete transfer must leave the current requested segment alive.");

        nextSegment.ReleaseTail.TrySetResult();
        await nextDownload.WaitAsync(Timeout);
        Check(received.ToArray().SequenceEqual(nextSegment.Body), "The requested segment must still complete intact after obsolete cancellation.");
    }

    private static async Task IneligibleRegisteredResourcesStreamDirectlyAsync()
    {
        foreach (var oversized in new[] { false, true })
        {
            using var fixture = new Fixture(2, oversized ? 17 * 1024 * 1024 : 4096, knownLength: oversized);
            var segment = fixture.Segments[1];
            segment.ReleaseTail.TrySetResult();
            await fixture.StartAsync();
            await segment.RequestStarted.Task.WaitAsync(Timeout);
            var received = 0;
            await fixture.DownloadAsync(1, (_, bytes, _) =>
            {
                Check(bytes.Span.SequenceEqual(segment.Body.AsSpan(received, bytes.Length)),
                    "An ineligible registered segment must stream its actual bytes in order.");
                received += bytes.Length;
                return Task.CompletedTask;
            }).WaitAsync(Timeout);
            Check(received == segment.Body.Length, "Unknown or oversized Content-Length must not make a registered segment permanently unplayable.");
            Check(segment.Requests <= 2, "Header-only prefetch rejection may require at most one fresh direct request.");
            Check(segment.CompletedBodies == 1, "Ineligible prefetch must stop before downloading a body that direct streaming would download again.");
        }
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
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

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        private readonly string _manifestUrl;
        private readonly string _origin;
        private int _activeBackgroundRequests;
        private int _peakBackgroundRequests;

        public Fixture(int segmentCount, int otherSegmentBytes = 4096, bool knownLength = true)
        {
            _origin = $"https://cdn.kodik.example/prefetch-{Guid.NewGuid():N}/";
            _manifestUrl = _origin + "playlist.m3u8";
            Segments = Enumerable.Range(0, segmentCount)
                .Select(index => new Segment(index == 0 ? 2048 : otherSegmentBytes, index == 0 || knownLength))
                .ToArray();
            Segments[0].ReleaseTail.TrySetResult();
            _http = new HttpClient(new Handler(Respond)) { Timeout = Timeout };
            _service = new KodikPlaybackService(_http, NullLogger.Instance, (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
            Session = KodikPlaybackService.CreateSession(new KodikLinkInfo(_origin, 720), 720);
        }

        public KodikPlaybackSession Session { get; }
        public Segment[] Segments { get; }
        public int PeakBackgroundRequests => Volatile.Read(ref _peakBackgroundRequests);

        public async Task StartAsync()
        {
            await _service.DownloadProxyResourceAsync(Session, "manifest", _manifestUrl,
                "/YummyKodik/kodik-proxy", CancellationToken.None).WaitAsync(Timeout);
            Check(Segments.Select((_, index) => ResourceUrl(index)).All(url => Session.ProxyResources.Values.Contains(url)),
                "The fixture must exercise resources registered through an actual rewritten EXTINF playlist.");
            await DownloadAsync(0, (_, _, _) => Task.CompletedTask).WaitAsync(Timeout);
        }

        public Task<KodikProxyResource> DownloadAsync(int index,
            Func<string, ReadOnlyMemory<byte>, CancellationToken, Task> callback,
            CancellationToken token = default)
        {
            var url = ResourceUrl(index);
            var key = Session.ProxyResources.Single(pair => pair.Value == url).Key;
            return _service.DownloadProxyResourceAsync(Session, key, url, "/YummyKodik/kodik-proxy", token, callback);
        }

        public void ReleaseAll()
        {
            foreach (var segment in Segments)
            {
                segment.ReleaseTail.TrySetResult();
            }
        }

        public void Dispose()
        {
            ReleaseAll();
            _http.Dispose();
        }

        private string ResourceUrl(int index) => _origin + $"segment-{index:000}.ts";

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == _manifestUrl)
            {
                var text = "#EXTM3U\n#EXT-X-TARGETDURATION:6\n"
                    + string.Concat(Enumerable.Range(0, Segments.Length).Select(index => $"#EXTINF:6.0,\nsegment-{index:000}.ts\n"))
                    + "#EXT-X-ENDLIST\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(text, Encoding.UTF8, "application/vnd.apple.mpegurl")
                };
            }

            var index = -1;
            for (var candidate = 0; candidate < Segments.Length; candidate++)
            {
                if (ResourceUrl(candidate) == url)
                {
                    index = candidate;
                    break;
                }
            }

            Check(index >= 0, "Unexpected HTTP resource in prefetch fixture.");
            var spec = Segments[index];
            Interlocked.Increment(ref spec.Requests);
            if (index > 0)
            {
                var active = Interlocked.Increment(ref _activeBackgroundRequests);
                int previous;
                do
                {
                    previous = Volatile.Read(ref _peakBackgroundRequests);
                    if (previous >= active)
                    {
                        break;
                    }
                }
                while (Interlocked.CompareExchange(ref _peakBackgroundRequests, active, previous) != previous);
            }

            var content = new StreamContent(new GatedStream(spec, () =>
            {
                if (index > 0)
                {
                    Interlocked.Decrement(ref _activeBackgroundRequests);
                }
            }));
            if (spec.KnownLength)
            {
                content.Headers.ContentLength = spec.Body.Length;
            }

            spec.RequestStarted.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Segment
    {
        public Segment(int length, bool knownLength)
        {
            Body = new byte[length];
            for (var i = 0; i < length; i++)
            {
                Body[i] = (byte)(0x47 + i % 131);
            }

            KnownLength = knownLength;
        }

        public byte[] Body { get; }
        public bool KnownLength { get; }
        public TaskCompletionSource RequestStarted { get; } = Signal();
        public TaskCompletionSource WaitingForTail { get; } = Signal();
        public TaskCompletionSource ReleaseTail { get; } = Signal();
        public TaskCompletionSource Completed { get; } = Signal();
        public int Requests;
        public int CanceledReads;
        public int CompletedBodies;
    }

    private sealed class GatedStream(Segment segment, Action onDispose) : Stream
    {
        private int _offset;
        private int _disposed;
        private bool _reachedEnd;
        public override bool CanRead => Volatile.Read(ref _disposed) == 0;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_offset >= 1024)
                {
                    segment.WaitingForTail.TrySetResult();
                    await segment.ReleaseTail.Task.WaitAsync(cancellationToken);
                }

                var count = Math.Min(buffer.Length, segment.Body.Length - _offset);
                if (_offset < 1024)
                {
                    count = Math.Min(count, 1024 - _offset);
                }

                if (count == 0)
                {
                    if (!_reachedEnd)
                    {
                        _reachedEnd = true;
                        Interlocked.Increment(ref segment.CompletedBodies);
                    }

                    return 0;
                }

                segment.Body.AsMemory(_offset, count).CopyTo(buffer);
                _offset += count;
                return count;
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref segment.CanceledReads);
                throw;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                onDispose();
                segment.Completed.TrySetResult();
            }

            base.Dispose(disposing);
        }
    }
}
