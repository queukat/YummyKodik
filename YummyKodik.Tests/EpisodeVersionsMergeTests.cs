using System.Reflection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Configuration;
using YummyKodik.Versioning;

internal static class EpisodeVersionsMergeTests
{
    public static void SavedVoiceReplacesAlreadyMergedPrimaryAcrossEpisodes()
        => RunAsync().GetAwaiter().GetResult();

    public static void PreferenceChangeRevisitsEarlierSeriesBeforeNextUnrelatedGroup()
        => RunPriorityAsync().GetAwaiter().GetResult();

    public static void PrimaryWithStaleOwnerBecomesVisibleAndThenNoOp()
        => RunStaleOwnerAsync().GetAwaiter().GetResult();

    public static void LinkOnlyBatchUsesFreshMetadataAndDoesNotRecurse()
        => RunFreshBatchAsync(false).GetAwaiter().GetResult();

    public static void IncompleteConcurrentScanIsNotPersisted()
        => RunFreshBatchAsync(true).GetAwaiter().GetResult();

    public static void NativeAlternatesAlreadyCoveringGroupAreNoOp()
        => RunNativeAlternatesAsync().GetAwaiter().GetResult();

    public static void NativeOwnershipTransfersToExplicitChoiceAndPreservesExternalLinks()
        => RunMixedNativeOwnershipAsync().GetAwaiter().GetResult();

    public static void IncompleteNativeMemberUsesProvenFileIdentity()
        => RunIncompleteNativeAsync(null).GetAwaiter().GetResult();

    public static void IncompleteNativeMemberRejectsAmbiguousIdentity()
    {
        foreach (var condition in new[] { "number", "stream", "provider", "edge", "range", "folder" })
            RunIncompleteNativeAsync(condition).GetAwaiter().GetResult();
    }

    public static void IncompleteNativeMemberRevalidatesProofBeforeSaving()
    {
        foreach (var condition in new[] { "fresh-stream", "fresh-edge", "fresh-file" })
            RunIncompleteNativeAsync(condition).GetAwaiter().GetResult();
    }

