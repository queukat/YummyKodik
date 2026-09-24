using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using YummyKodik.Configuration;

namespace YummyKodik.Versioning;

/// <summary>
/// Keeps an already proven managed native group stable while Jellyfin resolves its
/// season again. Ambiguous/new content remains the responsibility of the host resolver.
/// </summary>
public sealed class YummyKodikNativeEpisodeResolver(ILibraryManager libraryManager) : IItemResolver, IMultiItemResolver
{
    private static readonly HashSet<string> SidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".xml", ".json", ".txt", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".svg",
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx"
    };

    public ResolverPriority Priority => ResolverPriority.Third;

    public BaseItem? ResolvePath(ItemResolveArgs args) => null;

    public MultiItemResolverResult ResolveMultiple(Folder parent, List<FileSystemMetadata> files,
        CollectionType? collectionType, IDirectoryService directoryService)
        // The host uses null as "not handled", although its interface is not nullable.
        => ResolveManagedSeason(parent, files, collectionType, Plugin.Instance.Configuration)!;

    internal MultiItemResolverResult? ResolveManagedSeason(Folder parent, IReadOnlyList<FileSystemMetadata> files,
        CollectionType? collectionType, PluginConfiguration cfg)
    {
        if (parent is not Season || parent.Id == Guid.Empty || collectionType != CollectionType.tvshows ||
            !IsInsideRoot(cfg.OutputRootPath, parent.Path)) return null;

        var strmFiles = files.Where(file => !file.IsDirectory &&
            string.Equals(Path.GetExtension(file.FullName), ".strm", StringComparison.OrdinalIgnoreCase)).ToList();
        if (strmFiles.Count == 0 || files.Any(file => !file.IsDirectory &&
                !string.Equals(Path.GetExtension(file.FullName), ".strm", StringComparison.OrdinalIgnoreCase) &&
                !SidecarExtensions.Contains(Path.GetExtension(file.FullName)))) return null;

        var paths = strmFiles.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count != strmFiles.Count || paths.Any(path => !File.Exists(path) ||
                !SameDirectory(parent.Path, Path.GetDirectoryName(path)))) return null;

        var current = libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = parent.Id,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(true)
        }).OfType<Episode>().ToList();
        if (current.Count != paths.Count || current.Select(item => item.Id).Distinct().Count() != current.Count ||
            current.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != current.Count ||
            current.Any(item => !paths.Contains(item.Path) || item.ParentId != parent.Id || item.AdditionalParts.Length > 0 ||
                item.IndexNumberEnd.HasValue ||
                !YummyKodikEpisodeVersionsMergeHostedService.TryReadManagedEpisodeIdentity(item, out var season, out var episode, out _) ||
                item.IndexNumber != episode || item.ParentIndexNumber != season)) return null;

        var result = new MultiItemResolverResult();
        foreach (var group in current.GroupBy(item => (item.ParentIndexNumber, item.IndexNumber)))
        {
            var members = group.Cast<Video>().ToList();
            var primary = YummyKodikEpisodeVersionsMergeHostedService.PickPrimaryForResolvedGroup(members, cfg) as Episode;
            if (primary == null ||
                !YummyKodikEpisodeVersionsMergeHostedService.IsCanonicalNativeResolvedGroup(members, primary)) return null;

            // Resolve a new object exactly as the host does. Never feed its comparison
            // stage a mutable cached item or invent a different native/explicit topology.
            result.Items.Add(new Episode
            {
                Id = primary.Id,
                ParentId = parent.Id,
                Path = primary.Path,
                Name = primary.Name,
                IndexNumber = primary.IndexNumber,
                ParentIndexNumber = primary.ParentIndexNumber,
                SeriesId = primary.SeriesId,
                SeriesName = primary.SeriesName,
                SeasonId = primary.SeasonId,
                SeasonName = primary.SeasonName,
                ProductionYear = primary.ProductionYear,
                VideoType = primary.VideoType,
                IsInMixedFolder = primary.IsInMixedFolder,
                LocalAlternateVersions = primary.LocalAlternateVersions.ToArray()
            });
        }
        result.ExtraFiles.AddRange(files.Where(file => file.IsDirectory || !paths.Contains(file.FullName)));
        return result;
    }

    private static bool SameDirectory(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
           string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
               Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);

    private static bool IsInsideRoot(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path)) return false;
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
               !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
