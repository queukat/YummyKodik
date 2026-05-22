using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YummyKodik.Tasks;

public static class RefreshStateManager
{
    public const int SchemaVersion = 1;
    public const int GenerationContractVersion = 1;
    public const string StateFileName = ".yummykodik.refresh-state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string BuildSeasonKey(int seasonNumber)
    {
        return $"Season {NormalizeSeasonNumber(seasonNumber):00}";
    }

    public static string BuildFingerprint(RefreshStateFingerprintInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["generationContractVersion"] = input.GenerationContractVersion,
            ["mode"] = Normalize(input.Mode),
            ["serverBaseUrl"] = NormalizeBaseUrl(input.ServerBaseUrl),
            ["preferredTranslationFilter"] = Normalize(input.PreferredTranslationFilter),
            ["cleanKey"] = Normalize(input.CleanKey),
            ["rawTitle"] = Normalize(input.RawTitle),
            ["seriesTitle"] = Normalize(input.SeriesTitle),
            ["seasonKey"] = Normalize(input.SeasonKey),
            ["seasonNumber"] = input.SeasonNumber,
            ["animeId"] = input.AnimeId,
            ["animeUrl"] = Normalize(input.AnimeUrl),
            ["shikimoriId"] = input.ShikimoriId,
            ["kinopoiskId"] = input.KinopoiskId,
            ["imdbId"] = Normalize(input.ImdbId),
            ["expectedAvailableEpisodes"] = input.ExpectedAvailableEpisodes,
            ["knownSupportedEpisodes"] = NormalizeEpisodes(input.KnownSupportedEpisodes),
            ["allohaSupportedEpisodes"] = NormalizeEpisodes(input.AllohaSupportedEpisodes),
            ["cvhSupportedEpisodes"] = NormalizeEpisodes(input.CvhSupportedEpisodes),
            ["yummySupportedEpisodes"] = NormalizeEpisodes(input.YummySupportedEpisodes),
            ["providerCoverage"] = NormalizeStrings(input.ProviderCoverage),
            ["allohaApiBaseUrl"] = Normalize(input.AllohaApiBaseUrl),
            ["allohaApiTokenHash"] = Normalize(input.AllohaApiTokenHash)
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return "sha256:" + ComputeSha256Hex(json);
    }

    public static string HashSecret(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Length == 0 ? string.Empty : "sha256:" + ComputeSha256Hex(normalized);
    }

    public static async Task<bool> CanSkipSingleFileRefreshAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        if (input.CreateStrmPerVoiceTranslation)
        {
            return false;
        }

        var state = await TryReadStateAsync(seriesRoot, cancellationToken).ConfigureAwait(false);
        if (state == null ||
            state.SchemaVersion != SchemaVersion ||
            state.GenerationContractVersion != GenerationContractVersion ||
            state.Seasons == null ||
            !state.Seasons.TryGetValue(input.SeasonKey, out var season) ||
            season == null)
        {
            return false;
        }

        if (!string.Equals(season.Mode, RefreshStateMode.SingleFile, StringComparison.Ordinal) ||
            !string.Equals(season.Fingerprint, input.Fingerprint, StringComparison.Ordinal) ||
            season.ExpectedAvailableEpisodes != input.ExpectedAvailableEpisodes ||
            !CoversExpectedEpisodes(season.CoveredEpisodes, input.ExpectedAvailableEpisodes))
        {
            return false;
        }

        if (!await ManagedFilesMatchAsync(seriesRoot, season.ManagedFiles, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var expectedEpisodeFileBaseNames = BuildExpectedEpisodeFileBaseNames(input, season.ManagedFiles);
        var unexpectedArtifacts = EpisodeArtifactMaintenance.FindUnexpectedEpisodeArtifacts(
            Path.Combine(seriesRoot, input.SeasonKey),
            input.SeasonNumber,
            expectedEpisodeFileBaseNames,
            input.ExpectedAvailableEpisodes);

        return unexpectedArtifacts.Count == 0;
    }

    public static async Task<bool> WriteSeasonStateAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);

        var managedFiles = await BuildManagedFilesAsync(
                seriesRoot,
                input.SeasonKey,
                expectedEpisodeFileBaseNames,
                cancellationToken)
            .ConfigureAwait(false);

        if (managedFiles == null)
        {
            return false;
        }

        var state = await TryReadStateAsync(seriesRoot, cancellationToken).ConfigureAwait(false) ?? new RefreshStateFile();
        state.SchemaVersion = SchemaVersion;
        state.GenerationContractVersion = GenerationContractVersion;
        state.Seasons ??= new Dictionary<string, RefreshStateSeason>(StringComparer.OrdinalIgnoreCase);

