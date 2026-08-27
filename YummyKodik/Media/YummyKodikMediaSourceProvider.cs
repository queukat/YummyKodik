using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using YummyKodik.Alloha;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Media;

public sealed class YummyKodikMediaSourceProvider : IMediaSourceProvider
{
    private const string HlsFormatQuery = "&format=hls";
    private const string TranslationTypeVoice = "voice";
    private const string TranslationTypeSubtitles = "subtitles";

    private static readonly ConcurrentDictionary<string, TranslationCacheEntry> TranslationCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan TranslationCacheTtl = TimeSpan.FromHours(1);
    private static readonly ConcurrentDictionary<string, RuntimeCacheEntry> RuntimeCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan RuntimeCacheTtl = TimeSpan.FromHours(6);

    private readonly ILogger<YummyKodikMediaSourceProvider> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IInternalJellyfinUrlProvider _internalJellyfinUrlProvider;
    private readonly ILibraryManager _libraryManager;

    public YummyKodikMediaSourceProvider(
        ILogger<YummyKodikMediaSourceProvider> logger,
        IHttpClientFactory httpClientFactory,
        IInternalJellyfinUrlProvider internalJellyfinUrlProvider,
        ILibraryManager libraryManager)
    {
        _logger = new YummyKodikLogger<YummyKodikMediaSourceProvider>(logger);
        _httpClientFactory = httpClientFactory;
        _internalJellyfinUrlProvider = internalJellyfinUrlProvider;
        _libraryManager = libraryManager;
    }

