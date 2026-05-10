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

        private static readonly object PrefsLock = new();

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
                    var savedVoice = cfg.GetUserSeriesPreferredTranslationId(auth.UserId, seriesKey);
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
                        translations = catalog.GetAllVoiceNames(catalogProvider)
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
                        cancellationToken,
                        k => k.GetAnimeInfoAsync(request.KodikId, request.KodikIdType, cancellationToken))
                    .ConfigureAwait(false);

                kodik = infoRes.Client;
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
                    changed = cfg.SetUserSeriesPreferredTranslationId(auth.UserId, seriesKey, string.IsNullOrWhiteSpace(tid) ? null : tid);
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

            Guid userId = Guid.Empty;
            try
            {
                var auth = await _authorizationContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
                if (auth.IsAuthenticated)
                {
                    userId = auth.UserId;
                }
            }
            catch
            {
                // Ignore auth parsing errors for streaming.
            }

            try
            {
                var cfg = Plugin.Instance.Configuration;
                var quality = cfg.PreferredQuality > 0 ? cfg.PreferredQuality : 720;
                var providerValue = (provider ?? string.Empty).Trim();

                if (string.Equals(providerValue, YummyKodikStreamUri.AllohaProvider, StringComparison.OrdinalIgnoreCase))
                {
                    var requestedVoice = voice ?? tr;
                    try
                    {
                        AllohaPlaybackSession session;
                        if (!string.IsNullOrWhiteSpace(sessionId) &&
                            _allohaPlaybackService.TryGetSession(sessionId, out var cachedSession))
                        {
                            session = cachedSession;
                        }
                        else
                        {
                            if (!animeId.HasValue || animeId.Value <= 0)
                            {
                                return BadRequest("animeId is required for Alloha streams");
                            }

                            if (TryBuildDirectAllohaSource(
                                    allohaMovieToken,
                                    allohaRequestToken,
                                    allohaTranslationId,
                                    allohaSeason,
                                    ep,
                                    allohaHidden,
                                    allohaRefererUrl,
                                    out var directSource))
                            {
                                try
                                {
                                    var directSession = await _allohaPlaybackService.CreateSessionAsync(directSource, quality, requestedVoice, cancellationToken)
                                        .ConfigureAwait(false);
                                    if (!AllohaSessionSupportsRequestedVoice(directSession, requestedVoice))
                                    {
                                        _logger.LogWarning(
                                            "Alloha embedded source voice mismatch: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} translationId={TranslationId} selectedVoice={SelectedVoice} availableVoices={AvailableVoices}",
                                            userId,
                                            animeId.Value,
                                            ep,
                                            requestedVoice,
                                            directSource.TranslationId,
                                            directSession.SelectedVoiceName,
                                            string.Join(", ", directSession.AvailableVoiceNames));

                                        throw new InvalidOperationException("Alloha embedded source voice mismatch.");
                                    }
                                    else
                                    {
                                        session = directSession;

                                        if (!string.IsNullOrWhiteSpace(requestedVoice))
                                        {
                                            TrySaveTranslationId(cfg, userId, BuildAllohaSeriesKey(animeId.Value), requestedVoice);
                                        }

                                        _logger.LogInformation(
                                            "Alloha manifest prepared from embedded source: user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} translationId={TranslationId}",
                                            userId,
                                            animeId.Value,
                                            ep,
                                            requestedVoice,
                                            directSource.TranslationId);
                                    }
                                }
                                catch (Exception ex) when (IsYummyProviderFallbackException(ex))
                                {
                                    _logger.LogWarning(
                                        ex,
                                        "Alloha embedded source failed, falling back to live resolution. user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice} translationId={TranslationId}",
                                        userId,
                                        animeId.Value,
                                        ep,
                                        requestedVoice,
                                        directSource.TranslationId);

                                    session = await ResolveAllohaSessionAsync(
                                            cfg,
                                            userId,
                                            animeId.Value,
                                            ep,
                                            requestedVoice,
                                            quality,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                session = await ResolveAllohaSessionAsync(
                                        cfg,
                                        userId,
                                        animeId.Value,
                                        ep,
                                        requestedVoice,
                                        quality,
                                        cancellationToken)
                                    .ConfigureAwait(false);
                            }
                        }

                        Response.Headers["Cache-Control"] = "no-store";
                        return Content(
                            AllohaPlaybackService.BuildManifestResponseBody(session, BuildAllohaProxyBaseUrl()),
                            "application/vnd.apple.mpegurl");
                    }
                    catch (Exception ex) when (
                        animeId.HasValue &&
                        animeId.Value > 0 &&
                        IsYummyProviderFallbackException(ex))
                    {
                        _logger.LogWarning(
                            ex,
                            "Alloha stream attempt failed, trying fallback providers. user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                            userId,
                            animeId.Value,
                            ep,
                            requestedVoice);

                        return await ResolveYummyFallbackStreamAsync(
                                cfg,
                                userId,
                                YummyStreamProviderKind.Alloha,
                                animeId.Value,
                                ep,
                                requestedVoice,
                                quality,
                                format,
                                ex,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (string.Equals(providerValue, YummyKodikStreamUri.CvhProvider, StringComparison.OrdinalIgnoreCase))
                {
                    if (!animeId.HasValue || animeId.Value <= 0)
                    {
                        return BadRequest("animeId is required for CVH streams");
                    }

                    var requestedVoice = voice ?? tr;
                    try
                    {
                        var catalog = await LoadYummyVideoCatalogAsync(cfg, animeId.Value.ToString(), cancellationToken).ConfigureAwait(false);
                        return await ResolveCvhStreamFromCatalogAsync(
                                cfg,
                                userId,
                                catalog,
                                animeId.Value,
                                ep,
                                requestedVoice,
                                quality,
                                format,
                                "primary",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsYummyProviderFallbackException(ex))
                    {
                        _logger.LogWarning(
                            ex,
                            "CVH stream attempt failed, trying fallback providers. user={UserId} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                            userId,
                            animeId.Value,
                            ep,
                            requestedVoice);

                        return await ResolveYummyFallbackStreamAsync(
                                cfg,
                                userId,
                                YummyStreamProviderKind.Cvh,
                                animeId.Value,
                                ep,
                                requestedVoice,
                                quality,
                                format,
                                ex,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id))
                {
                    return BadRequest("type and id are required for Kodik streams");
                }

                if (!Enum.TryParse<KodikIdType>(type, true, out var idType))
                {
                    return BadRequest("unknown type");
                }

                var http = _httpClientFactory.CreateClient(HttpClientNames.Kodik);
                var token = await ResolveKodikTokenAsync(http, cfg, cancellationToken).ConfigureAwait(false);
                var kodik = new KodikClient(http, token);

                var infoRes = await ExecuteWithAutoTokenRefreshAsync(
                        kodik,
                        http,
                        cfg,
                        cancellationToken,
                        k => k.GetAnimeInfoAsync(id, idType, cancellationToken))
                    .ConfigureAwait(false);

                kodik = infoRes.Client;
                var info = infoRes.Result;

                var seriesKey = KodikPlaybackSelector.BuildSeriesKey(idType, id);

                var explicitTr = (tr ?? string.Empty).Trim();
                var preferredTokens = StringTokenParser.ParseTokens(cfg.PreferredTranslationFilter);

                var savedTrId = cfg.GetUserSeriesPreferredTranslationId(userId, seriesKey);

                var (chosenTrId, waitIfMissing, reason) = KodikPlaybackSelector.PickTranslationForPlayback(
                    info.Translations,
                    preferredTokens,
                    savedTrId,
                    explicitTr,
                    ep);

                if (string.IsNullOrWhiteSpace(chosenTrId))
                {
                    chosenTrId = "0";
                }

                KodikLinkInfo? link = null;
                Exception? lastUpstreamError = null;

                try
                {
                    var linkRes = await ExecuteWithAutoTokenRefreshAsync(
                            kodik,
                            http,
                            cfg,
                            cancellationToken,
                            k => k.GetEpisodeLinkAsync(id, idType, ep, chosenTrId, cancellationToken))
                        .ConfigureAwait(false);

                    kodik = linkRes.Client;
                    link = linkRes.Result;
                }
                catch (Exception ex) when (
                    (ex is KodikException && ex is not KodikTokenException) ||
                    ex is HttpRequestException ||
                    ex is TaskCanceledException)
                {
                    lastUpstreamError = ex;

                    _logger.LogWarning(
                        ex,
                        "Episode link attempt failed. type={Type} id={Id} ep={Ep} tr={TrId} reason={Reason}",
                        idType, id, ep, chosenTrId, reason);
                }

                // If the chosen translation is one of preferred (or explicitly selected), we do not fall back.
                // This enforces "preferred exists -> wait" behavior.
                if (link == null && string.IsNullOrWhiteSpace(explicitTr) && !waitIfMissing)
                {
                    foreach (var fallbackTrId in KodikPlaybackSelector.BuildFallbackTranslationCandidates(
                                 info.Translations,
                                 preferredTokens,
                                 chosenTrId,
                                 ep))
                    {
                        try
                        {
                            var linkRes = await ExecuteWithAutoTokenRefreshAsync(
                                    kodik,
                                    http,
                                    cfg,
                                    cancellationToken,
                                    k => k.GetEpisodeLinkAsync(id, idType, ep, fallbackTrId, cancellationToken))
                                .ConfigureAwait(false);

                            kodik = linkRes.Client;
                            link = linkRes.Result;

                            _logger.LogInformation(
                                "Fallback translation succeeded. type={Type} id={Id} ep={Ep} from={FromTr} to={ToTr} reason={Reason}",
                                idType, id, ep, chosenTrId, fallbackTrId, reason);

                            chosenTrId = fallbackTrId;
                            reason = reason + "+fallback";
                            waitIfMissing = false;
                            break;
                        }
                        catch (Exception ex) when (
                            (ex is KodikException && ex is not KodikTokenException) ||
                            ex is HttpRequestException ||
                            ex is TaskCanceledException)
                        {
                            lastUpstreamError = ex;

                            _logger.LogDebug(
                                ex,
                                "Fallback translation attempt failed. type={Type} id={Id} ep={Ep} tr={TrId}",
                                idType, id, ep, fallbackTrId);
                        }
                    }
                }

                if (link == null)
                {
                    var yummyKodikFallback = await TryResolveKodikStreamFromYummyIframeAsync(
                            cfg,
                            userId,
                            idType,
                            id,
                            ep,
                            explicitTr,
                            quality,
                            format,
                            lastUpstreamError,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (yummyKodikFallback != null)
                    {
                        return yummyKodikFallback;
                    }

                    if (lastUpstreamError != null)
                    {
                        _logger.LogWarning(
                            lastUpstreamError,
                            "All link attempts failed. type={Type} id={Id} ep={Ep} chosenTr={TrId} reason={Reason}",
                            idType, id, ep, chosenTrId, reason);
                    }

                    if (waitIfMissing)
                    {
                        Response.Headers["Cache-Control"] = "no-store";
                        Response.Headers["Retry-After"] = "3600";
                        return StatusCode(503, "Preferred translation exists but is not available for this episode yet.");
                    }

                    return StatusCode(502, "Upstream error");
                }

                var fmt = (format ?? "mp4").Trim().ToLowerInvariant();
                var targetUrl = fmt == "hls"
                    ? KodikClient.BuildHlsUrl(link, quality)
                    : KodikClient.BuildMp4Url(link, quality);

                Response.Headers["Cache-Control"] = "no-store";

                if (!string.IsNullOrWhiteSpace(explicitTr))
                {
                    TrySaveTranslationId(cfg, userId, seriesKey, explicitTr);
                }

                _logger.LogInformation(
                    "Stream redirect: user={UserId} type={Type} id={Id} ep={Ep} tr={TrId} reason={Reason} -> {Url}",
                    userId,
                    idType, id, ep, chosenTrId, reason, targetUrl);

                return Redirect(targetUrl);
            }
            catch (KodikTokenException ex)
            {
                _logger.LogWarning(ex, "YummyKodik stream failed due to Kodik token. type={Type} id={Id} ep={Ep}", type, id, ep);
                if (Enum.TryParse<KodikIdType>(type, true, out var fallbackIdType) &&
                    !string.IsNullOrWhiteSpace(id))
                {
                    var fallbackCfg = Plugin.Instance.Configuration;
                    var fallbackQuality = fallbackCfg.PreferredQuality > 0 ? fallbackCfg.PreferredQuality : 720;
                    var yummyKodikFallback = await TryResolveKodikStreamFromYummyIframeAsync(
                            fallbackCfg,
                            userId,
                            fallbackIdType,
                            id,
                            ep,
                            tr,
                            fallbackQuality,
                            format,
                            ex,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (yummyKodikFallback != null)
                    {
                        return yummyKodikFallback;
                    }
                }

                return StatusCode(503, "Kodik token is missing or invalid. Configure KodikToken in plugin settings.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "YummyKodik stream failed. type={Type} id={Id} ep={Ep}", type, id, ep);
                if (string.IsNullOrWhiteSpace(provider) &&
                    Enum.TryParse<KodikIdType>(type, true, out var fallbackIdType) &&
                    !string.IsNullOrWhiteSpace(id))
                {
                    var fallbackCfg = Plugin.Instance.Configuration;
                    var fallbackQuality = fallbackCfg.PreferredQuality > 0 ? fallbackCfg.PreferredQuality : 720;
                    var yummyKodikFallback = await TryResolveKodikStreamFromYummyIframeAsync(
                            fallbackCfg,
                            userId,
                            fallbackIdType,
                            id,
                            ep,
                            tr,
                            fallbackQuality,
                            format,
                            ex,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (yummyKodikFallback != null)
                    {
                        return yummyKodikFallback;
                    }
                }

                return StatusCode(502, "Upstream error");
            }
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

            if (!_allohaPlaybackService.TryGetSession(sessionKey, out var session))
            {
                return StatusCode(410, "Alloha session expired.");
            }

            var resourceKey = (resource ?? string.Empty).Trim();
            if (resourceKey.Length == 0)
            {
                return BadRequest("resource is required");
            }

            if (!_allohaPlaybackService.TryResolveProxyResourceUrl(session, resourceKey, out var resourceUrl))
            {
                return NotFound("Alloha proxy resource was not found.");
            }

            try
            {
                var response = await _allohaPlaybackService
                    .DownloadProxyResourceAsync(session, resourceKey, resourceUrl, BuildAllohaProxyBaseUrl(), cancellationToken)
                    .ConfigureAwait(false);

                Response.Headers["Cache-Control"] = "no-store";
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
            if (!cvh.TryGetSession(sessionKey, out var session))
            {
                return StatusCode(410, "CVH session expired.");
            }

            var resourceKey = (resource ?? string.Empty).Trim();
            if (resourceKey.Length == 0)
            {
                return BadRequest("resource is required");
            }

            if (!cvh.TryResolveProxyResourceUrl(session, resourceKey, out var resourceUrl))
            {
                return NotFound("CVH proxy resource was not found.");
            }

            try
            {
                var response = await cvh
                    .DownloadProxyResourceAsync(session, resourceUrl, BuildCvhProxyBaseUrl(), cancellationToken)
                    .ConfigureAwait(false);

                Response.Headers["Cache-Control"] = "no-store";
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

        private static string BuildSeriesKey(YummyStreamRequest request)
        {
            return request.Provider switch
            {
                YummyStreamProviderKind.Cvh when request.AnimeId > 0 => BuildCvhSeriesKey(request.AnimeId),
                YummyStreamProviderKind.Alloha when request.AnimeId > 0 => BuildAllohaSeriesKey(request.AnimeId),
                YummyStreamProviderKind.Kodik when !string.IsNullOrWhiteSpace(request.KodikId)
                    => KodikPlaybackSelector.BuildSeriesKey(request.KodikIdType, request.KodikId),
                _ => string.Empty
            };
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
            if (!string.IsNullOrWhiteSpace(normalizedLeftKey) && !string.IsNullOrWhiteSpace(normalizedRightKey))
            {
                if (string.Equals(normalizedLeftKey, normalizedRightKey, StringComparison.Ordinal) ||
                    normalizedLeftKey.Contains(normalizedRightKey, StringComparison.Ordinal) ||
                    normalizedRightKey.Contains(normalizedLeftKey, StringComparison.Ordinal))
                {
                    return true;
                }
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

        private static IReadOnlyList<YummyVideoProviderKind> GetFallbackProviderOrder(YummyStreamProviderKind failedProvider)
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
            var seriesKey = BuildAllohaSeriesKey(animeId);
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedVoice = cfg.GetUserSeriesPreferredTranslationId(userId, seriesKey);
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
                TrySaveTranslationId(cfg, userId, seriesKey, requestedVoice);
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
            var seriesKey = BuildAllohaSeriesKey(animeId);
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedVoice = cfg.GetUserSeriesPreferredTranslationId(userId, seriesKey);
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
                TrySaveTranslationId(cfg, userId, seriesKey, requestedVoice);
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
            var cvhSeriesKey = BuildCvhSeriesKey(animeId);
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var savedVoice = cfg.GetUserSeriesPreferredTranslationId(userId, cvhSeriesKey);
            string? chosenVoice;
            string reason;
            YummyVideoEntry? chosenEntry;

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                if (!TryFindMatchingProviderEntry(catalog, YummyVideoProviderKind.Cvh, episode, requestedVoice, out chosenEntry))
                {
                    throw new InvalidOperationException("Requested CVH translation is unavailable from upstream.");
                }

                chosenVoice = chosenEntry!.DisplayVoiceName;
                reason = "explicit";
            }
            else
            {
                chosenVoice = catalog.PickPreferredVoiceName(
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

            Response.Headers["Cache-Control"] = "no-store";

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                TrySaveTranslationId(cfg, userId, cvhSeriesKey, requestedVoice);
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
                Response.Headers["Cache-Control"] = "no-store";
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
            var (anime, catalog) = await LoadYummyVideoContextAsync(cfg, animeId.ToString(), cancellationToken)
                .ConfigureAwait(false);
            Exception? lastError = originalError;

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

                        Response.Headers["Cache-Control"] = "no-store";
                        return Content(
                            AllohaPlaybackService.BuildManifestResponseBody(session, BuildAllohaProxyBaseUrl()),
                            "application/vnd.apple.mpegurl");
                    }
                }
                catch (Exception ex) when (IsYummyProviderFallbackException(ex))
                {
                    lastError = ex;
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
                        quality,
                        format,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsYummyProviderFallbackException(ex))
            {
                lastError = ex;
                _logger.LogWarning(
                    ex,
                    "Kodik fallback attempt failed. from={FromProvider} animeId={AnimeId} ep={Ep} requestedVoice={RequestedVoice}",
                    failedProvider,
                    animeId,
                    episode,
                    requestedVoice);
            }

            throw new InvalidOperationException("All provider fallback attempts failed.", lastError);
        }

        private async Task<IActionResult> ResolveKodikFallbackStreamAsync(
            PluginConfiguration cfg,
            Guid userId,
            YummyAnimeResponse anime,
            int episode,
            string? explicitVoiceName,
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

            var infoRes = await ExecuteWithAutoTokenRefreshAsync(
                    kodik,
                    http,
                    cfg,
                    cancellationToken,
                    k => k.GetAnimeInfoAsync(id, idType, cancellationToken))
                .ConfigureAwait(false);

            kodik = infoRes.Client;
            var info = infoRes.Result;
            var requestedVoice = (explicitVoiceName ?? string.Empty).Trim();
            var seriesKey = KodikPlaybackSelector.BuildSeriesKey(idType, id);
            var preferredTokens = StringTokenParser.ParseTokens(cfg.PreferredTranslationFilter);
            var savedTrId = cfg.GetUserSeriesPreferredTranslationId(userId, seriesKey);
            string chosenTrId;
            bool waitIfMissing;
            string reason;

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                var translation = FindKodikTranslationByVoiceName(info.Translations, requestedVoice, episode);
                if (translation == null || string.IsNullOrWhiteSpace(translation.Id))
                {
                    throw new InvalidOperationException("Requested Kodik fallback translation is unavailable from upstream.");
                }

                chosenTrId = translation.Id.Trim();
                waitIfMissing = true;
                reason = "fallback-explicit-voice";
            }
            else
            {
                (chosenTrId, waitIfMissing, reason) = KodikPlaybackSelector.PickTranslationForPlayback(
                    info.Translations,
                    preferredTokens,
                    savedTrId,
                    explicitTranslationId: string.Empty,
                    episode);
            }

            if (string.IsNullOrWhiteSpace(chosenTrId))
            {
                chosenTrId = "0";
            }

            KodikLinkInfo? link = null;
            Exception? lastUpstreamError = null;

            try
            {
                var linkRes = await ExecuteWithAutoTokenRefreshAsync(
                        kodik,
                        http,
                        cfg,
                        cancellationToken,
                        k => k.GetEpisodeLinkAsync(id, idType, episode, chosenTrId, cancellationToken))
                    .ConfigureAwait(false);

                kodik = linkRes.Client;
                link = linkRes.Result;
            }
            catch (Exception ex) when (
                (ex is KodikException && ex is not KodikTokenException) ||
                ex is HttpRequestException ||
                ex is TaskCanceledException)
            {
                lastUpstreamError = ex;
                _logger.LogWarning(
                    ex,
                    "Kodik fallback link attempt failed. type={Type} id={Id} ep={Ep} tr={TrId} reason={Reason}",
                    idType,
                    id,
                    episode,
                    chosenTrId,
                    reason);
            }

            if (link == null && string.IsNullOrWhiteSpace(requestedVoice) && !waitIfMissing)
            {
                foreach (var fallbackTrId in KodikPlaybackSelector.BuildFallbackTranslationCandidates(
                             info.Translations,
                             preferredTokens,
                             chosenTrId,
                             episode))
                {
                    try
                    {
                        var linkRes = await ExecuteWithAutoTokenRefreshAsync(
                                kodik,
                                http,
                                cfg,
                                cancellationToken,
                                k => k.GetEpisodeLinkAsync(id, idType, episode, fallbackTrId, cancellationToken))
                            .ConfigureAwait(false);

                        kodik = linkRes.Client;
                        link = linkRes.Result;
                        chosenTrId = fallbackTrId;
                        reason += "+fallback";
                        waitIfMissing = false;
                        break;
                    }
                    catch (Exception ex) when (
                        (ex is KodikException && ex is not KodikTokenException) ||
                        ex is HttpRequestException ||
                        ex is TaskCanceledException)
                    {
                        lastUpstreamError = ex;
                        _logger.LogDebug(
                            ex,
                            "Kodik fallback translation attempt failed. type={Type} id={Id} ep={Ep} tr={TrId}",
                            idType,
                            id,
                            episode,
                            fallbackTrId);
                    }
                }
            }

            if (link == null)
            {
                throw new InvalidOperationException(
                    waitIfMissing
                        ? "Preferred Kodik fallback translation is not available for this episode yet."
                        : "Kodik fallback link is unavailable.",
                    lastUpstreamError);
            }

            var fmt = (format ?? "hls").Trim().ToLowerInvariant();
            var targetUrl = fmt == "hls"
                ? KodikClient.BuildHlsUrl(link, quality)
                : KodikClient.BuildMp4Url(link, quality);

            Response.Headers["Cache-Control"] = "no-store";

            if (!string.IsNullOrWhiteSpace(requestedVoice))
            {
                TrySaveTranslationId(cfg, userId, seriesKey, chosenTrId);
            }

            _logger.LogInformation(
                "Kodik fallback stream resolved: user={UserId} type={Type} id={Id} ep={Ep} requestedVoice={RequestedVoice} tr={TrId} reason={Reason} format={Format} -> {Url}",
                userId,
                idType,
                id,
                episode,
                requestedVoice,
                chosenTrId,
                reason,
                fmt,
                targetUrl);

            return Redirect(targetUrl);
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

                Response.Headers["Cache-Control"] = "no-store";

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
