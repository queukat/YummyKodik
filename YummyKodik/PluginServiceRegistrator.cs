using System;
using System.Net;
using System.Net.Http;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Resolvers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YummyKodik.Alloha;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Tasks;
using YummyKodik.Util;
using YummyKodik.Versioning;
using YummyKodik.Web;

namespace YummyKodik;

public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddTransient<YummyKodikHlsVideoEncodingFilter>();
        serviceCollection.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
            options.Filters.Add(new Microsoft.AspNetCore.Mvc.ServiceFilterAttribute(typeof(YummyKodikHlsVideoEncodingFilter))));

        serviceCollection.AddLogging(builder =>
        {
            builder.AddFilter("YummyKodik", YummyKodikLogFilter.ShouldLogPluginCategory);
        });

        serviceCollection.AddHttpClient(HttpClientNames.PlaybackManifest, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(45);
            client.MaxResponseContentBufferSize = 4 * 1024 * 1024;
        });

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

        serviceCollection
            .AddHttpClient(HttpClientNames.Yummy, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            });

        serviceCollection
            .AddHttpClient(HttpClientNames.Cvh, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                UseCookies = false,
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            });

        serviceCollection
            .AddHttpClient(HttpClientNames.AllohaApi, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            });

        serviceCollection
            .AddHttpClient(HttpClientNames.Shikimori, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/json,text/plain,*/*");
                client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AutomaticDecompression =
                    DecompressionMethods.GZip |
                    DecompressionMethods.Deflate |
                    DecompressionMethods.Brotli
            });

        serviceCollection.AddSingleton<IInternalJellyfinUrlProvider>(_ => new InternalJellyfinUrlProvider(applicationHost));
        // Jellyfin's GetExports creates providers with ActivatorUtilities as well
        // as through registrations. Both paths must receive the deferred manager.
        serviceCollection.AddSingleton<Func<IMediaSourceManager>>(sp => () => sp.GetRequiredService<IMediaSourceManager>());
        serviceCollection.AddSingleton(sp => new YummyKodikMediaSourceProvider(
            sp.GetRequiredService<ILogger<YummyKodikMediaSourceProvider>>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IInternalJellyfinUrlProvider>(),
            sp.GetRequiredService<ILibraryManager>(),
            sp.GetRequiredService<Func<IMediaSourceManager>>()));
        serviceCollection.AddSingleton<IMediaSourceProvider>(sp => sp.GetRequiredService<YummyKodikMediaSourceProvider>());
        var nativeSourceManager = serviceCollection.Last(descriptor => descriptor.ServiceType == typeof(IMediaSourceManager));
        serviceCollection.Remove(nativeSourceManager);
        serviceCollection.AddSingleton<IMediaSourceManager>(sp => new YummyKodikMediaSourceManager(
            (IMediaSourceManager)(nativeSourceManager.ImplementationInstance ??
                nativeSourceManager.ImplementationFactory?.Invoke(sp) ??
                ActivatorUtilities.CreateInstance(sp, nativeSourceManager.ImplementationType!)),
            sp.GetRequiredService<YummyKodikMediaSourceProvider>()));
        serviceCollection.AddSingleton<MediaBrowser.Controller.MediaSegments.IMediaSegmentProvider, YummyKodikMediaSegmentProvider>();
        serviceCollection.AddHttpClient(HttpClientNames.AllohaBrowserIdentity, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(3);
            client.MaxResponseContentBufferSize = 64 * 1024;
        });
        serviceCollection.AddSingleton(sp => new AllohaBrowserIdentity(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientNames.AllohaBrowserIdentity),
            System.IO.Path.Combine(sp.GetRequiredService<MediaBrowser.Common.Configuration.IApplicationPaths>().CachePath,
                "YummyKodik", "alloha-browser-major.txt")));
        serviceCollection.AddSingleton(sp => new AllohaPlaybackService(
            sp.GetRequiredService<ILogger<AllohaPlaybackService>>(), sp.GetRequiredService<AllohaBrowserIdentity>()));

        // Inject the series translation widget into Jellyfin Web once on startup.
        serviceCollection.AddHostedService<JellyfinWebSeriesTranslationBootstrapHostedService>();

        // Auto-merge STRM "versions" for duplicate episodes (translations) based on library events.
        serviceCollection.AddSingleton<YummyKodikEpisodeVersionsMergeHostedService>();
        serviceCollection.AddSingleton<IItemResolver, YummyKodikNativeEpisodeResolver>();
        serviceCollection.AddSingleton<IHostedService>(sp => sp.GetRequiredService<YummyKodikEpisodeVersionsMergeHostedService>());
        serviceCollection.AddSingleton<YummyKodikPostRefreshMergeBarrier>();
        serviceCollection.AddSingleton<RefreshYummyKodikLibraryTask>();
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask>(sp => sp.GetRequiredService<RefreshYummyKodikLibraryTask>());
        serviceCollection.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, RefreshSecondYummyKodikLibraryTask>();

        // Warm Jellyfin media segments for newly scanned YummyKodik STRM episodes instead of
        // waiting for the next global "Detect and Analyze Media Segments" scheduled task.
        serviceCollection.AddHostedService<YummyKodikMediaSegmentWarmupHostedService>();
    }
}
