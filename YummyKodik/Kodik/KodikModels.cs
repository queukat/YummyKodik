// File: Kodik/KodikModels.cs

using System;
using System.Collections.Generic;

namespace YummyKodik.Kodik
{
    public enum KodikIdType
    {
        Shikimori,
        Kinopoisk,
        Imdb
    }

    public sealed class KodikTranslation
    {
        /// <summary>
        /// Translation identifier passed back into GetEpisodeLinkAsync.
        /// Kodik may expose it via the option 'value' or 'data-id'.
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Raw translation type, usually "voice" or "subtitles".
        /// </summary>
        public string Type { get; init; } = string.Empty;

        /// <summary>
        /// Human readable translation name.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Max available episode number for this translation (0 if unknown).
        /// </summary>
        public int MaxEpisode { get; init; }
    }

    public sealed class KodikAnimeInfo
    {
        public KodikAnimeInfo(int seriesCount, IReadOnlyList<KodikTranslation> translations)
        {
            SeriesCount = seriesCount;
            Translations = translations ?? Array.Empty<KodikTranslation>();
        }

        /// <summary>
        /// Number of episodes, zero for movies.
        /// </summary>
        public int SeriesCount { get; }

        /// <summary>
        /// Available translations.
        /// </summary>
        public IReadOnlyList<KodikTranslation> Translations { get; }
    }

    public sealed class KodikLinkInfo
    {
        public KodikLinkInfo(string basePath, int maxQuality)
        {
            BasePath = basePath;
            MaxQuality = maxQuality;
        }

        /// <summary>
        /// Base url without protocol and without file name.
        /// Example: "//cloud.kodik-storage.com/.../.../"
        /// </summary>
        public string BasePath { get; }

        /// <summary>
        /// Max available quality, for example 720.
        /// </summary>
        public int MaxQuality { get; }
    }

    public sealed class KodikSkipRange
    {
        public KodikSkipRange(TimeSpan start, TimeSpan end)
        {
            Start = start;
            End = end;
        }

        public TimeSpan Start { get; }

        public TimeSpan End { get; }
    }

    public sealed class KodikEpisodeTimings
    {
        public KodikEpisodeTimings(KodikSkipRange? intro, KodikSkipRange? outro)
        {
            Intro = intro;
            Outro = outro;
        }

        public KodikSkipRange? Intro { get; }

        public KodikSkipRange? Outro { get; }

        public bool HasAny => Intro != null || Outro != null;
    }
}
