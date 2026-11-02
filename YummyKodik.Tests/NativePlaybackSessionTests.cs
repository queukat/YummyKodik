using System.Net;
using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using YummyKodik;
using YummyKodik.Configuration;
using YummyKodik.Logging;
using YummyKodik.Media;

internal static class NativePlaybackSessionTests
{
    public static void HlsRequestsEncodeOnlyTheBoundManagedStream()
    {
        using var f = new Fixture();
        using var opened = f.Provider.OpenMediaSource(f.First.Id.ToString("N"), [], default).GetAwaiter().GetResult();
        var manager = DispatchProxy.Create<IMediaSourceManager, Proxy>();
        ((Proxy)(object)manager).Call = (method, args) => method.Name == "GetLiveStreamInfo"
            ? ((string)args[0]! == "managed" ? opened : null)
            : throw new Exception("Unexpected stream lookup " + method.Name);
        var filter = new YummyKodikHlsVideoEncodingFilter(manager);

        foreach (var action in new[] { "GetMasterHlsVideoPlaylist", "GetVariantHlsVideoPlaylist", "GetHlsVideoSegment" })
        {
            var context = HlsContext("DynamicHls", "managed");
            context.ActionDescriptor.RouteValues["action"] = action;
            filter.OnActionExecuting(context);
            Require(context.ActionArguments["allowVideoStreamCopy"] is false, "Master, variant and seek/segment requests must consistently encode video.");
            Require(context.HttpContext.Request.Query["AllowVideoStreamCopy"] == "false", "Child requests must inherit the same policy.");
            Require(context.HttpContext.Request.Query["LiveStreamId"] == "managed" && context.HttpContext.Request.Query["StartTimeTicks"] == "5405000000",
                "Keep the pinned session and seek time unchanged.");
            Require(context.HttpContext.Request.Query["AllowAudioStreamCopy"] == "true", "Do not change audio policy.");
            filter.OnActionExecuting(context);
            Require(context.HttpContext.Request.Query["AllowVideoStreamCopy"].Count == 1, "Repeated application must not accumulate query values.");
        }

        var omitted = HlsContext("DynamicHls", "managed");
        omitted.ActionArguments.Remove("allowVideoStreamCopy");
        omitted.HttpContext.Request.QueryString = new QueryString("?LiveStreamId=managed");
        filter.OnActionExecuting(omitted);
        Require(omitted.ActionArguments["allowVideoStreamCopy"] is false && omitted.HttpContext.Request.Query["AllowVideoStreamCopy"] == "false",
            "MVC leaves an omitted optional parameter out of ActionArguments; normal Web requests must still encode.");

        foreach (var pair in new[] { ("DynamicHls", "foreign"), ("DynamicHls", ""), ("Videos", "managed") })
        {
            var context = HlsContext(pair.Item1, pair.Item2);
            var original = context.HttpContext.Request.QueryString;
            filter.OnActionExecuting(context);
            Require(context.ActionArguments["allowVideoStreamCopy"] is true && context.HttpContext.Request.QueryString == original,
                "Unmanaged, unbound and non-HLS playback must remain unchanged.");
        }

        var services = new ServiceCollection();
        services.AddSingleton(f.Inner);
        new PluginServiceRegistrator().RegisterServices(services, null!);
        using var container = services.BuildServiceProvider();
        Require(container.GetRequiredService<IOptions<MvcOptions>>().Value.Filters.OfType<ServiceFilterAttribute>()
            .Any(x => x.ServiceType == typeof(YummyKodikHlsVideoEncodingFilter)), "The host MVC pipeline must install the filter.");
    }

