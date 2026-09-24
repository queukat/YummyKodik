using System.Reflection;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik;
using YummyKodik.Configuration;
using YummyKodik.Logging;
using YummyKodik.Versioning;

internal static class LateNativeMergeTests
{
    public static void CycleDuringAuthoritativePassSurvivesBatchRelease()
        => RunLateCycleAsync(duringPass: true).GetAwaiter().GetResult();

    public static void CycleAfterAuthoritativePassSurvivesBatchRelease()
        => RunLateCycleAsync(duringPass: false).GetAwaiter().GetResult();

    private static async Task RunLateCycleAsync(bool duringPass)
    {
        using var fixture = new Fixture();
        var batch = await fixture.Service.BeginRefreshBatchAsync(CancellationToken.None);
        try
        {
            // The authoritative pass absorbs requests that precede its inventory.
            if (!duringPass) fixture.Library.RaiseAdded(fixture.Primary);
            if (duringPass)
            {
                fixture.Library.AfterBatch = () =>
                {
                    fixture.Library.AfterBatch = null;
                    fixture.RestoreCycle();
                    fixture.RaiseCycle();
                };
            }
            await fixture.Service.MergeAfterRefreshAsync(CancellationToken.None);
            Require(fixture.Library.BatchCount == 1, "The authoritative pass must execute one host batch.");
            if (!duringPass)
            {
                Require(!fixture.Pending, "Earlier events and normalized own ItemUpdated events must be absorbed.");
                fixture.RestoreCycle();
                fixture.RaiseCycle();
            }
            Require(fixture.Pending, "A cycle arriving after the group was processed must remain pending.");
        }
        finally
        {
            batch.Dispose();
            fixture.PauseTimer();
        }
        Require(fixture.Pending, "Releasing a completed authoritative batch must not discard a late conflict.");
        await fixture.RunWorkerAsync();
        Require(fixture.Library.BatchCount == 2 && !fixture.Pending,
            "The existing worker must drain the late conflict once, without self-triggering.");
        Require(fixture.Child.LocalAlternateVersions.Length == 0 && fixture.Child.OwnerId == fixture.Primary.Id,
            "The follow-up must actually remove the late reciprocal native edge.");
        await fixture.RunWorkerAsync();
        Require(fixture.Library.BatchCount == 2, "A drained worker must not persist an unchanged group again.");
    }

    public static void RepeatedCyclesCoalesceAndBusyWorkerRetainsRequest()
        => RunBusyWorkerAsync().GetAwaiter().GetResult();

    private static async Task RunBusyWorkerAsync()
    {
        using var fixture = new Fixture();
        var gate = (SemaphoreSlim)Field("_mergeLock").GetValue(fixture.Service)!;
        await gate.WaitAsync();
        try
        {
            fixture.RaiseCycle();
            Require(fixture.Pending, "The first native cycle must enqueue a merge.");
            Field("_pendingMergeReason").SetValue(fixture.Service, "already-pending");
            for (var i = 0; i < 10; i++) fixture.RaiseCycle();
            Require((string?)Field("_pendingMergeReason").GetValue(fixture.Service) == "already-pending",
                "Repeated cycle observations must not reissue/reset the pending debounce request.");
            await fixture.RunWorkerAsync();
            Require(fixture.Pending && fixture.Library.BatchCount == 0,
                "A worker that cannot acquire the existing gate must retain the pending conflict.");
        }
        finally
        {
            gate.Release();
        }
        await fixture.RunWorkerAsync();
        Require(!fixture.Pending && fixture.Library.BatchCount == 1,
            "The retained request must be consumed by the next admitted worker exactly once.");
    }

    public static void NormalizedUpdatesDoNotQueueAndStopUnsubscribes()
        => RunNormalizedUpdatesAsync().GetAwaiter().GetResult();

    private static async Task RunNormalizedUpdatesAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.MergeAllEligibleEpisodesAsync("FixtureNormalization", fixture.Configuration);
        Require(!fixture.Pending, "Own link-only None notifications must not trigger another merge.");
        fixture.Library.RaiseUpdated(fixture.Primary);
        fixture.Library.RaiseUpdated(fixture.Child);
        Require(!fixture.Pending, "Ordinary updates to a canonical native graph must do no queued work.");
        await fixture.Service.StopAsync(CancellationToken.None);
        fixture.RestoreCycle();
        fixture.Library.RaiseUpdated(fixture.Primary);
        Require(!fixture.Pending && fixture.Library.UpdateSubscriberCount == 0,
            "Stopping the service must unsubscribe the ItemUpdated observer.");
    }

    public static void IncompleteCycleUsesSplitProviderAndTopologyWitnesses()
        => RunIncompleteObserverAsync().GetAwaiter().GetResult();

    private static async Task RunIncompleteObserverAsync()
    {
        using var fixture = new Fixture();
        var sibling = fixture.CreateEpisode("AnimeVost", 24253);
        fixture.Library.Episodes.Add(sibling);
        fixture.Child.IndexNumber = fixture.Child.ParentIndexNumber = null;
        // The updated child has a direct edge only to the Alloha primary. Its
        // matching-provider witness is reached through that primary's native list.
        File.WriteAllText(fixture.Primary.Path,
            "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=15066&ep=1&voice=AniLiberty");
        fixture.Primary.LocalAlternateVersions = new[] { fixture.Child.Path, sibling.Path };
        fixture.Child.LocalAlternateVersions = new[] { fixture.Primary.Path };
        fixture.Library.RaiseUpdated(fixture.Child);
        fixture.PauseTimer();
        Require(fixture.Pending, "The incomplete cycle must be observed using split provider/topology witnesses.");
        await fixture.RunWorkerAsync();
        Require(fixture.Child.LocalAlternateVersions.Length == 0 && !fixture.Child.IndexNumber.HasValue &&
                !fixture.Child.ParentIndexNumber.HasValue,
            "The same canonical merge must remove the cycle without assigning numeric metadata.");
    }

    public static void CycleDuringBatchRepairsOnlyTargetDespitePendingGeneralMerge()
        => RunScopedBatchAsync().GetAwaiter().GetResult();

    private static async Task RunScopedBatchAsync()
    {
        using var fixture = new Fixture();
        var preferred = fixture.CreateEpisode("Other Dub", 24253);
        preferred.SetPrimaryVersionId(fixture.Primary.Id.ToString("N"));
        fixture.Primary.LinkedAlternateVersions = new[]
        {
            new LinkedChild { ItemId = preferred.Id, Path = preferred.Path, Type = LinkedChildType.Manual }
        };
        fixture.Configuration.PreferredTranslationFilter = "Other Dub";
        var unrelatedPrimary = fixture.CreateEpisode("Unrelated A", 24253, number: 2);
        var unrelatedChild = fixture.CreateEpisode("Unrelated B", 24253, number: 2);
        unrelatedPrimary.LocalAlternateVersions = new[] { unrelatedChild.Path };
        unrelatedChild.LocalAlternateVersions = new[] { unrelatedPrimary.Path };
        fixture.Library.Episodes.AddRange(new[] { preferred, unrelatedPrimary, unrelatedChild });

        var batch = await fixture.Service.BeginRefreshBatchAsync(CancellationToken.None);
        try
        {
            fixture.Library.RaiseAdded(fixture.Primary);
            Require(fixture.GeneralPending, "ItemAdded must leave a deferred general request during the batch.");
            fixture.RaiseCycle();
            Require(fixture.CyclePendingCount == 1,
                "A pending general request must not mask an independent observed cycle.");
            await fixture.RunWorkerAsync();
            Require(fixture.Library.BatchCount == 1 && fixture.Child.LocalAlternateVersions.Length == 0,
                "The existing worker must remove the native cycle before the refresh batch is disposed.");
            Require(fixture.Child.OwnerId == preferred.Id && fixture.Primary.OwnerId == preferred.Id &&
                    preferred.OwnerId == Guid.Empty && string.IsNullOrEmpty(preferred.PrimaryVersionId),
                "Target loading must include the preferred explicit-only sibling, not merely the cyclic pair.");
            Require(fixture.Library.FullInventoryQueryCount == 0 && fixture.Library.SeasonQueryCount > 0,
                "Target repair during a batch must not load the full library inventory.");
            Require(unrelatedPrimary.LocalAlternateVersions.SequenceEqual(new[] { unrelatedChild.Path }) &&
                    unrelatedChild.LocalAlternateVersions.SequenceEqual(new[] { unrelatedPrimary.Path }) &&
                    !fixture.Library.SavedIds.Contains(unrelatedPrimary.Id) && !fixture.Library.SavedIds.Contains(unrelatedChild.Id),
                "An unrelated episode in the same season must not be normalized by the targeted request.");
            Require(fixture.GeneralPending && fixture.CyclePendingCount == 0,
                "Target completion must preserve general deferred work without queuing itself again.");
        }
        finally
        {
            batch.Dispose();
            fixture.PauseTimer();
        }
    }

    public static void QueuedCycleAlreadyRepairedCausesNoWrites()
        => RunAlreadyRepairedAsync().GetAwaiter().GetResult();

    private static async Task RunAlreadyRepairedAsync()
    {
        using var fixture = new Fixture();
        using var batch = await fixture.Service.BeginRefreshBatchAsync(CancellationToken.None);
        fixture.RaiseCycle();
        fixture.Child.LocalAlternateVersions = Array.Empty<string>();
        fixture.Child.OwnerId = fixture.Primary.Id;
        fixture.Child.SetPrimaryVersionId(fixture.Primary.Id.ToString("N"));
        await fixture.RunWorkerAsync();
        Require(fixture.CyclePendingCount == 0 && fixture.Library.BatchCount == 0 &&
                fixture.Library.FullInventoryQueryCount == 0,
            "A target whose reciprocal edge disappeared before fresh processing must be consumed without writes or a full pass.");
    }

    private static FieldInfo Field(string name)
        => typeof(YummyKodikEpisodeVersionsMergeHostedService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"Missing service field {name}.");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        private readonly Plugin? _previousPlugin = Plugin.Instance;
        private readonly Func<PluginConfiguration?> _previousLogConfiguration = YummyKodikLogFilter.ConfigurationProvider;
        public YummyKodikEpisodeVersionsMergeHostedService Service { get; }
        public LibraryProxy Library { get; }
        public PluginConfiguration Configuration { get; }
        public Episode Primary { get; }
        public Episode Child { get; }
        public bool GeneralPending => (bool)Field("_mergeRequested").GetValue(Service)!;
        public int CyclePendingCount
        {
            get
            {
                var pending = Field("_pendingNativeCycleGroups").GetValue(Service)!;
                return (int)pending.GetType().GetProperty("Count")!.GetValue(pending)!;
            }
        }
        public bool Pending => GeneralPending || CyclePendingCount > 0;

        public Fixture()
        {
            Directory.CreateDirectory(Path.Combine(_root, "Season 01"));
            var paths = DispatchProxy.Create<IApplicationPaths, HostDependencyProxy>();
            ((HostDependencyProxy)(object)paths).Root = _root;
            var serializer = DispatchProxy.Create<IXmlSerializer, HostDependencyProxy>();
            var plugin = new Plugin(paths, serializer, NullLogger<Plugin>.Instance);
            Configuration = plugin.Configuration;
            Configuration.OutputRootPath = _root;
            Configuration.PreferredTranslationFilter = "AniLiberty";
            var library = DispatchProxy.Create<ILibraryManager, LibraryProxy>();
            Library = (LibraryProxy)(object)library;
            Library.Season = new Folder { Id = Guid.NewGuid(), Path = Path.Combine(_root, "Season 01") };
            Primary = CreateEpisode("AniLiberty", 24253);
            Child = CreateEpisode("AniBaza", 24253);
            Library.Episodes.AddRange(new[] { Primary, Child });
            RestoreCycle();
            Service = new YummyKodikEpisodeVersionsMergeHostedService(library, null!,
                NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            Service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            PauseTimer();
            Field("_mergeRequested").SetValue(Service, false);
            Require(Library.UpdateSubscriberCount == 1, "Start must subscribe to ItemUpdated.");
        }

        public Episode CreateEpisode(string voice, int animeId, int number = 1)
        {
            var result = new Episode
            {
                Id = Guid.NewGuid(), ParentId = Library.Season.Id, Name = voice, IndexNumber = number, ParentIndexNumber = 1,
                Path = Path.Combine(_root, "Season 01", $"S01E{number:00} - {voice}.strm")
            };
            File.WriteAllText(result.Path,
                $"http://localhost:8096/YummyKodik/stream?provider=cvh&animeId={animeId}&ep={number}&voice={voice}");
            return result;
        }

        public void RestoreCycle()
        {
            Primary.LocalAlternateVersions = new[] { Child.Path };
            Child.LocalAlternateVersions = new[] { Primary.Path };
            Primary.OwnerId = Child.OwnerId = Guid.Empty;
            Primary.SetPrimaryVersionId(null);
            Child.SetPrimaryVersionId(null);
        }

        public void RaiseCycle()
        {
            Library.RaiseUpdated(Primary);
            PauseTimer();
        }

        public void PauseTimer()
            => ((Timer?)Field("_mergeDebounceTimer").GetValue(Service))?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        public async Task RunWorkerAsync()
        {
            PauseTimer();
            var method = typeof(YummyKodikEpisodeVersionsMergeHostedService).GetMethod("MergeWorkerAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)method.Invoke(Service, new object[] { "LateNativeFixture" })!;
            PauseTimer();
        }

        public void Dispose()
        {
            Service.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Service.Dispose();
            typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, _previousPlugin);
            YummyKodikLogFilter.ConfigurationProvider = _previousLogConfiguration;
            var season = Path.Combine(_root, "Season 01");
            foreach (var file in Directory.EnumerateFiles(season)) File.Delete(file);
            Directory.Delete(season);
            Directory.Delete(_root);
        }
    }

    public class HostDependencyProxy : DispatchProxy
    {
        public string Root { get; set; } = string.Empty;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.ReturnType == typeof(string)) return Root;
            if (targetMethod?.Name.StartsWith("Deserialize", StringComparison.Ordinal) == true) return new PluginConfiguration();
            return targetMethod?.ReturnType.IsValueType == true ? Activator.CreateInstance(targetMethod.ReturnType) : null;
        }
    }

    public class LibraryProxy : DispatchProxy
    {
        public List<Episode> Episodes { get; } = new();
        public Folder Season { get; set; } = new();
        public int BatchCount { get; private set; }
        public int FullInventoryQueryCount { get; private set; }
        public int SeasonQueryCount { get; private set; }
        public HashSet<Guid> SavedIds { get; } = new();
        public Action? AfterBatch { get; set; }
        private EventHandler<ItemChangeEventArgs>? _updated;
        private EventHandler<ItemChangeEventArgs>? _added;
        public int UpdateSubscriberCount => _updated?.GetInvocationList().Length ?? 0;
        public void RaiseUpdated(Episode item) => _updated?.Invoke(this, new ItemChangeEventArgs { Item = item, UpdateReason = ItemUpdateType.None });
        public void RaiseAdded(Episode item) => _added?.Invoke(this, new ItemChangeEventArgs { Item = item });

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case "add_ItemUpdated": _updated += (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
                case "remove_ItemUpdated": _updated -= (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
                case "add_ItemAdded": _added += (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
                case "remove_ItemAdded": _added -= (EventHandler<ItemChangeEventArgs>)args![0]!; return null;
                case "get_IsScanRunning": return false;
                case nameof(ILibraryManager.GetNewItemId):
                    return Episodes.FirstOrDefault(item => string.Equals(item.Path, (string)args![0]!, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty;
                case nameof(ILibraryManager.GetItemById):
                    return (Guid)args![0]! == Season.Id ? Season : Episodes.FirstOrDefault(item => item.Id == (Guid)args[0]!);
                case nameof(ILibraryManager.GetItemList):
                    var query = (InternalItemsQuery)args![0]!;
                    if (query.ItemIds.Length == 0)
                    {
                        if (query.ParentId == Guid.Empty) FullInventoryQueryCount++;
                        else
                        {
                            SeasonQueryCount++;
                        }
                    }
                    return Episodes.Where(item => (query.ItemIds.Length == 0 || query.ItemIds.Contains(item.Id)) &&
                        (query.ParentId == Guid.Empty || item.ParentId == query.ParentId)).Cast<BaseItem>().ToList();
                case nameof(ILibraryManager.UpdateItemsAsync):
                    Require((ItemUpdateType)args![2]! == ItemUpdateType.None, "The follow-up must use the existing link-only batch path.");
                    BatchCount++;
                    foreach (var item in ((IReadOnlyList<BaseItem>)args[0]!).OfType<Episode>())
                    {
                        SavedIds.Add(item.Id);
                        RaiseUpdated(item);
                    }
                    AfterBatch?.Invoke();
                    return Task.CompletedTask;
                default: throw new NotSupportedException(targetMethod?.Name);
            }
        }
    }
}
