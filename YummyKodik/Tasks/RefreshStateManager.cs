using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace YummyKodik.Tasks;

internal enum RefreshSkipPath
{
    SingleFileBeforeKodik,
    PerVoiceBeforeKodik,
    PerVoiceAfterCatalog
}

internal enum RefreshSkipReason
{
    Matched,
    NotApplicableMode,
    PreferredQualityRequiresLookup,
    StateMissing,
    StateUnreadable,
    StateVersionMismatch,
    SeasonMissing,
    ModeMismatch,
    FingerprintMismatch,
    ExpectedEpisodeCountMismatch,
    CoverageIncomplete,
    CatalogSignatureMissing,
    CatalogSignatureMismatch,
    DeepValidationMissing,
    DeepValidationFromFuture,
    DeepValidationExpired,
    ManagedFilesMissingOrInvalid,
    ManagedFileHashMismatch,
    UnexpectedArtifacts
}

internal readonly record struct RefreshSkipDecision(
    RefreshSkipPath Path,
    bool ShouldSkip,
    RefreshSkipReason Reason,
    int ManagedFilesExpected = 0,
    int ManagedFilesChecked = 0,
    int UnexpectedArtifactCount = 0);

public static class RefreshStateManager
{
    public const int SchemaVersion = 1;
    public const int GenerationContractVersion = 3;
    public const string StateFileName = ".yummykodik.refresh-state.json";
    public const int KodikKnownMaximumQuality = 720;
    public static readonly TimeSpan PerVoiceDeepValidationInterval = TimeSpan.FromHours(24);

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
            ["streamGatewayBaseUrl"] = NormalizeBaseUrl(input.StreamGatewayBaseUrl),
            ["preferredTranslationFilter"] = Normalize(input.PreferredTranslationFilter),
            ["preferredQuality"] = input.PreferredQuality,
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

    public static string BuildKodikCatalogSignature(RefreshStateKodikCatalogInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var translations = (input.Translations ?? Array.Empty<RefreshStateKodikTranslationInput>())
            .Select(translation => new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = Normalize(translation.Id),
                ["type"] = Normalize(translation.Type),
                ["name"] = Normalize(translation.Name),
                ["maxEpisode"] = translation.MaxEpisode,
                ["availableEpisodes"] = NormalizeEpisodes(translation.AvailableEpisodes)
            })
            .OrderBy(translation => translation["id"]?.ToString(), StringComparer.Ordinal)
            .ThenBy(translation => translation["type"]?.ToString(), StringComparer.Ordinal)
            .ThenBy(translation => translation["name"]?.ToString(), StringComparer.Ordinal)
            .ThenBy(translation => translation["maxEpisode"])
            .ThenBy(
                translation => string.Join(",", (int[])(translation["availableEpisodes"] ?? Array.Empty<int>())),
                StringComparer.Ordinal)
            .ToArray();

        var payload = new SortedDictionary<string, object?>(StringComparer.Ordinal)
        {
            ["idType"] = Normalize(input.IdType),
            ["id"] = Normalize(input.Id),
            ["seriesCount"] = input.SeriesCount,
            ["translations"] = translations
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
        var decision = await EvaluateSingleFileRefreshAsync(seriesRoot, input, cancellationToken).ConfigureAwait(false);
        return decision.ShouldSkip;
    }

    internal static async Task<RefreshSkipDecision> EvaluateSingleFileRefreshAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        const RefreshSkipPath path = RefreshSkipPath.SingleFileBeforeKodik;

        if (input.CreateStrmPerVoiceTranslation)
        {
            return Reject(path, RefreshSkipReason.NotApplicableMode);
        }

        var stateRead = await ReadStateForDecisionAsync(seriesRoot, cancellationToken).ConfigureAwait(false);
        if (stateRead.State == null)
        {
            return Reject(path, stateRead.Reason);
        }

        var state = stateRead.State;
        if (state.SchemaVersion != SchemaVersion || state.GenerationContractVersion != GenerationContractVersion)
        {
            return Reject(path, RefreshSkipReason.StateVersionMismatch);
        }

        if (state.Seasons == null ||
            !state.Seasons.TryGetValue(input.SeasonKey, out var season) ||
            season == null)
        {
            return Reject(path, RefreshSkipReason.SeasonMissing);
        }

        var inputDecision = ValidateSeasonInputs(path, season, input, RefreshStateMode.SingleFile);
        if (inputDecision.HasValue)
        {
            return inputDecision.Value;
        }

