using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using YummyKodik.Alloha;
using YummyKodik.Cvh;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

return await CrosswalkTool.RunAsync(args).ConfigureAwait(false);

internal static class CrosswalkTool
{
    private const string DefaultConfigPath = @"C:\ProgramData\Jellyfin\Server\plugins\configurations\YummyKodik.xml";
    private const string DefaultAllohaTokenPath = @"C:\ProgramData\Jellyfin\Server\plugins\YummyKodik_1.1.0.0\AllohaApiToken.txt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args)
    {
        var options = ParseOptions(args);
        var anchorReportPath = ResolveAnchorReportPath(options.AnchorReportPath);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);
        Directory.CreateDirectory(outputDirectory);

        var anchorReport = await LoadAnchorReportAsync(anchorReportPath).ConfigureAwait(false);
        var anchors = anchorReport.TopCandidates
            .Where(x => !string.IsNullOrWhiteSpace(x.Slug))
            .Take(options.Top)
            .ToArray();

        if (anchors.Length == 0)
        {
            Console.Error.WriteLine("No anchor titles found.");
            return 1;
        }

        var config = LoadConfig(options.ConfigPath, options.AllohaTokenPath);

        using var yummyHttp = CreateDefaultHttpClient();
        using var kodikHttp = CreateDefaultHttpClient();
        using var cvhHttp = CreateDefaultHttpClient();
        using var allohaHttp = CreateUnsafeCertificateHttpClient();

        var yummyClient = new YummyClient(yummyHttp, config.YummyClientId, config.YummyApiBaseUrl);

        string? kodikToken = null;
        string? kodikTokenError = null;
        try
        {
            kodikToken = await KodikTokenResolver.ResolveTokenAsync(kodikHttp).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            kodikTokenError = ex.Message;
        }

        var aliasObservations = new List<AliasObservation>();
        var orthographicObservations = new List<AliasObservation>();
        var titleReports = new List<TitleCrosswalkReport>();