    private static async Task RunIncompleteNativeAsync(string? condition)
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var season = Path.Combine(root, condition == "folder" ? "Season 03" : "Season 04");
        Directory.CreateDirectory(season);
        try
        {
            var primary = CreateEpisode(season, 12, "AniLiberty");
            var sibling = CreateEpisode(season, 12, "AnimeVost");
            var incomplete = CreateEpisode(season, 12, "AniDUB");
            File.WriteAllText(primary.Path, "http://localhost:8096/YummyKodik/stream?provider=alloha&animeId=15066&ep=12&voice=AniLiberty");
            incomplete.IndexNumber = null;
            incomplete.ParentIndexNumber = null;
            primary.LocalAlternateVersions = new[] { incomplete.Path, sibling.Path };
            incomplete.LocalAlternateVersions = new[] { primary.Path, sibling.Path };
            sibling.OwnerId = primary.Id;
            sibling.SetPrimaryVersionId(primary.Id);
            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            var proxy = (GroupedLibraryProxy)(object)library;
            proxy.Episodes = new List<BaseItem> { primary, sibling, incomplete };
            proxy.SnapshotInventory = true;
            void RemoveEdges()
            {
                primary.LocalAlternateVersions = new[] { sibling.Path };
                incomplete.LocalAlternateVersions = Array.Empty<string>();
            }
            if (condition == "number") incomplete.IndexNumber = 11;
            if (condition == "range") incomplete.IndexNumberEnd = 13;
            if (condition == "stream") File.WriteAllText(incomplete.Path, "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=24253&ep=11");
            if (condition == "provider") File.WriteAllText(incomplete.Path, "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=987654&ep=12");
            if (condition == "edge") RemoveEdges();
            if (condition == "fresh-stream") proxy.BeforeReload = () => File.WriteAllText(incomplete.Path, "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=987654&ep=12");
            if (condition == "fresh-edge") proxy.BeforeReload = RemoveEdges;
            if (condition == "fresh-file") proxy.BeforeReload = () => File.Delete(incomplete.Path);
            var cfg = new PluginConfiguration { OutputRootPath = root, PreferredTranslationFilter = "AniDUB|AniLiberty" };
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            await service.MergeAllEligibleEpisodesAsync("IncompleteNative", cfg);
            if (condition != null)
            {
                Require(proxy.BatchCount == 0, $"Unproven incomplete member must not be persisted: {condition}.");
                Require(incomplete.OwnerId == Guid.Empty && !incomplete.PrimaryVersionId.HasValue,
                    "Rejected incomplete member must remain unchanged.");
                return;
            }
            Require(proxy.BatchCount == 1 && incomplete.OwnerId == primary.Id && incomplete.PrimaryVersionId == primary.Id,
                "A proven incomplete native member must lose its recursive ownership.");
            Require(!incomplete.IndexNumber.HasValue && !incomplete.ParentIndexNumber.HasValue,
                "Link normalization must not invent or save missing episode metadata.");
            Require(!primary.PrimaryVersionId.HasValue && primary.OwnerId == Guid.Empty &&
                    incomplete.LocalAlternateVersions.Length == 0,
                "Only a complete member can be primary; the incomplete back edge must disappear.");
            Require(primary.LocalAlternateVersions.ToHashSet().SetEquals(new[] { incomplete.Path, sibling.Path }),
                "All native versions must be retained under the chosen complete primary.");
            await service.MergeAllEligibleEpisodesAsync("IncompleteNativeAgain", cfg);
            Require(proxy.BatchCount == 1,
                "Split provider/topology witnesses must retain incomplete membership after cross-provider transfer and become no-op.");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(season)) File.Delete(path);
            Directory.Delete(season);
            Directory.Delete(root);
        }
    }

    private static async Task RunMixedNativeOwnershipAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldPrimary = CreateEpisode(root, 5, "AnimeVost");
            var native = CreateEpisode(root, 5, "AniStar");
            var selected = CreateEpisode(root, 5, "Dream Cast");
            var explicitChild = CreateEpisode(root, 5, "Other Dub");
            var external = Path.Combine(root, "not-an-episode.strm");
            File.WriteAllText(external, "unrelated external version");
            oldPrimary.LocalAlternateVersions = new[] { native.Path, external };
            native.OwnerId = oldPrimary.Id;
            // Reproduce the previously skipped topology: explicit primaries already
            // agree, but the old native owner still holds the physical children.
            foreach (var item in new[] { oldPrimary, native, explicitChild }) item.SetPrimaryVersionId(selected.Id);
            selected.LinkedAlternateVersions = new[] { oldPrimary, native, explicitChild }
                .Select(item => new LinkedChild { ItemId = item.Id, Type = LinkedChildType.LinkedAlternateVersion }).ToArray();
            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            var proxy = (GroupedLibraryProxy)(object)library;
            proxy.Episodes = new List<BaseItem> { oldPrimary, native, selected, explicitChild };
            var cfg = new PluginConfiguration { OutputRootPath = root, PreferredTranslationFilter = "Dream Cast" };
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);

            await service.MergeAllEligibleEpisodesAsync("RepairNativeOwnership", cfg);
            Require(proxy.BatchCount == 1, "Broken native ownership must invalidate the fast no-op check.");
            Require(selected.LocalAlternateVersions.ToHashSet().SetEquals(new[] { oldPrimary.Path, native.Path }),
                "An explicit choice must acquire both endpoints of the former native group.");
            Require(oldPrimary.LocalAlternateVersions.SequenceEqual(new[] { external }) &&
                    native.LocalAlternateVersions.Length == 0 && File.Exists(external),
                "Only in-group native links may move; unrelated local paths and files must survive.");
            Require(oldPrimary.OwnerId == selected.Id && native.OwnerId == selected.Id &&
                    selected.OwnerId == Guid.Empty && !selected.PrimaryVersionId.HasValue,
                "Native ownership must agree with the visible primary.");
            Require(selected.LinkedAlternateVersions.Single().ItemId == explicitChild.Id,
                "The non-native voice must remain explicitly linked exactly once.");
            var saved = proxy.Episodes.Cast<StoredEpisode>().Sum(item => item.SaveCount);
            await service.MergeAllEligibleEpisodesAsync("RepeatedScan", cfg);
            Require(proxy.BatchCount == 1 && proxy.Episodes.Cast<StoredEpisode>().Sum(item => item.SaveCount) == saved,
                "A normalized mixed group must trigger no additional persistence or subscriber work.");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root)) File.Delete(path);
            Directory.Delete(root);
        }
    }

    private static async Task RunNativeAlternatesAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var primary = CreateEpisode(root, 5, "AniStar");
            var child = CreateEpisode(root, 5, "AnimeVost");
            primary.LocalAlternateVersions = new[] { child.Path };
            // Jellyfin's mapper exposes this only as local, never in LinkedAlternateVersions.
            child.SetPrimaryVersionId(primary.Id);
            child.OwnerId = primary.Id;
            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            var proxy = (GroupedLibraryProxy)(object)library;
            proxy.Episodes = new List<BaseItem> { primary, child };
            var cfg = new PluginConfiguration { OutputRootPath = root, PreferredTranslationFilter = "AniStar" };
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            await service.MergeAllEligibleEpisodesAsync("ScanCompleted", cfg);
            Require(proxy.BatchCount == 0, "Already persisted native versions must not be added again as linked versions.");
            primary.OwnerId = child.Id;
            await service.MergeAllEligibleEpisodesAsync("RepairOwner", cfg);
            Require(proxy.BatchCount == 1 && primary.OwnerId == Guid.Empty,
                "An empty linked remainder must still allow primary ownership repair.");
            cfg.PreferredTranslationFilter = "AnimeVost";
            await service.MergeAllEligibleEpisodesAsync("VoiceChanged", cfg);
            Require(!child.PrimaryVersionId.HasValue && primary.PrimaryVersionId == child.Id &&
                    child.LocalAlternateVersions.SequenceEqual(new[] { primary.Path }) &&
                    child.LinkedAlternateVersions.Length == 0 && primary.LocalAlternateVersions.Length == 0 &&
                    primary.OwnerId == child.Id,
                "Selecting a native alternate must transfer its entire native ownership group.");
            // Exercise the host's native refresh rules after promotion. A leftover
            // back-edge would make the chosen primary owned/hidden again.
            foreach (var owner in proxy.Episodes.OfType<Video>())
            {
                foreach (var path in owner.LocalAlternateVersions)
                {
                    var owned = proxy.Episodes.OfType<Video>().Single(item => item.Path == path);
                    if (owned.OwnerId == Guid.Empty) owned.OwnerId = owner.Id;
                    if (!owned.PrimaryVersionId.HasValue) owned.SetPrimaryVersionId(owner.Id);
                }
            }
            Require(child.OwnerId == Guid.Empty && !child.PrimaryVersionId.HasValue,
                "Host native refresh must not undo the chosen primary or request another repair.");
            var count = proxy.BatchCount;
            await service.MergeAllEligibleEpisodesAsync("RepeatedVoice", cfg);
            Require(proxy.BatchCount == count, "The switched group must then be a no-op.");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root))
            {
                File.Delete(path);
            }
            Directory.Delete(root);
        }
    }

    private static async Task RunFreshBatchAsync(bool incomplete)
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var inventory = Enumerable.Range(0, 32)
                .Select(i => CreateEpisode(root, 5, i == 0 ? "AniStar" : "Voice" + i)).ToList();
            // Native local-version arrays must not cause recursive sibling metadata saves.
            inventory[0].LocalAlternateVersions = inventory.Skip(1).Take(30).Select(item => item.Path).ToArray();
            var nfoPath = Path.ChangeExtension(inventory[0].Path, ".nfo");
            File.WriteAllText(nfoPath, "unchanged sidecar");
            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            var proxy = (GroupedLibraryProxy)(object)library;
            proxy.Episodes = inventory.Cast<BaseItem>().ToList();
            proxy.BeforeReload = () =>
            {
                proxy.BeforeReload = null;
                // Replacing, not mutating, models a scanner committing a newer repository row
                // after the merge captured its inventory.
                var fresh = CreateEpisode(root, 5, "AniStar");
                fresh.Id = inventory[0].Id;
                fresh.Overview = "new metadata from scanner";
                fresh.LocalAlternateVersions = inventory[0].LocalAlternateVersions;
                if (incomplete)
                {
                    fresh.IndexNumber = null;
                }
                proxy.Episodes[0] = fresh;
            };
            var cfg = new PluginConfiguration { OutputRootPath = root, PreferredTranslationFilter = "AniStar" };
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            await service.MergeAllEligibleEpisodesAsync("ScanCompleted", cfg);
            Require(inventory[0].SaveCount == 0, "The old inventory object must never be persisted.");
            Require(File.ReadAllText(nfoPath) == "unchanged sidecar", "Link edits must leave NFOs untouched.");
            if (incomplete)
            {
                Require(proxy.BatchCount == 0 && inventory.All(item => item.SaveCount == 0),
                    "An incomplete scanner result must cause no persistence or subscriber work.");
                return;
            }
            Require(proxy.BatchCount == 1, "32 versions must use one host batch, not recursive metadata writes.");
            Require(proxy.Episodes.Cast<StoredEpisode>().All(item => item.SaveCount == 1),
                "Each changed physical item must be persisted exactly once.");
            Require(proxy.Episodes[0].Overview == "new metadata from scanner",
                "The fresh metadata must survive the link-only batch.");
            await service.MergeAllEligibleEpisodesAsync("RepeatedScanCompleted", cfg);
            Require(proxy.BatchCount == 1, "A repeated unchanged merge must do no persistence.");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root))
            {
                File.Delete(path);
            }
            Directory.Delete(root);
        }
    }

    private static async Task RunStaleOwnerAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var oldPrimary = CreateEpisode(root, 5, "AnimeVost");
            var selected = CreateEpisode(root, 5, "AniStar");
            oldPrimary.SetPrimaryVersionId(selected.Id);
            selected.LinkedAlternateVersions = new[]
            {
                new LinkedChild { ItemId = oldPrimary.Id, Type = LinkedChildType.LinkedAlternateVersion }
            };
            // This is the persisted failure: version links have already moved, but ownership has not.
            selected.OwnerId = oldPrimary.Id;
            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            ((GroupedLibraryProxy)(object)library).Episodes = new List<BaseItem> { oldPrimary, selected };
            Require(library.GetItemList(new InternalItemsQuery()).Count == 0,
                "The ordinary episode query must reproduce both physical versions being hidden before repair.");

            var cfg = new PluginConfiguration { OutputRootPath = root };
            cfg.SetUserSeriesPreferredTranslationId(Guid.Empty, "yummy:24253", "AniStar");
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            await service.MergeAllEligibleEpisodesAsync("Startup", cfg);

            var visible = library.GetItemList(new InternalItemsQuery());
            Require(visible.Count == 1 && visible[0].Id == selected.Id,
                "The repaired AniStar primary must be visible to ordinary episode and next-episode queries.");
            Require(selected.OwnerId == Guid.Empty && !selected.PrimaryVersionId.HasValue,
                "The selected primary must be independent of its former owner.");
            Require(selected.SaveCount == 1 && oldPrimary.SaveCount == 0 &&
                oldPrimary.PrimaryVersionId == selected.Id &&
                selected.LinkedAlternateVersions.Single().ItemId == oldPrimary.Id,
                "Only the stale ownership must be persisted while the correct alternate links are preserved.");

            await service.MergeAllEligibleEpisodesAsync("RepeatedStartup", cfg);
            Require(selected.SaveCount == 1 && oldPrimary.SaveCount == 0,
                "The repaired group must be a no-op on the next merge pass.");
        }
        finally
        {
            foreach (var path in Directory.EnumerateFiles(root))
            {
                File.Delete(path);
            }
            Directory.Delete(root);
        }
    }

    private static async Task RunPriorityAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        var seriesDirectories = new[] { "Target", "Trigger", "Later" }
            .Select(name => Path.Combine(root, name)).ToArray();
        var directories = seriesDirectories.Select(directory => Path.Combine(directory, "Season 04")).ToArray();
        foreach (var directory in directories)
        {
            Directory.CreateDirectory(directory);
        }
        try
        {
            var episodes = new List<BaseItem>();
            foreach (var (directory, index) in directories.Select((directory, index) => (directory, index)))
            {
                var anime = CreateEpisode(directory, 5, "AnimeVost", 24253 + index);
                var ani = CreateEpisode(directory, 5, "AniStar", 24253 + index);
                anime.SetPrimaryVersionId(ani.Id);
                ani.LinkedAlternateVersions = new[]
                {
                    new LinkedChild { ItemId = anime.Id, Type = LinkedChildType.LinkedAlternateVersion }
                };
                episodes.Add(anime);
                episodes.Add(ani);
            }

            var targetAnime = (StoredEpisode)episodes[0];
            var targetAni = (StoredEpisode)episodes[1];
            var trigger = (StoredEpisode)episodes[2];
            var later = (StoredEpisode)episodes[5];
            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            ((GroupedLibraryProxy)(object)library).Episodes = episodes;
            var cfg = new PluginConfiguration { OutputRootPath = root, PreferredTranslationFilter = "AnimeVost" };
            ((GroupedLibraryProxy)(object)library).SnapshotInventory = true;
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            var requested = false;
            var reachedLater = false;
            trigger.OnSave = () =>
            {
                Require(targetAni.PrimaryVersionId == targetAnime.Id &&
                    !targetAnime.PrimaryVersionId.HasValue &&
                    targetAnime.SaveCount == 1 && targetAni.SaveCount == 1,
                    "The target series must have completed its first merge before the new choice arrives.");
                cfg.SetUserSeriesPreferredTranslationId(Guid.Empty, "yummy:24253", "AniStar");
                service.RequestTranslationPreferenceMerge(seriesDirectories[0]);
                requested = true;
            };
            later.OnSave = () =>
            {
                Require(requested, "The explicit preference must arrive before the unrelated next group.");
                Require(!targetAni.PrimaryVersionId.HasValue && targetAnime.PrimaryVersionId == targetAni.Id &&
                    targetAnime.SaveCount == 2 && targetAni.SaveCount == 2,
                    "A newly selected voice must be persisted for the already visited series before the next unrelated group.");
                reachedLater = true;
            };

            await service.MergeAllEligibleEpisodesAsync("Startup", cfg);
            Require(reachedLater, "The ordinary pass must continue after handling the priority request.");
        }
        finally
        {
            foreach (var directory in directories)
            {
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    File.Delete(path);
                }
                Directory.Delete(directory);
                Directory.Delete(Path.GetDirectoryName(directory)!);
            }
            Directory.Delete(root);
        }
    }

    private static async Task RunAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var episodes = new List<BaseItem>();
            foreach (var number in new[] { 5, 6 })
            {
                var oldPrimary = CreateEpisode(root, number, "AnimeVost");
                var selected = CreateEpisode(root, number, "AniStar");
                selected.SetPrimaryVersionId(oldPrimary.Id);
                oldPrimary.LinkedAlternateVersions = new[]
                {
                    new LinkedChild { ItemId = selected.Id, Type = LinkedChildType.LinkedAlternateVersion }
                };
                episodes.Add(oldPrimary);
                episodes.Add(selected);
            }

            var library = DispatchProxy.Create<ILibraryManager, GroupedLibraryProxy>();
            ((GroupedLibraryProxy)(object)library).Episodes = episodes;
            var cfg = new PluginConfiguration { OutputRootPath = root };
            cfg.SetUserSeriesPreferredTranslationId(Guid.Empty, "yummy:24253", "AniStar");
            using var service = new YummyKodikEpisodeVersionsMergeHostedService(
                library, null!, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);

            await service.MergeAllEligibleEpisodesAsync("TranslationPreferenceChanged", cfg);

            foreach (var group in episodes.Cast<StoredEpisode>().GroupBy(episode => episode.IndexNumber))
            {
                var selected = group.Single(episode => episode.Name == "AniStar");
                var oldPrimary = group.Single(episode => episode.Name == "AnimeVost");
                Require(!selected.PrimaryVersionId.HasValue,
                    $"Saved AniStar must become primary for already merged E{group.Key}.");
                Require(oldPrimary.PrimaryVersionId == selected.Id,
                    "The old primary must point to the selected version.");
                Require(selected.LinkedAlternateVersions.Length == 1 &&
                    selected.LinkedAlternateVersions[0].ItemId == oldPrimary.Id &&
                    oldPrimary.LinkedAlternateVersions.Length == 0,
                    "Version links must move to the selected primary without a cycle.");
                Require(selected.SaveCount == 1 && oldPrimary.SaveCount == 1,
                    "Both changed versions must be persisted.");
            }

            await service.MergeAllEligibleEpisodesAsync("RepeatedPreference", cfg);
            Require(episodes.Cast<StoredEpisode>().All(episode => episode.SaveCount == 1),
                "An already satisfied preference must not rewrite the version links.");
        }
        finally
        {
            // This unique directory contains only this test's generated STRM files.
            foreach (var path in Directory.EnumerateFiles(root))
            {
                File.Delete(path);
            }
            Directory.Delete(root);
        }
    }

    private static StoredEpisode CreateEpisode(string root, int number, string voice, int animeId = 24253)
    {
        var episode = new StoredEpisode
        {
            Id = Guid.NewGuid(),
            Name = voice,
            IndexNumber = number,
            ParentIndexNumber = 4,
            Path = Path.Combine(root, $"S04E{number:00} - {voice}.strm")
        };
        episode.SetPrimaryVersionId(null);
        File.WriteAllText(episode.Path,
            $"http://localhost:8096/YummyKodik/stream?provider=cvh&animeId={animeId}&ep={number}&voice={voice}");
        return episode;
    }

    private static void Require(bool value, string message)
    {
        if (!value)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class StoredEpisode : Episode
    {
        public int SaveCount { get; private set; }
        public Action? OnSave { get; set; }

        public override string CreatePresentationUniqueKey()
            => (PrimaryVersionId ?? Id).ToString("N");

        public override Task UpdateToRepositoryAsync(ItemUpdateType updateReason, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Link edits must not invoke recursive Video metadata persistence.");

        public void Persist()
        {
            SaveCount++;
            OnSave?.Invoke();
        }
    }

    public class GroupedLibraryProxy : DispatchProxy
    {
        public List<BaseItem> Episodes { get; set; } = new();
        public int BatchCount { get; private set; }
        public Action? BeforeReload { get; set; }
        public bool SnapshotInventory { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList) &&
                args?[0] is InternalItemsQuery query)
            {
                if (query.ItemIds.Length > 0)
                {
                    BeforeReload?.Invoke();
                }
                // Jellyfin 12 ApplyAccessFiltering excludes linked versions and owned non-extra
                // items unless IncludeOwnedItems is set, independently of presentation grouping.
                IEnumerable<BaseItem> visible = query.IncludeOwnedItems
                    ? Episodes
                    : Episodes.Where(episode =>
                        (episode is not Video video || !video.PrimaryVersionId.HasValue) &&
                        (episode.OwnerId == Guid.Empty || episode.ExtraType.HasValue));
                if (query.ItemIds.Length > 0)
                {
                    visible = visible.Where(item => query.ItemIds.Contains(item.Id));
                }
                if (query.GroupByPresentationUniqueKey && query.User is not null)
                {
                    visible = visible.GroupBy(episode => episode.PresentationUniqueKey).Select(group => group.First());
                }
                if (SnapshotInventory && query.ItemIds.Length == 0)
                {
                    visible = visible.Cast<StoredEpisode>().Select(item => (BaseItem)new StoredEpisode
                    {
                        Id = item.Id, Name = item.Name, Path = item.Path,
                        IndexNumber = item.IndexNumber, IndexNumberEnd = item.IndexNumberEnd, ParentIndexNumber = item.ParentIndexNumber,
                        PrimaryVersionId = item.PrimaryVersionId, OwnerId = item.OwnerId,
                        LinkedAlternateVersions = item.LinkedAlternateVersions.ToArray(),
                        LocalAlternateVersions = item.LocalAlternateVersions.ToArray()
                    });
                }
                return visible.ToList();
            }
            if (targetMethod?.Name == nameof(ILibraryManager.UpdateItemsAsync))
            {
                Require((ItemUpdateType)args![2]! == ItemUpdateType.None,
                    "Link-only updates must not request NFO or downloaded metadata saves.");
                BatchCount++;
                foreach (var item in (IReadOnlyList<BaseItem>)args[0]!)
                {
                    ((StoredEpisode)item).Persist();
                }
                return Task.CompletedTask;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
