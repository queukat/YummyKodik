using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Xml.Serialization;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Api;
using YummyKodik.Configuration;
using YummyKodik.Logging;
using YummyKodik.Media;
using YummyKodik.Tasks;
using YummyKodik.Tasks.Refresh;
using YummyKodik.Versioning;
using YummyKodik.Yummy;

internal static class MultiLibraryTests
{
    public static void RefreshRoutesMovesAndFailuresIndependently()
        => RunScenarioAsync().GetAwaiter().GetResult();

    public static void KodikOutagePreservesYummyGeneration()
    {
        using var fixture = new Fixture();
        fixture.Lists[0] = ["first"];
        fixture.RefreshAsync(0).GetAwaiter().GetResult();
        var statePath = Directory.GetFiles(fixture.Configuration.OutputRootPath, RefreshStateManager.StateFileName, SearchOption.AllDirectories).Single();
        var beforeState = File.ReadAllText(statePath);
        var season = Directory.GetDirectories(Path.GetDirectoryName(statePath)!, "Season *").Single();
        var existing = Path.Combine(season, "S01E01 - ExistingKodik.strm");
        File.WriteAllText(existing, "preserve existing Kodik");
        fixture.Configuration.CreateStrmPerVoiceTranslation = true;
        fixture.FailKodik = true;
        fixture.RefreshAsync(0).GetAwaiter().GetResult();
        Require(Directory.GetFiles(season, "*AniStar.strm").Length == 1, "Available Yummy voice must generate during Kodik outage.");
        Require(File.ReadAllText(existing) == "preserve existing Kodik", "Unverified Kodik file must survive outage.");
        Require(File.ReadAllText(statePath) == beforeState, "Incomplete Kodik pass must not publish new state proof.");
    }

    public static void PartialFailureReportsFailedAfterMergingSuccessfulTitles()
    {
        using var fixture = new Fixture();
        fixture.Lists[0] = ["first", "second", "shared"];
        fixture.FailedTitle = "first";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var failed = false;
        try { fixture.PrimaryTask.ExecuteAsync(fixture, timeout.Token).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("YummyKodik refresh failed for 1 title(s)")) { failed = true; }
        Require(failed, "Partial failure must produce failed native task status.");
        Require(fixture.Keys(0).SetEquals(["second", "shared"]), "Independent titles must finish after a title failure.");
        Require(fixture.FullMergeQueries == 1, "Successful title artifacts must be merged before reporting failure.");
    }

    public static void WidgetKeepsWholeSeriesVoicesAcrossEpisodes()
    {
        using var fixture = new Fixture();
        fixture.VerifyWholeSeriesWidget();
    }