        foreach (var anchor in anchors)
        {
            var anime = await yummyClient.GetAnimeAsync(anchor.Slug, includeVideos: true).ConfigureAwait(false);
            var yummyCoverage = BuildYummyCoverage(anime);

            var titleReport = new TitleCrosswalkReport
            {
                Slug = anchor.Slug,
                Title = anime.Title,
                AnimeId = anime.AnimeId,
                ShikimoriId = anime.RemoteIds?.ShikimoriId ?? 0,
                KpId = anime.RemoteIds?.KpId ?? 0,
                ProviderReports = new List<ProviderCoverageReport>()
            };

            foreach (var providerName in new[] { "kodik", "cvh", "alloha" })
            {
                titleReport.ProviderReports.Add(new ProviderCoverageReport
                {
                    Provider = providerName,
                    YummyCoverage = ProjectCoverage(yummyCoverage.GetValueOrDefault(providerName)),
                    LiveCoverage = Array.Empty<CoverageRow>()
                });
            }

            if (!string.IsNullOrWhiteSpace(kodikToken))
            {
                try
                {
                    var kodikCoverage = await BuildKodikCoverageAsync(anime, kodikHttp, kodikToken).ConfigureAwait(false);
                    ReplaceProviderReport(titleReport.ProviderReports, "kodik", ProjectCoverage(yummyCoverage.GetValueOrDefault("kodik")), ProjectCoverage(kodikCoverage));
                    MatchCoverage(
                        aliasObservations,
                        orthographicObservations,
                        anime.Title,
                        anchor.Slug,
                        "kodik",
                        yummyCoverage.GetValueOrDefault("kodik"),
                        kodikCoverage);
                }
                catch (Exception ex)
                {
                    titleReport.Errors.Add("kodik: " + ex.Message);
                }
            }
            else if (!string.IsNullOrWhiteSpace(kodikTokenError))
            {
                titleReport.Errors.Add("kodik token: " + kodikTokenError);
            }

            try
            {
                var cvhCoverage = await BuildCvhCoverageAsync(anime, cvhHttp).ConfigureAwait(false);
                ReplaceProviderReport(titleReport.ProviderReports, "cvh", ProjectCoverage(yummyCoverage.GetValueOrDefault("cvh")), ProjectCoverage(cvhCoverage));
                MatchCoverage(
                    aliasObservations,
                    orthographicObservations,
                    anime.Title,
                    anchor.Slug,
                    "cvh",
                    yummyCoverage.GetValueOrDefault("cvh"),
                    cvhCoverage);
            }
            catch (Exception ex)
            {
                titleReport.Errors.Add("cvh: " + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(config.AllohaApiToken) && (anime.RemoteIds?.KpId ?? 0) > 0)
            {
                try
                {
                    var allohaCoverage = await BuildAllohaCoverageAsync(anime.RemoteIds!.KpId!.Value, config.AllohaApiToken, allohaHttp).ConfigureAwait(false);
                    ReplaceProviderReport(titleReport.ProviderReports, "alloha", ProjectCoverage(yummyCoverage.GetValueOrDefault("alloha")), ProjectCoverage(allohaCoverage));
                    MatchCoverage(
                        aliasObservations,
                        orthographicObservations,
                        anime.Title,
                        anchor.Slug,
                        "alloha",
                        yummyCoverage.GetValueOrDefault("alloha"),
                        allohaCoverage);
                }
                catch (Exception ex)
                {
                    titleReport.Errors.Add("alloha: " + ex.Message);
                }
            }
            else
            {
                titleReport.Errors.Add("alloha token missing or kpId unavailable.");
            }

            titleReports.Add(titleReport);
        }

        var strongAliases = BuildAliasSuggestions(aliasObservations);
        var orthographicVariants = BuildAliasSuggestions(orthographicObservations);

        var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(outputDirectory, $"multi-anchor-crosswalk-{timestamp}.json");
        var markdownPath = Path.Combine(outputDirectory, $"multi-anchor-crosswalk-{timestamp}.md");

        var report = new MultiAnchorCrosswalkReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            AnchorReportPath = anchorReportPath,
            AnchorCount = anchors.Length,
            KodikTokenResolved = !string.IsNullOrWhiteSpace(kodikToken),
            KodikTokenError = kodikTokenError ?? string.Empty,
            StrongAliases = strongAliases,
            OrthographicVariants = orthographicVariants,
            Titles = titleReports
        };

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report)).ConfigureAwait(false);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            report.GeneratedAtUtc,
            report.AnchorCount,
            report.KodikTokenResolved,
            report.KodikTokenError,
            strongAliasCount = report.StrongAliases.Length,
            orthographicVariantCount = report.OrthographicVariants.Length,
            outputFiles = new
            {
                json = jsonPath,
                markdown = markdownPath
            }
        }, JsonOptions));

        return 0;
    }

    private static ToolOptions ParseOptions(string[] args)
    {
        var anchorReportPath = string.Empty;
        var outputDirectory = Path.Combine(GetRepositoryRoot(), "artifacts");
        var configPath = DefaultConfigPath;
        var allohaTokenPath = DefaultAllohaTokenPath;
        var top = 10;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--anchor-report":
                    anchorReportPath = args[++i];
                    break;
                case "--output-dir":
                    outputDirectory = args[++i];
                    break;
                case "--top":
                    top = int.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "--config":
                    configPath = args[++i];
                    break;
                case "--alloha-token":
                    allohaTokenPath = args[++i];
                    break;
                default:
                    throw new InvalidOperationException("Unknown argument: " + args[i]);
            }
        }

        if (top <= 0)
        {
            top = 10;
        }

        return new ToolOptions(anchorReportPath, outputDirectory, top, configPath, allohaTokenPath);
    }

    private static string ResolveAnchorReportPath(string anchorReportPath)
    {
        if (!string.IsNullOrWhiteSpace(anchorReportPath))
        {
            return Path.GetFullPath(anchorReportPath);
        }

        var artifactsDirectory = Path.Combine(GetRepositoryRoot(), "artifacts");
        var latest = new DirectoryInfo(Path.GetFullPath(artifactsDirectory))
            .GetFiles("yummy-crosswalk-anchor-*.json")
            .OrderByDescending(x => x.LastWriteTimeUtc)
            .FirstOrDefault();

        if (latest == null)
        {
            throw new FileNotFoundException("Could not find yummy-crosswalk-anchor report in artifacts.");
        }

        return latest.FullName;
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from tool base directory.");
    }

    private static async Task<AnchorReportModel> LoadAnchorReportAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var report = await JsonSerializer.DeserializeAsync<AnchorReportModel>(stream, JsonOptions).ConfigureAwait(false);
        return report ?? throw new InvalidOperationException("Failed to deserialize anchor report.");
    }

    private static ToolConfig LoadConfig(string configPath, string allohaTokenPath)
    {
        var config = new ToolConfig();
        if (File.Exists(configPath))
        {
            var doc = new System.Xml.XmlDocument();
            doc.Load(configPath);
            var root = doc.DocumentElement;
            config.YummyApiBaseUrl = root?.SelectSingleNode("YummyApiBaseUrl")?.InnerText?.Trim() ?? "https://api.yani.tv";
            config.YummyClientId = root?.SelectSingleNode("YummyClientId")?.InnerText?.Trim() ?? string.Empty;
        }

        if (File.Exists(allohaTokenPath))
        {
            config.AllohaApiToken = File.ReadAllText(allohaTokenPath).Trim();
        }

        return config;
    }

    private static HttpClient CreateDefaultHttpClient()
    {
        return new HttpClient(new HttpClientHandler(), disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    private static HttpClient CreateUnsafeCertificateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = static (_, _, _, _) => true
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
    }

    private static Dictionary<string, Dictionary<string, HashSet<int>>> BuildYummyCoverage(YummyAnimeResponse anime)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<int>>>(StringComparer.Ordinal)
        {
            ["kodik"] = new(StringComparer.OrdinalIgnoreCase),
            ["cvh"] = new(StringComparer.OrdinalIgnoreCase),
            ["alloha"] = new(StringComparer.OrdinalIgnoreCase)
        };

        foreach (var video in anime.Videos ?? Enumerable.Empty<YummyVideoItem>())
        {
            if (!int.TryParse((video.Number ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode) ||
                episode <= 0 ||
                video.Data == null)
            {
                continue;
            }

            var provider = video.Data.PlayerId switch
            {
                4 => "kodik",
                3 => "cvh",
                2 => "alloha",
                _ => string.Empty
            };

            if (provider.Length == 0)
            {
                continue;
            }

            var voice = YummyVideoCatalog.NormalizeVoiceName(video.Data.Dubbing);
            AddCoverage(result[provider], voice, episode);
        }

        return result;
    }

    private static async Task<Dictionary<string, HashSet<int>>> BuildKodikCoverageAsync(
        YummyAnimeResponse anime,
        HttpClient httpClient,
        string token)
    {
        var remoteIds = anime.RemoteIds ?? new YummyRemoteIds();
        var (id, idType) = remoteIds.ShikimoriId switch
        {
            > 0 => (remoteIds.ShikimoriId!.Value.ToString(CultureInfo.InvariantCulture), KodikIdType.Shikimori),
            _ when remoteIds.KpId > 0 => (remoteIds.KpId!.Value.ToString(CultureInfo.InvariantCulture), KodikIdType.Kinopoisk),
            _ when !string.IsNullOrWhiteSpace(remoteIds.ImdbId) => (remoteIds.ImdbId!, KodikIdType.Imdb),
            _ => throw new InvalidOperationException("Anime does not have a Kodik-compatible remote id.")
        };

        var client = new KodikClient(httpClient, token, logger: null, isHttpLogEnabled: static () => false);
        var info = await client.GetAnimeInfoAsync(id, idType).ConfigureAwait(false);

        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var translation in info.Translations)
        {
            var voice = YummyVideoCatalog.NormalizeVoiceName(translation.Name);
            if (string.IsNullOrWhiteSpace(voice))
            {
                continue;
            }

            var episodes = translation.AvailableEpisodes.Count > 0
                ? translation.AvailableEpisodes
                : translation.MaxEpisode > 0
                    ? Enumerable.Range(1, translation.MaxEpisode).ToArray()
                    : Array.Empty<int>();

            foreach (var episode in episodes)
            {
                if (episode > 0)
                {
                    AddCoverage(result, voice, episode);
                }
            }
        }

        return result;
    }

    private static async Task<Dictionary<string, HashSet<int>>> BuildCvhCoverageAsync(
        YummyAnimeResponse anime,
        HttpClient httpClient)
    {
        var source = FindFirstCvhSource(anime) ?? throw new InvalidOperationException("No CVH source found in Yummy videos.");
        var client = new CvhClient(httpClient);
        var playlist = await client.GetPlaylistAsync(source, CancellationToken.None).ConfigureAwait(false);

        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in playlist.Items)
        {
            if (item.Episode <= 0)
            {
                continue;
            }

            var voice = YummyVideoCatalog.NormalizeVoiceName(item.VoiceStudio);
            AddCoverage(result, voice, item.Episode);
        }

        return result;
    }

    private static async Task<Dictionary<string, HashSet<int>>> BuildAllohaCoverageAsync(
        long kpId,
        string apiToken,
        HttpClient httpClient)
    {
        var client = new AllohaApiClient(httpClient, apiToken);
        var entries = await client.GetCatalogEntriesByKpAsync(kpId).ConfigureAwait(false);

        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (entry.EpisodeNumber <= 0)
            {
                continue;
            }

            var voice = YummyVideoCatalog.NormalizeVoiceName(entry.RawDubbing);
            AddCoverage(result, voice, entry.EpisodeNumber);
        }

        return result;
    }

    private static void MatchCoverage(
        List<AliasObservation> aliasObservations,
        List<AliasObservation> orthographicObservations,
        string title,
        string slug,
        string provider,
        Dictionary<string, HashSet<int>>? yummyCoverage,
        Dictionary<string, HashSet<int>> nativeCoverage)
    {
        if (yummyCoverage == null || yummyCoverage.Count == 0 || nativeCoverage.Count == 0)
        {
            return;
        }

        foreach (var nativePair in nativeCoverage)
        {
            var nativeSignature = BuildEpisodeSignature(nativePair.Value);
            if (nativeSignature.Length == 0)
            {
                continue;
            }

            var yummyMatches = yummyCoverage
                .Where(x => string.Equals(BuildEpisodeSignature(x.Value), nativeSignature, StringComparison.Ordinal))
                .ToArray();
            if (yummyMatches.Length != 1)
            {
                continue;
            }

            var yummyPair = yummyMatches[0];
            var reverseMatches = nativeCoverage
                .Where(x => string.Equals(BuildEpisodeSignature(x.Value), nativeSignature, StringComparison.Ordinal))
                .ToArray();
            if (reverseMatches.Length != 1)
            {
                continue;
            }

            var nativeName = nativePair.Key.Trim();
            var yummyName = yummyPair.Key.Trim();
            if (nativeName.Length == 0 || yummyName.Length == 0)
            {
                continue;
            }

            var nativeKey = TranslationNameKeyNormalizer.Normalize(nativeName);
            var yummyKey = TranslationNameKeyNormalizer.Normalize(yummyName);
            if (nativeKey.Length == 0 || yummyKey.Length == 0)
            {
                continue;
            }

            if (string.Equals(nativeKey, yummyKey, StringComparison.Ordinal))
            {
                if (!string.Equals(nativeName, yummyName, StringComparison.OrdinalIgnoreCase))
                {
                    orthographicObservations.Add(new AliasObservation
                    {
                        Provider = provider,
                        NativeName = nativeName,
                        NativeKey = nativeKey,
                        YummyName = yummyName,
                        YummyKey = yummyKey,
                        Title = title,
                        Slug = slug,
                        EpisodeCount = nativePair.Value.Count
                    });
                }

                continue;
            }

            aliasObservations.Add(new AliasObservation
            {
                Provider = provider,
                NativeName = nativeName,
                NativeKey = nativeKey,
                YummyName = yummyName,
                YummyKey = yummyKey,
                Title = title,
                Slug = slug,
                EpisodeCount = nativePair.Value.Count
            });
        }
    }

    private static AliasSuggestion[] BuildAliasSuggestions(IEnumerable<AliasObservation> observations)
    {
        return observations
            .GroupBy(x => BuildObservationKey(x), StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                return new AliasSuggestion
                {
                    Provider = first.Provider,
                    NativeName = group.Select(x => x.NativeName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First(),
                    NativeKey = first.NativeKey,
                    YummyName = group.Select(x => x.YummyName).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).First(),
                    YummyKey = first.YummyKey,
                    Count = group.Count(),
                    Titles = group.Select(x => x.Title).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray()
                };
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Provider, StringComparer.Ordinal)
            .ThenBy(x => x.NativeName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string BuildObservationKey(AliasObservation observation)
    {
        return string.Join("\t", observation.Provider, observation.NativeKey, observation.YummyKey);
    }

    private static void ReplaceProviderReport(
        List<ProviderCoverageReport> reports,
        string provider,
        CoverageRow[] yummyCoverage,
        CoverageRow[] liveCoverage)
    {
        var report = reports.FirstOrDefault(x => string.Equals(x.Provider, provider, StringComparison.Ordinal));
        if (report == null)
        {
            return;
        }

        report.YummyCoverage = yummyCoverage;
        report.LiveCoverage = liveCoverage;
    }

    private static CoverageRow[] ProjectCoverage(Dictionary<string, HashSet<int>>? coverage)
    {
        if (coverage == null || coverage.Count == 0)
        {
            return Array.Empty<CoverageRow>();
        }

        return coverage
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => new CoverageRow
            {
                Name = x.Key,
                Key = TranslationNameKeyNormalizer.Normalize(x.Key),
                Episodes = x.Value.OrderBy(ep => ep).ToArray()
            })
            .ToArray();
    }

    private static void AddCoverage(Dictionary<string, HashSet<int>> coverage, string voiceName, int episode)
    {
        var normalizedVoice = YummyVideoCatalog.NormalizeVoiceName(voiceName);
        if (string.IsNullOrWhiteSpace(normalizedVoice) || episode <= 0)
        {
            return;
        }

        if (!coverage.TryGetValue(normalizedVoice, out var episodes))
        {
            episodes = new HashSet<int>();
            coverage[normalizedVoice] = episodes;
        }

        episodes.Add(episode);
    }

    private static YummyCvhSource? FindFirstCvhSource(YummyAnimeResponse anime)
    {
        foreach (var video in anime.Videos ?? Enumerable.Empty<YummyVideoItem>())
        {
            if (video.Data?.PlayerId != 3)
            {
                continue;
            }

            var normalizedUrl = NormalizeUrl(video.IframeUrl);
            if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
            {
                continue;
            }

            var query = ParseQuery(uri.Query);
            if (!query.TryGetValue("anime_id", out var animeIdRaw) ||
                !long.TryParse(animeIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var animeId) ||
                animeId <= 0)
            {
                continue;
            }

            if (!query.TryGetValue("episode", out var episodeRaw) ||
                !int.TryParse(episodeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var episode) ||
                episode <= 0)
            {
                continue;
            }

            query.TryGetValue("dubbing_code", out var dubbingCode);
            query.TryGetValue("dubbing", out var dubbingName);

            return new YummyCvhSource
            {
                AnimeId = animeId,
                EpisodeNumber = episode,
                DubbingCode = (dubbingCode ?? string.Empty).Trim(),
                DubbingName = YummyVideoCatalog.NormalizeVoiceName(dubbingName)
            };
        }

        return null;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var value = (query ?? string.Empty).TrimStart('?');
        if (value.Length == 0)
        {
            return result;
        }

        foreach (var part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = part.IndexOf('=');
            if (index <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(part.Substring(0, index).Replace("+", " ", StringComparison.Ordinal));
            var val = Uri.UnescapeDataString(part[(index + 1)..].Replace("+", " ", StringComparison.Ordinal));
            result[key] = val;
        }

        return result;
    }

    private static string NormalizeUrl(string url)
    {
        return url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url.Trim();
    }

    private static string BuildEpisodeSignature(HashSet<int> episodes)
    {
        return string.Join(",", episodes.OrderBy(x => x));
    }

    private static string BuildMarkdown(MultiAnchorCrosswalkReport report)
    {
        var lines = new List<string>
        {
            "# Multi-Anchor Crosswalk",
            string.Empty,
            $"Generated: {report.GeneratedAtUtc:O}",
            $"AnchorReportPath: {report.AnchorReportPath}",
            $"AnchorCount: {report.AnchorCount}",
            $"KodikTokenResolved: {report.KodikTokenResolved}",
            $"KodikTokenError: {report.KodikTokenError}",
            string.Empty,
            "## Strong Aliases",
            string.Empty
        };

        foreach (var alias in report.StrongAliases)
        {
            lines.Add($"- [{alias.Provider}] {alias.NativeName} -> {alias.YummyName} ({alias.Count})");
        }

        lines.Add(string.Empty);
        lines.Add("## Orthographic Variants");
        lines.Add(string.Empty);

        foreach (var alias in report.OrthographicVariants)
        {
            lines.Add($"- [{alias.Provider}] {alias.NativeName} -> {alias.YummyName} ({alias.Count})");
        }

        lines.Add(string.Empty);
        lines.Add("## Titles");
        lines.Add(string.Empty);

        foreach (var title in report.Titles)
        {
            lines.Add($"### {title.Title}");
            lines.Add($"- slug: {title.Slug}");
            lines.Add($"- animeId: {title.AnimeId}");
            lines.Add($"- shikimoriId: {title.ShikimoriId}");
            lines.Add($"- kpId: {title.KpId}");
            if (title.Errors.Count > 0)
            {
                lines.Add($"- errors: {string.Join("; ", title.Errors)}");
            }

            foreach (var provider in title.ProviderReports)
            {
                lines.Add($"- {provider.Provider}: yummy={provider.YummyCoverage.Length}, live={provider.LiveCoverage.Length}");
            }

            lines.Add(string.Empty);
        }

        return string.Join(Environment.NewLine, lines);
    }

    private sealed record ToolOptions(
        string AnchorReportPath,
        string OutputDirectory,
        int Top,
        string ConfigPath,
        string AllohaTokenPath);

    private sealed class ToolConfig
    {
        public string YummyApiBaseUrl { get; set; } = "https://api.yani.tv";
        public string YummyClientId { get; set; } = string.Empty;
        public string AllohaApiToken { get; set; } = string.Empty;
    }

    private sealed class AnchorReportModel
    {
        public AnchorCandidateModel[] TopCandidates { get; set; } = Array.Empty<AnchorCandidateModel>();
    }

    private sealed class AnchorCandidateModel
    {
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public long AnimeId { get; set; }
        public long KpId { get; set; }
        public long ShikimoriId { get; set; }
    }

    private sealed class AliasObservation
    {
        public string Provider { get; set; } = string.Empty;
        public string NativeName { get; set; } = string.Empty;
        public string NativeKey { get; set; } = string.Empty;
        public string YummyName { get; set; } = string.Empty;
        public string YummyKey { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Slug { get; set; } = string.Empty;
        public int EpisodeCount { get; set; }
    }

    private sealed class AliasSuggestion
    {
        public string Provider { get; set; } = string.Empty;
        public string NativeName { get; set; } = string.Empty;
        public string NativeKey { get; set; } = string.Empty;
        public string YummyName { get; set; } = string.Empty;
        public string YummyKey { get; set; } = string.Empty;
        public int Count { get; set; }
        public string[] Titles { get; set; } = Array.Empty<string>();
    }

    private sealed class MultiAnchorCrosswalkReport
    {
        public DateTime GeneratedAtUtc { get; set; }
        public string AnchorReportPath { get; set; } = string.Empty;
        public int AnchorCount { get; set; }
        public bool KodikTokenResolved { get; set; }
        public string KodikTokenError { get; set; } = string.Empty;
        public AliasSuggestion[] StrongAliases { get; set; } = Array.Empty<AliasSuggestion>();
        public AliasSuggestion[] OrthographicVariants { get; set; } = Array.Empty<AliasSuggestion>();
        public List<TitleCrosswalkReport> Titles { get; set; } = new();
    }

    private sealed class TitleCrosswalkReport
    {
        public string Slug { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public long AnimeId { get; set; }
        public long ShikimoriId { get; set; }
        public long KpId { get; set; }
        public List<string> Errors { get; set; } = new();
        public List<ProviderCoverageReport> ProviderReports { get; set; } = new();
    }

    private sealed class ProviderCoverageReport
    {
        public string Provider { get; set; } = string.Empty;
        public CoverageRow[] YummyCoverage { get; set; } = Array.Empty<CoverageRow>();
        public CoverageRow[] LiveCoverage { get; set; } = Array.Empty<CoverageRow>();
    }

    private sealed class CoverageRow
    {
        public string Name { get; set; } = string.Empty;
        public string Key { get; set; } = string.Empty;
        public int[] Episodes { get; set; } = Array.Empty<int>();
    }
}
