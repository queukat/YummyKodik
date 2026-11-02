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
using YummyKodik.Logging;
using YummyKodik.Util;
using YummyKodik.Versioning;
using YummyKodik.Yummy;

namespace YummyKodik.Api
{
    public sealed partial class YummyKodikStreamController
    {
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
                var managedStreams = GetManagedSeriesStreams(seriesId, cfg, out _);
                var managedPreferenceKeys = BuildManagedPreferenceKeys(request, managedStreams);
                var translations = BuildTranslationOptions(managedStreams.Select(x => x.VoiceName));
                var saved = GetSavedTranslationPreference(cfg, auth.UserId, seriesKey, managedPreferenceKeys);
                var savedTranslationId = ResolveWidgetTranslationId(
                    ResolveVoiceNameForSelection(saved == "0" ? null : saved, managedStreams), translations);
                // This endpoint describes known library voices. Provider availability belongs to
                // refresh/playback; opening a details page must never request a remote catalogue.
                return Ok(new
                {
                    seriesKey,
                    idType = request.Provider == YummyStreamProviderKind.Kodik
                        ? request.KodikIdType.ToString().ToLowerInvariant()
                        : request.Provider.ToString().ToLowerInvariant(),
                    id = request.AnimeId > 0 ? request.AnimeId.ToString() : request.KodikId,
                    savedTranslationId,
                    chosenTranslationId = string.Empty,
                    reason = translations.Count == 0 ? "local-empty" : "local",
                    translations
                });
            }
            catch (SeriesNotManagedByYummyKodikException ex)
            {
                _logger.LogDebug(ex, "GetTranslations skipped for non-YummyKodik series. seriesId={SeriesId}", seriesId);
                return Ok(BuildEmptyTranslationsResponse("not-managed"));
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "GetTranslations skipped for invalid series id. seriesId={SeriesId}", seriesId);
                return BadRequest("seriesId is not a valid GUID");
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
                var managedStreams = GetManagedSeriesStreams(seriesId, cfg, out var seriesDirectory);
                var managedPreferenceKeys = BuildManagedPreferenceKeys(request, managedStreams);
                var selectedVoiceName = ResolveVoiceNameForSelection(tid, managedStreams);

                var changed = Plugin.Instance.UpdateTranslationPreferences(current =>
                    SetLibraryWideTranslationPreference(current, managedPreferenceKeys, selectedVoiceName));

                // Reapply an explicit choice even if it was already stored: a
                // startup/refresh merge may still be working through other titles.
                _episodeVersionsMergeService.RequestTranslationPreferenceMerge(seriesDirectory);

