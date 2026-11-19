using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace YummyKodik.Media;

/// <summary>Uses the selected HLS rendition's advertised bitrate instead of a resolution-based fallback.</summary>
internal static class HlsVariantBitratePolicy
{
    private const int MaximumReasonableBitrate = 1_000_000_000;

    private static readonly Regex AverageBandwidth = new(
        @"(?:^|,)AVERAGE-BANDWIDTH=(?<value>[0-9]+)(?:,|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PeakBandwidth = new(
        @"(?:^|,)BANDWIDTH=(?<value>[0-9]+)(?:,|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal static int? ParseDeclaredBitrate(string? streamInfo)
    {
        if (streamInfo == null || !streamInfo.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var attributes = streamInfo[(streamInfo.IndexOf(':') + 1)..];
        return ParseValue(AverageBandwidth.Match(attributes))
            ?? ParseValue(PeakBandwidth.Match(attributes));
    }

    internal static void Apply(MediaSourceInfo source, int? bitrate)
    {
        if (!bitrate.HasValue || bitrate.Value <= 0 || bitrate.Value > MaximumReasonableBitrate)
        {
            return;
        }

        var video = source.MediaStreams.FirstOrDefault(stream => stream.Type == MediaStreamType.Video);
        if (video is null) return;

        var audioBitrate = source.MediaStreams
            .Where(stream => stream.Type == MediaStreamType.Audio && stream.BitRate is > 0)
            .Sum(stream => (long)stream.BitRate!.Value);
        // The playlist's bandwidth describes the combined rendition. Subtract
        // only known audio rates; an absent estimate must not add phantom bitrate.
        video.BitRate = audioBitrate > 0 && audioBitrate < bitrate.Value
            ? (int)(bitrate.Value - audioBitrate)
            : bitrate.Value;
        source.Bitrate = bitrate.Value;
    }

    private static int? ParseValue(Match match)
    {
        if (!match.Success ||
            !int.TryParse(match.Groups["value"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 || value > MaximumReasonableBitrate)
        {
            return null;
        }
        return value;
    }
}
