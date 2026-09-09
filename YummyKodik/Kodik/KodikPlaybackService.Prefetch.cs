using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Kodik;

public sealed partial class KodikPlaybackService
{
    private const int PrefetchConcurrency = 2;
    private const int MaxSharedDownloadsPerSession = 4;
    private static readonly ConditionalWeakTable<KodikPlaybackSession, PrefetchState> PrefetchStates = new();

    private static void RegisterPrefetchSegments(KodikPlaybackSession session, Uri manifestUri, string[] lines)
    {
        var segments = new List<PrefetchSegment>();
        double duration = 0;
        foreach (var line in lines)
        {
            var value = line.Trim();
            if (value.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                double.TryParse(value[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }
            else if (duration > 0 && !value.StartsWith('#') &&
                     Uri.TryCreate(manifestUri, value, out var uri))
            {
                var url = uri.AbsoluteUri;
                segments.Add(new PrefetchSegment(BuildResourceId(url), url, duration));
                duration = 0;
            }
        }

        if (segments.Count > 0)
        {
            var state = PrefetchStates.GetOrCreateValue(session);
            lock (state.Gate)
            {
                if (!state.Segments.Select(segment => segment.Id).SequenceEqual(segments.Select(segment => segment.Id)))
                {
                    state.RequestedIndex = -1;
                    state.Attempted.Clear();
                }

                state.Segments = segments.ToArray();
                state.LastDemandUtc = DateTime.UtcNow;
            }
        }
    }

    private async Task<CachedKodikProxyResource> DownloadWithPrefetchAsync(
        KodikPlaybackSession session,
        string resourceId,
        string upstreamUrl,
        CancellationToken cancellationToken,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? writer)
    {
        if (!PrefetchStates.TryGetValue(session, out var state))
        {
            return await DownloadProxyResourcePayloadAsync(upstreamUrl, cancellationToken, writer).ConfigureAwait(false);
        }

        int index;
        lock (state.Gate)
        {
            index = Array.FindIndex(state.Segments, segment => segment.Id == resourceId);
            if (index >= 0)
            {
                if (index < state.RequestedIndex || index > state.RequestedIndex + 1)
                {
                    state.Attempted.Clear();
                }

                state.RequestedIndex = index;
                state.LastDemandUtc = DateTime.UtcNow;
            }
        }

        if (index < 0)
        {
            return await DownloadProxyResourcePayloadAsync(upstreamUrl, cancellationToken, writer).ConfigureAwait(false);
        }

        CancelObsoletePrefetches(state);
        if (TryGetCachedResource(upstreamUrl, out var cached))
        {
            QueuePrefetch(session);
            return cached;
        }

        var transfer = GetOrStartSharedDownload(state, resourceId, upstreamUrl, consumer: true);
        QueuePrefetch(session);
        if (transfer is null)
        {
            return await DownloadProxyResourcePayloadAsync(upstreamUrl, cancellationToken, writer).ConfigureAwait(false);
        }

        var forwarded = false;
        try
        {
            await transfer.Ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var prefix = transfer.Prefix;
            if (writer is not null && prefix.Length > 0)
            {
                forwarded = true;
                await writer(transfer.ContentType, prefix, cancellationToken).ConfigureAwait(false);
            }

            var payload = await transfer.Completion.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (writer is not null && forwarded && payload.Body.Length > prefix.Length)
            {
                await writer(transfer.ContentType, payload.Body.AsMemory(prefix.Length), cancellationToken).ConfigureAwait(false);
            }

            return payload;
        }
        catch (PrefetchUnavailableException) when (!forwarded)
        {
            return await DownloadProxyResourcePayloadAsync(upstreamUrl, cancellationToken, writer).ConfigureAwait(false);
        }
        finally
        {
            if (Interlocked.Decrement(ref transfer.Consumers) == 0)
            {
                CancelObsoletePrefetches(state);
            }
        }
    }

    private PendingDownload? GetOrStartSharedDownload(PrefetchState state, string resourceId, string upstreamUrl, bool consumer = false)
    {
        PendingDownload transfer;
        lock (state.Gate)
        {
            if (state.Downloads.TryGetValue(resourceId, out var existing))
            {
                if (existing.Cancellation.IsCancellationRequested)
                {
                    return null;
                }

                if (consumer)
                {
                    Interlocked.Increment(ref existing.Consumers);
                }

                return existing;
            }

            if (state.Ineligible.Contains(resourceId) || state.Downloads.Count >= MaxSharedDownloadsPerSession)
            {
                return null;
            }

            transfer = new PendingDownload(pending => RunSharedDownloadAsync(state, resourceId, upstreamUrl, pending));
            transfer.Consumers = consumer ? 1 : 0;
            state.Downloads[resourceId] = transfer;
        }

        _ = ObserveSharedDownloadAsync(transfer);
        return transfer;
    }

    private async Task<CachedKodikProxyResource> RunSharedDownloadAsync(
        PrefetchState state, string resourceId, string upstreamUrl, PendingDownload transfer)
    {
        try
        {
            return await DownloadProxyResourcePayloadAsync(
                upstreamUrl,
                transfer.Cancellation.Token,
                (contentType, bytes, _) =>
                {
                    if (!transfer.Ready.Task.IsCompleted)
                    {
                        transfer.ContentType = contentType;
                        transfer.Prefix = bytes.ToArray();
                        transfer.Ready.TrySetResult();
                    }

                    return Task.CompletedTask;
                },
                requireCacheableLength: true).ConfigureAwait(false);
        }
        catch (PrefetchUnavailableException)
        {
            lock (state.Gate)
            {
                state.Ineligible.Add(resourceId);
            }

            throw;
        }
        finally
        {
            // Wake consumers on empty payloads and failures too. Completion carries the actual outcome.
            transfer.Ready.TrySetResult();
            lock (state.Gate)
            {
                state.Downloads.Remove(resourceId);
                transfer.Cancellation.Dispose();
            }
        }
    }

    private async Task ObserveSharedDownloadAsync(PendingDownload transfer)
    {
        try
        {
            await transfer.Completion.Value.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Kodik buffered download did not complete.");
        }
    }

    private void QueuePrefetch(KodikPlaybackSession session)
    {
        if (!PrefetchStates.TryGetValue(session, out var state))
        {
            return;
        }

        Interlocked.Exchange(ref state.RefillRequested, 1);
        if (Interlocked.CompareExchange(ref state.WorkerRunning, 1, 0) == 0)
        {
            _ = RunPrefetchAsync(session, state);
        }
    }

    private async Task RunPrefetchAsync(KodikPlaybackSession session, PrefetchState state)
    {
        try
        {
            do
            {
                Interlocked.Exchange(ref state.RefillRequested, 0);
                await Task.WhenAll(Enumerable.Range(0, PrefetchConcurrency)
                    .Select(_ => RunPrefetchWorkerAsync(session, state))).ConfigureAwait(false);
            }
            while (Volatile.Read(ref state.RefillRequested) != 0);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Kodik lookahead buffering stopped.");
        }
        finally
        {
            Volatile.Write(ref state.WorkerRunning, 0);
            if (Volatile.Read(ref state.RefillRequested) != 0)
            {
                QueuePrefetch(session);
            }
        }
    }

    private async Task RunPrefetchWorkerAsync(KodikPlaybackSession session, PrefetchState state)
    {
        while (session.ExpiresAtUtc > DateTime.UtcNow)
        {
            PrefetchSegment? candidate;
            lock (state.Gate)
            {
                if (DateTime.UtcNow - state.LastDemandUtc > TimeSpan.FromSeconds(30) &&
                    state.Downloads.Values.All(download => Volatile.Read(ref download.Consumers) == 0))
                {
                    return;
                }

                candidate = GetPrefetchWindow(state).FirstOrDefault(segment =>
                    !state.Attempted.Contains(segment.Id) && !state.Ineligible.Contains(segment.Id) &&
                    !state.Downloads.ContainsKey(segment.Id) && !TryGetCachedResource(segment.Url, out _));
                if (candidate is null)
                {
                    return;
                }

                state.Attempted.Add(candidate.Id);
            }

            var transfer = GetOrStartSharedDownload(state, candidate.Id, candidate.Url);
            if (transfer is not null)
            {
                await ObserveSharedDownloadAsync(transfer).ConfigureAwait(false);
            }
            else
            {
                Task[] pending;
                lock (state.Gate)
                {
                    state.Attempted.Remove(candidate.Id);
                    pending = state.Downloads.Values.Select(download => (Task)download.Completion.Value).ToArray();
                }

                // Capacity is temporary: preserve this candidate until an occupied slot becomes free.
                if (pending.Length > 0)
                {
                    await Task.WhenAny(pending).ConfigureAwait(false);
                }
            }
        }
    }

    private static IEnumerable<PrefetchSegment> GetPrefetchWindow(PrefetchState state)
    {
        var seconds = 0d;
        var count = 0;
        for (var i = state.RequestedIndex + 1; i < state.Segments.Length && count < 48; i++)
        {
            var segment = state.Segments[i];
            yield return segment;
            seconds += segment.Duration;
            count++;
            if (seconds >= 120)
            {
                yield break;
            }
        }
    }

    private static void CancelObsoletePrefetches(PrefetchState state)
    {
        lock (state.Gate)
        {
            var wanted = GetPrefetchWindow(state).Select(segment => segment.Id).ToHashSet(StringComparer.Ordinal);
            if (state.RequestedIndex >= 0 && state.RequestedIndex < state.Segments.Length)
            {
                wanted.Add(state.Segments[state.RequestedIndex].Id);
            }

            // Cancellation callbacks can finish downloads synchronously and remove dictionary entries.
            foreach (var (id, transfer) in state.Downloads.ToArray())
            {
                if (!wanted.Contains(id) && Volatile.Read(ref transfer.Consumers) == 0)
                {
                    transfer.Cancellation.Cancel();
                }
            }
        }
    }

    private sealed record PrefetchSegment(string Id, string Url, double Duration);
    private sealed class PrefetchUnavailableException : Exception;

    private sealed class PrefetchState
    {
        public object Gate { get; } = new();
        public PrefetchSegment[] Segments = [];
        public int RequestedIndex = -1;
        public DateTime LastDemandUtc = DateTime.UtcNow;
        public int WorkerRunning;
        public int RefillRequested;
        public Dictionary<string, PendingDownload> Downloads { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Attempted { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Ineligible { get; } = new(StringComparer.Ordinal);
    }

    private sealed class PendingDownload
    {
        public PendingDownload(Func<PendingDownload, Task<CachedKodikProxyResource>> download)
        {
            Completion = new Lazy<Task<CachedKodikProxyResource>>(() => download(this));
        }

        public Lazy<Task<CachedKodikProxyResource>> Completion { get; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenSource Cancellation { get; } = new();
        public byte[] Prefix = [];
        public string ContentType = "application/octet-stream";
        public int Consumers;
    }
}
