using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;

internal static class RefreshAuditTests
{
    public static void TruncatedXmlRebuilt()
    {
        const string broken = "<episodedetails><title>truncated";
        if (RefreshFileWriter.IsValidXmlContent(broken)) throw new Exception("Truncated XML must be rejected.");
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "S01E01.nfo");
            File.WriteAllText(path, broken);
            new EpisodeArtifactWriter().EnsureEpisodeNfoAsync(NullLogger.Instance, path, 1, 1, "Series", "", null, null, CancellationToken.None).GetAwaiter().GetResult();
            if (!RefreshFileWriter.IsValidXmlContent(File.ReadAllText(path))) throw new Exception("Broken NFO must be repaired.");
        }
        finally { Directory.Delete(root, true); }
    }

    public static void LongEpisodeNumbers()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var episode in new[] { 100, 1000 })
            {
                var prefix = RefreshPathUtilities.BuildEpisodeBaseName(1, episode);
                File.WriteAllText(Path.Combine(root, prefix + " - Known.strm"), "known");
                File.WriteAllText(Path.Combine(root, prefix + " - Known.nfo"), NfoBuilder.BuildEpisodeNfo(episode, 1, "Series", "", 1200));
                File.WriteAllText(Path.Combine(root, prefix + " - Missing.nfo"), NfoBuilder.BuildEpisodeNfo(episode, 1, "Series", "", null));
            }
            var aliases = EpisodeArtifactWriter.BuildExistingEpisodeTranslationFileBaseNames(root, 1);
            var state = ExistingLibraryFallbackRefreshInfoLoader.BuildExistingEpisodeState(root, 1);
            if (!aliases.ContainsKey(100) || !aliases.ContainsKey(1000) || !state.GeneratedEpisodeNumbers.SetEquals([100, 1000])) throw new Exception("Long episodes must be discoverable.");
            var updated = EpisodeRuntimeBackfillService.BackfillMissingAsync(root, NullLogger.Instance, CancellationToken.None).GetAwaiter().GetResult();
            if (updated != 2) throw new Exception("Long episode siblings must receive runtime backfill.");
            EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(NullLogger.Instance, root, 1, new Dictionary<int, HashSet<string>>(), 1000);
            if (Directory.GetFiles(root).Length != 0) throw new Exception("Stale long episode artifacts must be cleaned.");
            File.WriteAllText(Path.Combine(root, "S00E01.strm"), "special");
            EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(NullLogger.Instance, root, 0, new Dictionary<int, HashSet<string>>(), 1);
            if (Directory.GetFiles(root).Length != 0) throw new Exception("Special-season stale files must be cleaned.");
        }
        finally { Directory.Delete(root, true); }
    }
}
