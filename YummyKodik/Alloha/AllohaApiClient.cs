using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Yummy;

namespace YummyKodik.Alloha;

public sealed class AllohaApiClient
{
    private const string DefaultBaseUrl = "https://api.alloha.tv";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiToken;
    private readonly string _baseUrl;

    public AllohaApiClient(HttpClient httpClient, string apiToken, string? baseUrl = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiToken = (apiToken ?? string.Empty).Trim();
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl.Trim().TrimEnd('/');
    }

    public Task<IReadOnlyList<YummyVideoEntry>> GetCatalogEntriesByKpAsync(
        long kpId,
        CancellationToken cancellationToken = default)
    {
        if (kpId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kpId), "kpId must be positive.");
        }

        return GetCatalogEntriesAsync("kp", kpId.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }

    public Task<IReadOnlyList<YummyVideoEntry>> GetCatalogEntriesByImdbAsync(
        string imdbId,
        CancellationToken cancellationToken = default)
    {
        var value = (imdbId ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("imdbId must be non-empty.", nameof(imdbId));
        }

        return GetCatalogEntriesAsync("imdb", value, cancellationToken);
    }

    private async Task<IReadOnlyList<YummyVideoEntry>> GetCatalogEntriesAsync(
        string queryName,
        string queryValue,
        CancellationToken cancellationToken)
    {
        if (_apiToken.Length == 0)
        {
            throw new InvalidOperationException("Alloha API token is not configured.");
        }

        var requestUri =
            $"{_baseUrl}/?token={Uri.EscapeDataString(_apiToken)}&{queryName}={Uri.EscapeDataString(queryValue)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Alloha API request failed. status={(int)response.StatusCode} {queryName}={queryValue} body={TrimForLog(body)}");
        }

        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException($"Alloha API returned an empty response for {queryName}={queryValue}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Alloha API returned malformed JSON for {queryName}={queryValue}.", ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException($"Alloha API returned an empty JSON object for {queryName}={queryValue}.");
            }

            var status = GetStringProperty(root, "status");
            var errorInfo = GetStringProperty(root, "error_info");

            if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) ||
                !root.TryGetProperty("data", out var dataElement) ||
                dataElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new InvalidOperationException(
                    $"Alloha API returned {status} for {queryName}={queryValue}: {(errorInfo ?? "unknown error")}");
            }

            return BuildCatalogEntries(dataElement);
        }
    }

    internal static IReadOnlyList<YummyVideoEntry> BuildCatalogEntries(JsonElement data)
    {
        var entries = new List<YummyVideoEntry>();
        if (!data.TryGetProperty("seasons", out var seasonsElement))
        {
            return entries;
        }

        foreach (var seasonPair in EnumerateContainerEntries(seasonsElement)
                     .OrderBy(x => GetPositiveIntProperty(x.Value, "season"))
                     .ThenBy(x => ParsePositiveInt(x.Key)))
        {
            var seasonData = seasonPair.Value;
            if (seasonData.ValueKind != JsonValueKind.Object ||
                !seasonData.TryGetProperty("episodes", out var episodesElement))
            {
                continue;
            }

            var seasonNumber = GetPositiveIntProperty(seasonData, "season");
            if (seasonNumber <= 0)
            {
                seasonNumber = ParsePositiveInt(seasonPair.Key);
            }

            if (seasonNumber <= 0)
            {
                continue;
            }

            foreach (var episodePair in EnumerateContainerEntries(episodesElement)
                         .OrderBy(x => GetPositiveIntProperty(x.Value, "episode"))
                         .ThenBy(x => ParsePositiveInt(x.Key)))
            {
                var episodeData = episodePair.Value;
                if (episodeData.ValueKind != JsonValueKind.Object ||
                    !episodeData.TryGetProperty("translation", out var translationsElement))
                {
                    continue;
                }

                var episodeNumber = GetPositiveIntProperty(episodeData, "episode");
                if (episodeNumber <= 0)
                {
                    episodeNumber = ParsePositiveInt(episodePair.Key);
                }

                if (episodeNumber <= 0)
                {
                    continue;
                }

                foreach (var translationPair in EnumerateContainerEntries(translationsElement)
                             .OrderBy(x => GetPositiveIntProperty(x.Value, "id"))
                             .ThenBy(x => ParsePositiveInt(x.Key)))
                {
                    var translationData = translationPair.Value;
                    if (translationData.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var translationId = ParsePositiveInt(translationPair.Key);
                    if (translationId <= 0)
                    {
                        translationId = GetPositiveIntProperty(translationData, "id");
                    }
                    if (translationId <= 0)
                    {
                        translationId = GetPositiveIntProperty(translationData, "translation_id");
                    }

                    var translationName = GetStringProperty(translationData, "translation") ?? string.Empty;
                    var iframeUrl = NormalizeUrl(GetStringProperty(translationData, "iframe"));
                    if (!TryBuildAllohaSource(iframeUrl, translationId, seasonNumber, episodeNumber, out var source))
                    {
                        continue;
                    }

                    entries.Add(new YummyVideoEntry
                    {
                        EpisodeNumber = episodeNumber,
                        Provider = YummyVideoProviderKind.Alloha,
                        RawDubbing = translationName.Trim(),
                        DisplayVoiceName = YummyVideoCatalog.NormalizeVoiceName(translationName),
                        DurationSeconds = 0,
                        Skips = null,
                        IframeUrl = iframeUrl,
                        Alloha = source
                    });
                }
            }
        }

        return entries;
    }

    private static bool TryBuildAllohaSource(
        string iframeUrl,
        int fallbackTranslationId,
        int fallbackSeasonNumber,
        int fallbackEpisodeNumber,
        out YummyAllohaSource source)
    {
        source = new YummyAllohaSource();

        if (!Uri.TryCreate(iframeUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var query = ParseQuery(uri.Query);
        if (!query.TryGetValue("token_movie", out var movieToken) || string.IsNullOrWhiteSpace(movieToken))
        {
            return false;
        }

        if (!query.TryGetValue("token", out var requestToken) || string.IsNullOrWhiteSpace(requestToken))
        {
            return false;
        }

        var translationId = query.TryGetValue("translation", out var translationRaw) &&
                            int.TryParse(translationRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedTranslationId) &&
                            parsedTranslationId > 0
            ? parsedTranslationId
            : fallbackTranslationId;

        var seasonNumber = query.TryGetValue("season", out var seasonRaw) &&
                           int.TryParse(seasonRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeasonNumber) &&
                           parsedSeasonNumber > 0
            ? parsedSeasonNumber
            : fallbackSeasonNumber;

        var episodeNumber = query.TryGetValue("episode", out var episodeRaw) &&
                            int.TryParse(episodeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedEpisodeNumber) &&
                            parsedEpisodeNumber > 0
            ? parsedEpisodeNumber
            : fallbackEpisodeNumber;

        query.TryGetValue("hidden", out var hidden);

        if (translationId <= 0 || seasonNumber <= 0 || episodeNumber <= 0)
        {
            return false;
        }

        source = new YummyAllohaSource
        {
            MovieToken = movieToken.Trim(),
            RequestToken = requestToken.Trim(),
            TranslationId = translationId,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            Hidden = (hidden ?? string.Empty).Trim(),
            RefererUrl = iframeUrl
        };

        return true;
    }

    private static string NormalizeUrl(string? value)
    {
        var url = (value ?? string.Empty).Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return "https:" + url;
        }

        return url;
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var value = (query ?? string.Empty).TrimStart('?');
        if (value.Length == 0)
        {
            return result;
        }

        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = part.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part.Substring(0, separatorIndex));
            var parsedValue = Uri.UnescapeDataString(part[(separatorIndex + 1)..]);
            result[key] = parsedValue;
        }

        return result;
    }

    private static int ParsePositiveInt(string? value)
    {
        return int.TryParse((value ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : 0;
    }

    private static IEnumerable<KeyValuePair<string, JsonElement>> EnumerateContainerEntries(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                yield return new KeyValuePair<string, JsonElement>(property.Name, property.Value);
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                index++;
                yield return new KeyValuePair<string, JsonElement>(index.ToString(CultureInfo.InvariantCulture), item);
            }
        }
    }

    private static int GetPositiveIntProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt32(out var parsed) && parsed > 0 => parsed,
            JsonValueKind.String => ParsePositiveInt(value.GetString()),
            _ => 0
        };
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null
        };
    }

    private static string TrimForLog(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= 512 ? trimmed : trimmed[..512];
    }
}

