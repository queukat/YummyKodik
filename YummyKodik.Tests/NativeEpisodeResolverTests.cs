using System.Reflection;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using YummyKodik;
using YummyKodik.Configuration;
using YummyKodik.Versioning;

internal static class NativeEpisodeResolverTests
{
    public static void RepeatedResolutionPreservesSelectedOwnerWithoutMutatingStoredItems()
    {
        using var fixture = new Fixture();
        var before = Snapshot(fixture.Items);
        var first = fixture.Resolve() ?? throw new InvalidOperationException("A proven native season must be resolved.");
        var selected = (Episode)first.Items.Single();
        Require(selected.Id == fixture.Primary.Id && selected.Path == fixture.Primary.Path &&
                selected.Path != fixture.Child.Path,
            "Filesystem resolution must keep the canonical selected owner, even when another filename sorts first.");
        Require(!ReferenceEquals(selected, fixture.Primary) &&
                !ReferenceEquals(selected.LocalAlternateVersions, fixture.Primary.LocalAlternateVersions),
            "The host must receive a fresh resolver object and independent native array.");
        Require(selected.ParentId == fixture.Season.Id && selected.SeriesId == fixture.Primary.SeriesId &&
                selected.SeasonId == fixture.Primary.SeasonId && selected.IndexNumber == 1 &&
                selected.ParentIndexNumber == 1 && selected.IsInMixedFolder == fixture.Primary.IsInMixedFolder,
            "The resolved episode must retain its season/series identity and mixed-folder behavior.");
        selected.LocalAlternateVersions[0] = "changed by host comparison";
        var second = fixture.Resolve() ?? throw new InvalidOperationException("Repeated resolution must remain eligible.");
        var secondPrimary = (Episode)second.Items.Single();
        Require(secondPrimary.Path == fixture.Primary.Path &&
                secondPrimary.LocalAlternateVersions.SequenceEqual(new[] { fixture.Child.Path }),
            "A repeated scan must emit the same canonical native group.");
        Require(Snapshot(fixture.Items) == before && fixture.Library.WriteCalls == 0,
            "Resolving must neither mutate returned repository records nor persist anything.");
    }

    public static void AmbiguousOrMixedSeasonsFallBackWithoutMutation()
    {
        foreach (var condition in new[]
        {
            "unknown-stream", "wrong-episode", "wrong-number", "missing-number", "missing-file", "unindexed-file",
            "explicit-member", "foreign-native", "pending-choice", "outside-root", "duplicate-record", "native-cycle", "other-video"
        })
        {
            using var fixture = new Fixture();
            switch (condition)
            {
                case "unknown-stream": File.WriteAllText(fixture.Child.Path, "https://example.invalid/unmanaged-video"); break;
                case "wrong-episode": File.WriteAllText(fixture.Child.Path, "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=24253&ep=2"); break;
                case "wrong-number": fixture.Child.IndexNumber = 2; break;
                case "missing-number": fixture.Child.IndexNumber = null; break;
                case "missing-file": File.Delete(fixture.Child.Path); break;
                case "unindexed-file": fixture.AddFile("S01E01 - Unknown.strm", "http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=24253&ep=1"); break;
                case "explicit-member":
                    fixture.Primary.LocalAlternateVersions = Array.Empty<string>();
                    fixture.Primary.LinkedAlternateVersions = new[] { new LinkedChild { ItemId = fixture.Child.Id, Type = LinkedChildType.LinkedAlternateVersion } };
                    fixture.Child.OwnerId = Guid.Empty;
                    break;
                case "foreign-native": fixture.Primary.LocalAlternateVersions = new[] { fixture.Child.Path, Path.Combine(fixture.Root, "outside.strm") }; break;
                case "pending-choice": fixture.Configuration.PreferredTranslationFilter = "AAA First"; break;
                case "outside-root": fixture.Configuration.OutputRootPath = Path.Combine(fixture.Root, "elsewhere"); break;
                case "duplicate-record": fixture.Items.Add(fixture.Child); break;
                case "native-cycle": fixture.Child.LocalAlternateVersions = new[] { fixture.Primary.Path }; break;
                case "other-video": fixture.AddFile("unrelated.mkv", "video"); break;
            }
            var before = Snapshot(fixture.Items);
            Require(fixture.Resolve() == null, $"Unproven or mixed season must use the ordinary host resolver: {condition}.");
            Require(Snapshot(fixture.Items) == before && fixture.Library.WriteCalls == 0,
                $"Rejected resolution must not alter repository objects: {condition}.");
        }
    }

