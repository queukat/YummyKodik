using System.Text;

namespace YummyKodik.Util;

internal static class ProxyResponseBodyReader
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(2);

    // Providers retain ownership of headers, manifest rewriting, retries and cache policy.
    internal static async Task<byte[]> ReadAsync(
        HttpResponseMessage response,
        bool isManifest,
        string contentType,
        int maxRetainedBinaryBytes,
        CancellationToken cancellationToken,
        Func<string, ReadOnlyMemory<byte>, CancellationToken, Task>? writeContentAsync)
    {
        // ResponseHeadersRead ends HttpClient's timeout at the headers. Bound the body explicitly.
        using var totalTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        totalTimeout.CancelAfter(TotalTimeout);
        using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(totalTimeout.Token);
        idleTimeout.CancelAfter(IdleTimeout);
        var ct = idleTimeout.Token;
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffered = new MemoryStream();
        var chunk = new byte[32 * 1024];
        var decided = writeContentAsync is null || isManifest;
        var streaming = false;
        var keepBuffer = true;
        long received = 0;

        while (true)
        {
            idleTimeout.CancelAfter(IdleTimeout);
            var count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            received += count;
            if (keepBuffer)
            {
                if (streaming && buffered.Length + count > maxRetainedBinaryBytes)
                {
                    keepBuffer = false;
                    buffered.SetLength(0);
                    buffered.Capacity = 0;
                }
                else
                {
                    buffered.Write(chunk, 0, count);
                }
            }

            if (!decided)
            {
                var prefix = Encoding.UTF8.GetString(buffered.GetBuffer(), 0, (int)buffered.Length)
                    .TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                // A manifest marker may span reads; don't leak an unrewritten prefix to the client.
                if (buffered.Length < 512 && prefix.Length < 7 &&
                    "#EXTM3U".StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                decided = true;
                streaming = !prefix.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
                if (streaming)
                {
                    await writeContentAsync!(contentType, buffered.GetBuffer().AsMemory(0, (int)buffered.Length), ct)
                        .ConfigureAwait(false);
                }
            }
            else if (streaming)
            {
                await writeContentAsync!(contentType, chunk.AsMemory(0, count), ct).ConfigureAwait(false);
            }
        }

        var expectedLength = response.Content.Headers.ContentLength;
        if (expectedLength.HasValue && response.Content.Headers.ContentEncoding.Count == 0 &&
            received != expectedLength.Value)
        {
            throw new IOException("Proxy received an incomplete response body.");
        }

        ct.ThrowIfCancellationRequested();
        return keepBuffer ? buffered.ToArray() : Array.Empty<byte>();
    }
}
