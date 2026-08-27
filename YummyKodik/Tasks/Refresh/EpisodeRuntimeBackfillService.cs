using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YummyKodik.Util;

namespace YummyKodik.Tasks.Refresh;

internal static class EpisodeRuntimeBackfillService
{
    private const int MinimumPlausibleDurationSeconds = 60;

    private static readonly Regex EpisodeNfoPattern = new(
        @"^S(?<season>\d{2})E(?<episode>\d{2})(?: - .+)?\.nfo$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    public static async Task<int> BackfillMissingAsync(
        string outputRoot,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);

        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return 0;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var groups = Directory
            .EnumerateFiles(outputRoot, "*.nfo", options)
            .Select(path => (Path: path, Match: EpisodeNfoPattern.Match(Path.GetFileName(path))))
            .Where(candidate => candidate.Match.Success)
            .GroupBy(
                candidate => BuildEpisodeKey(candidate.Path, candidate.Match),
                StringComparer.OrdinalIgnoreCase);

        var updatedCount = 0;
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshots = new List<EpisodeNfoSnapshot>();
            foreach (var candidate in group)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var xml = await File.ReadAllTextAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
                    if (!RefreshFileWriter.IsValidXmlContent(xml))
                    {
                        continue;
                    }

                    var hasRuntime = NfoBuilder.TryGetEpisodeRuntimeSeconds(xml, out var durationSeconds) &&
                                     durationSeconds > MinimumPlausibleDurationSeconds;
                    var hasExactRuntime = NfoBuilder.TryGetExactEpisodeRuntimeSeconds(xml, out var exactDurationSeconds) &&
                                          exactDurationSeconds > MinimumPlausibleDurationSeconds;
                    snapshots.Add(new EpisodeNfoSnapshot(
                        candidate.Path,
                        xml,
                        hasRuntime ? durationSeconds : null,
                        hasExactRuntime ? exactDurationSeconds : null));
                }
                catch (Exception ex) when (
                    !cancellationToken.IsCancellationRequested &&
                    ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(
                        ex,
                        "[YummyKodik] Failed to inspect episode NFO while backfilling sibling runtimes. path='{Path}'",
                        candidate.Path);
                }
            }

            var exactDurations = snapshots
                .Where(snapshot => snapshot.ExactDurationSeconds.HasValue)
                .Select(snapshot => snapshot.ExactDurationSeconds!.Value)
                .OrderBy(value => value)
                .ToArray();
            var knownDurations = exactDurations.Length > 0
                ? exactDurations
                : snapshots
                    .Where(snapshot => snapshot.DurationSeconds.HasValue)
                    .Select(snapshot => snapshot.DurationSeconds!.Value)
                    .OrderBy(value => value)
                    .ToArray();
            if (knownDurations.Length == 0)
            {
                continue;
            }

            var fallbackDurationSeconds = knownDurations[knownDurations.Length / 2];
            foreach (var snapshot in snapshots.Where(snapshot => !snapshot.DurationSeconds.HasValue))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var enrichedXml = NfoBuilder.EnsureEpisodeRuntime(snapshot.Xml, fallbackDurationSeconds);
                if (string.Equals(snapshot.Xml, enrichedXml, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    await RefreshFileWriter.WriteTextAtomicallyAsync(
                            snapshot.Path,
                            enrichedXml,
                            perf: null,
                            artifactKind: "nfo.runtime",
                            cancellationToken)
                        .ConfigureAwait(false);
                    updatedCount++;
                }
                catch (Exception ex) when (
                    !cancellationToken.IsCancellationRequested &&
                    ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogDebug(
                        ex,
                        "[YummyKodik] Failed to backfill episode NFO runtime from a sibling version. path='{Path}'",
                        snapshot.Path);
                }
            }
        }

        if (updatedCount > 0)
        {
            logger.LogInformation(
                "[YummyKodik] Backfilled runtimes for {UpdatedCount} episode NFO file(s) from sibling versions.",
                updatedCount);
        }

        return updatedCount;
    }

    private static string BuildEpisodeKey(string path, Match match)
    {
        return string.Concat(
            Path.GetDirectoryName(path),
            "\0",
            match.Groups["season"].Value,
            "\0",
            match.Groups["episode"].Value);
    }

    private sealed record EpisodeNfoSnapshot(
        string Path,
        string Xml,
        int? DurationSeconds,
        int? ExactDurationSeconds);
}