    public static void SidecarsAndSingletonsArePreservedAndResolverIsRegisteredBeforeHost()
    {
        using var fixture = new Fixture();
        var singleton = fixture.CreateEpisode("S01E02 - Only.strm", "Only", number: 2);
        fixture.Items.Add(singleton);
        var nfo = fixture.AddFile("season.nfo", "<season/>");
        var image = fixture.AddFile("poster.jpg", "image");
        var subtitle = fixture.AddFile("S01E01.srt", "subtitle");
        var result = fixture.Resolve() ?? throw new InvalidOperationException("A clean singleton must not block a native season.");
        Require(result.Items.Count == 2 && result.Items.Any(item => item.Id == singleton.Id),
            "Clean single-version episodes must remain ordinary resolved items.");
        Require(result.ExtraFiles.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(new[] { nfo.FullName, image.FullName, subtitle.FullName }),
            "Artwork, NFO and subtitles must remain untouched extra files for the host.");
        Require(fixture.Resolver.Priority < ResolverPriority.Fourth && fixture.Resolver.ResolvePath(null!) == null,
            "The scoped multi-item resolver must precede the host movie resolver and never intercept individual paths.");
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, null!);
        Require(services.Any(service => service.ServiceType == typeof(IItemResolver) &&
                service.ImplementationType == typeof(YummyKodikNativeEpisodeResolver)),
            "The adapter must be registered through the host's existing IItemResolver extension point.");
    }

    private static string Snapshot(IEnumerable<Episode> items)
        => string.Join("\n", items.Select(item => $"{item.Id}|{item.Path}|{item.OwnerId}|{item.PrimaryVersionId}|{item.IndexNumber}|{item.ParentIndexNumber}|{string.Join(';', item.LocalAlternateVersions)}|{string.Join(';', item.LinkedAlternateVersions.Select(link => link.ItemId))}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "YummyKodikTests", Guid.NewGuid().ToString("N"));
        public Season Season { get; }
        public Episode Primary { get; }
        public Episode Child { get; }
        public PluginConfiguration Configuration { get; }
        public YummyKodikNativeEpisodeResolver Resolver { get; }
        public ResolverLibraryProxy Library { get; }
        public List<Episode> Items => Library.Items;
        private readonly List<FileSystemMetadata> _files = new();

        public Fixture()
        {
            var directory = Path.Combine(Root, "Season 01");
            Directory.CreateDirectory(directory);
            Season = new Season { Id = Guid.NewGuid(), ParentId = Guid.NewGuid(), Path = directory, IndexNumber = 1 };
            Configuration = new PluginConfiguration { OutputRootPath = Root, PreferredTranslationFilter = "ZZZ Preferred" };
            var library = DispatchProxy.Create<ILibraryManager, ResolverLibraryProxy>();
            Library = (ResolverLibraryProxy)(object)library;
            Primary = CreateEpisode("S01E01 - ZZZ Preferred.strm", "ZZZ Preferred");
            Child = CreateEpisode("S01E01 - AAA First.strm", "AAA First");
            Primary.LocalAlternateVersions = new[] { Child.Path };
            Child.OwnerId = Primary.Id;
            Child.SetPrimaryVersionId(Primary.Id);
            Items.AddRange(new[] { Primary, Child });
            Resolver = new YummyKodikNativeEpisodeResolver(library);
        }

        public Episode CreateEpisode(string name, string voice, int number = 1)
        {
            var file = AddFile(name, $"http://localhost:8096/YummyKodik/stream?provider=cvh&animeId=24253&ep={number}&voice={Uri.EscapeDataString(voice)}");
            return new Episode
            {
                Id = Guid.NewGuid(), ParentId = Season.Id, Path = file.FullName, Name = voice,
                IndexNumber = number, ParentIndexNumber = 1, SeriesId = Season.ParentId,
                SeriesName = "Fixture series", SeasonId = Season.Id, SeasonName = "Season 1"
            };
        }

        public FileSystemMetadata AddFile(string name, string content)
        {
            var path = Path.Combine(Season.Path, name);
            File.WriteAllText(path, content);
            var file = new FileSystemMetadata { FullName = path, Name = name };
            _files.Add(file);
            return file;
        }

        public MultiItemResolverResult? Resolve()
            => Resolver.ResolveManagedSeason(Season, _files, CollectionType.tvshows, Configuration);

        public void Dispose()
        {
            foreach (var file in Directory.EnumerateFiles(Season.Path)) File.Delete(file);
            Directory.Delete(Season.Path);
            Directory.Delete(Root);
        }
    }

    public class ResolverLibraryProxy : DispatchProxy
    {
        public List<Episode> Items { get; } = new();
        public int WriteCalls { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(ILibraryManager.GetItemList))
            {
                var query = (InternalItemsQuery)args![0]!;
                Require(query.ParentId != Guid.Empty && query.IncludeOwnedItems && !query.GroupByPresentationUniqueKey,
                    "Resolver inventory must be restricted to the current season and include owned members.");
                return Items.Where(item => item.ParentId == query.ParentId).Cast<BaseItem>().ToList();
            }
            if (targetMethod?.Name.StartsWith("Update", StringComparison.Ordinal) == true ||
                targetMethod?.Name.StartsWith("Create", StringComparison.Ordinal) == true) WriteCalls++;
            throw new NotSupportedException(targetMethod?.Name);
        }
    }
}
