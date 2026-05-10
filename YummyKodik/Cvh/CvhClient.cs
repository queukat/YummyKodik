using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Cvh;

public sealed class CvhClient
{
    private const string BaseUrl = "https://plapi.cdnvideohub.com/api/v1";
    private const string PlayerOrigin = "https://ru.yummyani.me";
    private static readonly TimeSpan SessionTtl = TimeSpan.FromHours(2);
    private static readonly ConcurrentDictionary<string, CvhPlaybackSession> SessionCache = new(StringComparer.Ordinal);
    private static readonly Regex ManifestUriAttributeRegex = new(
        "URI=(?<quote>[\"'])(?<uri>[^\"']+)\\k<quote>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public CvhClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<CvhResolvedStream> ResolveEpisodeStreamAsync(
        YummyCvhSource source,
        int preferredQuality,
        CancellationToken cancellationToken)
    {
        var cookies = new CvhCookieJar();
        return await ResolveEpisodeStreamAsync(source, preferredQuality, cookies, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CvhResolvedStream> ResolveEpisodeStreamAsync(
        YummyCvhSource source,
        int preferredQuality,
        CvhCookieJar cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (source.AnimeId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source.AnimeId));
        }

        if (source.EpisodeNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source.EpisodeNumber));
        }

        var playlist = await GetPlaylistAsync(source, cookies, cancellationToken).ConfigureAwait(false);
        var preferredVoice = PickPreferredVoice(source);
        var item = PickPlaylistItem(playlist.Items, source.EpisodeNumber, preferredVoice);
        if (item == null || string.IsNullOrWhiteSpace(item.VkId))
        {
            throw new InvalidOperationException($"CVH episode {source.EpisodeNumber} is not available for animeId={source.AnimeId}.");
        }

        var video = await GetVideoAsync(item.VkId.Trim(), source, cookies, cancellationToken).ConfigureAwait(false);
        var streamUrl = PickStreamUrl(video.Sources, preferredQuality);
        if (string.IsNullOrWhiteSpace(streamUrl))
        {
            throw new InvalidOperationException($"CVH did not return a playable stream URL for animeId={source.AnimeId} episode={source.EpisodeNumber}.");
        }

        return new CvhResolvedStream
        {
            StreamUrl = streamUrl,
            DurationSeconds = video.Duration > 0 ? video.Duration : null,
            VoiceName = NormalizeVoiceName(item.VoiceStudio)
        };
    }

    public async Task<CvhPlaylistResponse> GetPlaylistAsync(
        YummyCvhSource source,
        CancellationToken cancellationToken)
    {
        return await GetPlaylistAsync(source, cookies: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CvhPlaylistResponse> GetPlaylistAsync(
        YummyCvhSource source,
        CvhCookieJar? cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        var requestUri = $"{BaseUrl}/player/sv/playlist?pub={source.PublisherId}&id={source.AnimeId}&aggr={Uri.EscapeDataString(source.Aggregator)}";
        using var request = CreateBrowserLikeRequest(HttpMethod.Get, requestUri, source);
        using var response = await SendWithCookiesAsync(request, cookies, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return DeserializePayload<CvhPlaylistResponse>(json, $"playlist animeId={source.AnimeId}");
    }

    public async Task<CvhVideoResponse> GetVideoAsync(string vkId, YummyCvhSource source, CancellationToken cancellationToken)
    {
        return await GetVideoAsync(vkId, source, cookies: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CvhVideoResponse> GetVideoAsync(
        string vkId,
        YummyCvhSource source,
        CvhCookieJar? cookies,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vkId))
        {
            throw new ArgumentException("VK id must be non-empty.", nameof(vkId));
        }

        ArgumentNullException.ThrowIfNull(source);

        var requestUri = $"{BaseUrl}/player/sv/video/{Uri.EscapeDataString(vkId.Trim())}";
        using var request = CreateBrowserLikeRequest(HttpMethod.Get, requestUri, source);
        using var response = await SendWithCookiesAsync(request, cookies, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return DeserializePayload<CvhVideoResponse>(json, $"video vkId={vkId.Trim()}");
    }

    public async Task<CvhPlaybackSession> CreatePlaybackSessionAsync(
        YummyCvhSource source,
        int preferredQuality,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        CleanupExpiredSessions();

        var cookies = new CvhCookieJar();
        var resolved = await ResolveEpisodeStreamAsync(source, preferredQuality, cookies, cancellationToken).ConfigureAwait(false);
        var manifestText = await DownloadManifestAsync(resolved.StreamUrl, source, cookies, cancellationToken).ConfigureAwait(false);
        manifestText = PinManifestToPreferredQuality(manifestText, preferredQuality);

        var session = new CvhPlaybackSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ManifestUrl = resolved.StreamUrl,
            ManifestText = manifestText,
            Source = CloneSource(source),
            Cookies = cookies,
            ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl)
        };

        SessionCache[session.SessionId] = session;
        return session;
    }

    public bool TryGetSession(string sessionId, out CvhPlaybackSession session)
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

    public bool TryResolveProxyResourceUrl(CvhPlaybackSession session, string resourceId, out string resourceUrl)
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

    public async Task<CvhProxyResource> DownloadProxyResourceAsync(
        CvhPlaybackSession session,
        string resourceUrl,
        string proxyBaseUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var upstreamUrl = (resourceUrl ?? string.Empty).Trim();
        if (upstreamUrl.Length == 0)
        {
            throw new InvalidOperationException("CVH proxy resource url is empty.");
        }

        using var request = CreateProxyResourceRequest(upstreamUrl, session.Source);

        using var response = await SendWithCookiesAsync(request, session.Cookies, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"CVH proxy resource request failed. status={(int)response.StatusCode} url={upstreamUrl} body={TrimForLog(TryDecodeBody(body))}");
        }

        session.ExpiresAtUtc = DateTime.UtcNow.Add(SessionTtl);

        if (LooksLikeManifest(upstreamUrl, mediaType, body))
        {
            var manifestText = TryDecodeBody(body);
            var rewritten = RewriteManifestUrls(session, upstreamUrl, manifestText, proxyBaseUrl);
            return new CvhProxyResource
            {
                Content = Encoding.UTF8.GetBytes(rewritten),
                ContentType = ResolveManifestContentType(mediaType)
            };
        }

        return new CvhProxyResource
        {
            Content = body,
            ContentType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType
        };
    }

    public static string BuildManifestResponseBody(CvhPlaybackSession session, string proxyBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(session);
        return RewriteManifestUrls(session, session.ManifestUrl, session.ManifestText, proxyBaseUrl);
    }

    public async Task<string> DownloadManifestAsync(string manifestUrl, YummyCvhSource source, CancellationToken cancellationToken)
    {
        return await DownloadManifestAsync(manifestUrl, source, cookies: null, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> DownloadManifestAsync(
        string manifestUrl,
        YummyCvhSource source,
        CvhCookieJar? cookies,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new ArgumentException("Manifest url must be non-empty.", nameof(manifestUrl));
        }

        ArgumentNullException.ThrowIfNull(source);

        using var request = CreateBrowserLikeRequest(HttpMethod.Get, manifestUrl.Trim(), source);
        request.Headers.Accept.ParseAdd("application/vnd.apple.mpegurl");
        request.Headers.Accept.ParseAdd("application/x-mpegURL");
        request.Headers.Accept.ParseAdd("*/*");

        using var response = await SendWithCookiesAsync(request, cookies, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"CVH manifest request failed for animeId={source.AnimeId} episode={source.EpisodeNumber} status={(int)response.StatusCode} body={TrimForLog(text)}");
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException($"CVH returned an empty manifest for animeId={source.AnimeId} episode={source.EpisodeNumber}.");
        }

        return RewriteManifestUrls(manifestUrl.Trim(), text);
    }

    private static CvhPlaylistItem? PickPlaylistItem(
        IReadOnlyList<CvhPlaylistItem>? items,
        int episodeNumber,
        string? preferredVoice)
    {
        items ??= Array.Empty<CvhPlaylistItem>();
        var episodeItems = items
            .Where(x => x.Episode == episodeNumber)
            .ToList();

        if (episodeItems.Count == 0)
        {
            return null;
        }

        var voice = NormalizeVoiceName(preferredVoice);
        if (!string.IsNullOrWhiteSpace(voice))
        {
            var voiceKey = TranslationNameKeyNormalizer.Normalize(voice);
            if (!string.IsNullOrWhiteSpace(voiceKey))
            {
                var keyMatch = episodeItems.FirstOrDefault(x =>
                    string.Equals(
                        TranslationNameKeyNormalizer.Normalize(NormalizeVoiceName(x.VoiceStudio)),
                        voiceKey,
                        StringComparison.Ordinal));

                if (keyMatch != null)
                {
                    return keyMatch;
                }
            }

            var exact = episodeItems.FirstOrDefault(x =>
                string.Equals(NormalizeVoiceName(x.VoiceStudio), voice, StringComparison.OrdinalIgnoreCase));

            if (exact != null)
            {
                return exact;
            }

            var partial = episodeItems.FirstOrDefault(x =>
                NormalizeVoiceName(x.VoiceStudio).Contains(voice, StringComparison.OrdinalIgnoreCase) ||
                voice.Contains(NormalizeVoiceName(x.VoiceStudio), StringComparison.OrdinalIgnoreCase));

            if (partial != null)
            {
                return partial;
            }

            return null;
        }

        return episodeItems
            .OrderBy(x => NormalizeVoiceName(x.VoiceStudio), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string PickStreamUrl(CvhVideoSources? sources, int preferredQuality)
    {
        if (!string.IsNullOrWhiteSpace(sources?.HlsUrl))
        {
            return sources.HlsUrl.Trim();
        }

        preferredQuality = preferredQuality <= 0 ? 720 : preferredQuality;

        return preferredQuality switch
        {
            >= 1080 when !string.IsNullOrWhiteSpace(sources?.MpegFullHdUrl) => sources!.MpegFullHdUrl!.Trim(),
            >= 720 when !string.IsNullOrWhiteSpace(sources?.MpegHighUrl) => sources!.MpegHighUrl!.Trim(),
            >= 480 when !string.IsNullOrWhiteSpace(sources?.MpegMediumUrl) => sources!.MpegMediumUrl!.Trim(),
            _ when !string.IsNullOrWhiteSpace(sources?.MpegLowUrl) => sources!.MpegLowUrl!.Trim(),
            _ => string.Empty
        };
    }

    private static string PinManifestToPreferredQuality(string manifestText, int preferredQuality)
    {
        if (preferredQuality <= 0 || string.IsNullOrWhiteSpace(manifestText))
        {
            return manifestText;
        }

        var lines = manifestText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var variants = ParseManifestVariants(lines);
        if (variants.Count <= 1)
        {
            return manifestText;
        }

        var selected = SelectManifestVariant(variants, preferredQuality);
        if (selected == null)
        {
            return manifestText;
        }

        var skippedLineIndexes = new HashSet<int>();
        foreach (var variant in variants)
        {
            if (variant.StreamInfLineIndex == selected.Value.StreamInfLineIndex &&
                variant.UriLineIndex == selected.Value.UriLineIndex)
            {
                continue;
            }

            skippedLineIndexes.Add(variant.StreamInfLineIndex);
            skippedLineIndexes.Add(variant.UriLineIndex);
        }

        var filteredLines = new List<string>(lines.Length);
        for (var i = 0; i < lines.Length; i++)
        {
            if (!skippedLineIndexes.Contains(i))
            {
                filteredLines.Add(lines[i]);
            }
        }

        return string.Join('\n', filteredLines);
    }

    private static List<CvhManifestVariant> ParseManifestVariants(string[] lines)
    {
        var variants = new List<CvhManifestVariant>();
        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (!trimmed.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var uriIndex = -1;
            for (var j = i + 1; j < lines.Length; j++)
            {
                var uriLine = lines[j].Trim();
                if (uriLine.Length == 0)
                {
                    continue;
                }

                if (uriLine.StartsWith("#", StringComparison.Ordinal))
                {
                    break;
                }

                uriIndex = j;
                break;
            }

            if (uriIndex < 0)
            {
                continue;
            }

            variants.Add(new CvhManifestVariant(
                StreamInfLineIndex: i,
                UriLineIndex: uriIndex,
                Height: ParseManifestVariantHeight(trimmed),
                Bandwidth: ParseManifestVariantBandwidth(trimmed)));
        }

        return variants;
    }

    private static CvhManifestVariant? SelectManifestVariant(IReadOnlyList<CvhManifestVariant> variants, int preferredQuality)
    {
        var variantsWithHeight = variants
            .Where(x => x.Height.HasValue && x.Height.Value > 0)
            .OrderBy(x => GetQualityDistanceScore(x.Height!.Value, preferredQuality))
            .ThenByDescending(x => x.Height!.Value)
            .ToList();

        if (variantsWithHeight.Count > 0)
        {
            return variantsWithHeight[0];
        }

        var variantsWithBandwidth = variants
            .Where(x => x.Bandwidth.HasValue && x.Bandwidth.Value > 0)
            .OrderBy(x => x.Bandwidth!.Value)
            .ToList();

        if (variantsWithBandwidth.Count == 0)
        {
            return null;
        }

        var index = SelectBandwidthVariantIndex(variantsWithBandwidth.Count, preferredQuality);
        return variantsWithBandwidth[Math.Clamp(index, 0, variantsWithBandwidth.Count - 1)];
    }

    private static int GetQualityDistanceScore(int actualQuality, int preferredQuality)
    {
        if (actualQuality == preferredQuality)
        {
            return 0;
        }

        if (actualQuality < preferredQuality)
        {
            return preferredQuality - actualQuality;
        }

        return 100000 + (actualQuality - preferredQuality);
    }

    private static int SelectBandwidthVariantIndex(int variantCount, int preferredQuality)
    {
        if (variantCount <= 1)
        {
            return 0;
        }

        if (preferredQuality >= 1080)
        {
            return variantCount - 1;
        }

        if (preferredQuality >= 720)
        {
            return variantCount >= 3 ? variantCount - 2 : variantCount - 1;
        }

        if (preferredQuality >= 480)
        {
            return variantCount >= 4 ? variantCount - 3 : 0;
        }

        return 0;
    }

    private static int? ParseManifestVariantHeight(string manifestLine)
    {
        var resolutionMatch = Regex.Match(
            manifestLine ?? string.Empty,
            @"RESOLUTION=\d+x(?<height>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!resolutionMatch.Success)
        {
            return null;
        }

        return int.TryParse(
            resolutionMatch.Groups["height"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var height) && height > 0
            ? height
            : null;
    }

    private static long? ParseManifestVariantBandwidth(string manifestLine)
    {
        var bandwidthMatch = Regex.Match(
            manifestLine ?? string.Empty,
            @"(?:AVERAGE-)?BANDWIDTH=(?<value>\d+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        if (!bandwidthMatch.Success)
        {
            return null;
        }

        return long.TryParse(
            bandwidthMatch.Groups["value"].Value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var bandwidth) && bandwidth > 0
            ? bandwidth
            : null;
    }

    private static string NormalizeVoiceName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("Озвучка ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("Озвучка ".Length).Trim();
        }

        return normalized;
    }

    private static string? PickPreferredVoice(YummyCvhSource source)
    {
        var dubbingName = NormalizeVoiceName(source.DubbingName);
        if (!string.IsNullOrWhiteSpace(dubbingName))
        {
            return dubbingName;
        }

        var dubbingCode = NormalizeVoiceName(source.DubbingCode);
        if (string.IsNullOrWhiteSpace(dubbingCode) || !dubbingCode.Any(char.IsLetter))
        {
            return null;
        }

        return dubbingCode;
    }

    private static HttpRequestMessage CreateBrowserLikeRequest(HttpMethod method, string requestUri, YummyCvhSource source)
    {
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Referrer = new Uri(BuildIframeReferer(source));
        request.Headers.TryAddWithoutValidation("Origin", PlayerOrigin);
        request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36");
        return request;
    }

    private static HttpRequestMessage CreateProxyResourceRequest(string requestUri, YummyCvhSource source)
    {
        var request = CreateBrowserLikeRequest(HttpMethod.Get, requestUri, source);
        request.Headers.Accept.Clear();

        if (GuessProxyResourceKind(requestUri) == CvhProxyResourceKind.Playlist)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.apple.mpegurl"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-mpegURL"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
            request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
            request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
            return request;
        }

        // Media chunks are fetched by the browser media stack, not via XHR-like CORS requests.
        // Keep the request surface minimal for segment/key downloads.
        request.Headers.Remove("Origin");
        request.Headers.Remove("Sec-Fetch-Site");
        request.Headers.Remove("Sec-Fetch-Mode");
        request.Headers.Remove("Sec-Fetch-Dest");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return request;
    }

    private async Task<HttpResponseMessage> SendWithCookiesAsync(
        HttpRequestMessage request,
        CvhCookieJar? cookies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cookies != null && request.RequestUri != null)
        {
            var cookieHeader = cookies.GetCookieHeader(request.RequestUri);
            if (!string.IsNullOrWhiteSpace(cookieHeader))
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
        }

        var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        cookies?.Capture(response);
        return response;
    }

    private static string RewriteManifestUrls(string manifestUrl, string manifestText)
    {
        if (string.IsNullOrWhiteSpace(manifestText) || !Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri))
        {
            return manifestText;
        }

        var lines = manifestText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, line, out var absolute))
            {
                lines[i] = absolute.ToString();
            }
        }

        return string.Join('\n', lines);
    }

    private static string RewriteManifestUrls(
        CvhPlaybackSession session,
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
        var nextUriKind = CvhProxyResourceKind.Unknown;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                var attributeKind = DetermineAttributeUriKind(trimmed);
                if (attributeKind != CvhProxyResourceKind.Unknown)
                {
                    lines[i] = RewriteManifestUriAttributes(session, proxyBaseUrl, baseUri, line, attributeKind);
                }

                nextUriKind = DetermineNextUriKind(trimmed);
                continue;
            }

            if (Uri.TryCreate(baseUri, trimmed, out var absolute))
            {
                lines[i] = BuildProxyUrl(session, proxyBaseUrl, absolute.ToString(), nextUriKind);
            }

            nextUriKind = CvhProxyResourceKind.Unknown;
        }

        return string.Join('\n', lines);
    }

    private static string RewriteManifestUriAttributes(
        CvhPlaybackSession session,
        string proxyBaseUrl,
        Uri baseUri,
        string line,
        CvhProxyResourceKind kind)
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

                var proxied = BuildProxyUrl(session, proxyBaseUrl, absolute.ToString(), kind);
                return "URI=" + quote + proxied + quote;
            });
    }

    private static string BuildProxyUrl(
        CvhPlaybackSession session,
        string proxyBaseUrl,
        string upstreamUrl,
        CvhProxyResourceKind kind)
    {
        var resourceKey = ComputeResourceKey(upstreamUrl);
        session.ProxyResources[resourceKey] = upstreamUrl;

        var resourceName = resourceKey + DetermineProxyResourceExtension(upstreamUrl, kind);
        return $"{proxyBaseUrl}/{resourceName}?sessionId={Uri.EscapeDataString(session.SessionId)}&resource={Uri.EscapeDataString(resourceKey)}";
    }

    private static string DetermineProxyResourceExtension(string upstreamUrl, CvhProxyResourceKind kind)
    {
        if (Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var absolute))
        {
            var extension = Path.GetExtension(absolute.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 10)
            {
                return extension;
            }
        }

        return kind switch
        {
            CvhProxyResourceKind.Playlist => ".m3u8",
            CvhProxyResourceKind.Segment => ".ts",
            _ => ".bin"
        };
    }

    private static CvhProxyResourceKind GuessProxyResourceKind(string upstreamUrl)
    {
        if (!Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var absolute))
        {
            return CvhProxyResourceKind.Unknown;
        }

        var extension = Path.GetExtension(absolute.AbsolutePath);
        return extension.ToLowerInvariant() switch
        {
            ".m3u8" => CvhProxyResourceKind.Playlist,
            ".ts" => CvhProxyResourceKind.Segment,
            ".m4s" => CvhProxyResourceKind.Segment,
            ".mp4" => CvhProxyResourceKind.Segment,
            ".key" => CvhProxyResourceKind.Segment,
            _ => CvhProxyResourceKind.Unknown
        };
    }

    private static string ComputeResourceKey(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static CvhProxyResourceKind DetermineNextUriKind(string manifestLine)
    {
        if (manifestLine.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            return CvhProxyResourceKind.Playlist;
        }

        if (manifestLine.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
        {
            return CvhProxyResourceKind.Segment;
        }

        return CvhProxyResourceKind.Unknown;
    }

    private static CvhProxyResourceKind DetermineAttributeUriKind(string manifestLine)
    {
        if (manifestLine.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase) ||
            manifestLine.StartsWith("#EXT-X-I-FRAME-STREAM-INF", StringComparison.OrdinalIgnoreCase))
        {
            return CvhProxyResourceKind.Playlist;
        }

        if (manifestLine.StartsWith("#EXT-X-MAP", StringComparison.OrdinalIgnoreCase) ||
            manifestLine.StartsWith("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
        {
            return CvhProxyResourceKind.Segment;
        }

        return CvhProxyResourceKind.Unknown;
    }

    private static bool LooksLikeManifest(string upstreamUrl, string mediaType, byte[] body)
    {
        if (!string.IsNullOrWhiteSpace(mediaType) &&
            (mediaType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) ||
             mediaType.Contains("vnd.apple.mpegurl", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var text = TryDecodeBody(body);
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            return true;
        }

        return upstreamUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveManifestContentType(string mediaType)
    {
        return string.IsNullOrWhiteSpace(mediaType) ? "application/vnd.apple.mpegurl" : mediaType;
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

    private static YummyCvhSource CloneSource(YummyCvhSource source)
    {
        return new YummyCvhSource
        {
            AnimeId = source.AnimeId,
            EpisodeNumber = source.EpisodeNumber,
            DubbingCode = source.DubbingCode,
            DubbingName = source.DubbingName,
            Aggregator = source.Aggregator,
            PublisherId = source.PublisherId
        };
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

    private static string TrimForLog(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.Length <= 200)
        {
            return normalized;
        }

        return normalized.Substring(0, 200);
    }

    private static string BuildIframeReferer(YummyCvhSource source)
    {
        var dubbingCode = (source.DubbingCode ?? string.Empty).Trim();
        var dubbingName = (source.DubbingName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(dubbingName) && !string.IsNullOrWhiteSpace(dubbingCode))
        {
            dubbingName = "Озвучка " + dubbingCode;
        }

        var query = new List<string>
        {
            "anime_id=" + Uri.EscapeDataString(source.AnimeId.ToString(CultureInfo.InvariantCulture)),
            "episode=" + Uri.EscapeDataString(source.EpisodeNumber.ToString(CultureInfo.InvariantCulture))
        };

        if (!string.IsNullOrWhiteSpace(dubbingCode))
        {
            query.Add("dubbing_code=" + Uri.EscapeDataString(dubbingCode));
        }

        if (!string.IsNullOrWhiteSpace(dubbingName))
        {
            query.Add("dubbing=" + Uri.EscapeDataString(dubbingName));
        }

        return PlayerOrigin + "/iframeCVH.html?" + string.Join("&", query);
    }

    private static T DeserializePayload<T>(string json, string context)
        where T : class, new()
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException($"CVH returned an empty {context} response.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions) ?? new T();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"CVH returned invalid JSON for {context}.", ex);
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
}

public sealed class CvhResolvedStream
{
    public string StreamUrl { get; init; } = string.Empty;
    public int? DurationSeconds { get; init; }
    public string VoiceName { get; init; } = string.Empty;
}

public sealed class CvhPlaylistResponse
{
    [JsonPropertyName("titleName")]
    public string TitleName { get; set; } = string.Empty;

    [JsonPropertyName("isSerial")]
    public bool IsSerial { get; set; }

    [JsonPropertyName("items")]
    public List<CvhPlaylistItem> Items { get; set; } = new();
}

public sealed class CvhPlaylistItem
{
    [JsonPropertyName("cvhId")]
    public string? CvhId { get; set; }

    [JsonPropertyName("vkId")]
    public string? VkId { get; set; }

    [JsonPropertyName("voiceStudio")]
    public string? VoiceStudio { get; set; }

    [JsonPropertyName("voiceType")]
    public string? VoiceType { get; set; }

    [JsonPropertyName("season")]
    public int EpisodeSeason { get; set; }

    [JsonPropertyName("episode")]
    public int Episode { get; set; }
}

public sealed class CvhVideoResponse
{
    [JsonPropertyName("duration")]
    public int Duration { get; set; }

    [JsonPropertyName("thumbUrl")]
    public string? ThumbUrl { get; set; }

    [JsonPropertyName("sources")]
    public CvhVideoSources? Sources { get; set; }
}

public sealed class CvhVideoSources
{
    [JsonPropertyName("hlsUrl")]
    public string? HlsUrl { get; set; }

    [JsonPropertyName("dashUrl")]
    public string? DashUrl { get; set; }

    [JsonPropertyName("mpegLowUrl")]
    public string? MpegLowUrl { get; set; }

    [JsonPropertyName("mpegMediumUrl")]
    public string? MpegMediumUrl { get; set; }

    [JsonPropertyName("mpegHighUrl")]
    public string? MpegHighUrl { get; set; }

    [JsonPropertyName("mpegFullHdUrl")]
    public string? MpegFullHdUrl { get; set; }
}

public sealed class CvhPlaybackSession
{
    public string SessionId { get; init; } = string.Empty;
    public string ManifestUrl { get; init; } = string.Empty;
    public string ManifestText { get; init; } = string.Empty;
    public YummyCvhSource Source { get; init; } = new();
    internal CvhCookieJar Cookies { get; init; } = new();
    public ConcurrentDictionary<string, string> ProxyResources { get; } = new(StringComparer.Ordinal);
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed class CvhProxyResource
{
    public byte[] Content { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
}

internal enum CvhProxyResourceKind
{
    Unknown = 0,
    Playlist = 1,
    Segment = 2
}

internal readonly record struct CvhManifestVariant(
    int StreamInfLineIndex,
    int UriLineIndex,
    int? Height,
    long? Bandwidth);

internal sealed class CvhCookieJar
{
    private readonly CookieContainer _cookies = new();
    private readonly object _syncRoot = new();

    public string GetCookieHeader(Uri requestUri)
    {
        if (requestUri == null)
        {
            return string.Empty;
        }

        lock (_syncRoot)
        {
            return _cookies.GetCookieHeader(requestUri);
        }
    }

    public void Capture(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var requestUri = response.RequestMessage?.RequestUri;
        if (requestUri == null || !response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            return;
        }

        lock (_syncRoot)
        {
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                try
                {
                    _cookies.SetCookies(requestUri, value);
                }
                catch (CookieException)
                {
                    // Ignore malformed upstream cookies; a bad cookie should not abort playback.
                }
            }
        }
    }
}
