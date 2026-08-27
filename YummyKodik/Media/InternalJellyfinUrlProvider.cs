using System.Net;
using MediaBrowser.Controller;

namespace YummyKodik.Media;

public interface IInternalJellyfinUrlProvider
{
    string GetBaseUrl();
}

/// <summary>
/// Provides the Jellyfin API origin reachable from the Jellyfin process and its encoder.
/// </summary>
internal sealed class InternalJellyfinUrlProvider : IInternalJellyfinUrlProvider
{
    private readonly IServerApplicationHost _applicationHost;

    public InternalJellyfinUrlProvider(IServerApplicationHost applicationHost)
    {
        _applicationHost = applicationHost;
    }

    public string GetBaseUrl()
    {
        var url = _applicationHost.GetApiUrlForLocalAccess(IPAddress.Loopback, allowHttps: false);
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new InvalidOperationException("Jellyfin did not provide a local API URL.");
        }

        return url.Trim().TrimEnd('/');
    }
}
