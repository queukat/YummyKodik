using System;
using System.Collections.Generic;
using System.Linq;

namespace YummyKodik.Kodik;

internal static class KodikPlaybackSelector
{
    public static string BuildSeriesKey(KodikIdType idType, string id)
    {
        var left = idType.ToString().ToLowerInvariant();
        var right = (id ?? string.Empty).Trim().ToLowerInvariant();
        return $"{left}:{right}";
    }

    public static (string TranslationId, bool WaitIfMissing, string Reason) PickTranslationForPlayback(
        IReadOnlyList<KodikTranslation> translations,
        string[] preferredTokens,
        string? savedTranslationId,
        string explicitTranslationId,
        int episode)
    {
        if (translations == null || translations.Count == 0)
        {
            return ("0", false, "no-translations");
        }

        var ep = episode <= 0 ? 1 : episode;
        var reasonPrefix = string.Empty;

        KodikTranslation? FindById(string tid)
        {
            var needle = (tid ?? string.Empty).Trim();
            if (needle.Length == 0)
            {
                return null;
            }

            return translations.FirstOrDefault(t =>
                string.Equals((t.Id ?? string.Empty).Trim(), needle, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(explicitTranslationId))
        {
            var tid = explicitTranslationId.Trim();
            var trObj = FindById(tid);
            if (trObj != null)
            {
                if (CoversEpisode(trObj, ep))
                {
                    return (tid, true, "explicit");
                }

                reasonPrefix = "explicit-beyond-max+";
            }
            else
            {
                reasonPrefix = "explicit-missing+";
            }
        }

        if (!string.IsNullOrWhiteSpace(savedTranslationId))
        {
            var saved = savedTranslationId.Trim();
            var trObj = FindById(saved);
            if (trObj != null)
            {
                if (CoversEpisode(trObj, ep))
                {
                    return (saved, true, reasonPrefix + "saved");
                }

                reasonPrefix += "saved-beyond-max+";
            }
        }

        if (preferredTokens.Length > 0)
        {
            foreach (var token in preferredTokens)
            {
                var hit = FindBestMatchByToken(translations, token, ep);
                var hitId = (hit?.Id ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(hitId))
                {
                    return (hitId, true, reasonPrefix + "preferred-token");
                }
            }
        }

        var voice = translations.FirstOrDefault(t =>
            string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Id) &&
            CoversEpisode(t, ep));

        if (voice != null && !string.IsNullOrWhiteSpace(voice.Id))
        {
            return (voice.Id.Trim(), false, reasonPrefix + "fallback-voice");
        }

        var firstCovering = translations.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.Id) &&
            CoversEpisode(t, ep));

        if (firstCovering != null && !string.IsNullOrWhiteSpace(firstCovering.Id))
        {
            return (firstCovering.Id.Trim(), false, reasonPrefix + "fallback-first");
        }

        var anyId = (translations.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Id))?.Id ?? "0").Trim();
        if (string.IsNullOrWhiteSpace(anyId))
        {
            anyId = "0";
        }

        return (anyId, true, reasonPrefix + "no-coverage");
    }

    public static IEnumerable<string> BuildFallbackTranslationCandidates(
        IReadOnlyList<KodikTranslation> translations,
        string[] preferredTokens,
        string alreadyTriedId,
        int episode)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ep = episode <= 0 ? 1 : episode;

        void Add(string? tid)
        {
            var v = (tid ?? string.Empty).Trim();
            if (v.Length == 0)
            {
                return;
            }

            if (string.Equals(v, alreadyTriedId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.Equals(v, "0", StringComparison.Ordinal))
            {
                return;
            }

            used.Add(v);
        }

        if (preferredTokens.Length > 0)
        {
            foreach (var token in preferredTokens)
            {
                var hit = FindBestMatchByToken(translations, token, ep);
                if (hit != null && CoversEpisode(hit, ep))
                {
                    Add(hit.Id);
                }
            }
        }

        foreach (var t in translations.Where(t =>
                     string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
                     CoversEpisode(t, ep)))
        {
            Add(t.Id);
        }

        foreach (var t in translations.Where(t => CoversEpisode(t, ep)))
        {
            Add(t.Id);
        }

        return used;
    }

    private static bool CoversEpisode(KodikTranslation t, int episode)
    {
        var ep = episode <= 0 ? 1 : episode;

        var max = t.MaxEpisode;
        if (max <= 0)
        {
            return true;
        }

        return ep <= max;
    }

    private static KodikTranslation? FindBestMatchByToken(
        IReadOnlyList<KodikTranslation> translations,
        string token,
        int episode)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var needle = token.Trim();

        bool NameMatches(KodikTranslation t) =>
            !string.IsNullOrWhiteSpace(t.Name) &&
            t.Name.Contains(needle, StringComparison.OrdinalIgnoreCase);

        bool Eligible(KodikTranslation t) =>
            !string.IsNullOrWhiteSpace(t.Id) &&
            CoversEpisode(t, episode);

        var voiceHit = translations.FirstOrDefault(t =>
            string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
            Eligible(t) &&
            NameMatches(t));

        if (voiceHit != null)
        {
            return voiceHit;
        }

        return translations.FirstOrDefault(t =>
            Eligible(t) &&
            NameMatches(t));
    }
}
