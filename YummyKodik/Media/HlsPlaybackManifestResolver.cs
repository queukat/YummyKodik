using System.Globalization;
using System.Net.Http;

namespace YummyKodik.Media;

/// <summary>Binds playback and duration to one gateway-selected provider session.</summary>
internal static class HlsPlaybackManifestResolver
{
    internal sealed record ResolvedManifest(string Url, long RunTimeTicks, float? FrameRate);

    public static async Task<ResolvedManifest> ResolveAsync(HttpClient client, string gatewayUrl, CancellationToken cancellationToken)
    {
        var gateway = new Uri(gatewayUrl);
        var current = gateway;
        float? selectedFrameRate = null;
        for (var depth = 0; depth < 4; depth++)
        {
            using var response = await client.GetAsync(current, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var responseUri = response.RequestMessage?.RequestUri ?? current;
            var location = response.Content.Headers.ContentLocation;
            var resolved = location == null ? responseUri : new Uri(responseUri, location);
            if (!IsLocalProxy(gateway, resolved))
            {
                throw new InvalidOperationException("Playback manifest did not identify a stable local provider session.");
            }

            var manifest = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var lines = manifest.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0 || lines[0] != "#EXTM3U")
            {
                throw new InvalidOperationException("Playback response is not an HLS manifest.");
            }

            var durations = lines.Where(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal)).ToArray();
            if (durations.Length > 0)
            {
                if (!lines.Contains("#EXT-X-ENDLIST", StringComparer.Ordinal))
                {
                    throw new InvalidOperationException("An incomplete playlist cannot define an episode's duration.");
                }

                double seconds = 0;
                foreach (var line in durations)
                {
                    if (!double.TryParse(line[8..].Split(',')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                        !double.IsFinite(duration) || duration <= 0)
                    {
                        throw new InvalidOperationException("Invalid HLS segment duration.");
                    }
                    seconds += duration;
                }

                if (!double.IsFinite(seconds) || seconds > TimeSpan.MaxValue.TotalSeconds)
                {
                    throw new InvalidOperationException("Invalid HLS total duration.");
                }
                return new ResolvedManifest(resolved.AbsoluteUri, TimeSpan.FromSeconds(seconds).Ticks, selectedFrameRate);
            }

            var variantIndex = Array.FindIndex(lines, line => !line.StartsWith('#'));
            var variant = variantIndex < 0 ? null : lines[variantIndex];
            if (variantIndex >= 0)
            {
                // The first rendition is also the stream Jellyfin would select as 0:0.
                // Carry its declared frame rate across nested media playlists.
                var streamInfo = lines.Take(variantIndex)
                    .LastOrDefault(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase));
                selectedFrameRate = HlsVideoFrameRatePolicy.ParseDeclaredFrameRate(streamInfo) ?? selectedFrameRate;
            }

            if (variant == null || !Uri.TryCreate(resolved, variant, out current) || !IsLocalProxy(gateway, current))
            {
                throw new InvalidOperationException("Playback master has no local media playlist.");
            }
        }

        throw new InvalidOperationException("Playback playlist nesting limit exceeded.");
    }

    private static bool IsLocalProxy(Uri gateway, Uri url)
    {
        var routeStart = gateway.AbsolutePath.LastIndexOf("/YummyKodik/", StringComparison.OrdinalIgnoreCase);
        if (routeStart < 0) return false;
        var routeRoot = gateway.AbsolutePath[..routeStart] + "/YummyKodik/";
        return url.Scheme == gateway.Scheme && url.Authority == gateway.Authority &&
            (url.AbsolutePath.StartsWith(routeRoot + "alloha-proxy/", StringComparison.OrdinalIgnoreCase) ||
             url.AbsolutePath.StartsWith(routeRoot + "cvh-proxy/", StringComparison.OrdinalIgnoreCase) ||
             url.AbsolutePath.StartsWith(routeRoot + "kodik-proxy/", StringComparison.OrdinalIgnoreCase));
    }
}
