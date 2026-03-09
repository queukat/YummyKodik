// File: Kodik/KodikClient.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;

namespace YummyKodik.Kodik
{
    /// <summary>
    /// Thin Kodik client that exposes only what Jellyfin plugin needs.
    /// </summary>
    public sealed class KodikClient
    {
        private const string KodikSearchUrl = "https://kodikapi.com/search";

        // Verbose HTTP logging settings.
        private const int HttpLogBodyMaxLen = 1500;
        private const int HttpLogFormMaxLen = 900;

        private readonly HttpClient _httpClient;
        private readonly string _token;
        private readonly ILogger? _logger;
        private readonly Func<bool> _isHttpLogEnabled;
        private int? _cryptStep;

        public KodikClient(HttpClient httpClient, string token, ILogger? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _token = token ?? throw new ArgumentNullException(nameof(token));

            // Default to plugin logger so call sites do not need to pass it explicitly.
            _logger = logger ?? Plugin.Instance?.Logger;

            // Config-driven toggle for request/response logs.
            // Read dynamically so changes in plugin settings apply without restart.
            _isHttpLogEnabled = () =>
            {
                try
                {
                    return Plugin.Instance?.Configuration?.EnableHttpDebugLogging ?? false;
                }
                catch
                {
                    return false;
                }
            };
        }

        /// <summary>
        /// Returns number of episodes and available translations for a given anime id.
        /// Prefer Kodik search endpoint because player page HTML changes frequently.
        /// </summary>
        public async Task<KodikAnimeInfo> GetAnimeInfoAsync(
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id must be non empty.", nameof(id));
            }

            _logger?.LogInformation("Kodik.GetAnimeInfoAsync: idType={IdType} id={Id}", idType, id);

            try
            {
                var results = await SearchAsync(id, idType, cancellationToken).ConfigureAwait(false);
                if (results.Count == 0)
                {
                    throw new KodikNoResultsException($"Kodik search returned no results for {idType} id {id}.");
                }

                var maxEpisode = 0;

                // builder to accumulate per-translation info without mutating init-only model
                var translationsMap = new Dictionary<string, (string Id, string Name, string Type, int MaxEp)>(StringComparer.Ordinal);

                foreach (var r in results)
                {
                    var epCount = r.EpisodesCount.GetValueOrDefault(0);
                    var lastEp = r.LastEpisode.GetValueOrDefault(0);

                    var epMax = Math.Max(epCount, lastEp);
                    maxEpisode = Math.Max(maxEpisode, epMax);

                    var tr = r.Translation;
                    if (tr == null)
                    {
                        continue;
                    }

                    var tid = tr.Id.HasValue ? tr.Id.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
                    var tname = (tr.Title ?? string.Empty).Trim();
                    var ttype = (tr.Type ?? string.Empty).Trim();

                    if (string.IsNullOrWhiteSpace(tid) && string.IsNullOrWhiteSpace(tname))
                    {
                        continue;
                    }

                    // prefer id as key, fallback to name-based key to avoid collisions
                    var key = !string.IsNullOrWhiteSpace(tid) ? tid : ("name:" + tname);

                    if (!translationsMap.TryGetValue(key, out var cur))
                    {
                        translationsMap[key] = (tid, tname, ttype, epMax);
                    }
                    else
                    {
                        var mergedId = string.IsNullOrWhiteSpace(cur.Id) ? tid : cur.Id;
                        var mergedName = string.IsNullOrWhiteSpace(cur.Name) ? tname : cur.Name;
                        var mergedType = string.IsNullOrWhiteSpace(cur.Type) ? ttype : cur.Type;
                        var mergedMax = Math.Max(cur.MaxEp, epMax);

                        translationsMap[key] = (mergedId, mergedName, mergedType, mergedMax);
                    }
                }

                var translations = translationsMap.Values
                    .Select(x => new KodikTranslation
                    {
                        Id = x.Id ?? string.Empty,
                        Name = x.Name ?? string.Empty,
                        Type = x.Type ?? string.Empty,
                        MaxEpisode = x.MaxEp
                    })
                    .Where(t => !string.IsNullOrWhiteSpace(t.Id) || !string.IsNullOrWhiteSpace(t.Name))
                    .ToList();

                if (translations.Count == 0)
                {
                    translations.Add(new KodikTranslation
                    {
                        Id = "0",
                        Name = "Unknown",
                        Type = "unknown",
                        MaxEpisode = maxEpisode
                    });
                }

                _logger?.LogInformation(
                    "Kodik.GetAnimeInfoAsync done (search). seriesCount={SeriesCount} translations={TrCount}",
                    maxEpisode,
                    translations.Count);

                return new KodikAnimeInfo(maxEpisode, translations);
            }
            catch (Exception ex) when (ex is KodikException or HttpRequestException or TaskCanceledException or JsonException)
            {
                // Fallback to old HTML parsing if search fails.
                _logger?.LogWarning(ex, "Kodik search failed, falling back to player HTML parsing. idType={IdType} id={Id}", idType, id);

                var playerUrl = await GetPlayerPageUrlAsync(id, idType, cancellationToken).ConfigureAwait(false);
                var html = await GetStringAsync(playerUrl, cancellationToken).ConfigureAwait(false);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var isSerial = IsSerialUrl(playerUrl);
                var seriesCount = 0;
                var translations = new List<KodikTranslation>();

                if (isSerial)
                {
                    var seriesSelect = doc.DocumentNode
                        .SelectSingleNode("//div[contains(@class,'serial-series-box')]//select");

                    if (seriesSelect != null)
                    {
                        var episodeOptions = seriesSelect.SelectNodes(".//option");
                        seriesCount = episodeOptions?.Count ?? 0;
                    }

                    translations.AddRange(ParseTranslations(doc, "//div[contains(@class,'serial-translations-box')]//select"));
                }
                else
                {
                    translations.AddRange(ParseTranslations(doc, "//div[contains(@class,'movie-translations-box')]//select"));
                }

                if (translations.Count == 0)
                {
                    translations.Add(new KodikTranslation
                    {
                        Id = "0",
                        Name = "Unknown",
                        Type = "unknown",
                        MaxEpisode = seriesCount
                    });
                }

                _logger?.LogInformation(
                    "Kodik.GetAnimeInfoAsync done (html). isSerial={IsSerial} seriesCount={SeriesCount} translations={TrCount}",
                    isSerial,
                    seriesCount,
                    translations.Count);

                return new KodikAnimeInfo(seriesCount, translations);
            }
        }

