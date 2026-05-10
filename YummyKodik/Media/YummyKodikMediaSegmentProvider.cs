using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Media;

public sealed class YummyKodikMediaSegmentProvider : IMediaSegmentProvider
{
    private static readonly ConcurrentDictionary<string, SegmentCacheEntry> SegmentCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SegmentCacheTtl = TimeSpan.FromHours(6);

    private readonly ILibraryManager _libraryManager;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YummyKodikMediaSegmentProvider> _logger;

    public YummyKodikMediaSegmentProvider(
        ILibraryManager libraryManager,
        IHttpClientFactory httpClientFactory,
        ILogger<YummyKodikMediaSegmentProvider> logger)
    {
        _libraryManager = libraryManager;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string Name => "YummyKodik skip timings";

    public ValueTask<bool> Supports(BaseItem item)
    {
        var supported =
            item != null &&
            TryGetLogicalUri(item, out var uri) &&
            (YummyKodikStreamUri.TryParseRequest(uri, out _) || TryParseLogicalUri(uri, out _, out _, out _, out _));

        return ValueTask.FromResult(supported);
    }

    public async Task<IReadOnlyList<MediaSegmentDto>> GetMediaSegments(
        MediaSegmentGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(request.ItemId);
        if (item == null)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        if (TryGetLogicalUri(item, out var cvhUri) &&
            YummyKodikStreamUri.TryParseRequest(cvhUri, out var streamRequest) &&
            streamRequest.Provider == YummyStreamProviderKind.Cvh)
        {
            var cvhSegments = await GetCvhMediaSegmentsAsync(streamRequest, request.ItemId, cancellationToken).ConfigureAwait(false);
            return FilterExistingSegments(request, cvhSegments);
        }

        if (TryGetLogicalUri(item, out var allohaUri) &&
            YummyKodikStreamUri.TryParseRequest(allohaUri, out streamRequest) &&
            streamRequest.Provider == YummyStreamProviderKind.Alloha)
        {
            var allohaSegments = await GetProviderMediaSegmentsAsync(
                    streamRequest,
                    request.ItemId,
                    YummyVideoProviderKind.Alloha,
                    cancellationToken)
                .ConfigureAwait(false);
            return FilterExistingSegments(request, allohaSegments);
        }

        if (!TryGetLogicalUri(item, out var uri) ||
            !TryParseLogicalUri(uri, out var idType, out var id, out var episode, out var explicitTranslationId))
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var cfg = Plugin.Instance.Configuration;

        try
        {
            var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
            var token = await ResolveKodikTokenAsync(http, cfg, cancellationToken).ConfigureAwait(false);
            var kodik = new KodikClient(http, token, _logger);

            var translationId = (explicitTranslationId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(translationId))
            {
                translationId = await PickTranslationIdAsync(
                        kodik,
                        http,
                        cfg,
                        idType,
                        id,
                        episode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(translationId))
            {
                translationId = "0";
            }

            var cacheKey = BuildCacheKey(idType, id, episode, translationId);
            if (TryGetCachedSegments(cacheKey, out var cached))
            {
                return FilterExistingSegments(request, cached);
            }

            var timingsRes = await ExecuteWithAutoTokenRefreshAsync(
                    kodik,
                    http,
                    cfg,
                    cancellationToken,
                    client => client.GetEpisodeTimingsAsync(id, idType, episode, translationId, cancellationToken))
                .ConfigureAwait(false);

            var segments = BuildSegments(request.ItemId, timingsRes.Result);
            if (segments.Count == 0)
            {
                var yummyFallbackSegments = await TryGetYummyFallbackSegmentsForKodikItemAsync(
                        item,
                        request.ItemId,
                        episode,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (yummyFallbackSegments.Count > 0)
                {
                    segments = yummyFallbackSegments;
                }
            }

            SegmentCache[cacheKey] = new SegmentCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(SegmentCacheTtl),
                Segments = segments
            };

            return FilterExistingSegments(request, segments);
        }
        catch (KodikTokenException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping media segment generation because Kodik token is unavailable. itemId={ItemId}",
                request.ItemId);

            return Array.Empty<MediaSegmentDto>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve media segments. itemId={ItemId} idType={IdType} id={Id} episode={Episode}",
                request.ItemId,
                idType,
                id,
                episode);

            return Array.Empty<MediaSegmentDto>();
        }
    }

