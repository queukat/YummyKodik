using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace YummyKodik.Alloha;

public sealed partial class AllohaPlaybackService
{
    /// <summary>Check video access before the gateway commits to this provider's cut.</summary>
    internal async Task EnsurePlaybackReadyAsync(
        AllohaPlaybackSession session, string proxyBaseUrl, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await session.PlaybackReadinessSemaphore.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (TryGetProxyResourceRefreshCooldown(session, out _))
                    throw new InvalidOperationException("Alloha playback recovery is temporarily unavailable.");
                if (session.PlaybackReady) return;

                var manifest = BuildManifestResponseBody(session, proxyBaseUrl);
                for (var depth = 0; depth < 4; depth++)
                {
                    var lines = manifest.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0 || lines[0] != "#EXTM3U")
                        throw new InvalidOperationException("Alloha playback response is not an HLS playlist.");

                    var mediaPlaylist = lines.Any(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal));
                    foreach (var line in lines)
                    {
                        // Verify dependencies of the first fragment, without downloading the episode
                        // or starting background prefetch before the player actually requests video.
                        if (mediaPlaylist && (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                                              line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal)))
                        {
                            var uri = ManifestUriAttributeRegex.Match(line);
                            if (uri.Success) await ReadResourceAsync(uri.Groups["uri"].Value).ConfigureAwait(false);
                        }
                        if (line.StartsWith('#')) continue;

                        var resource = await ReadResourceAsync(line).ConfigureAwait(false);
                        if (mediaPlaylist)
                        {
                            session.PlaybackReady = true;
                            return;
                        }
                        manifest = Encoding.UTF8.GetString(resource.Content);
                        break;
                    }
                }
                throw new InvalidOperationException("Alloha playback has no reachable media fragment within the playlist nesting limit.");
            }
            finally
            {
                session.PlaybackReadinessSemaphore.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Alloha video readiness check timed out.");
        }

        async Task<AllohaProxyResource> ReadResourceAsync(string proxyUrl)
        {
            // Jellyfin emits root-relative proxy URLs (including an optional base path).
            // Only the generated query is needed to resolve the resource in this session.
            var queryStart = proxyUrl.IndexOf('?');
            var resourceId = QueryHelpers.ParseQuery(queryStart >= 0 ? proxyUrl[queryStart..] : string.Empty)["resource"].ToString();
            if (!TryResolveProxyResourceUrl(session, resourceId, out var upstreamUrl))
                throw new InvalidOperationException("Alloha readiness resource is missing from the session.");
            var resource = await DownloadProxyResourceCoreAsync(
                session, resourceId, upstreamUrl, proxyBaseUrl, false, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            if (resource.Content.Length == 0 || resource.ContentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Alloha returned no playable resource.");
            return resource;
        }
    }
}
