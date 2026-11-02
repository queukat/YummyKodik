using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Cvh;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Shikimori;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Util;
using YummyKodik.Web;
using YummyKodik.Yummy;
partial class Program
{
    static void LimitToExpectedAvailableEpisodes_CapsProviderCoverageToAiredCount()
    {
        var anime = new YummyAnimeResponse
        {
            Episodes = new YummyEpisodesInfo
            {
                Aired = 1,
                Count = 12
            },
            Videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "1"
                }
            }
        };

        var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, TestData.EpisodeOne);

        AssertEqual(1, limited.Length, "Provider coverage should be capped to the aired episode count.");
        AssertEqual(1, limited[0], "Only the first aired episode should remain available.");
    }

    static void GetExpectedAvailableEpisodeCount_UsesKnownVideoEpisodesWhenAiredLags()
    {
        var anime = new YummyAnimeResponse
        {
            Episodes = new YummyEpisodesInfo
            {
                Aired = 1,
                Count = 0
            },
            Videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "1"
                },
                new()
                {
                    Number = "2"
                }
            }
        };

        var expected = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime);

        AssertEqual(2, expected, "Explicit video episode numbers should override stale aired metadata when providers are already ahead.");
    }

    static void LimitToExpectedAvailableEpisodes_PreservesExplicitProviderCoverageWhenAiredLags()
    {
        var anime = new YummyAnimeResponse
        {
            Episodes = new YummyEpisodesInfo
            {
                Aired = 1,
                Count = 0
            },
            Videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "1"
                }
            }
        };

        var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, TestData.EpisodesOneTwo);

        AssertEqual(2, limited.Length, "Provider-reported episode coverage should be preserved when aired lags behind live data.");
        AssertEqual(1, limited[0], "Episode ordering should stay normalized.");
        AssertEqual(2, limited[1], "The second explicitly supported episode should not be cut off by stale aired metadata.");
    }

    static void LimitToExpectedAvailableEpisodes_KeepsKnownEpisodesWhenAiredCountIsUnknown()
    {
        var anime = new YummyAnimeResponse
        {
            Episodes = new YummyEpisodesInfo
            {
                Aired = 0,
                Count = 0
            },
            Videos = new List<YummyVideoItem>
            {
                new()
                {
                    Number = "1"
                }
            }
        };

        var limited = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(anime, TestData.UnorderedEpisodesWithDuplicate);

        AssertEqual(3, limited.Length, "When Yummy does not know aired count, existing provider episodes should be preserved.");
        AssertEqual(1, limited[0], "Episodes should be normalized and sorted.");
        AssertEqual(2, limited[1], "Duplicate provider episodes should be removed.");
        AssertEqual(3, limited[2], "Episode ordering should remain stable after normalization.");
    }

    static void NfoBuilder_BuildSeriesNfo_ProducesXml()
    {
        var xml = NfoBuilder.BuildSeriesNfo("Test series", "Plot");

        AssertTrue(!string.IsNullOrWhiteSpace(xml), "Series NFO should not be empty.");
        AssertTrue(xml.Contains("<tvshow>", StringComparison.Ordinal), "Series NFO should contain the tvshow root element.");
        AssertTrue(xml.Contains("<title>Test series</title>", StringComparison.Ordinal), "Series NFO should contain the title.");
    }

    static void NfoBuilder_BuildEpisodeNfo_ProducesXml()
    {
        var xml = NfoBuilder.BuildEpisodeNfo(episodeNumber: 1, season: 2, seriesTitle: "Test series", description: "Plot");

        AssertTrue(!string.IsNullOrWhiteSpace(xml), "Episode NFO should not be empty.");
        AssertTrue(xml.Contains("<episodedetails>", StringComparison.Ordinal), "Episode NFO should contain the episodedetails root element.");
        AssertTrue(xml.Contains("<season>2</season>", StringComparison.Ordinal), "Episode NFO should contain the requested season number.");
    }

    static void NfoBuilder_BuildEpisodeNfo_WritesExactRuntime()
    {
        const int durationSeconds = 1425;
        var xml = NfoBuilder.BuildEpisodeNfo(
            episodeNumber: 1,
            season: 2,
            seriesTitle: "Test series",
            description: "Plot",
            durationSeconds);
        var document = XDocument.Parse(xml);

        AssertEqual("24", document.Root!.Element("runtime")!.Value, "Episode NFO should include Jellyfin's minute-based runtime.");
        AssertEqual(
            durationSeconds.ToString(),
            document.Descendants("durationinseconds").Single().Value,
            "Episode NFO should also keep the exact duration in Jellyfin streamdetails.");
        AssertTrue(
            NfoBuilder.TryGetEpisodeRuntimeSeconds(xml, out var parsedDuration),
            "Generated episode NFO runtime should be reusable without another provider request.");
        AssertEqual(durationSeconds, parsedDuration, "Generated episode NFO should retain exact runtime seconds.");
    }

    static void NfoBuilder_EnsureEpisodeRuntime_BackfillsLegacyNfo()
    {
        const int durationSeconds = 1425;
        var legacyXml = NfoBuilder.BuildEpisodeNfo(1, 1, "Test series", "Plot");
        var enrichedXml = NfoBuilder.EnsureEpisodeRuntime(legacyXml, durationSeconds);
        var document = XDocument.Parse(enrichedXml);

        AssertEqual("24", document.Root!.Element("runtime")!.Value, "Runtime backfill should add the minute-based Jellyfin field.");
        AssertEqual(
            durationSeconds.ToString(),
            document.Descendants("durationinseconds").Single().Value,
            "Runtime backfill should add the exact streamdetails duration.");
        AssertEqual(
            enrichedXml,
            NfoBuilder.EnsureEpisodeRuntime(enrichedXml, durationSeconds),
            "Runtime backfill should be idempotent once the NFO already has the desired values.");
        AssertTrue(
            NfoBuilder.TryGetEpisodeRuntimeSeconds(enrichedXml, out var parsedDuration),
            "Backfilled episode runtime should be reusable on later refresh runs.");
        AssertEqual(durationSeconds, parsedDuration, "Backfilled episode NFO should retain exact runtime seconds.");
    }

    static void EpisodeRuntimeBackfillService_BorrowsSiblingRuntime()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var seasonDir = Path.Combine(tempRoot, "Series", "Season 01");
        var knownPath = Path.Combine(seasonDir, "S01E01 - Known.nfo");
        var missingPath = Path.Combine(seasonDir, "S01E01 - Missing.nfo");
        var otherEpisodePath = Path.Combine(seasonDir, "S01E02 - Missing.nfo");

        try
        {
            Directory.CreateDirectory(seasonDir);
            File.WriteAllText(
                knownPath,
                NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot", durationSeconds: 1425));
            File.WriteAllText(
                missingPath,
                NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot"));
            File.WriteAllText(
                otherEpisodePath,
                NfoBuilder.BuildEpisodeNfo(2, 1, "Test", "Plot"));

            var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updated = EpisodeRuntimeBackfillService
                .BackfillMissingAsync(tempRoot, NullLogger.Instance, CancellationToken.None, updatedPaths)
                .GetAwaiter()
                .GetResult();

            AssertEqual(1, updated, "Only the sibling version of the same episode should be backfilled.");
            AssertTrue(
                updatedPaths.SetEquals(new[] { Path.GetFullPath(missingPath) }),
                "Runtime backfill should report only the NFO path it actually rewrote.");
            var enrichedXml = File.ReadAllText(missingPath);
            AssertTrue(
                NfoBuilder.TryGetExactEpisodeRuntimeSeconds(enrichedXml, out var durationSeconds),
                "Sibling runtime backfill should persist an exact duration.");
            AssertEqual(1425, durationSeconds, "Sibling runtime backfill should reuse the known episode duration.");
            AssertFalse(
                NfoBuilder.TryGetEpisodeRuntimeSeconds(File.ReadAllText(otherEpisodePath), out _),
                "A runtime must not leak into a different episode.");

            var secondPass = EpisodeRuntimeBackfillService
                .BackfillMissingAsync(tempRoot, NullLogger.Instance, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(0, secondPass, "Sibling runtime backfill should be idempotent.");
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    static void RefreshState_RuntimeBackfillReconcilesManagedNfoHash()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var seriesRoot = Path.Combine(tempRoot, "Series");
        var seasonDir = Path.Combine(seriesRoot, "Season 01");
        const string knownBaseName = "S01E01 - Known";
        const string missingBaseName = "S01E01 - Missing";
        var missingNfoPath = Path.Combine(seasonDir, missingBaseName + ".nfo");

        try
        {
            Directory.CreateDirectory(seasonDir);
            File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Test", "Plot"));
            File.WriteAllText(Path.Combine(seasonDir, knownBaseName + ".strm"), "https://jellyfin.test/known");
            File.WriteAllText(Path.Combine(seasonDir, missingBaseName + ".strm"), "https://jellyfin.test/missing");
            File.WriteAllText(
                Path.Combine(seasonDir, knownBaseName + ".nfo"),
                NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot", durationSeconds: 1425));
            File.WriteAllText(missingNfoPath, NfoBuilder.BuildEpisodeNfo(1, 1, "Test", "Plot"));

            var input = BuildRefreshStateSeasonInput();
            var expectedFiles = new Dictionary<int, HashSet<string>>
            {
                [1] = new(StringComparer.OrdinalIgnoreCase)
                {
                    knownBaseName,
                    missingBaseName
                }
            };
            var written = RefreshStateManager.WriteSeasonStateAsync(
                    seriesRoot,
                    input,
                    expectedFiles,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(written, "The fixture state should be written before runtime backfill.");
            AssertTrue(
                RefreshStateManager.CanSkipSingleFileRefreshAsync(seriesRoot, input, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
                "The fixture should initially match its managed hashes.");

            var updatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var updated = EpisodeRuntimeBackfillService
                .BackfillMissingAsync(tempRoot, NullLogger.Instance, CancellationToken.None, updatedPaths)
                .GetAwaiter()
                .GetResult();
            AssertEqual(1, updated, "Runtime backfill should rewrite the missing sibling NFO.");
            AssertFalse(
                RefreshStateManager.CanSkipSingleFileRefreshAsync(seriesRoot, input, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
                "A runtime rewrite must make the old managed hash stale before reconciliation.");

            var reconciled = RefreshStateManager
                .ReconcileManagedFileHashesAsync(tempRoot, updatedPaths, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(1, reconciled, "Only the NFO rewritten by runtime backfill should receive a new managed hash.");
            AssertTrue(
                RefreshStateManager.CanSkipSingleFileRefreshAsync(seriesRoot, input, CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
                "Reconciled runtime NFO hashes should preserve the safe refresh skip path.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void NeedsKodikSupplement_SkipsAnnouncementWithoutAvailableEpisodes()
    {
        var anime = new YummyAnimeResponse
        {
            Title = "Анонс сериала",
            AnimeStatus = new YummyAnimeStatus
            {
                Alias = "anons",
                Title = "Анонс"
            },
            Episodes = new YummyEpisodesInfo
            {
                Count = 12,
                Aired = 0
            },
            Videos = new List<YummyVideoItem>()
        };

        var result = YummyEpisodeAvailability.NeedsKodikSupplement(anime, Array.Empty<int>());
        AssertFalse(result, "Announcements without aired/provider episodes should skip Kodik probing.");
    }

    static void NeedsKodikSupplement_UsesKodikWhenAiredEpisodesExist()
    {
        var anime = new YummyAnimeResponse
        {
            Title = "Уже выходит",
            Episodes = new YummyEpisodesInfo
            {
                Count = 12,
                Aired = 3
            },
            Videos = new List<YummyVideoItem>()
        };

        var result = YummyEpisodeAvailability.NeedsKodikSupplement(anime, Array.Empty<int>());
        AssertTrue(result, "Titles with aired episodes should still probe Kodik when Yummy has not generated any files.");
    }

    static void EpisodeArtifactMaintenance_NormalizesEquivalentTranslationVariants()
    {
        var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();
        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria");

        AssertTrue(
            EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria.TV"),
            "Equivalent translation suffixes should collapse to the same key so Yummy can replace stale Kodik files.");
        AssertTrue(
            EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLiberty (AniLibria)"),
            "Parenthetical provider aliases should collapse to the same key so equivalent voices keep stable files.");
    }

    static void RefreshFileWriter_ReplacesReadOnlyExistingArtifact()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var seasonDir = Path.Combine(tempRoot, "Season 01");
        var strmPath = Path.Combine(seasonDir, "S01E01.strm");

        try
        {
            Directory.CreateDirectory(seasonDir);
            File.WriteAllText(strmPath, "old" + Environment.NewLine);
            File.SetAttributes(strmPath, File.GetAttributes(strmPath) | FileAttributes.ReadOnly);

            InvokeRefreshFileWriterWriteTextAtomically(strmPath, "new" + Environment.NewLine);

            AssertEqual("new" + Environment.NewLine, File.ReadAllText(strmPath), "Read-only generated artifacts should still be atomically replaced.");
            AssertTrue(
                (File.GetAttributes(strmPath) & FileAttributes.ReadOnly) != 0,
                "Replacing a read-only artifact should restore the original read-only attribute.");
            AssertFalse(
                Directory.EnumerateFiles(seasonDir, "*.tmp.*", SearchOption.TopDirectoryOnly).Any(),
                "Atomic replacement should not leave temp files behind.");
        }
        finally
        {
            if (File.Exists(strmPath))
            {
                File.SetAttributes(strmPath, FileAttributes.Normal);
            }

            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_AllowsSingleFileSkipWhenFingerprintAndFilesMatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot);
            var written = RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(written, "Complete single-file artifacts should produce refresh state.");

            var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(canSkip, "Matching fingerprint and complete managed files should allow the single-file pre-Kodik skip.");

            var stateJson = File.ReadAllText(Path.Combine(fixture.SeriesRoot, RefreshStateManager.StateFileName));
            AssertFalse(stateJson.Contains("secret-request", StringComparison.Ordinal), "Refresh state must not store raw secret-bearing STRM URLs.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_InvalidatesSkipWhenInputsChange()
    {
        var baseline = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput());
        AssertTrue(
            typeof(RefreshStateFingerprintInput).GetProperty("ServerBaseUrl") is null,
            "Refresh-state fingerprint inputs must not retain the client-facing ServerBaseUrl.");

        var sameGatewayWithTrailingSlash = RefreshStateManager.BuildFingerprint(
            BuildRefreshStateFingerprintInput(streamGatewayBaseUrl: "http://127.0.0.1:8096/"));
        AssertEqual(
            baseline,
            sameGatewayWithTrailingSlash,
            "Fingerprint should be independent of public server topology and normalize the internal gateway trailing slash.");

        var cases = new (string Name, RefreshStateFingerprintInput Input)[]
        {
            ("StreamGatewayBaseUrl", BuildRefreshStateFingerprintInput(streamGatewayBaseUrl: "http://127.0.0.1:18096")),
            ("PreferredTranslationFilter", BuildRefreshStateFingerprintInput(preferredTranslationFilter: "dreamcast")),
            ("PreferredQuality", BuildRefreshStateFingerprintInput(preferredQuality: 720)),
            ("Mode", BuildRefreshStateFingerprintInput(mode: "per-voice")),
            ("ProviderCoverage", BuildRefreshStateFingerprintInput(providerCoverage: new[] { "ep:1:preferred:Cvh" })),
            ("SeasonTitleIdentity", BuildRefreshStateFingerprintInput(seriesTitle: "Frieren Season Two", seasonNumber: 2)),
            ("ExpectedEpisodeCount", BuildRefreshStateFingerprintInput(expectedAvailableEpisodes: 2))
        };

        foreach (var testCase in cases)
        {
            var fingerprint = RefreshStateManager.BuildFingerprint(testCase.Input);
            AssertFalse(
                string.Equals(baseline, fingerprint, StringComparison.Ordinal),
                $"Fingerprint should change when {testCase.Name} changes.");
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot, fingerprint: baseline);
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var changedFingerprintInput = BuildRefreshStateSeasonInput(fingerprint: cases[0].Input is { } changedInput
                ? RefreshStateManager.BuildFingerprint(changedInput)
                : string.Empty);
            var changedFingerprintCanSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    changedFingerprintInput,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(changedFingerprintCanSkip, "Changed fingerprint should disable state pre-skip.");

            var changedExpectedCountInput = BuildRefreshStateSeasonInput(fingerprint: baseline, expectedAvailableEpisodes: 2);
            var changedExpectedCountCanSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    changedExpectedCountInput,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(changedExpectedCountCanSkip, "Changed expected episode count should disable state pre-skip.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_ExtraEpisodeArtifactsDisableSkip()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot);
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
            File.WriteAllText(Path.Combine(seasonDir, "S01E02.strm"), "stale");
            File.WriteAllText(Path.Combine(seasonDir, "S01E02.nfo"), "<episodedetails />");

            var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(canSkip, "Extra episode-shaped artifacts should disable state pre-skip so full cleanup still runs.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_ZeroExpectedEpisodesWithStaleArtifactsDisablesSkip()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fingerprint = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(
                expectedAvailableEpisodes: 0,
                providerCoverage: Array.Empty<string>()));
            var fixture = CreateRefreshStateFixture(
                tempRoot,
                expectedAvailableEpisodes: 0,
                fingerprint: fingerprint);

            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
            var staleStrmPath = Path.Combine(seasonDir, "S01E01.strm");
            var staleNfoPath = Path.Combine(seasonDir, "S01E01.nfo");
            File.WriteAllText(staleStrmPath, "stale");
            File.WriteAllText(staleNfoPath, "<episodedetails />");

            var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(canSkip, "Zero-episode state must not skip when stale episode-shaped artifacts are present.");

            EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
                NullLogger.Instance,
                seasonDir,
                fixture.Input.SeasonNumber,
                fixture.ExpectedEpisodeFileBaseNames,
                maxAvailableEpisodeNumber: 0);

            AssertFalse(File.Exists(staleStrmPath), "Zero-episode cleanup should remove stale STRM artifacts.");
            AssertFalse(File.Exists(staleNfoPath), "Zero-episode cleanup should remove stale NFO artifacts.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_PreservesMultipleSeasonsInSeriesRoot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var seasonOne = CreateRefreshStateFixture(tempRoot, seasonNumber: 1);
            var seasonTwo = CreateRefreshStateFixture(
                tempRoot,
                seasonNumber: 2,
                fingerprint: RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: 2)));

            RefreshStateManager.WriteSeasonStateAsync(
                    seasonOne.SeriesRoot,
                    seasonOne.Input,
                    seasonOne.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            RefreshStateManager.WriteSeasonStateAsync(
                    seasonTwo.SeriesRoot,
                    seasonTwo.Input,
                    seasonTwo.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(seasonOne.SeriesRoot, RefreshStateManager.StateFileName)));
            var seasons = document.RootElement.GetProperty("seasons");
            AssertTrue(seasons.TryGetProperty("Season 01", out _), "State file should keep the first season entry.");
            AssertTrue(seasons.TryGetProperty("Season 02", out _), "State file should add the second season entry without overwriting season one.");

            AssertTrue(
                RefreshStateManager.CanSkipSingleFileRefreshAsync(seasonOne.SeriesRoot, seasonOne.Input, CancellationToken.None).GetAwaiter().GetResult(),
                "Season one state should remain usable after writing season two.");
            AssertTrue(
                RefreshStateManager.CanSkipSingleFileRefreshAsync(seasonTwo.SeriesRoot, seasonTwo.Input, CancellationToken.None).GetAwaiter().GetResult(),
                "Season two state should be usable from the shared state file.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_LegacyGenerationContractDisablesSkip()
    {
        AssertTrue(
            RefreshStateManager.GenerationContractVersion > 1,
            "Gateway STRM generation must bump the refresh-state contract version.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot);
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var statePath = Path.Combine(fixture.SeriesRoot, RefreshStateManager.StateFileName);
            var currentVersion = RefreshStateManager.GenerationContractVersion;
            var currentJson = File.ReadAllText(statePath);
            var legacyJson = currentJson.Replace(
                $"\"generationContractVersion\": {currentVersion}",
                $"\"generationContractVersion\": {currentVersion - 1}",
                StringComparison.Ordinal);
            AssertFalse(
                string.Equals(currentJson, legacyJson, StringComparison.Ordinal),
                "Test fixture should contain the generated contract version before simulating a legacy state file.");
            File.WriteAllText(statePath, legacyJson);

            var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(canSkip, "A v1 refresh-state file must not skip refresh after the gateway STRM contract changes.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_WritesAndReadsMediaSegmentsForEpisodeFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
            var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
            var voiceFileBaseName = "S01E01 - AniLibria";
            File.WriteAllText(Path.Combine(seasonDir, voiceFileBaseName + ".strm"), "https://jellyfin.test/YummyKodik/cvh/1/1");
            File.WriteAllText(
                Path.Combine(seasonDir, voiceFileBaseName + ".nfo"),
                NfoBuilder.BuildEpisodeNfo(1, 1, "Frieren", "Plot"));
            fixture.ExpectedEpisodeFileBaseNames[1].Add(voiceFileBaseName);

            var mediaSegments = new Dictionary<string, RefreshStateMediaSegmentEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [voiceFileBaseName] = new RefreshStateMediaSegmentEntry
                {
                    FileBaseName = voiceFileBaseName,
                    SkipTimingSelectionVersion = YummyVideoCatalog.SkipTimingSelectionVersion,
                    EpisodeNumber = 1,
                    Provider = "Cvh",
                    VoiceName = "AniLibria",
                    SourceProvider = "Cvh",
                    SourceVoiceName = "Komnata Didi",
                    Segments = new[]
                    {
                        new RefreshStateMediaSegment
                        {
                            Type = "Intro",
                            StartTicks = TimeSpan.FromSeconds(44).Ticks,
                            EndTicks = TimeSpan.FromSeconds(133).Ticks
                        },
                        new RefreshStateMediaSegment
                        {
                            Type = "Outro",
                            StartTicks = TimeSpan.FromSeconds(1482).Ticks,
                            EndTicks = TimeSpan.FromSeconds(1496).Ticks
                        }
                    }
                }
            };

            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    mediaSegments,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var entry = RefreshStateManager.TryReadMediaSegmentEntryForPathAsync(
                    Path.Combine(seasonDir, voiceFileBaseName + ".strm"),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            AssertTrue(entry != null, "Media segment state should be readable by episode file path.");
            AssertEqual("AniLibria", entry!.VoiceName, "Requested voice should be stored with local media segments.");
            AssertEqual("Komnata Didi", entry.SourceVoiceName, "Source voice should record where fallback timings came from.");
            AssertEqual(2, entry.Segments.Length, "Intro and outro segments should be stored.");
            AssertEqual(TimeSpan.FromSeconds(44).Ticks, entry.Segments[0].StartTicks, "Intro start ticks should round-trip.");
            var statePath = Path.Combine(fixture.SeriesRoot, ".yummykodik.refresh-state.json");
            var oldState = JsonNode.Parse(File.ReadAllText(statePath))!;
            foreach (var node in oldState["seasons"]!.AsObject().SelectMany(season => season.Value!["mediaSegments"]!.AsArray()))
            {
                node!.AsObject().Remove("skipTimingSelectionVersion");
            }
            File.WriteAllText(statePath, oldState.ToJsonString());
            var oldEntry = RefreshStateManager.TryReadMediaSegmentEntryForPathAsync(
                    Path.Combine(seasonDir, voiceFileBaseName + ".strm"), CancellationToken.None)
                .GetAwaiter().GetResult();
            AssertTrue(oldEntry == null, "Pre-consensus saved timings must be recomputed, not silently reused.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_MissingSeasonsPermitProvenMetadataCleanup()
    {
        foreach (var scenario in new[] { "metadata-only", "mixed", "unknown-dir", "unknown-file", "modified-stream", "corrupt-state" })
        {
            var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
            try
            {
                var series = Path.Combine(root, "Iceblade");
                var missing = WriteManagedCleanupSeason(series, 2, "removed-season-two");
                Directory.Delete(missing, true);
                var statePath = Path.Combine(series, RefreshStateManager.StateFileName);
                if (scenario == "metadata-only")
                {
                    // The real announced-season case: state contains root metadata but no episode paths.
                    var state = JsonNode.Parse(File.ReadAllText(statePath))!;
                    var season = state["seasons"]!["Season 02"]!;
                    var files = season["managedFiles"]!.AsArray();
                    foreach (var file in files.Where(file => file!["relativePath"]!.GetValue<string>().Contains('/')).ToArray()) files.Remove(file);
                    File.WriteAllText(statePath, state.ToJsonString());
                    File.WriteAllBytes(Path.Combine(series, "backdrop.jpg"), [1, 2, 3]);
                }
                if (scenario is "mixed" or "modified-stream")
                {
                    var existing = WriteManagedCleanupSeason(series, 1, "removed-season-one");
                    if (scenario == "modified-stream") File.AppendAllText(Path.Combine(existing, "S01E01.strm"), "manual change");
                }
                if (scenario == "unknown-dir") Directory.CreateDirectory(Path.Combine(series, "Personal files"));
                if (scenario == "unknown-file") File.WriteAllText(Path.Combine(series, "personal.txt"), "Keep me");
                if (scenario == "corrupt-state") File.WriteAllText(statePath, "invalid json");
                var result = RunStaleReleaseCleanup(root, Array.Empty<string>());
                var shouldDelete = scenario is "metadata-only" or "mixed";
                AssertEqual(shouldDelete ? 1 : 0, result.DeletedSeriesDirectoryCount, "Missing-season deletion proof: " + scenario);
                AssertEqual(!shouldDelete, Directory.Exists(series), "Unknown or modified content must still survive: " + scenario);
            }
            finally { TryDeleteDirectory(root); }
        }
    }

    static void StaleReleaseCleanup_AcceptsKnownOlderOwnershipManifests()
    {
        foreach (var version in new[] { 1, 2, 3 })
        {
            var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
            try
            {
                var series = Path.Combine(root, "Old release");
                WriteManagedCleanupSeason(series, 1, "removed");
                var path = Path.Combine(series, RefreshStateManager.StateFileName);
                var state = JsonNode.Parse(File.ReadAllText(path))!;
                state["generationContractVersion"] = version;
                File.WriteAllText(path, state.ToJsonString());
                AssertEqual(1, RunStaleReleaseCleanup(root, Array.Empty<string>()).DeletedSeriesDirectoryCount,
                    "An old generation with the same proven ownership schema must not require regeneration before deletion.");
            }
            finally { TryDeleteDirectory(root); }
        }
    }

    static void StaleReleaseCleanup_AllowsHostMetadataAndRenamedPoster()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var series = Path.Combine(root, "Stale");
            var season = WriteManagedCleanupSeason(series, 1, "stale");
            File.WriteAllText(Path.Combine(season, "S01E01.nfo"), "<episodedetails><runtime>24</runtime><dateadded>2026-09-19</dateadded></episodedetails>");
            File.WriteAllText(Path.Combine(series, "tvshow.nfo"), "<tvshow><title>Host enriched title</title></tvshow>");
            File.WriteAllText(Path.Combine(season, "season.nfo"), "<season><seasonnumber>1</seasonnumber></season>");
            File.WriteAllBytes(Path.Combine(season, "S01E01 - Old Voice-thumb.jpg"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(series, "folder.jpg"), new byte[] { 1, 2, 3 });
            var statePath = Path.Combine(series, RefreshStateManager.StateFileName);
            var state = JsonNode.Parse(File.ReadAllText(statePath))!;
            state["seasons"]!["Season 01"]!["managedFiles"]!.AsArray().Add(new JsonObject
            {
                ["relativePath"] = "poster.jpg", ["sha256"] = new string('a', 64)
            });
            File.WriteAllText(statePath, state.ToJsonString());
            AssertEqual(1, RunStaleReleaseCleanup(root, Array.Empty<string>()).DeletedSeriesDirectoryCount,
                "Host metadata, artwork and a renamed poster must not pin a stale managed release.");
            AssertTrue(!Directory.Exists(series), "The stale series must disappear entirely.");
        }
        finally { TryDeleteDirectory(root); }
    }

    static void StaleReleaseCleanup_RejectsUnknownStreamsAndMalformedMetadata()
    {
        foreach (var invalid in new[] { "extra.strm", "private.jpg", "invalid-nfo", "foreign-thumb" })
        {
            var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
            try
            {
                var series = Path.Combine(root, "Stale");
                var season = WriteManagedCleanupSeason(series, 1, "stale");
                var name = invalid == "invalid-nfo" ? "S01E01.nfo" : invalid == "foreign-thumb" ? "S01E99 - Other-thumb.jpg" : invalid;
                File.WriteAllText(Path.Combine(season, name), "unproven");
                AssertEqual(0, RunStaleReleaseCleanup(root, Array.Empty<string>()).DeletedDirectoryCount,
                    "Unknown streams/files, malformed NFOs and unrelated thumbnails must block deletion.");
                AssertTrue(File.Exists(Path.Combine(season, name)), "Unproven content must remain.");
            }
            finally { TryDeleteDirectory(root); }
        }
    }

    static void StaleReleaseCleanup_HostMetadataDoesNotHideModifiedStream()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var series = Path.Combine(root, "Stale");
            var season = WriteManagedCleanupSeason(series, 1, "stale");
            File.AppendAllText(Path.Combine(season, "S01E01.strm"), "modified");
            File.WriteAllText(Path.Combine(season, "S01E01.nfo"), "<episodedetails><runtime>24</runtime></episodedetails>");
            File.WriteAllBytes(Path.Combine(series, "folder.jpg"), new byte[] { 1 });
            AssertEqual(0, RunStaleReleaseCleanup(root, Array.Empty<string>()).DeletedDirectoryCount,
                "Mutable metadata must never bypass stream ownership hashes.");
        }
        finally { TryDeleteDirectory(root); }
    }

    static void StaleReleaseCleanup_DeletesStaleManagedDirectory()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var staleSeries = Path.Combine(tempRoot, "Stale series");
            WriteManagedCleanupSeason(staleSeries, 1, "stale-release");

            var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

            AssertFalse(Directory.Exists(staleSeries), "A managed series absent from the current keys should be deleted.");
            AssertEqual(1, result.DeletedDirectoryCount, "Exactly one stale managed series root should be deleted.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_RetainsCurrentAndManualKeys()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var currentSeries = Path.Combine(tempRoot, "Current series");
            var manualSeries = Path.Combine(tempRoot, "Manual series");
            WriteManagedCleanupSeason(currentSeries, 1, "current-release");
            WriteManagedCleanupSeason(manualSeries, 1, "manual-release");

            var result = RunStaleReleaseCleanup(tempRoot, new[] { "CURRENT-RELEASE", "manual-release" });

            AssertTrue(Directory.Exists(currentSeries), "A current user-list key must retain its managed series directory.");
            AssertTrue(Directory.Exists(manualSeries), "A manual slug passed in the current key set must retain its managed series directory.");
            AssertEqual(2, result.RetainedDirectoryCount, "Both current and manual configured releases should be retained.");
            AssertEqual(0, result.DeletedDirectoryCount, "No current release should be deleted.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_SkipsMissingCorruptAndAmbiguousState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var missingState = Path.Combine(tempRoot, "Missing state");
            var corruptState = Path.Combine(tempRoot, "Corrupt state");
            var ambiguousState = Path.Combine(tempRoot, "Ambiguous state");
            Directory.CreateDirectory(missingState);
            Directory.CreateDirectory(corruptState);
            Directory.CreateDirectory(ambiguousState);
            File.WriteAllText(Path.Combine(corruptState, RefreshStateManager.StateFileName), "{ not-json");
            WriteCleanupState(ambiguousState, ("Season 01", string.Empty));

            var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

            AssertTrue(Directory.Exists(missingState), "Directories without managed refresh state must not be deleted.");
            AssertTrue(Directory.Exists(corruptState), "Directories with corrupt refresh state must not be deleted.");
            AssertTrue(Directory.Exists(ambiguousState), "Directories with ambiguous refresh state must not be deleted.");
            AssertEqual(3, result.SkippedDirectoryCount, "Missing, corrupt, and ambiguous state should each take the safe skip path.");
            AssertEqual(0, result.DeletedDirectoryCount, "Unproven directories must never be deleted by stale cleanup.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_SkipsSeasonWithUnknownFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seriesRoot = Path.Combine(tempRoot, "Unknown file series");
            var seasonDir = WriteManagedCleanupSeason(seriesRoot, 1, "stale-release");
            File.WriteAllText(Path.Combine(seasonDir, "user-note.txt"), "do not delete");

            var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

            AssertTrue(Directory.Exists(seriesRoot), "An untracked file in a stale season must block recursive deletion.");
            AssertTrue(Directory.Exists(seasonDir), "A season containing an untracked file must remain intact.");
            AssertEqual(0, result.DeletedDirectoryCount, "Cleanup must not delete a season whose manifest is no longer exact.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_SkipsSeasonWithModifiedManagedFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seriesRoot = Path.Combine(tempRoot, "Modified file series");
            var seasonDir = WriteManagedCleanupSeason(seriesRoot, 1, "stale-release");
            File.AppendAllText(Path.Combine(seasonDir, "S01E01.strm"), "modified");

            var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

            AssertTrue(Directory.Exists(seriesRoot), "A stale root with modified managed artifacts must not be deleted.");
            AssertTrue(Directory.Exists(seasonDir), "A season with a managed-file hash mismatch must remain intact.");
            AssertEqual(0, result.DeletedDirectoryCount, "Cleanup must not delete a stale season when its managed file hash no longer matches state.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_CanonicalizesPlainAndUrlCurrentKeys()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seriesRoot = Path.Combine(tempRoot, "Canonical key series");
            WriteManagedCleanupSeason(seriesRoot, 1, "frieren");
            var statePath = Path.Combine(seriesRoot, RefreshStateManager.StateFileName);
            var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            var season = state["seasons"]!.AsObject()["Season 01"]!.AsObject();
            season["cleanKey"] = "https://yani.tv/anime/frieren";
            File.WriteAllText(statePath, state.ToJsonString());

            var result = RunStaleReleaseCleanup(tempRoot, new[] { "frieren" });

            AssertTrue(Directory.Exists(seriesRoot), "A plain current slug must retain a matching URL-form state cleanKey.");
            AssertEqual(1, result.RetainedDirectoryCount, "Shared key canonicalization should retain the matching managed root.");
            AssertEqual(0, result.DeletedDirectoryCount, "Equivalent URL and slug key forms must never trigger stale deletion.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_SkipsUnknownGenerationContract()
    {
        AssertTrue(RefreshStateManager.GenerationContractVersion > 1, "Gateway cleanup must have a newer generation contract than legacy state files.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seriesRoot = Path.Combine(tempRoot, "Legacy contract series");
            WriteManagedCleanupSeason(seriesRoot, 1, "stale-release");
            var statePath = Path.Combine(seriesRoot, RefreshStateManager.StateFileName);
            var state = JsonNode.Parse(File.ReadAllText(statePath))!.AsObject();
            state["generationContractVersion"] = 5;
            File.WriteAllText(statePath, state.ToJsonString());

            var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

            AssertTrue(Directory.Exists(seriesRoot), "A stale directory with an unknown generation contract must be preserved.");
            AssertEqual(1, result.SkippedDirectoryCount, "Unknown generation state should take the safe skip path.");
            AssertEqual(0, result.DeletedDirectoryCount, "Unknown state must not authorize deletion.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_DeletesMixedRootStaleSeasonAndStateEntry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seriesRoot = Path.Combine(tempRoot, "Shared series");
            var currentSeason = Path.Combine(seriesRoot, "Season 01");
            var staleSeason = Path.Combine(seriesRoot, "Season 02");
            WriteManagedCleanupSeason(seriesRoot, 1, "current-release");
            WriteManagedCleanupSeason(seriesRoot, 2, "stale-release");

            var result = RunStaleReleaseCleanup(tempRoot, new[] { "current-release" });

            AssertTrue(Directory.Exists(seriesRoot), "A mixed series root must remain while it has a current season.");
            AssertTrue(Directory.Exists(currentSeason), "The current season directory must remain in a mixed series root.");
            AssertFalse(Directory.Exists(staleSeason), "The stale season directory must be removed from a mixed series root.");
            AssertEqual(1, result.DeletedDirectoryCount, "Removing one stale season must count as one managed directory deletion.");
            AssertEqual(0, result.DeletedSeriesDirectoryCount, "Removing only a stale season must not count as deleting its retained series root.");
            AssertEqual(1, result.DeletedSeasonDirectoryCount, "Mixed-root cleanup should report the removed stale season.");

            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(seriesRoot, RefreshStateManager.StateFileName)));
            var seasons = state.RootElement.GetProperty("seasons");
            AssertTrue(seasons.TryGetProperty("Season 01", out _), "Current season state must remain after mixed-root cleanup.");
            AssertFalse(seasons.TryGetProperty("Season 02", out _), "Stale season state must be removed with its directory.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void StaleReleaseCleanup_DeletesAllStaleRootForEmptyCurrentSet()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        try
        {
            var seriesRoot = Path.Combine(tempRoot, "All stale series");
            WriteManagedCleanupSeason(seriesRoot, 1, "stale-one");
            WriteManagedCleanupSeason(seriesRoot, 2, "stale-two");

            var result = RunStaleReleaseCleanup(tempRoot, Array.Empty<string>());

            AssertFalse(Directory.Exists(seriesRoot), "An all-stale managed series root should be deleted when the valid current user list is empty.");
            AssertEqual(1, result.DeletedDirectoryCount, "Deleting an all-stale multi-season root should count as one root deletion.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshTask_ProcessesAtMostTwoTitlesConcurrently()
    {
        var active = 0;
        var maxActive = 0;
        var keys = Enumerable.Range(1, 8).Select(x => x.ToString()).ToArray();
        var progress = new RecordingProgress();

        InvokeRefreshTaskStaticTask(
                "ProcessKeysInParallelAsync",
                keys,
                (Func<string, CancellationToken, Task>)(async (_, cancellationToken) =>
                {
                    var current = Interlocked.Increment(ref active);
                    UpdateMax(ref maxActive, current);
                    await Task.Delay(40, cancellationToken).ConfigureAwait(false);
                    Interlocked.Decrement(ref active);
                }),
                progress,
                NullLogger.Instance,
                null,
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        AssertTrue(maxActive <= 2, "Refresh workers should never exceed MaxDegreeOfParallelism=2.");
        AssertEqual(100.0, progress.Values.Last(), "Progress should reach 100 after all title workers complete.");
    }

    static void RefreshTask_RunGateSkipsConcurrentRun()
    {
        using var gate = new SemaphoreSlim(1, 1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondBodyRan = false;

        var first = InvokeRefreshTaskStaticTaskResult<bool>(
            "RunWithRunGateAsync",
            gate,
            NullLogger.Instance,
            (Func<Task>)(async () =>
            {
                entered.SetResult();
                await release.Task.ConfigureAwait(false);
            }));

        AssertTrue(entered.Task.Wait(TimeSpan.FromSeconds(2)), "First gated refresh body should start.");

        var second = InvokeRefreshTaskStaticTaskResult<bool>(
                "RunWithRunGateAsync",
                gate,
                NullLogger.Instance,
                (Func<Task>)(() =>
                {
                    secondBodyRan = true;
                    return Task.CompletedTask;
                }))
            .GetAwaiter()
            .GetResult();

        AssertFalse(second, "Second refresh should exit quickly while the run gate is held.");
        AssertFalse(secondBodyRan, "Skipped refresh should not execute the body.");

        release.SetResult();
        AssertTrue(first.GetAwaiter().GetResult(), "First refresh should complete normally after releasing the gate.");
    }

    static void RefreshTask_LazyKodikInitializationRunsOnlyWhenValueIsUsed()
    {
        var method = typeof(RefreshYummyKodikLibraryTask).GetMethod(
            "CreateSharedLazyTask",
            BindingFlags.Static | BindingFlags.NonPublic);
        AssertTrue(method is not null, "Shared lazy task helper should exist.");

        var calls = 0;
        Func<Task<int>> factory = async () =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(40).ConfigureAwait(false);
            return 42;
        };

        var lazy = (Lazy<Task<int>>)method!.MakeGenericMethod(typeof(int)).Invoke(null, new object[] { factory })!;
        AssertEqual(0, calls, "Kodik lazy initialization should not run until the shared value is requested.");

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(async () => await lazy.Value.ConfigureAwait(false)))
            .ToArray();
        var results = Task.WhenAll(tasks).GetAwaiter().GetResult();

        AssertEqual(1, calls, "Concurrent Kodik lazy initialization requests should share one factory call.");
        AssertTrue(results.All(value => value == 42), "All concurrent Kodik lazy callers should receive the initialized value.");
    }

    static void RefreshState_PerVoiceModeDoesNotPreSkip()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var canSkip = RefreshStateManager.CanSkipSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(canSkip, "Per-voice mode must not pre-skip Kodik lookup even when state and files match.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_KodikCatalogSignatureIsOrderIndependent()
    {
        var first = RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
        {
            IdType = "Shikimori",
            Id = "52991",
            SeriesCount = 2,
            Translations = new[]
            {
                new RefreshStateKodikTranslationInput
                {
                    Id = "610",
                    Type = "voice",
                    Name = "AniLibria",
                    MaxEpisode = 2,
                    AvailableEpisodes = new[] { 2, 1, 2 }
                },
                new RefreshStateKodikTranslationInput
                {
                    Id = "777",
                    Type = "voice",
                    Name = "AniStar",
                    MaxEpisode = 2,
                    AvailableEpisodes = new[] { 1, 2 }
                }
            }
        });
        var reordered = RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
        {
            IdType = "Shikimori",
            Id = "52991",
            SeriesCount = 2,
            Translations = new[]
            {
                new RefreshStateKodikTranslationInput
                {
                    Id = "777",
                    Type = "voice",
                    Name = "AniStar",
                    MaxEpisode = 2,
                    AvailableEpisodes = new[] { 2, 1 }
                },
                new RefreshStateKodikTranslationInput
                {
                    Id = "610",
                    Type = "voice",
                    Name = "AniLibria",
                    MaxEpisode = 2,
                    AvailableEpisodes = new[] { 1, 2 }
                }
            }
        });
        var changed = RefreshStateManager.BuildKodikCatalogSignature(new RefreshStateKodikCatalogInput
        {
            IdType = "Shikimori",
            Id = "52991",
            SeriesCount = 2,
            Translations = new[]
            {
                new RefreshStateKodikTranslationInput
                {
                    Id = "610",
                    Type = "voice",
                    Name = "AniLibria",
                    MaxEpisode = 2,
                    AvailableEpisodes = new[] { 1, 2 }
                },
                new RefreshStateKodikTranslationInput
                {
                    Id = "778",
                    Type = "voice",
                    Name = "New Voice",
                    MaxEpisode = 2,
                    AvailableEpisodes = new[] { 1, 2 }
                }
            }
        });

        AssertEqual(first, reordered, "Kodik catalog signature should ignore provider ordering and duplicate episode numbers.");
        AssertFalse(string.Equals(first, changed, StringComparison.Ordinal), "A changed Kodik translation catalog should invalidate the signature.");
    }

    static void RefreshState_PerVoiceDeepSkipRequiresFreshMatchingCatalog()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
            var validatedAtUtc = new DateTimeOffset(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);
            const string signature = "sha256:kodik-catalog-a";
            var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
            const string kodikOnlyFileBaseName = "S01E02 - Kodik Voice";
            File.WriteAllText(
                Path.Combine(seasonDir, kodikOnlyFileBaseName + ".strm"),
                "https://jellyfin.test/YummyKodik/stream?type=shikimori&id=52991&ep=2&tr=610&format=hls" + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(seasonDir, kodikOnlyFileBaseName + ".nfo"),
                NfoBuilder.BuildEpisodeNfo(2, 1, "Frieren", "Plot"));
            fixture.ExpectedEpisodeFileBaseNames[2] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                kodikOnlyFileBaseName
            };
            var written = RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    mediaSegmentEntriesByFileBaseName: null,
                    new RefreshStateKodikValidation
                    {
                        CatalogSignature = signature,
                        DeepValidatedAtUtc = validatedAtUtc
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(written, "Complete per-voice artifacts should produce refresh state with Kodik validation metadata.");

            var canSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    signature,
                    validatedAtUtc.AddHours(1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(canSkip, "Fresh matching Yummy state and Kodik catalog should skip deep per-episode validation.");

            var canSkipKodikLookup = RefreshStateManager.CanSkipPerVoiceKodikLookupAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    validatedAtUtc.AddHours(1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(canSkipKodikLookup, "A high-quality fast refresh should avoid a redundant Kodik lookup while verified fallback state is fresh.");

            var targetQualityInput = BuildRefreshStateSeasonInput(
                expectedAvailableEpisodes: fixture.Input.ExpectedAvailableEpisodes,
                createStrmPerVoiceTranslation: true,
                preferredQuality: RefreshStateManager.KodikKnownMaximumQuality,
                fingerprint: fixture.Input.Fingerprint);
            var targetQualityCanSkipLookup = RefreshStateManager.CanSkipPerVoiceKodikLookupAsync(
                    fixture.SeriesRoot,
                    targetQualityInput,
                    validatedAtUtc.AddHours(1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(targetQualityCanSkipLookup, "When Kodik can satisfy the requested quality, its lightweight catalog must still be checked every refresh.");

            var changedCatalogCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    "sha256:kodik-catalog-b",
                    validatedAtUtc.AddHours(1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(changedCatalogCanSkip, "Changed Kodik metadata should force deep validation.");

            var expiredCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    signature,
                    validatedAtUtc.Add(RefreshStateManager.PerVoiceDeepValidationInterval),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(expiredCanSkip, "Expired Kodik validation should force a periodic deep check.");

            var expiredLookupCanSkip = RefreshStateManager.CanSkipPerVoiceKodikLookupAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    validatedAtUtc.Add(RefreshStateManager.PerVoiceDeepValidationInterval),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(expiredLookupCanSkip, "Expired high-quality fast-path state should force a fresh Kodik catalog lookup.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_NewYummyKodikVoiceInvalidatesPreLookupSkip()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            static YummyAnimeResponse BuildAnime(bool includeDreamCast, string iframeMarker, int duration)
            {
                var videos = new List<YummyVideoItem>
                {
                    new()
                    {
                        Number = "9",
                        IframeUrl = "https://kodik.test/animevost/" + iframeMarker,
                        Duration = duration,
                        Data = new YummyVideoData
                        {
                            PlayerId = (int)YummyVideoProviderKind.Kodik,
                            Player = "Плеер Kodik",
                            Dubbing = "Озвучка AnimeVost"
                        }
                    }
                };

                if (includeDreamCast)
                {
                    videos.Add(new YummyVideoItem
                    {
                        Number = "9",
                        IframeUrl = "https://kodik.test/dream-cast/" + iframeMarker,
                        Duration = duration,
                        Data = new YummyVideoData
                        {
                            PlayerId = (int)YummyVideoProviderKind.Kodik,
                            Player = "Плеер Kodik",
                            Dubbing = "Озвучка Dream Cast"
                        }
                    });
                }

                return new YummyAnimeResponse
                {
                    AnimeId = 11237,
                    AnimeUrl = "taynaya-bitva-za-prestol-silneyshego-printsa-duraleya",
                    Title = "Тайная битва за престол сильнейшего принца-дуралея",
                    Videos = videos
                };
            }

            static YummyRefreshInfo BuildRefresh(YummyAnimeResponse anime)
            {
                return new YummyRefreshInfo(
                    new YummyAnimeTitleInfo(anime, anime.AnimeUrl, anime.Title, anime.Title, 1),
                    YummyVideoCatalog.Create(anime),
                    new SeriesFileInfo("series", "season", "http://127.0.0.1:8096"),
                    new EpisodeAvailabilityInfo(new[] { 9 }, 9, Array.Empty<int>(), Array.Empty<int>(), new[] { 9 }));
            }

            var cfg = new PluginConfiguration
            {
                CreateStrmPerVoiceTranslation = true,
                PreferredQuality = 1080
            };
            var baselineCoverage = RefreshStateService.BuildProviderCoverageFingerprintItems(
                cfg,
                BuildRefresh(BuildAnime(includeDreamCast: false, iframeMarker: "before", duration: 1420)));
            var metadataOnlyCoverage = RefreshStateService.BuildProviderCoverageFingerprintItems(
                cfg,
                BuildRefresh(BuildAnime(includeDreamCast: false, iframeMarker: "after", duration: 1500)));
            var changedCoverage = RefreshStateService.BuildProviderCoverageFingerprintItems(
                cfg,
                BuildRefresh(BuildAnime(includeDreamCast: true, iframeMarker: "after", duration: 1427)));

            AssertEqual(
                string.Join("|", baselineCoverage),
                string.Join("|", metadataOnlyCoverage),
                "Kodik iframe and duration churn must not invalidate the availability fingerprint.");
            AssertFalse(
                baselineCoverage.Any(x => x.Contains("dreamcast", StringComparison.Ordinal)),
                "The baseline fingerprint must reproduce the catalog before Dream Cast episode 9 appeared.");
            AssertTrue(
                changedCoverage.Any(x => string.Equals(x, "ep:9:yummy-kodik:voice:dreamcast", StringComparison.Ordinal)),
                "A new Yummy-advertised Kodik voice must enter the semantic refresh fingerprint.");

            var baselineFingerprint = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(
                mode: "per-voice",
                preferredQuality: 1080,
                expectedAvailableEpisodes: 9,
                providerCoverage: baselineCoverage));
            var changedFingerprint = RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(
                mode: "per-voice",
                preferredQuality: 1080,
                expectedAvailableEpisodes: 9,
                providerCoverage: changedCoverage));
            AssertFalse(
                string.Equals(baselineFingerprint, changedFingerprint, StringComparison.Ordinal),
                "New Kodik-only episode/voice availability must change the refresh fingerprint.");

            var fixture = CreateRefreshStateFixture(
                tempRoot,
                expectedAvailableEpisodes: 9,
                createStrmPerVoiceTranslation: true,
                fingerprint: baselineFingerprint);
            var validatedAtUtc = new DateTimeOffset(2026, 9, 1, 18, 46, 46, TimeSpan.Zero);
            AssertTrue(
                RefreshStateManager.WriteSeasonStateAsync(
                        fixture.SeriesRoot,
                        fixture.Input,
                        fixture.ExpectedEpisodeFileBaseNames,
                        mediaSegmentEntriesByFileBaseName: null,
                        new RefreshStateKodikValidation
                        {
                            CatalogSignature = "sha256:catalog-before-dream-cast-episode-9",
                            DeepValidatedAtUtc = validatedAtUtc
                        },
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
                "The pre-change per-voice state should be valid for the fast-path transition regression.");

            var unchangedDecision = RefreshStateManager.EvaluatePerVoiceKodikLookupAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    validatedAtUtc.AddMinutes(17),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(unchangedDecision.ShouldSkip, "An unchanged 1080p title should retain the pre-Kodik fast path.");

            var changedInput = BuildRefreshStateSeasonInput(
                expectedAvailableEpisodes: 9,
                createStrmPerVoiceTranslation: true,
                preferredQuality: 1080,
                fingerprint: changedFingerprint,
                cleanKey: fixture.Input.CleanKey);
            var changedDecision = RefreshStateManager.EvaluatePerVoiceKodikLookupAsync(
                    fixture.SeriesRoot,
                    changedInput,
                    validatedAtUtc.AddMinutes(17),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(changedDecision.ShouldSkip, "A newly advertised Kodik-only voice must force a fresh Kodik lookup.");
            AssertEqual(
                RefreshSkipReason.FingerprintMismatch,
                changedDecision.Reason,
                "The fast path should reject the changed title before redundant managed-file hashing.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_PerVoiceDeepSkipRejectsDamagedOrUnexpectedFiles()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = CreateRefreshStateFixture(tempRoot, createStrmPerVoiceTranslation: true);
            var validatedAtUtc = DateTimeOffset.UtcNow;
            const string signature = "sha256:kodik-catalog";
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    mediaSegmentEntriesByFileBaseName: null,
                    new RefreshStateKodikValidation
                    {
                        CatalogSignature = signature,
                        DeepValidatedAtUtc = validatedAtUtc
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
            var managedStrm = Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly).Single();
            File.AppendAllText(managedStrm, "damaged");
            var damagedCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    signature,
                    validatedAtUtc.AddMinutes(1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(damagedCanSkip, "A modified managed artifact should force deep validation.");

            File.WriteAllText(managedStrm, "https://jellyfin.test/YummyKodik/stream?ep=1&allohaRequestToken=secret-request-1" + Environment.NewLine);
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    mediaSegmentEntriesByFileBaseName: null,
                    new RefreshStateKodikValidation
                    {
                        CatalogSignature = signature,
                        DeepValidatedAtUtc = validatedAtUtc
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.strm"), "stale");
            File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.nfo"), "<episodedetails />");

            var unexpectedCanSkip = RefreshStateManager.CanSkipPerVoiceDeepRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    signature,
                    validatedAtUtc.AddMinutes(1),
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertFalse(unexpectedCanSkip, "Unexpected episode artifacts should force deep validation and cleanup.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshState_SkipDecisionReportsReasonsAndFileCounts()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));

        try
        {
            var missingInput = BuildRefreshStateSeasonInput();
            var missing = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                    Path.Combine(tempRoot, "Missing"),
                    missingInput,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(RefreshSkipReason.StateMissing, missing.Reason, "Missing state should have an actionable skip reason.");
            AssertEqual(0, missing.ManagedFilesChecked, "Missing state must not claim any file hashes were checked.");

            var fixture = CreateRefreshStateFixture(tempRoot);
            AssertTrue(
                RefreshStateManager.WriteSeasonStateAsync(
                        fixture.SeriesRoot,
                        fixture.Input,
                        fixture.ExpectedEpisodeFileBaseNames,
                        CancellationToken.None)
                    .GetAwaiter()
                    .GetResult(),
                "Complete fixture should write refresh state.");

            var matched = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertTrue(matched.ShouldSkip, "Matching state should produce a positive skip decision.");
            AssertEqual(RefreshSkipReason.Matched, matched.Reason, "Matching state should report Matched.");
            AssertEqual(3, matched.ManagedFilesChecked, "Fixture should hash tvshow NFO plus one STRM/NFO pair.");

            var changedInput = BuildRefreshStateSeasonInput(fingerprint: "sha256:changed");
            var changed = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    changedInput,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(RefreshSkipReason.FingerprintMismatch, changed.Reason, "Input drift should be distinguishable from file damage.");
            AssertEqual(0, changed.ManagedFilesChecked, "Fingerprint mismatch should reject before hashing files.");

            var seasonDir = Path.Combine(fixture.SeriesRoot, fixture.Input.SeasonKey);
            var strmPath = Directory.EnumerateFiles(seasonDir, "*.strm", SearchOption.TopDirectoryOnly).Single();
            File.AppendAllText(strmPath, "damaged");
            var damaged = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(RefreshSkipReason.ManagedFileHashMismatch, damaged.Reason, "Damaged content should report a managed hash mismatch.");
            AssertTrue(damaged.ManagedFilesChecked > 0 && damaged.ManagedFilesChecked <= 3, "File count should reflect hashes actually computed before mismatch.");

            File.WriteAllText(
                strmPath,
                "https://jellyfin.test/YummyKodik/stream?ep=1&allohaRequestToken=secret-request-1" + Environment.NewLine);
            RefreshStateManager.WriteSeasonStateAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    fixture.ExpectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.strm"), "stale");
            File.WriteAllText(Path.Combine(seasonDir, "S01E02 - Stale.nfo"), "<episodedetails />");
            var unexpected = RefreshStateManager.EvaluateSingleFileRefreshAsync(
                    fixture.SeriesRoot,
                    fixture.Input,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(RefreshSkipReason.UnexpectedArtifacts, unexpected.Reason, "Extra episode files should be a distinct skip rejection reason.");
            AssertEqual(3, unexpected.ManagedFilesChecked, "Unexpected-artifact scan should run only after all managed hashes match.");
            AssertEqual(2, unexpected.UnexpectedArtifactCount, "Both stale STRM and NFO should be reported.");

            var perVoiceRoot = Path.Combine(tempRoot, "PerVoice");
            var perVoiceFixture = CreateRefreshStateFixture(perVoiceRoot, createStrmPerVoiceTranslation: true);
            var validatedAt = DateTimeOffset.UtcNow.AddDays(-2);
            const string signature = "sha256:kodik-catalog";
            RefreshStateManager.WriteSeasonStateAsync(
                    perVoiceFixture.SeriesRoot,
                    perVoiceFixture.Input,
                    perVoiceFixture.ExpectedEpisodeFileBaseNames,
                    mediaSegmentEntriesByFileBaseName: null,
                    new RefreshStateKodikValidation
                    {
                        CatalogSignature = signature,
                        DeepValidatedAtUtc = validatedAt
                    },
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            var expired = RefreshStateManager.EvaluatePerVoiceDeepRefreshAsync(
                    perVoiceFixture.SeriesRoot,
                    perVoiceFixture.Input,
                    signature,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            AssertEqual(RefreshSkipReason.DeepValidationExpired, expired.Reason, "Expired 24-hour validation should have a stable reason.");
            AssertEqual(0, expired.ManagedFilesChecked, "Expired validation should reject before redundant hashing.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static RefreshStateSeasonInput BuildRefreshStateSeasonInput(
        int seasonNumber = 1,
        int expectedAvailableEpisodes = 1,
        bool createStrmPerVoiceTranslation = false,
        int preferredQuality = 1080,
        string? fingerprint = null,
        string cleanKey = "frieren")
    {
        return new RefreshStateSeasonInput
        {
            SeasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber),
            SeasonNumber = seasonNumber,
            CleanKey = cleanKey,
            CreateStrmPerVoiceTranslation = createStrmPerVoiceTranslation,
            PreferredQuality = preferredQuality,
            Fingerprint = fingerprint ?? RefreshStateManager.BuildFingerprint(BuildRefreshStateFingerprintInput(seasonNumber: seasonNumber)),
            ExpectedAvailableEpisodes = expectedAvailableEpisodes
        };
    }

    static RefreshStateFingerprintInput BuildRefreshStateFingerprintInput(
        string mode = "single-file",
        string streamGatewayBaseUrl = "http://127.0.0.1:8096",
        string preferredTranslationFilter = "anilibria",
        int preferredQuality = 1080,
        string seriesTitle = "Frieren",
        int seasonNumber = 1,
        int expectedAvailableEpisodes = 1,
        IReadOnlyList<string>? providerCoverage = null)
    {
        return new RefreshStateFingerprintInput
        {
            Mode = mode,
            StreamGatewayBaseUrl = streamGatewayBaseUrl,
            PreferredTranslationFilter = preferredTranslationFilter,
            PreferredQuality = preferredQuality,
            CleanKey = "frieren",
            RawTitle = seasonNumber == 1 ? "Frieren" : "Frieren 2",
            SeriesTitle = seriesTitle,
            SeasonKey = RefreshStateManager.BuildSeasonKey(seasonNumber),
            SeasonNumber = seasonNumber,
            AnimeId = 52991,
            AnimeUrl = "frieren",
            ShikimoriId = 52991,
            KinopoiskId = 123456,
            ImdbId = "tt1234567",
            ExpectedAvailableEpisodes = expectedAvailableEpisodes,
            KnownSupportedEpisodes = Enumerable.Range(1, expectedAvailableEpisodes).ToArray(),
            AllohaSupportedEpisodes = Enumerable.Range(1, expectedAvailableEpisodes).ToArray(),
            CvhSupportedEpisodes = Array.Empty<int>(),
            YummySupportedEpisodes = Enumerable.Range(1, expectedAvailableEpisodes).ToArray(),
            ProviderCoverage = providerCoverage ?? new[] { "ep:1:preferred:Alloha" },
            AllohaApiBaseUrl = "https://api.alloha.test",
            AllohaApiTokenHash = RefreshStateManager.HashSecret("alloha-api-token")
        };
    }

    static void GenerateKodikEpisodeFilesAsync_FillsMissingTranslationsForExistingEpisode()
    {
        var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();

        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "Dream Cast");
        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AnimeVost");

        var missing = TestData.KodikSupplementTranslationNames
            .Where(x => !EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, x))
            .ToArray();

        AssertEqual(1, missing.Length, "Only translations absent from Yummy/CVH coverage should remain for Kodik supplementation.");
        AssertEqual("AniLibria", missing[0], "Kodik supplement should only add the missing translation.");
    }

    static void ResolveEpisodesNeedingKodikTranslation_SkipsYummyCoveredPairs()
    {
        var expectedEpisodeTranslationKeys = new Dictionary<int, HashSet<string>>();
        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 1, "AniLibria.TV");
        EpisodeArtifactMaintenance.TrackExpectedEpisodeTranslation(expectedEpisodeTranslationKeys, 3, "AniLibria");
        var translation = new KodikTranslation
        {
            Id = "610",
            Type = "voice",
            Name = "AniLibria",
            MaxEpisode = 3,
            AvailableEpisodes = new[] { 1, 2, 3 }
        };

        var episodes = KodikSupplementService.ResolveEpisodesNeedingKodikTranslation(
            translation,
            new[] { 1, 2, 3 },
            expectedEpisodeTranslationKeys);

        AssertEqual("2", string.Join(",", episodes), "Kodik should deep-check only episode/voice pairs not already supplied by an equivalent Yummy provider voice.");
    }

    static void ResolveEpisodeTranslationFileBaseName_ReusesExistingEquivalentArtifactName()
    {
        var aliases = new Dictionary<int, Dictionary<string, string>>
        {
            [1] = new(StringComparer.OrdinalIgnoreCase)
            {
                [EpisodeArtifactMaintenance.NormalizeEpisodeTranslationKey("AniLibria.TV")] = "S01E01 - AniLibria.TV"
            }
        };

        var fileBaseName = EpisodeArtifactMaintenance.ResolveEpisodeTranslationFileBaseName(aliases, 1, "S01E01", "AniLibria");

        AssertEqual("S01E01 - AniLibria.TV", fileBaseName, "Refresh should reuse the existing equivalent file name to keep Jellyfin item ids stable.");
    }

    static void ResolveProviderCoverage_UsesYummyHintWhenKodikSeriesCountIsZero()
    {
        var coverage = YummyEpisodeAvailability.ResolveProviderCoverage(0, 1);
        AssertEqual(1, coverage.KodikAvailableEpisodes, "When Kodik search returns zero seriesCount but Yummy knows episode 1 exists, refresh should still generate files.");
        AssertEqual(1, coverage.OverallAvailableEpisodes, "The overall cleanup ceiling should preserve Yummy's known episode.");
    }

    static void ResolveProviderCoverage_PreservesYummyCoverageBeyondKodik()
    {
        var coverage = YummyEpisodeAvailability.ResolveProviderCoverage(8, 16);

        AssertEqual(8, coverage.KodikAvailableEpisodes, "Kodik link processing should stay bounded by Kodik's eight reported episodes.");
        AssertEqual(16, coverage.OverallAvailableEpisodes, "Whole-season cleanup must preserve the wider sixteen-episode Yummy/CVH coverage.");
    }

    static void CleanupUnexpectedEpisodeArtifacts_RemovesStaleFilesBeyondExpectedCoverage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var seasonDir = Path.Combine(tempRoot, "Season 02");
        Directory.CreateDirectory(seasonDir);

        try
        {
            File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Dream Cast.strm"), "keep");
            File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Dream Cast.nfo"), "<ok />");
            File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Old Voice.strm"), "delete");
            File.WriteAllText(Path.Combine(seasonDir, "S02E01 - Old Voice.nfo"), "<delete />");
            File.WriteAllText(Path.Combine(seasonDir, "S02E02 - Old Voice.strm"), "delete");
            File.WriteAllText(Path.Combine(seasonDir, "S02E02 - Old Voice.nfo"), "<delete />");

            var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>
            {
                [1] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "S02E01 - Dream Cast"
                }
            };

            EpisodeArtifactMaintenance.CleanupUnexpectedEpisodeArtifacts(
                NullLogger.Instance,
                seasonDir,
                2,
                expectedEpisodeFileBaseNames,
                1);

            AssertTrue(File.Exists(Path.Combine(seasonDir, "S02E01 - Dream Cast.strm")), "Expected translation STRM should remain after cleanup.");
            AssertTrue(File.Exists(Path.Combine(seasonDir, "S02E01 - Dream Cast.nfo")), "Expected translation NFO should remain after cleanup.");
            AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E01 - Old Voice.strm")), "Unexpected translation STRM should be removed.");
            AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E01 - Old Voice.nfo")), "Unexpected translation NFO should be removed.");
            AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E02 - Old Voice.strm")), "Episodes beyond the currently available range should be removed.");
            AssertFalse(File.Exists(Path.Combine(seasonDir, "S02E02 - Old Voice.nfo")), "NFO for unavailable future episodes should be removed.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void ExistingLibraryFallbackRefreshInfoLoader_LoadsSnapshotFromRefreshState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var seriesRoot = Path.Combine(tempRoot, "Frieren [shikimori-52991]");
        var seasonDir = Path.Combine(seriesRoot, "Season 01");

        try
        {
            Directory.CreateDirectory(seasonDir);
            File.WriteAllText(Path.Combine(seriesRoot, "tvshow.nfo"), NfoBuilder.BuildSeriesNfo("Frieren", "Existing plot"));
            File.WriteAllText(
                Path.Combine(seasonDir, "S01E01 - Dream Cast.strm"),
                "https://jellyfin.test/YummyKodik/stream?provider=cvh&animeId=21008&ep=1&voice=Dream%20Cast&format=hls" + Environment.NewLine);
            File.WriteAllText(
                Path.Combine(seasonDir, "S01E01 - Dream Cast.nfo"),
                NfoBuilder.BuildEpisodeNfo(1, 1, "Frieren", "Existing plot"));

            var input = BuildRefreshStateSeasonInput(fingerprint: "provider-only-fallback-test");
            var expectedEpisodeFileBaseNames = new Dictionary<int, HashSet<string>>
            {
                [1] = new(StringComparer.OrdinalIgnoreCase)
                {
                    "S01E01 - Dream Cast"
                }
            };

            RefreshStateManager.WriteSeasonStateAsync(
                    seriesRoot,
                    input,
                    expectedEpisodeFileBaseNames,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();

            var keys = ExistingLibraryFallbackRefreshInfoLoader.FindExistingCleanKeys(tempRoot);
            AssertTrue(keys.Contains("frieren", StringComparer.OrdinalIgnoreCase), "Existing refresh state should provide fallback title keys when the Yummy user list is unavailable.");

            var refresh = new ExistingLibraryFallbackRefreshInfoLoader()
                .TryLoad(NullLogger.Instance, "frieren", tempRoot, "http://127.0.0.1:8096");

            AssertTrue(refresh is not null, "Existing local library snapshot should be loadable by clean key.");
            AssertEqual("Frieren", refresh!.TitleInfo.Title, "Fallback title should come from tvshow.nfo.");
            AssertEqual(1, refresh.TitleInfo.SeasonNumber, "Fallback season should come from refresh state.");
            AssertEqual(52991L, refresh.TitleInfo.Anime.RemoteIds?.ShikimoriId ?? 0, "Fallback remote id should come from the provider tag.");
            AssertEqual(21008L, refresh.TitleInfo.Anime.AnimeId, "Fallback Yummy anime id should be recovered from existing provider STRM urls.");
            AssertEqual(seasonDir, refresh.Files.SeasonDir, "Fallback season dir should point at the existing generated season.");
            AssertEqual(
                "http://127.0.0.1:8096",
                refresh.Files.BaseUrl,
                "Fallback refresh should generate future STRM files through Jellyfin's internal gateway.");

            var state = ExistingLibraryFallbackRefreshInfoLoader.BuildExistingEpisodeState(seasonDir, 1);
            AssertTrue(state.GeneratedEpisodeNumbers.Contains(1), "Existing STRM files should count as already generated episodes.");
            AssertTrue(
                EpisodeArtifactMaintenance.HasExpectedEpisodeTranslation(state.ExpectedEpisodeTranslationKeys, 1, "Dream Cast"),
                "Existing per-voice files should be treated as expected during provider-only fallback.");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    static void RefreshTitleKeySource_ValidEmptyUserListMarksFetchSucceeded()
    {
        var handler = new DelegatingTestHandler(request =>
        {
            AssertEqual(HttpMethod.Get, request.Method, "Refresh title key source should fetch the configured Yummy user list.");
            AssertEqual("https://yummy.test/users/42/lists/7", request.RequestUri!.AbsoluteUri, "Refresh title key source should request the configured user and list ids.");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"response\":[]}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var yummy = new YummyClient(http, "test-client", "https://yummy.test");
        var source = new RefreshTitleKeySource(NullLogger.Instance, static () => { });
        var cfg = new PluginConfiguration
        {
            UseUserListSubscription = true,
            YummyUserId = 42,
            YummyUserListId = 7,
            Slugs = new List<string>()
        };

        var keys = source.BuildAsync(cfg, yummy, CancellationToken.None).GetAwaiter().GetResult();

        AssertEqual(0, keys.Count, "A valid empty user list should produce no release keys.");
        AssertTrue(source.UserListFetchSucceeded, "A valid empty user-list response must open the stale-cleanup success gate.");
        AssertFalse(source.UserListFetchFailed, "A valid empty user-list response must not be marked as a fetch failure.");
    }
}
