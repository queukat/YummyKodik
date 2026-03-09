using System;
using System.Net;
using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using YummyKodik.Media;
using YummyKodik.Tasks;
using YummyKodik.Util;
using YummyKodik.Versioning;

namespace YummyKodik;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Named HttpClient for Kodik-related requests (api, player html, token sources).
        serviceCollection
            .AddHttpClient(HttpClientNames.Kodik, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);

                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

                // keep wide accept, but include json explicitly (Kodik endpoints return json)
                client.DefaultRequestHeaders.Accept.ParseAdd(
                    "application/json,text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");

                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            });

        serviceCollection.AddSingleton<IMediaSourceProvider, YummyKodikMediaSourceProvider>();
        serviceCollection.AddSingleton<MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider, YummyKodikMediaSegmentProvider>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, RefreshYummyKodikLibraryTask>();

        // Auto-merge STRM "versions" for duplicate episodes (translations) based on library events.
        serviceCollection.AddHostedService<YummyKodikEpisodeVersionsMergeHostedService>();
    }
}