    [SuppressMessage("Major Code Smell", "S2325:Methods and properties that don't access instance data should be static", Justification = "Jellyfin IMediaSourceProvider requires an instance Name property.")]
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Jellyfin IMediaSourceProvider requires an instance Name property.")]
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
        var effectiveItem = ResolveMergedPrimaryVersion(item);
        if (!TryGetLogicalUri(effectiveItem, out var uri))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        IEnumerable<MediaSourceInfo>? sources = null;
        if (YummyKodikStreamUri.TryParseRequest(uri, out var request))
        {
            switch (request.Provider)
            {
                case YummyStreamProviderKind.Cvh:
                    sources = await GetCvhMediaSourcesAsync(effectiveItem, request, cancellationToken).ConfigureAwait(false);
                    break;
                case YummyStreamProviderKind.Alloha:
                    sources = await GetAllohaMediaSourcesAsync(effectiveItem, request, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        if (sources == null)
        {
            if (!TryParseLogicalUri(uri, out var idType, out var id, out var episode, out var explicitTranslationId))
            {
                return Array.Empty<MediaSourceInfo>();
            }

            var kodikRequest = new KodikMediaSourceRequest(idType, id, episode, explicitTranslationId);
            sources = await GetKodikMediaSourcesAsync(effectiveItem, kodikRequest, cancellationToken).ConfigureAwait(false);
        }

        var result = sources as MediaSourceInfo[] ?? sources.ToArray();
        MediaRunTimePolicy.FillMissingSourceRunTimes(effectiveItem.RunTimeTicks, result);
        await PublishResolvedRunTimeAsync(effectiveItem, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IEnumerable<MediaSourceInfo>> GetKodikMediaSourcesAsync(
        BaseItem item,
        KodikMediaSourceRequest request,
        CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance.Configuration;
        var quality = cfg.PreferredQuality > 0 ? cfg.PreferredQuality : 720;

        var baseUrl = _internalJellyfinUrlProvider.GetBaseUrl();

        var streamBase =
            $"{baseUrl}/YummyKodik/stream?type={request.IdType.ToString().ToLowerInvariant()}" +
            $"&id={Uri.EscapeDataString(request.Id)}&ep={request.Episode}";

        var (allTranslations, filteredTranslations) = await LoadOrderedTranslationsAsync(
                request.IdType,
                request.Id,
                cfg.PreferredTranslationFilter,
                cancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(request.ExplicitTranslationId))
        {
            return await BuildExplicitKodikMediaSourceAsync(
                    item,
                    request,
                    streamBase,
                    filteredTranslations,
                    allTranslations,
                    quality,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var runtimeTranslationId = PickRuntimeTranslationId(
            filteredTranslations,
            allTranslations,
            request.Episode,
            cfg.PreferredTranslationFilter);

        var defaultRunTimeTicks = await ResolveRunTimeTicksAsync(
                request.IdType,
                request.Id,
                request.Episode,
                runtimeTranslationId,
                quality,
                cancellationToken)
            .ConfigureAwait(false);

        var sources = BuildDefaultKodikMediaSources(
            item.Id.ToString(),
            request.Episode,
            streamBase,
            filteredTranslations,
            defaultRunTimeTicks);

        _logger.LogInformation(
            "Provided {Count} media sources for '{ItemName}' (episode {Episode}).",
            sources.Count,
            item.Name,
            request.Episode);

        return sources;
    }

    private async Task<(
        IReadOnlyList<KodikTranslation> AllTranslations,
        IReadOnlyList<KodikTranslation> FilteredTranslations)> LoadOrderedTranslationsAsync(
        KodikIdType idType,
        string id,
        string? preferredFilter,
        CancellationToken cancellationToken)
    {
        try
        {
            var allTranslations = await GetTranslationsCachedAsync(idType, id, cancellationToken).ConfigureAwait(false);
            var filteredTranslations = FilterAndOrderTranslations(allTranslations, preferredFilter);
            return (allTranslations, filteredTranslations);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load translations for idType={IdType} id={Id}. Returning Auto only.", idType, id);
            return (Array.Empty<KodikTranslation>(), Array.Empty<KodikTranslation>());
        }
    }

    private async Task<IEnumerable<MediaSourceInfo>> BuildExplicitKodikMediaSourceAsync(
        BaseItem item,
        KodikMediaSourceRequest request,
        string streamBase,
        IReadOnlyList<KodikTranslation> filteredTranslations,
        IReadOnlyList<KodikTranslation> allTranslations,
        int quality,
        CancellationToken cancellationToken)
    {
        var explicitName = BuildExplicitTranslationName(
            filteredTranslations,
            allTranslations,
            request.ExplicitTranslationId);

        var explicitRuntimeTicks = await ResolveRunTimeTicksAsync(
                request.IdType,
                request.Id,
                request.Episode,
                request.ExplicitTranslationId,
                quality,
                cancellationToken)
            .ConfigureAwait(false);

        return new[]
        {
            BuildSource(new MediaSourceBuildOptions
            {
                ItemId = item.Id.ToString(),
                Episode = request.Episode,
                Suffix = $"tr{request.ExplicitTranslationId}",
                Name = explicitName,
                Url = BuildStreamUrl(streamBase, request.ExplicitTranslationId),
                Container = "m3u8",
                SupportsDirectPlay = false,
                RunTimeTicks = explicitRuntimeTicks
            })
        };
    }

    private static List<MediaSourceInfo> BuildDefaultKodikMediaSources(
        string itemId,
        int episode,
        string streamBase,
        IReadOnlyList<KodikTranslation> translations,
        long? runTimeTicks)
    {
        var sources = new List<MediaSourceInfo>(8)
        {
            BuildSource(new MediaSourceBuildOptions
            {
                ItemId = itemId,
                Episode = episode,
                Suffix = "auto",
                Name = "Auto",
                Url = BuildStreamUrl(streamBase, translationId: null),
                Container = "m3u8",
                SupportsDirectPlay = false,
                RunTimeTicks = runTimeTicks
            })
        };

        foreach (var tr in translations)
        {
            var trId = GetPlayableTranslationId(tr, episode);
            if (trId == null)
            {
                continue;
            }

            sources.Add(BuildSource(new MediaSourceBuildOptions
            {
                ItemId = itemId,
                Episode = episode,
                Suffix = $"tr{trId}",
                Name = BuildTranslationLabel(tr),
                Url = BuildStreamUrl(streamBase, trId),
                Container = "m3u8",
                SupportsDirectPlay = false,
                RunTimeTicks = runTimeTicks
            }));
        }

        return sources;
    }

    private static string? GetPlayableTranslationId(KodikTranslation translation, int episode)
    {
        var trId = (translation.Id ?? string.Empty).Trim();
        if (trId.Length == 0 || string.Equals(trId, "0", StringComparison.Ordinal))
        {
            return null;
        }

        return translation.MaxEpisode > 0 && episode > translation.MaxEpisode ? null : trId;
    }

    private async Task<IEnumerable<MediaSourceInfo>> GetCvhMediaSourcesAsync(
        BaseItem item,
        YummyStreamRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Episode.HasValue || request.Episode.Value <= 0 || request.AnimeId <= 0)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var cfg = Plugin.Instance.Configuration;
        var baseUrl = _internalJellyfinUrlProvider.GetBaseUrl();

        var episode = request.Episode.Value;
        var catalog = await LoadYummyVideoCatalogAsync(cfg, request.AnimeId.ToString(), cancellationToken).ConfigureAwait(false);
        var explicitVoiceName = (request.VoiceName ?? string.Empty).Trim();
        var defaultVoiceName = catalog.PickPreferredVoiceName(
            episode,
            explicitVoiceName: string.Empty,
            savedVoiceName: string.Empty,
            preferredFilter: cfg.PreferredTranslationFilter,
            out _);

        if (!string.IsNullOrWhiteSpace(explicitVoiceName))
        {
            return new[]
            {
                BuildSource(new MediaSourceBuildOptions
                {
                    ItemId = item.Id.ToString(),
                    Episode = episode,
                    Suffix = "cvh-explicit",
                    Name = explicitVoiceName,
                    Url = YummyKodikStreamUri.BuildCvhHttpUrl(baseUrl, request.AnimeId, episode, explicitVoiceName) + HlsFormatQuery,
                    Container = "m3u8",
                    SupportsDirectPlay = false,
                    RunTimeTicks = ResolveYummyRunTimeTicks(
                        catalog,
                        YummyVideoProviderKind.Cvh,
                        episode,
                        explicitVoiceName)
                })
            };
        }

        var sources = new List<MediaSourceInfo>(8);
        sources.Add(BuildSource(new MediaSourceBuildOptions
        {
            ItemId = item.Id.ToString(),
            Episode = episode,
            Suffix = "cvh-auto",
            Name = "Auto",
            Url = YummyKodikStreamUri.BuildCvhHttpUrl(baseUrl, request.AnimeId, episode) + HlsFormatQuery,
            Container = "m3u8",
            SupportsDirectPlay = false,
            RunTimeTicks = ResolveYummyRunTimeTicks(
                catalog,
                YummyVideoProviderKind.Cvh,
                episode,
                defaultVoiceName)
        }));

        foreach (var voiceName in catalog.GetSupportedVoiceNames(episode))
        {
            sources.Add(BuildSource(new MediaSourceBuildOptions
            {
                ItemId = item.Id.ToString(),
                Episode = episode,
                Suffix = "cvh-" + SafeIdPart(voiceName),
                Name = voiceName,
                Url = YummyKodikStreamUri.BuildCvhHttpUrl(baseUrl, request.AnimeId, episode, voiceName) + HlsFormatQuery,
                Container = "m3u8",
                SupportsDirectPlay = false,
                RunTimeTicks = ResolveYummyRunTimeTicks(
                    catalog,
                    YummyVideoProviderKind.Cvh,
                    episode,
                    voiceName)
            }));
        }

        _logger.LogInformation(
            "Provided {Count} CVH media sources for '{ItemName}' (episode {Episode}).",
            sources.Count,
            item.Name,
            episode);

        return sources;
    }

    private async Task<IEnumerable<MediaSourceInfo>> GetAllohaMediaSourcesAsync(
        BaseItem item,
        YummyStreamRequest request,
        CancellationToken cancellationToken)
    {
        if (!request.Episode.HasValue || request.Episode.Value <= 0 || request.AnimeId <= 0)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var cfg = Plugin.Instance.Configuration;
        var baseUrl = _internalJellyfinUrlProvider.GetBaseUrl();

        var episode = request.Episode.Value;
        var catalog = await LoadYummyVideoCatalogAsync(cfg, request.AnimeId.ToString(), cancellationToken).ConfigureAwait(false);
        var explicitVoiceName = (request.VoiceName ?? string.Empty).Trim();
        var embeddedSource = TryGetEmbeddedAllohaSource(request, out var directSource) ? directSource : null;
        var chosenVoiceName = catalog.PickPreferredVoiceName(
            YummyVideoProviderKind.Alloha,
            episode,
            explicitVoiceName,
            savedVoiceName: string.Empty,
            preferredFilter: cfg.PreferredTranslationFilter,
            out _);
        var chosenEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, episode, chosenVoiceName)
                          ?? catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, episode);

        if (chosenEntry?.Alloha == null)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var voiceLabel = ResolveAllohaVoiceLabel(explicitVoiceName, chosenEntry.DisplayVoiceName);
        var runTimeTicks = ToRunTimeTicks(chosenEntry.DurationSeconds)
                           ?? ResolveYummyRunTimeTicks(
                               catalog,
                               YummyVideoProviderKind.Alloha,
                               episode,
                               chosenEntry.DisplayVoiceName);

        var url =
            $"{baseUrl}/YummyKodik/stream?provider={YummyKodikStreamUri.AllohaProvider}" +
            $"&animeId={request.AnimeId}&ep={episode}";

        if (!string.IsNullOrWhiteSpace(explicitVoiceName))
        {
            url = YummyKodikStreamUri.BuildAllohaHttpUrl(baseUrl, request.AnimeId, episode, explicitVoiceName, embeddedSource);
        }

        return new[]
        {
            BuildSource(new MediaSourceBuildOptions
            {
                ItemId = item.Id.ToString(),
                Episode = episode,
                Suffix = "alloha-" + SafeIdPart(voiceLabel),
                Name = voiceLabel,
                Url = url + HlsFormatQuery,
                Container = "m3u8",
                SupportsDirectPlay = false,
                RunTimeTicks = runTimeTicks,
                SupportsProbing = !runTimeTicks.HasValue
            })
        };
    }

    private static string ResolveAllohaVoiceLabel(string explicitVoiceName, string? displayVoiceName)
    {
        if (!string.IsNullOrWhiteSpace(explicitVoiceName))
        {
            return explicitVoiceName;
        }

        return !string.IsNullOrWhiteSpace(displayVoiceName) ? displayVoiceName : "Auto";
    }

    private static MediaSourceInfo BuildSource(MediaSourceBuildOptions options)
    {
        return YummyKodikMediaSourceFactory.Build(options);
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

        if (type.Equals(TranslationTypeVoice, StringComparison.OrdinalIgnoreCase))
        {
            return name;
        }

        if (type.Equals(TranslationTypeSubtitles, StringComparison.OrdinalIgnoreCase))
        {
            return $"{name} [subs]";
        }

        return $"{name} [{type}]";
    }

    private static List<KodikTranslation> FilterAndOrderTranslations(
        IReadOnlyList<KodikTranslation> translations,
        string? preferredFilter)
    {
        translations ??= Array.Empty<KodikTranslation>();

        var tokens = StringTokenParser.ParseTokens(preferredFilter);

        // Prefer voice translations in the list. If none, use everything.
        var voice = translations
            .Where(t => string.Equals(t.Type, TranslationTypeVoice, StringComparison.OrdinalIgnoreCase))
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
            string.Equals(t.Type, TranslationTypeVoice, StringComparison.OrdinalIgnoreCase) &&
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
        var normalizedId = (id ?? string.Empty).Trim();
        if (normalizedId.Length == 0)
        {
            return Array.Empty<KodikTranslation>();
        }

        var key = $"{idType.ToString().ToLowerInvariant()}:{normalizedId}".ToLowerInvariant();

        if (TranslationCache.TryGetValue(key, out var cached) && cached.ExpiresAtUtc > DateTime.UtcNow)
        {
            return cached.Translations;
        }

        var loaded = await LoadTranslationsAsync(idType, normalizedId, cancellationToken).ConfigureAwait(false);

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
                client => client.GetAnimeInfoAsync(id, idType, cancellationToken),
                cancellationToken)
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
                    client => client.GetEpisodeRuntimeAsync(id, idType, episode, normalizedTranslationId, quality, cancellationToken),
                    cancellationToken)
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
            return streamBase + HlsFormatQuery;
        }

        return streamBase + $"&tr={Uri.EscapeDataString(trId)}{HlsFormatQuery}";
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

    private static string SafeIdPart(string value)
    {
        var chars = value
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == '_')
            .ToArray();

        return chars.Length == 0 ? TranslationTypeVoice : new string(chars);
    }

