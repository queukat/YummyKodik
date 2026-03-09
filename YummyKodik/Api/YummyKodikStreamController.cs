// File: Api/YummyKodikStreamController.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;

namespace YummyKodik.Api
{
    [ApiController]
    public sealed class YummyKodikStreamController : ControllerBase
    {
        private readonly ILogger<YummyKodikStreamController> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly ILibraryManager _libraryManager;

        private static readonly object PrefsLock = new();

        public YummyKodikStreamController(
            ILogger<YummyKodikStreamController> logger,
            IHttpClientFactory httpClientFactory,
            IAuthorizationContext authorizationContext,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _authorizationContext = authorizationContext;
            _libraryManager = libraryManager;
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
                var (idType, id, seriesKey) = await ResolveSeriesFromJellyfinAsync(seriesId, cancellationToken).ConfigureAwait(false);

                var cfg = Plugin.Instance.Configuration;

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
                    idType = idType.ToString().ToLowerInvariant(),
                    id,
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
                var (_, _, seriesKey) = await ResolveSeriesFromJellyfinAsync(seriesId, cancellationToken).ConfigureAwait(false);

                var cfg = Plugin.Instance.Configuration;
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
            [FromQuery] string type,
            [FromQuery] string id,
            [FromQuery] int ep,
            [FromQuery] string? tr = null,
            [FromQuery] string? format = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id) || ep < 0)
            {
                return BadRequest("type, id, ep are required (ep must be >= 0)");
            }

            if (!Enum.TryParse<KodikIdType>(type, true, out var idType))
            {
                return BadRequest("unknown type");
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
                return StatusCode(503, "Kodik token is missing or invalid. Configure KodikToken in plugin settings.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "YummyKodik stream failed. type={Type} id={Id} ep={Ep}", type, id, ep);
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

        private async Task<(KodikIdType IdType, string Id, string SeriesKey)> ResolveSeriesFromJellyfinAsync(
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

            var seriesPath = (item.Path ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(seriesPath) || !Directory.Exists(seriesPath))
            {
                throw new InvalidOperationException("Series path is not a directory");
            }

            var cfg = Plugin.Instance.Configuration;
            var root = (cfg.OutputRootPath ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(root))
            {
                var fullRoot = Path.GetFullPath(root);
                var fullSeries = Path.GetFullPath(seriesPath);

                if (!fullSeries.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Series path is outside plugin output root");
                }
            }

            var strmFile = Directory.EnumerateFiles(seriesPath, "*.strm", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(strmFile) || !System.IO.File.Exists(strmFile))
            {
                throw new InvalidOperationException("No .strm files found for this series");
            }

            var content = (await System.IO.File.ReadAllTextAsync(strmFile, cancellationToken).ConfigureAwait(false)).Trim();

            if (!YummyKodikStreamUri.TryParse(content, out var idType, out var id, out _))
            {
                throw new InvalidOperationException("Failed to parse Kodik id from .strm content");
            }

            var seriesKey = KodikPlaybackSelector.BuildSeriesKey(idType, id);
            return (idType, id, seriesKey);
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
    }
}
