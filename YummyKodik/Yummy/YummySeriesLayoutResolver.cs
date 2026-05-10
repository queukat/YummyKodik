using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using YummyKodik.Shikimori;

namespace YummyKodik.Yummy
{
    /// <summary>
    /// Resolves a stable series title and season number from Yummy API metadata.
    /// </summary>
    public static class YummySeriesLayoutResolver
    {
        private static readonly Regex SeasonWithPartSuffixRegex = new(
            @"^(?<title>.+?)\s(?<season>\d+)\s*[\.\-|:]*\s*(?:part|часть)\s*(?<part>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex TrailingSeasonSuffixRegex = new(
            @"^(?<title>.+?)\s(?<season>\d+)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        public static int ResolveSeasonNumber(
            YummyAnimeResponse? anime,
            string? fallbackTitle,
            ShikimoriSeriesLayoutInfo? shikimoriLayout = null)
        {
            if (IsSpecialTypeAlias(anime?.Type?.Alias))
            {
                return 0;
            }

            var title = (fallbackTitle ?? anime?.Title ?? string.Empty).Trim();
            if (TryResolveSeasonNumberFromTitle(title, out var titleSeason))
            {
                return titleSeason;
            }

            if (shikimoriLayout?.SeasonNumber > 0)
            {
                return shikimoriLayout.SeasonNumber;
            }

            if (TryResolveSeasonNumberFromViewingOrder(anime, out var viewingOrderSeason))
            {
                return viewingOrderSeason;
            }

            return 1;
        }

        public static string ResolveSeriesTitle(
            YummyAnimeResponse? anime,
            string? fallbackTitle,
            int seasonNumber,
            ShikimoriSeriesLayoutInfo? shikimoriLayout = null)
        {
            var rawTitle = (fallbackTitle ?? anime?.Title ?? string.Empty).Trim();
            if (rawTitle.Length == 0)
            {
                return string.Empty;
            }

            if (seasonNumber > 1 &&
                TryStripTrailingSeasonSuffix(rawTitle, seasonNumber, out var strippedTitle))
            {
                return strippedTitle;
            }

            if (!string.IsNullOrWhiteSpace(shikimoriLayout?.BaseTitle))
            {
                return shikimoriLayout.BaseTitle;
            }

            var viewingOrderTitle = TryResolveBaseTitleFromViewingOrder(anime, seasonNumber);
            if (!string.IsNullOrWhiteSpace(viewingOrderTitle))
            {
                return viewingOrderTitle;
            }

            return rawTitle;
        }

        public static bool ShouldCreateSeasonDirectory(YummyAnimeResponse? anime, int seasonNumber)
        {
            if (seasonNumber > 1)
            {
                return true;
            }

            if (anime?.Episodes?.Count > 0)
            {
                return true;
            }

            var typeAlias = (anime?.Type?.Alias ?? string.Empty).Trim();
            if (typeAlias.Length == 0)
            {
                return false;
            }

            return !typeAlias.Equals("movie", StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryResolveBaseTitleFromViewingOrder(YummyAnimeResponse? anime, int seasonNumber)
        {
            var items = anime?.ViewingOrder;
            if (seasonNumber == 1 || items == null || items.Count == 0)
            {
                return null;
            }

            var current = items.FirstOrDefault(x =>
                (anime?.AnimeId > 0 && x.AnimeId == anime.AnimeId) ||
                (!string.IsNullOrWhiteSpace(anime?.AnimeUrl) &&
                 string.Equals(x.AnimeUrl, anime.AnimeUrl, StringComparison.OrdinalIgnoreCase)));

            if (current?.Data?.Index is int currentIndex && currentIndex <= 0)
            {
                return null;
            }

            var rootTitle = items
                .Where(x => !string.IsNullOrWhiteSpace(x.Title))
                .OrderBy(x => x.Data?.Index ?? int.MaxValue)
                .Select(x => x.Title.Trim())
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(rootTitle) ? null : rootTitle;
        }

        private static bool TryResolveSeasonNumberFromViewingOrder(YummyAnimeResponse? anime, out int seasonNumber)
        {
            seasonNumber = 1;

            if (!IsMainlineTypeAlias(anime?.Type?.Alias))
            {
                return false;
            }

            var items = anime?.ViewingOrder;
            if (items == null || items.Count == 0)
            {
                return false;
            }

            var current = items.FirstOrDefault(x =>
                (anime?.AnimeId > 0 && x.AnimeId == anime.AnimeId) ||
                (!string.IsNullOrWhiteSpace(anime?.AnimeUrl) &&
                 string.Equals(x.AnimeUrl, anime.AnimeUrl, StringComparison.OrdinalIgnoreCase)));

            var index = current?.Data?.Index;
            if (!index.HasValue || index.Value < 0)
            {
                return false;
            }

            seasonNumber = index.Value + 1;
            return seasonNumber > 0;
        }

        private static bool IsMainlineTypeAlias(string? alias)
        {
            var normalizedAlias = (alias ?? string.Empty).Trim();
            return normalizedAlias.Equals("tv", StringComparison.OrdinalIgnoreCase) ||
                   normalizedAlias.Equals("ona", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSpecialTypeAlias(string? alias)
        {
            return string.Equals((alias ?? string.Empty).Trim(), "special", StringComparison.OrdinalIgnoreCase);
        }

        public static bool HasExplicitSeasonNumber(string? title)
        {
            return TryResolveSeasonNumberFromTitle(title, out _);
        }

        private static bool TryResolveSeasonNumberFromTitle(string? title, out int seasonNumber)
        {
            seasonNumber = 1;

            var normalizedTitle = (title ?? string.Empty).Trim();
            if (normalizedTitle.Length == 0)
            {
                return false;
            }

            var match = SeasonWithPartSuffixRegex.Match(normalizedTitle);
            if (!match.Success)
            {
                match = TrailingSeasonSuffixRegex.Match(normalizedTitle);
            }

            if (!match.Success ||
                !int.TryParse(match.Groups["season"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seasonNumber) ||
                seasonNumber <= 0)
            {
                seasonNumber = 1;
                return false;
            }

            return true;
        }

        private static bool TryStripTrailingSeasonSuffix(string rawTitle, int seasonNumber, out string title)
        {
            title = string.Empty;

            var normalizedTitle = (rawTitle ?? string.Empty).Trim();
            if (normalizedTitle.Length == 0 || seasonNumber <= 1)
            {
                return false;
            }

            var seasonWithPartMatch = SeasonWithPartSuffixRegex.Match(normalizedTitle);
            if (seasonWithPartMatch.Success &&
                int.TryParse(seasonWithPartMatch.Groups["season"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeasonWithPart) &&
                parsedSeasonWithPart == seasonNumber)
            {
                var explicitBaseTitle = seasonWithPartMatch.Groups["title"].Value.TrimEnd();
                if (explicitBaseTitle.Length > 0)
                {
                    title = explicitBaseTitle;
                    return true;
                }
            }

            var seasonMatch = TrailingSeasonSuffixRegex.Match(normalizedTitle);
            if (!seasonMatch.Success ||
                !int.TryParse(seasonMatch.Groups["season"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeason) ||
                parsedSeason != seasonNumber)
            {
                return false;
            }

            var trimmed = seasonMatch.Groups["title"].Value.TrimEnd();
            if (trimmed.Length == 0)
            {
                return false;
            }

            title = trimmed;
            return true;
        }
    }
}
