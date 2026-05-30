// File: Versioning/YummyKodikEpisodeVersionsMergeHostedService.cs

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YummyKodik.Logging;

namespace YummyKodik.Versioning;

/// <summary>
/// Automatically merges duplicate Episode items into "Versions" by using Jellyfin version-linking.
/// Triggered by library events (ItemAdded + ScanCompleted when available).
///
/// Constraints from your requirements:
/// - Only Episodes (TV).
/// - Only within one root path: PluginConfiguration.OutputRootPath.
/// - Primary version is selected via PreferredTranslationFilter (substring match against STRM filename).
/// - STRM quality is irrelevant.
/// </summary>
public sealed class YummyKodikEpisodeVersionsMergeHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan ItemAddedDebounce = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan ScanCompletedDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupDebounce = TimeSpan.FromSeconds(15);
    private static readonly char[] PreferredTokenSeparators = { '|', ',', ';' };

    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<YummyKodikEpisodeVersionsMergeHostedService> _logger;

    private readonly SemaphoreSlim _mergeLock = new(1, 1);

    private Timer? _mergeDebounceTimer;
    private Timer? _scanPollTimer;

    private volatile bool _mergeRequested;
    private volatile bool _stopping;

    private bool _wasScanRunning;

    // Optional runtime-hook for ScanCompleted event (not guaranteed on every build/version).
    private EventInfo? _scanCompletedEvent;
    private Delegate? _scanCompletedHandler;

    public YummyKodikEpisodeVersionsMergeHostedService(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ILogger<YummyKodikEpisodeVersionsMergeHostedService> logger)
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _logger = new YummyKodikLogger<YummyKodikEpisodeVersionsMergeHostedService>(logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[YummyKodik] Episode versions auto-merge service starting.");

        _libraryManager.ItemAdded += OnItemAdded;

        // Try to hook ScanCompleted (or LibraryScanCompleted) if it exists.
        TryHookScanCompletedEvent();

        // Fallback: poll IsScanRunning to detect scan end, if ScanCompleted event is absent.
        _wasScanRunning = SafeIsScanRunning();
        _scanPollTimer = new Timer(_ => ScanPollTick(), null, ScanPollInterval, ScanPollInterval);

        // Existing episode items may already be present when the plugin starts.
        // Schedule an initial pass so partially merged libraries heal after restart.
        RequestMerge(StartupDebounce, "Startup");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[YummyKodik] Episode versions auto-merge service stopping.");

        _stopping = true;

        _libraryManager.ItemAdded -= OnItemAdded;

        UnhookScanCompletedEvent();

        _mergeDebounceTimer?.Dispose();
        _mergeDebounceTimer = null;

        _scanPollTimer?.Dispose();
        _scanPollTimer = null;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _mergeDebounceTimer?.Dispose(); } catch { /* ignore */ }
        try { _scanPollTimer?.Dispose(); } catch { /* ignore */ }

        _mergeDebounceTimer = null;
        _scanPollTimer = null;

        _mergeLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            var item = e.Item;

            // We only care about Episode items under OutputRootPath.
            if (!IsEligibleEpisode(item))
            {
                return;
            }

            // Debounce: a scan/filewatcher will add many episodes quickly.
            RequestMerge(ItemAddedDebounce, "ItemAdded");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[YummyKodik] ItemAdded handler failure (ignored).");
        }
    }

    // This exact signature is for EventHandler.
    private void OnScanCompleted(object? sender, EventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        RequestMerge(ScanCompletedDebounce, "ScanCompleted");
    }

    // Used when ScanCompleted is EventHandler<TEventArgs>.
    private void OnScanCompletedGeneric<T>(object? sender, T e) where T : EventArgs
    {
        OnScanCompleted(sender, e);
    }

    private void ScanPollTick()
    {
        if (_stopping)
        {
            return;
        }

        bool nowRunning;
        try
        {
            nowRunning = SafeIsScanRunning();
        }
        catch
        {
            return;
        }

        if (_wasScanRunning && !nowRunning)
        {
            // We consider this "ScanCompleted" fallback.
            RequestMerge(ScanCompletedDebounce, "ScanCompleted(poll)");
        }

        _wasScanRunning = nowRunning;
    }

    private bool SafeIsScanRunning()
    {
        try
        {
            return _libraryManager.IsScanRunning;
        }
        catch
        {
            return false;
        }
    }

    private void RequestMerge(TimeSpan delay, string reason)
    {
        if (_stopping)
        {
            return;
        }

        _mergeRequested = true;

        if (_mergeDebounceTimer == null)
        {
            _mergeDebounceTimer = new Timer(
                _ => MergeWorkerAsync(reason).ConfigureAwait(false),
                null,
                delay,
                Timeout.InfiniteTimeSpan);
        }
        else
        {
            _mergeDebounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task MergeWorkerAsync(string reason)
    {
        if (_stopping)
        {
            return;
        }

        // If already running, just leave the request flag raised and let next debounce tick handle it.
        if (!await _mergeLock.WaitAsync(0).ConfigureAwait(false))
        {
            _mergeRequested = true;
            return;
        }

        try
        {
            if (!_mergeRequested)
            {
                return;
            }

            _mergeRequested = false;

            await MergeAllEligibleEpisodesAsync(reason).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[YummyKodik] Episode versions merge failed. reason={Reason}", reason);
        }
        finally
        {
            _mergeLock.Release();
        }

        // If something arrived while we were working, schedule another quick pass.
        if (_mergeRequested && !_stopping)
        {
            _mergeDebounceTimer?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task MergeAllEligibleEpisodesAsync(string reason)
    {
        var cfg = Plugin.Instance.Configuration;
        var root = (cfg.OutputRootPath ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(root))
        {
            _logger.LogDebug("[YummyKodik] OutputRootPath is empty -> skip merge pass.");
            return;
        }

        // Load all episodes, then filter by root path.
        //
        // IMPORTANT:
        // .strm-based items are often marked as "virtual" by Jellyfin.
        // If we filter IsVirtualItem=false we can accidentally exclude the entire plugin library,
        // and then Jellyfin will effectively "pick first by name" among duplicates.
        // So we DO NOT filter by IsVirtualItem here; instead we filter strictly by:
        // - OutputRootPath containment
        // - .strm extension
        // - valid episode number
        var allEpisodes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        })
        .OfType<Episode>()
        .Where(ep => IsUnderRoot(root, ep.Path))
        .Where(ep => ep.IndexNumber.HasValue && ep.IndexNumber.Value > 0)
        .Where(ep => ep.Path != null && ep.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        .ToList();

        if (allEpisodes.Count == 0)
        {
            return;
        }

        // Group duplicates per season folder + episode number (and IndexNumberEnd if present).
        var groups = allEpisodes
            .Select(ep =>
            {
                var seasonDir = NormalizeDir(Path.GetDirectoryName(ep.Path) ?? string.Empty);
                return new
                {
                    Key = new EpisodeGroupKey(
                        SeasonDir: seasonDir,
                        EpisodeNumber: ep.IndexNumber!.Value,
                        EpisodeNumberEnd: ep.IndexNumberEnd),
                    Episode = ep
                };
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Key.SeasonDir))
            .GroupBy(x => x.Key)
            .Select(g => new { Key = g.Key, Items = g.Select(x => x.Episode).ToList() })
            .Where(g => g.Items.Count > 1)
            .ToList();

        if (groups.Count == 0)
        {
            _logger.LogDebug("[YummyKodik] No duplicate episodes to merge. reason={Reason}", reason);
            return;
        }

        var preferredTokens = ParsePreferredTokens(cfg.PreferredTranslationFilter);

        _logger.LogInformation(
            "[YummyKodik] Auto-merging episode versions: groups={Groups} episodesInRoot={Episodes} reason={Reason} preferredTokens={Tokens}",
            groups.Count,
            allEpisodes.Count,
            reason,
            preferredTokens.Length);

        var merged = 0;

        foreach (var g in groups)
        {
            // Episode inherits Video, we work with Video API for versions.
            var videos = g.Items.OfType<Video>().ToList();
            if (videos.Count < 2)
            {
                continue;
            }

            var changed = await MergeEpisodeGroupAsync(videos, preferredTokens).ConfigureAwait(false);
            if (changed)
            {
                merged++;
            }
        }

        _logger.LogInformation(
            "[YummyKodik] Auto-merge finished: mergedGroups={Merged}/{Total} reason={Reason}",
            merged,
            groups.Count,
            reason);
    }

    private static async Task<bool> MergeEpisodeGroupAsync(List<Video> items, string[] preferredTokens)
    {
        var list = BuildMergeCandidates(items);
        if (list.Count < 2)
        {
            return false;
        }

        var primary = PickPrimaryByPreferredFilter(list, preferredTokens);
        if (primary == null)
        {
            return false;
        }

        var desiredPrimaryId = primary.Id.ToString("N", CultureInfo.InvariantCulture);
        var desiredAlternates = BuildDesiredAlternates(list, primary.Id);

        if (desiredAlternates.Count == 0)
        {
            return false;
        }

        if (IsEpisodeGroupAlreadyMerged(primary, list, desiredPrimaryId, desiredAlternates))
        {
            return false;
        }

        var childrenChanged = await UpdateChildVersionsAsync(list, primary.Id, desiredPrimaryId).ConfigureAwait(false);
        var primaryChanged = await UpdatePrimaryVersionAsync(primary, desiredAlternates).ConfigureAwait(false);

        return childrenChanged || primaryChanged;
    }

    private static List<Video> BuildMergeCandidates(IEnumerable<Video> items)
    {
        return items
            .Where(i => i != null && !string.IsNullOrWhiteSpace(i.Path))
            .GroupBy(i => i.Id)
            .Select(g => g.First())
            .ToList();
    }

    private static List<LinkedChild> BuildDesiredAlternates(List<Video> items, Guid primaryId)
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var desiredAlternates = new List<LinkedChild>(items.Count - 1);

        foreach (var item in items.Where(v => v.Id != primaryId))
        {
            AddDesiredAlternate(item, seenPaths, desiredAlternates);
        }

        return desiredAlternates;
    }

    private static void AddDesiredAlternate(Video item, HashSet<string> seenPaths, List<LinkedChild> desiredAlternates)
    {
        var path = (item.Path ?? string.Empty).Trim();
        if (path.Length == 0 || !seenPaths.Add(path))
        {
            return;
        }

        desiredAlternates.Add(new LinkedChild
        {
            Path = path,
            ItemId = item.Id
        });
    }

    private static bool IsEpisodeGroupAlreadyMerged(
        Video primary,
        IReadOnlyList<Video> items,
        string desiredPrimaryId,
        IReadOnlyList<LinkedChild> desiredAlternates)
    {
        return string.IsNullOrEmpty(primary.PrimaryVersionId)
               && LinkedChildrenSetEquals(primary.LinkedAlternateVersions, desiredAlternates)
               && AreChildVersionsAlreadyMerged(items, primary.Id, desiredPrimaryId);
    }

    private static bool AreChildVersionsAlreadyMerged(
        IEnumerable<Video> items,
        Guid primaryId,
        string desiredPrimaryId)
    {
        return items
            .Where(v => v.Id != primaryId)
            .All(v => IsChildVersionAlreadyMerged(v, desiredPrimaryId));
    }

    private static bool IsChildVersionAlreadyMerged(Video child, string desiredPrimaryId)
    {
        return string.Equals(child.PrimaryVersionId ?? string.Empty, desiredPrimaryId, StringComparison.OrdinalIgnoreCase)
               && (child.LinkedAlternateVersions == null || child.LinkedAlternateVersions.Length == 0);
    }

    private static async Task<bool> UpdateChildVersionsAsync(
        IEnumerable<Video> items,
        Guid primaryId,
        string desiredPrimaryId)
    {
        var anyChanged = false;

        foreach (var child in items.Where(v => v.Id != primaryId))
        {
            if (!UpdateChildVersionLinks(child, desiredPrimaryId))
            {
                continue;
            }

            await child.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            anyChanged = true;
        }

        return anyChanged;
    }

    private static bool UpdateChildVersionLinks(Video child, string desiredPrimaryId)
    {
        var changed = false;

        if (!string.Equals(child.PrimaryVersionId ?? string.Empty, desiredPrimaryId, StringComparison.OrdinalIgnoreCase))
        {
            child.SetPrimaryVersionId(desiredPrimaryId);
            changed = true;
        }

        if (child.LinkedAlternateVersions != null && child.LinkedAlternateVersions.Length > 0)
        {
            child.LinkedAlternateVersions = Array.Empty<LinkedChild>();
            changed = true;
        }

        return changed;
    }

    private static async Task<bool> UpdatePrimaryVersionAsync(Video primary, IReadOnlyList<LinkedChild> desiredAlternates)
    {
        if (!UpdatePrimaryVersionLinks(primary, desiredAlternates))
        {
            return false;
        }

        await primary.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
        return true;
    }

    private static bool UpdatePrimaryVersionLinks(Video primary, IReadOnlyList<LinkedChild> desiredAlternates)
    {
        var changed = false;

        if (!string.IsNullOrEmpty(primary.PrimaryVersionId))
        {
            primary.SetPrimaryVersionId(null);
            changed = true;
        }

        if (!LinkedChildrenSetEquals(primary.LinkedAlternateVersions, desiredAlternates))
        {
            primary.LinkedAlternateVersions = desiredAlternates.ToArray();
            changed = true;
        }

        return changed;
    }

    private static Video? PickPrimaryByPreferredFilter(List<Video> items, string[] preferredTokens)
    {
        if (items == null || items.Count == 0)
        {
            return null;
        }

        // Always work on deterministic ordering by filename to avoid "random" FirstOrDefault matches.
        var ordered = items
            .OrderBy(v => GetFileNameNoExt(v.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        return PickPreferredTranslation(ordered, preferredTokens)
               ?? PickCurrentPrimary(ordered)
               ?? PickBaseNameVersion(ordered)
               ?? ordered[0];
    }

    private static Video? PickPreferredTranslation(IReadOnlyList<Video> ordered, IEnumerable<string> preferredTokens)
    {
        foreach (var token in preferredTokens)
        {
            var needleRaw = (token ?? string.Empty).Trim();
            if (needleRaw.Length == 0)
            {
                continue;
            }

            var hit = PickPreferredTranslation(ordered, needleRaw);
            if (hit != null)
            {
                return hit;
            }
        }

        return null;
    }

    private static Video? PickPreferredTranslation(IReadOnlyList<Video> ordered, string needleRaw)
    {
        var needleSafe = NormalizeTokenForFilename(needleRaw);
        return ordered.FirstOrDefault(v => FileNameMatchesPreferredToken(v.Path, needleRaw, needleSafe));
    }

    private static bool FileNameMatchesPreferredToken(string? path, string needleRaw, string needleSafe)
    {
        var fileName = GetFileNameNoExt(path);
        return fileName.Contains(needleRaw, StringComparison.OrdinalIgnoreCase)
               || (needleSafe.Length > 0 && fileName.Contains(needleSafe, StringComparison.OrdinalIgnoreCase));
    }

    private static Video? PickCurrentPrimary(IEnumerable<Video> ordered)
    {
        return ordered.FirstOrDefault(v =>
            string.IsNullOrEmpty(v.PrimaryVersionId) &&
            v.LinkedAlternateVersions != null &&
            v.LinkedAlternateVersions.Length > 0);
    }

    private static Video? PickBaseNameVersion(IEnumerable<Video> ordered)
    {
        return ordered.FirstOrDefault(v =>
            GetFileNameNoExt(v.Path).IndexOf(" - ", StringComparison.OrdinalIgnoreCase) < 0);
    }

    private static string NormalizeTokenForFilename(string token)
    {
        var s = (token ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return string.Empty;
        }

        // Apply the same replacement logic as Tasks.RefreshYummyKodikLibraryTask.SafeFilename
        // to make PreferredTranslationFilter tokens match the actual on-disk filenames.
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            s = s.Replace(c, '_');
        }

        return s.Trim();
    }

    private static string GetFileNameNoExt(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFileNameWithoutExtension(path) ?? string.Empty;
        }
        catch
        {
            return path.Trim();
        }
    }

    private bool IsEligibleEpisode(BaseItem? item)
    {
        if (item is not Episode ep)
        {
            return false;
        }

        var cfg = Plugin.Instance.Configuration;
        var root = (cfg.OutputRootPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return IsUnderRoot(root, ep.Path);
    }

    private bool IsUnderRoot(string root, string? itemPath)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(itemPath))
        {
            return false;
        }

        try
        {
            // Preferred way (handles separators and case properly across platforms).
            return _fileSystem.ContainsSubPath(root, itemPath);
        }
        catch
        {
            // Fallback: fullpath startswith.
            try
            {
                var fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                var fullItem = Path.GetFullPath(itemPath);
                return fullItem.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    private static string NormalizeDir(string dir)
    {
        var d = (dir ?? string.Empty).Trim();
        if (d.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            d = Path.GetFullPath(d);
        }
        catch
        {
            // ignore
        }

        return d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string[] ParsePreferredTokens(string? filter)
    {
        var s = (filter ?? string.Empty).Trim();
        if (s.Length == 0)
        {
            return Array.Empty<string>();
        }

        return s.Split(PreferredTokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToArray();
    }

    private static bool LinkedChildrenSetEquals(LinkedChild[]? existing, IReadOnlyList<LinkedChild> desired)
    {
        existing ??= Array.Empty<LinkedChild>();

        if (existing.Length != desired.Count)
        {
            // We still compare as sets: quick exit on count mismatch is ok because both are unique-by-path in desired.
            // existing might contain duplicates, so set compare is needed.
        }

        var a = new HashSet<string>(existing.Select(x => (x?.Path ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);
        var b = new HashSet<string>(desired.Select(x => (x?.Path ?? string.Empty).Trim()), StringComparer.OrdinalIgnoreCase);

        a.RemoveWhere(string.IsNullOrWhiteSpace);
        b.RemoveWhere(string.IsNullOrWhiteSpace);

        return a.SetEquals(b);
    }

    private void TryHookScanCompletedEvent()
    {
        // We try a couple of likely names.
        var names = new[] { "ScanCompleted", "LibraryScanCompleted" };
        var type = _libraryManager.GetType();

        foreach (var name in names)
        {
            try
            {
                var ev = type.GetEvent(name, BindingFlags.Instance | BindingFlags.Public);
                if (ev == null)
                {
                    continue;
                }

                var handlerType = ev.EventHandlerType;
                if (handlerType == null)
                {
                    continue;
                }

                var invoke = handlerType.GetMethod("Invoke");
                var pars = invoke?.GetParameters();
                if (pars == null || pars.Length != 2)
                {
                    continue;
                }

                // (object sender, TEventArgs e)
                var eventArgsType = pars[1].ParameterType;
                if (!typeof(EventArgs).IsAssignableFrom(eventArgsType))
                {
                    continue;
                }

                MethodInfo method;

                if (eventArgsType == typeof(EventArgs))
                {
                    method = GetType().GetMethod(nameof(OnScanCompleted), BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new MissingMethodException(nameof(OnScanCompleted));
                }
                else
                {
                    var generic = GetType().GetMethod(nameof(OnScanCompletedGeneric), BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingMethodException(nameof(OnScanCompletedGeneric));

                    method = generic.MakeGenericMethod(eventArgsType);
                }

                var del = Delegate.CreateDelegate(handlerType, this, method);

                ev.AddEventHandler(_libraryManager, del);

                _scanCompletedEvent = ev;
                _scanCompletedHandler = del;

                _logger.LogInformation("[YummyKodik] Hooked library event: {EventName}", name);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[YummyKodik] Failed to hook {EventName} (ignored).", name);
            }
        }

        _logger.LogInformation("[YummyKodik] ScanCompleted event not found (will use IsScanRunning polling fallback).");
    }

    private void UnhookScanCompletedEvent()
    {
        try
        {
            if (_scanCompletedEvent != null && _scanCompletedHandler != null)
            {
                _scanCompletedEvent.RemoveEventHandler(_libraryManager, _scanCompletedHandler);
                _logger.LogInformation("[YummyKodik] Unhooked library ScanCompleted event.");
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _scanCompletedEvent = null;
            _scanCompletedHandler = null;
        }
    }

    private readonly record struct EpisodeGroupKey(string SeasonDir, int EpisodeNumber, int? EpisodeNumberEnd);
}
