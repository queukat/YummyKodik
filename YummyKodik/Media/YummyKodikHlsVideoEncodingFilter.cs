using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.WebUtilities;

namespace YummyKodik.Media;

/// <summary>Keeps the host VOD playlist aligned with its encoded video segments.</summary>
internal sealed class YummyKodikHlsVideoEncodingFilter(IMediaSourceManager mediaSourceManager) : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ActionDescriptor.RouteValues.TryGetValue("controller", out var controller) ||
            controller != "DynamicHls" ||
            !context.ActionDescriptor.Parameters.Any(parameter => parameter.Name == "allowVideoStreamCopy") ||
            !context.ActionArguments.TryGetValue("liveStreamId", out var value) ||
            value is not string liveStreamId || string.IsNullOrWhiteSpace(liveStreamId) ||
            mediaSourceManager.GetLiveStreamInfo(liveStreamId) is not YummyKodikMediaSourceProvider.NativePlaybackStream)
        {
            return;
        }

        // The host predicts six-second VOD segments. Copying arbitrary provider
        // keyframes after a seek can produce many shorter segments and reach the
        // predicted ENDLIST minutes before the actual video ends. Let the host
        // encoder align keyframes, retaining its codec, hardware and user policy.
        context.ActionArguments["allowVideoStreamCopy"] = false;
        // Master playlists forward the raw query to their children. Keep every
        // subsequent playlist/segment/seek on the same encoding policy and key.
        var query = QueryHelpers.ParseQuery(context.HttpContext.Request.QueryString.Value);
        query["AllowVideoStreamCopy"] = "false";
        context.HttpContext.Request.QueryString = QueryString.Create(query);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
