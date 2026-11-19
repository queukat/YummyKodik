using System.Globalization;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace YummyKodik.Media;

/// <summary>Preserves client stream-copy decisions and a valid frame rate when re-encoding is required.</summary>
internal sealed class YummyKodikHlsVideoEncodingFilter(IMediaSourceManager mediaSourceManager) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller) ||
            controller != "DynamicHls" ||
            !context.ActionDescriptor.Parameters.Any(parameter => parameter.Name == "allowVideoStreamCopy") ||
            !context.ActionArguments.TryGetValue("liveStreamId", out var value) ||
            value is not string liveStreamId || string.IsNullOrWhiteSpace(liveStreamId) ||
            mediaSourceManager.GetLiveStreamInfo(liveStreamId) is not YummyKodikMediaSourceProvider.NativePlaybackStream stream)
        {
            return;
        }

        // The host and client decide whether the video can be copied. Never
        // disable stream copy for every HLS request: that forces a needless
        // decode/filter/encode cycle even for compatible H.264 video.
        if (!IsDisabled(context, "allowVideoStreamCopy") &&
            !IsDisabled(context, "enableAutoStreamCopy"))
        {
            return;
        }

        // If re-encoding is explicitly requested, FFmpeg needs an actual
        // frame rate for TS sources whose 90 kHz clock is misidentified as FPS.
        // Respect caller-specified rates and maximums.
        if (context.ActionArguments.TryGetValue("framerate", out var configuredRate) && configuredRate != null ||
            context.HttpContext.Request.Query.ContainsKey("framerate"))
        {
            return;
        }

        var video = stream.MediaSource.MediaStreams.FirstOrDefault(item => item.Type == MediaStreamType.Video);
        var rate = video?.AverageFrameRate ?? video?.RealFrameRate;
        if (!IsValidRate(rate))
        {
            rate = video?.RealFrameRate;
        }
        if (!IsValidRate(rate)) return;

        var max = context.ActionArguments.TryGetValue("maxFramerate", out var maxArgument)
            ? maxArgument as float?
            : null;
        if (max == null &&
            context.HttpContext.Request.Query.TryGetValue("maxFramerate", out var maxValue) &&
            float.TryParse(maxValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedMax))
        {
            max = parsedMax;
        }
        if (IsValidRate(max)) rate = Math.Min(rate.Value, max!.Value);

        context.ActionArguments["framerate"] = rate.Value;
        var query = QueryHelpers.ParseQuery(context.HttpContext.Request.QueryString.Value);
        query["framerate"] = rate.Value.ToString("0.###", CultureInfo.InvariantCulture);
        context.HttpContext.Request.QueryString = QueryString.Create(query);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }

    private static bool IsDisabled(ActionExecutingContext context, string key)
    {
        if (context.ActionArguments.TryGetValue(key, out var argument) && argument is bool configured)
        {
            return !configured;
        }
        return context.HttpContext.Request.Query.TryGetValue(key, out var raw) &&
               bool.TryParse(raw.ToString(), out var parsed) && !parsed;
    }

    private static bool IsValidRate(float? rate)
        => rate.HasValue && float.IsFinite(rate.Value) && rate.Value > 0 && rate.Value <= 240;
}
