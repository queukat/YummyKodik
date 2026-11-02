using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using YummyKodik.Configuration;

namespace YummyKodik.Media;

/// <summary>Scopes host episode inventories before materialization, including native owned versions when requested.</summary>
internal static class ManagedEpisodeInventory
{
    internal static IReadOnlyList<Episode> Load(ILibraryManager library, IReadOnlyList<string> roots, bool includeOwnedItems = false)
    {
        var ancestors = roots.Select(root => library.FindByPath(root, true))
            .OfType<Folder>().Select(folder => folder.Id).Where(id => id != Guid.Empty).Distinct().ToArray();
        // An empty ancestor filter means the entire host database. Unindexed folders must wait instead.
        if (ancestors.Length == 0) return Array.Empty<Episode>();

        return library.GetItemList(new InternalItemsQuery
        {
            AncestorIds = ancestors,
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IncludeOwnedItems = includeOwnedItems,
            GroupByPresentationUniqueKey = false,
            Recursive = true
        }).OfType<Episode>()
            .Where(episode => roots.Any(root => LibraryTarget.ContainsPath(root, episode.Path)))
            .Where(episode => episode.Path != null && episode.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