        return await ValidateManagedFilesAndArtifactsAsync(
                path,
                seriesRoot,
                input,
                season,
                input.ExpectedAvailableEpisodes,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static Task<bool> CanSkipPerVoiceDeepRefreshAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        string kodikCatalogSignature,
        CancellationToken cancellationToken)
    {
        return CanSkipPerVoiceDeepRefreshAsync(
            seriesRoot,
            input,
            kodikCatalogSignature,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public static Task<bool> CanSkipPerVoiceKodikLookupAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        CancellationToken cancellationToken)
    {
        return CanSkipPerVoiceKodikLookupAsync(
            seriesRoot,
            input,
            DateTimeOffset.UtcNow,
            cancellationToken);
    }

    public static async Task<bool> CanSkipPerVoiceKodikLookupAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var decision = await EvaluatePerVoiceKodikLookupAsync(seriesRoot, input, nowUtc, cancellationToken)
            .ConfigureAwait(false);
        return decision.ShouldSkip;
    }

    internal static async Task<RefreshSkipDecision> EvaluatePerVoiceKodikLookupAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        const RefreshSkipPath path = RefreshSkipPath.PerVoiceBeforeKodik;

        if (!input.CreateStrmPerVoiceTranslation)
        {
            return Reject(path, RefreshSkipReason.NotApplicableMode);
        }

        if (input.PreferredQuality <= KodikKnownMaximumQuality)
        {
            return Reject(path, RefreshSkipReason.PreferredQualityRequiresLookup);
        }

        var stateRead = await ReadStateForDecisionAsync(seriesRoot, cancellationToken).ConfigureAwait(false);
        if (stateRead.State == null)
        {
            return Reject(path, stateRead.Reason);
        }

        var state = stateRead.State;
        if (state.SchemaVersion != SchemaVersion || state.GenerationContractVersion != GenerationContractVersion)
        {
            return Reject(path, RefreshSkipReason.StateVersionMismatch);
        }

        if (state.Seasons == null ||
            !state.Seasons.TryGetValue(input.SeasonKey, out var season) ||
            season == null)
        {
            return Reject(path, RefreshSkipReason.SeasonMissing);
        }

        var inputDecision = ValidateSeasonInputs(path, season, input, RefreshStateMode.PerVoice);
        if (inputDecision.HasValue)
        {
            return inputDecision.Value;
        }

        if (string.IsNullOrWhiteSpace(season.KodikCatalogSignature))
        {
            return Reject(path, RefreshSkipReason.CatalogSignatureMissing);
        }

        var validationDecision = ValidateDeepValidation(path, season, nowUtc);
        if (validationDecision.HasValue)
        {
            return validationDecision.Value;
        }

        var maxExpectedEpisodeNumber = Math.Max(
            input.ExpectedAvailableEpisodes,
            season.CoveredEpisodes.DefaultIfEmpty().Max());
        return await ValidateManagedFilesAndArtifactsAsync(
                path,
                seriesRoot,
                input,
                season,
                maxExpectedEpisodeNumber,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<bool> CanSkipPerVoiceDeepRefreshAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        string kodikCatalogSignature,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var decision = await EvaluatePerVoiceDeepRefreshAsync(
                seriesRoot,
                input,
                kodikCatalogSignature,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);
        return decision.ShouldSkip;
    }

    internal static async Task<RefreshSkipDecision> EvaluatePerVoiceDeepRefreshAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        string kodikCatalogSignature,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        const RefreshSkipPath path = RefreshSkipPath.PerVoiceAfterCatalog;

        if (!input.CreateStrmPerVoiceTranslation)
        {
            return Reject(path, RefreshSkipReason.NotApplicableMode);
        }

        if (string.IsNullOrWhiteSpace(kodikCatalogSignature))
        {
            return Reject(path, RefreshSkipReason.CatalogSignatureMissing);
        }

        var stateRead = await ReadStateForDecisionAsync(seriesRoot, cancellationToken).ConfigureAwait(false);
        if (stateRead.State == null)
        {
            return Reject(path, stateRead.Reason);
        }

        var state = stateRead.State;
        if (state.SchemaVersion != SchemaVersion || state.GenerationContractVersion != GenerationContractVersion)
        {
            return Reject(path, RefreshSkipReason.StateVersionMismatch);
        }

