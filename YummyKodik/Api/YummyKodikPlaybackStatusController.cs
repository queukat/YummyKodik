using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using YummyKodik.Media;

namespace YummyKodik.Api;

/// <summary>Identifies an installed plugin's managed playback items without resolving remote streams.</summary>
[ApiController]
[Authorize]
public sealed class YummyKodikPlaybackStatusController(
    ILibraryManager libraryManager,
    YummyKodikMediaSourceProvider mediaSourceProvider) : ControllerBase
{
    [HttpGet("YummyKodik/playback-status/{itemId:guid}")]
    public IActionResult Get(Guid itemId)
    {
        Response.Headers.CacheControl = "private, no-store";
        var item = libraryManager.GetItemById(itemId);
        var managed = item is not null && mediaSourceProvider.TryGetNativeGateway(item, out _);
        return Ok(new { managed });
    }
}