        state.Seasons[input.SeasonKey] = new RefreshStateSeason
        {
            SeasonNumber = input.SeasonNumber,
            CleanKey = input.CleanKey,
            Fingerprint = input.Fingerprint,
            Mode = input.CreateStrmPerVoiceTranslation
                ? RefreshStateMode.PerVoice
                : RefreshStateMode.SingleFile,
            ExpectedAvailableEpisodes = input.ExpectedAvailableEpisodes,
            CoveredEpisodes = expectedEpisodeFileBaseNames.Keys
                .Where(ep => ep > 0)
                .Distinct()
                .OrderBy(ep => ep)
                .ToArray(),
            ManagedFiles = managedFiles,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var statePath = ResolveStatePath(seriesRoot);
        Directory.CreateDirectory(seriesRoot);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await WriteTextAtomicallyAsync(statePath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static async Task<RefreshStateFile?> TryReadStateAsync(
        string seriesRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = ResolveStatePath(seriesRoot);
            if (!File.Exists(path))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return JsonSerializer.Deserialize<RefreshStateFile>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static async Task<RefreshStateManagedFile[]?> BuildManagedFilesAsync(
        string seriesRoot,
        string seasonKey,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        CancellationToken cancellationToken)
    {
        var relativePaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfExists(relativePaths, seriesRoot, "tvshow.nfo");
        AddIfExists(relativePaths, seriesRoot, "poster.jpg");

        foreach (var fileBaseName in expectedEpisodeFileBaseNames
                     .OrderBy(x => x.Key)
                     .SelectMany(x => x.Value.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)))
        {
            relativePaths.Add(CombineRelative(seasonKey, fileBaseName + ".strm"));
            relativePaths.Add(CombineRelative(seasonKey, fileBaseName + ".nfo"));
        }

        var managedFiles = new List<RefreshStateManagedFile>(relativePaths.Count);
        foreach (var relativePath in relativePaths)
        {
            if (!TryResolveManagedPath(seriesRoot, relativePath, out var fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            managedFiles.Add(new RefreshStateManagedFile
            {
                RelativePath = NormalizeRelativePath(relativePath),
                Sha256 = await ComputeFileSha256HexAsync(fullPath, cancellationToken).ConfigureAwait(false),
                Kind = ResolveManagedFileKind(relativePath)
            });
        }

        return managedFiles.ToArray();
    }

    private static void AddIfExists(SortedSet<string> relativePaths, string seriesRoot, string relativePath)
    {
        if (TryResolveManagedPath(seriesRoot, relativePath, out var fullPath) && File.Exists(fullPath))
        {
            relativePaths.Add(relativePath);
        }
    }

    private static async Task<bool> ManagedFilesMatchAsync(
        string seriesRoot,
        IReadOnlyList<RefreshStateManagedFile>? managedFiles,
        CancellationToken cancellationToken)
    {
        if (managedFiles == null || managedFiles.Count == 0)
        {
            return false;
        }

        foreach (var file in managedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath) ||
                string.IsNullOrWhiteSpace(file.Sha256) ||
                !TryResolveManagedPath(seriesRoot, file.RelativePath, out var fullPath) ||
                !File.Exists(fullPath))
            {
                return false;
            }

            var currentHash = await ComputeFileSha256HexAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<int, HashSet<string>> BuildExpectedEpisodeFileBaseNames(
        RefreshStateSeasonInput input,
        IReadOnlyList<RefreshStateManagedFile>? managedFiles)
    {
        var result = new Dictionary<int, HashSet<string>>();
        if (managedFiles == null || managedFiles.Count == 0)
        {
            return result;
        }

        var expectedSeasonPrefix = "S" + NormalizeSeasonNumber(input.SeasonNumber).ToString("00", CultureInfo.InvariantCulture);
        var seasonPathPrefix = NormalizeRelativePath(input.SeasonKey) + "/";

        foreach (var managedFile in managedFiles)
        {
            var relativePath = NormalizeRelativePath(managedFile.RelativePath);
            if (!relativePath.StartsWith(seasonPathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var extension = Path.GetExtension(relativePath);
            if (!string.Equals(extension, ".strm", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, ".nfo", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileBaseName = Path.GetFileNameWithoutExtension(relativePath) ?? string.Empty;
            if (!fileBaseName.StartsWith(expectedSeasonPrefix + "E", StringComparison.OrdinalIgnoreCase) ||
                fileBaseName.Length < expectedSeasonPrefix.Length + 3 ||
                !int.TryParse(
                    fileBaseName.AsSpan(expectedSeasonPrefix.Length + 1, 2),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var episodeNumber) ||
                episodeNumber <= 0)
            {
                continue;
            }

            if (!result.TryGetValue(episodeNumber, out var fileBaseNames))
            {
                fileBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                result[episodeNumber] = fileBaseNames;
            }

            fileBaseNames.Add(fileBaseName);
        }

        return result;
    }

    private static bool CoversExpectedEpisodes(IEnumerable<int>? coveredEpisodes, int expectedAvailableEpisodes)
    {
        if (expectedAvailableEpisodes <= 0)
        {
            return true;
        }

        var covered = new HashSet<int>((coveredEpisodes ?? Array.Empty<int>()).Where(ep => ep > 0));
        for (var ep = 1; ep <= expectedAvailableEpisodes; ep++)
        {
            if (!covered.Contains(ep))
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveStatePath(string seriesRoot)
    {
        return Path.Combine(seriesRoot, StateFileName);
    }

    private static bool TryResolveManagedPath(string seriesRoot, string relativePath, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(seriesRoot) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var root = Path.GetFullPath(seriesRoot);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var normalizedRelativePath = relativePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);

        fullPath = Path.GetFullPath(Path.Combine(root, normalizedRelativePath));
        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> ComputeFileSha256HexAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteTextAtomicallyAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException($"Failed to determine directory for path '{path}'.");
        }

        Directory.CreateDirectory(directory);
        var tempPath = Path.Combine(directory, Path.GetFileName(path) + ".tmp." + Guid.NewGuid().ToString("N"));

        try
        {
            await File.WriteAllTextAsync(tempPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup for interrupted atomic writes.
                }
            }
        }
    }

    private static string ComputeSha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int NormalizeSeasonNumber(int seasonNumber)
    {
        return seasonNumber >= 0 ? seasonNumber : 1;
    }

    private static int[] NormalizeEpisodes(IEnumerable<int>? episodes)
    {
        return (episodes ?? Array.Empty<int>())
            .Where(ep => ep > 0)
            .Distinct()
            .OrderBy(ep => ep)
            .ToArray();
    }

    private static string[] NormalizeStrings(IEnumerable<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Select(Normalize)
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static string NormalizeBaseUrl(string? value)
    {
        return Normalize(value).TrimEnd('/');
    }

    private static string Normalize(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeRelativePath(string? value)
    {
        return Normalize(value).Replace('\\', '/');
    }

    private static string CombineRelative(string left, string right)
    {
        return NormalizeRelativePath(left).TrimEnd('/') + "/" + NormalizeRelativePath(right).TrimStart('/');
    }

    private static string ResolveManagedFileKind(string relativePath)
    {
        var extension = Path.GetExtension(relativePath);
        if (extension.StartsWith(".", StringComparison.Ordinal))
        {
            extension = extension[1..];
        }

        return extension.ToLowerInvariant();
    }

    private sealed class RefreshStateFile
    {
        public int SchemaVersion { get; set; }
        public int GenerationContractVersion { get; set; }
        public Dictionary<string, RefreshStateSeason>? Seasons { get; set; }
    }

    private sealed class RefreshStateSeason
    {
        public int SeasonNumber { get; set; }
        public string CleanKey { get; set; } = string.Empty;
        public string Fingerprint { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public int ExpectedAvailableEpisodes { get; set; }
        public int[] CoveredEpisodes { get; set; } = Array.Empty<int>();
        public RefreshStateManagedFile[] ManagedFiles { get; set; } = Array.Empty<RefreshStateManagedFile>();
        public DateTimeOffset UpdatedAtUtc { get; set; }
    }

    private static class RefreshStateMode
    {
        public const string SingleFile = "single-file";
        public const string PerVoice = "per-voice";
    }
}

public sealed class RefreshStateSeasonInput
{
    public string SeasonKey { get; init; } = string.Empty;
    public int SeasonNumber { get; init; }
    public string CleanKey { get; init; } = string.Empty;
    public bool CreateStrmPerVoiceTranslation { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public int ExpectedAvailableEpisodes { get; init; }
}

public sealed class RefreshStateFingerprintInput
{
    public int GenerationContractVersion { get; init; } = RefreshStateManager.GenerationContractVersion;
    public string Mode { get; init; } = string.Empty;
    public string ServerBaseUrl { get; init; } = string.Empty;
    public string PreferredTranslationFilter { get; init; } = string.Empty;
    public string CleanKey { get; init; } = string.Empty;
    public string RawTitle { get; init; } = string.Empty;
    public string SeriesTitle { get; init; } = string.Empty;
    public string SeasonKey { get; init; } = string.Empty;
    public int SeasonNumber { get; init; }
    public long AnimeId { get; init; }
    public string AnimeUrl { get; init; } = string.Empty;
    public long? ShikimoriId { get; init; }
    public long? KinopoiskId { get; init; }
    public string ImdbId { get; init; } = string.Empty;
    public int ExpectedAvailableEpisodes { get; init; }
    public IReadOnlyList<int> KnownSupportedEpisodes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> AllohaSupportedEpisodes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> CvhSupportedEpisodes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> YummySupportedEpisodes { get; init; } = Array.Empty<int>();
    public IReadOnlyList<string> ProviderCoverage { get; init; } = Array.Empty<string>();
    public string AllohaApiBaseUrl { get; init; } = string.Empty;
    public string AllohaApiTokenHash { get; init; } = string.Empty;
}

public sealed class RefreshStateManagedFile
{
    public string RelativePath { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
}
