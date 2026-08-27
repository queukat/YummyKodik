using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Kodik;

public sealed class KodikPlaybackService
{
    private const int MaxCachedResourceBytes = 16 * 1024 * 1024;
    private const long MaxTotalCachedBytes = 192L * 1024 * 1024;
    private const int MaxProxyDownloadAttempts = 3;
    private const string ManifestResourceId = "manifest";

    private static readonly string[] ProxyResourceFileSuffixes =
    [
        ".m3u8",
        ".m4s",
        ".mp4",
        ".ts",
        ".aac",
        ".vtt",
        ".webvtt",
        ".key"
    ];

    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);
    private static readonly TimeSpan ResourceCacheTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<string, KodikPlaybackSession> SessionCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, CachedKodikProxyResource> ResourceCache = new(StringComparer.Ordinal);
    private static readonly object ResourceCacheLock = new();
    private static long _resourceCacheBytes;

    private static readonly Regex ManifestUriAttributeRegex = new(
        "URI=(?<quote>[\"'])(?<uri>[^\"']+)\\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
        RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;

    public KodikPlaybackService(HttpClient httpClient, ILogger? logger = null)
        : this(httpClient, logger, Task.Delay)
    {
    }

    internal KodikPlaybackService(
        HttpClient httpClient,
        ILogger? logger,
        Func<TimeSpan, CancellationToken, Task> delayAsync)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
        _delayAsync = delayAsync ?? throw new ArgumentNullException(nameof(delayAsync));
    }

    public static KodikPlaybackSession CreateSession(KodikLinkInfo link, int preferredQuality)
    {
        ArgumentNullException.ThrowIfNull(link);

        CleanupExpiredSessions();

        var manifestUrl = KodikClient.BuildHlsUrl(link, preferredQuality);
        var session = new KodikPlaybackSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ManifestUrl = manifestUrl,
            ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl)
        };

        session.ProxyResources[ManifestResourceId] = manifestUrl;
        SessionCache[session.SessionId] = session;
        return session;
    }

    public static bool TryGetSession(string sessionId, out KodikPlaybackSession session)
    {
        CleanupExpiredSessions();

        if (SessionCache.TryGetValue((sessionId ?? string.Empty).Trim(), out var cachedSession) &&
            cachedSession.ExpiresAtUtc > DateTime.UtcNow)
        {
            cachedSession.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);
            session = cachedSession;
            return true;
        }

        session = null!;
        return false;
    }

    public static bool TryResolveProxyResourceUrl(KodikPlaybackSession session, string resourceId, out string resourceUrl)
    {
        ArgumentNullException.ThrowIfNull(session);

        resourceUrl = string.Empty;
        var resourceKey = (resourceId ?? string.Empty).Trim();
        if (resourceKey.Length == 0)
        {
            return false;
        }

        if (!session.ProxyResources.TryGetValue(resourceKey, out var cachedUrl) || string.IsNullOrWhiteSpace(cachedUrl))
        {
            return false;
        }

        session.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);
        resourceUrl = cachedUrl;
        return true;
    }

    public static string BuildManifestProxyUrl(KodikPlaybackSession session, string proxyBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(session);
        return BuildProxyResourceUrl(session, proxyBaseUrl, ManifestResourceId, session.ManifestUrl);
    }

    public async Task<KodikProxyResource> DownloadProxyResourceAsync(
        KodikPlaybackSession session,
        string resourceId,
        string resourceUrl,
        string proxyBaseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var upstreamUrl = (resourceUrl ?? string.Empty).Trim();
        if (upstreamUrl.Length == 0)
        {
            throw new InvalidOperationException("Kodik proxy resource url is empty.");
        }

        var payload = await DownloadProxyResourcePayloadAsync(upstreamUrl, cancellationToken).ConfigureAwait(false);
        if (!payload.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Kodik proxy resource request failed. status={(int)payload.StatusCode} url={upstreamUrl} body={TrimForLog(TryDecodeBody(payload.Body))}");
        }

        session.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);

        if (LooksLikeManifest(upstreamUrl, payload.MediaType, payload.Body))
        {
            var manifestText = TryDecodeBody(payload.Body);
            var rewritten = RewriteManifestUrls(session, upstreamUrl, manifestText, proxyBaseUrl);
            return new KodikProxyResource
            {
                Content = Encoding.UTF8.GetBytes(rewritten),
                ContentType = ResolveManifestContentType(payload.MediaType)
            };
        }

        return new KodikProxyResource
        {
            Content = payload.Body,
            ContentType = ResolveBinaryContentType(upstreamUrl, payload.MediaType)
        };
    }

    private async Task<CachedKodikProxyResource> DownloadProxyResourcePayloadAsync(
        string upstreamUrl,
        CancellationToken cancellationToken)
    {
        if (TryGetCachedResource(upstreamUrl, out var cached))
        {
            _logger?.LogDebug("Kodik proxy cache hit. url={Url}", Short(upstreamUrl, 200));
            return cached;
        }

        for (var attempt = 1; attempt <= MaxProxyDownloadAttempts; attempt++)
        {
            TimeSpan retryDelay;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, upstreamUrl);
                request.Headers.Accept.ParseAdd(ResolveAcceptHeader(upstreamUrl));

                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;

                var downloaded = new CachedKodikProxyResource
                {
                    StatusCode = response.StatusCode,
                    MediaType = mediaType,
                    Body = body,
                    ExpiresAtUtc = DateTime.UtcNow.Add(ResourceCacheTtl),
                    LastAccessUtc = DateTime.UtcNow
                };

                if (downloaded.IsSuccessStatusCode && CanCache(downloaded))
                {
                    StoreCachedResource(upstreamUrl, downloaded);
                }

                if (!IsTransientProxyFailureStatus(downloaded.StatusCode) ||
                    attempt == MaxProxyDownloadAttempts)
                {
                    return downloaded;
                }

                retryDelay = ResolveProxyStatusRetryDelay(attempt);

                _logger?.LogDebug(
                    "Kodik proxy received transient status={StatusCode}; retrying attempt={Attempt}/{MaxAttempts} after {DelayMs}ms. url={Url}",
                    (int)downloaded.StatusCode,
                    attempt + 1,
                    MaxProxyDownloadAttempts,
                    retryDelay.TotalMilliseconds,
                    Short(upstreamUrl, 200));
            }
            catch (HttpRequestException ex) when (attempt < MaxProxyDownloadAttempts)
            {
                retryDelay = ResolveProxyTransportRetryDelay(attempt);
                _logger?.LogDebug(
                    ex,
                    "Kodik proxy transport failed; retrying attempt={Attempt}/{MaxAttempts} after {DelayMs}ms. url={Url}",
                    attempt + 1,
                    MaxProxyDownloadAttempts,
                    retryDelay.TotalMilliseconds,
                    Short(upstreamUrl, 200));
            }
            catch (TaskCanceledException ex) when (
                !cancellationToken.IsCancellationRequested &&
                attempt < MaxProxyDownloadAttempts)
            {
                retryDelay = ResolveProxyTransportRetryDelay(attempt);
                _logger?.LogDebug(
                    ex,
                    "Kodik proxy request timed out; retrying attempt={Attempt}/{MaxAttempts} after {DelayMs}ms. url={Url}",
                    attempt + 1,
                    MaxProxyDownloadAttempts,
                    retryDelay.TotalMilliseconds,
                    Short(upstreamUrl, 200));
            }

            await _delayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Kodik proxy retry loop finished without a response.");
    }

    private static TimeSpan ResolveProxyStatusRetryDelay(int completedAttempt)
    {
        return completedAttempt switch
        {
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(6),
            _ => TimeSpan.Zero
        };
    }

    private static TimeSpan ResolveProxyTransportRetryDelay(int completedAttempt)
    {
        return TimeSpan.FromMilliseconds(150 * completedAttempt);
    }

    private static bool IsTransientProxyFailureStatus(HttpStatusCode statusCode)
    {
        // Kodik CDN manifests can become visible just before their HLS segments finish propagating.
        return statusCode == HttpStatusCode.NotFound ||
               statusCode == HttpStatusCode.RequestTimeout ||
               (int)statusCode >= 500;
    }

    private static bool TryGetCachedResource(string upstreamUrl, out CachedKodikProxyResource cached)
    {
        var key = NormalizeCacheKey(upstreamUrl);
        var now = DateTime.UtcNow;

        if (ResourceCache.TryGetValue(key, out cached!) && cached.ExpiresAtUtc > now)
        {
            cached.LastAccessUtc = now;
            return true;
        }

        if (cached != null)
        {
            RemoveCachedResource(key, cached);
        }

        cached = null!;
        return false;
    }

    private static void StoreCachedResource(string upstreamUrl, CachedKodikProxyResource payload)
    {
        var key = NormalizeCacheKey(upstreamUrl);
        var now = DateTime.UtcNow;

        lock (ResourceCacheLock)
        {
            CleanupExpiredResources(now);

            if (ResourceCache.TryGetValue(key, out var existing))
            {
                _resourceCacheBytes -= existing.Body.LongLength;
            }

            ResourceCache[key] = payload;
            _resourceCacheBytes += payload.Body.LongLength;
            TrimResourceCache();
        }
    }

    private static void RemoveCachedResource(string key, CachedKodikProxyResource cached)
    {
        lock (ResourceCacheLock)
        {
            if (ResourceCache.TryRemove(key, out var removed))
            {
                _resourceCacheBytes -= removed.Body.LongLength;
            }
        }
    }

    private static void CleanupExpiredResources(DateTime now)
    {
        foreach (var pair in ResourceCache)
        {
            if (pair.Value.ExpiresAtUtc > now)
            {
                continue;
            }

            if (ResourceCache.TryRemove(pair.Key, out var removed))
            {
                _resourceCacheBytes -= removed.Body.LongLength;
            }
        }
    }

    private static void TrimResourceCache()
    {
        if (_resourceCacheBytes <= MaxTotalCachedBytes)
        {
            return;
        }

        foreach (var pair in ResourceCache.OrderBy(x => x.Value.LastAccessUtc))
        {
            if (_resourceCacheBytes <= MaxTotalCachedBytes)
            {
                break;
            }

            if (ResourceCache.TryRemove(pair.Key, out var removed))
            {
                _resourceCacheBytes -= removed.Body.LongLength;
            }
        }
    }

    private static bool CanCache(CachedKodikProxyResource payload)
    {
        return payload.Body.Length > 0 &&
               payload.Body.Length <= MaxCachedResourceBytes;
    }

    private static string RewriteManifestUrls(
        KodikPlaybackSession session,
        string manifestUrl,
        string manifestText,
        string proxyBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(manifestText) ||
            !Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri) ||
            string.IsNullOrWhiteSpace(proxyBaseUrl))
        {
            return manifestText;
        }

        var lines = manifestText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('#'))
            {
                lines[i] = RewriteManifestUriAttributes(session, proxyBaseUrl, baseUri, line);
                continue;
            }

            if (Uri.TryCreate(baseUri, trimmed, out var absolute))
            {
                lines[i] = RegisterProxyResource(session, proxyBaseUrl, absolute.ToString());
            }
        }

        return string.Join('\n', lines);
    }

    private static string RewriteManifestUriAttributes(
        KodikPlaybackSession session,
        string proxyBaseUrl,
        Uri baseUri,
        string line)
    {
        return ManifestUriAttributeRegex.Replace(
            line,
            match =>
            {
                var quote = match.Groups["quote"].Value;
                var rawUri = match.Groups["uri"].Value;
                if (!Uri.TryCreate(baseUri, rawUri, out var absolute))
                {
                    return match.Value;
                }

                var proxyUrl = RegisterProxyResource(session, proxyBaseUrl, absolute.ToString());
                return "URI=" + quote + proxyUrl + quote;
            });
    }

    private static string RegisterProxyResource(
        KodikPlaybackSession session,
        string proxyBaseUrl,
        string resourceUrl)
    {
        var resourceId = BuildResourceId(resourceUrl);
        session.ProxyResources[resourceId] = resourceUrl;
        return BuildProxyResourceUrl(session, proxyBaseUrl, resourceId, resourceUrl);
    }

    private static string BuildProxyResourceUrl(
        KodikPlaybackSession session,
        string proxyBaseUrl,
        string resourceId,
        string resourceUrl)
    {
        var normalizedBaseUrl = (proxyBaseUrl ?? string.Empty).TrimEnd('/');
        var suffix = ResolveProxyResourceFileSuffix(resourceUrl);
        var resourceName = Uri.EscapeDataString(resourceId) + suffix;

        return $"{normalizedBaseUrl}/{resourceName}?sessionId={Uri.EscapeDataString(session.SessionId)}&resource={Uri.EscapeDataString(resourceId)}";
    }

    private static string BuildResourceId(string resourceUrl)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes((resourceUrl ?? string.Empty).Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ResolveProxyResourceFileSuffix(string resourceUrl)
    {
        if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out var uri))
        {
            return ".bin";
        }

        var path = uri.AbsolutePath;
        foreach (var suffix in ProxyResourceFileSuffixes)
        {
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return suffix;
            }
        }

        var dot = path.LastIndexOf('.');
        if (dot >= 0 && dot + 1 < path.Length)
        {
            var extension = path[dot..];
            if (extension.Length <= 12)
            {
                return extension;
            }
        }

        return ".bin";
    }

    private static bool LooksLikeManifest(string resourceUrl, string mediaType, byte[] body)
    {
        var path = GetResourcePath(resourceUrl);
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
            mediaType.Contains("m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = TryDecodeBody(body);
        return text.TrimStart().StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveManifestContentType(string mediaType)
    {
        return string.IsNullOrWhiteSpace(mediaType) ? "application/vnd.apple.mpegurl" : mediaType;
    }

    private static string ResolveBinaryContentType(string resourceUrl, string mediaType)
    {
        if (!string.IsNullOrWhiteSpace(mediaType))
        {
            return mediaType;
        }

        var path = GetResourcePath(resourceUrl);
        if (path.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp2t";
        }

        if (path.EndsWith(".m4s", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp4";
        }

        if (path.EndsWith(".aac", StringComparison.OrdinalIgnoreCase))
        {
            return "audio/aac";
        }

        if (path.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".webvtt", StringComparison.OrdinalIgnoreCase))
        {
            return "text/vtt";
        }

        return "application/octet-stream";
    }

    private static string ResolveAcceptHeader(string resourceUrl)
    {
        return GetResourcePath(resourceUrl).EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.apple.mpegurl, application/x-mpegURL, */*"
            : "*/*";
    }

    private static string GetResourcePath(string resourceUrl)
    {
        return Uri.TryCreate(resourceUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : resourceUrl;
    }

    private static string TryDecodeBody(byte[] body)
    {
        if (body == null || body.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(body);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TrimForLog(string value)
    {
        return Short((value ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal), 450);
    }

    private static string Short(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static string NormalizeCacheKey(string upstreamUrl)
    {
        return (upstreamUrl ?? string.Empty).Trim();
    }

    private static void CleanupExpiredSessions()
    {
        var now = DateTime.UtcNow;
        foreach (var pair in SessionCache)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                SessionCache.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class CachedKodikProxyResource
    {
        public HttpStatusCode StatusCode { get; init; }
        public string MediaType { get; init; } = string.Empty;
        public byte[] Body { get; init; } = Array.Empty<byte>();
        public DateTime ExpiresAtUtc { get; init; }
        public DateTime LastAccessUtc { get; set; }
        public bool IsSuccessStatusCode => (int)StatusCode >= 200 && (int)StatusCode <= 299;
    }
}

public sealed class KodikPlaybackSession
{
    public string SessionId { get; init; } = string.Empty;
    public string ManifestUrl { get; init; } = string.Empty;
    public ConcurrentDictionary<string, string> ProxyResources { get; } = new(StringComparer.Ordinal);
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class KodikProxyResource
{
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
}
