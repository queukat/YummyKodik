using System.Globalization;
using System.Net.Http;

namespace YummyKodik.Media;

/// <summary>Binds playback and duration to one gateway-selected provider session.</summary>
internal static class HlsPlaybackManifestResolver
{
    internal sealed record ResolvedManifest(string Url, long RunTimeTicks);

    public static async Task<ResolvedManifest> ResolveAsync(HttpClient client, string gatewayUrl, CancellationToken cancellationToken)
    {
        var gateway = new Uri(gatewayUrl);
        var current = gateway;
        Uri? playback = null;
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

            playback ??= resolved;
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
                return new ResolvedManifest(playback.AbsoluteUri, TimeSpan.FromSeconds(seconds).Ticks);
            }

            var variant = lines.FirstOrDefault(line => !line.StartsWith('#'));
            if (variant == null || !Uri.TryCreate(resolved, variant, out current) || !IsLocalProxy(gateway, current))
            {
                throw new InvalidOperationException("Playback master has no local media playlist.");
            }
        }

        throw new InvalidOperationException("Playback playlist nesting limit exceeded.");
    }

    private static bool IsLocalProxy(Uri gateway, Uri url)
    {
        return url.Scheme == gateway.Scheme && url.Authority == gateway.Authority &&
            (url.AbsolutePath.StartsWith("/YummyKodik/alloha-proxy/", StringComparison.OrdinalIgnoreCase) ||
             url.AbsolutePath.StartsWith("/YummyKodik/cvh-proxy/", StringComparison.OrdinalIgnoreCase) ||
             url.AbsolutePath.StartsWith("/YummyKodik/kodik-proxy/", StringComparison.OrdinalIgnoreCase));
    }
}
