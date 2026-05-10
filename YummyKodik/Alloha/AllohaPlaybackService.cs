using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Alloha;

public sealed class AllohaPlaybackService
{
    private static readonly char[] VoiceLabelSeparators = ['/', '&', '+', ','];
    private const string WrapperUrl = "https://site.yummyani.me/";
    private const string AllohaOrigin = "https://alloha.yani.tv";
    private const string BrowserUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";
    private const string BrowserAcceptLanguage = "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7";
    private const string AcceptsControlsPrefix = "9badb2c5dd28e9cd0bed84e7391523d9d308a48b690428dae6049233218645d9";
    private const string ManifestGuardToken = "pXzvbyDGLYyB6VkwsWZDv3iMKZtsXNzpzRyxZUcsKHXxsSeaYakbo3hw9mBFRc5VQTpqAX6BW8aDEqyLaHYcXSQiV6KHYTVTK6MYRphNAy5sBjtrevqkDzKmLqNdfMZGEU9NELjmtKfZy3RNGzCd767sNh1mXEj4tCcvqndHtzmwAbZNkhm4ghDEasodotMBewypNQ56uotJAQGX11csfeRfBAPk8DcUWWkkqzxca8vbnEw12vUFbBzT6hz8ZB3F3dzUhUXoL2cr1WM1bXQArRCS1MUNMz3X5WDMMQoZKxj2AMTRqp7QQX4dDB9B7VzEZTmyFULhm1AcHHMkoMvSVvKYoBoAKLycYAgMHeD4ECJcGEAGpnkJhrV57zQ7";

