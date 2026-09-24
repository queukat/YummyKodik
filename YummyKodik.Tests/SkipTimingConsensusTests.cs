using YummyKodik.Yummy;

internal static class SkipTimingConsensusTests
{
    public static void Selection()
    {
        Check("two agreeing voices beat an earlier outlier", "B", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("C", 61, 90));
        Check("same voice's own markers stay authoritative", "A", "A",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("C", 61, 90));
        Check("tie retains historical fallback", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90));
        Check("single candidate remains usable", "A", "Missing", Entry("A", 15, 104));
        Check("provider duplicates cannot manufacture majority", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("B", 60, 90, 2));
        Check("normalized voice aliases are one vote", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("Озвучка B", 60, 90, 2));
        Check("matching starts alone are insufficient", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("C", 60, 130));
        Check("conflicting provider copies abstain", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("B", 110, 90, 2), Entry("C", 60, 90));
        Check("unrelated episode does not vote", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), Entry("C", 60, 90, episode: "6"));
        Check("existing compatible fallback wins over near equivalent majority member", "A", "Missing",
            Entry("A", 60, 90), Entry("B", 61, 90), Entry("C", 180, 90));
        var differingOutro = Entry("C", 61, 90);
        differingOutro.Skips!.Ending = new YummySkipSegment { Time = 900, Length = 30 };
        Check("do not combine timings from different cuts", "A", "Missing",
            Entry("A", 15, 104), Entry("B", 60, 90), differingOutro);
        // Captured Eternal Will S4E5 CVH candidates: no majority, so preserve transfer.
        Check("reported episode retains configured ambiguity fallback", "AnimeVost", "AniStar",
            Entry("AnimeVost", 15, 104), Entry("HORIZON", 0, 89));
    }

    private static void Check(string name, string expected, string requested, params YummyVideoItem[] videos)
    {
        var catalog = YummyVideoCatalog.Create(new YummyAnimeResponse { AnimeId = 24253, Videos = videos.ToList() });
        var result = catalog.FindPreferredEntryWithSkipsAcrossProviders(5, requested,
            new[] { YummyVideoProviderKind.Cvh, YummyVideoProviderKind.Alloha });
        if (result?.DisplayVoiceName != expected)
        {
            throw new InvalidOperationException($"{name}: expected {expected}, got {result?.DisplayVoiceName ?? "null"}");
        }
    }

    private static YummyVideoItem Entry(string voice, int start, int length, int provider = 3, string episode = "5") => new()
    {
        Number = episode,
        Data = new YummyVideoData { PlayerId = provider, Dubbing = voice },
        IframeUrl = provider == 2
            ? "https://alloha.yani.tv/?token_movie=movie&translation=215&season=1&episode=5&token=request"
            : "https://play.example/player?anime_id=24253&episode=5&dubbing_code=158&dubbing=" + Uri.EscapeDataString(voice),
        Skips = new YummyVideoSkips { Opening = new YummySkipSegment { Time = start, Length = length } }
    };
}