    private async Task<IReadOnlyList<MediaSegmentDto>> GetCvhMediaSegmentsAsync(
        YummyStreamRequest streamRequest,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        return await GetProviderMediaSegmentsAsync(
                streamRequest,
                itemId,
                YummyVideoProviderKind.Cvh,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MediaSegmentDto>> GetProviderMediaSegmentsAsync(
        YummyStreamRequest streamRequest,
        Guid itemId,
        YummyVideoProviderKind provider,
        CancellationToken cancellationToken)
    {
        if (!streamRequest.Episode.HasValue || streamRequest.Episode.Value <= 0 || streamRequest.AnimeId <= 0)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var cfg = Plugin.Instance.Configuration;
        var episode = streamRequest.Episode.Value;
        var voiceName = (streamRequest.VoiceName ?? string.Empty).Trim();
        var cacheKey = BuildProviderCacheKey(provider, streamRequest.AnimeId, episode, voiceName);

        if (TryGetCachedSegments(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var catalog = await LoadYummyVideoCatalogAsync(cfg, streamRequest.AnimeId.ToString(), cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(voiceName))
            {
                voiceName = catalog.PickPreferredVoiceName(
                    provider,
                    episode,
                    explicitVoiceName: string.Empty,
                    savedVoiceName: string.Empty,
                    preferredFilter: cfg.PreferredTranslationFilter,
                    out _)
                    ?? string.Empty;
            }

            var providerOrder = GetProviderFallbackOrder(provider);
            var skipEntry = catalog.FindPreferredEntryWithSkipsAcrossProviders(episode, voiceName, providerOrder);
            if (skipEntry != null && skipEntry.Provider != provider)
            {
                _logger.LogInformation(
                    "[YummyKodik] Media segment fallback used {FallbackProvider} skips for requested {RequestedProvider} item. animeId={AnimeId} episode={Episode} requestedVoice={RequestedVoice} resolvedVoice={ResolvedVoice}",
                    skipEntry.Provider,
                    provider,
                    streamRequest.AnimeId,
                    episode,
                    voiceName,
                    skipEntry.DisplayVoiceName);
            }

            var segments = BuildSegments(itemId, skipEntry?.Skips);
            SegmentCache[cacheKey] = new SegmentCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(SegmentCacheTtl),
                Segments = segments
            };

            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to resolve {Provider} media segments. animeId={AnimeId} episode={Episode} voice={Voice}",
                provider,
                streamRequest.AnimeId,
                episode,
                voiceName);

            return Array.Empty<MediaSegmentDto>();
        }
    }

    private async Task<IReadOnlyList<MediaSegmentDto>> TryGetYummyFallbackSegmentsForKodikItemAsync(
        BaseItem item,
        Guid itemId,
        int episode,
        CancellationToken cancellationToken)
    {
        if (!TryResolveSiblingYummyContext(item, episode, out var animeId, out var preferredVoiceName, out var providerOrder))
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var cacheKey = BuildYummyFallbackCacheKey(animeId, episode, preferredVoiceName, providerOrder);
        if (TryGetCachedSegments(cacheKey, out var cached))
        {
            return cached;
        }

        try
        {
            var cfg = Plugin.Instance.Configuration;
            var catalog = await LoadYummyVideoCatalogAsync(cfg, animeId.ToString(CultureInfo.InvariantCulture), cancellationToken)
                .ConfigureAwait(false);

            var skipEntry = catalog.FindPreferredEntryWithSkipsAcrossProviders(episode, preferredVoiceName, providerOrder);
            var segments = BuildSegments(itemId, skipEntry?.Skips);
            if (segments.Count > 0 && skipEntry != null)
            {
                _logger.LogInformation(
                    "[YummyKodik] Media segment fallback used {Provider} skips for Kodik item. animeId={AnimeId} episode={Episode} preferredVoice={PreferredVoice} resolvedVoice={ResolvedVoice}",
                    skipEntry.Provider,
                    animeId,
                    episode,
                    preferredVoiceName,
                    skipEntry.DisplayVoiceName);
            }

            SegmentCache[cacheKey] = new SegmentCacheEntry
            {
                ExpiresAtUtc = DateTime.UtcNow.Add(SegmentCacheTtl),
                Segments = segments
            };

            return segments;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to resolve Yummy segment fallback for Kodik item. animeId={AnimeId} episode={Episode} voice={Voice}",
                animeId,
                episode,
                preferredVoiceName);

            return Array.Empty<MediaSegmentDto>();
        }
    }

