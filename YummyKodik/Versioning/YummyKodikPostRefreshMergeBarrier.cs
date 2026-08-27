using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Util;

namespace YummyKodik.Versioning;

/// <summary>
/// Waits for Jellyfin to materialize episode items written by a managed refresh, then performs
/// one authoritative versions merge after the refresh batch has settled.
/// </summary>
public sealed class YummyKodikPostRefreshMergeBarrier
{
    private static readonly TimeSpan ArtifactTimestampTolerance = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadySettleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _libraryManager;
    private readonly YummyKodikEpisodeVersionsMergeHostedService _mergeService;
    private readonly ILogger<YummyKodikPostRefreshMergeBarrier> _logger;

    public YummyKodikPostRefreshMergeBarrier(
        ILibraryManager libraryManager,
        YummyKodikEpisodeVersionsMergeHostedService mergeService,
        ILogger<YummyKodikPostRefreshMergeBarrier> logger)
    {
        _libraryManager = libraryManager;
        _mergeService = mergeService;
        _logger = new YummyKodikLogger<YummyKodikPostRefreshMergeBarrier>(logger);
    }

    public Task<IDisposable> BeginRefreshBatchAsync(CancellationToken cancellationToken)
    {
        return _mergeService.BeginRefreshBatchAsync(cancellationToken);
    }