    private static readonly TimeSpan ResolutionCacheTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);
    private static readonly ConcurrentDictionary<string, CachedResolution> ResolutionCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, AllohaPlaybackSession> SessionCache = new(StringComparer.Ordinal);
    private static readonly Regex ViewportiRegex = new(
        "<meta\\s+name=[\"']viewporti[\"']\\s+content=[\"'](?<value>[^\"']+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex FileListRegex = new(
        "const\\s+fileList\\s*=\\s*JSON\\.parse\\('(?<json>.*?)'\\);",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ManifestUriAttributeRegex = new(
        "URI=(?<quote>[\"'])(?<uri>[^\"']+)\\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly ILogger<AllohaPlaybackService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> _resolveStreamTokenAsync;

    public AllohaPlaybackService(ILogger<AllohaPlaybackService> logger)
        : this(logger, CreateDefaultHttpClient(), CreateDefaultStreamTokenResolver(logger))
    {
    }

    internal AllohaPlaybackService(ILogger<AllohaPlaybackService> logger, HttpClient httpClient)
        : this(logger, httpClient, static (_, _) => Task.FromResult<string?>(null))
    {
    }

    internal AllohaPlaybackService(
        ILogger<AllohaPlaybackService> logger,
        HttpClient httpClient,
        Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> resolveStreamTokenAsync)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _resolveStreamTokenAsync = resolveStreamTokenAsync ?? throw new ArgumentNullException(nameof(resolveStreamTokenAsync));
    }

    public async Task<AllohaPlaybackSession> CreateSessionAsync(
        YummyAllohaSource source,
        int preferredQuality,
        CancellationToken cancellationToken)
    {
        return await CreateSessionAsync(source, preferredQuality, preferredVoiceName: null, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AllohaPlaybackSession> CreateSessionAsync(
        YummyAllohaSource source,
        int preferredQuality,
        string? preferredVoiceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        CleanupExpiredEntries();

        var cacheKey = BuildResolutionCacheKey(source, preferredQuality, preferredVoiceName);
        if (!ResolutionCache.TryGetValue(cacheKey, out var cached) || cached.ExpiresAtUtc <= DateTime.UtcNow)
        {
            cached = new CachedResolution
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(ResolutionCacheTtl),
                Payload = await ResolveViaHttpAsync(source, preferredQuality, preferredVoiceName, cancellationToken).ConfigureAwait(false)
            };

            ResolutionCache[cacheKey] = cached;
        }

        var session = new AllohaPlaybackSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ManifestUrl = cached.Payload.ManifestUrl,
            ManifestText = cached.Payload.ManifestText,
            RefererUrl = cached.Payload.RefererUrl,
            RequiredHttpHeaders = BuildRequiredHttpHeaders(cached.Payload),
            StreamToken = cached.Payload.StreamToken,
            IframeUrl = cached.Payload.IframeUrl,
            WebSocketBaseUrl = cached.Payload.WebSocketBaseUrl,
            WebSocketSessionId = cached.Payload.WebSocketSessionId,
            AudioTrackId = cached.Payload.AudioTrackId,
            SelectedVoiceName = cached.Payload.SelectedVoiceName,
            AvailableVoiceNames = cached.Payload.AvailableVoiceNames,
            SelectedQuality = cached.Payload.SelectedQuality,
            Source = CloneSource(source),
            ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl)
        };

        SessionCache[session.SessionId] = session;
        return session;
    }

    public bool TryGetSession(string sessionId, out AllohaPlaybackSession session)
    {
        CleanupExpiredEntries();

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

    public bool TryResolveProxyResourceUrl(AllohaPlaybackSession session, string resourceId, out string resourceUrl)
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

    public async Task<AllohaProxyResource> DownloadProxyResourceAsync(
        AllohaPlaybackSession session,
        string resourceId,
        string resourceUrl,
        string proxyBaseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var upstreamUrl = (resourceUrl ?? string.Empty).Trim();
        if (upstreamUrl.Length == 0)
        {
            throw new InvalidOperationException("Alloha proxy resource url is empty.");
        }

        var resourceRefererUrl = ResolveProxyResourceReferer(session, resourceId, upstreamUrl);
        var requestRefererUrl = ResolveProxyRequestReferer(session, resourceRefererUrl, upstreamUrl);
        var (statusCode, mediaType, body) = await DownloadProxyResourcePayloadAsync(session, upstreamUrl, requestRefererUrl, cancellationToken)
            .ConfigureAwait(false);

        if (IsRecoverableProxyResourceFailure(statusCode, body) &&
            await TryRefreshProxyResourceAsync(session, resourceId, upstreamUrl, resourceRefererUrl, proxyBaseUrl, cancellationToken)
                .ConfigureAwait(false))
        {
            upstreamUrl = session.ProxyResources.TryGetValue(resourceId, out var reboundUrl) &&
                          !string.IsNullOrWhiteSpace(reboundUrl)
                ? reboundUrl
                : upstreamUrl;
            resourceRefererUrl = ResolveProxyResourceReferer(session, resourceId, upstreamUrl);
            requestRefererUrl = ResolveProxyRequestReferer(session, resourceRefererUrl, upstreamUrl);
            (statusCode, mediaType, body) = await DownloadProxyResourcePayloadAsync(session, upstreamUrl, requestRefererUrl, cancellationToken)
                .ConfigureAwait(false);
        }

        if ((int)statusCode < 200 || (int)statusCode >= 300)
        {
            throw new InvalidOperationException(
                $"Alloha proxy resource request failed. status={(int)statusCode} url={upstreamUrl} body={TrimForLog(TryDecodeBody(body))}");
        }

        session.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);

        if (LooksLikeManifest(upstreamUrl, mediaType, body))
        {
            var manifestText = TryDecodeBody(body);
            var rewritten = RewriteManifestUrls(session, upstreamUrl, manifestText, proxyBaseUrl, resourceId);
            return new AllohaProxyResource
            {
                Content = Encoding.UTF8.GetBytes(rewritten),
                ContentType = ResolveManifestContentType(mediaType)
            };
        }

        return new AllohaProxyResource
        {
            Content = body,
            ContentType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType
        };
    }

    private static bool IsRecoverableProxyResourceFailure(HttpStatusCode statusCode, byte[] body)
    {
        if (statusCode == HttpStatusCode.Forbidden)
        {
            return true;
        }

        if (statusCode != HttpStatusCode.InternalServerError)
        {
            return false;
        }

        var bodyText = TryDecodeBody(body);
        return bodyText.Contains("Could not process this request", StringComparison.OrdinalIgnoreCase);
    }

    public static string BuildManifestResponseBody(AllohaPlaybackSession session, string proxyBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(session);
        return RewriteManifestUrls(session, session.ManifestUrl, session.ManifestText, proxyBaseUrl, parentResourceId: null);
    }

    private async Task<ResolvedAllohaPayload> ResolveViaHttpAsync(
        YummyAllohaSource source,
        int preferredQuality,
        string? preferredVoiceName,
        CancellationToken cancellationToken)
    {
        var iframeUrl = BuildIframeRequestUrl(source.RefererUrl);
        var iframeOrigin = ResolveIframeOrigin(iframeUrl, source.RefererUrl);
        var iframeHtml = await DownloadIframeHtmlAsync(iframeUrl, cancellationToken).ConfigureAwait(false);
        var bootstrap = ParseBootstrapPayload(iframeHtml, source);
        var borth = BuildBorthHeader(bootstrap.Viewporti);
        var playlist = await RequestBnsiPayloadAsync(source, iframeOrigin, bootstrap.FileId, borth, cancellationToken).ConfigureAwait(false);
        var manifestCandidate = SelectManifestCandidate(playlist.ManifestCandidates, preferredQuality, preferredVoiceName);
        var manifestUrl = manifestCandidate.Url;
        var manifestText = await DownloadManifestAsync(manifestUrl, source, iframeOrigin, cancellationToken).ConfigureAwait(false);
        var selectedQuality = manifestCandidate.Quality ?? Math.Max(0, preferredQuality);
        var tokenRequest = new AllohaStreamTokenRequest
        {
            IframeUrl = iframeUrl,
            WebSocketBaseUrl = playlist.WebSocketBaseUrl,
            WebSocketSessionId = playlist.WebSocketSessionId,
            AudioTrackId = manifestCandidate.AudioTrackId,
            SelectedQuality = selectedQuality
        };
        var streamToken = await TryResolveDynamicAcceptsControlsAsync(tokenRequest, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Alloha manifest resolved: translation={TranslationId} season={Season} episode={Episode} fileId={FileId} quality={Quality} audioTrackId={AudioTrackId} selectedVoice={SelectedVoice} tokenReady={TokenReady} url={Url}",
            source.TranslationId,
            source.SeasonNumber,
            source.EpisodeNumber,
            bootstrap.FileId,
            selectedQuality,
            manifestCandidate.AudioTrackId,
            manifestCandidate.VoiceName,
            !string.IsNullOrWhiteSpace(streamToken),
            manifestUrl);

        return new ResolvedAllohaPayload
        {
            ManifestUrl = manifestUrl,
            ManifestText = manifestText,
            AcceptsControls = AcceptsControlsPrefix,
            Guard = ManifestGuardToken,
            OriginUrl = iframeOrigin,
            RefererUrl = source.RefererUrl,
            IframeUrl = iframeUrl,
            WebSocketBaseUrl = playlist.WebSocketBaseUrl,
            WebSocketSessionId = playlist.WebSocketSessionId,
            AudioTrackId = manifestCandidate.AudioTrackId,
            SelectedVoiceName = manifestCandidate.VoiceName,
            AvailableVoiceNames = playlist.AvailableVoiceNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            SelectedQuality = selectedQuality,
            StreamToken = streamToken ?? string.Empty
        };
    }

    private async Task<string> DownloadIframeHtmlAsync(string requestUrl, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, requestUrl);
        ConfigureCommonBrowserHeaders(message);
        message.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        message.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        message.Headers.Referrer = new Uri(WrapperUrl);

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Alloha iframe request failed. status={(int)response.StatusCode} url={requestUrl} body={TrimForLog(body)}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Alloha iframe returned an empty HTML document.");
        }

        return body;
    }

    private static AllohaBootstrapPayload ParseBootstrapPayload(string html, YummyAllohaSource source)
    {
        var viewportMatch = ViewportiRegex.Match(html ?? string.Empty);
        var viewporti = viewportMatch.Success
            ? (viewportMatch.Groups["value"].Value ?? string.Empty).Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(viewporti))
        {
            throw new InvalidOperationException("Alloha iframe HTML does not contain viewporti.");
        }

        var fileListMatch = FileListRegex.Match(html ?? string.Empty);
        var fileListJson = fileListMatch.Success
            ? (fileListMatch.Groups["json"].Value ?? string.Empty).Trim()
            : string.Empty;
        if (string.IsNullOrWhiteSpace(fileListJson))
        {
            throw new InvalidOperationException("Alloha iframe HTML does not contain fileList.");
        }

        long fileId;
        try
        {
            fileId = ResolveFileId(fileListJson, source);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Alloha fileList JSON is malformed.", ex);
        }

        if (fileId <= 0)
        {
            throw new InvalidOperationException(
                $"Alloha file id was not found for translation={source.TranslationId}, season={source.SeasonNumber}, episode={source.EpisodeNumber}.");
        }

        return new AllohaBootstrapPayload
        {
            Viewporti = viewporti,
            FileId = fileId
        };
    }

    private static long ResolveFileId(string fileListJson, YummyAllohaSource source)
    {
        using var doc = ParseJsonDocument(fileListJson);
        var root = doc.RootElement;

        if (TryResolveFileIdFromTranslations(root, source, out var id))
        {
            return id;
        }

        if (TryResolveFileIdFromActive(root, source, out id))
        {
            return id;
        }

        return 0;
    }

    private static JsonDocument ParseJsonDocument(string fileListJson)
    {
        try
        {
            return JsonDocument.Parse(fileListJson);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse(Regex.Unescape(fileListJson));
        }
    }

    private static bool TryResolveFileIdFromTranslations(
        JsonElement root,
        YummyAllohaSource source,
        out long fileId)
    {
        fileId = 0;

        if (!TryGetProperty(root, "all", out var allElement))
        {
            return false;
        }

        if (!TryGetProperty(allElement, "t" + source.TranslationId, out var translationElement))
        {
            return false;
        }

        if (!TryGetProperty(translationElement, "file", out var fileElement))
        {
            return false;
        }

        if (!TryGetProperty(fileElement, source.SeasonNumber.ToString(), out var seasonElement))
        {
            return false;
        }

        if (!TryGetProperty(seasonElement, source.EpisodeNumber.ToString(), out var episodeElement))
        {
            return false;
        }

        return TryGetInt64(episodeElement, "id", out fileId) && fileId > 0;
    }

    private static bool TryResolveFileIdFromActive(
        JsonElement root,
        YummyAllohaSource source,
        out long fileId)
    {
        fileId = 0;

        if (!TryGetProperty(root, "active", out var activeElement))
        {
            return false;
        }

        if (!TryGetInt32(activeElement, "id_translation", out var translationId) ||
            translationId != source.TranslationId)
        {
            return false;
        }

        if (!TryGetInt32(activeElement, "seasons", out var seasonNumber) ||
            seasonNumber != source.SeasonNumber)
        {
            return false;
        }

        if (!TryGetInt32(activeElement, "episode", out var episodeNumber) ||
            episodeNumber != source.EpisodeNumber)
        {
            return false;
        }

        return TryGetInt64(activeElement, "id", out fileId) && fileId > 0;
    }

    private async Task<AllohaBnsiPayload> RequestBnsiPayloadAsync(
        YummyAllohaSource source,
        string originUrl,
        long fileId,
        string borth,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{originUrl}/bnsi/movies/{fileId}");
        ConfigureCommonBrowserHeaders(message);
        message.Headers.TryAddWithoutValidation("Accept", "*/*");
        message.Headers.TryAddWithoutValidation("Origin", originUrl);
        message.Headers.TryAddWithoutValidation("Referer", source.RefererUrl);
        message.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        message.Headers.TryAddWithoutValidation("Borth", borth);
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        message.Content = new StringContent(
            $"token={Uri.EscapeDataString(source.RequestToken)}&av1=true&autoplay=0&audio=&subtitle=",
            Encoding.UTF8,
            "application/x-www-form-urlencoded");

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Alloha bnsi request failed. status={(int)response.StatusCode} fileId={fileId} body={TrimForLog(body)}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException($"Alloha bnsi request returned an empty response for fileId={fileId}.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ParseBnsiPayload(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Alloha bnsi response is invalid JSON for fileId={fileId}.", ex);
        }
    }

    private static AllohaBnsiPayload ParseBnsiPayload(JsonElement root)
    {
        var payload = new AllohaBnsiPayload
        {
            SkipTimeSeconds = TryGetInt32(root, "skipTime", out var skipTime) ? Math.Max(0, skipTime) : 0,
            RemoveTimeSeconds = TryGetInt32(root, "removeTime", out var removeTime) ? Math.Max(0, removeTime) : 0,
            WebSocketBaseUrl = TryGetString(root, "pnr", out var websocketBaseUrl) ? websocketBaseUrl : string.Empty,
            WebSocketSessionId = TryGetString(root, "pnk", out var websocketSessionId) ? websocketSessionId : string.Empty
        };

        if (TryGetProperty(root, "hlsSource", out var hlsSourceElement) &&
            hlsSourceElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var sourceElement in hlsSourceElement.EnumerateArray())
            {
                if (sourceElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var audioTrackId = ResolveAudioTrackId(sourceElement);
                var voiceName = ResolveVoiceName(sourceElement);
                if (!string.IsNullOrWhiteSpace(voiceName))
                {
                    payload.AvailableVoiceNames.Add(voiceName);
                }

                if (TryGetProperty(sourceElement, "quality", out var qualityElement) &&
                    qualityElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in qualityElement.EnumerateObject())
                    {
                        int? quality = int.TryParse(property.Name, out var qualityValue) ? qualityValue : null;
                        foreach (var url in SplitAlternativeUrls(property.Value.GetString()))
                        {
                            payload.ManifestCandidates.Add(new AllohaManifestCandidate
                            {
                                Quality = quality,
                                Url = url,
                                AudioTrackId = audioTrackId,
                                VoiceName = voiceName
                            });
                        }
                    }
                }

                foreach (var fallbackName in new[] { "manifest", "url", "src" })
                {
                    if (!TryGetProperty(sourceElement, fallbackName, out var fallbackElement))
                    {
                        continue;
                    }

                    foreach (var url in SplitAlternativeUrls(fallbackElement.GetString()))
                    {
                        payload.ManifestCandidates.Add(new AllohaManifestCandidate
                        {
                            Quality = null,
                            Url = url,
                            AudioTrackId = audioTrackId,
                            VoiceName = voiceName
                        });
                    }
                }
            }
        }

        return payload;
    }

    private async Task<string> DownloadManifestAsync(
        string manifestUrl,
        YummyAllohaSource source,
        string originUrl,
        CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
        ConfigureCommonBrowserHeaders(message);
        message.Headers.TryAddWithoutValidation("Accept", "application/vnd.apple.mpegurl, application/x-mpegURL, */*");
        message.Headers.TryAddWithoutValidation("Accepts-Controls", AcceptsControlsPrefix);
        message.Headers.TryAddWithoutValidation("Authorizations", "Bearer " + ManifestGuardToken);
        message.Headers.TryAddWithoutValidation("Origin", originUrl);
        message.Headers.TryAddWithoutValidation("Referer", source.RefererUrl);
        message.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        message.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        using var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        var manifestText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Alloha manifest request failed. status={(int)response.StatusCode} url={manifestUrl} body={TrimForLog(manifestText)}");
        }

        if (string.IsNullOrWhiteSpace(manifestText))
        {
            throw new InvalidOperationException("Alloha returned an empty manifest.");
        }

        return manifestText;
    }

    private async Task<HttpResponseMessage> CreateSessionRequestAsync(
        AllohaPlaybackSession session,
        string resourceUrl,
        string refererUrl,
        CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, resourceUrl);
        ConfigureCommonBrowserHeaders(message);
        message.Headers.TryAddWithoutValidation("Accept", ResolveProxyRequestAcceptHeader(resourceUrl));
        message.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        message.Headers.TryAddWithoutValidation("Pragma", "no-cache");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        message.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");

        var effectiveAcceptsControls = !string.IsNullOrWhiteSpace(session.StreamToken)
            ? session.StreamToken
            : GetRequiredHeaderValue(session, "Accepts-Controls");
        if (!string.IsNullOrWhiteSpace(effectiveAcceptsControls))
        {
            message.Headers.TryAddWithoutValidation("Accepts-Controls", effectiveAcceptsControls);
        }

        foreach (var pair in session.RequiredHttpHeaders)
        {
            if (string.Equals(pair.Key, "Accepts-Controls", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, "Accept", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, "Origin", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pair.Key, "Referer", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }
        }

        if (!string.IsNullOrWhiteSpace(refererUrl))
        {
            message.Headers.TryAddWithoutValidation("Referer", refererUrl);
        }

        var originUrl = GetRequiredHeaderValue(session, "Origin");
        if (string.IsNullOrWhiteSpace(originUrl))
        {
            originUrl = TryBuildOriginFromReferer(refererUrl);
        }

        if (!string.IsNullOrWhiteSpace(originUrl))
        {
            message.Headers.TryAddWithoutValidation("Origin", originUrl);
        }

        var response = await _httpClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
        return response;
    }

    private async Task<(HttpStatusCode StatusCode, string MediaType, byte[] Body)> DownloadProxyResourcePayloadAsync(
        AllohaPlaybackSession session,
        string upstreamUrl,
        string refererUrl,
        CancellationToken cancellationToken)
    {
        using var response = await CreateSessionRequestAsync(session, upstreamUrl, refererUrl, cancellationToken).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, mediaType, body);
    }

    private async Task<string?> TryResolveDynamicAcceptsControlsAsync(
        AllohaStreamTokenRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return null;
        }

        try
        {
            return await _resolveStreamTokenAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Alloha stream token resolution failed. iframe={IframeUrl} quality={Quality} audioTrackId={AudioTrackId}",
                request.IframeUrl,
                request.SelectedQuality,
                request.AudioTrackId);
            return null;
        }
    }

    private async Task<bool> TryRefreshProxyResourceAsync(
        AllohaPlaybackSession session,
        string resourceId,
        string upstreamUrl,
        string refererUrl,
        string proxyBaseUrl,
        CancellationToken cancellationToken)
    {
        if (await TryRefreshSessionAsync(session, proxyBaseUrl, cancellationToken).ConfigureAwait(false) &&
            TryRebindProxyResourceAfterRefresh(session, resourceId, upstreamUrl, refererUrl, out var refreshedUrl, out var refreshedReferer))
        {
            session.ProxyResources[resourceId] = refreshedUrl;
            session.ProxyResourceReferers[resourceId] = refreshedReferer;

            _logger.LogInformation(
                "Alloha proxy resource rebound after session refresh. resource={ResourceId} url={Url}",
                resourceId,
                refreshedUrl);

            return true;
        }

        return await TryRefreshStreamTokenAsync(session, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryRefreshSessionAsync(
        AllohaPlaybackSession session,
        string proxyBaseUrl,
        CancellationToken cancellationToken)
    {
        var source = session.Source;
        if (source == null ||
            string.IsNullOrWhiteSpace(source.MovieToken) ||
            string.IsNullOrWhiteSpace(source.RequestToken) ||
            source.TranslationId <= 0 ||
            source.SeasonNumber <= 0 ||
            source.EpisodeNumber <= 0 ||
            string.IsNullOrWhiteSpace(source.RefererUrl))
        {
            return false;
        }

        try
        {
            var refreshed = await ResolveViaHttpAsync(
                    CloneSource(source),
                    session.SelectedQuality,
                    session.SelectedVoiceName,
                    cancellationToken)
                .ConfigureAwait(false);

            ApplyResolvedPayload(session, source, refreshed);
            BuildManifestResponseBody(session, proxyBaseUrl);

            _logger.LogInformation(
                "Alloha session refreshed after upstream resource failure. quality={Quality} selectedVoice={SelectedVoice} manifest={ManifestUrl}",
                session.SelectedQuality,
                session.SelectedVoiceName,
                session.ManifestUrl);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Alloha session refresh after upstream resource failure failed. quality={Quality} selectedVoice={SelectedVoice}",
                session.SelectedQuality,
                session.SelectedVoiceName);
            return false;
        }
    }

    private async Task<bool> TryRefreshStreamTokenAsync(
        AllohaPlaybackSession session,
        CancellationToken cancellationToken)
    {
        var request = new AllohaStreamTokenRequest
        {
            IframeUrl = session.IframeUrl,
            WebSocketBaseUrl = session.WebSocketBaseUrl,
            WebSocketSessionId = session.WebSocketSessionId,
            AudioTrackId = session.AudioTrackId,
            SelectedQuality = session.SelectedQuality
        };

        if (string.IsNullOrWhiteSpace(request.IframeUrl) &&
            (string.IsNullOrWhiteSpace(request.WebSocketBaseUrl) ||
             string.IsNullOrWhiteSpace(request.WebSocketSessionId) ||
             string.IsNullOrWhiteSpace(request.AudioTrackId)))
        {
            return false;
        }

        var refreshedToken = await TryResolveDynamicAcceptsControlsAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(refreshedToken))
        {
            return false;
        }

        session.StreamToken = refreshedToken;
        session.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);

        _logger.LogInformation(
            "Alloha stream token refreshed after upstream 403. quality={Quality} iframe={IframeUrl}",
            session.SelectedQuality,
            session.IframeUrl);

        return true;
    }

    private static void ApplyResolvedPayload(
        AllohaPlaybackSession session,
        YummyAllohaSource source,
        ResolvedAllohaPayload payload)
    {
        session.ManifestUrl = payload.ManifestUrl;
        session.ManifestText = payload.ManifestText;
        session.RefererUrl = payload.RefererUrl;
        session.RequiredHttpHeaders = BuildRequiredHttpHeaders(payload);
        session.StreamToken = payload.StreamToken ?? string.Empty;
        session.IframeUrl = payload.IframeUrl;
        session.WebSocketBaseUrl = payload.WebSocketBaseUrl;
        session.WebSocketSessionId = payload.WebSocketSessionId;
        session.AudioTrackId = payload.AudioTrackId;
        session.SelectedVoiceName = payload.SelectedVoiceName;
        session.AvailableVoiceNames = payload.AvailableVoiceNames;
        session.SelectedQuality = payload.SelectedQuality;
        session.Source = CloneSource(source);
        session.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);
    }

    private static string? GetRequiredHeaderValue(AllohaPlaybackSession session, string headerName)
    {
        if (session.RequiredHttpHeaders == null)
        {
            return null;
        }

        return session.RequiredHttpHeaders.TryGetValue(headerName, out var value)
            ? (value ?? string.Empty).Trim()
            : null;
    }

    private static AllohaManifestCandidate SelectManifestCandidate(
        IReadOnlyList<AllohaManifestCandidate> candidates,
        int preferredQuality,
        string? preferredVoiceName = null)
    {
        var available = candidates
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .ToList();

        if (available.Count == 0)
        {
            throw new InvalidOperationException("Alloha did not return any HLS sources.");
        }

        var matchingVoiceCandidates = FilterCandidatesByVoice(available, preferredVoiceName);
        if (matchingVoiceCandidates.Count > 0)
        {
            available = matchingVoiceCandidates;
        }

        if (preferredQuality <= 0)
        {
            return available
                .OrderByDescending(x => x.Quality ?? 0)
                .First();
        }

        return available
            .OrderBy(x => GetQualityDistanceScore(x.Quality, preferredQuality))
            .ThenByDescending(x => x.Quality ?? 0)
            .First();
    }

    private static string SelectManifestUrl(IReadOnlyList<AllohaManifestCandidate> candidates, int preferredQuality)
    {
        return SelectManifestCandidate(candidates, preferredQuality).Url;
    }

    private static List<AllohaManifestCandidate> FilterCandidatesByVoice(
        IReadOnlyList<AllohaManifestCandidate> candidates,
        string? preferredVoiceName)
    {
        var preferredVoiceKeys = GetVoiceMatchKeys(preferredVoiceName);
        if (preferredVoiceKeys.Count == 0)
        {
            return new List<AllohaManifestCandidate>();
        }

        return candidates
            .Where(x => GetVoiceMatchKeys(x.VoiceName).Any(preferredVoiceKeys.Contains))
            .ToList();
    }

    private static HashSet<string> GetVoiceMatchKeys(string? voiceName)
    {
        var normalizedVoiceName = YummyVideoCatalog.NormalizeVoiceName(voiceName);
        var keys = new HashSet<string>(StringComparer.Ordinal);

        AddVoiceMatchKey(keys, normalizedVoiceName);

        foreach (var part in normalizedVoiceName.Split(VoiceLabelSeparators, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            AddVoiceMatchKey(keys, part);
        }

        return keys;
    }

    private static void AddVoiceMatchKey(HashSet<string> keys, string? value)
    {
        var normalizedKey = TranslationNameKeyNormalizer.Normalize(value);
        if (!string.IsNullOrWhiteSpace(normalizedKey))
        {
            keys.Add(normalizedKey);
        }
    }

    private static int GetQualityDistanceScore(int? quality, int preferredQuality)
    {
        if (!quality.HasValue)
        {
            return int.MaxValue;
        }

        if (quality.Value == preferredQuality)
        {
            return 0;
        }

        if (quality.Value < preferredQuality)
        {
            return preferredQuality - quality.Value;
        }

        return 100000 + (quality.Value - preferredQuality);
    }

    private static IEnumerable<string> SplitAlternativeUrls(string? rawValue)
    {
        var normalized = (rawValue ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            yield break;
        }

        foreach (var part in normalized.Split(" or ", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(part))
            {
                yield return part.Trim();
            }
        }
    }

    private static string BuildIframeRequestUrl(string refererUrl)
    {
        var url = (refererUrl ?? string.Empty).Trim();
        if (url.Length == 0 || url.Contains("_r=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "_r=" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static string BuildBorthHeader(string viewporti)
    {
        return AcceptsControlsPrefix + "|" + BuildBorthSuffix(viewporti);
    }

    private static string BuildBorthSuffix(string viewporti)
    {
        return YcTransform(YcInverseTransform(YfInverseTransform(viewporti)));
    }

    // This mirrors Alloha's exact client-side Borth transform for viewporti.
    private static string YfInverseTransform(string value)
    {
        var length = value?.Length ?? 0;
        if (length <= 0)
        {
            return string.Empty;
        }

        var maxGroup = 0;
        while ((1 << maxGroup) < length)
        {
            maxGroup++;
        }

        static int GetGroup(int index)
        {
            if (index == 0)
            {
                return 0;
            }

            var group = 1;
            while (index > 1)
            {
                group++;
                index >>= 1;
            }

            return group;
        }

        var counts = new int[maxGroup + 1];
        for (var i = 0; i < length; i++)
        {
            counts[GetGroup(i)]++;
        }

        var slices = new string[maxGroup + 1];
        var offset = 0;
        for (var group = maxGroup; group >= 0; group--)
        {
            var count = counts[group];
            slices[group] = value!.Substring(offset, count);
            offset += count;
        }

        var positions = new int[maxGroup + 1];
        var output = new char[length];
        for (var i = 0; i < length; i++)
        {
            var group = GetGroup(i);
            output[i] = slices[group][positions[group]++];
        }

        return new string(output);
    }

    private static string YcInverseTransform(string value)
    {
        var length = value?.Length ?? 0;
        if (length <= 0)
        {
            return string.Empty;
        }

        var maxGroup = 0;
        while ((1 << maxGroup) < length)
        {
            maxGroup++;
        }

        int GetGroup(int index)
        {
            if (index == 0)
            {
                return maxGroup;
            }

            var group = 0;
            while ((index & 1) == 0)
            {
                group++;
                index >>= 1;
            }

            return group;
        }

        var counts = new int[maxGroup + 1];
        for (var i = 0; i < length; i++)
        {
            counts[GetGroup(i)]++;
        }

        var slices = new string[maxGroup + 1];
        var offset = 0;
        for (var group = 0; group <= maxGroup; group++)
        {
            var count = counts[group];
            slices[group] = value!.Substring(offset, count);
            offset += count;
        }

        var positions = new int[maxGroup + 1];
        var output = new char[length];
        for (var i = 0; i < length; i++)
        {
            var group = GetGroup(i);
            output[i] = slices[group][positions[group]++];
        }

        return new string(output);
    }

    private static string YcTransform(string value)
    {
        var length = value?.Length ?? 0;
        if (length <= 0)
        {
            return string.Empty;
        }

        static bool IsPrime(int value)
        {
            if (value < 2)
            {
                return false;
            }

            if (value % 2 == 0)
            {
                return value == 2;
            }

            for (var factor = 3; factor * factor <= value; factor += 2)
            {
                if (value % factor == 0)
                {
                    return false;
                }
            }

            return true;
        }

        var modulus = Math.Max(2, length + 1);
        while (!IsPrime(modulus))
        {
            modulus++;
        }

        var used = new bool[length];
        var order = new List<int>(length);
        var cursor = 0;
        while (order.Count < length)
        {
            cursor = (cursor + 2) % modulus;
            if (cursor < length && !used[cursor])
            {
                order.Add(cursor);
                used[cursor] = true;
            }
        }

        var output = new char[length];
        for (var i = 0; i < length; i++)
        {
            output[order[i]] = value![i];
        }

        return new string(output);
    }

    private static void ConfigureCommonBrowserHeaders(HttpRequestMessage message)
    {
        message.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        message.Headers.TryAddWithoutValidation("Accept-Language", BrowserAcceptLanguage);
    }

    private static string ResolveAudioTrackId(JsonElement sourceElement)
    {
        foreach (var propertyName in new[] { "audioId", "audio_id", "trackId", "track_id", "id" })
        {
            if (!TryGetString(sourceElement, propertyName, out var value) || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            return value;
        }

        return string.Empty;
    }

    private static string ResolveVoiceName(JsonElement sourceElement)
    {
        string? opaqueCandidate = null;

        foreach (var propertyName in new[] { "label", "voice", "name", "translation", "translator", "title", "audioName", "audioLabel" })
        {
            if (!TryGetString(sourceElement, propertyName, out var rawValue) || string.IsNullOrWhiteSpace(rawValue))
            {
                continue;
            }

            var value = NormalizeVoiceLabel(rawValue);
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (!LooksLikeOpaqueAllohaVoiceMarker(value))
            {
                return value;
            }

            opaqueCandidate ??= value;
        }

        return opaqueCandidate ?? string.Empty;
    }

    private static string NormalizeVoiceLabel(string label)
    {
        var value = label.Trim();
        var pipeIndex = value.LastIndexOf('|');
        if (pipeIndex >= 0 && pipeIndex < value.Length - 1)
        {
            value = value[(pipeIndex + 1)..].Trim();
        }

        if (value.StartsWith('('))
        {
            var closingParen = value.IndexOf(')');
            if (closingParen >= 0 && closingParen < value.Length - 1)
            {
                value = value[(closingParen + 1)..].Trim();
            }
        }

        return value;
    }

    private static bool LooksLikeOpaqueAllohaVoiceMarker(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length > 0 && normalized.All(char.IsDigit);
    }

    private static Func<AllohaStreamTokenRequest, CancellationToken, Task<string?>> CreateDefaultStreamTokenResolver(ILogger<AllohaPlaybackService> logger)
    {
        var websocketResolver = new AllohaWebSocketStreamTokenResolver(logger);
        var headlessResolver = new AllohaHeadlessStreamTokenResolver(logger);

        return async (request, cancellationToken) =>
        {
            if (request == null)
            {
                return null;
            }

            var token = await websocketResolver.ResolveStreamTokenAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }

            if (string.IsNullOrWhiteSpace(request.IframeUrl))
            {
                return null;
            }

            return await headlessResolver.ResolveStreamTokenAsync(
                    request.IframeUrl,
                    request.SelectedQuality,
                    cancellationToken)
                .ConfigureAwait(false);
        };
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetInt32(JsonElement element, string name, out int value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out var child))
        {
            return false;
        }

        if (child.ValueKind == JsonValueKind.Number)
        {
            return child.TryGetInt32(out value);
        }

        return int.TryParse(child.GetString(), out value);
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(element, name, out var child))
        {
            return false;
        }

        switch (child.ValueKind)
        {
            case JsonValueKind.String:
                value = (child.GetString() ?? string.Empty).Trim();
                return value.Length > 0;
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                value = child.GetRawText().Trim();
                return value.Length > 0;
            default:
                return false;
        }
    }

    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        if (!TryGetProperty(element, name, out var child))
        {
            return false;
        }

        if (child.ValueKind == JsonValueKind.Number)
        {
            return child.TryGetInt64(out value);
        }

        return long.TryParse(child.GetString(), out value);
    }

    private static bool LooksLikeManifest(string resourceUrl, string mediaType, byte[] body)
    {
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (resourceUrl.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var text = TryDecodeBody(body);
        return text.Contains("#EXTM3U", StringComparison.Ordinal);
    }

    private static bool LooksLikeManifestUrl(string resourceUrl)
    {
        return (resourceUrl ?? string.Empty).Trim().EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveManifestContentType(string mediaType)
    {
        return !string.IsNullOrWhiteSpace(mediaType) &&
               mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase)
            ? mediaType
            : "application/vnd.apple.mpegurl";
    }

    private static string RewriteManifestUrls(
        AllohaPlaybackSession session,
        string manifestUrl,
        string manifestText,
        string proxyBaseUrl,
        string? parentResourceId)
    {
        if (string.IsNullOrWhiteSpace(manifestText) ||
            !Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri))
        {
            return manifestText;
        }

        var lines = manifestText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var originalLine = lines[i];
            var line = originalLine.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                lines[i] = RewriteManifestDirectiveUris(session, baseUri, manifestUrl, originalLine, proxyBaseUrl, parentResourceId);
                continue;
            }

            if (Uri.TryCreate(baseUri, line, out var absolute))
            {
                lines[i] = BuildProxyResourceUrl(
                    session,
                    proxyBaseUrl,
                    absolute,
                    manifestUrl,
                    NormalizeProxyResourceReference(baseUri, line, absolute),
                    parentResourceId);
            }
        }

        return string.Join('\n', lines);
    }

    private static string RewriteManifestDirectiveUris(
        AllohaPlaybackSession session,
        Uri baseUri,
        string manifestUrl,
        string line,
        string proxyBaseUrl,
        string? parentResourceId)
    {
        if (line.IndexOf("URI=", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return line;
        }

        return ManifestUriAttributeRegex.Replace(
            line,
            match =>
            {
                var resourceValue = match.Groups["uri"].Value;
                if (!Uri.TryCreate(baseUri, resourceValue, out var absolute))
                {
                    return match.Value;
                }

                var quote = match.Groups["quote"].Value;
                var proxyUrl = BuildProxyResourceUrl(
                    session,
                    proxyBaseUrl,
                    absolute,
                    manifestUrl,
                    NormalizeProxyResourceReference(baseUri, resourceValue, absolute),
                    parentResourceId);
                return $"URI={quote}{proxyUrl}{quote}";
            });
    }

    private static string BuildProxyResourceUrl(
        AllohaPlaybackSession session,
        string proxyBaseUrl,
        Uri absoluteUrl,
        string refererUrl,
        string originalReference,
        string? parentResourceId)
    {
        var resourceId = RegisterProxyResource(session, absoluteUrl.ToString(), refererUrl, originalReference, parentResourceId);
        var suffix = GetProxyResourceFileSuffix(absoluteUrl);
        return proxyBaseUrl.TrimEnd('/') +
               "/" + resourceId + suffix +
               "?sessionId=" + Uri.EscapeDataString(session.SessionId) +
               "&resource=" + Uri.EscapeDataString(resourceId);
    }

    private static string RegisterProxyResource(
        AllohaPlaybackSession session,
        string resourceUrl,
        string refererUrl,
        string originalReference,
        string? parentResourceId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(resourceUrl));
        var resourceId = Convert.ToHexString(hash).ToLowerInvariant();
        session.ProxyResources[resourceId] = resourceUrl;
        if (!string.IsNullOrWhiteSpace(refererUrl))
        {
            session.ProxyResourceReferers[resourceId] = refererUrl;
        }

        if (!string.IsNullOrWhiteSpace(originalReference))
        {
            session.ProxyResourceOriginalReferences[resourceId] = originalReference;
        }

        var normalizedParentResourceId = (parentResourceId ?? string.Empty).Trim();
        if (normalizedParentResourceId.Length > 0)
        {
            session.ProxyResourceParentIds[resourceId] = normalizedParentResourceId;
        }

        return resourceId;
    }

    private static string NormalizeProxyResourceReference(Uri baseUri, string originalReference, Uri absoluteUrl)
    {
        var reference = (originalReference ?? string.Empty).Trim();
        if (reference.Length == 0)
        {
            return baseUri.MakeRelativeUri(absoluteUrl).ToString();
        }

        if (Uri.TryCreate(reference, UriKind.Absolute, out var absoluteReference) &&
            string.Equals(absoluteReference.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(absoluteReference.Authority, baseUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return baseUri.MakeRelativeUri(absoluteReference).ToString();
        }

        return reference;
    }

    private static bool TryRebindProxyResourceAfterRefresh(
        AllohaPlaybackSession session,
        string resourceId,
        string upstreamUrl,
        string refererUrl,
        out string refreshedUrl,
        out string refreshedReferer)
    {
        return TryResolveCurrentProxyResourceUrl(
            session,
            resourceId,
            upstreamUrl,
            refererUrl,
            new HashSet<string>(StringComparer.Ordinal),
            out refreshedUrl,
            out refreshedReferer);
    }

    private static bool TryResolveCurrentProxyResourceUrl(
        AllohaPlaybackSession session,
        string resourceId,
        string upstreamUrl,
        string refererUrl,
        ISet<string> visited,
        out string refreshedUrl,
        out string refreshedReferer)
    {
        refreshedUrl = string.Empty;
        refreshedReferer = string.Empty;

        var resourceKey = (resourceId ?? string.Empty).Trim();
        if (resourceKey.Length == 0 || !visited.Add(resourceKey))
        {
            return false;
        }

        var currentReferer = session.ManifestUrl;
        if (session.ProxyResourceParentIds.TryGetValue(resourceKey, out var parentResourceId) &&
            !string.IsNullOrWhiteSpace(parentResourceId))
        {
            if (!TryResolveCurrentProxyResourceUrl(
                    session,
                    parentResourceId,
                    session.ProxyResources.TryGetValue(parentResourceId, out var parentUrl) ? parentUrl : string.Empty,
                    session.ProxyResourceReferers.TryGetValue(parentResourceId, out var parentReferer) ? parentReferer : string.Empty,
                    visited,
                    out currentReferer,
                    out _))
            {
                return false;
            }
        }

        var resourceReference = ResolveProxyResourceReference(session, resourceKey, upstreamUrl, refererUrl);
        if (resourceReference.Length == 0 ||
            !Uri.TryCreate(currentReferer, UriKind.Absolute, out var parentUri) ||
            !Uri.TryCreate(parentUri, resourceReference, out var absoluteUri))
        {
            return false;
        }

        refreshedUrl = absoluteUri.ToString();
        refreshedReferer = currentReferer;
        return true;
    }

    private static string ResolveProxyResourceReference(
        AllohaPlaybackSession session,
        string resourceId,
        string upstreamUrl,
        string refererUrl)
    {
        if (session.ProxyResourceOriginalReferences.TryGetValue(resourceId, out var storedReference) &&
            !string.IsNullOrWhiteSpace(storedReference))
        {
            return storedReference;
        }

        if (Uri.TryCreate(refererUrl, UriKind.Absolute, out var refererUri) &&
            Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var upstreamUri))
        {
            return refererUri.MakeRelativeUri(upstreamUri).ToString();
        }

        return string.Empty;
    }

    private static string ResolveProxyResourceReferer(AllohaPlaybackSession session, string resourceId, string upstreamUrl)
    {
        var resourceKey = (resourceId ?? string.Empty).Trim();
        if (resourceKey.Length > 0 &&
            session.ProxyResourceReferers.TryGetValue(resourceKey, out var cachedReferer) &&
            !string.IsNullOrWhiteSpace(cachedReferer))
        {
            return cachedReferer;
        }

        return GetRequiredHeaderValue(session, "Referer")
               ?? session.RefererUrl
               ?? upstreamUrl;
    }

    private static string ResolveProxyRequestReferer(
        AllohaPlaybackSession session,
        string resourceRefererUrl,
        string upstreamUrl)
    {
        return GetRequiredHeaderValue(session, "Referer")
               ?? session.RefererUrl
               ?? resourceRefererUrl
               ?? upstreamUrl;
    }

    private static string ResolveProxyRequestAcceptHeader(string resourceUrl)
    {
        return LooksLikeManifestUrl(resourceUrl)
            ? "application/vnd.apple.mpegurl, application/x-mpegURL, */*"
            : "*/*";
    }

    private static string? TryBuildOriginFromReferer(string refererUrl)
    {
        return Uri.TryCreate((refererUrl ?? string.Empty).Trim(), UriKind.Absolute, out var refererUri)
            ? refererUri.GetLeftPart(UriPartial.Authority)
            : null;
    }

    private static string ResolveIframeOrigin(string iframeUrl, string refererUrl)
    {
        return TryBuildOriginFromReferer(iframeUrl)
               ?? TryBuildOriginFromReferer(refererUrl)
               ?? AllohaOrigin;
    }

    private static string GetProxyResourceFileSuffix(Uri absoluteUrl)
    {
        var pathAndQuery = absoluteUrl.PathAndQuery;
        foreach (var knownSuffix in new[] { ".m3u8", ".m4s", ".mp4", ".ts", ".aac", ".vtt" })
        {
            if (pathAndQuery.IndexOf(knownSuffix, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return knownSuffix;
            }
        }

        var suffix = Path.GetExtension(absoluteUrl.AbsolutePath ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(suffix) &&
            suffix.Length <= 10 &&
            suffix[0] == '.' &&
            suffix.Skip(1).All(char.IsLetterOrDigit))
        {
            return suffix.ToLowerInvariant();
        }

        return ".ts";
    }

    private static string TryDecodeBody(byte[] body)
    {
        if (body == null || body.Length == 0)
        {
            return string.Empty;
        }

        return Encoding.UTF8.GetString(body);
    }

    private static string TrimForLog(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length <= 200)
        {
            return normalized;
        }

        return normalized.Substring(0, 200);
    }

    private void CleanupExpiredEntries()
    {
        foreach (var pair in ResolutionCache)
        {
            if (pair.Value.ExpiresAtUtc <= DateTime.UtcNow)
            {
                ResolutionCache.TryRemove(pair.Key, out _);
            }
        }

        foreach (var pair in SessionCache)
        {
            if (pair.Value.ExpiresAtUtc <= DateTime.UtcNow)
            {
                SessionCache.TryRemove(pair.Key, out _);
            }
        }
    }

    private static string BuildResolutionCacheKey(
        YummyAllohaSource source,
        int preferredQuality,
        string? preferredVoiceName)
    {
        return string.Join(
            "|",
            source.MovieToken.Trim(),
            source.RequestToken.Trim(),
            source.TranslationId.ToString(),
            source.SeasonNumber.ToString(),
            source.EpisodeNumber.ToString(),
            Math.Max(0, preferredQuality).ToString(),
            TranslationNameKeyNormalizer.Normalize(YummyVideoCatalog.NormalizeVoiceName(preferredVoiceName)));
    }

    private static Dictionary<string, string> BuildRequiredHttpHeaders(ResolvedAllohaPayload payload)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accepts-Controls"] = payload.AcceptsControls,
            ["Authorizations"] = "Bearer " + payload.Guard,
            ["Origin"] = string.IsNullOrWhiteSpace(payload.OriginUrl) ? AllohaOrigin : payload.OriginUrl,
            ["Referer"] = payload.RefererUrl
        };
    }

    private static YummyAllohaSource CloneSource(YummyAllohaSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return new YummyAllohaSource
        {
            MovieToken = source.MovieToken,
            RequestToken = source.RequestToken,
            TranslationId = source.TranslationId,
            SeasonNumber = source.SeasonNumber,
            EpisodeNumber = source.EpisodeNumber,
            Hidden = source.Hidden,
            RefererUrl = source.RefererUrl
        };
    }

    private sealed class CachedResolution
    {
        public DateTime ExpiresAtUtc { get; init; }
        public ResolvedAllohaPayload Payload { get; init; } = new();
    }

    private sealed class ResolvedAllohaPayload
    {
        public string ManifestUrl { get; init; } = string.Empty;
        public string ManifestText { get; init; } = string.Empty;
        public string AcceptsControls { get; init; } = string.Empty;
        public string Guard { get; init; } = string.Empty;
        public string OriginUrl { get; init; } = string.Empty;
        public string RefererUrl { get; init; } = string.Empty;
        public string IframeUrl { get; init; } = string.Empty;
        public string WebSocketBaseUrl { get; init; } = string.Empty;
        public string WebSocketSessionId { get; init; } = string.Empty;
        public string AudioTrackId { get; init; } = string.Empty;
        public string SelectedVoiceName { get; init; } = string.Empty;
        public IReadOnlyList<string> AvailableVoiceNames { get; init; } = Array.Empty<string>();
        public string StreamToken { get; init; } = string.Empty;
        public int SelectedQuality { get; init; }
    }

    private sealed class AllohaBootstrapPayload
    {
        public string Viewporti { get; init; } = string.Empty;
        public long FileId { get; init; }
    }

    private sealed class AllohaBnsiPayload
    {
        public int SkipTimeSeconds { get; init; }
        public int RemoveTimeSeconds { get; init; }
        public string WebSocketBaseUrl { get; init; } = string.Empty;
        public string WebSocketSessionId { get; init; } = string.Empty;
        public List<string> AvailableVoiceNames { get; } = new();
        public List<AllohaManifestCandidate> ManifestCandidates { get; } = new();
    }

    private sealed class AllohaManifestCandidate
    {
        public int? Quality { get; init; }
        public string Url { get; init; } = string.Empty;
        public string AudioTrackId { get; init; } = string.Empty;
        public string VoiceName { get; init; } = string.Empty;
    }
}

public sealed class AllohaPlaybackSession
{
    public string SessionId { get; init; } = string.Empty;
    public string ManifestUrl { get; set; } = string.Empty;
    public string ManifestText { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, string> RequiredHttpHeaders { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, string> ProxyResources { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> ProxyResourceReferers { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> ProxyResourceOriginalReferences { get; } = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, string> ProxyResourceParentIds { get; } = new(StringComparer.Ordinal);
    public string RefererUrl { get; set; } = string.Empty;
    public string IframeUrl { get; set; } = string.Empty;
    public string WebSocketBaseUrl { get; set; } = string.Empty;
    public string WebSocketSessionId { get; set; } = string.Empty;
    public string AudioTrackId { get; set; } = string.Empty;
    public string SelectedVoiceName { get; set; } = string.Empty;
    public IReadOnlyList<string> AvailableVoiceNames { get; set; } = Array.Empty<string>();
    public string StreamToken { get; set; } = string.Empty;
    public int SelectedQuality { get; set; }
    public YummyAllohaSource? Source { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class AllohaProxyResource
{
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
}