    private static long? ToRunTimeTicks(int? durationSeconds)
    {
        return durationSeconds.HasValue && durationSeconds.Value > 0
            ? TimeSpan.FromSeconds(durationSeconds.Value).Ticks
            : null;
    }

    private static long? ResolveYummyRunTimeTicks(
        YummyVideoCatalog catalog,
        YummyVideoProviderKind preferredProvider,
        int episode,
        string? preferredVoiceName)
    {
        return ToRunTimeTicks(
            YummyEpisodeRuntimeResolver.ResolveDurationSeconds(
                catalog,
                preferredProvider,
                episode,
                preferredVoiceName));
    }

    private static bool TryGetEmbeddedAllohaSource(YummyStreamRequest request, out YummyAllohaSource source)
    {
        source = null!;

        if (request == null ||
            request.Provider != YummyStreamProviderKind.Alloha ||
            request.AnimeId <= 0 ||
            !request.Episode.HasValue ||
            request.Episode.Value <= 0 ||
            string.IsNullOrWhiteSpace(request.AllohaMovieToken) ||
            string.IsNullOrWhiteSpace(request.AllohaRequestToken) ||
            request.AllohaTranslationId <= 0 ||
            request.AllohaSeasonNumber <= 0 ||
            string.IsNullOrWhiteSpace(request.AllohaRefererUrl))
        {
            return false;
        }

        source = new YummyAllohaSource
        {
            MovieToken = request.AllohaMovieToken,
            RequestToken = request.AllohaRequestToken,
            TranslationId = request.AllohaTranslationId,
            SeasonNumber = request.AllohaSeasonNumber,
            EpisodeNumber = request.Episode.Value,
            Hidden = request.AllohaHidden,
            RefererUrl = request.AllohaRefererUrl
        };

        return true;
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

    private async Task PublishResolvedRunTimeAsync(
        BaseItem item,
        IReadOnlyList<MediaSourceInfo> sources,
        CancellationToken cancellationToken)
    {
        var resolvedRunTimeTicks = sources
            .Select(source => source.RunTimeTicks)
            .FirstOrDefault(value =>
                value.HasValue &&
                value.Value >= TimeSpan.FromMinutes(1).Ticks);
        if (!resolvedRunTimeTicks.HasValue)
        {
            return;
        }

        if (MediaRunTimePolicy.ShouldPublishAuthoritative(item.RunTimeTicks, resolvedRunTimeTicks))
        {
            item.RunTimeTicks = resolvedRunTimeTicks;
            try
            {
                await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    ex,
                    "Failed to persist resolved runtime for item={ItemId}. Playback will continue with the media-source runtime.",
                    item.Id);
            }
        }

        await PersistEpisodeRunTimeToNfoAsync(item, resolvedRunTimeTicks.Value, cancellationToken).ConfigureAwait(false);
    }

