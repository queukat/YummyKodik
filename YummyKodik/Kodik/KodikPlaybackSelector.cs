using System;
using System.Collections.Generic;
using System.Linq;
using YummyKodik.Util;

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

        if (TryPickExplicitTranslation(translations, explicitTranslationId, ep, ref reasonPrefix, out var selected))
        {
            return selected;
        }

        if (TryPickSavedTranslation(translations, savedTranslationId, ep, ref reasonPrefix, out selected))
        {
            return selected;
        }

        if (TryPickPreferredTranslation(translations, preferredTokens, ep, reasonPrefix, out selected))
        {
            return selected;
        }

        if (TryPickVoiceFallback(translations, ep, reasonPrefix, out selected))
        {
            return selected;
        }

        if (TryPickFirstCoveringFallback(translations, ep, reasonPrefix, out selected))
        {
            return selected;
        }

        return BuildNoCoverageSelection(translations, reasonPrefix);
    }

    public static IEnumerable<string> BuildFallbackTranslationCandidates(
        IReadOnlyList<KodikTranslation> translations,
        string[] preferredTokens,
        string alreadyTriedId,
        int episode)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ep = episode <= 0 ? 1 : episode;

        if (preferredTokens.Length > 0)
        {
            foreach (var token in preferredTokens)
            {
                var hit = FindBestMatchByToken(translations, token, ep);
                if (hit != null && CoversEpisode(hit, ep))
                {
                    AddCandidate(used, hit.Id, alreadyTriedId);
                }
            }
        }

        foreach (var t in translations.Where(t =>
                     string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
                     CoversEpisode(t, ep)))
        {
            AddCandidate(used, t.Id, alreadyTriedId);
        }

        foreach (var t in translations.Where(t => CoversEpisode(t, ep)))
        {
            AddCandidate(used, t.Id, alreadyTriedId);
        }

        return used;
    }

    private static bool TryPickExplicitTranslation(
        IReadOnlyList<KodikTranslation> translations,
        string? explicitTranslationId,
        int episode,
        ref string reasonPrefix,
        out (string TranslationId, bool WaitIfMissing, string Reason) selection)
    {
        selection = default;

        var translationId = NormalizeTranslationId(explicitTranslationId);
        if (translationId.Length == 0)
        {
            return false;
        }

        var translation = FindById(translations, translationId);
        if (translation == null)
        {
            reasonPrefix = "explicit-missing+";
            return false;
        }

        if (!CoversEpisode(translation, episode))
        {
            reasonPrefix = "explicit-beyond-max+";
            return false;
        }

        selection = (translationId, true, "explicit");
        return true;
    }

    private static bool TryPickSavedTranslation(
        IReadOnlyList<KodikTranslation> translations,
        string? savedTranslationId,
        int episode,
        ref string reasonPrefix,
        out (string TranslationId, bool WaitIfMissing, string Reason) selection)
    {
        selection = default;

        var translationId = NormalizeTranslationId(savedTranslationId);
        if (translationId.Length == 0)
        {
            return false;
        }

        var translation = FindById(translations, translationId);
        if (translation != null)
        {
            if (!CoversEpisode(translation, episode))
            {
                reasonPrefix += "saved-beyond-max+";
                return false;
            }

            selection = (translationId, true, reasonPrefix + "saved");
            return true;
        }

        var savedVoiceNameKey = TranslationNameKeyNormalizer.Normalize(savedTranslationId);
        if (savedVoiceNameKey.Length == 0)
        {
            return false;
        }

        var savedVoiceMatch = translations.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.Id) &&
            CoversEpisode(t, episode) &&
            string.Equals(
                TranslationNameKeyNormalizer.Normalize(t.Name),
                savedVoiceNameKey,
                StringComparison.Ordinal));
        var savedVoiceTranslationId = NormalizeTranslationId(savedVoiceMatch?.Id);
        if (savedVoiceTranslationId.Length == 0)
        {
            return false;
        }

        selection = (savedVoiceTranslationId, true, reasonPrefix + "saved-name");
        return true;
    }

    private static bool TryPickPreferredTranslation(
        IReadOnlyList<KodikTranslation> translations,
        string[] preferredTokens,
        int episode,
        string reasonPrefix,
        out (string TranslationId, bool WaitIfMissing, string Reason) selection)
    {
        selection = default;

        foreach (var token in preferredTokens)
        {
            var hit = FindBestMatchByToken(translations, token, episode);
            var translationId = NormalizeTranslationId(hit?.Id);
            if (translationId.Length == 0)
            {
                continue;
            }

            selection = (translationId, true, reasonPrefix + "preferred-token");
            return true;
        }

        return false;
    }

    private static bool TryPickVoiceFallback(
        IReadOnlyList<KodikTranslation> translations,
        int episode,
        string reasonPrefix,
        out (string TranslationId, bool WaitIfMissing, string Reason) selection)
    {
        var voice = translations.FirstOrDefault(t =>
            string.Equals(t.Type, "voice", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(t.Id) &&
            CoversEpisode(t, episode));

        return TryBuildSelection(voice, false, reasonPrefix + "fallback-voice", out selection);
    }

    private static bool TryPickFirstCoveringFallback(
        IReadOnlyList<KodikTranslation> translations,
        int episode,
        string reasonPrefix,
        out (string TranslationId, bool WaitIfMissing, string Reason) selection)
    {
        var firstCovering = translations.FirstOrDefault(t =>
            !string.IsNullOrWhiteSpace(t.Id) &&
            CoversEpisode(t, episode));

        return TryBuildSelection(firstCovering, false, reasonPrefix + "fallback-first", out selection);
    }

    private static (string TranslationId, bool WaitIfMissing, string Reason) BuildNoCoverageSelection(
        IReadOnlyList<KodikTranslation> translations,
        string reasonPrefix)
    {
        var anyId = NormalizeTranslationId(translations.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Id))?.Id);
        if (anyId.Length == 0)
        {
            anyId = "0";
        }

        return (anyId, true, reasonPrefix + "no-coverage");
    }

    private static bool TryBuildSelection(
        KodikTranslation? translation,
        bool waitIfMissing,
        string reason,
        out (string TranslationId, bool WaitIfMissing, string Reason) selection)
    {
        selection = default;

        var translationId = NormalizeTranslationId(translation?.Id);
        if (translationId.Length == 0)
        {
            return false;
        }

        selection = (translationId, waitIfMissing, reason);
        return true;
    }

    private static KodikTranslation? FindById(IReadOnlyList<KodikTranslation> translations, string? translationId)
    {
        var needle = NormalizeTranslationId(translationId);
        if (needle.Length == 0)
        {
            return null;
        }

        return translations.FirstOrDefault(t =>
            string.Equals(NormalizeTranslationId(t.Id), needle, StringComparison.Ordinal));
    }

    private static void AddCandidate(HashSet<string> used, string? translationId, string alreadyTriedId)
    {
        var value = NormalizeTranslationId(translationId);
        if (value.Length == 0)
        {
            return;
        }

        if (string.Equals(value, alreadyTriedId, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(value, "0", StringComparison.Ordinal))
        {
            return;
        }

        used.Add(value);
    }

    private static string NormalizeTranslationId(string? translationId)
    {
        return (translationId ?? string.Empty).Trim();
    }

    private static bool CoversEpisode(KodikTranslation t, int episode)
    {
        return t.CoversEpisode(episode);
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