    public IReadOnlyCollection<string> CaptureCurrentStrmPaths(string outputRoot)
    {
        try
        {
            return EnumerateCurrentStrmPaths(outputRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[YummyKodik] Failed to capture the pre-refresh STRM set; deleted-item readiness checks will be skipped.");
            return Array.Empty<string>();
        }
    }

    public async Task WaitForEpisodesThenMergeAsync(
        string outputRoot,
        DateTime refreshStartedUtc,
        IReadOnlyCollection<string> initialStrmPaths,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ExpectedEpisodeArtifact> expectedArtifacts;
        IReadOnlyCollection<string> currentStrmPaths;
        try
        {
            expectedArtifacts = CollectExpectedArtifacts(outputRoot, refreshStartedUtc);
            currentStrmPaths = EnumerateCurrentStrmPaths(outputRoot);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "[YummyKodik] Failed to collect refreshed STRM paths for the Jellyfin readiness barrier; running a best-effort versions merge.");
            expectedArtifacts = Array.Empty<ExpectedEpisodeArtifact>();
            currentStrmPaths = Array.Empty<string>();
        }

        var deletedPaths = initialStrmPaths
            .Except(currentStrmPaths, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var deadlineUtc = DateTime.UtcNow + ReadinessTimeout;
        var consecutiveReadySnapshots = 0;
        PostRefreshReadiness readiness = default;

        while (expectedArtifacts.Count > 0 || deletedPaths.Length > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var episodes = LoadEpisodeSnapshots(outputRoot);
                readiness = EvaluatePostRefreshReadiness(
                    expectedArtifacts,
                    episodes,
                    refreshStartedUtc,
                    deletedPaths);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "[YummyKodik] Post-refresh Jellyfin readiness check failed; running a best-effort versions merge.");
                break;
            }

            if (readiness.IsReady)
            {
                consecutiveReadySnapshots++;
                if (consecutiveReadySnapshots >= 2)
                {
                    break;
                }

                await Task.Delay(ReadySettleInterval, cancellationToken).ConfigureAwait(false);
                continue;
            }

            consecutiveReadySnapshots = 0;
            if (DateTime.UtcNow >= deadlineUtc)
            {
                var examples = string.Join(
                    ", ",
                    readiness.UnresolvedPaths
                        .Take(3)
                        .Select(SafeFileName));

                _logger.LogWarning(
                    "[YummyKodik] Jellyfin did not materialize all refreshed episodes before the merge deadline. unresolved={Unresolved}/{Expected} examples={Examples}. Running a best-effort merge.",
                    readiness.UnresolvedPaths.Count,
                    expectedArtifacts.Count + deletedPaths.Length,
                    examples);
                break;
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }

        await ApplyAvailableEpisodeRunTimesAsync(outputRoot, cancellationToken).ConfigureAwait(false);
        await _mergeService.MergeAfterRefreshAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "[YummyKodik] Post-refresh episode versions merge completed. expectedChangedEpisodes={Expected} expectedDeletedEpisodes={Deleted}",
            expectedArtifacts.Count,
            deletedPaths.Length);
    }

    internal async Task ApplyAvailableEpisodeRunTimesAsync(
        string outputRoot,
        CancellationToken cancellationToken)
    {
        var updatedCount = 0;
        var episodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        })
            .OfType<Episode>()
            .Where(episode => IsUnderRoot(outputRoot, episode.Path))
            .Where(episode =>
                episode.Path != null &&
                episode.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var episode in episodes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var nfoPath = Path.ChangeExtension(episode.Path, ".nfo");
                if (!File.Exists(nfoPath))
                {
                    continue;
                }

                var xml = await File.ReadAllTextAsync(nfoPath, cancellationToken).ConfigureAwait(false);
                if (!NfoBuilder.TryGetEpisodeRuntimeSeconds(xml, out var durationSeconds))
                {
                    continue;
                }

                var resolvedRunTimeTicks = TimeSpan.FromSeconds(durationSeconds).Ticks;
                if (!MediaRunTimePolicy.ShouldPublish(episode.RunTimeTicks, resolvedRunTimeTicks))
                {
                    continue;
                }

                episode.RunTimeTicks = resolvedRunTimeTicks;
                await episode.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                updatedCount++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(
                    ex,
                    "[YummyKodik] Failed to apply generated NFO runtime to episode item. path='{Path}'",
                    episode.Path);
            }
        }

        if (updatedCount > 0)
        {
            _logger.LogInformation(
                "[YummyKodik] Applied generated runtimes to {UpdatedCount} Jellyfin episode item(s).",
                updatedCount);
        }
    }

    internal static IReadOnlyList<ExpectedEpisodeArtifact> CollectExpectedArtifacts(
        string outputRoot,
        DateTime refreshStartedUtc)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return Array.Empty<ExpectedEpisodeArtifact>();
        }

        var thresholdUtc = NormalizeUtc(refreshStartedUtc) - ArtifactTimestampTolerance;
        var results = new List<ExpectedEpisodeArtifact>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var strmPath in Directory.EnumerateFiles(outputRoot, "*.strm", options))
        {
            DateTime strmWriteUtc;
            try
            {
                strmWriteUtc = File.GetLastWriteTimeUtc(strmPath);
            }
            catch
            {
                continue;
            }

            if (strmWriteUtc < thresholdUtc)
            {
                continue;
            }

            var requiredRefreshUtc = strmWriteUtc;
            var nfoPath = Path.ChangeExtension(strmPath, ".nfo");
            try
            {
                if (File.Exists(nfoPath))
                {
                    var nfoWriteUtc = File.GetLastWriteTimeUtc(nfoPath);
                    if (nfoWriteUtc > requiredRefreshUtc)
                    {
                        requiredRefreshUtc = nfoWriteUtc;
                    }
                }
            }
            catch
            {
                // The STRM path is still a valid readiness target.
            }

            var normalizedPath = NormalizePath(strmPath);
            if (normalizedPath.Length > 0 && seen.Add(normalizedPath))
            {
                results.Add(new ExpectedEpisodeArtifact(normalizedPath, requiredRefreshUtc));
            }
        }

        return results;
    }

    internal static PostRefreshReadiness EvaluatePostRefreshReadiness(
        IReadOnlyCollection<ExpectedEpisodeArtifact> expectedArtifacts,
        IReadOnlyCollection<EpisodeReadinessSnapshot> episodeSnapshots,
        DateTime refreshStartedUtc)
    {
        return EvaluatePostRefreshReadiness(
            expectedArtifacts,
            episodeSnapshots,
            refreshStartedUtc,
            Array.Empty<string>());
    }

    internal static PostRefreshReadiness EvaluatePostRefreshReadiness(
        IReadOnlyCollection<ExpectedEpisodeArtifact> expectedArtifacts,
        IReadOnlyCollection<EpisodeReadinessSnapshot> episodeSnapshots,
        DateTime refreshStartedUtc,
        IReadOnlyCollection<string> deletedPaths)
    {
        var episodesByPath = episodeSnapshots
            .Where(x => !string.IsNullOrWhiteSpace(x.Path))
            .GroupBy(x => NormalizePath(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var refreshThresholdUtc = NormalizeUtc(refreshStartedUtc) - ArtifactTimestampTolerance;
        var unresolved = new List<string>();

        foreach (var artifact in expectedArtifacts)
        {
            var path = NormalizePath(artifact.Path);
            if (!episodesByPath.TryGetValue(path, out var episode) ||
                episode.IndexNumber is not > 0)
            {
                unresolved.Add(path);
                continue;
            }

            // Existing items already have a stable library identity. Newly created items must
            // complete their metadata refresh after the generated STRM/NFO timestamps, otherwise
            // that later repository save can overwrite links written by an early merge.
            if (episode.DateCreatedUtc >= refreshThresholdUtc &&
                episode.DateLastRefreshedUtc + ArtifactTimestampTolerance < artifact.RequiredRefreshUtc)
            {
                unresolved.Add(path);
            }
        }

        foreach (var deletedPath in deletedPaths)
        {
            var path = NormalizePath(deletedPath);
            if (episodesByPath.ContainsKey(path))
            {
                unresolved.Add(path);
            }
        }

        return new PostRefreshReadiness(unresolved.Count == 0, unresolved);
    }

    private static IReadOnlyCollection<string> EnumerateCurrentStrmPaths(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return Array.Empty<string>();
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory.EnumerateFiles(outputRoot, "*.strm", options)
            .Select(NormalizePath)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<EpisodeReadinessSnapshot> LoadEpisodeSnapshots(string outputRoot)
    {
        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        })
            .OfType<Episode>()
            .Where(x => IsUnderRoot(outputRoot, x.Path))
            .Where(x => x.Path != null && x.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            .Select(x => new EpisodeReadinessSnapshot(
                NormalizePath(x.Path!),
                x.IndexNumber,
                NormalizeUtc(x.DateCreated),
                NormalizeUtc(x.DateLastRefreshed)))
            .ToList();
    }

    private static bool IsUnderRoot(string root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static DateTime NormalizeUtc(DateTime value)
    {
        if (value == default)
        {
            return default;
        }

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }
}

internal readonly record struct ExpectedEpisodeArtifact(string Path, DateTime RequiredRefreshUtc);

internal readonly record struct EpisodeReadinessSnapshot(
    string Path,
    int? IndexNumber,
    DateTime DateCreatedUtc,
    DateTime DateLastRefreshedUtc);

internal readonly record struct PostRefreshReadiness(
    bool IsReady,
    IReadOnlyList<string> UnresolvedPaths);