    private BaseItem ResolveMergedPrimaryVersion(BaseItem item)
    {
        if (IsMergedPrimaryVersion(item))
        {
            return item;
        }

        var targets = FindEpisodeVersionTargets(item);
        if (!MediaRunTimePolicy.HasExplicitSeriesSelection(
                Plugin.Instance.Configuration,
                targets.SelectMany(EnumerateSeriesPreferenceKeys)))
        {
            return item;
        }

        return targets.FirstOrDefault(IsMergedPrimaryVersion) ?? item;
    }

    private static bool IsMergedPrimaryVersion(BaseItem item)
    {
        return item is Video video &&
               string.IsNullOrWhiteSpace(video.PrimaryVersionId) &&
               video.LinkedAlternateVersions != null &&
               video.LinkedAlternateVersions.Length > 0;
    }

    private static IEnumerable<string> EnumerateSeriesPreferenceKeys(BaseItem item)
    {
        if (!TryGetLogicalUri(item, out var uri))
        {
            yield break;
        }

        if (YummyKodikStreamUri.TryParseRequest(uri, out var request))
        {
            if (request.Provider == YummyStreamProviderKind.Kodik &&
                !string.IsNullOrWhiteSpace(request.KodikId))
            {
                yield return KodikPlaybackSelector.BuildSeriesKey(
                    request.KodikIdType,
                    request.KodikId);
                yield break;
            }

            if (request.AnimeId > 0 &&
                request.Provider is YummyStreamProviderKind.Alloha or YummyStreamProviderKind.Cvh)
            {
                yield return $"yummy:{request.AnimeId}";
                yield return $"alloha:{request.AnimeId}";
                yield return $"cvh:{request.AnimeId}";
            }

            yield break;
        }

        if (TryParseLogicalUri(uri, out var idType, out var id, out _, out _))
        {
            yield return KodikPlaybackSelector.BuildSeriesKey(idType, id);
        }
    }

