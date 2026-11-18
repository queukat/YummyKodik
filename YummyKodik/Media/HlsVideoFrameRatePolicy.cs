using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Entities;

namespace YummyKodik.Media;

/// <summary>
/// Preserves verified HLS video frame rates without confusing MPEG-TS's
/// 90 kHz time base with 90,000 frames per second.
/// </summary>
internal static class HlsVideoFrameRatePolicy
{
    private const float MaximumPlausibleFrameRate = 240f;

    private static readonly Regex FrameRateAttribute = new(
        @"(?:^|,)FRAME-RATE=(?<rate>[0-9]+(?:\.[0-9]+)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static float? ParseDeclaredFrameRate(string? streamInfo)
    {
        if (streamInfo == null || !streamInfo.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var attributes = streamInfo[(streamInfo.IndexOf(':') + 1)..];
        var match = FrameRateAttribute.Match(attributes);
        if (!match.Success ||
            !float.TryParse(match.Groups["rate"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rate) ||
            !IsPlausible(rate))
        {
            return null;
        }

        return rate;
    }

    internal static void Normalize(IEnumerable<MediaStream> streams, float? declaredFrameRate)
    {
        var declared = IsPlausible(declaredFrameRate) ? declaredFrameRate : null;

        foreach (var stream in streams.Where(stream => stream.Type == MediaStreamType.Video))
        {
            var average = IsPlausible(stream.AverageFrameRate) ? stream.AverageFrameRate : null;
            var real = IsPlausible(stream.RealFrameRate) ? stream.RealFrameRate : null;

            // Prefer an actual probe result, then a declared value belonging
            // to the selected rendition. An unknown FPS stays unknown.
            real ??= average ?? declared;
            stream.RealFrameRate = real;
            stream.AverageFrameRate = average ?? (real == declared ? declared : null);
        }
    }

    private static bool IsPlausible(float? rate)
        => rate.HasValue && float.IsFinite(rate.Value) && rate.Value > 0 && rate.Value <= MaximumPlausibleFrameRate;
}