                return Ok(new
                {
                    changed,
                    seriesKey,
                    translationId = selectedVoiceName
                });
            }
            catch (SeriesNotManagedByYummyKodikException ex)
            {
                _logger.LogDebug(ex, "SetTranslation skipped for non-YummyKodik series. seriesId={SeriesId} tr={Tr}", seriesId, tr);
                return NotFound("Series is not managed by YummyKodik");
            }
            catch (ArgumentException ex)
            {
                _logger.LogDebug(ex, "SetTranslation skipped for invalid series id. seriesId={SeriesId} tr={Tr}", seriesId, tr);
                return BadRequest("seriesId is not a valid GUID");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SetTranslation failed. seriesId={SeriesId} tr={Tr}", seriesId, tr);
                return StatusCode(500, "Failed to save translation");
            }
        }

        private IReadOnlyList<ManagedSeriesStream> GetManagedSeriesStreams(
            string seriesId,
            PluginConfiguration cfg,
            out string? seriesDirectory)
        {
            seriesDirectory = null;
            if (!Guid.TryParse(seriesId, out var itemId) || itemId == Guid.Empty)
            {
                return Array.Empty<ManagedSeriesStream>();
            }

            var item = _libraryManager.GetItemById(itemId);
            var targets = cfg.GetLibraryTargets();
            var representativePath = item == null ? null : FindFirstStrmFileForItem(item, targets);
            var seasonDirectory = string.IsNullOrWhiteSpace(representativePath)
                ? null
                : Path.GetDirectoryName(representativePath);
            seriesDirectory = string.IsNullOrWhiteSpace(seasonDirectory)
                ? null
                : Directory.GetParent(seasonDirectory)?.FullName;

            var candidateDirectory = seriesDirectory;
            if (string.IsNullOrWhiteSpace(candidateDirectory) ||
                !targets.Any(target => target.ContainsPath(candidateDirectory)))
            {
                seriesDirectory = null;
                return Array.Empty<ManagedSeriesStream>();
            }

            var streams = new List<ManagedSeriesStream>();
            // Voice names and preference keys come from STRM contents, not host episode metadata.
            // Include every local version in this proven series root without materializing Jellyfin items.
            foreach (var path in Directory.EnumerateFiles(candidateDirectory, "*.strm", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                if (!LibraryTarget.ContainsPath(seriesDirectory, path))
                {
                    continue;
                }

                if (TryReadManagedSeriesStream(path, out var stream))
                {
                    streams.Add(stream);
                }
            }

            return streams;
        }

        private static bool TryReadManagedSeriesStream(string path, out ManagedSeriesStream stream)
        {
            stream = default!;
            try
            {
                var line = System.IO.File.ReadLines(path)
                    .Select(x => (x ?? string.Empty).Trim())
                    .FirstOrDefault(x => x.Length > 0);
                if (string.IsNullOrWhiteSpace(line) ||
                    !YummyKodikStreamUri.TryParseRequest(line, out var request))
                {
                    return false;
                }

                var voiceName = YummyVideoCatalog.NormalizeVoiceName(request.VoiceName);
                if (string.IsNullOrWhiteSpace(voiceName))
                {
                    voiceName = YummyVideoCatalog.NormalizeVoiceName(ExtractVoiceNameFromEpisodeFileName(path));
                }

                var kodikTranslationId = string.Empty;
                if (request.Provider == YummyStreamProviderKind.Kodik &&
                    Uri.TryCreate(line, UriKind.Absolute, out var uri))
                {
                    var query = YummyKodikStreamUri.ParseQueryToDictionary(uri.Query);
                    kodikTranslationId = query.TryGetValue("tr", out var value)
                        ? (value ?? string.Empty).Trim()
                        : string.Empty;
                }

                stream = new ManagedSeriesStream(request, voiceName, kodikTranslationId);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static IReadOnlyList<string> BuildManagedPreferenceKeys(
            YummyStreamRequest representativeRequest,
            IEnumerable<ManagedSeriesStream> managedStreams)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddPreferenceKeys(representativeRequest, keys);

            foreach (var stream in managedStreams)
            {
                AddPreferenceKeys(stream.Request, keys);
            }

            return keys.ToArray();
        }

        private static void AddPreferenceKeys(YummyStreamRequest request, ISet<string> keys)
        {
            if (IsYummyProviderRequest(request))
            {
                foreach (var yummyKey in EnumerateYummyPreferenceKeys(
                             request.AnimeId,
                             request.Provider,
                             includeAllLegacyProviderKeys: true))
                {
                    keys.Add(yummyKey);
                }

                return;
            }

            var key = BuildSeriesKey(request);
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }
        }

        private static string? GetSavedTranslationPreference(
            PluginConfiguration cfg,
            Guid userId,
            string primarySeriesKey,
            IEnumerable<string> managedPreferenceKeys)
        {
            var keys = new[] { primarySeriesKey }
                .Concat(managedPreferenceKeys)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase);

            foreach (var key in keys)
            {
                var saved = cfg.GetUserSeriesPreferredTranslationId(userId, key);
                if (!string.IsNullOrWhiteSpace(saved))
                {
                    return saved.Trim();
                }
            }

            return null;
        }

        private static bool SetLibraryWideTranslationPreference(
            PluginConfiguration cfg,
            IEnumerable<string> managedPreferenceKeys,
            string? voiceName)
        {
            var keys = managedPreferenceKeys
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim().ToLowerInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (keys.Length == 0)
            {
                return false;
            }

            var keySet = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            // PrimaryVersionId is library-global. A widget selection therefore replaces stale
            // per-user values for this managed series before mirroring one canonical default to
            // every provider key. The merge uses that default to choose primary versions, while
            // an explicitly requested per-voice STRM remains authoritative at the gateway.
            cfg.UserSeriesPreferredTranslations ??= new List<UserSeriesTranslationPreference>();
            for (var i = cfg.UserSeriesPreferredTranslations.Count - 1; i >= 0; i--)
            {
                var preference = cfg.UserSeriesPreferredTranslations[i];
                var key = (preference?.SeriesKey ?? string.Empty).Trim().ToLowerInvariant();
                if (keySet.Contains(key))
                {
                    cfg.UserSeriesPreferredTranslations.RemoveAt(i);
                    changed = true;
                }
            }

            var value = (voiceName ?? string.Empty).Trim();
            foreach (var key in keys)
            {
                changed |= cfg.SetUserSeriesPreferredTranslationId(
                    Guid.Empty,
                    key,
                    string.IsNullOrWhiteSpace(value) ? null : value);
            }

            return changed;
        }

        private static string PickGatewayVoice(string? explicitVoiceName, string? savedVoiceName)
        {
            var explicitVoice = (explicitVoiceName ?? string.Empty).Trim();
            return explicitVoice.Length > 0
                ? explicitVoice
                : (savedVoiceName ?? string.Empty).Trim();
        }

        private static IReadOnlyList<TranslationOption> BuildTranslationOptions(IEnumerable<string> managedVoiceNames)
        {
            var result = new List<TranslationOption>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var rawName in managedVoiceNames)
            {
                var name = YummyVideoCatalog.NormalizeVoiceName(rawName);
                var key = TranslationNameKeyNormalizer.Normalize(name);
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(key) || !keys.Add(key))
                {
                    continue;
                }

                // The widget deliberately uses a voice name as its id. A numeric Kodik id is
                // provider-specific and cannot select matching Yummy/CVH/Alloha file versions.
                result.Add(new TranslationOption(name, name, "voice"));
            }

            return result;
        }

        private static string ResolveWidgetTranslationId(
            string? selection,
            IReadOnlyList<TranslationOption> translations)
        {
            var value = (selection ?? string.Empty).Trim();
            return TranslationNameKeyNormalizer.FindEquivalent(
                       value,
                       translations.Select(option => option.Id))
                   ?? value;
        }

        private static string ResolveVoiceNameForSelection(
            string? selection,
            IEnumerable<ManagedSeriesStream> managedStreams)
        {
            var value = (selection ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return string.Empty;
            }

            foreach (var stream in managedStreams)
            {
                if (!string.IsNullOrWhiteSpace(stream.VoiceName) &&
                    (VoiceNamesEquivalent(value, stream.VoiceName) ||
                     string.Equals(value, stream.KodikTranslationId, StringComparison.Ordinal)))
                {
                    return stream.VoiceName;
                }
            }

            return YummyVideoCatalog.NormalizeVoiceName(value);
        }
    }
}
