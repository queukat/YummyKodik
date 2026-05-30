using Microsoft.Extensions.Logging;
using YummyKodik.Alloha;
using YummyKodik.Configuration;
using YummyKodik.Shikimori;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class YummyRefreshInfoLoader
{
    private readonly IHttpClientFactory _httpClientFactory;

    public YummyRefreshInfoLoader(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<YummyRefreshInfo> LoadAsync(
        ILogger logger,
        PluginConfiguration cfg,
        RefreshClients clients,
        string cleanKey,
        string root,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        YummyAnimeResponse anime;
        using (perf.Measure("stage.yummy.fetch"))
        {
            anime = await clients.Yummy.GetAnimeAsync(cleanKey, includeVideos: true, cancellationToken).ConfigureAwait(false);
        }

        var titleInfo = await ResolveYummyAnimeTitleInfoAsync(logger, anime, cleanKey, clients.Shikimori, perf, cancellationToken)
            .ConfigureAwait(false);
        var videoCatalog = await LoadYummyVideoCatalogAsync(logger, cfg, titleInfo, perf, cancellationToken).ConfigureAwait(false);
        var files = BuildSeriesFileInfo(logger, root, titleInfo, cfg);
        var availability = BuildEpisodeAvailabilityInfo(titleInfo.Anime, videoCatalog);

        return new YummyRefreshInfo(titleInfo, videoCatalog, files, availability);
    }

    private async Task<YummyAnimeTitleInfo> ResolveYummyAnimeTitleInfoAsync(
        ILogger logger,
        YummyAnimeResponse anime,
        string cleanKey,
        ShikimoriGraphQlClient shikimori,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        var rawTitle = string.IsNullOrWhiteSpace(anime.Title) ? cleanKey : anime.Title.Trim();
        ShikimoriSeriesLayoutInfo? shikimoriLayout = null;

        if (!YummySeriesLayoutResolver.HasExplicitSeasonNumber(rawTitle))
        {
            using (perf.Measure("stage.shikimori.layout"))
            {
                shikimoriLayout = await TryResolveSeriesLayoutFromShikimoriAsync(anime, shikimori, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var seasonNumber = YummySeriesLayoutResolver.ResolveSeasonNumber(anime, rawTitle, shikimoriLayout);
        var title = YummySeriesLayoutResolver.ResolveSeriesTitle(anime, rawTitle, seasonNumber, shikimoriLayout);
        if (string.IsNullOrWhiteSpace(title))
        {
            title = rawTitle;
        }

        var titleInfo = new YummyAnimeTitleInfo(anime, cleanKey, rawTitle, title, seasonNumber);
        LogResolvedSeriesLayout(logger, titleInfo);
        return titleInfo;
    }

    private static async Task<ShikimoriSeriesLayoutInfo?> TryResolveSeriesLayoutFromShikimoriAsync(
        YummyAnimeResponse anime,
        ShikimoriGraphQlClient shikimori,
        CancellationToken cancellationToken)
    {
        if (anime?.RemoteIds?.ShikimoriId is not long shikimoriId || shikimoriId <= 0)
        {
            return null;
        }

        try
        {
            return await shikimori.TryResolveSeriesLayoutAsync(shikimoriId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void LogResolvedSeriesLayout(ILogger logger, YummyAnimeTitleInfo titleInfo)
    {
        if (titleInfo.SeasonNumber == 1 &&
            string.Equals(titleInfo.RawTitle, titleInfo.Title, StringComparison.Ordinal))
        {
            return;
        }

        logger.LogInformation(
            "[YummyKodik] Series layout resolved. rawTitle='{RawTitle}' title='{Title}' season={Season} apiSeason={ApiSeason}",
            titleInfo.RawTitle,
            titleInfo.Title,
            titleInfo.SeasonNumber,
            titleInfo.Anime.Season);
    }

    private async Task<YummyVideoCatalog> LoadYummyVideoCatalogAsync(
        ILogger logger,
        PluginConfiguration cfg,
        YummyAnimeTitleInfo titleInfo,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        var allohaApiHttp = _httpClientFactory.CreateClient(HttpClientNames.AllohaApi);
        IReadOnlyList<YummyVideoEntry> allohaApiEntries;
        using (perf.Measure("stage.alloha.catalog"))
        {
            allohaApiEntries = await AllohaApiCatalogLoader
                .LoadEntriesAsync(cfg, titleInfo.Anime, allohaApiHttp, logger, cancellationToken)
                .ConfigureAwait(false);
        }

        allohaApiEntries = AllohaApiCatalogLoader.FilterEntriesForSeason(allohaApiEntries, titleInfo.SeasonNumber);
        return YummyVideoCatalog.Create(titleInfo.Anime, allohaApiEntries);
    }

    private static SeriesFileInfo BuildSeriesFileInfo(
        ILogger logger,
        string root,
        YummyAnimeTitleInfo titleInfo,
        PluginConfiguration cfg)
    {
        var folderName = RefreshPathUtilities.BuildSeriesFolderName(titleInfo.Title, titleInfo.Anime);
        var safeFolderName = RefreshPathUtilities.SafeFilename(folderName);
        var seriesRoot = RefreshPathUtilities.ResolveSeriesRoot(
            logger,
            Path.Combine(root, safeFolderName),
            RefreshPathUtilities.GetLegacySeriesRoots(root, titleInfo.RawTitle, titleInfo.Title, titleInfo.Anime));

        var seasonDirName = $"Season {titleInfo.SeasonNumber:00}";
        var seasonDir = Path.Combine(seriesRoot, seasonDirName);
        var baseUrl = (cfg.ServerBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        return new SeriesFileInfo(seriesRoot, seasonDir, baseUrl);
    }

    private static EpisodeAvailabilityInfo BuildEpisodeAvailabilityInfo(
        YummyAnimeResponse anime,
        YummyVideoCatalog videoCatalog)
    {
        if (anime.AnimeId <= 0)
        {
            var emptyEpisodes = Array.Empty<int>();
            return new EpisodeAvailabilityInfo(emptyEpisodes, 0, emptyEpisodes, emptyEpisodes, emptyEpisodes);
        }

        var knownSupportedEpisodes = videoCatalog.GetSupportedEpisodeNumbersAcrossProviders(RefreshConstants.PreferredYummyProviderOrder);
        var expectedAvailableEpisodes = YummyEpisodeAvailability.GetExpectedAvailableEpisodeCount(anime, knownSupportedEpisodes);
        var allohaSupportedEpisodes = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
            anime,
            videoCatalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Alloha));
        var cvhSupportedEpisodes = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
            anime,
            videoCatalog.GetSupportedEpisodeNumbers(YummyVideoProviderKind.Cvh));
        var yummySupportedEpisodes = YummyEpisodeAvailability.LimitToExpectedAvailableEpisodes(
            anime,
            videoCatalog.GetSupportedEpisodeNumbersAcrossProviders(RefreshConstants.PreferredYummyProviderOrder));

        return new EpisodeAvailabilityInfo(
            knownSupportedEpisodes,
            expectedAvailableEpisodes,
            allohaSupportedEpisodes,
            cvhSupportedEpisodes,
            yummySupportedEpisodes);
    }
}
