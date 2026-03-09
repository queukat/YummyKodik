// File: Tasks/RefreshYummyKodikLibraryTask.cs

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks
{
    /// <summary>
    /// Scheduled task that refreshes Yummy/Kodik-backed STRM library.
    /// </summary>
    public sealed class RefreshYummyKodikLibraryTask : IScheduledTask
    {
        private readonly ILogger<RefreshYummyKodikLibraryTask> _logger;

        public RefreshYummyKodikLibraryTask(ILogger<RefreshYummyKodikLibraryTask> logger)
        {
            _logger = logger;
        }

        public string Key => "YummyKodikRefresh";

        public string Name => "YummyKodik library refresh";

        public string Description => "Creates/updates YummyAnime based anime series and Kodik backed STRM episodes.";

        public string Category => "YummyKodik";

        public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            return ExecuteInternalAsync(progress, cancellationToken);
        }

        private async Task ExecuteInternalAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var cfg = plugin.Configuration;

            if (string.IsNullOrWhiteSpace(cfg.YummyClientId))
            {
                _logger.LogWarning("[YummyKodik] YummyClientId is not configured, skipping refresh.");
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.OutputRootPath))
            {
                _logger.LogWarning("[YummyKodik] OutputRootPath is not configured, skipping refresh.");
                return;
            }

            if (string.IsNullOrWhiteSpace(cfg.ServerBaseUrl))
            {
                _logger.LogWarning("[YummyKodik] ServerBaseUrl is not configured, skipping refresh.");
                return;
            }

            var root = cfg.OutputRootPath;
            Directory.CreateDirectory(root);

            using var http = new HttpClient();

            var yummyClient = new YummyClient(http, cfg.YummyClientId, cfg.YummyApiBaseUrl);

            var token = await KodikTokenProvider.GetTokenAsync(http, cancellationToken).ConfigureAwait(false);
            var kodikClient = new KodikClient(http, token);

            var allKeys = await BuildAnimeKeysAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);
            if (allKeys.Count == 0)
            {
                _logger.LogInformation("[YummyKodik] No slugs or list items configured, nothing to refresh.");
                return;
            }

            var perItemStep = 100.0 / allKeys.Count;
            var idx = 0;

            foreach (var key in allKeys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                idx++;

                progress?.Report(perItemStep * (idx - 1));

                try
                {
                    await RefreshSingleAnimeAsync(
                            key,
                            root,
                            yummyClient,
                            kodikClient,
                            cfg.PreferredQuality,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[YummyKodik] Failed to refresh key '{Key}': {Message}", key, ex.Message);
                }

                progress?.Report(perItemStep * idx);
            }
        }

        private async Task<List<string>> BuildAnimeKeysAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            CancellationToken cancellationToken)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (cfg.Slugs != null)
            {
                foreach (var s in cfg.Slugs)
                {
                    var v = NormalizeKey(s);
                    if (!string.IsNullOrEmpty(v))
                    {
                        set.Add(v);
                    }
                }
            }

            if (cfg.UseUserListSubscription)
            {
                if (cfg.YummyUserId <= 0)
                {
                    _logger.LogWarning("[YummyKodik] UseUserListSubscription enabled, but YummyUserId is not set.");
                    return set.ToList();
                }

                var listId = cfg.YummyUserListId < 0 ? 0 : cfg.YummyUserListId;

                try
                {
                    await EnsureAuthenticatedAsync(cfg, yummyClient, cancellationToken).ConfigureAwait(false);

                    IReadOnlyList<YummyUserListItem> items;

                    try
                    {
                        items = await yummyClient
                            .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (UnauthorizedAccessException)
                    {
                        if (!string.IsNullOrWhiteSpace(yummyClient.GetAccessToken()))
                        {
                            _logger.LogWarning("[YummyKodik] User list unauthorized, trying token refresh and retry.");
                            await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

                            items = await yummyClient
                                .GetUserListAsync(cfg.YummyUserId, listId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                        else
                        {
                            throw;
                        }
                    }

                    _logger.LogInformation(
                        "[YummyKodik] User list fetched. userId={UserId} listId={ListId} items={Count}",
                        cfg.YummyUserId,
                        listId,
                        items.Count);

                    foreach (var item in items)
                    {
                        var k = NormalizeKey(item.AnimeUrl);
                        if (!string.IsNullOrEmpty(k))
                        {
                            set.Add(k);
                            continue;
                        }

                        if (item.AnimeId > 0)
                        {
                            set.Add(item.AnimeId.ToString());
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[YummyKodik] Failed to fetch user list, falling back to manual slugs only.");
                }
            }

            return set.ToList();
        }

        private async Task EnsureAuthenticatedAsync(
            PluginConfiguration cfg,
            YummyClient yummyClient,
            CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;

            if (!string.IsNullOrWhiteSpace(cfg.YummyAccessToken))
            {
                yummyClient.SetAccessToken(cfg.YummyAccessToken);

                try
                {
                    var refreshed = await yummyClient.RefreshTokenAsync(cancellationToken).ConfigureAwait(false);

                    if (!string.Equals(cfg.YummyAccessToken, refreshed, StringComparison.Ordinal))
                    {
                        cfg.YummyAccessToken = refreshed;
                        plugin.SaveConfiguration();
                        _logger.LogInformation("[YummyKodik] Yummy access token refreshed and saved.");
                    }

                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[YummyKodik] Token refresh failed, will try login if credentials exist.");
                }
            }

            if (!string.IsNullOrWhiteSpace(cfg.YummyLogin) && !string.IsNullOrWhiteSpace(cfg.YummyPassword))
            {
                _logger.LogInformation("[YummyKodik] Logging in to Yummy to obtain user token.");

                var token = await yummyClient.LoginAsync(
                        cfg.YummyLogin.Trim(),
                        cfg.YummyPassword,
                        string.IsNullOrWhiteSpace(cfg.YummyRecaptchaResponse) ? null : cfg.YummyRecaptchaResponse.Trim(),
                        cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(token))
                {
                    cfg.YummyAccessToken = token;
                    plugin.SaveConfiguration();
                    _logger.LogInformation("[YummyKodik] Yummy access token obtained and saved.");
                }

                return;
            }

            _logger.LogWarning(
                "[YummyKodik] User list subscription enabled but no auth is configured. Set YummyAccessToken or YummyLogin and YummyPassword.");
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            var cfg = Plugin.Instance.Configuration;

            yield return new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(cfg.RefreshIntervalMinutes <= 0 ? 360 : cfg.RefreshIntervalMinutes).Ticks
            };
        }

        private async Task RefreshSingleAnimeAsync(
            string key,
            string root,
            YummyClient yummy,
            KodikClient kodik,
            int preferredQuality,
            CancellationToken cancellationToken)
        {
            var plugin = Plugin.Instance;
            var logger = plugin.Logger;

            var cleanKey = NormalizeKey(key);
            logger.LogInformation("[YummyKodik] Refreshing key '{Key}'.", cleanKey);

            var anime = await yummy.GetAnimeAsync(cleanKey, cancellationToken).ConfigureAwait(false);
            var title = string.IsNullOrWhiteSpace(anime.Title) ? cleanKey : anime.Title;

            var seasonNumber = ExtractSeasonNumberFromTitle(title);
            if (seasonNumber != 1)
            {
                logger.LogInformation("[YummyKodik] Season number detected from title. title='{Title}' season={Season}", title, seasonNumber);
            }

            KodikIdType idType;
            string id;

            if (TryPickKodikIdFromRemoteIds(anime.RemoteIds, out idType, out id))
            {
                logger.LogInformation(
                    "[YummyKodik] Using remote id from Yummy. title='{Title}' idType={IdType} id={Id}",
                    title, idType, id);
            }
            else
            {
                logger.LogWarning(
                    "[YummyKodik] remote_ids are missing for '{Title}' (key='{Key}'). Falling back to Kodik title search.",
                    title, cleanKey);

                var resolved = await KodikTitleResolver.ResolveIdAsync(
                        cleanKey,
                        title,
                        kodik,
                        cancellationToken)
                    .ConfigureAwait(false);

                idType = resolved.IdType;
                id = resolved.Id;
            }

            var info = await kodik.GetAnimeInfoAsync(id, idType, cancellationToken).ConfigureAwait(false);

            if (info.SeriesCount <= 0)
            {
                logger.LogInformation(
                    "[YummyKodik] Anime '{Title}' appears to be a movie (no series). Skipping STRM generation.",
                    title);
                return;
            }

            var folderName = BuildSeriesFolderName(title, anime);
            var safeFolderName = SafeFilename(folderName);

            var seriesRoot = Path.Combine(root, safeFolderName);

            // миграция: если раньше папка была только по title, переносим в новую
            var legacySeriesRoot = Path.Combine(root, SafeFilename(title));
            if (!Directory.Exists(seriesRoot) && Directory.Exists(legacySeriesRoot))
            {
                try
                {
                    Directory.Move(legacySeriesRoot, seriesRoot);
                    logger.LogInformation("[YummyKodik] Renamed legacy folder '{Old}' -> '{New}'.", legacySeriesRoot, seriesRoot);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[YummyKodik] Failed to rename legacy folder '{Old}' -> '{New}'. Will use new folder.", legacySeriesRoot, seriesRoot);
                }
            }

            Directory.CreateDirectory(seriesRoot);

            var seasonDirName = $"Season {seasonNumber:00}";
            var seasonDir = Path.Combine(seriesRoot, seasonDirName);

            // миграция: если раньше всегда писали Season 01, а теперь сезон распознан как другой, переносим папку сезона
            MigrateLegacySeasonFolder(logger, seriesRoot, seasonDir, seasonNumber);

            Directory.CreateDirectory(seasonDir);

            // миграция: переименование файлов S01E.. -> S{season:00}E..
            MigrateLegacyEpisodeFileNames(logger, seasonDir, seasonNumber);

            await EnsurePosterAsync(anime, seriesRoot, cancellationToken).ConfigureAwait(false);
            await EnsureTvShowNfoAsync(anime, seriesRoot, cancellationToken).ConfigureAwait(false);

            var cfg = Plugin.Instance.Configuration;

            var baseUrl = (cfg.ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
            {
                logger.LogWarning("[YummyKodik] ServerBaseUrl is empty, skipping refresh for '{Title}'.", title);
                return;
            }

            for (var ep = 1; ep <= info.SeriesCount; ep++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var baseName = $"S{seasonNumber:00}E{ep:00}";

                var streamBase =
                    $"{baseUrl}/YummyKodik/stream?type={idType.ToString().ToLowerInvariant()}" +
                    $"&id={Uri.EscapeDataString(id)}&ep={ep}";

                if (!cfg.CreateStrmPerVoiceTranslation)
                {
                    var strmPath = Path.Combine(seasonDir, baseName + ".strm");
                    var nfoPath = Path.Combine(seasonDir, baseName + ".nfo");

                    var url = streamBase + "&format=hls";

                    await File.WriteAllTextAsync(strmPath, url + Environment.NewLine, cancellationToken).ConfigureAwait(false);

                    if (!File.Exists(nfoPath))
                    {
                        var nfo = NfoBuilder.BuildEpisodeNfo(
                            ep,
                            season: seasonNumber,
                            seriesTitle: title,
                            description: anime.Description ?? string.Empty);

                        await File.WriteAllTextAsync(nfoPath, nfo, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                var fileTranslations = PickTranslationsForFileMode(info.Translations);

                if (fileTranslations.Count == 0)
                {
                    var fileBaseName = baseName + " - Auto";
                    var strmPath = Path.Combine(seasonDir, fileBaseName + ".strm");
                    var nfoPath = Path.Combine(seasonDir, fileBaseName + ".nfo");

                    var url = streamBase + "&format=hls";

                    await File.WriteAllTextAsync(strmPath, url + Environment.NewLine, cancellationToken).ConfigureAwait(false);

                    if (!File.Exists(nfoPath))
                    {
                        var nfo = NfoBuilder.BuildEpisodeNfo(
                            ep,
                            season: seasonNumber,
                            seriesTitle: title,
                            description: anime.Description ?? string.Empty);

                        await File.WriteAllTextAsync(nfoPath, nfo, cancellationToken).ConfigureAwait(false);
                    }

                    continue;
                }

                foreach (var tr in fileTranslations)
                {
                    var trId = (tr.Id ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(trId))
                    {
                        continue;
                    }
                
                    var suffixRaw = BuildTranslationFileSuffix(tr);
                    var suffix = SafeFilename(suffixRaw);
                    if (string.IsNullOrWhiteSpace(suffix))
                    {
                        suffix = "Translation_" + trId;
                    }
                
                    var fileBaseName = baseName + " - " + suffix;
                
                    var strmPath = Path.Combine(seasonDir, fileBaseName + ".strm");
                    var nfoPath = Path.Combine(seasonDir, fileBaseName + ".nfo");
                
                    // ✅ ВАЖНО: не создаём файлы на перевод, если этот перевод ещё не дошёл до ep.
                    // MaxEpisode<=0 считаем "неизвестно" -> не фильтруем.
                    if (tr.MaxEpisode > 0 && ep > tr.MaxEpisode)
                    {
                        // (опционально, но полезно) убираем ранее созданные плейсхолдеры, чтобы они не висели в библиотеке
                        TryDeleteFile(logger, strmPath);
                        TryDeleteFile(logger, nfoPath);
                        continue;
                    }
                
                    var url = streamBase + $"&tr={Uri.EscapeDataString(trId)}&format=hls";
                
                    await File.WriteAllTextAsync(strmPath, url + Environment.NewLine, cancellationToken).ConfigureAwait(false);
                
                    if (!File.Exists(nfoPath))
                    {
                        var nfo = NfoBuilder.BuildEpisodeNfo(
                            ep,
                            season: seasonNumber,
                            seriesTitle: title,
                            description: anime.Description ?? string.Empty);
                
                        await File.WriteAllTextAsync(nfoPath, nfo, cancellationToken).ConfigureAwait(false);
                    }
                }

            }

            logger.LogInformation(
                "[YummyKodik] Done refreshing '{Title}'. SeriesCount: {SeriesCount}, translations: {Translations}.",
                title,
                info.SeriesCount,
                info.Translations.Count);
        }

        private static int ExtractSeasonNumberFromTitle(string? title)
        {
            var s = (title ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                return 1;
            }

            // Rule: "<space><digits>" at the end is season number.
            var m = Regex.Match(s, @"\s(?<season>\d+)$", RegexOptions.CultureInvariant);
            if (!m.Success)
            {
                return 1;
            }

            if (!int.TryParse(m.Groups["season"].Value, out var season) || season <= 0)
            {
                return 1;
            }

            return season;
        }
        
        private static void TryDeleteFile(ILogger logger, string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                    logger.LogDebug("[YummyKodik] Deleted stale placeholder file '{Path}'.", path);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to delete file '{Path}'.", path);
            }
        }


        private static void MigrateLegacySeasonFolder(ILogger logger, string seriesRoot, string seasonDir, int seasonNumber)
        {
            if (seasonNumber == 1)
            {
                return;
            }

            var legacySeasonDir = Path.Combine(seriesRoot, "Season 01");

            if (Directory.Exists(seasonDir))
            {
                return;
            }

            if (!Directory.Exists(legacySeasonDir))
            {
                return;
            }

            try
            {
                Directory.Move(legacySeasonDir, seasonDir);
                logger.LogInformation("[YummyKodik] Renamed legacy season folder '{Old}' -> '{New}'.", legacySeasonDir, seasonDir);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[YummyKodik] Failed to rename legacy season folder '{Old}' -> '{New}'.", legacySeasonDir, seasonDir);
            }
        }

        private static void MigrateLegacyEpisodeFileNames(ILogger logger, string seasonDir, int seasonNumber)
        {
            if (seasonNumber == 1)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
            {
                return;
            }

            string newSeasonPrefix = "S" + seasonNumber.ToString("00");

            try
            {
                var files = Directory.EnumerateFiles(seasonDir, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(p =>
                    {
                        var ext = Path.GetExtension(p);
                        return ext.Equals(".strm", StringComparison.OrdinalIgnoreCase) ||
                               ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                foreach (var path in files)
                {
                    var fileName = Path.GetFileName(path);
                    if (string.IsNullOrWhiteSpace(fileName))
                    {
                        continue;
                    }

                    // Rename only "S01E..." into "S{season:00}E..."
                    if (!fileName.StartsWith("S01E", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var ext = Path.GetExtension(path);
                    var nameNoExt = Path.GetFileNameWithoutExtension(path) ?? string.Empty;

                    // Keep everything after "S01" intact, replace only season part.
                    // Example:
                    //   S01E01
                    //   S01E01 - AniLibria
                    var renamedNoExt = newSeasonPrefix + nameNoExt.Substring(3);

                    var target = Path.Combine(seasonDir, renamedNoExt + ext);

                    if (File.Exists(target))
                    {
                        // Prefer target, remove legacy duplicate.
                        try
                        {
                            File.Delete(path);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "[YummyKodik] Failed to delete legacy file '{Path}'.", path);
                        }

                        continue;
                    }

                    try
                    {
                        File.Move(path, target);
                    }
                    catch (Exception ex)
                    {
                        logger.LogDebug(ex, "[YummyKodik] Failed to rename file '{Old}' -> '{New}'.", path, target);
                        continue;
                    }

                    if (ext.Equals(".nfo", StringComparison.OrdinalIgnoreCase))
                    {
                        TryUpdateEpisodeNfoSeason(logger, target, seasonNumber);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Season file migration failed. seasonDir='{SeasonDir}' season={Season}", seasonDir, seasonNumber);
            }
        }

        private static void TryUpdateEpisodeNfoSeason(ILogger logger, string nfoPath, int seasonNumber)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(nfoPath) || !File.Exists(nfoPath))
                {
                    return;
                }

                var xml = File.ReadAllText(nfoPath);

                // Replace first <season>...</season> with correct value.
                var updated = Regex.Replace(
                    xml,
                    @"<season>\s*\d+\s*</season>",
                    "<season>" + seasonNumber + "</season>",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
                    matchTimeout: TimeSpan.FromSeconds(1));

                if (!string.Equals(xml, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(nfoPath, updated);
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to update season number inside nfo '{Path}'.", nfoPath);
            }
        }

        private static IReadOnlyList<KodikTranslation> PickTranslationsForFileMode(IReadOnlyList<KodikTranslation> translations)
        {
            translations ??= Array.Empty<KodikTranslation>();

            var voice = translations
                .Where(t => string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase))
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .Where(t => !string.Equals(t.Id.Trim(), "0", StringComparison.Ordinal))
                .ToList();

            if (voice.Count > 0)
            {
                return voice;
            }

            return translations
                .Where(t => !string.IsNullOrWhiteSpace(t.Id))
                .Where(t => !string.Equals(t.Id.Trim(), "0", StringComparison.Ordinal))
                .ToList();
        }

        private static string BuildTranslationFileSuffix(KodikTranslation t)
        {
            var name = (t.Name ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                var tid = (t.Id ?? string.Empty).Trim();
                name = string.IsNullOrWhiteSpace(tid) ? "Translation" : ("tr" + tid);
            }

            var type = (t.Type ?? string.Empty).Trim();
            if (type.Length == 0 || type.Equals("voice", StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }

            if (type.Equals("subtitles", StringComparison.OrdinalIgnoreCase))
            {
                return name + " [subs]";
            }

            return name + " [" + type + "]";
        }

        private static string BuildSeriesFolderName(string title, YummyAnimeResponse anime)
        {
            var baseTitle = (title ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(baseTitle))
            {
                baseTitle = NormalizeKey(anime?.AnimeUrl);
            }

            var tag = BuildBestIdTag(anime);
            return string.IsNullOrEmpty(tag) ? baseTitle : $"{baseTitle} {tag}";
        }

        private static string BuildBestIdTag(YummyAnimeResponse anime)
        {
            var r = anime?.RemoteIds;

            if (r?.ShikimoriId is long shiki && shiki > 0)
            {
                return $"[shikimoriid-{shiki}]";
            }

            if (r?.KpId is long kp && kp > 0)
            {
                return $"[kp-{kp}]";
            }

            if (!string.IsNullOrWhiteSpace(r?.ImdbId))
            {
                return $"[imdbid-{r.ImdbId.Trim()}]";
            }

            if (anime != null && anime.AnimeId > 0)
            {
                return $"[yaniid-{anime.AnimeId}]";
            }

            return string.Empty;
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

        private static string SafeFilename(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(c, '_');
            }

            return name.Trim();
        }

        private static async Task EnsurePosterAsync(
            YummyAnimeResponse anime,
            string seriesRoot,
            CancellationToken cancellationToken)
        {
            var posterPath = Path.Combine(seriesRoot, "poster.jpg");
            if (File.Exists(posterPath))
            {
                return;
            }

            var url = YummyClient.PickBestPosterUrl(anime);
            if (string.IsNullOrEmpty(url))
            {
                return;
            }

            using var http = new HttpClient();
            var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            await using var fs = File.OpenWrite(posterPath);
            await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        }

        private static async Task EnsureTvShowNfoAsync(
            YummyAnimeResponse anime,
            string seriesRoot,
            CancellationToken cancellationToken)
        {
            var nfoPath = Path.Combine(seriesRoot, "tvshow.nfo");
            if (File.Exists(nfoPath))
            {
                return;
            }

            var xml = NfoBuilder.BuildSeriesNfo(anime.Title, anime.Description ?? string.Empty);
            await File.WriteAllTextAsync(nfoPath, xml, cancellationToken).ConfigureAwait(false);
        }

        private static string NormalizeKey(string? s)
        {
            var v = (s ?? string.Empty).Trim();

            // remove wrapping quotes if any
            v = v.Trim().Trim('"', '\'', '“', '”');

            // remove trailing slashes
            v = v.Trim('/');

            return v;
        }
    }
}
