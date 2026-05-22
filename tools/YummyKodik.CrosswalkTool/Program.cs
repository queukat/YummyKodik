using System.Globalization;
using System.Text.Json;
using YummyKodik.Alloha;
using YummyKodik.Cvh;
using YummyKodik.Kodik;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.CrosswalkTool;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        return await CrosswalkTool.RunAsync(args).ConfigureAwait(false);
    }
}

internal static class CrosswalkTool
{
    private const string ProviderKodik = "kodik";
    private const string ProviderCvh = "cvh";
    private const string ProviderAlloha = "alloha";
    private const int KodikPlayerId = 4;
    private const int CvhPlayerId = 3;
    private const int AllohaPlayerId = 2;
    private const string DefaultAllohaPluginDirectoryName = "YummyKodik_1.1.0.0";
    private const string DefaultYummyApiHost = "api.yani.tv";

    private static readonly string[] ProviderNames =
    {
        ProviderKodik,
        ProviderCvh,
        ProviderAlloha
    };

    private static readonly string DefaultConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Jellyfin",
        "Server",
        "plugins",
        "configurations",
        "YummyKodik.xml");

    private static readonly string DefaultAllohaTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Jellyfin",
        "Server",
        "plugins",
        DefaultAllohaPluginDirectoryName,
        "AllohaApiToken.txt");

    private static readonly string DefaultYummyApiBaseUrl =
        new UriBuilder(Uri.UriSchemeHttps, DefaultYummyApiHost).Uri.GetLeftPart(UriPartial.Authority);

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
        var anchorSlugs = anchorReport.TopCandidates
            .Select(anchor => anchor.Slug)
            .Where(slug => !string.IsNullOrWhiteSpace(slug))
            .Take(options.Top)
            .ToArray();

        if (anchorSlugs.Length == 0)
        {
            await Console.Error.WriteLineAsync("No anchor titles found.").ConfigureAwait(false);
            return 1;
        }

        var config = LoadConfig(options.ConfigPath, options.AllohaTokenPath);

        using var yummyHttp = CreateDefaultHttpClient();
        using var kodikHttp = CreateDefaultHttpClient();
        using var cvhHttp = CreateDefaultHttpClient();
        using var allohaHttp = CreateDefaultHttpClient();

        var yummyClient = new YummyClient(yummyHttp, config.YummyClientId, config.YummyApiBaseUrl);

        var kodikToken = await ResolveKodikTokenAsync(kodikHttp).ConfigureAwait(false);

        var aliasObservations = new List<AliasObservation>();
        var orthographicObservations = new List<AliasObservation>();
        var titleReports = new List<TitleCrosswalkReport>();

        foreach (var anchorSlug in anchorSlugs)
        {
            var anime = await yummyClient.GetAnimeAsync(anchorSlug, includeVideos: true).ConfigureAwait(false);
            var yummyCoverage = BuildYummyCoverage(anime);
            var titleReport = CreateTitleReport(anchorSlug, anime, yummyCoverage);
            var coverageContext = new TitleCoverageContext(titleReport, aliasObservations, orthographicObservations, anime.Title, anchorSlug, yummyCoverage);

            await AddKodikCoverageAsync(coverageContext, anime, kodikHttp, kodikToken).ConfigureAwait(false);
            await AddCvhCoverageAsync(coverageContext, anime, cvhHttp).ConfigureAwait(false);
            await AddAllohaCoverageAsync(coverageContext, anime, config, allohaHttp).ConfigureAwait(false);

            titleReports.Add(titleReport);
        }

        var strongAliases = BuildAliasSuggestions(aliasObservations);
        var orthographicVariants = BuildAliasSuggestions(orthographicObservations);

        var report = new MultiAnchorCrosswalkReport
        {
            GeneratedAtUtc = DateTime.UtcNow,
            AnchorReportPath = anchorReportPath,
            AnchorCount = anchorSlugs.Length,
            KodikTokenResolved = !string.IsNullOrWhiteSpace(kodikToken.Token),
            KodikTokenError = kodikToken.Error ?? string.Empty,
            StrongAliases = strongAliases,
            OrthographicVariants = orthographicVariants,
            Titles = titleReports
        };

        var outputFiles = await WriteReportAsync(outputDirectory, report).ConfigureAwait(false);

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new
        {
            report.GeneratedAtUtc,
            report.AnchorCount,
            report.KodikTokenResolved,
            report.KodikTokenError,
            strongAliasCount = report.StrongAliases.Length,
            orthographicVariantCount = report.OrthographicVariants.Length,
            outputFiles = new
            {
                json = outputFiles.JsonPath,
                markdown = outputFiles.MarkdownPath
            }
        }, JsonOptions)).ConfigureAwait(false);

        return 0;
    }

    private static async Task<KodikTokenResult> ResolveKodikTokenAsync(HttpClient httpClient)
    {
        try
        {
            var token = await KodikTokenResolver.ResolveTokenAsync(httpClient).ConfigureAwait(false);
            return new KodikTokenResult(token, Error: null);
        }
        catch (Exception ex)
        {
            return new KodikTokenResult(Token: null, ex.Message);
        }
    }

    private static TitleCrosswalkReport CreateTitleReport(
        string slug,
        YummyAnimeResponse anime,
        Dictionary<string, Dictionary<string, HashSet<int>>> yummyCoverage)
    {
        return new TitleCrosswalkReport
        {
            Slug = slug,
            Title = anime.Title,
            AnimeId = anime.AnimeId,
            ShikimoriId = anime.RemoteIds?.ShikimoriId ?? 0,
            KpId = anime.RemoteIds?.KpId ?? 0,
            ProviderReports = ProviderNames
                .Select(providerName => new ProviderCoverageReport
                {
                    Provider = providerName,
                    YummyCoverage = ProjectCoverage(yummyCoverage.GetValueOrDefault(providerName)),
                    LiveCoverage = Array.Empty<CoverageRow>()
                })
                .ToList()
        };
    }

    private static async Task AddKodikCoverageAsync(
        TitleCoverageContext context,
        YummyAnimeResponse anime,
        HttpClient httpClient,
        KodikTokenResult kodikToken)
    {
        if (string.IsNullOrWhiteSpace(kodikToken.Token))
        {
            if (!string.IsNullOrWhiteSpace(kodikToken.Error))
            {
                context.TitleReport.Errors.Add(ProviderKodik + " token: " + kodikToken.Error);
            }

            return;
        }

        try
        {
            var liveCoverage = await BuildKodikCoverageAsync(anime, httpClient, kodikToken.Token).ConfigureAwait(false);
            UpdateProviderCoverage(context, ProviderKodik, liveCoverage);
        }
        catch (Exception ex)
        {
            context.TitleReport.Errors.Add(ProviderKodik + ": " + ex.Message);
        }
    }

    private static async Task AddCvhCoverageAsync(
        TitleCoverageContext context,
        YummyAnimeResponse anime,
        HttpClient httpClient)
    {
        try
        {
            var liveCoverage = await BuildCvhCoverageAsync(anime, httpClient).ConfigureAwait(false);
            UpdateProviderCoverage(context, ProviderCvh, liveCoverage);
        }
        catch (Exception ex)
        {
            context.TitleReport.Errors.Add(ProviderCvh + ": " + ex.Message);
        }
    }

    private static async Task AddAllohaCoverageAsync(
        TitleCoverageContext context,
        YummyAnimeResponse anime,
        ToolConfig config,
        HttpClient httpClient)
    {
        if (!TryGetAllohaKpId(config, anime, out var kpId))
        {
            context.TitleReport.Errors.Add(ProviderAlloha + " token missing or kpId unavailable.");
            return;
        }

        try
        {
            var liveCoverage = await BuildAllohaCoverageAsync(kpId, config.AllohaApiToken, httpClient).ConfigureAwait(false);
            UpdateProviderCoverage(context, ProviderAlloha, liveCoverage);
        }
        catch (Exception ex)
        {
            context.TitleReport.Errors.Add(ProviderAlloha + ": " + ex.Message);
        }
    }

    private static bool TryGetAllohaKpId(ToolConfig config, YummyAnimeResponse anime, out long kpId)
    {
        kpId = anime.RemoteIds?.KpId ?? 0;
        return !string.IsNullOrWhiteSpace(config.AllohaApiToken) && kpId > 0;
    }

    private static void UpdateProviderCoverage(
        TitleCoverageContext context,
        string provider,
        Dictionary<string, HashSet<int>> liveCoverage)
    {
        var providerYummyCoverage = context.YummyCoverage.GetValueOrDefault(provider);
        ReplaceProviderReport(context.TitleReport.ProviderReports, provider, ProjectCoverage(providerYummyCoverage), ProjectCoverage(liveCoverage));
        MatchCoverage(context, provider, providerYummyCoverage, liveCoverage);
    }

    private static async Task<ReportOutputFiles> WriteReportAsync(string outputDirectory, MultiAnchorCrosswalkReport report)
    {
        var timestamp = report.GeneratedAtUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var jsonPath = Path.Combine(outputDirectory, $"multi-anchor-crosswalk-{timestamp}.json");
        var markdownPath = Path.Combine(outputDirectory, $"multi-anchor-crosswalk-{timestamp}.md");

        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions)).ConfigureAwait(false);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdown(report)).ConfigureAwait(false);

        return new ReportOutputFiles(jsonPath, markdownPath);
    }

    private static ToolOptions ParseOptions(string[] args)
    {
        var anchorReportPath = string.Empty;
        var outputDirectory = Path.Combine(GetRepositoryRoot(), "artifacts");
        var configPath = DefaultConfigPath;
        var allohaTokenPath = DefaultAllohaTokenPath;
        var top = 10;

        var i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "--anchor-report":
                    anchorReportPath = ReadOptionValue(args, ref i);
                    break;
                case "--output-dir":
                    outputDirectory = ReadOptionValue(args, ref i);
                    break;
                case "--top":
                    top = int.Parse(ReadOptionValue(args, ref i), CultureInfo.InvariantCulture);
                    break;
                case "--config":
                    configPath = ReadOptionValue(args, ref i);
                    break;
                case "--alloha-token":
                    allohaTokenPath = ReadOptionValue(args, ref i);
                    break;
                default:
                    throw new InvalidOperationException("Unknown argument: " + args[i]);
            }

            i++;
        }

        if (top <= 0)
        {
            top = 10;
        }

        return new ToolOptions(anchorReportPath, outputDirectory, top, configPath, allohaTokenPath);
    }

    private static string ReadOptionValue(string[] args, ref int optionIndex)
    {
        var valueIndex = optionIndex + 1;
        if (valueIndex >= args.Length)
        {
            throw new InvalidOperationException("Missing value for argument: " + args[optionIndex]);
        }

        optionIndex = valueIndex;
        return args[valueIndex];
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
            config.YummyApiBaseUrl = root?.SelectSingleNode("YummyApiBaseUrl")?.InnerText?.Trim() ?? DefaultYummyApiBaseUrl;
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

    private static Dictionary<string, Dictionary<string, HashSet<int>>> BuildYummyCoverage(YummyAnimeResponse anime)
    {
        var result = new Dictionary<string, Dictionary<string, HashSet<int>>>(StringComparer.Ordinal)
        {
            [ProviderKodik] = new(StringComparer.OrdinalIgnoreCase),
            [ProviderCvh] = new(StringComparer.OrdinalIgnoreCase),
            [ProviderAlloha] = new(StringComparer.OrdinalIgnoreCase)
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
                KodikPlayerId => ProviderKodik,
                CvhPlayerId => ProviderCvh,
                AllohaPlayerId => ProviderAlloha,
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

            var episodes = GetAvailableEpisodes(translation);

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

    private static IReadOnlyCollection<int> GetAvailableEpisodes(KodikTranslation translation)
    {
        if (translation.AvailableEpisodes.Count > 0)
        {
            return translation.AvailableEpisodes;
        }

        return translation.MaxEpisode > 0
            ? Enumerable.Range(1, translation.MaxEpisode).ToArray()
            : Array.Empty<int>();
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
        TitleCoverageContext context,
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
            var match = TryCreateAliasMatch(context, provider, nativePair, yummyCoverage, nativeCoverage);
            if (match == null)
            {
                continue;
            }

            if (match.IsOrthographicVariant)
            {
                context.OrthographicObservations.Add(match.Observation);
                continue;
            }

            context.AliasObservations.Add(match.Observation);
        }
    }

    private static AliasMatch? TryCreateAliasMatch(
        TitleCoverageContext context,
        string provider,
        KeyValuePair<string, HashSet<int>> nativePair,
        Dictionary<string, HashSet<int>> yummyCoverage,
        Dictionary<string, HashSet<int>> nativeCoverage)
    {
        var nativeSignature = BuildEpisodeSignature(nativePair.Value);
        if (nativeSignature.Length == 0)
        {
            return null;
        }

        var yummyMatches = FindCoverageMatches(yummyCoverage, nativeSignature);
        if (yummyMatches.Length != 1 || FindCoverageMatches(nativeCoverage, nativeSignature).Length != 1)
        {
            return null;
        }

        if (!TryCreateNamePair(nativePair.Key, yummyMatches[0].Key, out var namePair))
        {
            return null;
        }

        var isOrthographicVariant = string.Equals(namePair.NativeKey, namePair.YummyKey, StringComparison.Ordinal);
        if (isOrthographicVariant && string.Equals(namePair.NativeName, namePair.YummyName, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var observation = CreateAliasObservation(context, provider, namePair, nativePair.Value.Count);
        return new AliasMatch(observation, isOrthographicVariant);
    }

    private static KeyValuePair<string, HashSet<int>>[] FindCoverageMatches(
        Dictionary<string, HashSet<int>> coverage,
        string signature)
    {
        return coverage
            .Where(x => string.Equals(BuildEpisodeSignature(x.Value), signature, StringComparison.Ordinal))
            .ToArray();
    }

    private static bool TryCreateNamePair(
        string nativeRawName,
        string yummyRawName,
        out TranslationNamePair namePair)
    {
        var nativeName = nativeRawName.Trim();
        var yummyName = yummyRawName.Trim();
        namePair = default;

        if (nativeName.Length == 0 || yummyName.Length == 0)
        {
            return false;
        }

        var nativeKey = TranslationNameKeyNormalizer.Normalize(nativeName);
        var yummyKey = TranslationNameKeyNormalizer.Normalize(yummyName);
        if (nativeKey.Length == 0 || yummyKey.Length == 0)
        {
            return false;
        }

        namePair = new TranslationNamePair(nativeName, nativeKey, yummyName, yummyKey);
        return true;
    }

    private static AliasObservation CreateAliasObservation(
        TitleCoverageContext context,
        string provider,
        TranslationNamePair namePair,
        int episodeCount)
    {
        return new AliasObservation
        {
            Provider = provider,
            NativeName = namePair.NativeName,
            NativeKey = namePair.NativeKey,
            YummyName = namePair.YummyName,
            YummyKey = namePair.YummyKey,
            Title = context.Title,
            Slug = context.Slug,
            EpisodeCount = episodeCount
        };
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
            if (TryCreateCvhSource(video, out var source))
            {
                return source;
            }
        }

        return null;
    }

    private static bool TryCreateCvhSource(YummyVideoItem video, out YummyCvhSource? source)
    {
        source = null;
        if (video.Data?.PlayerId != CvhPlayerId ||
            !TryParseQuery(video.IframeUrl, out var query) ||
            !TryGetPositiveInt64QueryValue(query, "anime_id", out var animeId) ||
            !TryGetPositiveInt32QueryValue(query, "episode", out var episode))
        {
            return false;
        }

        query.TryGetValue("dubbing_code", out var dubbingCode);
        query.TryGetValue("dubbing", out var dubbingName);

        source = new YummyCvhSource
        {
            AnimeId = animeId,
            EpisodeNumber = episode,
            DubbingCode = (dubbingCode ?? string.Empty).Trim(),
            DubbingName = YummyVideoCatalog.NormalizeVoiceName(dubbingName)
        };

        return true;
    }

    private static bool TryParseQuery(string url, out Dictionary<string, string> query)
    {
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var normalizedUrl = NormalizeUrl(url);
        if (!Uri.TryCreate(normalizedUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        query = ParseQuery(uri.Query);
        return true;
    }

    private static bool TryGetPositiveInt64QueryValue(Dictionary<string, string> query, string key, out long value)
    {
        value = 0;
        return query.TryGetValue(key, out var raw) &&
            long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value > 0;
    }

    private static bool TryGetPositiveInt32QueryValue(Dictionary<string, string> query, string key, out int value)
    {
        value = 0;
        return query.TryGetValue(key, out var raw) &&
            int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
            value > 0;
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

    private sealed record KodikTokenResult(string? Token, string? Error);

    private sealed record ReportOutputFiles(string JsonPath, string MarkdownPath);

    private sealed record TitleCoverageContext(
        TitleCrosswalkReport TitleReport,
        List<AliasObservation> AliasObservations,
        List<AliasObservation> OrthographicObservations,
        string Title,
        string Slug,
        Dictionary<string, Dictionary<string, HashSet<int>>> YummyCoverage);

    private sealed record AliasMatch(AliasObservation Observation, bool IsOrthographicVariant);

    private readonly record struct TranslationNamePair(
        string NativeName,
        string NativeKey,
        string YummyName,
        string YummyKey);

    private sealed class ToolConfig
    {
        public string YummyApiBaseUrl { get; set; } = DefaultYummyApiBaseUrl;
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
