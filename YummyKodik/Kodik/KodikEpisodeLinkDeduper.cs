using System;
using System.Collections.Generic;
using System.Linq;

namespace YummyKodik.Kodik
{
    public static class KodikEpisodeLinkDeduper
    {
        public static HashSet<int> KeepLatestEpisodePerResolvedLink(
            IEnumerable<int> candidateEpisodes,
            IReadOnlyDictionary<int, string> resolvedBasePaths)
        {
            var result = new HashSet<int>(candidateEpisodes);
            if (result.Count < 2 || resolvedBasePaths.Count < 2)
            {
                return result;
            }

            foreach (var duplicateGroup in resolvedBasePaths
                         .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                         .Where(group => group.Count() > 1))
            {
                var keepEpisode = duplicateGroup.Max(pair => pair.Key);
                foreach (var duplicateEpisode in duplicateGroup.Select(pair => pair.Key))
                {
                    if (duplicateEpisode != keepEpisode)
                    {
                        result.Remove(duplicateEpisode);
                    }
                }
            }

            return result;
        }
    }
}
