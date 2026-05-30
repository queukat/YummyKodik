using Microsoft.Extensions.Logging;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal static class RefreshPathUtilities
{
    public static IEnumerable<string> GetLegacySeriesRoots(
        string root,
        string rawTitle,
        string resolvedTitle,
        YummyAnimeResponse anime)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            yield return Path.Combine(root, SafeFilename(rawTitle));
            yield return Path.Combine(root, SafeFilename(BuildLegacySeriesFolderName(rawTitle, anime)));
        }

        if (!string.IsNullOrWhiteSpace(resolvedTitle))
        {
            yield return Path.Combine(root, SafeFilename(resolvedTitle));
            yield return Path.Combine(root, SafeFilename(BuildLegacySeriesFolderName(resolvedTitle, anime)));
        }

        if (!string.IsNullOrWhiteSpace(rawTitle))
        {
            yield return Path.Combine(root, SafeFilename(BuildSeriesFolderName(rawTitle, anime)));
        }
    }

    public static string ResolveSeriesRoot(ILogger logger, string seriesRoot, IEnumerable<string> legacyRoots)
    {
        if (string.IsNullOrWhiteSpace(seriesRoot))
        {
            return seriesRoot;
        }

        if (Directory.Exists(seriesRoot))
        {
            return seriesRoot;
        }

        foreach (var legacyRoot in legacyRoots
                     .Where(x => !string.IsNullOrWhiteSpace(x))
                     .Where(x => !string.Equals(x, seriesRoot, StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(legacyRoot))
            {
                continue;
            }

            logger.LogInformation(
                "[YummyKodik] Using existing legacy series folder '{Legacy}' instead of renaming to '{Canonical}' to keep Jellyfin item ids stable.",
                legacyRoot,
                seriesRoot);

            return legacyRoot;
        }

        return seriesRoot;
    }

    public static string BuildEpisodeBaseName(int seasonNumber, int episodeNumber)
    {
        var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
        return $"S{effectiveSeasonNumber:00}E{episodeNumber:00}";
    }

    public static string BuildProviderStreamUrl(
        string baseUrl,
        YummyVideoProviderKind provider,
        long animeId,
        int episode,
        string? voiceName = null,
        YummyVideoEntry? entry = null)
    {
        return provider switch
        {
            YummyVideoProviderKind.Alloha => YummyKodikStreamUri.BuildAllohaHttpUrl(baseUrl, animeId, episode, voiceName, entry?.Alloha),
            _ => YummyKodikStreamUri.BuildCvhHttpUrl(baseUrl, animeId, episode, voiceName)
        };
    }

    public static string BuildSeriesFolderName(string title, YummyAnimeResponse? anime)
    {
        var baseTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(baseTitle))
        {
            baseTitle = NormalizeKey(anime?.AnimeUrl);
        }

        var tag = YummyProviderTagFormatter.BuildBestIdTag(anime);
        return string.IsNullOrEmpty(tag) ? baseTitle : $"{baseTitle} {tag}";
    }

    public static string BuildLegacySeriesFolderName(string title, YummyAnimeResponse? anime)
    {
        var baseTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(baseTitle))
        {
            baseTitle = NormalizeKey(anime?.AnimeUrl);
        }

        var tag = YummyProviderTagFormatter.BuildLegacyBestIdTag(anime);
        return string.IsNullOrEmpty(tag) ? baseTitle : $"{baseTitle} {tag}";
    }

    public static string SafeFilename(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim();
    }

    public static string NormalizeKey(string? s)
    {
        var v = (s ?? string.Empty).Trim();
        v = v.Trim().Trim('"', '\'', '“', '”');
        v = v.Trim('/');
        return v;
    }
}