    private async Task<string> PickTranslationIdAsync(
        KodikClient kodik,
        HttpClient http,
        PluginConfiguration cfg,
        KodikIdType idType,
        string id,
        int episode,
        CancellationToken cancellationToken)
    {
        try
        {
            var infoRes = await ExecuteWithAutoTokenRefreshAsync(
                    kodik,
                    http,
                    cfg,
                    cancellationToken,
                    client => client.GetAnimeInfoAsync(id, idType, cancellationToken))
                .ConfigureAwait(false);

            var seriesKey = KodikPlaybackSelector.BuildSeriesKey(idType, id);
            var preferredTokens = StringTokenParser.ParseTokens(cfg.PreferredTranslationFilter);
            var savedTrId = cfg.GetUserSeriesPreferredTranslationId(Guid.Empty, seriesKey);

            var (translationId, _, _) = KodikPlaybackSelector.PickTranslationForPlayback(
                infoRes.Result.Translations,
                preferredTokens,
                savedTrId,
                explicitTranslationId: string.Empty,
                episode);

            return string.IsNullOrWhiteSpace(translationId) ? "0" : translationId;
        }
        catch (Exception ex) when (ex is not KodikTokenException)
        {
            _logger.LogDebug(
                ex,
                "Failed to pick translation for segment generation, falling back to default player page. idType={IdType} id={Id} episode={Episode}",
                idType,
                id,
                episode);

            return "0";
        }
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

    private static IReadOnlyList<MediaSegmentDto> BuildSegments(Guid itemId, KodikEpisodeTimings timings)
    {
        if (timings == null || !timings.HasAny)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var result = new List<MediaSegmentDto>(2);

        if (timings.Intro != null)
        {
            result.Add(new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = MediaSegmentType.Intro,
                StartTicks = timings.Intro.Start.Ticks,
                EndTicks = timings.Intro.End.Ticks
            });
        }

        if (timings.Outro != null)
        {
            result.Add(new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = MediaSegmentType.Outro,
                StartTicks = timings.Outro.Start.Ticks,
                EndTicks = timings.Outro.End.Ticks
            });
        }

