using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Tasks.Refresh;

/// <summary>
/// Removes generated seasons or series roots whose refresh state and file hashes prove they are no longer configured.
/// </summary>
internal static class StaleReleaseCleanupService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<StaleReleaseCleanupResult> CleanupAsync(
        string outputRoot,
        IReadOnlySet<string> currentNormalizedKeys,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(currentNormalizedKeys);
        ArgumentNullException.ThrowIfNull(logger);

        var result = new StaleReleaseCleanupResult();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Directory.Exists(outputRoot))
        {
            return result;
        }

        string fullRoot;
        try
        {
            fullRoot = Path.GetFullPath(outputRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogWarning(ex, "[YummyKodik] Cannot resolve output root while cleaning stale releases. root='{Root}'", outputRoot);
            return result;
        }

        var normalizedCurrentKeys = currentNormalizedKeys
            .Select(RefreshPathUtilities.NormalizeKey)
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string[] seriesDirectories;
        try
        {
            seriesDirectories = Directory.EnumerateDirectories(fullRoot, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[YummyKodik] Cannot enumerate output root while cleaning stale releases. root='{Root}'", fullRoot);
            return result;
        }

        foreach (var seriesDirectory in seriesDirectories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.ExaminedDirectoryCount++;

            if (!TryGetStrictChildPath(fullRoot, seriesDirectory, out var fullSeriesDirectory))
            {
                result.SkippedDirectoryCount++;
                logger.LogWarning("[YummyKodik] Skipping stale-release cleanup outside the configured output root. path='{Path}'", seriesDirectory);
                continue;
            }

            if (IsReparsePoint(fullSeriesDirectory, logger))
            {
                result.SkippedDirectoryCount++;
                continue;
            }

            var state = await TryReadManagedStateAsync(fullSeriesDirectory, logger, cancellationToken).ConfigureAwait(false);
            if (state == null)
            {
                result.SkippedDirectoryCount++;
                continue;
            }

            var currentSeasonKeys = state.Seasons
                .Where(pair => normalizedCurrentKeys.Contains(RefreshPathUtilities.NormalizeKey(pair.Value!.CleanKey)))
                .Select(pair => pair.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (currentSeasonKeys.Count == 0)
            {
                await TryDeleteSeriesRootAsync(fullRoot, fullSeriesDirectory, state, logger, result, cancellationToken).ConfigureAwait(false);
                continue;
            }

            result.RetainedDirectoryCount++;
            var removedSeasonKeys = new List<string>();
            foreach (var staleSeason in state.Seasons.Where(pair => !currentSeasonKeys.Contains(pair.Key)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryGetStrictDirectChildPath(fullSeriesDirectory, staleSeason.Key, out var seasonDirectory))
                {
                    result.SkippedDirectoryCount++;
                    continue;
                }

                // A missing stale season has no content to delete and can be reconciled from valid state.
                if (!Directory.Exists(seasonDirectory))
                {
                    removedSeasonKeys.Add(staleSeason.Key);
                    continue;
                }

                if (IsReparsePoint(seasonDirectory, logger) ||
                    !await IsExactManagedSeasonDirectoryAsync(seasonDirectory, staleSeason.Key, staleSeason.Value!, logger, cancellationToken).ConfigureAwait(false))
                {
                    result.SkippedDirectoryCount++;
                    continue;
                }

                try
                {
                    if (Directory.Exists(seasonDirectory))
                    {
                        Directory.Delete(seasonDirectory, recursive: true);
                        result.DeletedDirectoryCount++;
                        result.DeletedSeasonDirectoryCount++;
                        result.DeletedPaths.Add(seasonDirectory);
                        logger.LogInformation("[YummyKodik] Removed stale generated season directory '{Path}'.", seasonDirectory);
                    }

                    removedSeasonKeys.Add(staleSeason.Key);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    result.DeleteFailureCount++;
                    logger.LogWarning(ex, "[YummyKodik] Failed to remove stale generated season directory '{Path}'.", seasonDirectory);
                }
            }

            if (removedSeasonKeys.Count > 0 &&
                !await TryRemoveSeasonEntriesAtomicallyAsync(state, removedSeasonKeys, logger, cancellationToken).ConfigureAwait(false))
            {
                result.StateWriteFailureCount++;
            }
        }

        return result;
    }

    private static bool TryGetStrictChildPath(string fullRoot, string path, out string fullPath)
    {
        fullPath = string.Empty;

        try
        {
            var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullRoot));
            var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

            if (!normalizedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var relativePath = Path.GetRelativePath(normalizedRoot, normalizedPath);
            if (relativePath.Length == 0 ||
                relativePath.Equals(".", StringComparison.Ordinal) ||
                relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                relativePath.Equals("..", StringComparison.Ordinal))
            {
                return false;
            }

            fullPath = normalizedPath;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryGetStrictDirectChildPath(string parentDirectory, string childName, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(childName) ||
            childName.Contains(Path.DirectorySeparatorChar) ||
            childName.Contains(Path.AltDirectorySeparatorChar) ||
            !string.Equals(Path.GetFileName(childName), childName, StringComparison.Ordinal))
        {
            return false;
        }

        return TryGetStrictChildPath(parentDirectory, Path.Combine(parentDirectory, childName), out fullPath) &&
               string.Equals(
                   Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullPath) ?? string.Empty),
                   Path.TrimEndingDirectorySeparator(Path.GetFullPath(parentDirectory)),
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string directory, ILogger logger)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[YummyKodik] Cannot inspect a series directory during stale-release cleanup. path='{Path}'", directory);
            return true;
        }
    }

    private static async Task<bool> IsExactManagedSeasonDirectoryAsync(
        string seasonDirectory,
        string seasonKey,
        RefreshStateSeasonDocument season,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(seasonDirectory) || season.ManagedFiles == null)
        {
            return !Directory.Exists(seasonDirectory);
        }

        var expectedFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var prefix = NormalizeRelativePath(seasonKey) + "/";
        foreach (var managedFile in season.ManagedFiles)
        {
            if (managedFile == null ||
                string.IsNullOrWhiteSpace(managedFile.RelativePath) ||
                string.IsNullOrWhiteSpace(managedFile.Sha256))
            {
                return false;
            }

            var relativePath = NormalizeRelativePath(managedFile.RelativePath);
            if (!relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                if (relativePath.Contains('/'))
                {
                    return false;
                }

                continue;
            }

            var fileName = relativePath[prefix.Length..];
            if (!IsDirectFileName(fileName) || !expectedFiles.TryAdd(fileName, managedFile.Sha256))
            {
                return false;
            }
        }

        try
        {
            var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Directory.EnumerateFileSystemEntries(seasonDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(entry);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return false;
                }

                var fileName = Path.GetFileName(entry);
                if (!IsDirectFileName(fileName) || !actualFiles.Add(fileName) || !expectedFiles.TryGetValue(fileName, out var expectedHash))
                {
                    return false;
                }

                var actualHash = await ComputeFileSha256HexAsync(entry, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return actualFiles.SetEquals(expectedFiles.Keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[YummyKodik] Cannot verify a stale season directory before cleanup. path='{Path}'", seasonDirectory);
            return false;
        }
    }

    private static async Task<bool> IsExactManagedSeriesDirectoryAsync(
        string seriesDirectory,
        ManagedRefreshState state,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var expectedRootFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var season in state.Seasons)
        {
            if (!TryGetStrictDirectChildPath(seriesDirectory, season.Key, out var seasonDirectory) ||
                IsReparsePoint(seasonDirectory, logger) ||
                !await IsExactManagedSeasonDirectoryAsync(seasonDirectory, season.Key, season.Value!, logger, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            foreach (var managedFile in season.Value!.ManagedFiles!)
            {
                if (managedFile == null ||
                    string.IsNullOrWhiteSpace(managedFile.RelativePath) ||
                    string.IsNullOrWhiteSpace(managedFile.Sha256))
                {
                    return false;
                }

                var relativePath = NormalizeRelativePath(managedFile.RelativePath);
                if (relativePath.Contains('/'))
                {
                    continue;
                }

                if (!IsDirectFileName(relativePath))
                {
                    return false;
                }

                if (expectedRootFiles.TryGetValue(relativePath, out var existingHash))
                {
                    if (!string.Equals(existingHash, managedFile.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
                else
                {
                    expectedRootFiles.Add(relativePath, managedFile.Sha256);
                }
            }
        }

        try
        {
            var actualDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var directory in Directory.EnumerateDirectories(seriesDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsReparsePoint(directory, logger) || !actualDirectories.Add(Path.GetFileName(directory)))
                {
                    return false;
                }
            }

            if (!actualDirectories.SetEquals(state.Seasons.Keys))
            {
                return false;
            }

            var actualFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(seriesDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var fileName = Path.GetFileName(file);
                if (string.Equals(fileName, RefreshStateManager.StateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!actualFiles.Add(fileName) || !expectedRootFiles.TryGetValue(fileName, out var expectedHash))
                {
                    return false;
                }

                var actualHash = await ComputeFileSha256HexAsync(file, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return actualFiles.SetEquals(expectedRootFiles.Keys);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "[YummyKodik] Cannot verify a stale series directory before cleanup. path='{Path}'", seriesDirectory);
            return false;
        }
    }

    private static bool IsDirectFileName(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !value.Contains('/') &&
               !value.Contains('\\') &&
               !value.Equals(".", StringComparison.Ordinal) &&
               !value.Equals("..", StringComparison.Ordinal) &&
               string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
    }

    private static string NormalizeRelativePath(string value)
    {
        return value.Trim().Replace('\\', '/').TrimStart('/');
    }

    private static async Task<string> ComputeFileSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task TryDeleteSeriesRootAsync(
        string fullRoot,
        string fullSeriesDirectory,
        ManagedRefreshState state,
        ILogger logger,
        StaleReleaseCleanupResult result,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Re-check immediately before recursive deletion; enumeration results can be stale.
            if (!TryGetStrictChildPath(fullRoot, fullSeriesDirectory, out var deletionTarget) ||
                IsReparsePoint(deletionTarget, logger) ||
                !await IsExactManagedSeriesDirectoryAsync(deletionTarget, state, logger, cancellationToken).ConfigureAwait(false))
            {
                result.SkippedDirectoryCount++;
                return;
            }

            Directory.Delete(deletionTarget, recursive: true);
            result.DeletedDirectoryCount++;
            result.DeletedSeriesDirectoryCount++;
            result.DeletedPaths.Add(deletionTarget);
            logger.LogInformation("[YummyKodik] Removed stale generated series directory '{Path}'.", deletionTarget);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.DeleteFailureCount++;
            logger.LogWarning(ex, "[YummyKodik] Failed to remove stale generated series directory '{Path}'.", fullSeriesDirectory);
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task<ManagedRefreshState?> TryReadManagedStateAsync(
        string seriesDirectory,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(seriesDirectory, RefreshStateManager.StateFileName);
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(statePath, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<RefreshStateDocument>(json, JsonOptions);
            var root = JsonNode.Parse(json) as JsonObject;
            if (state == null ||
                root == null ||
                state.SchemaVersion != RefreshStateManager.SchemaVersion ||
                state.GenerationContractVersion != RefreshStateManager.GenerationContractVersion ||
                state.Seasons == null ||
                state.Seasons.Count == 0 ||
                !HasCanonicalUniqueSeasons(state.Seasons))
            {
                return null;
            }

            var seasonsPropertyName = root
                .Select(pair => pair.Key)
                .FirstOrDefault(key => string.Equals(key, "seasons", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(seasonsPropertyName) || root[seasonsPropertyName] is not JsonObject seasonsNode)
            {
                return null;
            }

            return new ManagedRefreshState(statePath, root, seasonsPropertyName, seasonsNode, state.Seasons);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "[YummyKodik] Cannot read refresh state during stale-release cleanup. path='{Path}'", statePath);
            return null;
        }
    }

    private static bool HasCanonicalUniqueSeasons(IReadOnlyDictionary<string, RefreshStateSeasonDocument?> seasons)
    {
        var seasonNumbers = new HashSet<int>();
        foreach (var season in seasons)
        {
            if (season.Value == null ||
                string.IsNullOrWhiteSpace(season.Value.CleanKey) ||
                !season.Value.SeasonNumber.HasValue ||
                season.Value.ManagedFiles == null ||
                !string.Equals(
                    season.Key,
                    RefreshStateManager.BuildSeasonKey(season.Value.SeasonNumber.Value),
                    StringComparison.Ordinal) ||
                !seasonNumbers.Add(season.Value.SeasonNumber.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<bool> TryRemoveSeasonEntriesAtomicallyAsync(
        ManagedRefreshState state,
        IReadOnlyCollection<string> removedSeasonKeys,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        foreach (var seasonKey in removedSeasonKeys)
        {
            if (!state.SeasonsNode.Remove(seasonKey))
            {
                logger.LogWarning("[YummyKodik] Skipping refresh-state update because season key is ambiguous. path='{Path}' season='{Season}'", state.StatePath, seasonKey);
                return false;
            }
        }

        var temporaryPath = state.StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = state.Root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(temporaryPath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, state.StatePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "[YummyKodik] Failed to update refresh state after stale season cleanup. path='{Path}'", state.StatePath);
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "[YummyKodik] Failed to remove temporary stale-release state file '{Path}'.", temporaryPath);
            }
        }
    }

    private sealed class RefreshStateDocument
    {
        public int SchemaVersion { get; set; }
        public int GenerationContractVersion { get; set; }
        public Dictionary<string, RefreshStateSeasonDocument?>? Seasons { get; set; }
    }

    private sealed class RefreshStateSeasonDocument
    {
        public string CleanKey { get; set; } = string.Empty;
        public int? SeasonNumber { get; set; }
        public RefreshStateManagedFileDocument?[]? ManagedFiles { get; set; }
    }

    private sealed class RefreshStateManagedFileDocument
    {
        public string RelativePath { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
    }

    private sealed record ManagedRefreshState(
        string StatePath,
        JsonObject Root,
        string SeasonsPropertyName,
        JsonObject SeasonsNode,
        Dictionary<string, RefreshStateSeasonDocument?> Seasons);
}

internal sealed class StaleReleaseCleanupResult
{
    public int ExaminedDirectoryCount { get; internal set; }
    public int RetainedDirectoryCount { get; internal set; }
    public int SkippedDirectoryCount { get; internal set; }
    public int DeletedDirectoryCount { get; internal set; }
    public int DeletedSeriesDirectoryCount { get; internal set; }
    public int DeletedSeasonDirectoryCount { get; internal set; }
    public int DeleteFailureCount { get; internal set; }
    public int StateWriteFailureCount { get; internal set; }
    public List<string> DeletedPaths { get; } = new();
}