    private static async Task RunScenarioAsync()
    {
        using var fixture = new Fixture();
        fixture.Lists[0] = ["first", "shared"];
        fixture.Lists[2] = ["second", "shared"];
        fixture.Configuration.Slugs = ["manual"];
        await fixture.RefreshAsync();
        Require(fixture.Keys(0).SetEquals(["first", "shared", "manual"]), "Primary folder must contain only its list and manual titles.");
        Require(fixture.ReportedChanges.Count == 0, "Generating files must not report deletions to Jellyfin.");
        Require(fixture.Keys(1).SetEquals(["second", "shared"]), "Second folder must contain only its own list, including independent overlapping titles.");
        Require(fixture.FullMergeQueries == 2, "Each independently scheduled folder must have one scoped authoritative merge.");
        Require(fixture.Progress.Last() == 100 && fixture.Progress.SequenceEqual(fixture.Progress.Order()), "Progress must be monotonic across both libraries.");
        Require(fixture.OrdinaryInventoryQueries == 8, "Each folder needs two runtime inventories and two readiness snapshots, all scoped at the host.");
        fixture.VerifySecondRootConsumers();

        fixture.Lists[0] = ["shared"];
        fixture.Lists[2] = ["second", "shared", "first"];
        await fixture.RefreshAsync();
        Require(fixture.Keys(0).SetEquals(["shared", "manual"]) && fixture.Keys(1).Contains("first"),
            "Moving a title between lists must remove only its old managed copy and generate the destination copy.");
        Require(fixture.ReportedChanges.Count == 1 &&
                LibraryTarget.ContainsPath(fixture.Configuration.OutputRootPath, fixture.ReportedChanges[0]) &&
                !Directory.Exists(fixture.ReportedChanges[0]),
            "A successfully removed series must notify native catalogue reconciliation with its exact deleted path.");

        foreach (var failedList in new[] { 0, 2 })
        {
            fixture.FailedList = failedList;
            var successfulList = failedList == 0 ? 2 : 0;
            fixture.Lists[successfulList] = [];
            var failedIndex = failedList == 0 ? 0 : 1;
            var retained = fixture.Keys(failedIndex);
            await fixture.RefreshAsync();
            Require(fixture.Keys(failedIndex).SetEquals(retained), "A failed list must preserve its own files, regardless of another list's success.");
            var failedRoot = failedIndex == 0 ? fixture.Configuration.OutputRootPath : fixture.Configuration.SecondOutputRootPath;
            Require(!fixture.ReportedChanges.Any(path => LibraryTarget.ContainsPath(failedRoot, path)),
                "A list fetch failure must never notify catalogue deletion for that library.");
            Require(fixture.Keys(1 - failedIndex).SetEquals(successfulList == 0 ? ["manual"] : []),
                "A successfully fetched empty list must clean only its own folder and preserve primary manual titles.");
            fixture.FailedList = null;
            fixture.Lists[0] = ["first", "shared"];
            fixture.Lists[2] = ["second", "shared"];
            await fixture.RefreshAsync();
        }

        fixture.Configuration.UseSecondUserListSubscription = false;
        fixture.Lists[0] = [];
        var preserved = fixture.Keys(1);
        fixture.ListRequests.Clear();
        await fixture.RefreshAsync();
        Require(fixture.ListRequests.SequenceEqual([0]) && fixture.Keys(1).SetEquals(preserved),
            "Disabling the second list must stop fetching and touching its folder.");
    }

