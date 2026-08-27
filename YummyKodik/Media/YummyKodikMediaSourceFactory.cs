using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace YummyKodik.Media;

internal static class YummyKodikMediaSourceFactory
{
    public static MediaSourceInfo Build(MediaSourceBuildOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return new MediaSourceInfo
        {
            Id = $"{options.ItemId}_ep{options.Episode}_{options.Suffix}",
            Path = options.Url,
            Protocol = MediaProtocol.Http,
            EncoderPath = options.Url,
            EncoderProtocol = MediaProtocol.Http,
            Container = options.Container,
            IsRemote = true,
            HasSegments = true,
            RequiresOpening = false,
            IsInfiniteStream = false,
            SupportsDirectPlay = options.SupportsDirectPlay,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = options.SupportsProbing,
            VideoType = VideoType.VideoFile,
            RunTimeTicks = options.RunTimeTicks,
            Name = options.Name,
            RequiredHttpHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        };
    }
}

internal sealed class MediaSourceBuildOptions
{
    public string ItemId { get; init; } = string.Empty;

    public int Episode { get; init; }

    public string Suffix { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Container { get; init; } = "mp4";

    public bool SupportsDirectPlay { get; init; } = true;

    public long? RunTimeTicks { get; init; }

    public bool SupportsProbing { get; init; } = true;
}
