using YummyKodik.Kodik;

namespace YummyKodik.Util;

public sealed class YummyStreamRequest
{
    public YummyStreamProviderKind Provider { get; init; }
    public KodikIdType KodikIdType { get; init; }
    public string KodikId { get; init; } = string.Empty;
    public long AnimeId { get; init; }
    public int? Episode { get; init; }
    public string VoiceName { get; init; } = string.Empty;
    public string AllohaMovieToken { get; init; } = string.Empty;
    public string AllohaRequestToken { get; init; } = string.Empty;
    public int AllohaTranslationId { get; init; }
    public int AllohaSeasonNumber { get; init; }
    public string AllohaHidden { get; init; } = string.Empty;
    public string AllohaRefererUrl { get; init; } = string.Empty;
}