    private static ActionExecutingContext HlsContext(string controller, string liveStreamId)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?LiveStreamId=" + liveStreamId + "&allowVideoStreamCopy=true&AllowAudioStreamCopy=true&StartTimeTicks=5405000000");
        var descriptor = new ActionDescriptor
        {
            RouteValues = new Dictionary<string, string?> { ["controller"] = controller },
            Parameters = [new ParameterDescriptor { Name = "allowVideoStreamCopy", ParameterType = typeof(bool?) }]
        };
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), descriptor), [],
            new Dictionary<string, object?> { ["liveStreamId"] = liveStreamId, ["allowVideoStreamCopy"] = true }, new object());
    }

    public static void NativeSelectionOpensOnlyChosenVoiceAndPinsProbe()
    {
        using var f = new Fixture();
        var sources = f.Manager.GetPlaybackMediaSources(f.First, null!, true, true, default).GetAwaiter().GetResult();
        Require(f.Probes == 0 && f.Requests.Count == 0 && !f.AllowRemoteProbe, "Listing native versions must not resolve/probe every voice.");
        Require(sources.Count == 2 && sources.All(s => s.RequiresOpening && s.RequiresClosing), "Native version IDs must be retained and opened by the host.");
        Require(!f.Provider.GetMediaSources(f.Second, default).GetAwaiter().GetResult().Any(), "Do not offer independent dynamic duplicates.");
        var token = sources[1].OpenToken[YummyKodikMediaSourceProvider.NativeOpenPrefix.Length..];
        using var opened = f.Provider.OpenMediaSource(token, [], default).GetAwaiter().GetResult();
        Require(opened.MediaSource.Id == f.Second.Id.ToString("N") && opened.MediaSource.Name == "Second voice", "An alternate must retain its own identity, not resolve to the primary.");
        Require(f.Requests.Count == 1 && f.Requests[0].Query.Contains("voice=Second"), "Resolve only the selected voice through the existing gateway.");
        Require(opened.MediaSource.Path == f.ProbedPaths.Single() && opened.MediaSource.EncoderPath == opened.MediaSource.Path, "Probe and encoder must consume the identical pinned provider session.");
        Require(opened.MediaSource.MediaStreams.Single(s => s.Type == MediaStreamType.Video).Width == 720, "Use actual fallback codec metadata, not native 1080p metadata.");
        Require(opened.MediaSource.LiveStreamId != null && !opened.MediaSource.RequiresOpening && !opened.MediaSource.IsInfiniteStream, "An opened VOD needs a host binding, not a second open or infinite-stream behavior.");
        f.Live[opened.MediaSource.LiveStreamId!] = opened.MediaSource;
        var seek = f.Manager.GetMediaSource(f.First, f.Second.Id.ToString("N"), opened.MediaSource.LiveStreamId!, true, default).GetAwaiter().GetResult();
        Require(ReferenceEquals(seek, opened.MediaSource) && f.Requests.Count == 1, "A seek must reuse the host live stream without resolving another provider.");
        opened.Close().GetAwaiter().GetResult();
        opened.Close().GetAwaiter().GetResult();
        Require(File.ReadAllText(f.Second.Path).Contains("/stream?"), "Do not persist expiring playback URLs to generated files.");
    }

    public static void ConcurrentOpensHaveIndependentSessionIdentity()
    {
        using var f = new Fixture();
        var first = f.Provider.OpenMediaSource(f.First.Id.ToString("N"), [], default);
        var second = f.Provider.OpenMediaSource(f.First.Id.ToString("N"), [], default);
        Task.WhenAll(first, second).GetAwaiter().GetResult();
        using var a = first.Result;
        using var b = second.Result;
        Require(a.MediaSource.Id == b.MediaSource.Id && a.MediaSource.LiveStreamId != b.MediaSource.LiveStreamId &&
            a.MediaSource.Path != b.MediaSource.Path, "Separate viewers must not overwrite another session binding.");
        Require(f.Probes == 2 && f.ProbedPaths.Contains(a.MediaSource.Path) && f.ProbedPaths.Contains(b.MediaSource.Path), "Each open must probe its own session.");
    }

    public static void SingleFileRetainsDynamicVoiceChoices()
    {
        using var f = new Fixture();
        File.WriteAllText(f.First.Path, "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=123&ep=2");
        var sources = f.Provider.GetMediaSources(f.First, default).GetAwaiter().GetResult().ToArray();
        Require(sources.Length >= 3 && sources.Any(s => s.Name.Contains("First")) && sources.Any(s => s.Name.Contains("Second")),
            "A single STRM without explicit voice must retain Auto and the existing dynamic voice catalogue.");
    }

    public static void HostExportCreationReceivesPlaybackLifecycle()
    {
        using var f = new Fixture();
        var services = new ServiceCollection();
        services.AddSingleton(f.Inner);
        new PluginServiceRegistrator().RegisterServices(services, null!);
        services.AddSingleton<ILogger<YummyKodikMediaSourceProvider>>(NullLogger<YummyKodikMediaSourceProvider>.Instance);
        services.AddSingleton<IHttpClientFactory>(f);
        services.AddSingleton<IInternalJellyfinUrlProvider>(f);
        services.AddSingleton(f.Library);
        using var container = services.BuildServiceProvider();
        // This is the host GetExports/CreateInstanceSafe path, not GetRequiredService.
        var exported = ActivatorUtilities.CreateInstance<YummyKodikMediaSourceProvider>(container);
        Require(!exported.GetMediaSources(f.First, default).GetAwaiter().GetResult().Any(), "A host-created provider must suppress the same duplicated native source.");
        using var opened = exported.OpenMediaSource(f.First.Id.ToString("N"), [], default).GetAwaiter().GetResult();
        Require(opened.MediaSource.LiveStreamId != null && f.Probes == 1, "Host export creation must support the same pinned open/probe lifecycle.");
    }

    public static void UnmanagedPlaybackRetainsHostBehavior()
    {
        using var f = new Fixture();
        var external = new Episode { Id = Guid.NewGuid(), Path = Path.Combine(f.Root, "outside.mkv") };
        var result = f.Manager.GetPlaybackMediaSources(external, null!, true, true, default).GetAwaiter().GetResult();
        Require(f.AllowRemoteProbe && result.All(s => !s.RequiresOpening) && f.Requests.Count == 0, "Other libraries must retain the native probe and lifecycle.");
        Require(!f.Provider.PrepareNativeSource(new MediaSourceInfo { Id = "foreign-dynamic" }), "Other providers' IDs must remain unchanged.");
    }

    public static void FailedOrCanceledOpenNeverPublishesAnUnprobedStream()
    {
        using var f = new Fixture();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try { f.Provider.OpenMediaSource(f.First.Id.ToString("N"), [], cts.Token).GetAwaiter().GetResult(); throw new Exception("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Require(f.Requests.Count == 0, "Canceled opens must not contact providers.");
        f.FailManifest = true;
        try { f.Provider.OpenMediaSource(f.First.Id.ToString("N"), [], default).GetAwaiter().GetResult(); throw new Exception("Failed manifest was accepted."); }
        catch (HttpRequestException) { }
        Require(f.Probes == 0, "A failed resolution must not probe an unrelated/native source.");
        f.FailManifest = false;
        f.MissingAudio = true;
        try { f.Provider.OpenMediaSource(f.First.Id.ToString("N"), [], default).GetAwaiter().GetResult(); throw new Exception("Unusable media was accepted."); }
        catch (InvalidOperationException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class Fixture : IDisposable, IHttpClientFactory, IInternalJellyfinUrlProvider
    {
        private readonly Plugin? _previous = Plugin.Instance;
        private readonly Func<PluginConfiguration?> _previousLog = YummyKodikLogFilter.ConfigurationProvider;
        private readonly HttpClient _client;
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        public Episode First { get; }
        public Episode Second { get; }
        public YummyKodikMediaSourceProvider Provider { get; }
        public YummyKodikMediaSourceManager Manager { get; }
        public ILibraryManager Library { get; }
        public IMediaSourceManager Inner { get; }
        public bool AllowRemoteProbe;
        public bool FailManifest;
        public bool MissingAudio;
        public int Probes;
        public List<Uri> Requests { get; } = [];
        public List<string> ProbedPaths { get; } = [];
        public Dictionary<string, MediaSourceInfo> Live { get; } = [];

        public Fixture()
        {
            Directory.CreateDirectory(Root);
            var paths = DispatchProxy.Create<IApplicationPaths, LateNativeMergeTests.HostDependencyProxy>();
            ((LateNativeMergeTests.HostDependencyProxy)(object)paths).Root = Root;
            var plugin = new Plugin(paths, DispatchProxy.Create<IXmlSerializer, LateNativeMergeTests.HostDependencyProxy>(), NullLogger<Plugin>.Instance);
            plugin.Configuration.OutputRootPath = Path.Combine(Root, "managed");
            plugin.Configuration.YummyClientId = "test";
            plugin.Configuration.YummyApiBaseUrl = "https://yummy.test";
            Directory.CreateDirectory(plugin.Configuration.OutputRootPath);
            First = Item("First");
            Second = Item("Second");
            Second.PrimaryVersionId = First.Id;
            var library = DispatchProxy.Create<ILibraryManager, Proxy>();
            Library = library;
            ((Proxy)(object)library).Call = (method, args) => method.Name == "GetItemById"
                ? new[] { First, Second }.FirstOrDefault(x => x.Id == (Guid)args[0]!) : throw new Exception("Unexpected library call " + method.Name);
            var inner = DispatchProxy.Create<IMediaSourceManager, Proxy>();
            Inner = inner;
            ((Proxy)(object)inner).Call = (method, args) =>
            {
                switch (method.Name)
                {
                    case "GetPlaybackMediaSources":
                        AllowRemoteProbe = (bool)args[2]!;
                        return Task.FromResult<IReadOnlyList<MediaSourceInfo>>([Source(First), Source(Second)]);
                    case "GetStaticMediaSources": return (IReadOnlyList<MediaSourceInfo>)[Source((Episode)args[0]!)];
                    case "AddMediaInfoWithProbe":
                        Require(args[2] == null && !(bool)args[3]! && !(bool)args[4]!, "Use an uncached VOD probe without live-TV delay/index stripping.");
                        var source = (MediaSourceInfo)args[0]!;
                        Probes++;
                        ProbedPaths.Add(source.Path);
                        source.MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Index = 0, Codec = "h264", Width = 720 }];
                        if (!MissingAudio) source.MediaStreams = [.. source.MediaStreams, new MediaStream { Type = MediaStreamType.Audio, Index = 1, Codec = "aac", SampleRate = 44100 }];
                        return Task.CompletedTask;
                    case "GetMediaSource": return Task.FromResult(Live[(string)args[2]!]);
                    default: throw new Exception("Unexpected manager call " + method.Name);
                }
            };
            _client = new HttpClient(new Handler(this));
            Provider = new YummyKodikMediaSourceProvider(NullLogger<YummyKodikMediaSourceProvider>.Instance, this, this, library, () => inner);
            Manager = new YummyKodikMediaSourceManager(inner, Provider);
        }

        private Episode Item(string voice)
        {
            var path = Path.Combine(Root, "managed", "S01E02 - " + voice + ".strm");
            File.WriteAllText(path, "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=123&ep=2&voice=" + voice);
            return new Episode { Id = Guid.NewGuid(), Path = path, RunTimeTicks = TimeSpan.FromSeconds(1200).Ticks };
        }
        private MediaSourceInfo Source(Episode item) => new()
        {
            Id = item.Id.ToString("N"), Name = item == First ? "First voice" : "Second voice",
            Path = File.ReadAllText(item.Path), MediaStreams = [new MediaStream { Type = MediaStreamType.Video, Width = 1080 }]
        };
        public HttpClient CreateClient(string name) => _client;
        public string GetBaseUrl() => "http://127.0.0.1:8096";
        public void Dispose()
        {
            _client.Dispose();
            typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previous);
            YummyKodikLogFilter.ConfigurationProvider = _previousLog;
            Directory.Delete(Root, true);
        }
        private sealed class Handler(Fixture owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.Requests.Add(request.RequestUri!);
                if (request.RequestUri!.AbsolutePath.StartsWith("/anime/"))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        RequestMessage = request,
                        Content = new StringContent("""
                            {"response":{"anime_id":123,"title":"Test","videos":[
                            {"number":"2","duration":1200,"iframe_url":"https://play.example/player?anime_id=123&episode=2&dubbing_code=1&dubbing=First","data":{"player_id":3,"dubbing":"First"}},
                            {"number":"2","duration":1200,"iframe_url":"https://play.example/player?anime_id=123&episode=2&dubbing_code=2&dubbing=Second","data":{"player_id":3,"dubbing":"Second"}}
                            ]}}
                            """)
                    });
                }
                var response = new HttpResponseMessage(owner.FailManifest ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
                {
                    RequestMessage = request, Content = new StringContent("#EXTM3U\n#EXTINF:1200,\nsegment.ts\n#EXT-X-ENDLIST")
                };
                response.Content.Headers.ContentLocation = new Uri(owner.GetBaseUrl() + "/YummyKodik/kodik-proxy/manifest.m3u8?sessionId=" + Guid.NewGuid().ToString("N"));
                return Task.FromResult(response);
            }
        }
    }

    public class Proxy : DispatchProxy
    {
        public Func<MethodInfo, object?[], object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args ?? []);
    }
}
