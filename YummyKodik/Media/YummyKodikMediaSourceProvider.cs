using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;

namespace YummyKodik.Media;

public sealed class YummyKodikMediaSourceProvider : IMediaSourceProvider
{
    private static readonly ConcurrentDictionary<string, TranslationCacheEntry> TranslationCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TranslationCacheTtl = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, RuntimeCacheEntry> RuntimeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RuntimeCacheTtl = TimeSpan.FromHours(6);

    private readonly ILogger<YummyKodikMediaSourceProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    public YummyKodikMediaSourceProvider(
        ILogger<YummyKodikMediaSourceProvider> logger,
        IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    public string Name => "YummyKodik media source";

    public Task<ILiveStream> OpenMediaSource(
        string openToken,
        List<ILiveStream> currentLiveStreams,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException("OpenMediaSource is not supported by YummyKodik.");
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (!TryGetLogicalUri(item, out var uri))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        if (!TryParseLogicalUri(uri, out var idType, out var id, out var episode, out var explicitTranslationId))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var cfg = Plugin.Instance.Configuration;
        var quality = cfg.PreferredQuality > 0 ? cfg.PreferredQuality : 720;

        var baseUrl = (cfg.ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(baseUrl))
        {
            _logger.LogWarning("ServerBaseUrl is not configured. YummyKodik media sources will not be exposed.");
            return Array.Empty<MediaSourceInfo>();
        }

        var streamBase =
            $"{baseUrl}/YummyKodik/stream?type={idType.ToString().ToLowerInvariant()}" +
            $"&id={Uri.EscapeDataString(id)}&ep={episode}";

        IReadOnlyList<KodikTranslation> allTranslations = Array.Empty<KodikTranslation>();
        IReadOnlyList<KodikTranslation> filteredTranslations = Array.Empty<KodikTranslation>();

        try
        {
            allTranslations = await GetTranslationsCachedAsync(idType, id, cancellationToken).ConfigureAwait(false);
            filteredTranslations = FilterAndOrderTranslations(allTranslations, cfg.PreferredTranslationFilter);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load translations for idType={IdType} id={Id}. Returning Auto only.", idType, id);
        }

        if (!string.IsNullOrWhiteSpace(explicitTranslationId))
        {
            var explicitName = BuildExplicitTranslationName(filteredTranslations, allTranslations, explicitTranslationId);
            var explicitRuntimeTicks = await ResolveRunTimeTicksAsync(
                    idType,
                    id,
                    episode,
                    explicitTranslationId,
                    quality,
                    cancellationToken)
                .ConfigureAwait(false);

            return new[]
            {
                BuildSource(
                    itemId: item.Id.ToString(),
                    episode: episode,
                    suffix: $"tr{explicitTranslationId}",
                    name: explicitName,
                    url: BuildStreamUrl(streamBase, explicitTranslationId),
                    container: "m3u8",
                    supportsDirectPlay: false,
                    runTimeTicks: explicitRuntimeTicks)
            };
        }

        var runtimeTranslationId = PickRuntimeTranslationId(filteredTranslations, allTranslations, episode, cfg.PreferredTranslationFilter);
        var defaultRunTimeTicks = await ResolveRunTimeTicksAsync(
                idType,
                id,
                episode,
                runtimeTranslationId,
                quality,
                cancellationToken)
            .ConfigureAwait(false);

        var sources = new List<MediaSourceInfo>(8);

        sources.Add(BuildSource(
            itemId: item.Id.ToString(),
            episode: episode,
            suffix: "auto",
            name: "Auto",
            url: BuildStreamUrl(streamBase, translationId: null),
            container: "m3u8",
            supportsDirectPlay: false,
            runTimeTicks: defaultRunTimeTicks));

        foreach (var tr in filteredTranslations)
        {
            if (string.IsNullOrWhiteSpace(tr.Id))
            {
                continue;
            }

            if (tr.MaxEpisode > 0 && episode > tr.MaxEpisode)
            {
                continue;
            }

            var trId = tr.Id.Trim();
            if (string.Equals(trId, "0", StringComparison.Ordinal))
            {
                continue;
            }

            var label = BuildTranslationLabel(tr);

            sources.Add(BuildSource(
                itemId: item.Id.ToString(),
                episode: episode,
                suffix: $"tr{trId}",
                name: label,
                url: BuildStreamUrl(streamBase, trId),
                container: "m3u8",
                supportsDirectPlay: false,
                runTimeTicks: defaultRunTimeTicks));
        }

        _logger.LogInformation(
            "Provided {Count} media sources for '{ItemName}' (episode {Episode}).",
            sources.Count,
            item.Name,
            episode);

        return sources;
    }

    private static MediaSourceInfo BuildSource(
        string itemId,
        int episode,
        string suffix,
        string name,
        string url,
        string container = "mp4",
        bool supportsDirectPlay = true,
        long? runTimeTicks = null)
    {
        var source = new MediaSourceInfo
        {
            Id = $"{itemId}_ep{episode}_{suffix}",
            Path = url,
            Protocol = MediaProtocol.Http,
            Container = container,
            IsRemote = true,
            HasSegments = true,
            RequiresOpening = false,
            IsInfiniteStream = false,
            SupportsDirectPlay = supportsDirectPlay,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            Name = name
        };

        SetOptionalRunTimeTicks(source, runTimeTicks);
        return source;
    }

    private static string BuildTranslationLabel(KodikTranslation t)
    {
        var name = (t.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = $"Translation {t.Id}";
        }

        var type = (t.Type ?? string.Empty).Trim();
        if (type.Length == 0)
        {
            return name;
        }

        if (type.Equals("voice", StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        if (type.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} [subs]";
        }

        return $"{name} [{type}]";
    }

    private static IReadOnlyList<KodikTranslation> FilterAndOrderTranslations(
        IReadOnlyList<KodikTranslation> translations,
        string? preferredFilter)
    {
        translations ??= Array.Empty<KodikTranslation>();

        var tokens = StringTokenParser.ParseTokens(preferredFilter);

        // Prefer voice translations in the list. If none, use everything.
        var voice = translations
            .Where(t => string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var baseList = voice.Count > 0 ? voice : translations.ToList();

        var used = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<KodikTranslation>(baseList.Count);

        void Add(KodikTranslation? t)
        {
            if (t == null)
            {
                return;
            }

            var id = (t.Id ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                return;
            }

            if (used.Add(id))
            {
                ordered.Add(t);
            }
        }

        if (tokens.Length > 0)
        {
            foreach (var token in tokens)
            {
                var hit = FindBestMatchByToken(baseList, token);
                Add(hit);
            }
        }

        foreach (var t in baseList.OrderBy(x => (x.Name ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase))
        {
            Add(t);
        }

        return ordered;
    }

    private static KodikTranslation? FindBestMatchByToken(IReadOnlyList<KodikTranslation> translations, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var needle = token.Trim();

        // Prefer voice match first (even if list already voice only).
        var voiceHit = translations.FirstOrDefault(t =>
            string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Name) &&
            t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Id));

        if (voiceHit != null)
        {
            return voiceHit;
        }

        var anyHit = translations.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.Name) &&
            t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Id));

