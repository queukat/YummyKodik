using MediaBrowser.Model.Entities;
using YummyKodik.Media;

internal static class HlsVideoFrameRatePolicyTests
{
    public static void RejectsMpegTsTimeBaseWithoutFabricatingFps()
    {
        var video = new MediaStream { Type = MediaStreamType.Video, RealFrameRate = 90000f };
        HlsVideoFrameRatePolicy.Normalize([video], null);
        if (video.RealFrameRate.HasValue || video.AverageFrameRate.HasValue)
            throw new Exception("The MPEG-TS 90 kHz clock must not be treated as 90,000 FPS.");
    }

    public static void PrefersRealAverageFromProbe()
    {
        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            RealFrameRate = 90000f,
            AverageFrameRate = 29.97f
        };
        HlsVideoFrameRatePolicy.Normalize([video], 24f);
        if (video.RealFrameRate != 29.97f || video.AverageFrameRate != 29.97f)
            throw new Exception("Prefer a meaningful probed average over an HLS hint.");
    }

    public static void UsesDeclaredRateWhenProbeIsUnreliable()
    {
        var video = new MediaStream
        {
            Type = MediaStreamType.Video,
            RealFrameRate = 90000f,
            AverageFrameRate = null
        };
        HlsVideoFrameRatePolicy.Normalize([video], 23.976f);
        if (video.RealFrameRate != 23.976f || video.AverageFrameRate != 23.976f)
            throw new Exception("Use the actual HLS variant FPS instead of an implausible probe.");
    }

    public static void PreservesValidRatesAndRejectsInvalidHints()
    {
        var video = new MediaStream { Type = MediaStreamType.Video, RealFrameRate = 60f };
        var audio = new MediaStream { Type = MediaStreamType.Audio, RealFrameRate = 90000f };
        HlsVideoFrameRatePolicy.Normalize([video, audio], 30f);
        if (video.RealFrameRate != 60f || audio.RealFrameRate != 90000f)
            throw new Exception("Do not overwrite valid video rates or alter audio streams.");

        foreach (var hint in new[]
        {
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,FRAME-RATE=90000",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,FRAME-RATE=0",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000,FRAME-RATE=NaN",
            "#EXT-X-STREAM-INF:BANDWIDTH=1000000"
        })
        {
            if (HlsVideoFrameRatePolicy.ParseDeclaredFrameRate(hint).HasValue)
                throw new Exception("Malformed or implausible HLS FPS hints must not be accepted.");
        }
        if (HlsVideoFrameRatePolicy.ParseDeclaredFrameRate(
                "#EXT-X-STREAM-INF:BANDWIDTH=1000000,FRAME-RATE=59.940") != 59.94f)
            throw new Exception("Valid fractional HLS frame rates must be retained.");
    }
}
