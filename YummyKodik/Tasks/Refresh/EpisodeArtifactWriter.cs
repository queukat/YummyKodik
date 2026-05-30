using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using YummyKodik.Util;

namespace YummyKodik.Tasks.Refresh;

internal sealed class EpisodeArtifactWriter
{
    public async Task WriteEpisodeArtifactsAsync(
        EpisodeArtifactWriteContext context,
        string fileBaseName,
        string url,
        int episodeNumber,
        CancellationToken cancellationToken)
    {
        await WriteEpisodeArtifactsAsync(
                context.Logger,
                context.SeasonDir,
                fileBaseName,
                url,
                episodeNumber,
                context.SeasonNumber,
                context.Title,
                context.Description,
                context.Perf,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task WriteEpisodeArtifactsAsync(
        ILogger logger,
        string seasonDir,
        string fileBaseName,
        string url,
        int episodeNumber,
        int seasonNumber,
        string title,
        string? description,
        RefreshPerformanceMetrics? perf,
        CancellationToken cancellationToken)
    {
        var strmPath = Path.Combine(seasonDir, fileBaseName + RefreshConstants.StrmExtension);
        var nfoPath = Path.Combine(seasonDir, fileBaseName + RefreshConstants.NfoExtension);

        await RefreshFileWriter.WriteTextAtomicallyAsync(strmPath, url + Environment.NewLine, perf, "strm", cancellationToken).ConfigureAwait(false);
        await EnsureEpisodeNfoAsync(logger, nfoPath, episodeNumber, seasonNumber, title, description, perf, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task EnsureEpisodeNfoAsync(
        ILogger logger,
        string nfoPath,
        int episodeNumber,
        int seasonNumber,
        string seriesTitle,
        string? description,
        RefreshPerformanceMetrics? perf,
        CancellationToken cancellationToken)
    {
        var xml = NfoBuilder.BuildEpisodeNfo(
            episodeNumber,
            season: seasonNumber,
            seriesTitle: seriesTitle,
            description: description ?? string.Empty);

        if (File.Exists(nfoPath))
        {
            if (await RefreshFileWriter.IsValidXmlFileAsync(nfoPath, cancellationToken).ConfigureAwait(false))
            {
                perf?.AddCount("io.nfo_unchanged");
                return;
            }

            perf?.AddCount("io.nfo_invalid_rebuilt");
            logger.LogInformation(
                "[YummyKodik] Existing episode nfo is empty or invalid XML, recreating it atomically. path='{Path}'",
                nfoPath);
        }

        await RefreshFileWriter.WriteTextAtomicallyAsync(nfoPath, xml, perf, "nfo", cancellationToken).ConfigureAwait(false);
    }

    public void CleanupUnexpectedEpisodeArtifacts(
        ILogger logger,
        string seasonDir,
        int seasonNumber,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        int maxAvailableEpisodeNumber,
        RefreshPerformanceMetrics? perf)
    {
        EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
            logger,
            seasonDir,
            seasonNumber,
            expectedEpisodeFileBaseNames,
            maxAvailableEpisodeNumber,
            path => TryDeleteFile(logger, path, perf));
    }

    public void TryDeleteFile(ILogger logger, string path, RefreshPerformanceMetrics? perf = null)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
                perf?.AddCount("io.file_deleted");
                logger.LogDebug("[YummyKodik] Deleted stale placeholder file '{Path}'.", path);
            }
        }
        catch (Exception ex)
        {
            perf?.AddCount("io.delete_failures");
            logger.LogDebug(ex, "[YummyKodik] Failed to delete file '{Path}'.", path);
        }
    }

    public static Dictionary<int, Dictionary<string, string>> BuildExistingEpisodeTranslationFileBaseNames(
        string seasonDir,
        int seasonNumber)
    {
        var result = new Dictionary<int, Dictionary<string, string>>();
        if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
        {
            return result;
        }

        var effectiveSeasonNumber = seasonNumber >= 0 ? seasonNumber : 1;
        var filePattern = new Regex(
            @"^S(?<season>\d{2})E(?<episode>\d{2})(?: - (?<suffix>.+))?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(1));

        foreach (var path in Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly))
        {
            var fileBaseName = Path.GetFileNameWithoutExtension(path);
            if (string.IsNullOrWhiteSpace(fileBaseName))
            {
                continue;
            }

            var match = filePattern.Match(fileBaseName);
            if (!match.Success ||
                !int.TryParse(match.Groups["season"].Value, out var parsedSeason) ||
                parsedSeason != effectiveSeasonNumber ||
                !int.TryParse(match.Groups["episode"].Value, out var episodeNumber))
            {
                continue;
            }

            var suffix = match.Groups["suffix"].Success
                ? (match.Groups["suffix"].Value ?? string.Empty).Trim()
                : string.Empty;
            var normalizedKey = EpisodeArtifactMaintenance.NormalizeEpisodeTranslationKey(suffix);
            if (normalizedKey.Length == 0)
            {
                continue;
            }

            if (!result.TryGetValue(episodeNumber, out var aliases))
            {
                aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[episodeNumber] = aliases;
            }

            aliases.TryAdd(normalizedKey, fileBaseName);
        }

        return result;
    }
}