    public static void RootValidationAndConfigurationCompatibility()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<PluginConfiguration><OutputRootPath>C:\\old-library</OutputRootPath><YummyUserListId>2</YummyUserListId></PluginConfiguration>");
        var legacy = (PluginConfiguration)serializer.Deserialize(reader)!;
        Require(legacy.GetLibraryTargets().Count == 1 && legacy.YummyUserListId == 2, "Old XML must not enable another list or lose its selection.");
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var cfg = new PluginConfiguration { OutputRootPath = root, UseSecondUserListSubscription = true };
        foreach (var invalid in new[] { "", root, Path.Combine(root, "nested"), Path.GetDirectoryName(root)! })
        {
            cfg.SecondOutputRootPath = invalid;
            try { LibraryTarget.Validate(cfg.GetLibraryTargets()); throw new InvalidOperationException("Overlapping/empty roots must be rejected."); }
            catch (ArgumentException) { }
        }
        cfg.SecondOutputRootPath = root + "-second";
        LibraryTarget.Validate(cfg.GetLibraryTargets());
        Require(!LibraryTarget.ContainsPath(root, cfg.SecondOutputRootPath) && LibraryTarget.ContainsPath(root, Path.Combine(root, "show")),
            "Containment must respect directory boundaries, not common path prefixes.");
        using var writer = new StringWriter();
        serializer.Serialize(writer, cfg);
        using var roundtripReader = new StringReader(writer.ToString());
        var roundtrip = (PluginConfiguration)serializer.Deserialize(roundtripReader)!;
        Require(roundtrip.GetLibraryTargets().Count == 2 && roundtrip.SecondOutputRootPath == cfg.SecondOutputRootPath,
            "Both targets must survive native configuration serialization.");
    }

    public static void ListsShareAuthenticationAndPropagateCancellation()
    {
        var refreshes = 0;
        var requests = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            if (request.RequestUri!.AbsolutePath.Contains("token"))
            {
                refreshes++;
                return "{\"response\":{\"token\":\"refreshed-test-token\"}}";
            }
            requests++;
            return "{\"response\":[]}";
        }));
        var cfg = new PluginConfiguration { YummyUserId = 42, UseUserListSubscription = true, UseSecondUserListSubscription = true, YummyAccessToken = "test-token" };
        var client = new YummyClient(http, "test", "https://yummy.test");
        foreach (var target in cfg.GetLibraryTargets())
            new RefreshTitleKeySource(NullLogger.Instance, () => { }).BuildAsync(cfg, client, CancellationToken.None, target).GetAwaiter().GetResult();
        Require(refreshes == 1 && requests == 2, "Two lists from the same account must share one authentication per run.");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        try
        {
            new RefreshTitleKeySource(NullLogger.Instance, () => { }).BuildAsync(cfg, client, cts.Token).GetAwaiter().GetResult();
            throw new InvalidOperationException("Cancellation must not be converted to a list failure.");
        }
        catch (OperationCanceledException) { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void SeparateSchedulesNeverTouchTheOtherFolder()
        => RunSeparateSchedulesAsync().GetAwaiter().GetResult();

    private static async Task RunSeparateSchedulesAsync()
    {
        using var fixture = new Fixture();
        fixture.Lists[0] = ["first"];
        fixture.Lists[2] = ["second"];
        Require(fixture.PrimaryTask.Key == "YummyKodikRefresh", "Existing primary schedule identity must survive upgrade.");
        Require(fixture.SecondTask.Key != fixture.PrimaryTask.Key &&
                fixture.SecondTask.GetDefaultTriggers().Single().IntervalTicks == TimeSpan.FromDays(1).Ticks,
            "The second library must have its own daily scheduled task.");
        await fixture.RefreshAsync(0);
        Require(fixture.ListRequests.SequenceEqual([0]) && !Directory.Exists(fixture.Configuration.SecondOutputRootPath),
            "The primary task must not fetch or create the second folder.");
        Require(fixture.InventoryRoots.Count == 5 && fixture.InventoryRoots.All(root => root == fixture.Configuration.OutputRootPath),
            "Primary metadata, readiness and merge queries must never enumerate the second library.");
        var primaryFiles = Directory.GetFiles(fixture.Configuration.OutputRootPath, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.GetLastWriteTimeUtc);
        fixture.ListRequests.Clear();
        fixture.InventoryRoots.Clear();
        await fixture.RefreshAsync(1);
        Require(fixture.ListRequests.SequenceEqual([2]) && fixture.Keys(1).SetEquals(["second"]) &&
                fixture.InventoryRoots.Count == 5 && fixture.InventoryRoots.All(root => root == fixture.Configuration.SecondOutputRootPath),
            "The second task must fetch and inventory only the second list/folder.");
        Require(primaryFiles.All(pair => File.GetLastWriteTimeUtc(pair.Key) == pair.Value),
            "Second-library work must not rewrite primary files or refresh state.");

        fixture.ListRequests.Clear();
        fixture.InventoryRoots.Clear();
        var gate = (SemaphoreSlim)typeof(RefreshYummyKodikLibraryTask).GetField("_runGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.PrimaryTask)!;
        await gate.WaitAsync();
        try
        {
            await fixture.RefreshAsync(1);
            Require(fixture.ListRequests.Count == 0 && fixture.InventoryRoots.Count == 0,
                "The second task must share the primary gate and skip overlapping work.");
        }
        finally { gate.Release(); }
        fixture.Configuration.UseSecondUserListSubscription = false;
        await fixture.RefreshAsync(1);
        Require(fixture.ListRequests.Count == 0 && fixture.InventoryRoots.Count == 0,
            "A disabled second task must not touch files, providers or host inventories.");
    }

    public static void UnindexedRootNeverFallsBackToGlobalInventory()
    {
        var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
        var proxy = (LibraryProxy)(object)library;
        var missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Require(ManagedEpisodeInventory.Load(library, [missing], true).Count == 0 && proxy.OrdinaryInventoryQueries == 0 && proxy.MergeQueries == 0,
            "An unindexed root must not turn an empty ancestor filter into a full-host query.");
    }

    public static void WarmupScopesInventoriesAndSkipsUnindexedRoots()
    {
        using var fixture = new Fixture();
        fixture.VerifyWarmupInventoryScope();
    }

    private sealed class Fixture : IDisposable, IHttpClientFactory, IInternalJellyfinUrlProvider, IProgress<double>
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        private readonly Plugin? _previousPlugin = Plugin.Instance;
        private readonly Func<PluginConfiguration?> _previousLog = YummyKodikLogFilter.ConfigurationProvider;
        private readonly HttpClient _http;
        private readonly YummyKodikEpisodeVersionsMergeHostedService _merge;
        private readonly RefreshYummyKodikLibraryTask _task;
        public RefreshYummyKodikLibraryTask PrimaryTask => _task;
        public RefreshSecondYummyKodikLibraryTask SecondTask { get; }
        private readonly ILibraryManager _library;
        public PluginConfiguration Configuration { get; }
        public Dictionary<int, string[]> Lists { get; } = new();
        public List<int> ListRequests { get; } = new();
        public int? FailedList { get; set; }
        public bool FailKodik { get; set; }
        public string? FailedTitle { get; set; }
        public List<double> Progress { get; } = new();
        public List<string> ReportedChanges { get; } = new();
        public int OrdinaryInventoryQueries => ((LibraryProxy)(object)_library).OrdinaryInventoryQueries;
        public int FullMergeQueries => ((LibraryProxy)(object)_library).MergeQueries;
        public List<string> InventoryRoots => ((LibraryProxy)(object)_library).InventoryRoots;

        public Fixture()
        {
            Directory.CreateDirectory(_root);
            var paths = DispatchProxy.Create<IApplicationPaths, LateNativeMergeTests.HostDependencyProxy>();
            ((LateNativeMergeTests.HostDependencyProxy)(object)paths).Root = _root;
            var serializer = DispatchProxy.Create<IXmlSerializer, LateNativeMergeTests.HostDependencyProxy>();
            var plugin = new Plugin(paths, serializer, NullLogger<Plugin>.Instance);
            Configuration = plugin.Configuration;
            Configuration.YummyUserId = 42;
            Configuration.OutputRootPath = Path.Combine(_root, "watching");
            Configuration.SecondOutputRootPath = Path.Combine(_root, "watched");
            Configuration.UseUserListSubscription = Configuration.UseSecondUserListSubscription = true;
            Configuration.SecondYummyUserListId = 2;
            Configuration.DeleteReleasesNotInYummyList = true;
            Configuration.YummyApiBaseUrl = "https://yummy.test";
            _http = new HttpClient(new Handler(Respond));
            _library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            ((LibraryProxy)(object)_library).Root = _root;
            _merge = new YummyKodikEpisodeVersionsMergeHostedService(_library, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            var barrier = new YummyKodikPostRefreshMergeBarrier(_library, _merge, NullLogger<YummyKodikPostRefreshMergeBarrier>.Instance);
            var monitor = DispatchProxy.Create<ILibraryMonitor, MonitorProxy>();
            ((MonitorProxy)(object)monitor).ReportedChanges = ReportedChanges;
            _task = new RefreshYummyKodikLibraryTask(NullLogger<RefreshYummyKodikLibraryTask>.Instance, this, this, barrier, monitor);
            SecondTask = new RefreshSecondYummyKodikLibraryTask(_task);
        }

        private string Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/lists/"))
            {
                var list = int.Parse(path.Split('/').Last());
                ListRequests.Add(list);
                if (FailedList == list) throw new HttpRequestException("Synthetic list outage");
                return JsonSerializer.Serialize(new { response = Lists[list].Select(key => new { anime_url = key }) });
            }
            if (FailKodik && !path.StartsWith("/anime/")) throw new HttpRequestException("Synthetic Kodik outage");
            Require(path.StartsWith("/anime/"), "Complete Yummy coverage must not call additional providers.");
            var slug = path.Split('/').Last();
            if (slug == FailedTitle) throw new InvalidOperationException("Synthetic title failure");
            var id = slug switch { "first" => 101, "second" => 102, "shared" => 103, _ => 104 };
            return JsonSerializer.Serialize(new { response = new { anime_id = id, anime_url = slug, title = slug, episodes = new { count = 1, aired = 1 },
                videos = new[] { new { number = "1", duration = 1200, iframe_url = $"https://play.example/player?anime_id={id}&episode=1&dubbing_code=158&dubbing=AniStar", data = new { player_id = 3, dubbing = "AniStar" } } } } });
        }

        public async Task RefreshAsync(int? target = null)
        {
            Progress.Clear();
            ReportedChanges.Clear();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            if (target != 1)
            {
                var failed = false;
                try { await _task.ExecuteAsync(this, timeout.Token); }
                catch (InvalidOperationException ex) when (FailedList == 0 && ex.Message.StartsWith("Yummy list 0 was not updated")) { failed = true; }
                Require(failed == (FailedList == 0), "Failed primary list must produce a failed scheduled task after local fallback.");
            }
            if (target != 0)
            {
                Progress.Clear();
                var failed = false;
                try { await SecondTask.ExecuteAsync(this, timeout.Token); }
                catch (InvalidOperationException ex) when (FailedList == 2 && ex.Message.StartsWith("Yummy list 2 was not updated")) { failed = true; }
                Require(failed == (FailedList == 2), "Failed secondary list must produce a failed scheduled task after local fallback.");
            }
        }

        public HashSet<string> Keys(int index) => Directory.EnumerateFiles(index == 0 ? Configuration.OutputRootPath : Configuration.SecondOutputRootPath,
            RefreshStateManager.StateFileName, SearchOption.AllDirectories).SelectMany(path =>
                JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("seasons").EnumerateObject()
                    .Select(season => season.Value.GetProperty("cleanKey").GetString()!)).ToHashSet();

        public void VerifySecondRootConsumers()
        {
            var item = ((LibraryProxy)(object)_library).Items().First(item => LibraryTarget.ContainsPath(Configuration.SecondOutputRootPath, item.Path));
            var controller = new YummyKodikStreamController(NullLogger<YummyKodikStreamController>.Instance, this, null!, _library, null!, _merge);
            var method = typeof(YummyKodikStreamController).GetMethod("GetManagedSeriesStreams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var args = new object?[] { item.Id.ToString(), Configuration, null };
            var inventory = ((LibraryProxy)(object)_library).InventoryRoots;
            inventory.Clear();
            var hostQueryCount = ((LibraryProxy)(object)_library).MergeQueries + ((LibraryProxy)(object)_library).OrdinaryInventoryQueries;
            var streams = (System.Collections.IEnumerable)method.Invoke(controller, args)!;
            Require(streams.Cast<object>().Any() && args[2] is string directory && LibraryTarget.ContainsPath(Configuration.SecondOutputRootPath, directory),
                "The voice widget must discover managed streams in the second library.");
            Require(inventory.Count == 0 && hostQueryCount == ((LibraryProxy)(object)_library).MergeQueries + ((LibraryProxy)(object)_library).OrdinaryInventoryQueries,
                "The voice widget must read the selected series STRMs without materializing host episode metadata.");
            using var warmup = new YummyKodikMediaSegmentWarmupHostedService(_library, null!, NullLogger<YummyKodikMediaSegmentWarmupHostedService>.Instance);
            Require((bool)typeof(YummyKodikMediaSegmentWarmupHostedService).GetMethod("IsEligibleEpisode", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(warmup, [item])!,
                "Second-library episodes must receive skip-timing warmup.");
        }

        public void VerifyWholeSeriesWidget()
        {
            var season = Path.Combine(Configuration.OutputRootPath, "Example", "Season 01");
            Directory.CreateDirectory(season);
            for (var episode = 1; episode <= 2; episode++)
                for (var voice = 0; voice < (episode == 1 ? 10 : 4); voice++)
                    File.WriteAllText(Path.Combine(season, $"S01E{episode:00} - Voice{voice}.strm"),
                        $"http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=4861&ep={episode}&voice=Voice{voice}");
            var foreignSeason = Path.Combine(Configuration.OutputRootPath, "Other", "Season 01");
            Directory.CreateDirectory(foreignSeason);
            File.WriteAllText(Path.Combine(foreignSeason, "S01E01 - Foreign.strm"),
                "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=999&ep=1&voice=Foreign");
            var proxy = (LibraryProxy)(object)_library;
            var controller = new YummyKodikStreamController(NullLogger<YummyKodikStreamController>.Instance, this, null!, _library, null!, _merge);
            var method = typeof(YummyKodikStreamController).GetMethod("GetManagedSeriesStreams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            foreach (var episode in new[] { "S01E01", "S01E02" })
            {
                var item = proxy.Items().First(x => x.Path.Contains(episode) && LibraryTarget.ContainsPath(season, x.Path));
                var result = (System.Collections.IEnumerable)method.Invoke(controller, [item.Id.ToString(), Configuration, null])!;
                var voices = result.Cast<object>().Select(x => (string)x.GetType().GetProperty("VoiceName")!.GetValue(x)!).ToHashSet();
                Require(voices.SetEquals(Enumerable.Range(0, 10).Select(x => $"Voice{x}")),
                    "Both episodes must expose the same ten series voices, excluding other titles.");
            }
            Require(proxy.MergeQueries == 0 && proxy.OrdinaryInventoryQueries == 0,
                "Voice discovery must not query host episode metadata.");
            var auth = DispatchProxy.Create<MediaBrowser.Controller.Net.IAuthorizationContext, WidgetAuthorizationProxy>();
            var localController = new YummyKodikStreamController(NullLogger<YummyKodikStreamController>.Instance,
                new RejectWidgetHttpFactory(), auth, _library, null!, _merge)
            {
                ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
                { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() }
            };
            foreach (var provider in new[] { "cvh", "kodik" })
            {
                if (provider == "kodik")
                    foreach (var file in Directory.EnumerateFiles(season, "*.strm"))
                        File.WriteAllText(file, "http://localhost:8096/YummyKodik/stream?type=shikimori&id=4861&ep=1&tr=42");
                var episode = proxy.Items().First(x => LibraryTarget.ContainsPath(season, x.Path));
                var result = localController.GetTranslations(episode.Id.ToString()).GetAwaiter().GetResult();
                Require(result is Microsoft.AspNetCore.Mvc.OkObjectResult, "Local widget must succeed with provider access prohibited.");
                var json = JsonSerializer.SerializeToElement(((Microsoft.AspNetCore.Mvc.OkObjectResult)result).Value);
                Require(json.GetProperty("translations").GetArrayLength() == 10 && json.GetProperty("reason").GetString() == "local",
                    "Yummy and Kodik must expose the local series union without creating an HTTP client.");
            }
        }

        public void VerifyWarmupInventoryScope()
        {
            using var warmup = new YummyKodikMediaSegmentWarmupHostedService(_library, null!, NullLogger<YummyKodikMediaSegmentWarmupHostedService>.Instance);
            var enqueue = typeof(YummyKodikMediaSegmentWarmupHostedService).GetMethod("EnqueueMissingSegmentItems", BindingFlags.NonPublic | BindingFlags.Instance)!;
            enqueue.Invoke(warmup, ["Test"]);
            Require(OrdinaryInventoryQueries == 0, "Unindexed warmup roots must not query the host inventory.");
            Directory.CreateDirectory(Configuration.OutputRootPath);
            Directory.CreateDirectory(Configuration.SecondOutputRootPath);
            var foreign = Path.Combine(_root, "unrelated");
            Directory.CreateDirectory(foreign);
            File.WriteAllText(Path.Combine(foreign, "foreign.strm"), "unrelated");
            enqueue.Invoke(warmup, ["Test"]);
            Require(OrdinaryInventoryQueries == 1 && InventoryRoots.ToHashSet().SetEquals(
                [Configuration.OutputRootPath, Configuration.SecondOutputRootPath]),
                "Warmup must scope one host query to both configured roots before episode materialization.");
        }

        public HttpClient CreateClient(string name) => _http;
        public string GetBaseUrl() => "http://127.0.0.1:8096";
        public void Report(double value) => Progress.Add(value);
        public void Dispose()
        {
            _merge.Dispose();
            _http.Dispose();
            typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previousPlugin);
            YummyKodikLogFilter.ConfigurationProvider = _previousLog;
            Directory.Delete(_root, true);
        }
    }

    private sealed class RejectWidgetHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("Widget must not contact providers.");
    }

    public class WidgetAuthorizationProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => Task.FromResult(new MediaBrowser.Controller.Net.AuthorizationInfo
            {
                IsAuthenticated = true,
                User = new Jellyfin.Database.Implementations.Entities.User("widget-test", "test", "test") { Id = Guid.NewGuid() }
            });
    }

    public class LibraryProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;
        public int MergeQueries { get; private set; }
        public int OrdinaryInventoryQueries { get; private set; }
        private readonly Dictionary<Guid, string> _folders = new();
        public List<string> InventoryRoots { get; } = new();
        public List<BaseItem> Items() => Directory.EnumerateFiles(Root, "*.strm", SearchOption.AllDirectories).Select(path => (BaseItem)new Episode
        {
            Id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(path))), Path = path,
            IndexNumber = 1, ParentIndexNumber = 1, DateCreated = DateTime.UtcNow, DateLastRefreshed = DateTime.UtcNow, RunTimeTicks = TimeSpan.FromMinutes(20).Ticks
        }).ToList();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ILibraryManager.FindByPath))
            {
                var root = (string)args![0]!;
                if (!Directory.Exists(root)) return null;
                var id = new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(root)));
                _folders[id] = root;
                return new Folder { Id = id, Path = root };
            }
            if (method!.Name == nameof(ILibraryManager.GetItemList))
            {
                var query = (InternalItemsQuery)args![0]!;
                if (query.IncludeOwnedItems) MergeQueries++;
                else OrdinaryInventoryQueries++;
                if (query.AncestorIds.Length > 0)
                {
                    var roots = query.AncestorIds.Select(id => _folders[id]).ToArray();
                    InventoryRoots.AddRange(roots);
                    return Items().Where(item => roots.Any(root => LibraryTarget.ContainsPath(root, item.Path))).ToList();
                }
                return Items();
            }
            if (method.Name == nameof(ILibraryManager.GetItemById)) return Items().FirstOrDefault(item => item.Id == (Guid)args![0]!);
            if (method.ReturnType == typeof(Task)) return Task.CompletedTask;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    public class MonitorProxy : DispatchProxy
    {
        public List<string> ReportedChanges { get; set; } = new();
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == nameof(ILibraryMonitor.ReportFileSystemChanged))
            {
                var path = (string)args![0]!;
                Require(!Directory.Exists(path) && !File.Exists(path), "Only completed filesystem deletions may be reported.");
                ReportedChanges.Add(path);
            }
            return null;
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request)) });
        }
    }
}