        return anyHit;
    }

    private async Task<IReadOnlyList<KodikTranslation>> GetTranslationsCachedAsync(
        KodikIdType idType,
        string id,
        CancellationToken cancellationToken)
    {
        var key = $"{idType.ToString().ToLowerInvariant()}:{(id ?? string.Empty).Trim()}".ToLowerInvariant();

        if (TranslationCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Translations;
        }

        var loaded = await LoadTranslationsAsync(idType, id, cancellationToken).ConfigureAwait(false);

        TranslationCache[key] = new TranslationCacheEntry
        {
            ExpiresAtUtc = DateTime.UtcNow.Add(TranslationCacheTtl),
            Translations = loaded
        };

        return loaded;
    }

    private async Task<IReadOnlyList<KodikTranslation>> LoadTranslationsAsync(
        KodikIdType idType,
        string id,
        CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance.Configuration;
        var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
        var token = await ResolveKodikTokenAsync(http, cfg, cancellationToken).ConfigureAwait(false);
        var kodik = new KodikClient(http, token, _logger);

        var infoRes = await ExecuteWithAutoTokenRefreshAsync(
                kodik,
                http,
                cfg,
                cancellationToken,
                client => client.GetAnimeInfoAsync(id, idType, cancellationToken))
            .ConfigureAwait(false);

        return infoRes.Result.Translations ?? Array.Empty<KodikTranslation>();
    }

    private static bool TryGetLogicalUri(BaseItem item, out string uri)
    {
        uri = string.Empty;

        if (string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        if (item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) && File.Exists(item.Path))
        {
            uri = File.ReadAllText(item.Path).Trim();
            return !string.IsNullOrEmpty(uri);
        }

        uri = item.Path.Trim();
        return !string.IsNullOrEmpty(uri);
    }

    private async Task<long?> ResolveRunTimeTicksAsync(
        KodikIdType idType,
        string id,
        int episode,
        string translationId,
        int quality,
        CancellationToken cancellationToken)
    {
        var normalizedTranslationId = string.IsNullOrWhiteSpace(translationId) ? "0" : translationId.Trim();
        var cacheKey = BuildRuntimeCacheKey(idType, id, episode, normalizedTranslationId, quality);

        if (TryGetCachedRunTimeTicks(cacheKey, out var cachedRunTimeTicks))
        {
            return cachedRunTimeTicks;
        }

        var cfg = Plugin.Instance.Configuration;
        var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);

        try
        {
            var token = await ResolveKodikTokenAsync(http, cfg, cancellationToken).ConfigureAwait(false);
            var kodik = new KodikClient(http, token, _logger);

            var runtimeRes = await ExecuteWithAutoTokenRefreshAsync(
                    kodik,
                    http,
                    cfg,
                    cancellationToken,
                    client => client.GetEpisodeRuntimeAsync(id, idType, episode, normalizedTranslationId, quality, cancellationToken))
                .ConfigureAwait(false);

            var runTimeTicks = runtimeRes.Result?.Ticks;
            RuntimeCache[cacheKey] = new RuntimeCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(RuntimeCacheTtl),
                RunTimeTicks = runTimeTicks
            };

            return runTimeTicks;
        }
        catch (Exception ex) when (ex is KodikException || ex is HttpRequestException || ex is TaskCanceledException)
        {
            _logger.LogDebug(
                ex,
                "Failed to resolve runtime for idType={IdType} id={Id} episode={Episode} tr={TrId}",
                idType,
                id,
                episode,
                normalizedTranslationId);

            RuntimeCache[cacheKey] = new RuntimeCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(TimeSpan.FromMinutes(30)),
                RunTimeTicks = null
            };

            return null;
        }
    }

    private static bool TryParseLogicalUri(
        string uri,
        out KodikIdType idType,
        out string id,
        out int episode,
        out string explicitTranslationId)
    {
        idType = default;
        id = string.Empty;
        episode = 0;
        explicitTranslationId = string.Empty;

        if (!YummyKodikStreamUri.TryParse(uri, out idType, out id, out var ep))
        {
            return false;
        }

        if (!ep.HasValue || ep.Value <= 0)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        episode = ep.Value;
        id = id.Trim();

        if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed))
        {
            var dict = YummyKodikStreamUri.ParseQueryToDictionary(parsed.Query);
            if (dict.TryGetValue("tr", out var tr) && !string.IsNullOrWhiteSpace(tr))
            {
                explicitTranslationId = tr.Trim();
            }
        }

        return true;
    }

    private static string BuildStreamUrl(string streamBase, string? translationId)
    {
        var trId = (translationId ?? string.Empty).Trim();
        if (trId.Length == 0 || string.Equals(trId, "0", StringComparison.Ordinal))
        {
            return streamBase + "&format=hls";
        }

        return streamBase + $"&tr={Uri.EscapeDataString(trId)}&format=hls";
    }

    private static string PickRuntimeTranslationId(
        IReadOnlyList<KodikTranslation> filteredTranslations,
        IReadOnlyList<KodikTranslation> allTranslations,
        int episode,
        string? preferredFilter)
    {
        var preferredTokens = StringTokenParser.ParseTokens(preferredFilter);
        var translationPool = allTranslations.Count > 0 ? allTranslations : filteredTranslations;

        var (translationId, _, _) = KodikPlaybackSelector.PickTranslationForPlayback(
            translationPool,
            preferredTokens,
            savedTranslationId: string.Empty,
            explicitTranslationId: string.Empty,
            episode);

        return string.IsNullOrWhiteSpace(translationId) ? "0" : translationId.Trim();
    }

    private static string BuildExplicitTranslationName(
        IReadOnlyList<KodikTranslation> filteredTranslations,
        IReadOnlyList<KodikTranslation> allTranslations,
        string explicitTranslationId)
    {
        var trId = (explicitTranslationId ?? string.Empty).Trim();
        if (trId.Length == 0)
        {
            return "Current translation";
        }

        var translation = FindTranslationById(filteredTranslations, trId) ?? FindTranslationById(allTranslations, trId);
        return translation != null ? BuildTranslationLabel(translation) : $"Translation {trId}";
    }

    private static KodikTranslation? FindTranslationById(IReadOnlyList<KodikTranslation> translations, string translationId)
    {
        return translations.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.Id) &&
            string.Equals(t.Id.Trim(), translationId, StringComparison.Ordinal));
    }

    private static string BuildRuntimeCacheKey(KodikIdType idType, string id, int episode, string translationId, int quality)
    {
        return $"{idType.ToString().ToLowerInvariant()}:{(id ?? string.Empty).Trim().ToLowerInvariant()}:ep:{episode}:tr:{translationId}:q:{quality}";
    }

    private static bool TryGetCachedRunTimeTicks(string cacheKey, out long? runTimeTicks)
    {
        runTimeTicks = null;

        if (!RuntimeCache.TryGetValue(cacheKey, out var cached) || cached.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return false;
        }

        runTimeTicks = cached.RunTimeTicks;
        return true;
    }

    private static void SetOptionalRunTimeTicks(MediaSourceInfo source, long? runTimeTicks)
    {
        if (source == null || !runTimeTicks.HasValue || runTimeTicks.Value <= 0)
        {
            return;
        }

        var property = typeof(MediaSourceInfo).GetProperty("RunTimeTicks");
        if (property == null || !property.CanWrite)
        {
            return;
        }

        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (propertyType != typeof(long))
        {
            return;
        }

        var value = property.PropertyType == typeof(long?) ? runTimeTicks : runTimeTicks.Value;
        property.SetValue(source, value);
    }

    private static async Task<string> ResolveKodikTokenAsync(HttpClient http, PluginConfiguration cfg, CancellationToken ct)
    {
        var configured = (cfg.KodikToken ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return await KodikTokenProvider.GetTokenAsync(http, ct).ConfigureAwait(false);
    }

    private static async Task<(KodikClient Client, T Result)> ExecuteWithAutoTokenRefreshAsync<T>(
        KodikClient client,
        HttpClient http,
        PluginConfiguration cfg,
        CancellationToken ct,
        Func<KodikClient, Task<T>> action)
    {
        try
        {
            var result = await action(client).ConfigureAwait(false);
            return (client, result);
        }
        catch (KodikTokenException) when (string.IsNullOrWhiteSpace((cfg.KodikToken ?? string.Empty).Trim()))
        {
            KodikTokenProvider.InvalidateCache();

            var freshToken = await KodikTokenProvider.GetTokenAsync(
                    http,
                    ct,
                    forceRefresh: true,
                    allowStaleOnFailure: false)
                .ConfigureAwait(false);

            var refreshedClient = new KodikClient(http, freshToken);
            var result = await action(refreshedClient).ConfigureAwait(false);
            return (refreshedClient, result);
        }
    }

    private sealed class TranslationCacheEntry
    {
        public DateTime ExpiresAtUtc { get; set; }
        public IReadOnlyList<KodikTranslation> Translations { get; set; } = Array.Empty<KodikTranslation>();
    }

    private sealed class RuntimeCacheEntry
    {
        public DateTime ExpiresAtUtc { get; set; }
        public long? RunTimeTicks { get; set; }
    }
}
