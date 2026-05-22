// File: Api/YummyKodikStreamController.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using YummyKodik.Alloha;
using YummyKodik.Cvh;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Api
{
    [ApiController]
    public sealed class YummyKodikStreamController : ControllerBase
    {
        private readonly ILogger<YummyKodikStreamController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly ILibraryManager _libraryManager;
        private readonly AllohaPlaybackService _allohaPlaybackService;

        private static readonly YummyVideoProviderKind[] AllohaFallbackProviderOrder =
        {
            YummyVideoProviderKind.Cvh
        };

        private static readonly YummyVideoProviderKind[] CvhFallbackProviderOrder =
        {
            YummyVideoProviderKind.Alloha
        };

        private static readonly YummyVideoProviderKind[] YummyVoiceProviderOrder =
        {
            YummyVideoProviderKind.Alloha,
            YummyVideoProviderKind.Cvh
        };

        private static readonly object PrefsLock = new();

        private readonly record struct KodikStreamSelection(string TranslationId, bool WaitIfMissing, string Reason);

        private readonly record struct KodikLinkAttempt(KodikClient Client, KodikLinkInfo? Link, Exception? Error);

        private sealed record AllohaEmbeddedSourceRequest(
            string? MovieToken,
            string? RequestToken,
            int TranslationId,
            int Season,
            string? Hidden,
            string? RefererUrl);

        private sealed record AllohaStreamRequest(
            PluginConfiguration Cfg,
            Guid UserId,
            int Episode,
            string? TranslationId,
            long? AnimeId,
            string? VoiceName,
            AllohaEmbeddedSourceRequest EmbeddedSource,
            string? SessionId,
            int Quality,
            string? Format,
            CancellationToken CancellationToken);

        private sealed record AllohaSessionRequest(
            PluginConfiguration Cfg,
            Guid UserId,
            long AnimeId,
            int Episode,
            string? RequestedVoice,
            int Quality,
            CancellationToken CancellationToken);

        private sealed record CvhStreamRequest(
            PluginConfiguration Cfg,
            Guid UserId,
            int Episode,
            string? TranslationId,
            long? AnimeId,
            string? VoiceName,
            int Quality,
            string? Format,
            CancellationToken CancellationToken);

        private sealed record KodikStreamRequest(
            PluginConfiguration Cfg,
            Guid UserId,
            string? Type,
            string? Id,
            int Episode,
            string? TranslationId,
            int Quality,
            string? Format,
            CancellationToken CancellationToken);

        private sealed record KodikLinkRequest(
            KodikClient Kodik,
            HttpClient Http,
            PluginConfiguration Cfg,
            KodikIdType IdType,
            string Id,
            int Episode,
            string TranslationId,
            CancellationToken CancellationToken);

        private sealed record KodikTranslationFallbackRequest(
            KodikClient Kodik,
            HttpClient Http,
            PluginConfiguration Cfg,
            IReadOnlyList<KodikTranslation> Translations,
            string[] PreferredTokens,
            KodikIdType IdType,
            string Id,
            int Episode,
            KodikStreamSelection Selection,
            Exception? LastUpstreamError,
            bool LogSuccess,
            CancellationToken CancellationToken);

        private sealed record MissingKodikStreamLinkRequest(
            PluginConfiguration Cfg,
            Guid UserId,
            KodikIdType IdType,
            string Id,
            int Episode,
            string ExplicitTranslationId,
            int Quality,
            string? Format,
            KodikStreamSelection Selection,
            Exception? LastUpstreamError,
            CancellationToken CancellationToken);

        private sealed record KodikRedirectRequest(
            PluginConfiguration Cfg,
            Guid UserId,
            KodikIdType IdType,
            string Id,
            int Episode,
            string ExplicitTranslationId,
            int Quality,
            string? Format,
            string SeriesKey,
            KodikStreamSelection Selection,
            KodikLinkInfo Link);

        private sealed record KodikFallbackAfterFailureRequest(
            Guid UserId,
            string? Type,
            string? Id,
            int Episode,
            string? TranslationId,
            string? Provider,
            string? Format,
            Exception OriginalError,
            bool AllowProviderSpecified,
            CancellationToken CancellationToken);

        private readonly record struct KodikFallbackLinkAttempt(
            KodikClient Client,
            KodikLinkInfo? Link,
            Exception? Error,
            KodikStreamSelection Selection);

        public YummyKodikStreamController(
            ILogger<YummyKodikStreamController> logger,
            IHttpClientFactory httpClientFactory,
            IAuthorizationContext authorizationContext,
            ILibraryManager libraryManager,
            AllohaPlaybackService allohaPlaybackService)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _authorizationContext = authorizationContext;
            _libraryManager = libraryManager;
            _allohaPlaybackService = allohaPlaybackService;
        }

        [Authorize]
        [HttpGet("YummyKodik/getTranslations")]
        public async Task<IActionResult> GetTranslations(
            [FromQuery] string seriesId,
            CancellationToken cancellationToken = default)
        {
            var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
            if (!auth.IsAuthenticated || auth.UserId == Guid.Empty)
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(seriesId))
            {
                return BadRequest("seriesId is required");
            }

            try
            {
                var cfg = Plugin.Instance.Configuration;
                var request = await ResolveSeriesFromJellyfinAsync(seriesId, cancellationToken).ConfigureAwait(false);
                var seriesKey = BuildSeriesKey(request);

                if (TryMapCatalogProvider(request.Provider, out var catalogProvider, out var providerId) &&
                    request.AnimeId > 0)
                {
                    var catalog = await LoadYummyVideoCatalogAsync(cfg, request.AnimeId.ToString(), cancellationToken).ConfigureAwait(false);
                    var firstEpisode = catalog.GetFirstSupportedEpisodeNumber(catalogProvider) ?? 1;
                    var savedVoice = GetSavedYummyVoiceName(cfg, auth.UserId, request.AnimeId, request.Provider);
                    var chosenVoice = catalog.PickPreferredVoiceName(
                        catalogProvider,
                        firstEpisode,
                        explicitVoiceName: string.Empty,
                        savedVoiceName: savedVoice,
                        preferredFilter: cfg.PreferredTranslationFilter,
                        out var providerReason);

                    return Ok(new
                    {
                        seriesKey,
                        idType = providerId,
                        id = request.AnimeId.ToString(),
                        savedTranslationId = savedVoice ?? string.Empty,
                        chosenTranslationId = chosenVoice ?? string.Empty,
                        reason = providerReason,
                        translations = catalog.GetAllVoiceNamesAcrossProviders(YummyVoiceProviderOrder)
                            .Select(x => new
                            {
                                id = x,
                                name = x,
                                type = "voice"
                            })
                            .ToArray()
                    });
                }

                var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
                var token = await ResolveKodikTokenAsync(http, cfg, cancellationToken).ConfigureAwait(false);
                var kodik = new KodikClient(http, token);

                var infoRes = await ExecuteWithAutoTokenRefreshAsync(
                        kodik,
                        http,
                        cfg,
                        k => k.GetAnimeInfoAsync(request.KodikId, request.KodikIdType, cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);

                var info = infoRes.Result;

                var preferredTokens = StringTokenParser.ParseTokens(cfg.PreferredTranslationFilter);
                var savedTrId = cfg.GetUserSeriesPreferredTranslationId(auth.UserId, seriesKey);

                // GetTranslations has no episode, pick "episode 1" as a stable default for coverage checks.
                var (chosenTrId, _, reason) = KodikPlaybackSelector.PickTranslationForPlayback(
                    info.Translations,
                    preferredTokens,
                    savedTrId,
                    explicitTranslationId: string.Empty,
                    episode: 1);

                if (string.IsNullOrWhiteSpace(chosenTrId))
                {
                    chosenTrId = "0";
                }

                return Ok(new
                {
                    seriesKey,
                    idType = request.KodikIdType.ToString().ToLowerInvariant(),
                    id = request.KodikId,
                    savedTranslationId = savedTrId ?? string.Empty,
                    chosenTranslationId = chosenTrId,
                    reason,
                    translations = info.Translations.Select(t => new
                    {
                        id = (t.Id ?? string.Empty).Trim(),
                        name = (t.Name ?? string.Empty).Trim(),
                        type = (t.Type ?? string.Empty).Trim()
                    }).ToArray()
                });
            }
            catch (KodikTokenException ex)
            {
                _logger.LogWarning(ex, "GetTranslations failed due to Kodik token. seriesId={SeriesId}", seriesId);
                return StatusCode(503, "Kodik token is missing or invalid. Configure KodikToken in plugin settings.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetTranslations failed. seriesId={SeriesId}", seriesId);
                return StatusCode(500, "Failed to resolve translations");
            }
        }

        [Authorize]
        [HttpGet("YummyKodik/setTranslation")]
        public async Task<IActionResult> SetTranslation(
            [FromQuery] string seriesId,
            [FromQuery] string? tr = null,
            CancellationToken cancellationToken = default)
        {
            var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
            if (!auth.IsAuthenticated || auth.UserId == Guid.Empty)
            {
                return Unauthorized();
            }

            if (string.IsNullOrWhiteSpace(seriesId))
            {
                return BadRequest("seriesId is required");
            }

            try
            {
                var cfg = Plugin.Instance.Configuration;
                var request = await ResolveSeriesFromJellyfinAsync(seriesId, cancellationToken).ConfigureAwait(false);
                var seriesKey = BuildSeriesKey(request);
                var tid = (tr ?? string.Empty).Trim();

                bool changed;
                lock (PrefsLock)
                {
                    if (IsYummyProviderRequest(request))
                    {
                        changed = SetYummyVoicePreference(cfg, auth.UserId, request.AnimeId, request.Provider, tid);
                    }
                    else
                    {
                        var translationId = string.IsNullOrWhiteSpace(tid) ? null : tid;
                        changed = cfg.SetUserSeriesPreferredTranslationId(auth.UserId, seriesKey, translationId);
                    }
                    if (changed)
                    {
                        Plugin.Instance.SaveConfiguration();
                    }
                }

                return Ok(new
                {
                    changed,
                    seriesKey,
                    translationId = tid
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SetTranslation failed. seriesId={SeriesId} tr={Tr}", seriesId, tr);
                return StatusCode(500, "Failed to save translation");
            }
        }

        [AllowAnonymous]
        [HttpGet("YummyKodik/stream")]
        public async Task<IActionResult> Stream(
            [FromQuery] string? type = null,
            [FromQuery] string? id = null,
            [FromQuery] int ep = 0,
            [FromQuery] string? tr = null,
            [FromQuery] string? provider = null,
            [FromQuery] long? animeId = null,
            [FromQuery] string? voice = null,
            [FromQuery] string? allohaMovieToken = null,
            [FromQuery] string? allohaRequestToken = null,
            [FromQuery] int allohaTranslationId = 0,
            [FromQuery] int allohaSeason = 0,
            [FromQuery] string? allohaHidden = null,
            [FromQuery] string? allohaRefererUrl = null,
            [FromQuery] string? sessionId = null,
            [FromQuery] string? format = null,
            CancellationToken cancellationToken = default)
        {
            if (ep <= 0)
            {
                return BadRequest("ep is required (ep must be > 0)");
            }

            var userId = await GetOptionalStreamingUserIdAsync().ConfigureAwait(false);

            try
            {
                var cfg = Plugin.Instance.Configuration;
                var quality = GetPreferredQuality(cfg);
                var providerValue = (provider ?? string.Empty).Trim();

                if (IsStreamProvider(providerValue, YummyKodikStreamUri.AllohaProvider))
                {
                    return await ResolveAllohaStreamRequestAsync(
                            new AllohaStreamRequest(
                                cfg,
                                userId,
                                ep,
                                tr,
                                animeId,
                                voice,
                                new AllohaEmbeddedSourceRequest(
                                    allohaMovieToken,
                                    allohaRequestToken,
                                    allohaTranslationId,
                                    allohaSeason,
                                    allohaHidden,
                                    allohaRefererUrl),
                                sessionId,
                                quality,
                                format,
                                cancellationToken))
                        .ConfigureAwait(false);
                }

                if (IsStreamProvider(providerValue, YummyKodikStreamUri.CvhProvider))
                {
                    return await ResolveCvhStreamRequestAsync(
                            new CvhStreamRequest(cfg, userId, ep, tr, animeId, voice, quality, format, cancellationToken))
                        .ConfigureAwait(false);
                }

                return await ResolveKodikStreamRequestAsync(
                        new KodikStreamRequest(cfg, userId, type, id, ep, tr, quality, format, cancellationToken))
                    .ConfigureAwait(false);
            }
            catch (KodikTokenException ex)
            {
                _logger.LogWarning(ex, "YummyKodik stream failed due to Kodik token. type={Type} id={Id} ep={Ep}", type, id, ep);
                var yummyKodikFallback = await TryResolveKodikStreamFallbackAfterFailureAsync(
                        new KodikFallbackAfterFailureRequest(
                            userId,
                            type,
                            id,
                            ep,
                            tr,
                            provider,
                            format,
                            ex,
                            AllowProviderSpecified: true,
                            cancellationToken))
                    .ConfigureAwait(false);
                if (yummyKodikFallback != null)
                {
                    return yummyKodikFallback;
                }

                return StatusCode(503, "Kodik token is missing or invalid. Configure KodikToken in plugin settings.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "YummyKodik stream failed. type={Type} id={Id} ep={Ep}", type, id, ep);
                var yummyKodikFallback = await TryResolveKodikStreamFallbackAfterFailureAsync(
                        new KodikFallbackAfterFailureRequest(
                            userId,
                            type,
                            id,
                            ep,
                            tr,
                            provider,
                            format,
                            ex,
                            AllowProviderSpecified: false,
                            cancellationToken))
                    .ConfigureAwait(false);
                if (yummyKodikFallback != null)
                {
                    return yummyKodikFallback;
                }

                return StatusCode(502, "Upstream error");
            }
        }

        private async Task<Guid> GetOptionalStreamingUserIdAsync()
        {
            try
            {
                var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                return auth.IsAuthenticated ? auth.UserId : Guid.Empty;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Ignoring auth parsing error for streaming.");
                return Guid.Empty;
            }
        }

        private async Task<IActionResult> ResolveAllohaStreamRequestAsync(AllohaStreamRequest request)
        {
            var requestedVoice = request.VoiceName ?? request.TranslationId;

            try
            {
                if (TryGetCachedAllohaSession(request.SessionId, out var cachedSession))
                {
                    return BuildAllohaManifestResult(cachedSession);
                }

                if (!TryGetPositiveAnimeId(request.AnimeId, out var resolvedAnimeId))
                {
                    return BadRequest("animeId is required for Alloha streams");
                }

                var session = await ResolveNewAllohaSessionForStreamAsync(
                        new AllohaSessionRequest(
                            request.Cfg,
                            request.UserId,
                            resolvedAnimeId,
                            request.Episode,
                            requestedVoice,
                            request.Quality,
                            request.CancellationToken),
                        request.EmbeddedSource)
                    .ConfigureAwait(false);

                return BuildAllohaManifestResult(session);
            }
            catch (Exception ex) when (CanTryYummyProviderFallback(request.AnimeId, ex))
            {
                var resolvedAnimeId = request.AnimeId.GetValueOrDefault();
                _logger.LogWarning(
                    ex,
                    "Alloha stream attempt failed, trying fallback providers. user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                    request.UserId,
                    resolvedAnimeId,
                    request.Episode,
                    requestedVoice);

                return await ResolveYummyFallbackStreamAsync(
                        request.Cfg,
                        request.UserId,
                        YummyStreamProviderKind.Alloha,
                        resolvedAnimeId,
                        request.Episode,
                        requestedVoice,
                        request.Quality,
                        request.Format,
                        ex,
                        request.CancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task<AllohaPlaybackSession> ResolveNewAllohaSessionForStreamAsync(
            AllohaSessionRequest request,
            AllohaEmbeddedSourceRequest embeddedSource)
        {
            if (!TryBuildDirectAllohaSource(
                    embeddedSource.MovieToken,
                    embeddedSource.RequestToken,
                    embeddedSource.TranslationId,
                    embeddedSource.Season,
                    request.Episode,
                    embeddedSource.Hidden,
                    embeddedSource.RefererUrl,
                    out var directSource))
            {
                return await ResolveAllohaSessionAsync(
                        request.Cfg,
                        request.UserId,
                        request.AnimeId,
                        request.Episode,
                        request.RequestedVoice,
                        request.Quality,
                        request.CancellationToken)
                    .ConfigureAwait(false);
            }

            return await ResolveDirectOrLiveAllohaSessionAsync(
                    request,
                    directSource)
                .ConfigureAwait(false);
        }

        private async Task<AllohaPlaybackSession> ResolveDirectOrLiveAllohaSessionAsync(
            AllohaSessionRequest request,
            YummyAllohaSource directSource)
        {
            try
            {
                var directSession = await _allohaPlaybackService.CreateSessionAsync(
                        directSource,
                        request.Quality,
                        request.RequestedVoice,
                        request.CancellationToken)
                    .ConfigureAwait(false);
                EnsureDirectAllohaSessionSupportsVoice(
                    request.UserId,
                    request.AnimeId,
                    request.Episode,
                    request.RequestedVoice,
                    directSource,
                    directSession);

                if (!string.IsNullOrWhiteSpace(request.RequestedVoice))
                {
                    TrySaveYummyVoicePreference(
                        request.Cfg,
                        request.UserId,
                        request.AnimeId,
                        YummyStreamProviderKind.Alloha,
                        request.RequestedVoice);
                }

                _logger.LogInformation(
                    "Alloha manifest prepared from embedded source: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} translationId={TranslationId}",
                    request.UserId,
                    request.AnimeId,
                    request.Episode,
                    request.RequestedVoice,
                    directSource.TranslationId);

                return directSession;
            }
            catch (Exception ex) when (IsYummyProviderFallbackException(ex))
            {
                _logger.LogWarning(
                    ex,
                    "Alloha embedded source failed, falling back to live resolution. user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} translationId={TranslationId}",
                    request.UserId,
                    request.AnimeId,
                    request.Episode,
                    request.RequestedVoice,
                    directSource.TranslationId);

                return await ResolveAllohaSessionAsync(
                        request.Cfg,
                        request.UserId,
                        request.AnimeId,
                        request.Episode,
                        request.RequestedVoice,
                        request.Quality,
                        request.CancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private void EnsureDirectAllohaSessionSupportsVoice(
            Guid userId,
            long animeId,
            int episode,
            string? requestedVoice,
            YummyAllohaSource directSource,
            AllohaPlaybackSession directSession)
        {
            if (AllohaSessionSupportsRequestedVoice(directSession, requestedVoice))
            {
                return;
            }

            _logger.LogWarning(
                "Alloha embedded source voice mismatch: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} translationId={TranslationId} selectedVoice={SelectedVoice} availableVoices={AvailableVoices}",
                userId,
                animeId,
                episode,
                requestedVoice,
                directSource.TranslationId,
                directSession.SelectedVoiceName,
                string.Join(", ", directSession.AvailableVoiceNames));

            throw new InvalidOperationException("Alloha embedded source voice mismatch.");
        }

        private async Task<IActionResult> ResolveCvhStreamRequestAsync(CvhStreamRequest request)
        {
            if (!TryGetPositiveAnimeId(request.AnimeId, out var resolvedAnimeId))
            {
                return BadRequest("animeId is required for CVH streams");
            }

            var requestedVoice = request.VoiceName ?? request.TranslationId;
            try
            {
                var catalog = await LoadYummyVideoCatalogAsync(
                        request.Cfg,
                        resolvedAnimeId.ToString(),
                        request.CancellationToken)
                    .ConfigureAwait(false);
                return await ResolveCvhStreamFromCatalogAsync(
                        request.Cfg,
                        request.UserId,
                        catalog,
                        resolvedAnimeId,
                        request.Episode,
                        requestedVoice,
                        request.Quality,
                        request.Format,
                        "primary",
                        request.CancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsYummyProviderFallbackException(ex))
            {
                _logger.LogWarning(
                    ex,
                    "CVH stream attempt failed, trying fallback providers. user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                    request.UserId,
                    resolvedAnimeId,
                    request.Episode,
                    requestedVoice);

                return await ResolveYummyFallbackStreamAsync(
                        request.Cfg,
                        request.UserId,
                        YummyStreamProviderKind.Cvh,
                        resolvedAnimeId,
                        request.Episode,
                        requestedVoice,
                        request.Quality,
                        request.Format,
                        ex,
                        request.CancellationToken)
                    .ConfigureAwait(false);
            }
        }

        private async Task<IActionResult> ResolveKodikStreamRequestAsync(KodikStreamRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Id))
            {
                return BadRequest("type and id are required for Kodik streams");
            }

            if (!Enum.TryParse<KodikIdType>(request.Type, true, out var idType))
            {
                return BadRequest("unknown type");
            }

            var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
            var token = await ResolveKodikTokenAsync(http, request.Cfg, request.CancellationToken).ConfigureAwait(false);
            var kodik = new KodikClient(http, token);
            var infoRes = await LoadKodikInfoAsync(kodik, http, request.Cfg, request.Id, idType, request.CancellationToken)
                .ConfigureAwait(false);

            kodik = infoRes.Client;
            var info = infoRes.Result;
            var seriesKey = KodikPlaybackSelector.BuildSeriesKey(idType, request.Id);
            var explicitTr = (request.TranslationId ?? string.Empty).Trim();
            var preferredTokens = StringTokenParser.ParseTokens(request.Cfg.PreferredTranslationFilter);
            var savedTrId = request.Cfg.GetUserSeriesPreferredTranslationId(request.UserId, seriesKey);
            var selection = PickKodikStreamSelection(info.Translations, preferredTokens, savedTrId, explicitTr, request.Episode);

            var linkAttempt = await TryResolveKodikEpisodeLinkAsync(
                    new KodikLinkRequest(
                        kodik,
                        http,
                        request.Cfg,
                        idType,
                        request.Id,
                        request.Episode,
                        selection.TranslationId,
                        request.CancellationToken))
                .ConfigureAwait(false);
            LogPrimaryKodikLinkFailure(linkAttempt.Error, idType, request.Id, request.Episode, selection);

            var link = linkAttempt.Link;
            kodik = linkAttempt.Client;
            var lastUpstreamError = linkAttempt.Error;

            if (ShouldTryKodikTranslationFallback(link, explicitTr, selection))
            {
                var fallbackAttempt = await TryResolveKodikTranslationFallbackAsync(
                        new KodikTranslationFallbackRequest(
                            kodik,
                            http,
                            request.Cfg,
                            info.Translations,
                            preferredTokens,
                            idType,
                            request.Id,
                            request.Episode,
                            selection,
                            lastUpstreamError,
                            LogSuccess: true,
                            request.CancellationToken))
                    .ConfigureAwait(false);

                link = fallbackAttempt.Link;
                selection = fallbackAttempt.Selection;
                lastUpstreamError = fallbackAttempt.Error;
            }

            if (link == null)
            {
                return await ResolveMissingKodikStreamLinkAsync(
                        new MissingKodikStreamLinkRequest(
                            request.Cfg,
                            request.UserId,
                            idType,
                            request.Id,
                            request.Episode,
                            explicitTr,
                            request.Quality,
                            request.Format,
                            selection,
                            lastUpstreamError,
                            request.CancellationToken))
                    .ConfigureAwait(false);
            }

            return RedirectKodikStream(
                new KodikRedirectRequest(
                    request.Cfg,
                    request.UserId,
                    idType,
                    request.Id,
                    request.Episode,
                    explicitTr,
                    request.Quality,
                    request.Format,
                    seriesKey,
                    selection,
                    link));
        }

        private static async Task<(KodikClient Client, KodikAnimeInfo Result)> LoadKodikInfoAsync(
            KodikClient kodik,
            HttpClient http,
            PluginConfiguration cfg,
            string id,
            KodikIdType idType,
            CancellationToken cancellationToken)
        {
            return await ExecuteWithAutoTokenRefreshAsync(
                    kodik,
                    http,
                    cfg,
                    k => k.GetAnimeInfoAsync(id, idType, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        private static KodikStreamSelection PickKodikStreamSelection(
            IReadOnlyList<KodikTranslation> translations,
            string[] preferredTokens,
            string? savedTrId,
            string explicitTr,
            int episode)
        {
            var (chosenTrId, waitIfMissing, reason) = KodikPlaybackSelector.PickTranslationForPlayback(
                translations,
                preferredTokens,
                savedTrId,
                explicitTr,
                episode);

            return NormalizeKodikStreamSelection(chosenTrId, waitIfMissing, reason);
        }

        private static KodikStreamSelection PickKodikFallbackSelection(
            IReadOnlyList<KodikTranslation> translations,
            string[] preferredTokens,
            string? savedTrId,
            string requestedVoice,
            string savedYummyVoice,
            int episode)
        {
            if (TryPickExplicitKodikFallbackSelection(translations, requestedVoice, episode, out var explicitSelection))
            {
                return explicitSelection;
            }

            if (TryPickSavedYummyVoiceKodikFallbackSelection(translations, savedYummyVoice, episode, out var savedSelection))
            {
                return savedSelection;
            }

            var (chosenTrId, waitIfMissing, reason) = KodikPlaybackSelector.PickTranslationForPlayback(
                translations,
                preferredTokens,
                savedTrId,
                explicitTranslationId: string.Empty,
                episode);

            return NormalizeKodikStreamSelection(chosenTrId, waitIfMissing, reason);
        }

        private static bool TryPickExplicitKodikFallbackSelection(
            IReadOnlyList<KodikTranslation> translations,
            string requestedVoice,
            int episode,
            out KodikStreamSelection selection)
        {
            selection = default;
            if (string.IsNullOrWhiteSpace(requestedVoice))
            {
                return false;
            }

            var translation = FindKodikTranslationByVoiceName(translations, requestedVoice, episode);
            if (translation == null || string.IsNullOrWhiteSpace(translation.Id))
            {
                throw new InvalidOperationException("Requested Kodik fallback translation is unavailable from upstream.");
            }

            selection = new KodikStreamSelection(translation.Id.Trim(), true, "fallback-explicit-voice");
            return true;
        }

        private static bool TryPickSavedYummyVoiceKodikFallbackSelection(
            IReadOnlyList<KodikTranslation> translations,
            string savedYummyVoice,
            int episode,
            out KodikStreamSelection selection)
        {
            selection = default;
            if (string.IsNullOrWhiteSpace(savedYummyVoice))
            {
                return false;
            }

            var translation = FindKodikTranslationByVoiceName(translations, savedYummyVoice, episode);
            if (translation == null || string.IsNullOrWhiteSpace(translation.Id))
            {
                return false;
            }

            selection = new KodikStreamSelection(translation.Id.Trim(), true, "fallback-saved-yummy-voice");
            return true;
        }

        private static KodikStreamSelection NormalizeKodikStreamSelection(
            string? translationId,
            bool waitIfMissing,
            string reason)
        {
            var normalizedTranslationId = string.IsNullOrWhiteSpace(translationId)
                ? "0"
                : translationId.Trim();

            return new KodikStreamSelection(normalizedTranslationId, waitIfMissing, reason);
        }

        private static async Task<KodikLinkAttempt> TryResolveKodikEpisodeLinkAsync(KodikLinkRequest request)
        {
            try
            {
                var linkRes = await ExecuteWithAutoTokenRefreshAsync(
                        request.Kodik,
                        request.Http,
                        request.Cfg,
                        k => k.GetEpisodeLinkAsync(
                            request.Id,
                            request.IdType,
                            request.Episode,
                            request.TranslationId,
                            request.CancellationToken),
                        request.CancellationToken)
                    .ConfigureAwait(false);

                return new KodikLinkAttempt(linkRes.Client, linkRes.Result, null);
            }
            catch (Exception ex) when (IsRetryableKodikLinkException(ex))
            {
                return new KodikLinkAttempt(request.Kodik, null, ex);
            }
        }

        private async Task<KodikFallbackLinkAttempt> TryResolveKodikTranslationFallbackAsync(
            KodikTranslationFallbackRequest request)
        {
            var currentClient = request.Kodik;
            var currentError = request.LastUpstreamError;

            foreach (var fallbackTrId in KodikPlaybackSelector.BuildFallbackTranslationCandidates(
                         request.Translations,
                         request.PreferredTokens,
                         request.Selection.TranslationId,
                         request.Episode))
            {
                var attempt = await TryResolveKodikEpisodeLinkAsync(
                        new KodikLinkRequest(
                            currentClient,
                            request.Http,
                            request.Cfg,
                            request.IdType,
                            request.Id,
                            request.Episode,
                            fallbackTrId,
                            request.CancellationToken))
                    .ConfigureAwait(false);

                currentClient = attempt.Client;
                if (attempt.Link != null)
                {
                    if (request.LogSuccess)
                    {
                        _logger.LogInformation(
                            "Fallback translation succeeded. type={Type} id={Id} ep={Ep} from={FromTr} to={ToTr} reason={Reason}",
                            request.IdType,
                            request.Id,
                            request.Episode,
                            request.Selection.TranslationId,
                            fallbackTrId,
                            request.Selection.Reason);
                    }

                    var fallbackSelection = new KodikStreamSelection(
                        fallbackTrId,
                        false,
                        request.Selection.Reason + "+fallback");

                    return new KodikFallbackLinkAttempt(currentClient, attempt.Link, null, fallbackSelection);
                }

                currentError = attempt.Error ?? currentError;
                LogKodikFallbackTranslationFailure(
                    attempt.Error,
                    request.IdType,
                    request.Id,
                    request.Episode,
                    fallbackTrId);
            }

            return new KodikFallbackLinkAttempt(currentClient, null, currentError, request.Selection);
        }

        private async Task<IActionResult> ResolveMissingKodikStreamLinkAsync(MissingKodikStreamLinkRequest request)
        {
            var yummyKodikFallback = await TryResolveKodikStreamFromYummyIframeAsync(
                    request.Cfg,
                    request.UserId,
                    request.IdType,
                    request.Id,
                    request.Episode,
                    request.ExplicitTranslationId,
                    request.Quality,
                    request.Format,
                    request.LastUpstreamError,
                    request.CancellationToken)
                .ConfigureAwait(false);
            if (yummyKodikFallback != null)
            {
                return yummyKodikFallback;
            }

            LogAllKodikLinkAttemptsFailed(
                request.LastUpstreamError,
                request.IdType,
                request.Id,
                request.Episode,
                request.Selection);
            if (request.Selection.WaitIfMissing)
            {
                SetNoStoreCacheHeader();
                Response.Headers.RetryAfter = "3600";
                return StatusCode(503, "Preferred translation exists but is not available for this episode yet.");
            }

            return StatusCode(502, "Upstream error");
        }

        private RedirectResult RedirectKodikStream(KodikRedirectRequest request)
        {
            var fmt = (request.Format ?? "mp4").Trim().ToLowerInvariant();
            var targetUrl = fmt == "hls"
                ? KodikClient.BuildHlsUrl(request.Link, request.Quality)
                : KodikClient.BuildMp4Url(request.Link, request.Quality);

            SetNoStoreCacheHeader();

            if (!string.IsNullOrWhiteSpace(request.ExplicitTranslationId))
            {
                TrySaveTranslationId(
                    request.Cfg,
                    request.UserId,
                    request.SeriesKey,
                    request.ExplicitTranslationId);
            }

            _logger.LogInformation(
                "Stream redirect: user={UserId} type={Type} id={Id} ep={Ep} tr={TrId} reason={Reason} -> {Url}",
                request.UserId,
                request.IdType,
                request.Id,
                request.Episode,
                request.Selection.TranslationId,
                request.Selection.Reason,
                targetUrl);

            return Redirect(targetUrl);
        }

        private static KodikLinkInfo RequireKodikFallbackLink(
            KodikLinkInfo? link,
            KodikStreamSelection selection,
            Exception? lastUpstreamError)
        {
            if (link != null)
            {
                return link;
            }

            throw new InvalidOperationException(
                $"Kodik fallback link is unavailable for translation '{selection.TranslationId}' ({selection.Reason}).",
                lastUpstreamError);
        }

        private RedirectResult RedirectKodikFallbackStream(KodikRedirectRequest request, string requestedVoice)
        {
            _logger.LogInformation(
                "Kodik fallback stream selected. user={UserId} type={Type} id={Id} ep={Ep} requestedVoice={RequestedVoice} tr={TrId} reason={Reason}",
                request.UserId,
                request.IdType,
                request.Id,
                request.Episode,
                requestedVoice,
                request.Selection.TranslationId,
                request.Selection.Reason);

            return RedirectKodikStream(request);
        }

        private async Task<IActionResult?> TryResolveKodikStreamFallbackAfterFailureAsync(
            KodikFallbackAfterFailureRequest request)
        {
            if (!request.AllowProviderSpecified && !string.IsNullOrWhiteSpace(request.Provider))
            {
                return null;
            }

            if (!Enum.TryParse<KodikIdType>(request.Type, true, out var fallbackIdType) ||
                string.IsNullOrWhiteSpace(request.Id))
            {
                return null;
            }

            var fallbackCfg = Plugin.Instance.Configuration;
            var fallbackQuality = GetPreferredQuality(fallbackCfg);
            return await TryResolveKodikStreamFromYummyIframeAsync(
                    fallbackCfg,
                    request.UserId,
                    fallbackIdType,
                    request.Id,
                    request.Episode,
                    request.TranslationId,
                    fallbackQuality,
                    request.Format,
                    request.OriginalError,
                    request.CancellationToken)
                .ConfigureAwait(false);
        }

        private ContentResult BuildAllohaManifestResult(AllohaPlaybackSession session)
        {
            SetNoStoreCacheHeader();
            return Content(
                AllohaPlaybackService.BuildManifestResponseBody(session, BuildAllohaProxyBaseUrl()),
                "application/vnd.apple.mpegurl");
        }

        private void SetNoStoreCacheHeader()
        {
            Response.Headers.CacheControl = "no-store";
        }

        private static bool TryGetCachedAllohaSession(string? sessionId, out AllohaPlaybackSession session)
        {
            session = default!;
            return !string.IsNullOrWhiteSpace(sessionId) &&
                   AllohaPlaybackService.TryGetSession(sessionId, out session);
        }

        private static bool TryGetPositiveAnimeId(long? animeId, out long resolvedAnimeId)
        {
            resolvedAnimeId = animeId.GetValueOrDefault();
            return resolvedAnimeId > 0;
        }

        private static bool CanTryYummyProviderFallback(long? animeId, Exception ex)
        {
            return animeId.HasValue &&
                   animeId.Value > 0 &&
                   IsYummyProviderFallbackException(ex);
        }

        private static bool IsStreamProvider(string providerValue, string expectedProvider)
        {
            return string.Equals(providerValue, expectedProvider, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ShouldTryKodikTranslationFallback(
            KodikLinkInfo? link,
            string explicitTranslationId,
            KodikStreamSelection selection)
        {
            return link == null &&
                   string.IsNullOrWhiteSpace(explicitTranslationId) &&
                   !selection.WaitIfMissing;
        }

        private static bool IsRetryableKodikLinkException(Exception ex)
        {
            return (ex is KodikException && ex is not KodikTokenException) ||
                   ex is HttpRequestException ||
                   ex is TaskCanceledException;
        }

        private static int GetPreferredQuality(PluginConfiguration cfg)
        {
            return cfg.PreferredQuality > 0 ? cfg.PreferredQuality : 720;
        }

        private void LogPrimaryKodikLinkFailure(
            Exception? error,
            KodikIdType idType,
            string id,
            int episode,
            KodikStreamSelection selection)
        {
            if (error == null)
            {
                return;
            }

            _logger.LogWarning(
                error,
                "Episode link attempt failed. type={Type} id={Id} ep={Ep} tr={TrId} reason={Reason}",
                idType,
                id,
                episode,
                selection.TranslationId,
                selection.Reason);
        }

        private void LogKodikFallbackLinkFailure(
            Exception? error,
            KodikIdType idType,
            string id,
            int episode,
            KodikStreamSelection selection)
        {
            if (error == null)
            {
                return;
            }

            _logger.LogWarning(
                error,
                "Kodik fallback link attempt failed. type={Type} id={Id} ep={Ep} tr={TrId} reason={Reason}",
                idType,
                id,
                episode,
                selection.TranslationId,
                selection.Reason);
        }

        private void LogKodikFallbackTranslationFailure(
            Exception? error,
            KodikIdType idType,
            string id,
            int episode,
            string fallbackTrId)
        {
            if (error == null)
            {
                return;
            }

            _logger.LogDebug(
                error,
                "Fallback translation attempt failed. type={Type} id={Id} ep={Ep} tr={TrId}",
                idType,
                id,
                episode,
                fallbackTrId);
        }

        private void LogAllKodikLinkAttemptsFailed(
            Exception? error,
            KodikIdType idType,
            string id,
            int episode,
            KodikStreamSelection selection)
        {
            if (error == null)
            {
                return;
            }

            _logger.LogWarning(
                error,
                "All link attempts failed. type={Type} id={Id} ep={Ep} chosenTr={TrId} reason={Reason}",
                idType,
                id,
                episode,
                selection.TranslationId,
                selection.Reason);
        }

        [AllowAnonymous]
        [HttpGet("YummyKodik/alloha-proxy")]
        [HttpGet("YummyKodik/alloha-proxy/{resourceName}")]
        public async Task<IActionResult> AllohaProxy(
            [FromQuery] string? sessionId = null,
            [FromQuery] string? resource = null,
            string? resourceName = null,
            CancellationToken cancellationToken = default)
        {
            var sessionKey = (sessionId ?? string.Empty).Trim();
            if (sessionKey.Length == 0)
            {
                return BadRequest("sessionId is required");
            }

            if (!AllohaPlaybackService.TryGetSession(sessionKey, out var session))
            {
                return StatusCode(410, "Alloha session expired.");
            }

            var resourceKey = (resource ?? string.Empty).Trim();
            if (resourceKey.Length == 0)
            {
                return BadRequest("resource is required");
            }

            if (!AllohaPlaybackService.TryResolveProxyResourceUrl(session, resourceKey, out var resourceUrl))
            {
                return NotFound("Alloha proxy resource was not found.");
            }

            try
            {
                var response = await _allohaPlaybackService
                    .DownloadProxyResourceAsync(session, resourceKey, resourceUrl, BuildAllohaProxyBaseUrl(), cancellationToken)
                    .ConfigureAwait(false);

                SetNoStoreCacheHeader();
                return File(response.Content, response.ContentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Alloha proxy failed. sessionId={SessionId} resource={Resource}", sessionKey, resourceKey);
                return StatusCode(502, "Upstream error");
            }
        }

        [AllowAnonymous]
        [HttpGet("YummyKodik/cvh-proxy")]
        [HttpGet("YummyKodik/cvh-proxy/{resourceName}")]
        public async Task<IActionResult> CvhProxy(
            [FromQuery] string? sessionId = null,
            [FromQuery] string? resource = null,
            string? resourceName = null,
            CancellationToken cancellationToken = default)
        {
            var sessionKey = (sessionId ?? string.Empty).Trim();
            if (sessionKey.Length == 0)
            {
                return BadRequest("sessionId is required");
            }

            var cvhHttp = _httpClientFactory.CreateClient(HttpClientNames.Cvh);
            var cvh = new CvhClient(cvhHttp);
            if (!CvhClient.TryGetSession(sessionKey, out var session))
            {
                return StatusCode(410, "CVH session expired.");
            }

            var resourceKey = (resource ?? string.Empty).Trim();
            if (resourceKey.Length == 0)
            {
                return BadRequest("resource is required");
            }

            if (!CvhClient.TryResolveProxyResourceUrl(session, resourceKey, out var resourceUrl))
            {
                return NotFound("CVH proxy resource was not found.");
            }

            try
            {
                var response = await cvh
                    .DownloadProxyResourceAsync(session, resourceUrl, BuildCvhProxyBaseUrl(), cancellationToken)
                    .ConfigureAwait(false);

                SetNoStoreCacheHeader();
                return File(response.Content, response.ContentType);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CVH proxy failed. sessionId={SessionId} resource={Resource}", sessionKey, resourceKey);
                return StatusCode(502, "Upstream error");
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

        private async Task<YummyStreamRequest> ResolveSeriesFromJellyfinAsync(
            string seriesId,
            CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(seriesId, out var itemGuid) || itemGuid == Guid.Empty)
            {
                throw new ArgumentException("seriesId is not a valid GUID", nameof(seriesId));
            }

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null)
            {
                throw new InvalidOperationException("Series item not found");
            }

            var cfg = Plugin.Instance.Configuration;
            var fullRoot = NormalizeConfiguredRoot(cfg.OutputRootPath);
            var strmFile = FindFirstStrmFileForItem(item, fullRoot);
            if (string.IsNullOrWhiteSpace(strmFile) || !System.IO.File.Exists(strmFile))
            {
                throw new InvalidOperationException("No .strm files found for this series");
            }

            var content = (await System.IO.File.ReadAllTextAsync(strmFile, cancellationToken).ConfigureAwait(false)).Trim();

            if (!YummyKodikStreamUri.TryParseRequest(content, out var request))
            {
                throw new InvalidOperationException("Failed to parse stream request from .strm content");
            }

            return request;
        }

        private string? FindFirstStrmFileForItem(BaseItem item, string? fullRoot)
        {
            var itemPath = (item.Path ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(itemPath))
            {
                if (itemPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) &&
                    System.IO.File.Exists(itemPath) &&
                    IsPathWithinRoot(itemPath, fullRoot))
                {
                    return itemPath;
                }

                if (Directory.Exists(itemPath) && IsPathWithinRoot(itemPath, fullRoot))
                {
                    var directMatch = Directory.EnumerateFiles(itemPath, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(directMatch) && System.IO.File.Exists(directMatch))
                    {
                        return directMatch;
                    }
                }
            }

            var descendantMatch = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    AncestorIds = new[] { item.Id },
                    IncludeItemTypes = new[] { BaseItemKind.Episode },
                    Recursive = true
                })
                .Select(x => (x.Path ?? string.Empty).Trim())
                .FirstOrDefault(path =>
                    !string.IsNullOrWhiteSpace(path) &&
                    path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) &&
                    System.IO.File.Exists(path) &&
                    IsPathWithinRoot(path, fullRoot));

            return string.IsNullOrWhiteSpace(descendantMatch) ? null : descendantMatch;
        }

        private static string? NormalizeConfiguredRoot(string? root)
        {
            var value = (root ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            return Path.GetFullPath(value);
        }

        private static bool IsPathWithinRoot(string path, string? fullRoot)
        {
            try
            {
                var fullPath = Path.GetFullPath(path);
                return string.IsNullOrWhiteSpace(fullRoot) ||
                       fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static void TrySaveTranslationId(PluginConfiguration cfg, Guid userId, string seriesKey, string translationId)
        {
            var tid = (translationId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seriesKey))
            {
                return;
            }

            lock (PrefsLock)
            {
                var changed = cfg.SetUserSeriesPreferredTranslationId(userId, seriesKey, string.IsNullOrWhiteSpace(tid) ? null : tid);
                if (!changed)
                {
                    return;
                }

                Plugin.Instance.SaveConfiguration();
            }
        }

        private static void TrySaveYummyVoicePreference(
            PluginConfiguration cfg,
            Guid userId,
            long animeId,
            YummyStreamProviderKind provider,
            string? voiceName)
        {
            if (animeId <= 0)
            {
                return;
            }

            lock (PrefsLock)
            {
                var changed = SetYummyVoicePreference(cfg, userId, animeId, provider, voiceName);
                if (!changed)
                {
                    return;
                }

                Plugin.Instance.SaveConfiguration();
            }
        }

        private static bool SetYummyVoicePreference(
            PluginConfiguration cfg,
            Guid userId,
            long animeId,
            YummyStreamProviderKind provider,
            string? voiceName)
        {
            if (animeId <= 0)
            {
                return false;
            }

            var value = (voiceName ?? string.Empty).Trim();
            var keys = string.IsNullOrWhiteSpace(value)
                ? EnumerateYummyPreferenceKeys(animeId, provider, includeAllLegacyProviderKeys: true)
                : EnumerateYummyPreferenceKeys(animeId, provider, includeAllLegacyProviderKeys: false);

            var changed = false;
            foreach (var key in keys)
            {
                changed |= cfg.SetUserSeriesPreferredTranslationId(
                    userId,
                    key,
                    string.IsNullOrWhiteSpace(value) ? null : value);
            }

            return changed;
        }

        private static string? GetSavedYummyVoiceName(
            PluginConfiguration cfg,
            Guid userId,
            long animeId,
            YummyStreamProviderKind provider)
        {
            if (animeId <= 0)
            {
                return null;
            }

            foreach (var key in EnumerateYummyPreferenceKeys(animeId, provider, includeAllLegacyProviderKeys: true))
            {
                var saved = cfg.GetUserSeriesPreferredTranslationId(userId, key);
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    return saved;
                }
            }

            return null;
        }

        private static IEnumerable<string> EnumerateYummyPreferenceKeys(
            long animeId,
            YummyStreamProviderKind provider,
            bool includeAllLegacyProviderKeys)
        {
            if (animeId <= 0)
            {
                yield break;
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var key in BuildYummyPreferenceKeyCandidates(animeId, provider, includeAllLegacyProviderKeys))
            {
                if (!string.IsNullOrWhiteSpace(key) && used.Add(key))
                {
                    yield return key;
                }
            }
        }

        private static IEnumerable<string> BuildYummyPreferenceKeyCandidates(
            long animeId,
            YummyStreamProviderKind provider,
            bool includeAllLegacyProviderKeys)
        {
            yield return BuildYummySeriesKey(animeId);

            if (provider == YummyStreamProviderKind.Alloha)
            {
                yield return BuildAllohaSeriesKey(animeId);
            }
            else if (provider == YummyStreamProviderKind.Cvh)
            {
                yield return BuildCvhSeriesKey(animeId);
            }

            if (!includeAllLegacyProviderKeys)
            {
                yield break;
            }

            yield return BuildAllohaSeriesKey(animeId);
            yield return BuildCvhSeriesKey(animeId);
        }

        private static bool ShouldUseDifferentYummyProviderForSavedVoice(
            YummyVideoCatalog catalog,
            YummyVideoProviderKind currentProvider,
            int episode,
            string? savedVoiceName,
            out YummyVideoProviderKind savedVoiceProvider)
        {
            savedVoiceProvider = YummyVideoProviderKind.Unknown;
            if (catalog == null || string.IsNullOrWhiteSpace(savedVoiceName))
            {
                return false;
            }

            var provider = catalog.PickPreferredProvider(
                episode,
                explicitVoiceName: savedVoiceName,
                providers: YummyVoiceProviderOrder);

            if (!provider.HasValue || provider.Value == currentProvider)
            {
                return false;
            }

            savedVoiceProvider = provider.Value;
            return true;
        }

        private static string BuildSeriesKey(YummyStreamRequest request)
        {
            return request.Provider switch
            {
                YummyStreamProviderKind.Cvh when request.AnimeId > 0 => BuildYummySeriesKey(request.AnimeId),
                YummyStreamProviderKind.Alloha when request.AnimeId > 0 => BuildYummySeriesKey(request.AnimeId),
                YummyStreamProviderKind.Kodik when !string.IsNullOrWhiteSpace(request.KodikId)
                    => KodikPlaybackSelector.BuildSeriesKey(request.KodikIdType, request.KodikId),
                _ => string.Empty
            };
        }

        private static bool IsYummyProviderRequest(YummyStreamRequest request)
        {
            return request.AnimeId > 0 &&
                   (request.Provider == YummyStreamProviderKind.Alloha ||
                    request.Provider == YummyStreamProviderKind.Cvh);
        }

        private static string BuildYummySeriesKey(long animeId)
        {
            return $"yummy:{animeId}";
        }

        private static string BuildCvhSeriesKey(long animeId)
        {
            return $"cvh:{animeId}";
        }

        private static string BuildAllohaSeriesKey(long animeId)
        {
            return $"alloha:{animeId}";
        }

        private static bool VoiceNamesEquivalent(string? left, string? right)
        {
            var normalizedLeft = YummyVideoCatalog.NormalizeVoiceName(left);
            var normalizedRight = YummyVideoCatalog.NormalizeVoiceName(right);

            if (string.IsNullOrWhiteSpace(normalizedLeft) || string.IsNullOrWhiteSpace(normalizedRight))
            {
                return false;
            }

            var normalizedLeftKey = TranslationNameKeyNormalizer.Normalize(normalizedLeft);
            var normalizedRightKey = TranslationNameKeyNormalizer.Normalize(normalizedRight);
            if (!string.IsNullOrWhiteSpace(normalizedLeftKey) &&
                !string.IsNullOrWhiteSpace(normalizedRightKey) &&
                (string.Equals(normalizedLeftKey, normalizedRightKey, StringComparison.Ordinal) ||
                 normalizedLeftKey.Contains(normalizedRightKey, StringComparison.Ordinal) ||
                 normalizedRightKey.Contains(normalizedLeftKey, StringComparison.Ordinal)))
            {
                return true;
            }

            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase) ||
                   normalizedLeft.Contains(normalizedRight, StringComparison.OrdinalIgnoreCase) ||
                   normalizedRight.Contains(normalizedLeft, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryFindMatchingAllohaEntry(
            YummyVideoCatalog catalog,
            int episode,
            string requestedVoice,
            out YummyVideoEntry? entry)
        {
            return TryFindMatchingProviderEntry(
                catalog,
                YummyVideoProviderKind.Alloha,
                episode,
                requestedVoice,
                out entry) &&
                entry?.Alloha != null;
        }

        private static bool TryFindMatchingProviderEntry(
            YummyVideoCatalog catalog,
            YummyVideoProviderKind provider,
            int episode,
            string requestedVoice,
            out YummyVideoEntry? entry)
        {
            entry = null;

            var matchedVoice = catalog
                .GetSupportedVoiceNames(provider, episode)
                .FirstOrDefault(x => VoiceNamesEquivalent(requestedVoice, x));
            if (string.IsNullOrWhiteSpace(matchedVoice))
            {
                return false;
            }

            entry = catalog.FindPreferredPlayableEntry(provider, episode, matchedVoice);
            return entry != null;
        }

        private static YummyVideoProviderKind[] GetFallbackProviderOrder(YummyStreamProviderKind failedProvider)
        {
            return failedProvider switch
            {
                YummyStreamProviderKind.Cvh => CvhFallbackProviderOrder,
                YummyStreamProviderKind.Alloha => AllohaFallbackProviderOrder,
                _ => Array.Empty<YummyVideoProviderKind>()
            };
        }

        private static bool IsYummyProviderFallbackException(Exception ex)
        {
            return ex is InvalidOperationException or HttpRequestException or TaskCanceledException or KodikException;
        }

        private static KodikTranslation? FindKodikTranslationByVoiceName(
            IReadOnlyList<KodikTranslation> translations,
            string requestedVoice,
            int episode)
        {
            if (translations == null || translations.Count == 0 || string.IsNullOrWhiteSpace(requestedVoice))
            {
                return null;
            }

            var ep = episode <= 0 ? 1 : episode;
            var eligible = translations
                .Where(t => !string.IsNullOrWhiteSpace(t.Id) && t.CoversEpisode(ep))
                .ToArray();

            return eligible.FirstOrDefault(t =>
                       string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
                       VoiceNamesEquivalent(requestedVoice, t.Name)) ??
                   eligible.FirstOrDefault(t => VoiceNamesEquivalent(requestedVoice, t.Name));
        }

        private static bool TryPickKodikIdFromRemoteIds(
            YummyRemoteIds? remoteIds,
            out KodikIdType idType,
            out string id)
        {
            idType = KodikIdType.Shikimori;
            id = string.Empty;

            if (remoteIds == null)
            {
                return false;
            }

            if (remoteIds.ShikimoriId.HasValue && remoteIds.ShikimoriId.Value > 0)
            {
                idType = KodikIdType.Shikimori;
                id = remoteIds.ShikimoriId.Value.ToString();
                return true;
            }

            if (remoteIds.KpId.HasValue && remoteIds.KpId.Value > 0)
            {
                idType = KodikIdType.Kinopoisk;
                id = remoteIds.KpId.Value.ToString();
                return true;
            }

            if (!string.IsNullOrWhiteSpace(remoteIds.ImdbId))
            {
                idType = KodikIdType.Imdb;
                id = remoteIds.ImdbId.Trim();
                return true;
            }

            return false;
        }

        private static bool AllohaSessionSupportsRequestedVoice(AllohaPlaybackSession session, string? requestedVoice)
        {
            var requested = (requestedVoice ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(requested))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(session.SelectedVoiceName))
            {
                if (VoiceNamesEquivalent(requested, session.SelectedVoiceName))
                {
                    return true;
                }

                if (!LooksLikeOpaqueAllohaVoiceMarker(session.SelectedVoiceName))
                {
                    return false;
                }
            }

            var availableVoices = session.AvailableVoiceNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();
            if (availableVoices.Length > 0)
            {
                if (AllohaSessionHasSingleOpaqueTrackMarker(session))
                {
                    return true;
                }

                return availableVoices.Any(x => VoiceNamesEquivalent(requested, x));
            }

            if (AllohaSessionHasSingleOpaqueTrackMarker(session))
            {
                return true;
            }

            return true;
        }

        private static bool AllohaSessionHasSingleOpaqueTrackMarker(AllohaPlaybackSession session)
        {
            var markers = session.AvailableVoiceNames
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (markers.Count == 0 && !string.IsNullOrWhiteSpace(session.SelectedVoiceName))
            {
                markers.Add(session.SelectedVoiceName.Trim());
            }

            if (markers.Count != 1)
            {
                return false;
            }

            var marker = markers[0];
            if (!LooksLikeOpaqueAllohaVoiceMarker(marker))
            {
                return false;
            }

            var selectedVoice = (session.SelectedVoiceName ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(selectedVoice) &&
                !LooksLikeOpaqueAllohaVoiceMarker(selectedVoice))
            {
                return false;
            }

            var audioTrackId = (session.AudioTrackId ?? string.Empty).Trim();
            return !LooksLikeNumericAllohaVoiceMarker(marker) ||
                   string.IsNullOrWhiteSpace(audioTrackId) ||
                   string.Equals(marker, audioTrackId, StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeOpaqueAllohaVoiceMarker(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            return LooksLikeNumericAllohaVoiceMarker(normalized);
        }

        private static bool LooksLikeNumericAllohaVoiceMarker(string value)
        {
            return value.Length > 0 && value.All(char.IsDigit);
        }

        private string BuildAllohaProxyBaseUrl()
        {
            var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
            return $"{Request.Scheme}://{Request.Host}{pathBase}/YummyKodik/alloha-proxy";
        }

        private string BuildCvhProxyBaseUrl()
        {
            var pathBase = Request.PathBase.HasValue ? Request.PathBase.Value : string.Empty;
            return $"{Request.Scheme}://{Request.Host}{pathBase}/YummyKodik/cvh-proxy";
        }

        private static bool TryMapCatalogProvider(
            YummyStreamProviderKind provider,
            out YummyVideoProviderKind catalogProvider,
            out string providerId)
        {
            switch (provider)
            {
                case YummyStreamProviderKind.Cvh:
                    catalogProvider = YummyVideoProviderKind.Cvh;
                    providerId = YummyKodikStreamUri.CvhProvider;
                    return true;
                case YummyStreamProviderKind.Alloha:
                    catalogProvider = YummyVideoProviderKind.Alloha;
                    providerId = YummyKodikStreamUri.AllohaProvider;
                    return true;
                default:
                    catalogProvider = YummyVideoProviderKind.Unknown;
                    providerId = string.Empty;
                    return false;
            }
        }

        private async Task<AllohaPlaybackSession> ResolveAllohaSessionAsync(
            PluginConfiguration cfg,
            Guid userId,
            long animeId,
            int episode,
            string? explicitVoiceName,
            int quality,
            CancellationToken cancellationToken)
        {
            var catalog = await LoadYummyVideoCatalogAsync(cfg, animeId.ToString(), cancellationToken).ConfigureAwait(false);
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedVoice = GetSavedYummyVoiceName(cfg, userId, animeId, YummyStreamProviderKind.Alloha);
            string? chosenVoice;
            string reason;
            YummyVideoEntry? chosenEntry;

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                if (!TryFindMatchingAllohaEntry(catalog, episode, requestedVoice, out chosenEntry))
                {
                    throw new InvalidOperationException("Requested Alloha translation is unavailable from upstream.");
                }

                chosenVoice = chosenEntry!.DisplayVoiceName;
                reason = "explicit";
            }
            else
            {
                if (ShouldUseDifferentYummyProviderForSavedVoice(
                        catalog,
                        YummyVideoProviderKind.Alloha,
                        episode,
                        savedVoice,
                        out var savedVoiceProvider))
                {
                    throw new InvalidOperationException($"Saved voice is available from {savedVoiceProvider}.");
                }

                chosenVoice = catalog.PickPreferredVoiceName(
                    YummyVideoProviderKind.Alloha,
                    episode,
                    requestedVoice,
                    savedVoice,
                    cfg.PreferredTranslationFilter,
                    out reason);
                chosenEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, episode, chosenVoice)
                              ?? catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, episode);
            }

            if (chosenEntry?.Alloha == null)
            {
                throw new InvalidOperationException("Alloha episode is not available for this anime.");
            }

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                TrySaveYummyVoicePreference(cfg, userId, animeId, YummyStreamProviderKind.Alloha, requestedVoice);
            }

            var session = await _allohaPlaybackService.CreateSessionAsync(chosenEntry.Alloha, quality, chosenVoice, cancellationToken)
                .ConfigureAwait(false);

            if (!AllohaSessionSupportsRequestedVoice(session, requestedVoice))
            {
                _logger.LogWarning(
                    "Alloha resolved source voice mismatch: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} chosenVoice={ChosenVoice} selectedVoice={SelectedVoice} availableVoices={AvailableVoices} translationId={TranslationId}",
                    userId,
                    animeId,
                    episode,
                    requestedVoice,
                    chosenEntry.DisplayVoiceName,
                    session.SelectedVoiceName,
                    string.Join(", ", session.AvailableVoiceNames),
                    chosenEntry.Alloha.TranslationId);

                throw new InvalidOperationException("Requested Alloha translation is unavailable from upstream.");
            }

            _logger.LogInformation(
                "Alloha manifest prepared: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} chosenVoice={ChosenVoice} reason={Reason}",
                userId,
                animeId,
                episode,
                requestedVoice,
                chosenEntry.DisplayVoiceName,
                reason);

            return session;
        }

        private async Task<AllohaPlaybackSession> ResolveAllohaSessionFromCatalogAsync(
            PluginConfiguration cfg,
            Guid userId,
            YummyVideoCatalog catalog,
            long animeId,
            int episode,
            string? explicitVoiceName,
            int quality,
            CancellationToken cancellationToken)
        {
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedVoice = GetSavedYummyVoiceName(cfg, userId, animeId, YummyStreamProviderKind.Alloha);
            string? chosenVoice;
            string reason;
            YummyVideoEntry? chosenEntry;

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                if (!TryFindMatchingProviderEntry(catalog, YummyVideoProviderKind.Alloha, episode, requestedVoice, out chosenEntry))
                {
                    throw new InvalidOperationException("Requested Alloha translation is unavailable from upstream.");
                }

                chosenVoice = chosenEntry!.DisplayVoiceName;
                reason = "explicit";
            }
            else
            {
                if (ShouldUseDifferentYummyProviderForSavedVoice(
                        catalog,
                        YummyVideoProviderKind.Alloha,
                        episode,
                        savedVoice,
                        out var savedVoiceProvider))
                {
                    throw new InvalidOperationException($"Saved voice is available from {savedVoiceProvider}.");
                }

                chosenVoice = catalog.PickPreferredVoiceName(
                    YummyVideoProviderKind.Alloha,
                    episode,
                    requestedVoice,
                    savedVoice,
                    cfg.PreferredTranslationFilter,
                    out reason);
                chosenEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, episode, chosenVoice)
                              ?? catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Alloha, episode);
            }

            if (chosenEntry?.Alloha == null)
            {
                throw new InvalidOperationException("Alloha episode is not available for this anime.");
            }

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                TrySaveYummyVoicePreference(cfg, userId, animeId, YummyStreamProviderKind.Alloha, requestedVoice);
            }

            var session = await _allohaPlaybackService.CreateSessionAsync(chosenEntry.Alloha, quality, chosenVoice, cancellationToken)
                .ConfigureAwait(false);

            if (!AllohaSessionSupportsRequestedVoice(session, requestedVoice))
            {
                _logger.LogWarning(
                    "Alloha resolved source voice mismatch: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} chosenVoice={ChosenVoice} selectedVoice={SelectedVoice} availableVoices={AvailableVoices} translationId={TranslationId}",
                    userId,
                    animeId,
                    episode,
                    requestedVoice,
                    chosenEntry.DisplayVoiceName,
                    session.SelectedVoiceName,
                    string.Join(", ", session.AvailableVoiceNames),
                    chosenEntry.Alloha.TranslationId);

                throw new InvalidOperationException("Requested Alloha translation is unavailable from upstream.");
            }

            _logger.LogInformation(
                "Alloha manifest prepared: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} chosenVoice={ChosenVoice} reason={Reason}",
                userId,
                animeId,
                episode,
                requestedVoice,
                chosenEntry.DisplayVoiceName,
                reason);

            return session;
        }

        private async Task<IActionResult> ResolveCvhStreamFromCatalogAsync(
            PluginConfiguration cfg,
            Guid userId,
            YummyVideoCatalog catalog,
            long animeId,
            int episode,
            string? explicitVoiceName,
            int quality,
            string? format,
            string reasonPrefix,
            CancellationToken cancellationToken)
        {
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedVoice = GetSavedYummyVoiceName(cfg, userId, animeId, YummyStreamProviderKind.Cvh);
            string reason;
            YummyVideoEntry? chosenEntry;

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                if (!TryFindMatchingProviderEntry(catalog, YummyVideoProviderKind.Cvh, episode, requestedVoice, out chosenEntry))
                {
                    throw new InvalidOperationException("Requested CVH translation is unavailable from upstream.");
                }
                reason = "explicit";
            }
            else
            {
                if (ShouldUseDifferentYummyProviderForSavedVoice(
                        catalog,
                        YummyVideoProviderKind.Cvh,
                        episode,
                        savedVoice,
                        out var savedVoiceProvider))
                {
                    throw new InvalidOperationException($"Saved voice is available from {savedVoiceProvider}.");
                }

                var chosenVoice = catalog.PickPreferredVoiceName(
                    YummyVideoProviderKind.Cvh,
                    episode,
                    requestedVoice,
                    savedVoice,
                    cfg.PreferredTranslationFilter,
                    out reason);
                chosenEntry = catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, episode, chosenVoice)
                              ?? catalog.FindPreferredPlayableEntry(YummyVideoProviderKind.Cvh, episode);
            }

            if (chosenEntry?.Cvh == null || chosenEntry.Cvh.AnimeId <= 0)
            {
                throw new InvalidOperationException("CVH episode is not available for this anime.");
            }

            var cvhSourceAnimeId = chosenEntry.Cvh.AnimeId;
            var cvhSourceVoice = !string.IsNullOrWhiteSpace(chosenEntry.Cvh.DubbingCode)
                ? chosenEntry.Cvh.DubbingCode
                : chosenEntry.DisplayVoiceName;

            var cvhHttp = _httpClientFactory.CreateClient(HttpClientNames.Cvh);
            var cvh = new CvhClient(cvhHttp);
            var resolved = await cvh.ResolveEpisodeStreamAsync(chosenEntry.Cvh, quality, cancellationToken)
                .ConfigureAwait(false);
            var resolvedVoice = YummyVideoCatalog.NormalizeVoiceName(resolved.VoiceName);

            if (!string.IsNullOrWhiteSpace(requestedVoice) &&
                !VoiceNamesEquivalent(requestedVoice, resolvedVoice))
            {
                _logger.LogWarning(
                    "CVH voice mismatch: user={UserId} animeId={AnimeId} cvhAnimeId={CvhAnimeId} ep={Ep} requestedVoice={RequestedVoice} chosenVoice={ChosenVoice} sourceVoice={SourceVoice} resolvedVoice={ResolvedVoice}",
                    userId,
                    animeId,
                    cvhSourceAnimeId,
                    episode,
                    requestedVoice,
                    chosenEntry.DisplayVoiceName,
                    cvhSourceVoice,
                    resolvedVoice);

                throw new InvalidOperationException("Requested CVH translation is unavailable from upstream.");
            }

            SetNoStoreCacheHeader();

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                TrySaveYummyVoicePreference(cfg, userId, animeId, YummyStreamProviderKind.Cvh, requestedVoice);
            }

            _logger.LogInformation(
                "CVH stream resolved: user={UserId} animeId={AnimeId} cvhAnimeId={CvhAnimeId} ep={Ep} requestedVoice={RequestedVoice} chosenVoice={ChosenVoice} sourceVoice={SourceVoice} resolvedVoice={ResolvedVoice} reason={Reason} format={Format} -> {Url}",
                userId,
                animeId,
                cvhSourceAnimeId,
                episode,
                requestedVoice,
                chosenEntry.DisplayVoiceName,
                cvhSourceVoice,
                resolvedVoice,
                string.IsNullOrWhiteSpace(reasonPrefix) ? reason : reasonPrefix + "+" + reason,
                format ?? "hls",
                resolved.StreamUrl);

            var cvhFormat = (format ?? "hls").Trim().ToLowerInvariant();
            if (cvhFormat == "hls")
            {
                var session = await cvh.CreatePlaybackSessionAsync(chosenEntry.Cvh, quality, cancellationToken)
                    .ConfigureAwait(false);
                SetNoStoreCacheHeader();
                return Content(
                    CvhClient.BuildManifestResponseBody(session, BuildCvhProxyBaseUrl()),
                    "application/vnd.apple.mpegurl");
            }

            return Redirect(resolved.StreamUrl);
        }

        private async Task<IActionResult> ResolveYummyFallbackStreamAsync(
            PluginConfiguration cfg,
            Guid userId,
            YummyStreamProviderKind failedProvider,
            long animeId,
            int episode,
            string? explicitVoiceName,
            int quality,
            string? format,
            Exception originalError,
            CancellationToken cancellationToken)
        {
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedYummyVoice = string.IsNullOrWhiteSpace(requestedVoice)
                ? GetSavedYummyVoiceName(cfg, userId, animeId, failedProvider)
                : string.Empty;
            var (anime, catalog) = await LoadYummyVideoContextAsync(cfg, animeId.ToString(), cancellationToken)
                .ConfigureAwait(false);
            var fallbackErrors = new List<Exception> { originalError };

            foreach (var provider in GetFallbackProviderOrder(failedProvider))
            {
                try
                {
                    if (provider == YummyVideoProviderKind.Cvh)
                    {
                        var result = await ResolveCvhStreamFromCatalogAsync(
                                cfg,
                                userId,
                                catalog,
                                animeId,
                                episode,
                                requestedVoice,
                                quality,
                                format,
                                "fallback-cvh",
                                cancellationToken)
                            .ConfigureAwait(false);

                        _logger.LogInformation(
                            "Yummy provider fallback succeeded. from={FromProvider} to={ToProvider} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                            failedProvider,
                            provider,
                            animeId,
                            episode,
                            requestedVoice);

                        return result;
                    }

                    if (provider == YummyVideoProviderKind.Alloha)
                    {
                        var session = await ResolveAllohaSessionFromCatalogAsync(
                                cfg,
                                userId,
                                catalog,
                                animeId,
                                episode,
                                requestedVoice,
                                quality,
                                cancellationToken)
                            .ConfigureAwait(false);

                        _logger.LogInformation(
                            "Yummy provider fallback succeeded. from={FromProvider} to={ToProvider} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                            failedProvider,
                            provider,
                            animeId,
                            episode,
                            requestedVoice);

                        SetNoStoreCacheHeader();
                        return Content(
                            AllohaPlaybackService.BuildManifestResponseBody(session, BuildAllohaProxyBaseUrl()),
                            "application/vnd.apple.mpegurl");
                    }
                }
                catch (Exception ex) when (IsYummyProviderFallbackException(ex))
                {
                    fallbackErrors.Add(ex);
                    _logger.LogWarning(
                        ex,
                        "Yummy provider fallback attempt failed. from={FromProvider} to={ToProvider} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                        failedProvider,
                        provider,
                        animeId,
                        episode,
                        requestedVoice);
                }
            }

            try
            {
                return await ResolveKodikFallbackStreamAsync(
                        cfg,
                        userId,
                        anime,
                        episode,
                        requestedVoice,
                        savedYummyVoice,
                        quality,
                        format,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsYummyProviderFallbackException(ex))
            {
                fallbackErrors.Add(ex);
                _logger.LogWarning(
                    ex,
                    "Kodik fallback attempt failed. from={FromProvider} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                    failedProvider,
                    animeId,
                    episode,
                    requestedVoice);
            }

            throw new InvalidOperationException("All provider fallback attempts failed.", fallbackErrors[^1]);
        }

        private async Task<IActionResult> ResolveKodikFallbackStreamAsync(
            PluginConfiguration cfg,
            Guid userId,
            YummyAnimeResponse anime,
            int episode,
            string? explicitVoiceName,
            string? savedYummyVoiceName,
            int quality,
            string? format,
            CancellationToken cancellationToken)
        {
            if (!TryPickKodikIdFromRemoteIds(anime.RemoteIds, out var idType, out var id))
            {
                throw new InvalidOperationException("Kodik fallback id is unavailable for this anime.");
            }

            var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
            var token = await ResolveKodikTokenAsync(http, cfg, cancellationToken).ConfigureAwait(false);
            var kodik = new KodikClient(http, token);
            var infoRes = await LoadKodikInfoAsync(kodik, http, cfg, id, idType, cancellationToken)
                .ConfigureAwait(false);

            kodik = infoRes.Client;
            var info = infoRes.Result;
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedYummyVoice = (savedYummyVoiceName ?? string.Empty).Trim();
            var seriesKey = KodikPlaybackSelector.BuildSeriesKey(idType, id);
            var preferredTokens = StringTokenParser.ParseTokens(cfg.PreferredTranslationFilter);
            var savedTrId = cfg.GetUserSeriesPreferredTranslationId(userId, seriesKey);
            var selection = PickKodikFallbackSelection(
                info.Translations,
                preferredTokens,
                savedTrId,
                requestedVoice,
                savedYummyVoice,
                episode);

            var linkAttempt = await TryResolveKodikEpisodeLinkAsync(
                    new KodikLinkRequest(
                        kodik,
                        http,
                        cfg,
                        idType,
                        id,
                        episode,
                        selection.TranslationId,
                        cancellationToken))
                .ConfigureAwait(false);
            LogKodikFallbackLinkFailure(linkAttempt.Error, idType, id, episode, selection);

            var link = linkAttempt.Link;
            var lastUpstreamError = linkAttempt.Error;

            if (ShouldTryKodikTranslationFallback(link, requestedVoice, selection))
            {
                var fallbackAttempt = await TryResolveKodikTranslationFallbackAsync(
                        new KodikTranslationFallbackRequest(
                            linkAttempt.Client,
                            http,
                            cfg,
                            info.Translations,
                            preferredTokens,
                            idType,
                            id,
                            episode,
                            selection,
                            lastUpstreamError,
                            LogSuccess: false,
                            cancellationToken))
                    .ConfigureAwait(false);

                link = fallbackAttempt.Link;
                selection = fallbackAttempt.Selection;
                lastUpstreamError = fallbackAttempt.Error;
            }

            link = RequireKodikFallbackLink(link, selection, lastUpstreamError);
            return RedirectKodikFallbackStream(
                new KodikRedirectRequest(
                    cfg,
                    userId,
                    idType,
                    id,
                    episode,
                    string.Empty,
                    quality,
                    format,
                    seriesKey,
                    selection,
                    link),
                requestedVoice);
        }

        private async Task<IActionResult?> TryResolveKodikStreamFromYummyIframeAsync(
            PluginConfiguration cfg,
            Guid userId,
            KodikIdType idType,
            string id,
            int episode,
            string? translationId,
            int quality,
            string? format,
            Exception? originalError,
            CancellationToken cancellationToken)
        {
            try
            {
                if (!TryResolveYummyKodikFallbackFromArtifacts(
                        cfg,
                        idType,
                        id,
                        episode,
                        translationId,
                        out var animeId,
                        out var voiceName))
                {
                    return null;
                }

                var (anime, _) = await LoadYummyVideoContextAsync(
                        cfg,
                        animeId.ToString(CultureInfo.InvariantCulture),
                        cancellationToken)
                    .ConfigureAwait(false);

                var video = FindYummyKodikVideo(anime, episode, voiceName);
                if (video == null || string.IsNullOrWhiteSpace(video.IframeUrl))
                {
                    _logger.LogInformation(
                        "Yummy Kodik iframe fallback unavailable. type={Type} id={Id} animeId={AnimeId} ep={Ep} voice={Voice}",
                        idType,
                        id,
                        animeId,
                        episode,
                        voiceName);
                    return null;
                }

                var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
                var kodik = new KodikClient(http, token: string.Empty);
                var link = await kodik.GetPlayerLinkAsync(video.IframeUrl, episode, cancellationToken).ConfigureAwait(false);

                var fmt = (format ?? "mp4").Trim().ToLowerInvariant();
                var targetUrl = fmt == "hls"
                    ? KodikClient.BuildHlsUrl(link, quality)
                    : KodikClient.BuildMp4Url(link, quality);

                SetNoStoreCacheHeader();

                var tid = (translationId ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(tid))
                {
                    TrySaveTranslationId(cfg, userId, KodikPlaybackSelector.BuildSeriesKey(idType, id), tid);
                }

                _logger.LogInformation(
                    originalError,
                    "Yummy Kodik iframe fallback succeeded. user={UserId} type={Type} id={Id} animeId={AnimeId} ep={Ep} tr={TrId} voice={Voice} format={Format} -> {Url}",
                    userId,
                    idType,
                    id,
                    animeId,
                    episode,
                    tid,
                    voiceName,
                    fmt,
                    targetUrl);

                return Redirect(targetUrl);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Yummy Kodik iframe fallback failed. type={Type} id={Id} ep={Ep} tr={TrId}",
                    idType,
                    id,
                    episode,
                    translationId);
                return null;
            }
        }

        private static YummyVideoItem? FindYummyKodikVideo(
            YummyAnimeResponse anime,
            int episode,
            string voiceName)
        {
            var videos = (anime.Videos ?? new List<YummyVideoItem>())
                .Where(v =>
                    v?.Data != null &&
                    v.Data.PlayerId == (int)YummyVideoProviderKind.Kodik &&
                    TryParseEpisodeNumber(v.Number, out var ep) &&
                    ep == episode &&
                    !string.IsNullOrWhiteSpace(v.IframeUrl))
                .ToList();

            if (videos.Count == 0)
            {
                return null;
            }

            var voiceKey = TranslationNameKeyNormalizer.Normalize(voiceName);
            if (!string.IsNullOrWhiteSpace(voiceKey))
            {
                var keyMatch = videos.FirstOrDefault(v =>
                    string.Equals(
                        TranslationNameKeyNormalizer.Normalize(YummyVideoCatalog.NormalizeVoiceName(v.Data?.Dubbing)),
                        voiceKey,
                        StringComparison.Ordinal));

                if (keyMatch != null)
                {
                    return keyMatch;
                }
            }

            var normalizedVoice = YummyVideoCatalog.NormalizeVoiceName(voiceName);
            if (!string.IsNullOrWhiteSpace(normalizedVoice))
            {
                var exactMatch = videos.FirstOrDefault(v =>
                    string.Equals(
                        YummyVideoCatalog.NormalizeVoiceName(v.Data?.Dubbing),
                        normalizedVoice,
                        StringComparison.OrdinalIgnoreCase));

                if (exactMatch != null)
                {
                    return exactMatch;
                }
            }

            return videos[0];
        }

        private static bool TryResolveYummyKodikFallbackFromArtifacts(
            PluginConfiguration cfg,
            KodikIdType idType,
            string id,
            int episode,
            string? translationId,
            out long animeId,
            out string voiceName)
        {
            animeId = 0;
            voiceName = string.Empty;

            var root = (cfg.OutputRootPath ?? string.Empty).Trim();
            if (idType != KodikIdType.Shikimori ||
                string.IsNullOrWhiteSpace(root) ||
                !Directory.Exists(root))
            {
                return false;
            }

            var tag = $"[shikimori-{id.Trim()}]";
            var legacyTag = $"[shikimoriid-{id.Trim()}]";
            var tid = (translationId ?? string.Empty).Trim();

            try
            {
                foreach (var seriesDir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var dirName = Path.GetFileName(seriesDir);
                    if (!dirName.Contains(tag, StringComparison.OrdinalIgnoreCase) &&
                        !dirName.Contains(legacyTag, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var foundAnimeId = FindFirstYummyAnimeIdInSeriesDirectory(seriesDir);
                    if (foundAnimeId <= 0)
                    {
                        continue;
                    }

                    foreach (var strmPath in Directory.EnumerateFiles(seriesDir, "*.strm", SearchOption.AllDirectories))
                    {
                        var line = System.IO.File.ReadLines(strmPath).FirstOrDefault()?.Trim() ?? string.Empty;
                        if (!YummyKodikStreamUri.TryParseRequest(line, out var request) ||
                            request.Provider != YummyStreamProviderKind.Kodik ||
                            request.KodikIdType != idType ||
                            !string.Equals(request.KodikId, id.Trim(), StringComparison.OrdinalIgnoreCase) ||
                            request.Episode != episode)
                        {
                            continue;
                        }

                        var query = Uri.TryCreate(line, UriKind.Absolute, out var uri)
                            ? YummyKodikStreamUri.ParseQueryToDictionary(uri.Query)
                            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                        if (!string.IsNullOrWhiteSpace(tid) &&
                            (!query.TryGetValue("tr", out var fileTr) ||
                             !string.Equals((fileTr ?? string.Empty).Trim(), tid, StringComparison.Ordinal)))
                        {
                            continue;
                        }

                        animeId = foundAnimeId;
                        voiceName = ExtractVoiceNameFromEpisodeFileName(strmPath);
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static long FindFirstYummyAnimeIdInSeriesDirectory(string seriesDir)
        {
            try
            {
                foreach (var strmPath in Directory.EnumerateFiles(seriesDir, "*.strm", SearchOption.AllDirectories))
                {
                    var line = System.IO.File.ReadLines(strmPath).FirstOrDefault()?.Trim() ?? string.Empty;
                    if (YummyKodikStreamUri.TryParseRequest(line, out var request) &&
                        request.AnimeId > 0)
                    {
                        return request.AnimeId;
                    }
                }
            }
            catch
            {
                return 0;
            }

            return 0;
        }

        private static string ExtractVoiceNameFromEpisodeFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            var idx = name.IndexOf(" - ", StringComparison.Ordinal);
            return idx >= 0 && idx + 3 < name.Length
                ? name[(idx + 3)..].Trim()
                : string.Empty;
        }

        private static bool TryParseEpisodeNumber(string? raw, out int episode)
        {
            return int.TryParse(
                (raw ?? string.Empty).Trim(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out episode);
        }

        private async Task<YummyVideoCatalog> LoadYummyVideoCatalogAsync(
            PluginConfiguration cfg,
            string animeKey,
            CancellationToken cancellationToken)
        {
            var (_, catalog) = await LoadYummyVideoContextAsync(cfg, animeKey, cancellationToken).ConfigureAwait(false);
            return catalog;
        }

        private async Task<(YummyAnimeResponse Anime, YummyVideoCatalog Catalog)> LoadYummyVideoContextAsync(
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
            var seasonNumber = YummySeriesLayoutResolver.ResolveSeasonNumber(
                anime,
                string.IsNullOrWhiteSpace(anime.Title) ? animeKey : anime.Title);
            var allohaApiHttp = _httpClientFactory.CreateClient(HttpClientNames.AllohaApi);
            var allohaApiEntries = await AllohaApiCatalogLoader
                .LoadEntriesAsync(cfg, anime, allohaApiHttp, _logger, cancellationToken)
                .ConfigureAwait(false);
            allohaApiEntries = AllohaApiCatalogLoader.FilterEntriesForSeason(allohaApiEntries, seasonNumber);

            return (anime, YummyVideoCatalog.Create(anime, allohaApiEntries));
        }

        private static bool TryBuildDirectAllohaSource(
            string? movieToken,
            string? requestToken,
            int translationId,
            int seasonNumber,
            int episodeNumber,
            string? hidden,
            string? refererUrl,
            out YummyAllohaSource source)
        {
            source = new YummyAllohaSource();

            if (string.IsNullOrWhiteSpace(movieToken) ||
                string.IsNullOrWhiteSpace(requestToken) ||
                translationId <= 0 ||
                seasonNumber <= 0 ||
                episodeNumber <= 0 ||
                string.IsNullOrWhiteSpace(refererUrl))
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
                RefererUrl = refererUrl.Trim()
            };

            return true;
        }
    }
}
