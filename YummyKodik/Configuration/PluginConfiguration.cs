// File: Configuration/PluginConfiguration.cs

using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace YummyKodik.Configuration
{
    /// <summary>
    /// Plugin configuration for YummyKodik plugin.
    /// </summary>
    public sealed class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// YummyAnime client identifier (from https://yummyani.me/dev/applications).
        /// This is the public application token (X-Application).
        /// </summary>
        public string YummyClientId { get; set; } = "e_y7qb7p9d_z1mdw";

        /// <summary>
        /// Base URL for YummyAnime REST API (api.yani.tv by default).
        /// You can change it if the API endpoint changes.
        /// </summary>
        public string YummyApiBaseUrl { get; set; } = "https://api.yani.tv";

        /// <summary>
        /// Root output path for generated STRM/NFO structure.
        /// Jellyfin library should point to this directory.
        /// </summary>
        public string OutputRootPath { get; set; } = @"D:\video\YummyKodik";

        /// <summary>
        /// Global subtitle/voice filter substring for preferred translation.
        /// Use '|' to provide multiple options (first match wins).
        /// Example: "anilibria|aniliberty".
        /// </summary>
        public string PreferredTranslationFilter { get; set; } = "anilibria|aniliberty";

        /// <summary>
        /// External Jellyfin server base URL used inside generated STRM files and runtime media sources.
        /// Example: "https://jellyfin.example.com" or "http://192.168.1.10:8096".
        /// Must be reachable by the playback client.
        /// </summary>
        public string ServerBaseUrl { get; set; } = string.Empty;

        /// <summary>
        /// Preferred quality for streaming (360, 480, 720, 1080).
        /// </summary>
        public int PreferredQuality { get; set; } = 720;

        /// <summary>
        /// Enables verbose HTTP request/response logging for Kodik client.
        /// When enabled, logs include method, URL (sanitized), optional form payload (sanitized),
        /// and response body snippet (may be large/noisy).
        /// </summary>
        public bool EnableHttpDebugLogging { get; set; } = false;

        /// <summary>
        /// If enabled, library refresh creates separate STRM and NFO files per voice translation for each episode.
        /// Example: "S01E01 - AniLibria.strm".
        /// </summary>
        public bool CreateStrmPerVoiceTranslation { get; set; } = false;

        /// <summary>
        /// List of YummyAnime slugs that should be present in the virtual library.
        /// Example: "provozhayushchaya-posledniy-put-friren".
        /// </summary>
        public List<string> Slugs { get; set; } = new();

        /// <summary>
        /// Minutes between automatic refresh runs.
        /// </summary>
        public int RefreshIntervalMinutes { get; set; } = 360;

        public string KodikToken { get; set; } = string.Empty;

        // User list subscription
        public bool UseUserListSubscription { get; set; } = false;

        public int YummyUserId { get; set; } = 219413;

        public int YummyUserListId { get; set; } = 0;

        /// <summary>
        /// Optional: user access token for private endpoints (Authorization: Bearer).
        /// If empty, plugin can login using YummyLogin and YummyPassword.
        /// </summary>
        public string YummyAccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Optional: login for /profile/login.
        /// </summary>
        public string YummyLogin { get; set; } = string.Empty;

        /// <summary>
        /// Optional: password for /profile/login.
        /// </summary>
        public string YummyPassword { get; set; } = string.Empty;

        /// <summary>
        /// Optional: recaptcha response if API starts requiring it.
        /// </summary>
        public string YummyRecaptchaResponse { get; set; } = string.Empty;

        /// <summary>
        /// Legacy global per-series preferred Kodik translation id.
        /// XmlSerializer cannot serialize Dictionary, so we store it as a list of pairs.
        /// Key format: "{idType}:{id}" in lower invariant, e.g. "shikimori:52991".
        /// </summary>
        public List<SeriesTranslationPreference> SeriesPreferredTranslations { get; set; } = new();

        /// <summary>
        /// Per-user per-series preferred Kodik translation id.
        /// Stored as list to be XmlSerializer-friendly.
        /// UserId is stored as Guid string ("N" format).
        /// </summary>
        public List<UserSeriesTranslationPreference> UserSeriesPreferredTranslations { get; set; } = new();

        public string? GetSeriesPreferredTranslationId(string seriesKey)
        {
            if (string.IsNullOrWhiteSpace(seriesKey))
            {
                return null;
            }

            var key = seriesKey.Trim().ToLowerInvariant();
            var list = SeriesPreferredTranslations ?? new List<SeriesTranslationPreference>();

            var hit = list.FirstOrDefault(x =>
                x != null &&
                !string.IsNullOrWhiteSpace(x.SeriesKey) &&
                string.Equals(x.SeriesKey.Trim().ToLowerInvariant(), key, StringComparison.Ordinal));

            var tid = hit?.TranslationId?.Trim();
            return string.IsNullOrWhiteSpace(tid) ? null : tid;
        }

        public bool SetSeriesPreferredTranslationId(string seriesKey, string translationId)
        {
            var key = (seriesKey ?? string.Empty).Trim().ToLowerInvariant();
            var tid = (translationId ?? string.Empty).Trim();

            if (key.Length == 0 || tid.Length == 0)
            {
                return false;
            }

            SeriesPreferredTranslations ??= new List<SeriesTranslationPreference>();

            var existing = SeriesPreferredTranslations.FirstOrDefault(x =>
                x != null &&
                !string.IsNullOrWhiteSpace(x.SeriesKey) &&
                string.Equals(x.SeriesKey.Trim().ToLowerInvariant(), key, StringComparison.Ordinal));

            if (existing != null)
            {
                if (string.Equals(existing.TranslationId?.Trim(), tid, StringComparison.Ordinal))
                {
                    return false;
                }

                existing.TranslationId = tid;
                return true;
            }

            SeriesPreferredTranslations.Add(new SeriesTranslationPreference
            {
                SeriesKey = key,
                TranslationId = tid
            });

            return true;
        }

        public bool ClearSeriesPreferredTranslationId(string seriesKey)
        {
            if (string.IsNullOrWhiteSpace(seriesKey))
            {
                return false;
            }

            var key = seriesKey.Trim().ToLowerInvariant();
            SeriesPreferredTranslations ??= new List<SeriesTranslationPreference>();

            var idx = SeriesPreferredTranslations.FindIndex(x =>
                x != null &&
                !string.IsNullOrWhiteSpace(x.SeriesKey) &&
                string.Equals(x.SeriesKey.Trim().ToLowerInvariant(), key, StringComparison.Ordinal));

            if (idx < 0)
            {
                return false;
            }

            SeriesPreferredTranslations.RemoveAt(idx);
            return true;
        }

        public string? GetUserSeriesPreferredTranslationId(Guid userId, string seriesKey)
        {
            if (string.IsNullOrWhiteSpace(seriesKey))
            {
                return null;
            }

            // Normalized key once.
            var key = seriesKey.Trim().ToLowerInvariant();

            // 1) Try per-user preference if we have a user id.
            if (userId != Guid.Empty)
            {
                var uid = userId.ToString("N");
                var list = UserSeriesPreferredTranslations ?? new List<UserSeriesTranslationPreference>();

                var hit = list.FirstOrDefault(x =>
                    x != null &&
                    !string.IsNullOrWhiteSpace(x.UserId) &&
                    !string.IsNullOrWhiteSpace(x.SeriesKey) &&
                    string.Equals(x.UserId.Trim(), uid, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.SeriesKey.Trim().ToLowerInvariant(), key, StringComparison.Ordinal));

                var tid = hit?.TranslationId?.Trim();
                if (!string.IsNullOrWhiteSpace(tid))
                {
                    return tid;
                }
            }

            // 2) Fallback to legacy global per-series preference (important for cases when stream request has no user context).
            return GetSeriesPreferredTranslationId(key);
        }

        /// <summary>
        /// Sets preferred translation for (user, series).
        /// If translationId is null/empty, the preference is removed (fallback to tokens and legacy global).
        /// </summary>
        public bool SetUserSeriesPreferredTranslationId(Guid userId, string seriesKey, string? translationId)
        {
            if (userId == Guid.Empty)
            {
                var tid = (translationId ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(tid))
                {
                    return ClearSeriesPreferredTranslationId(seriesKey);
                }

                return SetSeriesPreferredTranslationId(seriesKey, tid);
            }

            var uid = userId.ToString("N");
            var key = (seriesKey ?? string.Empty).Trim().ToLowerInvariant();
            var tid2 = (translationId ?? string.Empty).Trim();

            if (key.Length == 0)
            {
                return false;
            }

            UserSeriesPreferredTranslations ??= new List<UserSeriesTranslationPreference>();

            var existing = UserSeriesPreferredTranslations.FirstOrDefault(x =>
                x != null &&
                !string.IsNullOrWhiteSpace(x.UserId) &&
                !string.IsNullOrWhiteSpace(x.SeriesKey) &&
                string.Equals(x.UserId.Trim(), uid, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.SeriesKey.Trim().ToLowerInvariant(), key, StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(tid2))
            {
                if (existing == null)
                {
                    return false;
                }

                UserSeriesPreferredTranslations.Remove(existing);
                return true;
            }

            if (existing != null)
            {
                if (string.Equals(existing.TranslationId?.Trim(), tid2, StringComparison.Ordinal))
                {
                    return false;
                }

                existing.TranslationId = tid2;
                return true;
            }

            UserSeriesPreferredTranslations.Add(new UserSeriesTranslationPreference
            {
                UserId = uid,
                SeriesKey = key,
                TranslationId = tid2
            });

            return true;
        }
    }

    public sealed class SeriesTranslationPreference
    {
        public string SeriesKey { get; set; } = string.Empty;
        public string TranslationId { get; set; } = string.Empty;
    }

    public sealed class UserSeriesTranslationPreference
    {
        public string UserId { get; set; } = string.Empty;
        public string SeriesKey { get; set; } = string.Empty;
        public string TranslationId { get; set; } = string.Empty;
    }
}