public static class AllohaApiCatalogLoader
{
    private const string DefaultBaseUrl = "https://api.alloha.tv";
    private static readonly ConcurrentDictionary<string, CatalogCacheEntry> CatalogCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<YummyVideoEntry>>>> InflightCatalogLoads = new(StringComparer.Ordinal);
    private static readonly TimeSpan CatalogCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan CatalogFailureCacheTtl = TimeSpan.FromMinutes(1);

    public static async Task<IReadOnlyList<YummyVideoEntry>> LoadEntriesAsync(
        PluginConfiguration cfg,
        YummyAnimeResponse? anime,
        HttpClient httpClient,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cfg);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);

        var apiToken = ResolveApiToken(cfg, logger);
        if (apiToken.Length == 0 || anime?.RemoteIds == null)
        {
            return Array.Empty<YummyVideoEntry>();
        }

        if (!TryResolveQuery(anime.RemoteIds, out var queryName, out var queryValue))
        {
            return Array.Empty<YummyVideoEntry>();
        }

        var cacheKey = BuildCacheKey(cfg.AllohaApiBaseUrl, apiToken, queryName, queryValue);
        if (TryGetCachedEntries(cacheKey, out var cachedEntries))
        {
            return cachedEntries;
        }

        var lazyLoad = InflightCatalogLoads.GetOrAdd(
            cacheKey,
            _ => CreateCatalogLoadTask(cacheKey, cfg, anime, httpClient, logger, apiToken, queryName, queryValue));

        return await lazyLoad.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public static IReadOnlyList<YummyVideoEntry> FilterEntriesForSeason(
        IEnumerable<YummyVideoEntry>? entries,
        int seasonNumber)
    {
        if (entries == null)
        {
            return Array.Empty<YummyVideoEntry>();
        }

        if (seasonNumber <= 0)
        {
            return entries.ToArray();
        }

        return entries
            .Where(entry =>
                entry.Provider != YummyVideoProviderKind.Alloha ||
                entry.Alloha == null ||
                entry.Alloha.SeasonNumber <= 0 ||
                entry.Alloha.SeasonNumber == seasonNumber)
            .ToArray();
    }

    private static string ResolveApiToken(PluginConfiguration cfg, ILogger logger)
    {
        var configured = (cfg.AllohaApiToken ?? string.Empty).Trim();
        if (configured.Length > 0)
        {
            return configured;
        }

        return TryResolveSidecarToken(logger);
    }

    private static string TryResolveSidecarToken(ILogger logger)
    {
        try
        {
            var pluginDir = Path.GetDirectoryName(Plugin.Instance?.AssemblyFilePath ?? string.Empty);
            if (string.IsNullOrWhiteSpace(pluginDir))
            {
                return string.Empty;
            }

            var sidecarPath = Path.Combine(pluginDir, "AllohaApiToken.txt");
            if (!File.Exists(sidecarPath))
            {
                return string.Empty;
            }

            var token = File.ReadAllText(sidecarPath).Trim();
            if (token.Length > 0)
            {
                logger.LogDebug("[YummyKodik] Using Alloha API token from sidecar file '{Path}'.", sidecarPath);
            }

            return token;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[YummyKodik] Failed to read Alloha API sidecar token file.");
            return string.Empty;
        }
    }

    private static bool TryResolveQuery(
        YummyRemoteIds remoteIds,
        out string queryName,
        out string queryValue)
    {
        if (remoteIds.KpId is > 0)
        {
            queryName = "kp";
            queryValue = remoteIds.KpId.Value.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        var imdbId = (remoteIds.ImdbId ?? string.Empty).Trim();
        if (imdbId.Length > 0)
        {
            queryName = "imdb";
            queryValue = imdbId;
            return true;
        }

        queryName = string.Empty;
        queryValue = string.Empty;
        return false;
    }

    private static string BuildCacheKey(
        string? configuredBaseUrl,
        string apiToken,
        string queryName,
        string queryValue)
    {
        var baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? DefaultBaseUrl
            : configuredBaseUrl.Trim().TrimEnd('/');

        return $"{baseUrl}|{apiToken}|{queryName}|{queryValue}";
    }

    private static bool TryGetCachedEntries(string cacheKey, out IReadOnlyList<YummyVideoEntry> entries)
    {
        entries = Array.Empty<YummyVideoEntry>();

        if (!CatalogCache.TryGetValue(cacheKey, out var cached))
        {
            return false;
        }

        if (cached.ExpiresAtUtc <= DateTime.UtcNow)
        {
            CatalogCache.TryRemove(cacheKey, out _);
            return false;
        }

        entries = cached.Entries;
        return true;
    }

    private static Lazy<Task<IReadOnlyList<YummyVideoEntry>>> CreateCatalogLoadTask(
        string cacheKey,
        PluginConfiguration cfg,
        YummyAnimeResponse anime,
        HttpClient httpClient,
        ILogger logger,
        string apiToken,
        string queryName,
        string queryValue)
    {
        Lazy<Task<IReadOnlyList<YummyVideoEntry>>>? lazyLoad = null;
        lazyLoad = new Lazy<Task<IReadOnlyList<YummyVideoEntry>>>(() =>
        {
            var registration = lazyLoad!;
            var loadTask = LoadAndCacheEntriesAsync(cacheKey, cfg, anime, httpClient, logger, apiToken, queryName, queryValue);
            _ = loadTask.ContinueWith(
                static (_, state) =>
                {
                    var (key, registration, inflightLoads) =
                        ((string Key, Lazy<Task<IReadOnlyList<YummyVideoEntry>>> Registration, ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<YummyVideoEntry>>>> InflightLoads))state!;
                    inflightLoads.TryRemove(new KeyValuePair<string, Lazy<Task<IReadOnlyList<YummyVideoEntry>>>>(key, registration));
                },
                (cacheKey, registration, InflightCatalogLoads),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            return loadTask;
        }, LazyThreadSafetyMode.ExecutionAndPublication);

        return lazyLoad;
    }

    private static async Task<IReadOnlyList<YummyVideoEntry>> LoadAndCacheEntriesAsync(
        string cacheKey,
        PluginConfiguration cfg,
        YummyAnimeResponse anime,
        HttpClient httpClient,
        ILogger logger,
        string apiToken,
        string queryName,
        string queryValue)
    {
        var client = new AllohaApiClient(httpClient, apiToken, cfg.AllohaApiBaseUrl);

        try
        {
            IReadOnlyList<YummyVideoEntry> entries = queryName switch
            {
                "kp" => await client.GetCatalogEntriesByKpAsync(
                        long.Parse(queryValue, CultureInfo.InvariantCulture),
                        CancellationToken.None)
                    .ConfigureAwait(false),
                "imdb" => await client.GetCatalogEntriesByImdbAsync(queryValue, CancellationToken.None).ConfigureAwait(false),
                _ => Array.Empty<YummyVideoEntry>()
            };

            var cachedEntries = entries.Count == 0 ? Array.Empty<YummyVideoEntry>() : entries.ToArray();
            var cachedEntryCount = cachedEntries.Length;
            CatalogCache[cacheKey] = new CatalogCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(CatalogCacheTtl),
                Entries = cachedEntries
            };

            if (cachedEntryCount > 0)
            {
                logger.LogInformation(
                    "[YummyKodik] Alloha API catalog loaded. animeId={AnimeId} kpId={KpId} imdbId={ImdbId} entries={Count}",
                    anime.AnimeId,
                    anime.RemoteIds?.KpId,
                    anime.RemoteIds?.ImdbId,
                    cachedEntryCount);
            }

            return cachedEntries;
        }
        catch (Exception ex)
        {
            CatalogCache[cacheKey] = new CatalogCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(CatalogFailureCacheTtl),
                Entries = Array.Empty<YummyVideoEntry>()
            };

            logger.LogWarning(
                ex,
                "[YummyKodik] Alloha API fallback failed. animeId={AnimeId} kpId={KpId} imdbId={ImdbId}",
                anime.AnimeId,
                anime.RemoteIds?.KpId,
                anime.RemoteIds?.ImdbId);
            return Array.Empty<YummyVideoEntry>();
        }
    }

    private sealed class CatalogCacheEntry
    {
        public DateTime ExpiresAtUtc { get; init; }

        public IReadOnlyList<YummyVideoEntry> Entries { get; init; } = Array.Empty<YummyVideoEntry>();
    }
}
