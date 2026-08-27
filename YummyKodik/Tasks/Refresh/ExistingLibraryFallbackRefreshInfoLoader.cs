using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class ExistingLibraryFallbackRefreshInfoLoader
{
    private static readonly Regex ProviderTagRegex = new(
        @"\[(?<kind>shikimoriid|shikimori|kp|imdbid|yaniid)-(?<id>[^\]]+)\]",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    private static readonly Regex EpisodeFileRegex = new(
        @"^S(?<season>\d{2})E(?<episode>\d{2})(?: - (?<suffix>.+))?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    public static IReadOnlyList<string> FindExistingCleanKeys(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var statePath in EnumerateStateFiles(root))
        {
            foreach (var key in ReadCleanKeysFromState(statePath))
            {
                keys.Add(key);
            }
        }

        return keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public YummyRefreshInfo? TryLoad(
        ILogger logger,
        string cleanKey,
        string root,
        string internalBaseUrl)
    {
        var match = TryFindStateMatch(root, cleanKey) ?? TryFindLooseDirectoryMatch(root, cleanKey);
        if (match == null)
        {
            return null;
        }

        var title = ReadTvShowValue(match.SeriesRoot, "title");
        if (string.IsNullOrWhiteSpace(title))
        {
            title = StripProviderTags(Path.GetFileName(match.SeriesRoot));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            title = cleanKey;
        }

        var description = ReadTvShowValue(match.SeriesRoot, "plot");
        var anime = BuildSyntheticAnime(cleanKey, title, description, match);
        var titleInfo = new YummyAnimeTitleInfo(anime, cleanKey, title, title, match.SeasonNumber);
        var files = new SeriesFileInfo(
            match.SeriesRoot,
            match.SeasonDir,
            (internalBaseUrl ?? string.Empty).Trim().TrimEnd('/'));
        var emptyEpisodes = Array.Empty<int>();
        var availability = new EpisodeAvailabilityInfo(emptyEpisodes, 0, emptyEpisodes, emptyEpisodes, emptyEpisodes);

        logger.LogWarning(
            "[YummyKodik] Using existing local library snapshot for '{Title}' because Yummy metadata is unavailable. seriesRoot='{SeriesRoot}' season={Season}",
            title,
            match.SeriesRoot,
            match.SeasonNumber);

        return new YummyRefreshInfo(titleInfo, YummyVideoCatalog.Create(anime), files, availability);
    }

    public static EpisodeGenerationState BuildExistingEpisodeState(string seasonDir, int seasonNumber)
    {
        var state = new EpisodeGenerationState
        {
            ExistingEpisodeTranslationFileBaseNames =
                EpisodeArtifactWriter.BuildExistingEpisodeTranslationFileBaseNames(seasonDir, seasonNumber)
        };

        if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
        {
            return state;
        }

        foreach (var path in Directory.EnumerateFiles(seasonDir, "*.*", SearchOption.TopDirectoryOnly))
        {
            var extension = Path.GetExtension(path);
            if (!string.Equals(extension, RefreshConstants.StrmExtension, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(extension, RefreshConstants.NfoExtension, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileBaseName = Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            if (!TryParseEpisodeFileBaseName(fileBaseName, seasonNumber, out var episodeNumber, out var suffix))
            {
                continue;
            }

            EpisodeArtifactMaintenance.TrackExpectedEpisodeArtifact(
                state.ExpectedEpisodeFileBaseNames,
                episodeNumber,
                fileBaseName);

            if (!string.IsNullOrWhiteSpace(suffix))
            {
                EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(
                    state.ExpectedEpisodeTranslationKeys,
                    episodeNumber,
                    suffix);
            }

            if (string.Equals(extension, RefreshConstants.StrmExtension, StringComparison.OrdinalIgnoreCase))
            {
                state.GeneratedEpisodeNumbers.Add(episodeNumber);
            }
        }

        return state;
    }

    private static ExistingLibraryMatch? TryFindStateMatch(string root, string cleanKey)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        foreach (var statePath in EnumerateStateFiles(root))
        {
            var seriesRoot = Path.GetDirectoryName(statePath);
            if (string.IsNullOrWhiteSpace(seriesRoot))
            {
                continue;
            }

            foreach (var season in ReadSeasonMatchesFromState(statePath, cleanKey))
            {
                var seasonDir = Path.Combine(seriesRoot, season.SeasonKey);
                var match = new ExistingLibraryMatch(seriesRoot, seasonDir, season.SeasonNumber);
                FillIdsFromSeriesRoot(match);
                FillIdsFromExistingStreams(match);
                return match;
            }
        }

        return null;
    }

    private static ExistingLibraryMatch? TryFindLooseDirectoryMatch(string root, string cleanKey)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return null;
        }

        var normalizedKey = (cleanKey ?? string.Empty).Trim();
        if (normalizedKey.Length == 0)
        {
            return null;
        }

        foreach (var seriesRoot in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var match = new ExistingLibraryMatch(seriesRoot, string.Empty, 1);
            FillIdsFromSeriesRoot(match);

            if (!LooseMatchCleanKey(match, normalizedKey, Path.GetFileName(seriesRoot)))
            {
                continue;
            }

            var (seasonDir, seasonNumber) = FindBestSeasonDirectory(seriesRoot);
            match.SeasonDir = seasonDir;
            match.SeasonNumber = seasonNumber;
            FillIdsFromExistingStreams(match);
            return match;
        }

        return null;
    }

    private static bool LooseMatchCleanKey(ExistingLibraryMatch match, string cleanKey, string folderName)
    {
        if (long.TryParse(cleanKey, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericKey) &&
            (match.AnimeId == numericKey ||
             match.ShikimoriId == numericKey ||
             match.KinopoiskId == numericKey))
        {
            return true;
        }

        var normalizedFolder = NormalizeForLooseMatch(StripProviderTags(folderName));
        var normalizedKey = NormalizeForLooseMatch(cleanKey);
        return normalizedKey.Length >= 4 && normalizedFolder.Contains(normalizedKey, StringComparison.Ordinal);
    }

    private static (string SeasonDir, int SeasonNumber) FindBestSeasonDirectory(string seriesRoot)
    {
        var seasons = Directory.Exists(seriesRoot)
            ? Directory.EnumerateDirectories(seriesRoot, "Season *", SearchOption.TopDirectoryOnly)
                .Select(path => (Path: path, Season: ParseSeasonNumber(Path.GetFileName(path))))
                .Where(x => x.Season >= 0)
                .OrderByDescending(x => x.Season)
                .ToArray()
            : Array.Empty<(string Path, int Season)>();

        if (seasons.Length > 0)
        {
            return (seasons[0].Path, seasons[0].Season);
        }

        return (Path.Combine(seriesRoot, "Season 01"), 1);
    }

    private static YummyAnimeResponse BuildSyntheticAnime(
        string cleanKey,
        string title,
        string description,
        ExistingLibraryMatch match)
    {
        YummyRemoteIds? remoteIds = new YummyRemoteIds
        {
            ShikimoriId = match.ShikimoriId > 0 ? match.ShikimoriId : null,
            KpId = match.KinopoiskId > 0 ? match.KinopoiskId : null,
            ImdbId = string.IsNullOrWhiteSpace(match.ImdbId) ? null : match.ImdbId
        };

        if (remoteIds.ShikimoriId == null &&
            remoteIds.KpId == null &&
            string.IsNullOrWhiteSpace(remoteIds.ImdbId))
        {
            remoteIds = null;
        }

        var existingEpisodeCount = CountExistingEpisodes(match.SeasonDir, match.SeasonNumber);
        return new YummyAnimeResponse
        {
            Title = title,
            Description = description,
            AnimeUrl = cleanKey,
            AnimeId = match.AnimeId,
            Season = match.SeasonNumber,
            Episodes = new YummyEpisodesInfo
            {
                Count = existingEpisodeCount,
                Aired = existingEpisodeCount
            },
            Videos = new List<YummyVideoItem>(),
            RemoteIds = remoteIds
        };
    }

    private static int CountExistingEpisodes(string seasonDir, int seasonNumber)
    {
        if (string.IsNullOrWhiteSpace(seasonDir) || !Directory.Exists(seasonDir))
        {
            return 0;
        }

        return Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly)
            .Select(path => Path.GetFileNameWithoutExtension(path) ?? string.Empty)
            .Select(fileBaseName => TryParseEpisodeFileBaseName(fileBaseName, seasonNumber, out var episode, out _) ? episode : 0)
            .Where(episode => episode > 0)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static void FillIdsFromSeriesRoot(ExistingLibraryMatch match)
    {
        var folderName = Path.GetFileName(match.SeriesRoot) ?? string.Empty;
        foreach (Match tag in ProviderTagRegex.Matches(folderName))
        {
            var kind = tag.Groups["kind"].Value;
            var id = tag.Groups["id"].Value.Trim();

            if (kind.Equals("shikimori", StringComparison.OrdinalIgnoreCase) ||
                kind.Equals("shikimoriid", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shikimoriId))
                {
                    match.ShikimoriId = shikimoriId;
                }
            }
            else if (kind.Equals("kp", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kpId))
                {
                    match.KinopoiskId = kpId;
                }
            }
            else if (kind.Equals("imdbid", StringComparison.OrdinalIgnoreCase))
            {
                match.ImdbId = id;
            }
            else if (kind.Equals("yaniid", StringComparison.OrdinalIgnoreCase) &&
                     long.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId))
            {
                match.AnimeId = animeId;
            }
        }
    }

    private static void FillIdsFromExistingStreams(ExistingLibraryMatch match)
    {
        if (string.IsNullOrWhiteSpace(match.SeasonDir) || !Directory.Exists(match.SeasonDir))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(match.SeasonDir, "*.strm", SearchOption.TopDirectoryOnly))
        {
            var line = ReadFirstLine(path);
            if (!YummyKodikStreamUri.TryParseRequest(line, out var request))
            {
                continue;
            }

            if (request.AnimeId > 0 && match.AnimeId <= 0)
            {
                match.AnimeId = request.AnimeId;
            }

            if (request.Provider != YummyStreamProviderKind.Kodik || string.IsNullOrWhiteSpace(request.KodikId))
            {
                continue;
            }

            switch (request.KodikIdType)
            {
                case KodikIdType.Shikimori when match.ShikimoriId <= 0 &&
                                                long.TryParse(request.KodikId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shikimoriId):
                    match.ShikimoriId = shikimoriId;
                    break;
                case KodikIdType.Kinopoisk when match.KinopoiskId <= 0 &&
                                                long.TryParse(request.KodikId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kpId):
                    match.KinopoiskId = kpId;
                    break;
                case KodikIdType.Imdb when string.IsNullOrWhiteSpace(match.ImdbId):
                    match.ImdbId = request.KodikId.Trim();
                    break;
            }
        }
    }

    private static IEnumerable<string> EnumerateStateFiles(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, RefreshStateManager.StateFileName, SearchOption.AllDirectories)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<string> ReadCleanKeysFromState(string statePath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("seasons", out var seasons) ||
                seasons.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<string>();
            }

            var keys = new List<string>();
            foreach (var season in seasons.EnumerateObject())
            {
                if (season.Value.TryGetProperty("cleanKey", out var cleanKeyElement) &&
                    cleanKeyElement.ValueKind == JsonValueKind.String)
                {
                    var cleanKey = (cleanKeyElement.GetString() ?? string.Empty).Trim();
                    if (cleanKey.Length > 0)
                    {
                        keys.Add(cleanKey);
                    }
                }
            }

            return keys;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<ExistingStateSeason> ReadSeasonMatchesFromState(string statePath, string cleanKey)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(statePath));
            if (!document.RootElement.TryGetProperty("seasons", out var seasons) ||
                seasons.ValueKind != JsonValueKind.Object)
            {
                return Array.Empty<ExistingStateSeason>();
            }

            var matches = new List<ExistingStateSeason>();
            foreach (var season in seasons.EnumerateObject())
            {
                if (!season.Value.TryGetProperty("cleanKey", out var cleanKeyElement) ||
                    !string.Equals(cleanKeyElement.GetString()?.Trim(), cleanKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var seasonNumber = TryGetIntProperty(season.Value, "seasonNumber", out var parsedSeason)
                    ? parsedSeason
                    : ParseSeasonNumber(season.Name);
                if (seasonNumber < 0)
                {
                    seasonNumber = 1;
                }

                matches.Add(new ExistingStateSeason(season.Name, seasonNumber));
            }

            return matches;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<ExistingStateSeason>();
        }
    }

    private static string ReadTvShowValue(string seriesRoot, string elementName)
    {
        try
        {
            var path = Path.Combine(seriesRoot, "tvshow.nfo");
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var document = XDocument.Load(path);
            return document.Root?.Element(elementName)?.Value?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return string.Empty;
        }
    }

    private static string ReadFirstLine(string path)
    {
        try
        {
            return File.ReadLines(path).FirstOrDefault()?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool TryParseEpisodeFileBaseName(
        string fileBaseName,
        int seasonNumber,
        out int episodeNumber,
        out string suffix)
    {
        episodeNumber = 0;
        suffix = string.Empty;

        var match = EpisodeFileRegex.Match(fileBaseName ?? string.Empty);
        if (!match.Success ||
            !int.TryParse(match.Groups["season"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeason) ||
            parsedSeason != Math.Max(1, seasonNumber) ||
            !int.TryParse(match.Groups["episode"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out episodeNumber) ||
            episodeNumber <= 0)
        {
            return false;
        }

        suffix = match.Groups["suffix"].Success ? match.Groups["suffix"].Value.Trim() : string.Empty;
        return true;
    }

    private static bool TryGetIntProperty(JsonElement element, string propertyName, out int value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.Number &&
               property.TryGetInt32(out value);
    }

    private static int ParseSeasonNumber(string value)
    {
        var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seasonNumber)
            ? seasonNumber
            : -1;
    }

    private static string StripProviderTags(string value)
    {
        return ProviderTagRegex.Replace(value ?? string.Empty, string.Empty).Trim();
    }

    private static string NormalizeForLooseMatch(string value)
    {
        return new string((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }

    private sealed class ExistingLibraryMatch
    {
        public ExistingLibraryMatch(string seriesRoot, string seasonDir, int seasonNumber)
        {
            SeriesRoot = seriesRoot;
            SeasonDir = seasonDir;
            SeasonNumber = seasonNumber;
        }

        public string SeriesRoot { get; }
        public string SeasonDir { get; set; }
        public int SeasonNumber { get; set; }
        public long AnimeId { get; set; }
        public long ShikimoriId { get; set; }
        public long KinopoiskId { get; set; }
        public string ImdbId { get; set; } = string.Empty;
    }

    private sealed record ExistingStateSeason(string SeasonKey, int SeasonNumber);
}