        /// <summary>
        /// Returns base path and max quality for a specific episode and translation.
        /// Uses Kodik search to obtain translation-specific player link, avoiding fragile HTML translation switching.
        /// </summary>
        public async Task<KodikLinkInfo> GetEpisodeLinkAsync(
            string id,
            KodikIdType idType,
            int episode,
            string translationId,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id must be non empty.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(translationId))
            {
                throw new ArgumentException("Translation id must be non empty.", nameof(translationId));
            }

            if (episode < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(episode), "Episode must be non negative.");
            }

            _logger?.LogInformation(
                "Kodik.GetEpisodeLinkAsync: idType={IdType} id={Id} episode={Episode} tr={TrId}",
                idType,
                id,
                episode,
                translationId);

            var playerPage = await GetEpisodePlayerPageAsync(
                    id,
                    idType,
                    episode,
                    translationId,
                    cancellationToken)
                .ConfigureAwait(false);

            var urlParams = ExtractUrlParams(playerPage.Html);
            var (videoType, videoHash, videoId) = ExtractVideoData(playerPage.Document, playerPage.RequestUrl);
            var scriptUrls = ExtractScriptSrcCandidates(playerPage.Document);
            if (scriptUrls.Count == 0)
            {
                _logger?.LogWarning("No script src candidates found on player page. url={Url}", SanitizeUrl(playerPage.RequestUrl));
                throw new KodikUnexpectedException("Player page does not contain script src tags.");
            }

            var linkData = await GetLinkWithDataAsync(
                videoType,
                videoHash,
                videoId,
                urlParams,
                scriptUrls,
                cancellationToken).ConfigureAwait(false);

            var directUrl = linkData.Url.Replace("https:", string.Empty, StringComparison.OrdinalIgnoreCase);
            var lastSlash = directUrl.LastIndexOf("/", StringComparison.Ordinal);
            if (lastSlash < 0)
            {
                _logger?.LogWarning("Direct url format not recognized. url={Url}", Short(linkData.Url, 300));
                throw new KodikUnexpectedException("Direct url format is not recognized.");
            }

            var basePath = directUrl.Substring(0, lastSlash + 1);

            _logger?.LogInformation(
                "Kodik link resolved. basePath={BasePath} maxQ={MaxQ}",
                Short(basePath, 200),
                linkData.MaxQuality);

            return new KodikLinkInfo(basePath, linkData.MaxQuality);
        }

        public async Task<KodikEpisodeTimings> GetEpisodeTimingsAsync(
            string id,
            KodikIdType idType,
            int episode,
            string translationId,
            CancellationToken cancellationToken = default)
        {
            var playerPage = await GetEpisodePlayerPageAsync(
                    id,
                    idType,
                    episode,
                    translationId,
                    cancellationToken)
                .ConfigureAwait(false);

            var timings = ParseEpisodeTimings(playerPage.Html);

            if (timings.HasAny)
            {
                _logger?.LogInformation(
                    "Kodik timings resolved. idType={IdType} id={Id} episode={Episode} tr={TrId} hasIntro={HasIntro} hasOutro={HasOutro}",
                    idType,
                    id,
                    episode,
                    translationId,
                    timings.Intro != null,
                    timings.Outro != null);
            }
            else
            {
                _logger?.LogInformation(
                    "Kodik timings not found. idType={IdType} id={Id} episode={Episode} tr={TrId}",
                    idType,
                    id,
                    episode,
                    translationId);
            }

            return timings;
        }

        /// <summary>
        /// Builds direct mp4 url.
        /// </summary>
        public static string BuildMp4Url(KodikLinkInfo link, int quality)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            var q = Math.Min(quality, link.MaxQuality);
            if (q <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be positive.");
            }

            return "https:" + link.BasePath + q + ".mp4";
        }

        /// <summary>
        /// Builds HLS manifest url.
        /// </summary>
        public static string BuildHlsUrl(KodikLinkInfo link, int quality)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            var q = Math.Min(quality, link.MaxQuality);
            if (q <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be positive.");
            }

            return "https:" + link.BasePath + q + ".mp4:hls:manifest.m3u8";
        }

        public async Task<TimeSpan?> GetEpisodeRuntimeAsync(
            string id,
            KodikIdType idType,
            int episode,
            string translationId,
            int quality,
            CancellationToken cancellationToken = default)
        {
            var link = await GetEpisodeLinkAsync(id, idType, episode, translationId, cancellationToken).ConfigureAwait(false);
            return await GetHlsRuntimeAsync(link, quality, cancellationToken).ConfigureAwait(false);
        }

        public async Task<TimeSpan?> GetHlsRuntimeAsync(
            KodikLinkInfo link,
            int quality,
            CancellationToken cancellationToken = default)
        {
            if (link == null)
            {
                throw new ArgumentNullException(nameof(link));
            }

            var manifestUrl = BuildHlsUrl(link, quality);
            return await GetHlsRuntimeFromManifestAsync(manifestUrl, depth: 0, cancellationToken).ConfigureAwait(false);
        }

        private async Task<(string RequestUrl, string Html, HtmlDocument Document)> GetEpisodePlayerPageAsync(
            string id,
            KodikIdType idType,
            int episode,
            string translationId,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                throw new ArgumentException("Id must be non empty.", nameof(id));
            }

            if (string.IsNullOrWhiteSpace(translationId))
            {
                throw new ArgumentException("Translation id must be non empty.", nameof(translationId));
            }

            if (episode < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(episode), "Episode must be non negative.");
            }

            string playerUrl;

            if (translationId == "0")
            {
                playerUrl = await GetPlayerPageUrlAsync(id, idType, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var results = await SearchAsync(id, idType, cancellationToken).ConfigureAwait(false);
                if (results.Count == 0)
                {
                    throw new KodikNoResultsException($"Kodik search returned no results for {idType} id {id}.");
                }

                KodikSearchResult? hit = null;

                foreach (var r in results)
                {
                    var trId = r.Translation?.Id.HasValue == true
                        ? r.Translation!.Id!.Value.ToString(CultureInfo.InvariantCulture)
                        : string.Empty;

                    if (string.Equals(trId, translationId, StringComparison.Ordinal))
                    {
                        hit = r;
                        break;
                    }
                }

                hit ??= results.FirstOrDefault();

                if (hit == null || string.IsNullOrWhiteSpace(hit.Link))
                {
                    throw new KodikNoResultsException($"Kodik search did not return usable player link for {idType} id {id}.");
                }

                playerUrl = EnsureAbsoluteKodikUrl(hit.Link);
            }

            var requestUrl = playerUrl;
            if (IsSerialUrl(playerUrl) && episode > 0)
            {
                requestUrl = BuildSerialEpisodeUrlFromPlayerUrl(playerUrl, episode);
            }

            var html = await GetStringAsync(requestUrl, cancellationToken).ConfigureAwait(false);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            return (requestUrl, html, doc);
        }

        private async Task<string> GetPlayerPageUrlAsync(
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken)
        {
            var builder = new StringBuilder("https://kodikapi.com/get-player?title=Player&hasPlayer=false&url=");

            switch (idType)
            {
                case KodikIdType.Shikimori:
                    builder.Append($"https%3A%2F%2Fkodikdb.com%2Ffind-player%3FshikimoriID%3D{id}");
                    builder.Append("&token=");
                    builder.Append(Uri.EscapeDataString(_token));
                    builder.Append("&shikimoriID=");
                    builder.Append(Uri.EscapeDataString(id));
                    break;

                case KodikIdType.Kinopoisk:
                    builder.Append($"https%3A%2F%2Fkodikdb.com%2Ffind-player%3FkinopoiskID%3D{id}");
                    builder.Append("&token=");
                    builder.Append(Uri.EscapeDataString(_token));
                    builder.Append("&kinopoiskID=");
                    builder.Append(Uri.EscapeDataString(id));
                    break;

                case KodikIdType.Imdb:
                    builder.Append($"https%3A%2F%2Fkodikdb.com%2Ffind-player%3FimdbID%3D{id}");
                    builder.Append("&token=");
                    builder.Append(Uri.EscapeDataString(_token));
                    builder.Append("&imdbID=");
                    builder.Append(Uri.EscapeDataString(id));
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(idType), idType, "Unknown id type.");
            }

            var requestUrl = builder.ToString();

            LogHttpRequest("GET", requestUrl, null);

            var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            LogHttpResponse("GET", requestUrl, response, content);

            // IMPORTANT: Kodik sometimes returns 500 but provides {"error":"..."} in body.
            if (!response.IsSuccessStatusCode)
            {
                if (TryExtractKodikError(content, out var apiError))
                {
                    _logger?.LogWarning(
                        "Kodik get-player returned error (HTTP {Status}). url={Url} error={Error}",
                        (int)response.StatusCode,
                        SanitizeUrl(requestUrl),
                        apiError);

                    if (IsTokenError(apiError))
                    {
                        throw new KodikTokenException("Kodik token is missing or invalid.");
                    }

                    throw new KodikServiceException($"Kodik get-player returned error: {apiError}");
                }

                _logger?.LogWarning(
                    "Kodik get-player HTTP failed. status={Status} url={Url} bodySnippet={Body}",
                    (int)response.StatusCode,
                    SanitizeUrl(requestUrl),
                    Short(content, 350));

                throw new KodikServiceException(
                    $"Unexpected status code {response.StatusCode} while calling get-player.");
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                _logger?.LogWarning("Kodik get-player returned error: {Error}", error);

                if (IsTokenError(error))
                {
                    throw new KodikTokenException("Kodik token is missing or invalid.");
                }

                throw new KodikServiceException($"Kodik get-player returned error: {error}");
            }

            if (!root.TryGetProperty("found", out var foundProp) || !foundProp.GetBoolean())
            {
                _logger?.LogInformation("Kodik get-player: not found for {IdType} id={Id}", idType, id);
                throw new KodikNoResultsException($"No player found for {idType} id {id}.");
            }

            var linkProp = root.GetProperty("link").GetString();
            if (string.IsNullOrEmpty(linkProp))
            {
                _logger?.LogWarning("Kodik get-player returned empty link.");
                throw new KodikServiceException("Kodik get-player returned empty link.");
            }

            string resolved;
            if (linkProp.StartsWith("//", StringComparison.Ordinal))
            {
                resolved = "https:" + linkProp;
            }
            else if (linkProp.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                resolved = linkProp;
            }
            else
            {
                resolved = "https://" + linkProp.TrimStart('/');
            }

            _logger?.LogDebug("Kodik get-player resolved link. url={Url}", SanitizeUrl(resolved));
            return resolved;
        }

        private static bool IsSerialUrl(string url)
        {
            const string marker = ".info/";
            var index = url.IndexOf(marker, StringComparison.Ordinal);

            if (index < 0 || index + marker.Length >= url.Length)
            {
                return false;
            }

            var c = url[index + marker.Length];
            return c == 's';
        }

        private static List<KodikTranslation> ParseTranslations(HtmlDocument doc, string selectXPath)
        {
            var result = new List<KodikTranslation>();
            var selectNode = doc.DocumentNode.SelectSingleNode(selectXPath);

            if (selectNode == null)
            {
                return result;
            }

            var optionNodes = selectNode.SelectNodes(".//option");
            if (optionNodes == null)
            {
                return result;
            }

            foreach (var option in optionNodes)
            {
                var valueId = option.GetAttributeValue("value", null);
                var dataId = option.GetAttributeValue("data-id", null);

                var translationId = !string.IsNullOrWhiteSpace(valueId)
                    ? valueId
                    : (dataId ?? "0");

                var type = option.GetAttributeValue("data-translation-type", string.Empty);
                var name = option.InnerText?.Trim() ?? string.Empty;

                if (string.IsNullOrEmpty(translationId) && string.IsNullOrEmpty(name))
                {
                    continue;
                }

                result.Add(new KodikTranslation
                {
                    Id = translationId,
                    Type = type,
                    Name = name,
                    MaxEpisode = 0
                });
            }

            return result;
        }

        private static string BuildSerialEpisodeUrlFromPlayerUrl(string playerUrl, int episode)
        {
            if (!Uri.TryCreate(playerUrl, UriKind.Absolute, out var uri))
            {
                throw new KodikUnexpectedException($"Player url is not absolute: {playerUrl}");
            }

            // Keep same path (/serial/{mediaId}/{mediaHash}/720p)
            // Update or add query params: episode, season, first_url, min_age.
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(uri.Query))
            {
                var q = uri.Query.StartsWith("?", StringComparison.Ordinal) ? uri.Query.Substring(1) : uri.Query;
                foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var k = Uri.UnescapeDataString(part.Substring(0, eq));
                    var v = Uri.UnescapeDataString(part.Substring(eq + 1));
                    dict[k] = v;
                }
            }

            dict["episode"] = episode.ToString(CultureInfo.InvariantCulture);
            if (!dict.ContainsKey("season"))
            {
                dict["season"] = "1";
            }
            if (!dict.ContainsKey("first_url"))
            {
                dict["first_url"] = "false";
            }
            if (!dict.ContainsKey("min_age"))
            {
                dict["min_age"] = "16";
            }

            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0)
                {
                    sb.Append('&');
                }

                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
            }

            var rebuilt = new UriBuilder(uri)
            {
                Query = sb.ToString()
            };

            return rebuilt.Uri.ToString();
        }

        private async Task<TimeSpan?> GetHlsRuntimeFromManifestAsync(
            string manifestUrl,
            int depth,
            CancellationToken cancellationToken)
        {
            if (depth > 4)
            {
                _logger?.LogDebug("Kodik HLS runtime resolution aborted due to manifest nesting depth. url={Url}", SanitizeUrl(manifestUrl));
                return null;
            }

            var manifest = await GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);

            if (TryParseHlsDuration(manifest, out var duration))
            {
                _logger?.LogDebug(
                    "Kodik HLS runtime resolved. url={Url} duration={Duration}",
                    SanitizeUrl(manifestUrl),
                    duration);

                return duration;
            }

            if (!TryGetNestedManifestUrl(manifestUrl, manifest, out var nestedManifestUrl))
            {
                _logger?.LogDebug("Kodik HLS runtime was not found in manifest. url={Url}", SanitizeUrl(manifestUrl));
                return null;
            }

            return await GetHlsRuntimeFromManifestAsync(nestedManifestUrl, depth + 1, cancellationToken).ConfigureAwait(false);
        }

        private static bool TryParseHlsDuration(string manifest, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(manifest))
            {
                return false;
            }

            double totalSeconds = 0;
            var foundSegments = false;

            foreach (var rawLine in manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (!line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line.Substring("#EXTINF:".Length);
                var commaIndex = value.IndexOf(',');
                if (commaIndex >= 0)
                {
                    value = value.Substring(0, commaIndex);
                }

                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) || seconds <= 0)
                {
                    continue;
                }

                totalSeconds += seconds;
                foundSegments = true;
            }

            if (!foundSegments || totalSeconds <= 0)
            {
                return false;
            }

            duration = TimeSpan.FromSeconds(totalSeconds);
            return true;
        }

        private static bool TryGetNestedManifestUrl(string manifestUrl, string manifest, out string nestedManifestUrl)
        {
            nestedManifestUrl = string.Empty;

            if (!Uri.TryCreate(manifestUrl, UriKind.Absolute, out var baseUri))
            {
                return false;
            }

            foreach (var rawLine in manifest.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    nestedManifestUrl = $"{baseUri.Scheme}:{line}";
                    return true;
                }

                if (Uri.TryCreate(baseUri, line, out var nestedUri))
                {
                    nestedManifestUrl = nestedUri.ToString();
                    return true;
                }
            }

            return false;
        }

        private Dictionary<string, string> ExtractUrlParams(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                throw new KodikUnexpectedException("Player html is empty.");
            }

            try
            {
                var json = ExtractJsAssignedJsonObject(html, "urlParams");
                _logger?.LogDebug("urlParams json extracted. len={Len} snippet={Snippet}", json.Length, Short(json, 450));
                return ParseJsonToDict(json);
            }
            catch (Exception ex) when (ex is not KodikException)
            {
                var near = TryGetSnippetNear(html, "urlParams", 900);
                _logger?.LogWarning(ex, "Failed to extract urlParams. htmlLen={Len} near={Near}", html.Length, Short(near, 900));
                throw new KodikUnexpectedException("urlParams block not found in player page.", ex);
            }
        }

        private Dictionary<string, string> ParseJsonToDict(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    _logger?.LogWarning("urlParams json is not object. kind={Kind} snippet={Snippet}", root.ValueKind, Short(json, 450));
                    throw new KodikUnexpectedException("urlParams json is not an object.");
                }

                var dict = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var prop in root.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.ValueKind switch
                    {
                        JsonValueKind.String => prop.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => prop.Value.GetRawText(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        JsonValueKind.Null => string.Empty,
                        JsonValueKind.Undefined => string.Empty,
                        JsonValueKind.Object => prop.Value.GetRawText(),
                        JsonValueKind.Array => prop.Value.GetRawText(),
                        _ => prop.Value.ToString()
                    };
                }

                _logger?.LogDebug("urlParams parsed. keys={Count}", dict.Count);
                return dict;
            }
            catch (JsonException ex)
            {
                _logger?.LogWarning(ex, "Failed to parse urlParams json. snippet={Snippet}", Short(json, 700));
                throw new KodikUnexpectedException("Failed to parse urlParams json block.", ex);
            }
        }

        private static string ExtractJsAssignedJsonObject(string html, string varName)
        {
            var idx = html.IndexOf(varName, StringComparison.Ordinal);
            if (idx < 0)
            {
                throw new InvalidOperationException($"Marker '{varName}' not found.");
            }

            var eq = html.IndexOf('=', idx);
            if (eq < 0)
            {
                throw new InvalidOperationException($"Assignment for '{varName}' not found.");
            }

            var pos = eq + 1;
            while (pos < html.Length && char.IsWhiteSpace(html[pos]))
            {
                pos++;
            }

            if (pos >= html.Length)
            {
                throw new InvalidOperationException("Unexpected end while parsing assignment.");
            }

            var first = html[pos];

            // Case 1: urlParams = {...};
            if (first == '{')
            {
                return ReadBalancedBraces(html, pos);
            }

            // Case 2: urlParams = '{...}';  or  urlParams = "{...}";
            if (first == '\'' || first == '"')
            {
                var quoted = ReadJsQuotedString(html, pos, first);
                var startObj = quoted.IndexOf('{');
                var endObj = quoted.LastIndexOf('}');
                if (startObj < 0 || endObj < startObj)
                {
                    throw new InvalidOperationException("Quoted urlParams does not contain JSON object.");
                }

                return quoted.Substring(startObj, endObj - startObj + 1);
            }

            throw new InvalidOperationException($"Unsupported urlParams assignment form. firstChar='{first}'");
        }

        private static KodikEpisodeTimings ParseEpisodeTimings(string html)
        {
            if (!TryExtractSkipButtonValue(html, out var rawRanges))
            {
                return new KodikEpisodeTimings(null, null);
            }

            var ranges = ParseSkipRanges(rawRanges);
            if (ranges.Count == 0)
            {
                return new KodikEpisodeTimings(null, null);
            }

            var intro = ranges.Count >= 1 ? ranges[0] : null;
            var outro = ranges.Count >= 2 ? ranges[1] : null;

            return new KodikEpisodeTimings(intro, outro);
        }

        private static bool TryExtractSkipButtonValue(string html, out string rawRanges)
        {
            rawRanges = string.Empty;

            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            var match = Regex.Match(
                html,
                @"playerSettings\.skipButton\s*=\s*parseSkipButton\(\s*(?:""(?<double>[^""]*)""|'(?<single>[^']*)')",
                RegexOptions.CultureInvariant);

            if (!match.Success)
            {
                return false;
            }

            rawRanges = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Value;

            rawRanges = (rawRanges ?? string.Empty).Trim();
            return rawRanges.Length > 0;
        }

        private static IReadOnlyList<KodikSkipRange> ParseSkipRanges(string rawRanges)
        {
            var result = new List<KodikSkipRange>(2);
            if (string.IsNullOrWhiteSpace(rawRanges))
            {
                return result;
            }

            foreach (var part in rawRanges.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var bounds = part.Split('-', 2, StringSplitOptions.TrimEntries);
                if (bounds.Length != 2)
                {
                    continue;
                }

                if (!TryParseSkipTimestamp(bounds[0], out var start) ||
                    !TryParseSkipTimestamp(bounds[1], out var end) ||
                    end <= start)
                {
                    continue;
                }

                result.Add(new KodikSkipRange(start, end));
            }

            return result;
        }

        private static bool TryParseSkipTimestamp(string raw, out TimeSpan time)
        {
            time = default;

            var value = (raw ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return false;
            }

            var parts = value.Split(':', StringSplitOptions.TrimEntries);
            if (parts.Length is < 1 or > 3)
            {
                return false;
            }

            var numbers = new int[3];
            var offset = 3 - parts.Length;

            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out numbers[offset + i]) ||
                    numbers[offset + i] < 0)
                {
                    return false;
                }
            }

            try
            {
                time = new TimeSpan(numbers[0], numbers[1], numbers[2]);
                return true;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
        }

        private static string ReadBalancedBraces(string s, int start)
        {
            var depth = 0;
            var i = start;

            while (i < s.Length)
            {
                var ch = s[i];

                if (ch == '{')
                {
                    depth++;
                }
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        // include this closing brace
                        return s.Substring(start, i - start + 1);
                    }
                }

                i++;
            }

            throw new InvalidOperationException("Unbalanced braces while reading JSON object.");
        }

        private static string ReadJsQuotedString(string s, int quotePos, char quote)
        {
            // quotePos points to opening quote
            var i = quotePos + 1;
            var sb = new StringBuilder();

            while (i < s.Length)
            {
                var ch = s[i];

                if (ch == '\\' && i + 1 < s.Length)
                {
                    // keep escaped chars as-is, JSON inside usually doesn't need unescaping here
                    sb.Append(s[i + 1]);
                    i += 2;
                    continue;
                }

                if (ch == quote)
                {
                    return sb.ToString();
                }

                sb.Append(ch);
                i++;
            }

            throw new InvalidOperationException("Unterminated quoted string while reading urlParams.");
        }

        private async Task<(string Url, int MaxQuality)> GetLinkWithDataAsync(
            string videoType,
            string videoHash,
            string videoId,
            Dictionary<string, string> urlParams,
            IReadOnlyList<string> scriptUrls,
            CancellationToken cancellationToken)
        {
            var postPath = await GetPostLinkFromScriptsAsync(scriptUrls, cancellationToken).ConfigureAwait(false);

            urlParams.TryGetValue("d", out var d);
            urlParams.TryGetValue("d_sign", out var dSign);
            urlParams.TryGetValue("pd", out var pd);
            urlParams.TryGetValue("pd_sign", out var pdSign);
            urlParams.TryGetValue("ref_sign", out var refSign);

            var payload = new Dictionary<string, string>
            {
                ["hash"] = videoHash,
                ["id"] = videoId,
                ["type"] = videoType,
                ["d"] = d ?? string.Empty,
                ["d_sign"] = dSign ?? string.Empty,
                ["pd"] = pd ?? string.Empty,
                ["pd_sign"] = pdSign ?? string.Empty,
                ["ref"] = string.Empty,
                ["ref_sign"] = refSign ?? string.Empty,
                ["bad_user"] = "true",
                ["cdn_is_working"] = "true"
            };

            var postUrl = "https://kodik.info" + postPath;

            LogHttpRequest("POST", postUrl, payload);

            using var content = new FormUrlEncodedContent(payload);
            var response = await _httpClient
                .PostAsync(postUrl, content, cancellationToken)
                .ConfigureAwait(false);

            var jsonString = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            LogHttpResponse("POST", postUrl, response, jsonString);

            if (!response.IsSuccessStatusCode)
            {
                if (TryExtractKodikError(jsonString, out var apiError))
                {
                    _logger?.LogWarning(
                        "Video links returned error (HTTP {Status}). url={Url} error={Error}",
                        (int)response.StatusCode,
                        SanitizeUrl(postUrl),
                        apiError);

                    if (IsTokenError(apiError))
                    {
                        throw new KodikTokenException("Kodik token is missing or invalid.");
                    }

                    throw new KodikServiceException($"Video links request returned error: {apiError}");
                }

                _logger?.LogWarning(
                    "Video links HTTP failed. status={Status} url={Url} bodySnippet={Body}",
                    (int)response.StatusCode,
                    SanitizeUrl(postUrl),
                    Short(jsonString, 450));

                throw new KodikServiceException(
                    $"Unexpected status code {response.StatusCode} while requesting video links.");
            }

            using var document = JsonDocument.Parse(jsonString);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                _logger?.LogWarning("Video links returned error: {Error}", error);

                if (IsTokenError(error))
                {
                    throw new KodikTokenException("Kodik token is missing or invalid.");
                }

                throw new KodikServiceException($"Video links request returned error: {error}");
            }

            var linksElement = root.GetProperty("links");
            string? dataUrl = null;
            var maxQuality = 0;

            foreach (var property in linksElement.EnumerateObject())
            {
                if (!int.TryParse(property.Name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var quality))
                {
                    continue;
                }

                if (quality > maxQuality)
                {
                    maxQuality = quality;
                }

                if (dataUrl == null)
                {
                    var items = property.Value;
                    if (items.ValueKind == JsonValueKind.Array && items.GetArrayLength() > 0)
                    {
                        if (items[0].TryGetProperty("src", out var srcEl) &&
                            srcEl.ValueKind == JsonValueKind.String)
                        {
                            var src = srcEl.GetString();
                            if (!string.IsNullOrWhiteSpace(src))
                            {
                                dataUrl = src;
                            }
                        }
                    }
                }
            }

            if (dataUrl == null)
            {
                _logger?.LogWarning("Base video url not found in links payload. payloadSnippet={Payload}", Short(jsonString, 800));
                throw new KodikUnexpectedException("Base video url not found in links payload.");
            }

            var finalUrl = dataUrl.IndexOf("mp4:hls:manifest", StringComparison.Ordinal) >= 0
                ? dataUrl
                : ConvertEncodedUrl(dataUrl);

            if (maxQuality == 0)
            {
                _logger?.LogWarning("Max quality could not be determined. payloadSnippet={Payload}", Short(jsonString, 800));
                throw new KodikUnexpectedException("Max quality could not be determined from links payload.");
            }

            _logger?.LogDebug("Video link selected. maxQ={MaxQ} urlSnippet={Url}", maxQuality, Short(finalUrl, 250));
            return (finalUrl, maxQuality);
        }

        private async Task<string> GetPostLinkFromScriptsAsync(IReadOnlyList<string> scriptUrls, CancellationToken cancellationToken)
        {
            Exception? last = null;

            foreach (var raw in scriptUrls)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                try
                {
                    return await GetPostLinkAsync(raw, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is KodikException or HttpRequestException or TaskCanceledException)
                {
                    last = ex;
                }
            }

            throw new KodikUnexpectedException("Failed to extract post link from any script src candidate.", last);
        }

        private async Task<string> GetPostLinkAsync(string scriptUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(scriptUrl))
            {
                throw new ArgumentNullException(nameof(scriptUrl));
            }

            var url = scriptUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? scriptUrl
                : (scriptUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + scriptUrl : "https://kodik.info" + (scriptUrl.StartsWith("/", StringComparison.Ordinal) ? scriptUrl : "/" + scriptUrl));

            LogHttpRequest("GET", url, null);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var scriptBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            LogHttpResponse("GET", url, response, scriptBody);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "Script fetch failed. status={Status} url={Url} bodySnippet={Body}",
                    (int)response.StatusCode,
                    SanitizeUrl(url),
                    Short(scriptBody, 450));

                throw new KodikServiceException(
                    $"Unexpected status code {response.StatusCode} while requesting script file.");
            }

            var ajaxIndex = scriptBody.IndexOf("$.ajax", StringComparison.Ordinal);
            if (ajaxIndex < 0)
            {
                _logger?.LogDebug("Script does not contain $.ajax. url={Url} snippet={Body}", SanitizeUrl(url), Short(scriptBody, 700));
                throw new KodikUnexpectedException("$.ajax call not found inside script file.");
            }

            var start = ajaxIndex + 30;
            if (start >= scriptBody.Length)
            {
                throw new KodikUnexpectedException("Unexpected script format while extracting post link.");
            }

            var cacheIndex = scriptBody.IndexOf("cache:!1", start, StringComparison.Ordinal);
            if (cacheIndex < 0)
            {
                _logger?.LogDebug("Script does not contain cache flag. url={Url} snippet={Body}", SanitizeUrl(url), Short(scriptBody, 700));
                throw new KodikUnexpectedException("cache flag not found while extracting post link.");
            }

            var encoded = scriptBody.Substring(start, cacheIndex - 3 - start);

            try
            {
                var bytes = Convert.FromBase64String(encoded);
                var decoded = Encoding.UTF8.GetString(bytes);
                _logger?.LogDebug("Post link decoded. path={Path}", Short(decoded, 160));
                return decoded;
            }
            catch (FormatException ex)
            {
                _logger?.LogWarning(ex, "Failed to decode base64 post link. encodedSnippet={Enc}", Short(encoded, 200));
                throw new KodikDecryptionException("Failed to decode base64 encoded post link.", ex);
            }
        }

        private string ConvertEncodedUrl(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded))
            {
                throw new ArgumentException("Encoded url is empty.", nameof(encoded));
            }

            if (_cryptStep.HasValue)
            {
                var attempt = TryDecodeWithRot(encoded, _cryptStep.Value);
                if (attempt != null)
                {
                    return attempt;
                }
            }

            for (var rot = 0; rot < 26; rot++)
            {
                var attempt = TryDecodeWithRot(encoded, rot);
                if (attempt == null)
                {
                    continue;
                }

                _cryptStep = rot;
                return attempt;
            }

            throw new KodikDecryptionException("Failed to decode Kodik video url.");
        }

        private string? TryDecodeWithRot(string encoded, int rot)
        {
            var shifted = ShiftAlphabet(encoded, rot);
            var padded = PadBase64(shifted);

            try
            {
                var bytes = Convert.FromBase64String(padded);
                var decoded = Encoding.UTF8.GetString(bytes);
                return decoded.IndexOf("mp4:hls:manifest", StringComparison.Ordinal) >= 0 ? decoded : null;
            }
            catch (FormatException)
            {
                return null;
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static string ShiftAlphabet(string input, int shift)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var sb = new StringBuilder(input.Length);

            foreach (var ch in input)
            {
                var isLower = char.IsLower(ch);
                var upper = char.ToUpperInvariant(ch);
                var index = alphabet.IndexOf(upper, StringComparison.Ordinal);

                if (index >= 0)
                {
                    var newChar = alphabet[(index + shift) % alphabet.Length];
                    sb.Append(isLower ? char.ToLowerInvariant(newChar) : newChar);
                }
                else
                {
                    sb.Append(ch);
                }
            }

            return sb.ToString();
        }

        private static string PadBase64(string input)
        {
            var padding = (4 - (input.Length % 4)) % 4;
            if (padding == 0)
            {
                return input;
            }

            return input + new string('=', padding);
        }

        private static string ExtractBetween(string source, string startMarker, string endMarker)
        {
            var start = source.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0)
            {
                return string.Empty;
            }

            start += startMarker.Length;

            var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                return string.Empty;
            }

            return source.Substring(start, end - start);
        }

        private static (string Type, string Hash, string Id) ExtractVideoData(HtmlDocument doc, string urlForLogs)
        {
            var scripts = doc.DocumentNode.SelectNodes("//script");
            if (scripts == null || scripts.Count == 0)
            {
                throw new KodikUnexpectedException("Player page contains no script tags.");
            }

            foreach (var s in scripts)
            {
                var text = s.InnerText;
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var type = ExtractBetween(text, ".type = '", "'");
                if (string.IsNullOrEmpty(type))
                {
                    type = ExtractBetween(text, ".type = \"", "\"");
                }

                var hash = ExtractBetween(text, ".hash = '", "'");
                if (string.IsNullOrEmpty(hash))
                {
                    hash = ExtractBetween(text, ".hash = \"", "\"");
                }

                var id = ExtractBetween(text, ".id = '", "'");
                if (string.IsNullOrEmpty(id))
                {
                    id = ExtractBetween(text, ".id = \"", "\"");
                }

                if (!string.IsNullOrEmpty(type) && !string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(id))
                {
                    return (type, hash, id);
                }
            }

            throw new KodikUnexpectedException($"Failed to parse video type, hash or id from player script. url={urlForLogs}");
        }

        private static List<string> ExtractScriptSrcCandidates(HtmlDocument doc)
        {
            var nodes = doc.DocumentNode.SelectNodes("//script[@src]");
            if (nodes == null || nodes.Count == 0)
            {
                return new List<string>(0);
            }

            var list = new List<string>(nodes.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var n in nodes)
            {
                var src = n.GetAttributeValue("src", null);
                if (string.IsNullOrWhiteSpace(src))
                {
                    continue;
                }

                var abs = src.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? src
                    : (src.StartsWith("//", StringComparison.Ordinal) ? "https:" + src : "https://kodik.info" + (src.StartsWith("/", StringComparison.Ordinal) ? src : "/" + src));

                if (seen.Add(abs))
                {
                    list.Add(abs);
                }
            }

            return list;
        }

        private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            LogHttpRequest("GET", url, null);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            LogHttpResponse("GET", url, response, content);

            if (!response.IsSuccessStatusCode)
            {
                _logger?.LogWarning(
                    "HTTP GET failed. status={Status} url={Url} bodySnippet={Body}",
                    (int)response.StatusCode,
                    SanitizeUrl(url),
                    Short(content, 450));

                throw new KodikServiceException(
                    $"Unexpected status code {response.StatusCode} for url {SanitizeUrl(url)}.");
            }

            return content;
        }

        private static string Short(string? s, int maxLen)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }

            if (s.Length <= maxLen)
            {
                return s;
            }

            return s.Substring(0, maxLen);
        }

        private static string TryGetSnippetNear(string source, string needle, int radius)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(needle))
            {
                return string.Empty;
            }

            var idx = source.IndexOf(needle, StringComparison.Ordinal);
            if (idx < 0)
            {
                return Short(source, radius);
            }

            var start = Math.Max(0, idx - radius / 2);
            var end = Math.Min(source.Length, start + radius);
            return source.Substring(start, end - start);
        }

        private static bool IsSensitiveKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            return key.Equals("token", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("password", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("access_token", StringComparison.OrdinalIgnoreCase) ||
                   key.Equals("cookie", StringComparison.OrdinalIgnoreCase) ||
                   key.EndsWith("_token", StringComparison.OrdinalIgnoreCase) ||
                   key.EndsWith("_password", StringComparison.OrdinalIgnoreCase);
        }

        private static string SanitizeValue(string key, string? value)
        {
            if (IsSensitiveKey(key))
            {
                return "***";
            }

            var v = (value ?? string.Empty).Trim();
            if (v.Length == 0)
            {
                return string.Empty;
            }

            // Replace newlines for compact log lines.
            v = v.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

            // Keep it short.
            return Short(v, 160);
        }

        private void LogHttpRequest(string method, string url, IReadOnlyDictionary<string, string>? form)
        {
            if (_logger == null || !_isHttpLogEnabled())
            {
                return;
            }

            var safeUrl = SanitizeUrl(url);

            if (form == null || form.Count == 0)
            {
                _logger.LogInformation("HTTP {Method} request. url={Url}", method, safeUrl);
                return;
            }

            // Log form keys and safe values, but keep it bounded.
            var parts = new List<string>(form.Count);

            foreach (var kv in form)
            {
                var k = kv.Key ?? string.Empty;
                var v = SanitizeValue(k, kv.Value);
                parts.Add($"{k}={v}");
            }

            var formDump = string.Join("&", parts);
            _logger.LogInformation(
                "HTTP {Method} request. url={Url} form={Form}",
                method,
                safeUrl,
                Short(formDump, HttpLogFormMaxLen));
        }

        private void LogHttpResponse(string method, string url, HttpResponseMessage response, string body)
        {
            if (_logger == null || !_isHttpLogEnabled())
            {
                return;
            }

            var safeUrl = SanitizeUrl(url);
            var len = body?.Length ?? 0;

            var snippet = body ?? string.Empty;
            snippet = snippet.Replace("\r", string.Empty, StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

            _logger.LogInformation(
                "HTTP {Method} response. status={Status} url={Url} len={Len} bodySnippet={Body}",
                method,
                (int)response.StatusCode,
                safeUrl,
                len,
                Short(snippet, HttpLogBodyMaxLen));
        }

        private static string SanitizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            // Mask common secret query params.
            return Regex.Replace(
                url,
                @"([?&](?:token|access_token|password|pwd|auth)=)[^&]+",
                "$1***",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        }

        private async Task<List<KodikSearchResult>> SearchAsync(string id, KodikIdType idType, CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["token"] = _token,
                ["limit"] = "100",
                ["all"] = "true",
                ["with_episodes"] = "false",
                ["with_episodes_data"] = "false"
            };

            switch (idType)
            {
                case KodikIdType.Shikimori:
                    payload["shikimori_id"] = id;
                    break;
                case KodikIdType.Kinopoisk:
                    payload["kinopoisk_id"] = id;
                    break;
                case KodikIdType.Imdb:
                    payload["imdb_id"] = id;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(idType), idType, "Unknown id type.");
            }

            var url = KodikSearchUrl + "?" + BuildQueryString(payload);

            LogHttpRequest("POST", url, payload);

            using var content = new FormUrlEncodedContent(payload);
            using var resp = await _httpClient.PostAsync(url, content, cancellationToken).ConfigureAwait(false);
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            LogHttpResponse("POST", url, resp, json);

            // IMPORTANT: Kodik sometimes returns 500 but provides {"error":"..."} in body.
            if (!resp.IsSuccessStatusCode)
            {
                if (TryExtractKodikError(json, out var apiError))
                {
                    _logger?.LogWarning(
                        "Kodik search returned error (HTTP {Status}). error={Error}",
                        (int)resp.StatusCode,
                        apiError);

                    if (IsTokenError(apiError))
                    {
                        throw new KodikTokenException("Kodik token is missing or invalid.");
                    }

                    throw new KodikServiceException($"Kodik search returned error: {apiError}");
                }

                _logger?.LogWarning("Kodik search HTTP failed. status={Status} bodySnippet={Body}", (int)resp.StatusCode, Short(json, 350));
                throw new KodikServiceException($"Kodik search failed: {resp.StatusCode}");
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var parsed = JsonSerializer.Deserialize<KodikSearchResponse>(json, options);

            if (!string.IsNullOrWhiteSpace(parsed?.Error))
            {
                _logger?.LogWarning("Kodik search returned error: {Error}", parsed!.Error);

                if (IsTokenError(parsed.Error))
                {
                    throw new KodikTokenException("Kodik token is missing or invalid.");
                }

                throw new KodikServiceException($"Kodik search returned error: {parsed.Error}");
            }

            return parsed?.Results ?? new List<KodikSearchResult>(0);
        }

        private static string BuildQueryString(Dictionary<string, string> dict)
        {
            var sb = new StringBuilder();
            foreach (var kv in dict)
            {
                if (sb.Length > 0)
                {
                    sb.Append('&');
                }

                sb.Append(Uri.EscapeDataString(kv.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(kv.Value ?? string.Empty));
            }

            return sb.ToString();
        }

        private static string EnsureAbsoluteKodikUrl(string linkOrUrl)
        {
            var s = (linkOrUrl ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                return s;
            }

            if (s.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }

            if (s.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + s;
            }

            if (s.StartsWith("/", StringComparison.Ordinal))
            {
                return "https://kodik.info" + s;
            }

            return "https://" + s.TrimStart('/');
        }

        private static bool TryExtractKodikError(string? json, out string error)
        {
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (!root.TryGetProperty("error", out var errProp))
                {
                    return false;
                }

                if (errProp.ValueKind == JsonValueKind.String)
                {
                    error = (errProp.GetString() ?? string.Empty).Trim();
                    return error.Length > 0;
                }

                // sometimes "error" might be non-string, keep raw
                error = errProp.GetRawText().Trim();
                return error.Length > 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static bool IsTokenError(string? error)
        {
            var e = (error ?? string.Empty).Trim();
            return string.Equals(e, "Отсутствует или неверный токен", StringComparison.Ordinal);
        }

        private sealed class KodikSearchResponse
        {
            [JsonPropertyName("error")]
            public string? Error { get; set; }

            [JsonPropertyName("results")]
            public List<KodikSearchResult>? Results { get; set; }
        }

        private sealed class KodikSearchResult
        {
            [JsonPropertyName("link")]
            public string? Link { get; set; }

            [JsonPropertyName("episodes_count")]
            public int? EpisodesCount { get; set; }

            [JsonPropertyName("last_episode")]
            public int? LastEpisode { get; set; }

            [JsonPropertyName("translation")]
            public KodikSearchTranslation? Translation { get; set; }
        }

        private sealed class KodikSearchTranslation
        {
            [JsonPropertyName("id")]
            public int? Id { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("type")]
            public string? Type { get; set; }
        }
    }
}
