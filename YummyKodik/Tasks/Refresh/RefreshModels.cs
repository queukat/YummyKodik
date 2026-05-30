using System.Net.Http;
using Microsoft.Extensions.Logging;
using YummyKodik.Kodik;
using YummyKodik.Shikimori;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed record RefreshClients(
    YummyClient Yummy,
    ShikimoriGraphQlClient Shikimori,
    HttpClient YummyHttp,
    Lazy<Task<RefreshKodikClients>> KodikClients);

internal sealed record RefreshKodikClients(
    KodikClient Kodik,
    HttpClient KodikHttp,
    string KodikToken);

internal sealed record YummyAnimeTitleInfo(
    YummyAnimeResponse Anime,
    string CleanKey,
    string RawTitle,
    string Title,
    int SeasonNumber);

internal sealed record SeriesFileInfo(
    string SeriesRoot,
    string SeasonDir,
    string BaseUrl);

internal sealed record EpisodeAvailabilityInfo(
    IReadOnlyList<int> KnownSupportedEpisodes,
    int ExpectedAvailableEpisodes,
    int[] AllohaSupportedEpisodes,
    int[] CvhSupportedEpisodes,
    int[] YummySupportedEpisodes);

internal sealed record YummyRefreshInfo(
    YummyAnimeTitleInfo TitleInfo,
    YummyVideoCatalog VideoCatalog,
    SeriesFileInfo Files,
    EpisodeAvailabilityInfo Availability);

internal sealed class EpisodeGenerationState
{
    public HashSet<int> GeneratedEpisodeNumbers { get; } = new();

    public Dictionary<int, HashSet<string>> ExpectedEpisodeFileBaseNames { get; } = new();

    public Dictionary<int, HashSet<string>> ExpectedEpisodeTranslationKeys { get; } = new();

    public Dictionary<int, Dictionary<string, string>> ExistingEpisodeTranslationFileBaseNames { get; set; } = new();
}

internal sealed record KodikLookupResult(
    KodikAnimeInfo Info,
    KodikIdType IdType,
    string Id);

internal sealed record YummyEpisodeGenerationContext(
    ILogger Logger,
    YummyRefreshInfo Refresh,
    EpisodeGenerationState State,
    string SeasonDir,
    bool CreateStrmPerVoiceTranslation,
    string? PreferredTranslationFilter,
    RefreshPerformanceMetrics? Perf);

internal sealed record KodikEpisodeGenerationContext(
    ILogger Logger,
    YummyRefreshInfo Refresh,
    KodikLookupResult Lookup,
    KodikClient Kodik,
    EpisodeGenerationState State,
    string SeasonDir,
    bool CreateStrmPerVoiceTranslation,
    RefreshPerformanceMetrics? Perf);

internal sealed record EpisodeArtifactWriteContext(
    ILogger Logger,
    string SeasonDir,
    int SeasonNumber,
    string Title,
    string? Description,
    RefreshPerformanceMetrics? Perf);

internal readonly record struct EpisodeArtifactGenerationResult(int EpisodesWritten, int FilesWritten);

internal enum TextWriteOutcome
{
    Created,
    Updated,
    Unchanged
}
