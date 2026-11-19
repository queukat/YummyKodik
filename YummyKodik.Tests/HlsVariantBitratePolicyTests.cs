using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using YummyKodik.Media;

internal static class HlsVariantBitratePolicyTests
{
    public static void ReadsOnlySelectedVariantBandwidth()
    {
        const string info = "#EXT-X-STREAM-INF:BANDWIDTH=5200000,AVERAGE-BANDWIDTH=4400000,RESOLUTION=1920x1080";
        if (HlsVariantBitratePolicy.ParseDeclaredBitrate(info) != 4400000)
            throw new Exception("Average HLS bandwidth should take priority over peak bandwidth.");
        if (HlsVariantBitratePolicy.ParseDeclaredBitrate(
                "#EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720") != 2000000)
            throw new Exception("Peak HLS bandwidth must be used when average bandwidth is absent.");
        if (HlsVariantBitratePolicy.ParseDeclaredBitrate(
                "#EXT-X-STREAM-INF:BANDWIDTH=999999999999,AVERAGE-BANDWIDTH=2500000") != 2500000)
            throw new Exception("An implausible peak must not suppress a valid average.");
    }

    public static void ReplacesProbedResolutionGuessWithoutTouchingAudio()
    {
        var video = new MediaStream { Type = MediaStreamType.Video, Index = 0, BitRate = 20000000 };
        var audio = new MediaStream { Type = MediaStreamType.Audio, Index = 1, BitRate = 192000 };
        var source = new MediaSourceInfo
        {
            MediaStreams = [video, audio],
            Bitrate = 20192000
        };
        HlsVariantBitratePolicy.Apply(source, 4500000);
        if (source.Bitrate != 4500000 || video.BitRate != 4308000 || audio.BitRate != 192000)
            throw new Exception("The selected rendition's HLS bandwidth must override fallback video estimates.");
    }

    public static void IgnoresAbsentOrInvalidBandwidth()
    {
        foreach (var declaration in new[]
        {
            "#EXT-X-STREAM-INF:RESOLUTION=1920x1080",
            "#EXT-X-STREAM-INF:BANDWIDTH=0",
            "#EXT-X-STREAM-INF:BANDWIDTH=-1",
            "#EXT-X-STREAM-INF:BANDWIDTH=not-a-number",
            "#EXT-X-STREAM-INF:BANDWIDTH=99999999999999999999"
        })
        {
            if (HlsVariantBitratePolicy.ParseDeclaredBitrate(declaration).HasValue)
                throw new Exception("Invalid HLS bandwidth must remain unknown.");
        }

        var video = new MediaStream { Type = MediaStreamType.Video, BitRate = 8000000 };
        var source = new MediaSourceInfo { MediaStreams = [video], Bitrate = 8000000 };
        HlsVariantBitratePolicy.Apply(source, null);
        if (source.Bitrate != 8000000 || video.BitRate != 8000000)
            throw new Exception("Missing HLS metadata must preserve Jellyfin's probed values.");
    }
}
