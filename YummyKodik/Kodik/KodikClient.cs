// File: Kodik/KodikClient.cs

using System;
using System.Collections.Concurrent;
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
        private const string KodikSearchUrl = "https://kodik-api.com/search";
        private const string KodikPlayerBaseUrl = "https://kodikplayer.com";
        private const string HttpsSchemePrefix = "https:";
        private const string TokenInvalidMessage = "Kodik token is missing or invalid.";
        private const int UnknownCryptStep = -1;

        // Verbose HTTP logging settings.
        private const int HttpLogBodyMaxLen = 1500;
        private const int HttpLogFormMaxLen = 900;
        private static readonly char[] ManifestLineSeparators = { '\r', '\n' };
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly JsonSerializerOptions SearchJsonSerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        private readonly HttpClient _httpClient;
        private readonly string _token;
        private readonly ILogger? _logger;
        private readonly Func<bool> _isHttpLogEnabled;
        private readonly Action<string, long>? _metricSink;
        private readonly bool _enableRunCache;
        private readonly CancellationToken _cacheCancellationToken;
        private readonly ConcurrentDictionary<SearchCacheKey, Lazy<Task<List<KodikSearchResult>>>> _searchCache = new();
        private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _postPathCache =
            new(StringComparer.OrdinalIgnoreCase);
        private int _cryptStep = UnknownCryptStep;

        public KodikClient(HttpClient httpClient, string token, ILogger? logger = null)
            : this(httpClient, token, logger, CreateDefaultHttpLogSwitch(), null, false, CancellationToken.None)
        {
        }

        public KodikClient(HttpClient httpClient, string token, ILogger? logger, Func<bool> isHttpLogEnabled)
            : this(httpClient, token, logger, isHttpLogEnabled, null, false, CancellationToken.None)
        {
        }

        internal KodikClient(
            HttpClient httpClient,
            string token,
            Action<string, long> metricSink,
            CancellationToken cacheCancellationToken = default,
            ILogger? logger = null)
            : this(
                httpClient,
                token,
                logger,
                CreateDefaultHttpLogSwitch(),
                metricSink,
                true,
                cacheCancellationToken)
        {
        }

        private KodikClient(
            HttpClient httpClient,
            string token,
            ILogger? logger,
            Func<bool> isHttpLogEnabled,
            Action<string, long>? metricSink,
            bool enableRunCache,
            CancellationToken cacheCancellationToken)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _token = token ?? throw new ArgumentNullException(nameof(token));
            _logger = logger;
            _isHttpLogEnabled = isHttpLogEnabled ?? throw new ArgumentNullException(nameof(isHttpLogEnabled));
            _metricSink = metricSink;
            _enableRunCache = enableRunCache;
            _cacheCancellationToken = cacheCancellationToken;
        }

        private static Func<bool> CreateDefaultHttpLogSwitch()
        {
            // Read dynamically so changes in plugin settings apply without restart.
            return () =>
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

            var searchInfo = await TryGetAnimeInfoFromSearchAsync(id, idType, cancellationToken).ConfigureAwait(false);
            return searchInfo ?? await GetAnimeInfoFromPlayerHtmlAsync(id, idType, cancellationToken).ConfigureAwait(false);
        }

        private async Task<KodikAnimeInfo?> TryGetAnimeInfoFromSearchAsync(
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken)
        {
            try
            {
                var results = await SearchAsync(id, idType, cancellationToken).ConfigureAwait(false);
                if (results.Count == 0)
                {
                    throw new KodikNoResultsException($"Kodik search returned no results for {idType} id {id}.");
                }

                return BuildAnimeInfoFromSearchResults(results);
            }
            catch (KodikNoResultsException)
            {
                // Zero search hits are common for announcements and freshly added titles.
                // Keep the fallback, but do not spam warning-level stack traces.
                LogKodikSearchNoResultsFallback(idType, id);
            }
            catch (Exception ex) when (IsSearchFallbackException(ex))
            {
                // Fallback to old HTML parsing if search fails.
                _logger?.LogWarning(ex, "Kodik search failed, falling back to player HTML parsing. idType={IdType} id={Id}", idType, id);
            }

            return null;
        }

        private KodikAnimeInfo BuildAnimeInfoFromSearchResults(IReadOnlyList<KodikSearchResult> results)
        {
            var maxEpisode = 0;

            // builder to accumulate per-translation info without mutating init-only model
            var translationsMap = new Dictionary<string, (string Id, string Name, string Type, int MaxEp, HashSet<int> Episodes)>(StringComparer.Ordinal);

            foreach (var result in results)
            {
                if (!TryCreateSearchTranslationEntry(result, out var key, out var entry, out var entryMaxEpisode))
                {
                    maxEpisode = Math.Max(maxEpisode, entryMaxEpisode);
                    continue;
                }

                maxEpisode = Math.Max(maxEpisode, entryMaxEpisode);
                MergeSearchTranslationEntry(translationsMap, key, entry);
            }

            var translations = BuildSearchTranslations(translationsMap);
            AddFallbackTranslationIfEmpty(translations, maxEpisode);

            _logger?.LogInformation(
                "Kodik.GetAnimeInfoAsync done (search). seriesCount={SeriesCount} translations={TrCount}",
                maxEpisode,
                translations.Count);

            return new KodikAnimeInfo(maxEpisode, translations);
        }

        private static bool TryCreateSearchTranslationEntry(
            KodikSearchResult result,
            out string key,
            out (string Id, string Name, string Type, int MaxEp, HashSet<int> Episodes) entry,
            out int maxEpisode)
        {
            var explicitEpisodes = ExtractAvailableEpisodes(result);
            var epCount = result.EpisodesCount.GetValueOrDefault(0);
            var lastEp = result.LastEpisode.GetValueOrDefault(0);

            maxEpisode = explicitEpisodes.Count > 0
                ? explicitEpisodes.Max()
                : Math.Max(epCount, lastEp);

            key = string.Empty;
            entry = (string.Empty, string.Empty, string.Empty, maxEpisode, explicitEpisodes);

            var translation = result.Translation;
            if (translation == null)
            {
                return false;
            }

            var translationId = translation.Id.HasValue
                ? translation.Id.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;
            var translationName = (translation.Title ?? string.Empty).Trim();
            var translationType = (translation.Type ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(translationId) && string.IsNullOrWhiteSpace(translationName))
            {
                return false;
            }

            // prefer id as key, fallback to name-based key to avoid collisions
            key = !string.IsNullOrWhiteSpace(translationId) ? translationId : "name:" + translationName;
            entry = (translationId, translationName, translationType, maxEpisode, explicitEpisodes);
            return true;
        }

        private static void MergeSearchTranslationEntry(
            Dictionary<string, (string Id, string Name, string Type, int MaxEp, HashSet<int> Episodes)> translationsMap,
            string key,
            (string Id, string Name, string Type, int MaxEp, HashSet<int> Episodes) entry)
        {
            if (!translationsMap.TryGetValue(key, out var current))
            {
                translationsMap[key] = entry;
                return;
            }

            var mergedId = string.IsNullOrWhiteSpace(current.Id) ? entry.Id : current.Id;
            var mergedName = string.IsNullOrWhiteSpace(current.Name) ? entry.Name : current.Name;
            var mergedType = string.IsNullOrWhiteSpace(current.Type) ? entry.Type : current.Type;
            var mergedMax = Math.Max(current.MaxEp, entry.MaxEp);
            current.Episodes.UnionWith(entry.Episodes);

            translationsMap[key] = (mergedId, mergedName, mergedType, mergedMax, current.Episodes);
        }

        private static List<KodikTranslation> BuildSearchTranslations(
            Dictionary<string, (string Id, string Name, string Type, int MaxEp, HashSet<int> Episodes)> translationsMap)
        {
            return translationsMap.Values
                .Select(x => new KodikTranslation
                {
                    Id = x.Id ?? string.Empty,
                    Name = x.Name ?? string.Empty,
                    Type = x.Type ?? string.Empty,
                    MaxEpisode = x.MaxEp,
                    AvailableEpisodes = x.Episodes.Count > 0
                        ? x.Episodes.OrderBy(ep => ep).ToArray()
                        : Array.Empty<int>()
                })
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) || !string.IsNullOrWhiteSpace(t.Name))
                .ToList();
        }

        private async Task<KodikAnimeInfo> GetAnimeInfoFromPlayerHtmlAsync(
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken)
        {
            var playerUrl = await GetPlayerPageUrlAsync(id, idType, cancellationToken).ConfigureAwait(false);
            var html = await GetStringAsync(playerUrl, cancellationToken).ConfigureAwait(false);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var isSerial = IsSerialUrl(playerUrl);
            var seriesCount = isSerial ? CountHtmlSeriesEpisodes(doc) : 0;
            var htmlTranslations = GetHtmlTranslations(doc, isSerial);
            AddFallbackTranslationIfEmpty(htmlTranslations, seriesCount);

            _logger?.LogInformation(
                "Kodik.GetAnimeInfoAsync done (html). isSerial={IsSerial} seriesCount={SeriesCount} translations={TrCount}",
                isSerial,
                seriesCount,
                htmlTranslations.Count);

            return new KodikAnimeInfo(seriesCount, htmlTranslations);
        }

        private static int CountHtmlSeriesEpisodes(HtmlDocument doc)
        {
            var seriesSelect = doc.DocumentNode
                .SelectSingleNode("//div[contains(@class,'serial-series-box')]//select");
            var episodeOptions = seriesSelect?.SelectNodes(".//option");
            return episodeOptions?.Count ?? 0;
        }

        private static List<KodikTranslation> GetHtmlTranslations(HtmlDocument doc, bool isSerial)
        {
            var selectXPath = isSerial
                ? "//div[contains(@class,'serial-translations-box')]//select"
                : "//div[contains(@class,'movie-translations-box')]//select";

            return ParseTranslations(doc, selectXPath);
        }

        private static void AddFallbackTranslationIfEmpty(List<KodikTranslation> translations, int maxEpisode)
        {
            if (translations.Count > 0)
            {
                return;
            }

            translations.Add(new KodikTranslation
            {
                Id = "0",
                Name = "Unknown",
                Type = "unknown",
                MaxEpisode = maxEpisode
            });
        }

        private void LogKodikSearchNoResultsFallback(KodikIdType idType, string id)
        {
            _logger?.LogInformation(
                "Kodik search returned no results, falling back to player HTML parsing. idType={IdType} id={Id}",
                idType,
                id);
        }

        private static bool IsSearchFallbackException(Exception ex)
        {
            return ex is KodikException or HttpRequestException or TaskCanceledException or JsonException;
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

            _logger?.LogDebug(
                "Kodik.GetEpisodeLinkAsync: idType={IdType} id={Id} episode={Episode} tr={TrId}",
                idType,
                id,
                episode,
                translationId);

            var playerUrls = await GetEpisodePlayerPageUrlCandidatesAsync(
                    id,
                    idType,
                    episode,
                    translationId,
                    cancellationToken)
                .ConfigureAwait(false);

            Exception? lastCandidateError = null;

            foreach (var playerUrl in playerUrls)
            {
                try
                {
                    return await ResolveLinkFromPlayerUrlAsync(playerUrl, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRetriablePlayerCandidateException(ex, cancellationToken))
                {
                    lastCandidateError = ex;

                    _logger?.LogWarning(
                        ex,
                        "Kodik player candidate failed. type={Type} id={Id} episode={Episode} tr={TrId} url={Url}",
                        idType,
                        id,
                        episode,
                        translationId,
                        SanitizeUrl(playerUrl));
                }
            }

            throw lastCandidateError != null
                ? new KodikServiceException("All Kodik player link candidates failed.", lastCandidateError)
                : new KodikServiceException("All Kodik player link candidates failed.");
        }

        public async Task<KodikLinkInfo> GetPlayerLinkAsync(
            string playerUrl,
            int episode,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(playerUrl))
            {
                throw new ArgumentException("Player url must be non empty.", nameof(playerUrl));
            }

            var absolute = EnsureAbsoluteKodikUrl(playerUrl);
            var requestUrl = episode > 0 && IsEpisodeContainerUrl(absolute)
                ? BuildSerialEpisodeUrlFromPlayerUrl(absolute, episode)
                : absolute;

            return await ResolveLinkFromPlayerUrlAsync(requestUrl, cancellationToken).ConfigureAwait(false);
        }

        private async Task<KodikLinkInfo> ResolveLinkFromPlayerUrlAsync(
            string playerUrl,
            CancellationToken cancellationToken)
        {
            var playerPage = await GetPlayerPageDocumentAsync(playerUrl, cancellationToken).ConfigureAwait(false);
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

            var directUrl = linkData.Url.Replace(HttpsSchemePrefix, string.Empty, StringComparison.OrdinalIgnoreCase);
            var lastSlash = directUrl.LastIndexOf('/');
            if (lastSlash < 0)
            {
                _logger?.LogWarning("Direct url format not recognized. url={Url}", Short(linkData.Url, 300));
                throw new KodikUnexpectedException("Direct url format is not recognized.");
            }

            var basePath = directUrl.Substring(0, lastSlash + 1);

            _logger?.LogDebug(
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
            ArgumentNullException.ThrowIfNull(link);

            var q = Math.Min(quality, link.MaxQuality);
            if (q <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be positive.");
            }

            return HttpsSchemePrefix + link.BasePath + q + ".mp4";
        }

        /// <summary>
        /// Builds HLS manifest url.
        /// </summary>
        public static string BuildHlsUrl(KodikLinkInfo link, int quality)
        {
            ArgumentNullException.ThrowIfNull(link);

            var q = Math.Min(quality, link.MaxQuality);
            if (q <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(quality), "Quality must be positive.");
            }

            return HttpsSchemePrefix + link.BasePath + q + ".mp4:hls:manifest.m3u8";
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
            ArgumentNullException.ThrowIfNull(link);

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

            var candidates = await GetEpisodePlayerPageUrlCandidatesAsync(
                    id,
                    idType,
                    episode,
                    translationId,
                    cancellationToken)
                .ConfigureAwait(false);

            return await GetPlayerPageDocumentAsync(candidates[0], cancellationToken).ConfigureAwait(false);
        }

        private async Task<List<string>> GetEpisodePlayerPageUrlCandidatesAsync(
            string id,
            KodikIdType idType,
            int episode,
            string translationId,
            CancellationToken cancellationToken)
        {
            if (translationId == "0")
            {
                return await GetDefaultPlayerPageUrlCandidatesAsync(id, idType, episode, cancellationToken).ConfigureAwait(false);
            }

            var results = await SearchAsync(id, idType, cancellationToken).ConfigureAwait(false);
            if (results.Count == 0)
            {
                throw new KodikNoResultsException($"Kodik search returned no results for {idType} id {id}.");
            }

            var hit = FindSearchResultByTranslationId(results, translationId) ?? results[0];
            if (string.IsNullOrWhiteSpace(hit.Link))
            {
                throw new KodikNoResultsException($"Kodik search did not return usable player link for {idType} id {id}.");
            }

            return BuildEpisodePlayerPageUrlCandidates(hit, episode);
        }

        private async Task<List<string>> GetDefaultPlayerPageUrlCandidatesAsync(
            string id,
            KodikIdType idType,
            int episode,
            CancellationToken cancellationToken)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playerUrl = await GetPlayerPageUrlAsync(id, idType, cancellationToken).ConfigureAwait(false);
            AddEpisodePlayerPageUrlCandidate(result, seen, playerUrl, episode);
            return result;
        }

        private static KodikSearchResult? FindSearchResultByTranslationId(
            IReadOnlyList<KodikSearchResult> results,
            string translationId)
        {
            return results.FirstOrDefault(result => SearchResultHasTranslationId(result, translationId));
        }

        private static bool SearchResultHasTranslationId(KodikSearchResult result, string translationId)
        {
            var translation = result.Translation;
            var candidateId = translation?.Id.HasValue == true
                ? translation.Id.Value.ToString(CultureInfo.InvariantCulture)
                : string.Empty;

            return string.Equals(candidateId, translationId, StringComparison.Ordinal);
        }

        private static List<string> BuildEpisodePlayerPageUrlCandidates(KodikSearchResult hit, int episode)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (TryGetEpisodePlayerLink(hit, episode, out var episodePlayerLink))
            {
                AddEpisodePlayerPageUrlCandidate(result, seen, episodePlayerLink, episode);
            }

            AddEpisodePlayerPageUrlCandidate(result, seen, hit.Link, episode);

            return result;
        }

        private static void AddEpisodePlayerPageUrlCandidate(
            List<string> result,
            HashSet<string> seen,
            string? raw,
            int episode)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return;
            }

            var absolute = EnsureAbsoluteKodikUrl(raw);
            var requestUrl = episode > 0 && IsEpisodeContainerUrl(absolute)
                ? BuildSerialEpisodeUrlFromPlayerUrl(absolute, episode)
                : absolute;

            if (seen.Add(requestUrl))
            {
                result.Add(requestUrl);
            }
        }

        private async Task<(string RequestUrl, string Html, HtmlDocument Document)> GetPlayerPageDocumentAsync(
            string requestUrl,
            CancellationToken cancellationToken)
        {
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
            var requestUrl = BuildPlayerLookupUrl(id, idType);

            LogHttpRequest("GET", requestUrl, null);

            var response = await _httpClient.GetAsync(requestUrl, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            LogHttpResponse("GET", requestUrl, response, content);

            if (!response.IsSuccessStatusCode)
            {
                ThrowForPlayerLookupHttpError(response, content, requestUrl);
            }

            if (TryResolvePlayerLookupHtml(response, content, requestUrl, out var resolvedHtmlUrl))
            {
                return resolvedHtmlUrl;
            }

            return ResolvePlayerLookupLinkFromJson(content, idType, id);
        }

        private string BuildPlayerLookupUrl(string id, KodikIdType idType)
        {
            var builder = new StringBuilder(KodikPlayerBaseUrl + "/find-player?");
            builder.Append(GetPlayerLookupIdParameterName(idType));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(id));

            if (!string.IsNullOrWhiteSpace(_token))
            {
                builder.Append("&token=");
                builder.Append(Uri.EscapeDataString(_token));
            }

            return builder.ToString();
        }

        private static string GetPlayerLookupIdParameterName(KodikIdType idType)
        {
            return idType switch
            {
                KodikIdType.Shikimori => "shikimoriID",
                KodikIdType.Kinopoisk => "kinopoiskID",
                KodikIdType.Imdb => "imdbID",
                _ => throw new ArgumentOutOfRangeException(nameof(idType), idType, "Unknown id type.")
            };
        }

        private void ThrowForPlayerLookupHttpError(
            HttpResponseMessage response,
            string content,
            string requestUrl)
        {
            // IMPORTANT: Kodik sometimes returns 500 but provides {"error":"..."} in body.
            if (TryExtractKodikError(content, out var apiError))
            {
                _logger?.LogWarning(
                    "Kodik get-player returned error (HTTP {Status}). url={Url} error={Error}",
                    (int)response.StatusCode,
                    SanitizeUrl(requestUrl),
                    apiError);

                ThrowForKodikApiError(apiError, "Kodik get-player returned error");
            }

            _logger?.LogWarning(
                "Kodik get-player HTTP failed. status={Status} url={Url} bodySnippet={Body}",
                (int)response.StatusCode,
                SanitizeUrl(requestUrl),
                Short(content, 350));

            throw new KodikServiceException(
                $"Unexpected status code {response.StatusCode} while calling get-player.");
        }

        private bool TryResolvePlayerLookupHtml(
            HttpResponseMessage response,
            string content,
            string requestUrl,
            out string resolvedHtmlUrl)
        {
            resolvedHtmlUrl = string.Empty;
            if (!LooksLikeHtmlResponse(response, content))
            {
                return false;
            }

            var finalUrl = response.RequestMessage?.RequestUri?.ToString();
            resolvedHtmlUrl = !string.IsNullOrWhiteSpace(finalUrl) ? finalUrl : requestUrl;

            if (!string.Equals(resolvedHtmlUrl, requestUrl, StringComparison.OrdinalIgnoreCase) ||
                LooksLikePlayerPageHtml(content))
            {
                _logger?.LogInformation(
                    "Kodik get-player returned HTML page. requestUrl={RequestUrl} finalUrl={FinalUrl}",
                    SanitizeUrl(requestUrl),
                    SanitizeUrl(resolvedHtmlUrl));

                return true;
            }

            _logger?.LogDebug(
                "Kodik get-player returned HTML that does not look like a player page. url={Url} bodySnippet={Body}",
                SanitizeUrl(requestUrl),
                Short(content, 350));

            throw new KodikUnexpectedException("Kodik get-player returned HTML instead of JSON.");
        }

        private string ResolvePlayerLookupLinkFromJson(string content, KodikIdType idType, string id)
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                _logger?.LogWarning("Kodik get-player returned error: {Error}", error);
                ThrowForKodikApiError(error, "Kodik get-player returned error");
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

            var resolved = ResolvePlayerLookupLink(linkProp);

            _logger?.LogDebug("Kodik get-player resolved link. url={Url}", SanitizeUrl(resolved));
            return resolved;
        }

        private static string ResolvePlayerLookupLink(string link)
        {
            if (link.StartsWith("//", StringComparison.Ordinal))
            {
                return HttpsSchemePrefix + link;
            }

            if (link.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return link;
            }

            return "https://" + link.TrimStart('/');
        }

        private static void ThrowForKodikApiError(string? error, string messagePrefix)
        {
            if (IsTokenError(error))
            {
                throw new KodikTokenException(TokenInvalidMessage);
            }

            throw new KodikServiceException($"{messagePrefix}: {error}");
        }

        private static bool LooksLikeHtmlResponse(HttpResponseMessage response, string content)
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var trimmed = (content ?? string.Empty).TrimStart();
            return trimmed.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith('<');
        }

        private static bool LooksLikePlayerPageHtml(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return false;
            }

            return html.Contains("serial-series-box", StringComparison.OrdinalIgnoreCase) ||
                   html.Contains("serial-translations-box", StringComparison.OrdinalIgnoreCase) ||
                   html.Contains("movie-translations-box", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSerialUrl(string url) => IsEpisodeContainerUrl(url);

        private static bool IsEpisodeContainerUrl(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return false;
            }

            var path = uri.AbsolutePath.TrimStart('/');
            if (path.StartsWith("serial/", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("season/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

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
                var valueId = option.GetAttributeValue("value", string.Empty);
                var dataId = option.GetAttributeValue("data-id", string.Empty);

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
                var q = uri.Query.StartsWith('?') ? uri.Query.Substring(1) : uri.Query;
                foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0)
                    {
                        continue;
                    }

                    var k = Uri.UnescapeDataString(part.AsSpan(0, eq));
                    var v = Uri.UnescapeDataString(part.AsSpan(eq + 1));
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

        private static bool TryGetEpisodePlayerLink(KodikSearchResult result, int episode, out string playerUrl)
        {
            playerUrl = string.Empty;

            if (episode <= 0 || result.Seasons == null || result.Seasons.Count == 0)
            {
                return false;
            }

            var episodeKey = episode.ToString(CultureInfo.InvariantCulture);

            if (result.LastSeason.HasValue && result.LastSeason.Value > 0)
            {
                var lastSeasonKey = result.LastSeason.Value.ToString(CultureInfo.InvariantCulture);
                if (TryGetEpisodePlayerLink(result.Seasons, lastSeasonKey, episodeKey, out playerUrl))
                {
                    return true;
                }
            }

            foreach (var seasonKey in result.Seasons.Keys.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (TryGetEpisodePlayerLink(result.Seasons, seasonKey, episodeKey, out playerUrl))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetEpisodePlayerLink(
            IReadOnlyDictionary<string, KodikSearchSeason> seasons,
            string seasonKey,
            string episodeKey,
            out string playerUrl)
        {
            playerUrl = string.Empty;

            if (!seasons.TryGetValue(seasonKey, out var season) ||
                season?.Episodes == null ||
                !season.Episodes.TryGetValue(episodeKey, out var episode) ||
                string.IsNullOrWhiteSpace(episode?.Link))
            {
                return false;
            }

            playerUrl = EnsureAbsoluteKodikUrl(episode.Link);
            return !string.IsNullOrWhiteSpace(playerUrl);
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

            foreach (var rawLine in manifest.Split(ManifestLineSeparators, StringSplitOptions.RemoveEmptyEntries))
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

            foreach (var rawLine in manifest.Split(ManifestLineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (!line.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
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

            if (first == '{')
            {
                return ReadBalancedBraces(html, pos);
            }

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

            var intro = ranges[0];
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
                RegexOptions.CultureInvariant,
                RegexTimeout);

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

        private static List<KodikSkipRange> ParseSkipRanges(string rawRanges)
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
            var payload = BuildVideoLinksPayload(videoType, videoHash, videoId, urlParams);
            try
            {
                var postUrl = KodikPlayerBaseUrl + postPath.Path;
                var jsonString = await PostVideoLinksAsync(postUrl, payload, cancellationToken).ConfigureAwait(false);
                var (dataUrl, maxQuality) = ResolveVideoLinkData(jsonString);
                var finalUrl = NormalizeVideoDataUrl(dataUrl);

                EnsureVideoLinkQuality(maxQuality, jsonString);

                _logger?.LogDebug("Video link selected. maxQ={MaxQ} urlSnippet={Url}", maxQuality, Short(finalUrl, 250));
                return (finalUrl, maxQuality);
            }
            catch (Exception ex) when (ShouldInvalidatePostPath(ex, cancellationToken))
            {
                if (_postPathCache.TryRemove(
                        new KeyValuePair<string, Lazy<Task<string>>>(postPath.ScriptUrl, postPath.Registration)))
                {
                    AddMetric("kodik.script.cache_evictions");
                }

                throw;
            }
        }

        private static Dictionary<string, string> BuildVideoLinksPayload(
            string videoType,
            string videoHash,
            string videoId,
            Dictionary<string, string> urlParams)
        {
            urlParams.TryGetValue("d", out var d);
            urlParams.TryGetValue("d_sign", out var dSign);
            urlParams.TryGetValue("pd", out var pd);
            urlParams.TryGetValue("pd_sign", out var pdSign);
            urlParams.TryGetValue("ref_sign", out var refSign);

            return new Dictionary<string, string>
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
        }

        private async Task<string> PostVideoLinksAsync(
            string postUrl,
            Dictionary<string, string> payload,
            CancellationToken cancellationToken)
        {
            LogHttpRequest("POST", postUrl, payload);

            using var content = new FormUrlEncodedContent(payload);
            var response = await _httpClient
                .PostAsync(postUrl, content, cancellationToken)
                .ConfigureAwait(false);

            var jsonString = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            LogHttpResponse("POST", postUrl, response, jsonString);

            if (!response.IsSuccessStatusCode)
            {
                ThrowForVideoLinksHttpError(response, jsonString, postUrl);
            }

            return jsonString;
        }

        private void ThrowForVideoLinksHttpError(
            HttpResponseMessage response,
            string jsonString,
            string postUrl)
        {
            if (TryExtractKodikError(jsonString, out var apiError))
            {
                _logger?.LogWarning(
                    "Video links returned error (HTTP {Status}). url={Url} error={Error}",
                    (int)response.StatusCode,
                    SanitizeUrl(postUrl),
                    apiError);

                ThrowForKodikApiError(apiError, "Video links request returned error");
            }

            _logger?.LogWarning(
                "Video links HTTP failed. status={Status} url={Url} bodySnippet={Body}",
                (int)response.StatusCode,
                SanitizeUrl(postUrl),
                Short(jsonString, 450));

            throw new KodikServiceException(
                $"Unexpected status code {response.StatusCode} while requesting video links.");
        }

        private (string DataUrl, int MaxQuality) ResolveVideoLinkData(string jsonString)
        {
            using var document = JsonDocument.Parse(jsonString);
            var root = document.RootElement;

            if (root.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                _logger?.LogWarning("Video links returned error: {Error}", error);
                ThrowForKodikApiError(error, "Video links request returned error");
            }

            var linksElement = root.GetProperty("links");
            var (dataUrl, maxQuality) = ExtractVideoLinkData(linksElement);
            if (dataUrl == null)
            {
                _logger?.LogWarning("Base video url not found in links payload. payloadSnippet={Payload}", Short(jsonString, 800));
                throw new KodikUnexpectedException("Base video url not found in links payload.");
            }

            return (dataUrl, maxQuality);
        }

        private static (string? DataUrl, int MaxQuality) ExtractVideoLinkData(JsonElement linksElement)
        {
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

                if (dataUrl == null && TryGetFirstVideoLinkSource(property.Value, out var source))
                {
                    dataUrl = source;
                }
            }

            return (dataUrl, maxQuality);
        }

        private static bool TryGetFirstVideoLinkSource(JsonElement items, out string source)
        {
            source = string.Empty;

            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
            {
                return false;
            }

            if (!items[0].TryGetProperty("src", out var srcElement) ||
                srcElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            source = srcElement.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(source);
        }

        private string NormalizeVideoDataUrl(string dataUrl)
        {
            return dataUrl.Contains("mp4:hls:manifest", StringComparison.Ordinal)
                ? dataUrl
                : ConvertEncodedUrl(dataUrl);
        }

        private void EnsureVideoLinkQuality(int maxQuality, string jsonString)
        {
            if (maxQuality == 0)
            {
                _logger?.LogWarning("Max quality could not be determined. payloadSnippet={Payload}", Short(jsonString, 800));
                throw new KodikUnexpectedException("Max quality could not be determined from links payload.");
            }
        }

        private async Task<CachedPostPath> GetPostLinkFromScriptsAsync(
            IReadOnlyList<string> scriptUrls,
            CancellationToken cancellationToken)
        {
            Exception? last = null;
            var failedCachedUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var normalizedUrls = scriptUrls
                .Where(raw => !string.IsNullOrWhiteSpace(raw))
                .Select(EnsureAbsoluteKodikResourceUrl)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            // Prefer an already proven candidate even when it appears after an unrelated script tag.
            foreach (var url in normalizedUrls)
            {
                if (!_enableRunCache || !_postPathCache.TryGetValue(url, out var cached))
                {
                    continue;
                }

                AddMetric("kodik.script.cache_hits");
                try
                {
                    var path = await cached.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return new CachedPostPath(url, path, cached);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                           ex is KodikException or HttpRequestException or TaskCanceledException)
                {
                    last = ex;
                    failedCachedUrls.Add(url);
                    if (_postPathCache.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(url, cached)))
                    {
                        AddMetric("kodik.script.cache_evictions");
                    }
                }
            }

            foreach (var url in normalizedUrls)
            {
                if (failedCachedUrls.Contains(url))
                {
                    continue;
                }

                try
                {
                    if (!_enableRunCache)
                    {
                        var uncachedPath = await GetPostLinkAsync(url, cancellationToken).ConfigureAwait(false);
                        return new CachedPostPath(
                            url,
                            uncachedPath,
                            new Lazy<Task<string>>(() => Task.FromResult(uncachedPath)));
                    }

                    var candidate = new Lazy<Task<string>>(
                        () => GetPostLinkAsync(url, _cacheCancellationToken),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    var registration = _postPathCache.GetOrAdd(url, candidate);
                    AddMetric(
                        ReferenceEquals(registration, candidate)
                            ? "kodik.script.cache_misses"
                            : "kodik.script.cache_hits");

                    var path = await registration.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
                    return new CachedPostPath(url, path, registration);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                           ex is KodikException or HttpRequestException or TaskCanceledException)
                {
                    last = ex;
                    if (_enableRunCache && _postPathCache.TryGetValue(url, out var failed) &&
                        failed.IsValueCreated && (failed.Value.IsFaulted || failed.Value.IsCanceled))
                    {
                        if (_postPathCache.TryRemove(new KeyValuePair<string, Lazy<Task<string>>>(url, failed)))
                        {
                            AddMetric("kodik.script.cache_evictions");
                        }
                    }
                }
            }

            if (last != null)
            {
                throw new KodikUnexpectedException("Failed to extract post link from any script src candidate.", last);
            }

            throw new KodikUnexpectedException("Failed to extract post link from any script src candidate.");
        }

        private async Task<string> GetPostLinkAsync(string scriptUrl, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(scriptUrl))
            {
                throw new ArgumentNullException(nameof(scriptUrl));
            }

            var url = EnsureAbsoluteKodikResourceUrl(scriptUrl);

            LogHttpRequest("GET", url, null);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var scriptBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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

            var cachedCryptStep = Volatile.Read(ref _cryptStep);
            if (cachedCryptStep != UnknownCryptStep)
            {
                var attempt = TryDecodeWithRot(encoded, cachedCryptStep);
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

                Volatile.Write(ref _cryptStep, rot);
                return attempt;
            }

            throw new KodikDecryptionException("Failed to decode Kodik video url.");
        }

        private static string? TryDecodeWithRot(string encoded, int rot)
        {
            var shifted = ShiftAlphabet(encoded, rot);
            var padded = PadBase64(shifted);

            try
            {
                var bytes = Convert.FromBase64String(padded);
                var decoded = Encoding.UTF8.GetString(bytes);
                return decoded.Contains("mp4:hls:manifest", StringComparison.Ordinal) ? decoded : null;
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

            foreach (var text in scripts.Select(script => script.InnerText))
            {
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
                var src = n.GetAttributeValue("src", string.Empty);
                if (TryNormalizeKodikResourceUrl(src, out var absolute) && seen.Add(absolute))
                {
                    list.Add(absolute);
                }
            }

            return list;
        }

        private static bool TryNormalizeKodikResourceUrl(string? raw, out string absolute)
        {
            absolute = string.Empty;

            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            absolute = EnsureAbsoluteKodikResourceUrl(raw);
            return true;
        }

        private static string EnsureAbsoluteKodikResourceUrl(string raw)
        {
            if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return raw;
            }

            if (raw.StartsWith("//", StringComparison.Ordinal))
            {
                return HttpsSchemePrefix + raw;
            }

            if (raw.StartsWith('/'))
            {
                return KodikPlayerBaseUrl + raw;
            }

            return KodikPlayerBaseUrl + '/' + raw;
        }

        private async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
        {
            LogHttpRequest("GET", url, null);

            var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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

        private void LogHttpRequest(string method, string url, Dictionary<string, string>? form)
        {
            AddMetric("kodik.http_requests");

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

        private void AddMetric(string key, long delta = 1)
        {
            _metricSink?.Invoke(key, delta);
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
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                RegexTimeout);
        }

        private async Task<List<KodikSearchResult>> SearchAsync(
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken)
        {
            if (!_enableRunCache)
            {
                return await FetchSearchAsync(id, idType, cancellationToken).ConfigureAwait(false);
            }

            var key = new SearchCacheKey(idType, id.Trim());
            Lazy<Task<List<KodikSearchResult>>> registration;
            if (_searchCache.TryGetValue(key, out var cached))
            {
                registration = cached;
                AddMetric("kodik.search.cache_hits");
            }
            else
            {
                var candidate = new Lazy<Task<List<KodikSearchResult>>>(
                    () => FetchSearchAsync(key.Id, key.IdType, _cacheCancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication);
                registration = _searchCache.GetOrAdd(key, candidate);
                AddMetric(
                    ReferenceEquals(registration, candidate)
                        ? "kodik.search.cache_misses"
                        : "kodik.search.cache_hits");
            }

            try
            {
                return await registration.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                if (registration.IsValueCreated &&
                    (registration.Value.IsFaulted || registration.Value.IsCanceled))
                {
                    if (_searchCache.TryRemove(
                            new KeyValuePair<SearchCacheKey, Lazy<Task<List<KodikSearchResult>>>>(key, registration)))
                    {
                        AddMetric("kodik.search.cache_evictions");
                    }
                }

                throw;
            }
        }

        private static bool ShouldInvalidatePostPath(Exception ex, CancellationToken cancellationToken)
        {
            return !cancellationToken.IsCancellationRequested &&
                   ex is not KodikTokenException &&
                   ex is KodikException or HttpRequestException or TaskCanceledException or JsonException;
        }

        private async Task<List<KodikSearchResult>> FetchSearchAsync(
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken)
        {
            var payload = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["token"] = _token,
                ["limit"] = "100",
                ["all"] = "true",
                ["with_episodes"] = "true",
                ["with_episodes_data"] = "true"
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
            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

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
                        throw new KodikTokenException(TokenInvalidMessage);
                    }

                    throw new KodikServiceException($"Kodik search returned error: {apiError}");
                }

                _logger?.LogWarning("Kodik search HTTP failed. status={Status} bodySnippet={Body}", (int)resp.StatusCode, Short(json, 350));
                throw new KodikServiceException($"Kodik search failed: {resp.StatusCode}");
            }

            var parsed = JsonSerializer.Deserialize<KodikSearchResponse>(json, SearchJsonSerializerOptions);

            if (!string.IsNullOrWhiteSpace(parsed?.Error))
            {
                _logger?.LogWarning("Kodik search returned error: {Error}", parsed!.Error);

                if (IsTokenError(parsed.Error))
                {
                    throw new KodikTokenException(TokenInvalidMessage);
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
                return HttpsSchemePrefix + s;
            }

            if (s.StartsWith('/'))
            {
                return KodikPlayerBaseUrl + s;
            }

            return "https://" + s.TrimStart('/');
        }

        private static HashSet<int> ExtractAvailableEpisodes(KodikSearchResult? result)
        {
            var episodes = new HashSet<int>();
            if (result?.Seasons == null || result.Seasons.Count == 0)
            {
                return episodes;
            }

            foreach (var season in GetSeasonsToInspect(result))
            {
                AddAvailableEpisodes(episodes, season);
            }

            return episodes;
        }

        private static IEnumerable<KodikSearchSeason> GetSeasonsToInspect(KodikSearchResult result)
        {
            if (TryGetLastSeason(result, out var lastSeason))
            {
                return new[] { lastSeason };
            }

            return result.Seasons == null
                ? Array.Empty<KodikSearchSeason>()
                : result.Seasons.Values;
        }

        private static bool TryGetLastSeason(KodikSearchResult result, out KodikSearchSeason lastSeason)
        {
            lastSeason = null!;

            if (!result.LastSeason.HasValue || result.LastSeason.Value <= 0 || result.Seasons == null)
            {
                return false;
            }

            var lastSeasonKey = result.LastSeason.Value.ToString(CultureInfo.InvariantCulture);
            if (!result.Seasons.TryGetValue(lastSeasonKey, out var season) || season == null)
            {
                return false;
            }

            lastSeason = season;
            return true;
        }

        private static void AddAvailableEpisodes(HashSet<int> episodes, KodikSearchSeason? season)
        {
            if (season?.Episodes == null || season.Episodes.Count == 0)
            {
                return;
            }

            foreach (var episodeKey in season.Episodes.Keys)
            {
                if (TryParsePositiveEpisodeNumber(episodeKey, out var episodeNumber))
                {
                    episodes.Add(episodeNumber);
                }
            }
        }

        private static bool TryParsePositiveEpisodeNumber(string episodeKey, out int episodeNumber)
        {
            return int.TryParse(episodeKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out episodeNumber) &&
                   episodeNumber > 0;
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

        private static bool IsRetriablePlayerCandidateException(Exception ex, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            return (ex is KodikException && ex is not KodikTokenException) ||
                   ex is HttpRequestException ||
                   ex is TaskCanceledException;
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

        private readonly record struct SearchCacheKey(KodikIdType IdType, string Id);

        private readonly record struct CachedPostPath(
            string ScriptUrl,
            string Path,
            Lazy<Task<string>> Registration);

        private sealed class KodikSearchResult
        {
            [JsonPropertyName("link")]
            public string? Link { get; set; }

            [JsonPropertyName("last_season")]
            public int? LastSeason { get; set; }

            [JsonPropertyName("episodes_count")]
            public int? EpisodesCount { get; set; }

            [JsonPropertyName("last_episode")]
            public int? LastEpisode { get; set; }

            [JsonPropertyName("seasons")]
            public Dictionary<string, KodikSearchSeason>? Seasons { get; set; }

            [JsonPropertyName("translation")]
            public KodikSearchTranslation? Translation { get; set; }
        }

        private sealed class KodikSearchSeason
        {
            [JsonPropertyName("episodes")]
            public Dictionary<string, KodikSearchEpisode>? Episodes { get; set; }
        }

        private sealed class KodikSearchEpisode
        {
            [JsonPropertyName("link")]
            public string? Link { get; set; }
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
