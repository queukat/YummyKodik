using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace YummyKodik.Util;

public static class TranslationNameKeyNormalizer
{
    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{Nd}]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TrailingAliasRegex = new(
        @"^(?<primary>.+?)\s*\((?<alias>[^()]+)\)\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // Human-reviewed cross-provider voice variants. Keep this list explicit and curated.
    private static readonly (string CanonicalName, string[] Aliases)[] CuratedAliasGroups =
    [
        ("2x2", ["2х2"]),
        ("AniLibria", ["Anilib", "AniLiberty", "AniLiberty (AniLibria)", "AnilibriaTV"]),
        ("StudioBand", ["Студийная Банда", "Studio Band"]),
        ("AniLeague", ["AniLeague.TV"])
    ];

    private static readonly IReadOnlyDictionary<string, string> CanonicalAliases = BuildCanonicalAliases();

    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        if (normalized.StartsWith("Озвучка ", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized.Substring("Озвучка ".Length).Trim();
        }

        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var trailingAliasKey = TryNormalizeTrailingAlias(normalized);
        if (trailingAliasKey.Length > 0)
        {
            return CanonicalizeKey(trailingAliasKey);
        }

        return CanonicalizeKey(NormalizeTokens(normalized));
    }

    private static IReadOnlyDictionary<string, string> BuildCanonicalAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in CuratedAliasGroups)
        {
            var canonicalKey = NormalizeTokens(group.CanonicalName);
            if (canonicalKey.Length == 0)
            {
                continue;
            }

            foreach (var alias in group.Aliases.Prepend(group.CanonicalName))
            {
                var aliasKey = NormalizeTokens(alias);
                if (aliasKey.Length == 0 || string.Equals(aliasKey, canonicalKey, StringComparison.Ordinal))
                {
                    continue;
                }

                aliases[aliasKey] = canonicalKey;
            }
        }

        return aliases;
    }

    private static string TryNormalizeTrailingAlias(string value)
    {
        var match = TrailingAliasRegex.Match(value);
        if (!match.Success)
        {
            return string.Empty;
        }

        var primary = match.Groups["primary"].Value.Trim();
        var alias = match.Groups["alias"].Value.Trim();
        if (!ContainsLetter(primary) || !ContainsLetter(alias))
        {
            return string.Empty;
        }

        var primaryKey = NormalizeTokens(primary);
        var aliasKey = NormalizeTokens(alias);
        if (primaryKey.Length == 0 || aliasKey.Length == 0)
        {
            return string.Empty;
        }

        return aliasKey.Length <= primaryKey.Length ? aliasKey : primaryKey;
    }

    private static string NormalizeTokens(string value)
    {
        var tokens = TokenRegex.Matches(value)
            .Select(x => x.Value.Trim().ToLowerInvariant())
            .Where(x => x.Length > 0)
            .ToList();

        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (string.Equals(tokens[0], "озвучка", StringComparison.Ordinal))
        {
            tokens.RemoveAt(0);
        }

        while (tokens.Count > 0 && IsTransportSuffix(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
        }

        return tokens.Count == 0
            ? string.Empty
            : string.Concat(tokens);
    }

    private static string CanonicalizeKey(string key)
    {
        return CanonicalAliases.TryGetValue(key, out var canonicalKey)
            ? canonicalKey
            : key;
    }

    private static bool ContainsLetter(string value)
    {
        return value.Any(char.IsLetter);
    }

    private static bool IsTransportSuffix(string token)
    {
        return string.Equals(token, "tv", StringComparison.Ordinal);
    }
}
