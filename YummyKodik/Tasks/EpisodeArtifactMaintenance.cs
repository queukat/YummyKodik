using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YummyKodik.Util;

namespace YummyKodik.Tasks;

public static class EpisodeArtifactMaintenance
{
    public static void TrackExpectedEpisodeArtifact(
        IDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        int episodeNumber,
        string fileBaseName)
    {
        if (episodeNumber <= 0 || string.IsNullOrWhiteSpace(fileBaseName))
        {
            return;
        }

        if (!expectedEpisodeFileBaseNames.TryGetValue(episodeNumber, out var fileBaseNames))
        {
            fileBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            expectedEpisodeFileBaseNames[episodeNumber] = fileBaseNames;
        }

        fileBaseNames.Add(fileBaseName);
    }

    public static void TrackExpectedEpisodeTranslation(
        IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
        int episodeNumber,
        string translationSuffix)
    {
        if (episodeNumber <= 0)
        {
            return;
        }

        var normalizedKey = NormalizeEpisodeTranslationKey(translationSuffix);
        if (normalizedKey.Length == 0)
        {
            return;
        }

        if (!expectedEpisodeTranslationKeys.TryGetValue(episodeNumber, out var translationKeys))
        {
            translationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            expectedEpisodeTranslationKeys[episodeNumber] = translationKeys;
        }

        translationKeys.Add(normalizedKey);
    }

    public static bool HasExpectedEpisodeArtifacts(
        IDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        int episodeNumber)
    {
        return expectedEpisodeFileBaseNames.TryGetValue(episodeNumber, out var fileBaseNames) &&
               fileBaseNames.Count > 0;
    }

    public static bool HasExpectedEpisodeTranslation(
        IDictionary<int, HashSet<string>> expectedEpisodeTranslationKeys,
        int episodeNumber,
        string translationSuffix)
    {
        if (!expectedEpisodeTranslationKeys.TryGetValue(episodeNumber, out var translationKeys) ||
            translationKeys.Count == 0)
        {
            return false;
        }

        return translationKeys.Contains(NormalizeEpisodeTranslationKey(translationSuffix));
    }

    public static string NormalizeEpisodeTranslationKey(string? translationSuffix)
    {
        return TranslationNameKeyNormalizer.Normalize(SanitizeFileName(translationSuffix ?? string.Empty));
    }

    public static string ResolveEpisodeTranslationFileBaseName(
        IDictionary<int, Dictionary<string, string>> existingEpisodeTranslationFileBaseNames,
        int episodeNumber,
        string baseName,
        string translationSuffix)
    {
        var defaultFileBaseName = baseName + " - " + translationSuffix;
        var normalizedKey = NormalizeEpisodeTranslationKey(translationSuffix);
        if (normalizedKey.Length == 0)
        {
            return defaultFileBaseName;
        }

        if (existingEpisodeTranslationFileBaseNames.TryGetValue(episodeNumber, out var aliases) &&
            aliases.TryGetValue(normalizedKey, out var existingFileBaseName) &&
            !string.IsNullOrWhiteSpace(existingFileBaseName))
        {
            return existingFileBaseName;
        }

        if (!existingEpisodeTranslationFileBaseNames.TryGetValue(episodeNumber, out aliases))
        {
            aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            existingEpisodeTranslationFileBaseNames[episodeNumber] = aliases;
        }

        aliases[normalizedKey] = defaultFileBaseName;
        return defaultFileBaseName;
    }

    public static void CleanupUnexpectedEpisodeArtifacts(
        ILogger logger,
        string seasonDir,
        int seasonNumber,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        int maxAvailableEpisodeNumber,
        Action<string>? deleteFile = null)
    {
        if (string.IsNullOrWhiteSpace(seasonDir) ||
            !Directory.Exists(seasonDir) ||
            maxAvailableEpisodeNumber <= 0)
        {
            return;
        }

        var pattern = new Regex(
            @"^S(?<season>\d{2})E(?<episode>\d{2})(?: - .+)?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        foreach (var path in Directory.EnumerateFiles(seasonDir))
        {
            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileBaseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fileBaseName))
            {
                continue;
            }

            var match = pattern.Match(fileBaseName);
            if (!match.Success)
            {
                continue;
            }

            if (!int.TryParse(match.Groups["season"].Value, out var parsedSeason) ||
                parsedSeason != Math.Max(1, seasonNumber) ||
                !int.TryParse(match.Groups["episode"].Value, out var episodeNumber))
            {
                continue;
            }

            if (episodeNumber > maxAvailableEpisodeNumber)
            {
                (deleteFile ?? (p => TryDeleteFile(logger, p)))(path);
                continue;
            }

            if (expectedEpisodeFileBaseNames.TryGetValue(episodeNumber, out var expectedFileBaseNames) &&
                expectedFileBaseNames.Contains(fileBaseName))
            {
                continue;
            }

            (deleteFile ?? (p => TryDeleteFile(logger, p)))(path);
        }
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

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }
}
