using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;

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
            TryParseLogicalUri(uri, out _, out _, out _, out _);

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

    private sealed class SegmentCacheEntry
    {
        public DateTime ExpiresAtUtc { get; set; }

        public IReadOnlyList<MediaSegmentDto> Segments { get; set; } = Array.Empty<MediaSegmentDto>();
    }
}