        return result;
    }

    private static IReadOnlyList<MediaSegmentDto> BuildSegments(Guid itemId, YummyVideoSkips? skips)
    {
        if (skips == null)
        {
            return Array.Empty<MediaSegmentDto>();
        }

        var result = new List<MediaSegmentDto>(2);

        if (skips.Opening != null && skips.Opening.Length > 0)
        {
            result.Add(new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = MediaSegmentType.Intro,
                StartTicks = TimeSpan.FromSeconds(Math.Max(0, skips.Opening.Time)).Ticks,
                EndTicks = TimeSpan.FromSeconds(Math.Max(0, skips.Opening.Time) + skips.Opening.Length).Ticks
            });
        }

        if (skips.Ending != null && skips.Ending.Length > 0)
        {
            result.Add(new MediaSegmentDto
            {
                Id = Guid.NewGuid(),
                ItemId = itemId,
                Type = MediaSegmentType.Outro,
                StartTicks = TimeSpan.FromSeconds(Math.Max(0, skips.Ending.Time)).Ticks,
                EndTicks = TimeSpan.FromSeconds(Math.Max(0, skips.Ending.Time) + skips.Ending.Length).Ticks
            });
        }

        return result;
    }

    private static IReadOnlyList<MediaSegmentDto> FilterExistingSegments(
        MediaSegmentGenerationRequest request,
        IReadOnlyList<MediaSegmentDto> segments)
    {
        if (segments.Count == 0 || request.ExistingSegments == null || request.ExistingSegments.Count == 0)
        {
            return segments;
        }

        var existingTypes = request.ExistingSegments
            .Select(x => x.Type)
            .ToHashSet();

        if (existingTypes.Count == 0)
        {
            return segments;
        }

        return segments
            .Where(x => !existingTypes.Contains(x.Type))
            .ToArray();
    }

    private static string BuildCacheKey(KodikIdType idType, string id, int episode, string translationId)
    {
        var typePart = idType.ToString().ToLowerInvariant();
        var idPart = (id ?? string.Empty).Trim().ToLowerInvariant();
        var trPart = (translationId ?? string.Empty).Trim();
        return $"{typePart}:{idPart}:ep:{episode}:tr:{trPart}";
    }

    private static string BuildProviderCacheKey(YummyVideoProviderKind provider, long animeId, int episode, string voiceName)
    {
        var providerKey = provider.ToString().ToLowerInvariant();
        return $"{providerKey}:{animeId}:ep:{episode}:voice:{(voiceName ?? string.Empty).Trim().ToLowerInvariant()}";
    }

    private static string BuildYummyFallbackCacheKey(
        long animeId,
        int episode,
        string voiceName,
        IReadOnlyList<YummyVideoProviderKind> providerOrder)
    {
        var providers = string.Join(",", providerOrder.Select(x => x.ToString().ToLowerInvariant()));
        return $"yummy-fallback:{animeId}:ep:{episode}:voice:{(voiceName ?? string.Empty).Trim().ToLowerInvariant()}:providers:{providers}";
    }

    private static bool TryGetCachedSegments(string cacheKey, out IReadOnlyList<MediaSegmentDto> segments)
    {
        segments = Array.Empty<MediaSegmentDto>();

        if (!SegmentCache.TryGetValue(cacheKey, out var cached) || cached.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return false;
        }

        segments = cached.Segments;
        return true;
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

    private async Task<YummyVideoCatalog> LoadYummyVideoCatalogAsync(
        PluginConfiguration cfg,
        string animeKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cfg.YummyClientId))
        {
            throw new InvalidOperationException("YummyClientId is not configured.");
        }

        var http = _httpClientFactory.CreateClient(HttpClientNames.Yummy);
        var yummy = new YummyClient(http, cfg.YummyClientId, cfg.YummyApiBaseUrl);
        yummy.SetAccessToken(cfg.YummyAccessToken);

        var anime = await yummy.GetAnimeAsync(animeKey, includeVideos: true, cancellationToken).ConfigureAwait(false);
        var allohaApiHttp = _httpClientFactory.CreateClient(HttpClientNames.AllohaApi);
        var allohaApiEntries = await AllohaApiCatalogLoader
            .LoadEntriesAsync(cfg, anime, allohaApiHttp, _logger, cancellationToken)
            .ConfigureAwait(false);

        return YummyVideoCatalog.Create(anime, allohaApiEntries);
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

        if (!YummyKodikStreamUri.TryParse(uri, out idType, out id, out var ep) ||
            !ep.HasValue ||
            ep.Value <= 0 ||
            string.IsNullOrWhiteSpace(id))
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

    private static YummyVideoProviderKind[] GetProviderFallbackOrder(YummyVideoProviderKind provider)
    {
        return provider switch
        {
            YummyVideoProviderKind.Cvh => new[] { YummyVideoProviderKind.Cvh, YummyVideoProviderKind.Alloha },
            YummyVideoProviderKind.Alloha => new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh },
            _ => new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh }
        };
    }

    private static bool TryResolveSiblingYummyContext(
        BaseItem item,
        int episode,
        out long animeId,
        out string preferredVoiceName,
        out YummyVideoProviderKind[] providerOrder)
    {
        animeId = 0;
        preferredVoiceName = ExtractVoiceNameFromPath(item.Path);
        providerOrder = Array.Empty<YummyVideoProviderKind>();

        if (string.IsNullOrWhiteSpace(item.Path) ||
            !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(item.Path))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        YummyStreamRequest? bestRequest = null;
        var bestScore = int.MinValue;

        foreach (var siblingPath in Directory.EnumerateFiles(directory, "*.strm", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(siblingPath, item.Path, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string logicalUri;
            try
            {
                logicalUri = File.ReadAllText(siblingPath).Trim();
            }
            catch
            {
                continue;
            }

            if (!YummyKodikStreamUri.TryParseRequest(logicalUri, out var siblingRequest) ||
                siblingRequest.Provider is not YummyStreamProviderKind.Cvh and not YummyStreamProviderKind.Alloha ||
                !siblingRequest.Episode.HasValue ||
                siblingRequest.Episode.Value != episode ||
                siblingRequest.AnimeId <= 0)
            {
                continue;
            }

            var score = ScoreSiblingYummyRequest(siblingRequest, preferredVoiceName);
            if (score <= bestScore)
            {
                continue;
            }

            bestScore = score;
            bestRequest = siblingRequest;
        }

        if (bestRequest == null)
        {
            return false;
        }

        animeId = bestRequest.AnimeId;
        if (string.IsNullOrWhiteSpace(preferredVoiceName))
        {
            preferredVoiceName = bestRequest.VoiceName ?? string.Empty;
        }

        providerOrder = bestRequest.Provider switch
        {
            YummyStreamProviderKind.Cvh => new[] { YummyVideoProviderKind.Cvh, YummyVideoProviderKind.Alloha },
            YummyStreamProviderKind.Alloha => new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh },
            _ => new[] { YummyVideoProviderKind.Alloha, YummyVideoProviderKind.Cvh }
        };

        return true;
    }

    private static int ScoreSiblingYummyRequest(YummyStreamRequest request, string preferredVoiceName)
    {
        var normalizedPreferredVoice = YummyVideoCatalog.NormalizeVoiceName(preferredVoiceName);
        var normalizedRequestVoice = YummyVideoCatalog.NormalizeVoiceName(request.VoiceName);

        if (!string.IsNullOrWhiteSpace(normalizedPreferredVoice) &&
            string.Equals(normalizedPreferredVoice, normalizedRequestVoice, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (!string.IsNullOrWhiteSpace(normalizedPreferredVoice) &&
            !string.IsNullOrWhiteSpace(normalizedRequestVoice) &&
            (normalizedRequestVoice.Contains(normalizedPreferredVoice, StringComparison.OrdinalIgnoreCase) ||
             normalizedPreferredVoice.Contains(normalizedRequestVoice, StringComparison.OrdinalIgnoreCase)))
        {
            return 50;
        }

        return request.Provider == YummyStreamProviderKind.Alloha ? 20 : 10;
    }

    private static string ExtractVoiceNameFromPath(string? path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path ?? string.Empty);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var separatorIndex = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (separatorIndex < 0 || separatorIndex + 3 >= fileName.Length)
        {
            return string.Empty;
        }

        return fileName[(separatorIndex + 3)..].Trim();
    }

    private sealed class SegmentCacheEntry
    {
        public DateTime ExpiresAtUtc { get; set; }

        public IReadOnlyList<MediaSegmentDto> Segments { get; set; } = Array.Empty<MediaSegmentDto>();
    }
}
