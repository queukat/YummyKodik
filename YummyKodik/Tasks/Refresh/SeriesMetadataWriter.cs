using Microsoft.Extensions.Logging;
using YummyKodik.Util;
using YummyKodik.Yummy;

namespace YummyKodik.Tasks.Refresh;

internal sealed class SeriesMetadataWriter
{
    public async Task EnsureAsync(
        ILogger logger,
        YummyRefreshInfo refresh,
        HttpClient posterHttp,
        RefreshPerformanceMetrics perf,
        CancellationToken cancellationToken)
    {
        // Create/update the card from Yummy metadata first so Kodik outages do not hide the title.
        using (perf.Measure("stage.series.nfo"))
        {
            await EnsureTvShowNfoAsync(
                    logger,
                    refresh.TitleInfo.Title,
                    refresh.TitleInfo.Anime,
                    refresh.Files.SeriesRoot,
                    perf,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            using (perf.Measure("stage.poster"))
            {
                await EnsurePosterAsync(refresh.TitleInfo.Anime, refresh.Files.SeriesRoot, posterHttp, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning(
                ex,
                "[YummyKodik] Failed to update poster for '{Title}'. Series card metadata will still be kept.",
                refresh.TitleInfo.Title);
        }
    }

    private static async Task EnsurePosterAsync(
        YummyAnimeResponse anime,
        string seriesRoot,
        HttpClient http,
        CancellationToken cancellationToken)
    {
        var posterPath = Path.Combine(seriesRoot, "poster.jpg");
        if (File.Exists(posterPath))
        {
            return;
        }

        var url = YummyClient.PickBestPosterUrl(anime);
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        var resp = await http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        Directory.CreateDirectory(seriesRoot);
        var tempPath = Path.Combine(
            seriesRoot,
            Path.GetFileName(posterPath) + ".tmp." + Guid.NewGuid().ToString("N"));

        try
        {
            await using (var fs = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, posterPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup for interrupted poster writes.
                }
            }
        }
    }

    private static async Task EnsureTvShowNfoAsync(
        ILogger logger,
        string seriesTitle,
        YummyAnimeResponse anime,
        string seriesRoot,
        RefreshPerformanceMetrics? perf,
        CancellationToken cancellationToken)
    {
        var nfoPath = Path.Combine(seriesRoot, "tvshow.nfo");
        var xml = NfoBuilder.BuildSeriesNfo(seriesTitle, anime.Description ?? string.Empty);

        if (File.Exists(nfoPath))
        {
            if (await RefreshFileWriter.IsValidXmlFileAsync(nfoPath, cancellationToken).ConfigureAwait(false))
            {
                perf?.AddCount("io.nfo_unchanged");
                return;
            }

            perf?.AddCount("io.nfo_invalid_rebuilt");
            logger.LogWarning(
                "[YummyKodik] Existing tvshow.nfo is empty or invalid XML, recreating it atomically. path='{Path}'",
                nfoPath);
        }

        await RefreshFileWriter.WriteTextAtomicallyAsync(nfoPath, xml, perf, "nfo", cancellationToken).ConfigureAwait(false);
    }
}