        if (state.Seasons == null ||
            !state.Seasons.TryGetValue(input.SeasonKey, out var season) ||
            season == null)
        {
            return Reject(path, RefreshSkipReason.SeasonMissing);
        }

        var inputDecision = ValidateSeasonInputs(path, season, input, RefreshStateMode.PerVoice);
        if (inputDecision.HasValue)
        {
            return inputDecision.Value;
        }

        if (string.IsNullOrWhiteSpace(season.KodikCatalogSignature))
        {
            return Reject(path, RefreshSkipReason.CatalogSignatureMissing);
        }

        if (!string.Equals(season.KodikCatalogSignature, kodikCatalogSignature, StringComparison.Ordinal))
        {
            return Reject(path, RefreshSkipReason.CatalogSignatureMismatch);
        }

        var validationDecision = ValidateDeepValidation(path, season, nowUtc);
        if (validationDecision.HasValue)
        {
            return validationDecision.Value;
        }

        var maxExpectedEpisodeNumber = Math.Max(
            input.ExpectedAvailableEpisodes,
            season.CoveredEpisodes.DefaultIfEmpty().Max());
        return await ValidateManagedFilesAndArtifactsAsync(
                path,
                seriesRoot,
                input,
                season,
                maxExpectedEpisodeNumber,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<bool> WriteSeasonStateAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        CancellationToken cancellationToken)
    {
        return await WriteSeasonStateAsync(
                seriesRoot,
                input,
                expectedEpisodeFileBaseNames,
                mediaSegmentEntriesByFileBaseName: null,
                kodikValidation: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<bool> WriteSeasonStateAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        IReadOnlyDictionary<string, RefreshStateMediaSegmentEntry>? mediaSegmentEntriesByFileBaseName,
        CancellationToken cancellationToken)
    {
        return await WriteSeasonStateAsync(
                seriesRoot,
                input,
                expectedEpisodeFileBaseNames,
                mediaSegmentEntriesByFileBaseName,
                kodikValidation: null,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<bool> WriteSeasonStateAsync(
        string seriesRoot,
        RefreshStateSeasonInput input,
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        IReadOnlyDictionary<string, RefreshStateMediaSegmentEntry>? mediaSegmentEntriesByFileBaseName,
        RefreshStateKodikValidation? kodikValidation,
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
            MediaSegments = BuildMediaSegmentEntries(expectedEpisodeFileBaseNames, mediaSegmentEntriesByFileBaseName),
            KodikCatalogSignature = Normalize(kodikValidation?.CatalogSignature),
            KodikDeepValidatedAtUtc = kodikValidation?.DeepValidatedAtUtc,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var statePath = ResolveStatePath(seriesRoot);
        Directory.CreateDirectory(seriesRoot);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        await WriteTextAtomicallyAsync(statePath, json + Environment.NewLine, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public static async Task<RefreshStateMediaSegmentEntry?> TryReadMediaSegmentEntryForPathAsync(
        string episodeFilePath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(episodeFilePath))
        {
            return null;
        }

        try
        {
            var fullPath = Path.GetFullPath(episodeFilePath);
            var seasonDir = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrWhiteSpace(seasonDir))
            {
                return null;
            }

            var seriesRoot = Path.GetDirectoryName(seasonDir);
            if (string.IsNullOrWhiteSpace(seriesRoot))
            {
                return null;
            }

            var seasonKey = Path.GetFileName(seasonDir);
            var fileBaseName = Path.GetFileNameWithoutExtension(fullPath);
            if (string.IsNullOrWhiteSpace(seasonKey) || string.IsNullOrWhiteSpace(fileBaseName))
            {
                return null;
            }

            var state = await TryReadStateAsync(seriesRoot, cancellationToken).ConfigureAwait(false);
            if (state == null ||
                state.SchemaVersion != SchemaVersion ||
                state.GenerationContractVersion != GenerationContractVersion ||
                state.Seasons == null ||
                !state.Seasons.TryGetValue(seasonKey, out var season) ||
                season?.MediaSegments == null ||
                season.MediaSegments.Length == 0)
            {
                return null;
            }

            return season.MediaSegments.FirstOrDefault(entry =>
                string.Equals(entry.FileBaseName, fileBaseName, StringComparison.OrdinalIgnoreCase) &&
                entry.Segments is { Length: > 0 });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
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

    private static async Task<StateReadDecision> ReadStateForDecisionAsync(
        string seriesRoot,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = ResolveStatePath(seriesRoot);
            if (!File.Exists(path))
            {
                return new StateReadDecision(null, RefreshSkipReason.StateMissing);
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var state = JsonSerializer.Deserialize<RefreshStateFile>(json, JsonOptions);
            return state == null
                ? new StateReadDecision(null, RefreshSkipReason.StateUnreadable)
                : new StateReadDecision(state, RefreshSkipReason.Matched);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new StateReadDecision(null, RefreshSkipReason.StateUnreadable);
        }
    }

    private static RefreshSkipDecision? ValidateSeasonInputs(
        RefreshSkipPath path,
        RefreshStateSeason season,
        RefreshStateSeasonInput input,
        string expectedMode)
    {
        if (!string.Equals(season.Mode, expectedMode, StringComparison.Ordinal))
        {
            return Reject(path, RefreshSkipReason.ModeMismatch);
        }

        if (!string.Equals(season.Fingerprint, input.Fingerprint, StringComparison.Ordinal))
        {
            return Reject(path, RefreshSkipReason.FingerprintMismatch);
        }

        if (season.ExpectedAvailableEpisodes != input.ExpectedAvailableEpisodes)
        {
            return Reject(path, RefreshSkipReason.ExpectedEpisodeCountMismatch);
        }

        if (!CoversExpectedEpisodes(season.CoveredEpisodes, input.ExpectedAvailableEpisodes))
        {
            return Reject(path, RefreshSkipReason.CoverageIncomplete);
        }

        return null;
    }

    private static RefreshSkipDecision? ValidateDeepValidation(
        RefreshSkipPath path,
        RefreshStateSeason season,
        DateTimeOffset nowUtc)
    {
        if (!season.KodikDeepValidatedAtUtc.HasValue)
        {
            return Reject(path, RefreshSkipReason.DeepValidationMissing);
        }

        if (season.KodikDeepValidatedAtUtc.Value > nowUtc)
        {
            return Reject(path, RefreshSkipReason.DeepValidationFromFuture);
        }

        if (nowUtc - season.KodikDeepValidatedAtUtc.Value >= PerVoiceDeepValidationInterval)
        {
            return Reject(path, RefreshSkipReason.DeepValidationExpired);
        }

        return null;
    }

    private static async Task<RefreshSkipDecision> ValidateManagedFilesAndArtifactsAsync(
        RefreshSkipPath path,
        string seriesRoot,
        RefreshStateSeasonInput input,
        RefreshStateSeason season,
        int maxExpectedEpisodeNumber,
        CancellationToken cancellationToken)
    {
        var managedFiles = await CheckManagedFilesAsync(seriesRoot, season.ManagedFiles, cancellationToken)
            .ConfigureAwait(false);
        if (!managedFiles.Matches)
        {
            return new RefreshSkipDecision(
                path,
                false,
                managedFiles.Reason,
                managedFiles.Expected,
                managedFiles.Checked);
        }

        var expectedEpisodeFileBaseNames = BuildExpectedEpisodeFileBaseNames(input, season.ManagedFiles);
        var unexpectedArtifacts = EpisodeArtifactMaintenance.FindUnexpectedEpisodeArtifacts(
            Path.Combine(seriesRoot, input.SeasonKey),
            input.SeasonNumber,
            expectedEpisodeFileBaseNames,
            maxExpectedEpisodeNumber);
        if (unexpectedArtifacts.Count > 0)
        {
            return new RefreshSkipDecision(
                path,
                false,
                RefreshSkipReason.UnexpectedArtifacts,
                managedFiles.Expected,
                managedFiles.Checked,
                unexpectedArtifacts.Count);
        }

        return new RefreshSkipDecision(
            path,
            true,
            RefreshSkipReason.Matched,
            managedFiles.Expected,
            managedFiles.Checked);
    }

    private static RefreshSkipDecision Reject(RefreshSkipPath path, RefreshSkipReason reason)
    {
        return new RefreshSkipDecision(path, false, reason);
    }

    private static RefreshStateMediaSegmentEntry[] BuildMediaSegmentEntries(
        IReadOnlyDictionary<int, HashSet<string>> expectedEpisodeFileBaseNames,
        IReadOnlyDictionary<string, RefreshStateMediaSegmentEntry>? mediaSegmentEntriesByFileBaseName)
    {
        if (mediaSegmentEntriesByFileBaseName == null || mediaSegmentEntriesByFileBaseName.Count == 0)
        {
            return Array.Empty<RefreshStateMediaSegmentEntry>();
        }

        var expectedFileBaseNames = expectedEpisodeFileBaseNames
            .SelectMany(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return mediaSegmentEntriesByFileBaseName
            .Where(x => expectedFileBaseNames.Contains(x.Key) && x.Value.Segments is { Length: > 0 })
            .Select(x => x.Value)
            .OrderBy(x => x.EpisodeNumber)
            .ThenBy(x => x.FileBaseName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static async Task<ManagedFileCheckResult> CheckManagedFilesAsync(
        string seriesRoot,
        IReadOnlyList<RefreshStateManagedFile>? managedFiles,
        CancellationToken cancellationToken)
    {
        if (managedFiles == null || managedFiles.Count == 0)
        {
            return new ManagedFileCheckResult(
                false,
                RefreshSkipReason.ManagedFilesMissingOrInvalid,
                managedFiles?.Count ?? 0,
                0);
        }

        var checkedCount = 0;
        foreach (var file in managedFiles)
        {
            if (string.IsNullOrWhiteSpace(file.RelativePath) ||
                string.IsNullOrWhiteSpace(file.Sha256) ||
                !TryResolveManagedPath(seriesRoot, file.RelativePath, out var fullPath) ||
                !File.Exists(fullPath))
            {
                return new ManagedFileCheckResult(
                    false,
                    RefreshSkipReason.ManagedFilesMissingOrInvalid,
                    managedFiles.Count,
                    checkedCount);
            }

            var currentHash = await ComputeFileSha256HexAsync(fullPath, cancellationToken).ConfigureAwait(false);
            checkedCount++;
            if (!string.Equals(currentHash, file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return new ManagedFileCheckResult(
                    false,
                    RefreshSkipReason.ManagedFileHashMismatch,
                    managedFiles.Count,
                    checkedCount);
            }
        }

        return new ManagedFileCheckResult(true, RefreshSkipReason.Matched, managedFiles.Count, checkedCount);
    }

    private readonly record struct StateReadDecision(RefreshStateFile? State, RefreshSkipReason Reason);

    private readonly record struct ManagedFileCheckResult(
        bool Matches,
        RefreshSkipReason Reason,
        int Expected,
        int Checked);

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
        public RefreshStateMediaSegmentEntry[] MediaSegments { get; set; } = Array.Empty<RefreshStateMediaSegmentEntry>();
        public string KodikCatalogSignature { get; set; } = string.Empty;
        public DateTimeOffset? KodikDeepValidatedAtUtc { get; set; }
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
    public int PreferredQuality { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public int ExpectedAvailableEpisodes { get; init; }
}

public sealed class RefreshStateFingerprintInput
{
    public int GenerationContractVersion { get; init; } = RefreshStateManager.GenerationContractVersion;
    public string Mode { get; init; } = string.Empty;
    public string StreamGatewayBaseUrl { get; init; } = string.Empty;
    public string PreferredTranslationFilter { get; init; } = string.Empty;
    public int PreferredQuality { get; init; }
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

public sealed class RefreshStateKodikCatalogInput
{
    public string IdType { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public int SeriesCount { get; init; }
    public IReadOnlyList<RefreshStateKodikTranslationInput> Translations { get; init; } =
        Array.Empty<RefreshStateKodikTranslationInput>();
}

public sealed class RefreshStateKodikTranslationInput
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int MaxEpisode { get; init; }
    public IReadOnlyList<int> AvailableEpisodes { get; init; } = Array.Empty<int>();
}

public sealed class RefreshStateKodikValidation
{
    public string CatalogSignature { get; init; } = string.Empty;
    public DateTimeOffset DeepValidatedAtUtc { get; init; }
}

public sealed class RefreshStateMediaSegmentEntry
{
    public string FileBaseName { get; init; } = string.Empty;
    public int EpisodeNumber { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string VoiceName { get; init; } = string.Empty;
    public string SourceProvider { get; init; } = string.Empty;
    public string SourceVoiceName { get; init; } = string.Empty;
    public RefreshStateMediaSegment[] Segments { get; init; } = Array.Empty<RefreshStateMediaSegment>();
}

public sealed class RefreshStateMediaSegment
{
    public string Type { get; init; } = string.Empty;
    public long StartTicks { get; init; }
    public long EndTicks { get; init; }
}