    private IReadOnlyList<BaseItem> FindEpisodeVersionTargets(BaseItem item)
    {
        if (item is not Episode ||
            string.IsNullOrWhiteSpace(item.PresentationUniqueKey) ||
            string.IsNullOrWhiteSpace(item.Path))
        {
            return new[] { item };
        }

        var sourceDirectory = Path.GetDirectoryName(item.Path);
        if (string.IsNullOrWhiteSpace(sourceDirectory))
        {
            return new[] { item };
        }

        try
        {
            var targets = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    PresentationUniqueKey = item.PresentationUniqueKey,
                    Recursive = true
                })
                .OfType<Episode>()
                .Where(candidate => MediaRunTimePolicy.IsSiblingEpisodeVersion(
                    item.PresentationUniqueKey,
                    sourceDirectory,
                    candidate.PresentationUniqueKey,
                    candidate.Path))
                .GroupBy(candidate => candidate.Id)
                .Select(group => (BaseItem)group.First())
                .ToList();

            if (targets.All(candidate => candidate.Id != item.Id))
            {
                targets.Add(item);
            }

            return targets;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Failed to enumerate sibling episode versions while resolving merged primary for item={ItemId}.",
                item.Id);
            return new[] { item };
        }
    }

    private async Task PersistEpisodeRunTimeToNfoAsync(
        BaseItem item,
        long runTimeTicks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(item.Path) ||
            !item.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var durationTotalSeconds = Math.Round(
            TimeSpan.FromTicks(runTimeTicks).TotalSeconds,
            MidpointRounding.AwayFromZero);
        if (durationTotalSeconds <= 0 || durationTotalSeconds > int.MaxValue)
        {
            return;
        }

        var nfoPath = Path.ChangeExtension(item.Path, ".nfo");
        if (!File.Exists(nfoPath))
        {
            return;
        }

        try
        {
            var xml = await File.ReadAllTextAsync(nfoPath, cancellationToken).ConfigureAwait(false);
            if (!RefreshFileWriter.IsValidXmlContent(xml))
            {
                return;
            }

            var enrichedXml = NfoBuilder.EnsureEpisodeRuntime(xml, (int)durationTotalSeconds);
            if (string.Equals(xml, enrichedXml, StringComparison.Ordinal))
            {
                return;
            }

            await RefreshFileWriter.WriteTextAtomicallyAsync(
                    nfoPath,
                    enrichedXml,
                    perf: null,
                    artifactKind: "nfo.runtime.playback",
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (
            !cancellationToken.IsCancellationRequested &&
            ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(
                ex,
                "Failed to persist resolved playback runtime to episode NFO. path='{Path}'",
                nfoPath);
        }
    }

    private static async Task<string> ResolveKodikTokenAsync(HttpClient http, PluginConfiguration cfg, CancellationToken ct)
    {
        var configured = (cfg.KodikToken ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        return await KodikTokenProvider.GetTokenAsync(http, cancellationToken: ct).ConfigureAwait(false);
    }

    private static async Task<(KodikClient Client, T Result)> ExecuteWithAutoTokenRefreshAsync<T>(
        KodikClient client,
        HttpClient http,
        PluginConfiguration cfg,
        Func<KodikClient, Task<T>> action,
        CancellationToken ct)
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
                    forceRefresh: true,
                    allowStaleOnFailure: false,
                    cancellationToken: ct)
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

    private sealed record KodikMediaSourceRequest(
        KodikIdType IdType,
        string Id,
        int Episode,
        string ExplicitTranslationId);
}
