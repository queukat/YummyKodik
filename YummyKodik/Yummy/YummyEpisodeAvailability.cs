using System;
using System.Collections.Generic;
using System.Linq;

namespace YummyKodik.Yummy
{
    public static class YummyEpisodeAvailability
    {
        public static bool NeedsKodikSupplement(
            YummyAnimeResponse anime,
            IReadOnlyCollection<int> generatedEpisodeNumbers,
            IEnumerable<int>? explicitEpisodeNumbers = null)
        {
            if (anime == null)
            {
                throw new ArgumentNullException(nameof(anime));
            }

            var expectedAvailableEpisodes = GetExpectedAvailableEpisodeCount(anime, explicitEpisodeNumbers);
            if (expectedAvailableEpisodes <= 0)
            {
                return false;
            }

            if (generatedEpisodeNumbers == null || generatedEpisodeNumbers.Count == 0)
            {
                return true;
            }

            for (var ep = 1; ep <= expectedAvailableEpisodes; ep++)
            {
                if (!generatedEpisodeNumbers.Contains(ep))
                {
                    return true;
                }
            }

            return false;
        }

        public static int GetExpectedAvailableEpisodeCount(YummyAnimeResponse anime, IEnumerable<int>? explicitEpisodeNumbers = null)
        {
            if (anime == null)
            {
                throw new ArgumentNullException(nameof(anime));
            }

            var explicitEpisodeMax = NormalizeEpisodeNumbers(explicitEpisodeNumbers)
                .DefaultIfEmpty(0)
                .Max();

            var animeVideoEpisodeMax = NormalizeEpisodeNumbers(anime.Videos?.Select(video => ParseEpisodeNumber(video?.Number)))
                .DefaultIfEmpty(0)
                .Max();

            var knownAvailableEpisodeMax = Math.Max(explicitEpisodeMax, animeVideoEpisodeMax);
            var aired = anime.Episodes?.Aired ?? 0;
            if (aired > 0)
            {
                return Math.Max(aired, knownAvailableEpisodeMax);
            }

            if (knownAvailableEpisodeMax > 0)
            {
                return knownAvailableEpisodeMax;
            }

            // Planned episode totals are common for announcements. If nothing has aired yet and
            // Yummy does not expose any playable providers, treat the title as "not available yet"
            // so we still create folders/posters but skip noisy Kodik probing.
            if ((anime.Videos?.Count ?? 0) == 0)
            {
                return 0;
            }

            return Math.Max(0, anime.Episodes?.Count ?? 0);
        }

        public static int[] LimitToExpectedAvailableEpisodes(YummyAnimeResponse anime, IEnumerable<int>? episodeNumbers)
        {
            if (anime == null)
            {
                throw new ArgumentNullException(nameof(anime));
            }

            var normalizedEpisodes = NormalizeEpisodeNumbers(episodeNumbers)
                .ToArray();

            var expectedAvailableEpisodes = GetExpectedAvailableEpisodeCount(anime, normalizedEpisodes);
            if (expectedAvailableEpisodes <= 0)
            {
                return normalizedEpisodes;
            }

            return normalizedEpisodes
                .Where(ep => ep <= expectedAvailableEpisodes)
                .ToArray();
        }

        public static int ResolveKodikAvailableEpisodeCount(int kodikSeriesCount, int expectedAvailableEpisodes)
        {
            if (kodikSeriesCount > 0)
            {
                return expectedAvailableEpisodes > 0
                    ? Math.Min(kodikSeriesCount, expectedAvailableEpisodes)
                    : kodikSeriesCount;
            }

            return Math.Max(0, expectedAvailableEpisodes);
        }

        private static IEnumerable<int> NormalizeEpisodeNumbers(IEnumerable<int?>? episodeNumbers)
        {
            return (episodeNumbers ?? Array.Empty<int?>())
                .Where(ep => ep.HasValue && ep.Value > 0)
                .Select(ep => ep!.Value)
                .Distinct()
                .OrderBy(ep => ep);
        }

        private static IEnumerable<int> NormalizeEpisodeNumbers(IEnumerable<int>? episodeNumbers)
        {
            return (episodeNumbers ?? Array.Empty<int>())
                .Where(ep => ep > 0)
                .Distinct()
                .OrderBy(ep => ep);
        }

        private static int? ParseEpisodeNumber(string? rawNumber)
        {
            return int.TryParse(rawNumber?.Trim(), out var episodeNumber) && episodeNumber > 0
                ? episodeNumber
                : null;
        }
    }
}
