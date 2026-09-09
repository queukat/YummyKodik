// File: Versioning/YummyKodikEpisodeVersionsMergeHostedService.cs

using System;
using System.Collections.Concurrent;
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
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YummyKodik.Configuration;
using YummyKodik.Kodik;
using YummyKodik.Logging;
using YummyKodik.Util;

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
    private readonly ConcurrentQueue<string> _prioritySeriesDirectories = new();

    private Timer? _mergeDebounceTimer;
    private Timer? _scanPollTimer;

    private volatile bool _mergeRequested;
    private volatile bool _stopping;
    private volatile string _pendingMergeReason = "Unknown";

    private int _activeRefreshBatches;
    private int _authoritativeRefreshMergeCompleted;

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

    public void RequestTranslationPreferenceMerge(string? seriesDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(seriesDirectory))
        {
            _prioritySeriesDirectories.Enqueue(NormalizeDir(seriesDirectory));
        }
        RequestMerge(TimeSpan.FromSeconds(1), "TranslationPreferenceChanged");
    }

    /// <summary>
    /// Suppresses event-driven merge passes while the managed refresh task is still writing files.
    /// The returned scope must remain active until the post-refresh readiness barrier and final
    /// merge have completed.
    /// </summary>
    public async Task<IDisposable> BeginRefreshBatchAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Increment(ref _activeRefreshBatches) == 1)
        {
            Interlocked.Exchange(ref _authoritativeRefreshMergeCompleted, 0);
        }
        try
        {
            _mergeDebounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Service shutdown won the race.
        }

        try
        {
            // Drain a worker that passed admission before this batch became active. Workers that
            // acquire the lock after this point re-check _activeRefreshBatches and leave without
            // touching Jellyfin repository links.
            await _mergeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            _mergeLock.Release();
        }
        catch
        {
            EndRefreshBatch();
            throw;
        }

        return new RefreshBatchScope(this);
    }

    /// <summary>
    /// Runs the single authoritative merge pass after a managed refresh readiness barrier.
    /// </summary>
    public async Task MergeAfterRefreshAsync(CancellationToken cancellationToken)
    {
        await _mergeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_stopping)
            {
                return;
            }

            _mergeRequested = false;
            await MergeAllEligibleEpisodesAsync("PostRefresh").ConfigureAwait(false);
            _mergeRequested = false;
            Interlocked.Exchange(ref _authoritativeRefreshMergeCompleted, 1);
        }
        finally
        {
            _mergeLock.Release();
        }
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
        _pendingMergeReason = reason;

        if (Volatile.Read(ref _activeRefreshBatches) > 0)
        {
            return;
        }

        if (_mergeDebounceTimer == null)
        {
            _mergeDebounceTimer = new Timer(
                _ => MergeWorkerAsync(_pendingMergeReason).ConfigureAwait(false),
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
            if (Volatile.Read(ref _activeRefreshBatches) > 0)
            {
                _mergeRequested = true;
                return;
            }

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
        if (_mergeRequested &&
            !_stopping &&
            Volatile.Read(ref _activeRefreshBatches) == 0)
        {
            _mergeDebounceTimer?.Change(TimeSpan.FromSeconds(3), Timeout.InfiniteTimeSpan);
        }
    }

    private Task MergeAllEligibleEpisodesAsync(string reason)
        => MergeAllEligibleEpisodesAsync(reason, Plugin.Instance.Configuration);

    internal async Task MergeAllEligibleEpisodesAsync(string reason, PluginConfiguration cfg)
    {
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
            // Jellyfin 12 treats linked alternate versions as owned items and hides them by default.
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            Recursive = true
        })
        .OfType<Episode>()
        .Where(ep => IsUnderRoot(root, ep.Path))
        .Where(ep => ep.IndexNumber.HasValue && ep.IndexNumber.Value > 0)
        .Where(ep => ep.Path != null && ep.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        .Where(ep => File.Exists(ep.Path))
        .ToList();

        if (allEpisodes.Count == 0)
        {
            _prioritySeriesDirectories.Clear();
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
            _prioritySeriesDirectories.Clear();
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

        var mergedGroups = new HashSet<EpisodeGroupKey>();

        foreach (var g in groups)
        {
            // Keep the existing merge owner/lock, but apply an interactive choice
            // before continuing the bulk pass. Include already visited groups:
            // their preference may have changed while another title was saved.
            while (_prioritySeriesDirectories.TryDequeue(out var priorityDirectory))
            {
                foreach (var priorityGroup in groups.Where(group => IsUnderRoot(priorityDirectory, group.Key.SeasonDir)))
                {
                    if (await MergeEpisodeGroupAsync(priorityGroup.Items.Cast<Video>().ToList(), cfg, preferredTokens,
                            forceReload: true).ConfigureAwait(false))
                    {
                        mergedGroups.Add(priorityGroup.Key);
                    }
                }
            }

            // Episode inherits Video, we work with Video API for versions.
            var videos = g.Items.OfType<Video>().ToList();
            if (videos.Count < 2)
            {
                continue;
            }

            var changed = await MergeEpisodeGroupAsync(videos, cfg, preferredTokens).ConfigureAwait(false);
            if (changed)
            {
                mergedGroups.Add(g.Key);
            }
        }

        _logger.LogInformation(
            "[YummyKodik] Auto-merge finished: mergedGroups={Merged}/{Total} reason={Reason}",
            mergedGroups.Count,
            groups.Count,
            reason);
    }

    private async Task<bool> MergeEpisodeGroupAsync(List<Video> items, PluginConfiguration cfg, string[] preferredTokens,
        bool forceReload = false)
    {
        var list = BuildMergeCandidates(items);
        if (list.Count < 2)
        {
            return false;
        }

        var primary = PickPrimaryByPreferredFilter(list, cfg, preferredTokens);
        if (primary == null)
        {
            return false;
        }

        var desiredPrimaryId = primary.Id;
        var desiredAlternates = BuildDesiredAlternates(list, primary.Id);

        // An explicit choice can revisit a group already changed earlier in this pass.
        // Its old inventory cannot establish a no-op for that newer request.
        if (!forceReload && IsEpisodeGroupAlreadyMerged(primary, list, desiredPrimaryId, desiredAlternates))
        {
            return false;
        }

        // The inventory may be minutes old. Re-read complete records from the repository,
        // not GetItemById's cache, before persisting anything derived from that inventory.
        var fresh = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ItemIds = list.Select(item => item.Id).ToArray(),
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IncludeOwnedItems = true,
            GroupByPresentationUniqueKey = false,
            DtoOptions = new DtoOptions(true)
        }).OfType<Video>().ToList();
        if (fresh.Count != list.Count || fresh.Any(item => !list.Any(previous =>
                previous.Id == item.Id &&
                string.Equals(previous.Path, item.Path, StringComparison.OrdinalIgnoreCase) &&
                previous.IndexNumber == item.IndexNumber &&
                (previous as Episode)?.IndexNumberEnd == (item as Episode)?.IndexNumberEnd &&
                previous.ParentIndexNumber == item.ParentIndexNumber &&
                item.IndexNumber.HasValue && item.ParentIndexNumber.HasValue &&
                File.Exists(item.Path))))
        {
            // A scan moved, removed or has not finished indexing a member. Its completion
            // owns the next merge; never write incomplete metadata back over that scan.
            return false;
        }

        primary = PickPrimaryByPreferredFilter(fresh, cfg, preferredTokens);
        if (primary == null)
        {
            return false;
        }
        desiredAlternates = BuildDesiredAlternates(fresh, primary.Id);
        var changed = fresh.Where(item => item.Id != primary.Id)
            .Where(item => UpdateChildVersionLinks(item, primary.Id))
            .Cast<BaseItem>().ToList();
        if (UpdatePrimaryVersionLinks(primary, desiredAlternates))
        {
            changed.Add(primary);
        }

        foreach (var parentGroup in changed.GroupBy(item => item.ParentId))
        {
            var parent = parentGroup.Key == Guid.Empty ? null : _libraryManager.GetItemById(parentGroup.Key);
            // Version links are not downloaded/edited title metadata. The Video wrapper
            // recursively saves local alternates; the host batch API preserves persistence
            // and ItemUpdated events without that cascade or a metadata-saver rewrite.
            await _libraryManager.UpdateItemsAsync(parentGroup.ToArray(), parent!, ItemUpdateType.None,
                CancellationToken.None).ConfigureAwait(false);
        }

        return changed.Count > 0;
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
        // Jellyfin persists a child as local OR linked, preferring local when both are
        // supplied. Requesting it again as linked would therefore never reach a no-op.
        var localPaths = new HashSet<string>(
            items.Single(item => item.Id == primaryId).LocalAlternateVersions,
            StringComparer.OrdinalIgnoreCase);
        var desiredAlternates = new List<LinkedChild>(items.Count - 1);

        foreach (var item in items.Where(v => v.Id != primaryId && !localPaths.Contains(v.Path)))
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
            ItemId = item.Id,
            Type = LinkedChildType.LinkedAlternateVersion
        });
    }

    private static bool IsEpisodeGroupAlreadyMerged(
        Video primary,
        IReadOnlyList<Video> items,
        Guid desiredPrimaryId,
        IReadOnlyList<LinkedChild> desiredAlternates)
    {
        return !primary.PrimaryVersionId.HasValue
               && primary.OwnerId == Guid.Empty
               && LinkedChildrenSetEquals(primary.LinkedAlternateVersions, desiredAlternates)
               && AreChildVersionsAlreadyMerged(items, primary.Id, desiredPrimaryId);
    }

    private static bool AreChildVersionsAlreadyMerged(
        IEnumerable<Video> items,
        Guid primaryId,
        Guid desiredPrimaryId)
    {
        return items
            .Where(v => v.Id != primaryId)
            .All(v => IsChildVersionAlreadyMerged(v, desiredPrimaryId));
    }

    private static bool IsChildVersionAlreadyMerged(Video child, Guid desiredPrimaryId)
    {
        return child.PrimaryVersionId == desiredPrimaryId
               && (child.LinkedAlternateVersions == null || child.LinkedAlternateVersions.Length == 0);
    }

    private static bool UpdateChildVersionLinks(Video child, Guid desiredPrimaryId)
    {
        var changed = false;

        if (child.PrimaryVersionId != desiredPrimaryId)
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

    private static bool UpdatePrimaryVersionLinks(Video primary, IReadOnlyList<LinkedChild> desiredAlternates)
    {
        var changed = false;

        if (primary.PrimaryVersionId.HasValue)
        {
            primary.SetPrimaryVersionId(null);
            changed = true;
        }

        // Jellyfin's scanner may have marked this alternate as owned. Clearing
        // PrimaryVersionId alone leaves it hidden from normal episode queries.
        if (primary.OwnerId != Guid.Empty)
        {
            primary.OwnerId = Guid.Empty;
            changed = true;
        }

        if (!LinkedChildrenSetEquals(primary.LinkedAlternateVersions, desiredAlternates))
        {
            primary.LinkedAlternateVersions = desiredAlternates.ToArray();
            changed = true;
        }

        return changed;
    }

    private static Video? PickPrimaryByPreferredFilter(List<Video> items, PluginConfiguration cfg, string[] preferredTokens)
    {
        if (items == null || items.Count == 0)
        {
            return null;
        }

        // Always work on deterministic ordering by filename to avoid "random" FirstOrDefault matches.
        var ordered = items
            .OrderBy(v => GetFileNameNoExt(v.Path), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var savedPreferenceTokens = BuildSavedPreferenceTokensForPaths(
            ordered.Select(v => v.Path ?? string.Empty),
            cfg);

        return PickPreferredTranslation(ordered, savedPreferenceTokens)
               ?? PickPreferredTranslation(ordered, preferredTokens)
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
        return ordered.FirstOrDefault(v => PathMatchesPreferredToken(v.Path, needleRaw, needleSafe));
    }

    private static bool PathMatchesPreferredToken(string? path, string? needleRaw, string? needleSafe)
    {
        if (FileNameMatchesPreferredToken(path, needleRaw, needleSafe))
        {
            return true;
        }

        if (!TryReadStrmRequest(path, out var request, out var query))
        {
            return false;
        }

        if (query.TryGetValue("tr", out var tr) &&
            string.Equals((tr ?? string.Empty).Trim(), (needleRaw ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return TextMatchesPreferredToken(request.VoiceName, needleRaw, needleSafe);
    }

    private static bool FileNameMatchesPreferredToken(string? path, string? needleRaw, string? needleSafe)
    {
        var fileName = GetFileNameNoExt(path);
        return TextMatchesPreferredToken(fileName, needleRaw, needleSafe);
    }

    private static bool TextMatchesPreferredToken(string? value, string? needleRaw, string? needleSafe)
    {
        var text = (value ?? string.Empty).Trim();
        var needle = (needleRaw ?? string.Empty).Trim();
        if (text.Length == 0 || needle.Length == 0)
        {
            return false;
        }

        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrEmpty(needleSafe) && text.Contains(needleSafe, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var textKey = TranslationNameKeyNormalizer.Normalize(text);
        var needleKey = TranslationNameKeyNormalizer.Normalize(needle);
        return textKey.Length > 0 &&
               needleKey.Length > 0 &&
               (string.Equals(textKey, needleKey, StringComparison.Ordinal) ||
                textKey.Contains(needleKey, StringComparison.Ordinal) ||
                needleKey.Contains(textKey, StringComparison.Ordinal));
    }

    private static Video? PickCurrentPrimary(IEnumerable<Video> ordered)
    {
        return ordered.FirstOrDefault(v =>
            !v.PrimaryVersionId.HasValue &&
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

    private static string[] BuildSavedPreferenceTokensForPaths(IEnumerable<string> paths, PluginConfiguration cfg)
    {
        var seriesKeys = BuildSeriesPreferenceKeysForPaths(paths);
        if (seriesKeys.Count == 0)
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();
        var seenTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in EnumerateSavedPreferenceTokens(cfg, seriesKeys))
        {
            if (!string.IsNullOrWhiteSpace(token) && seenTokens.Add(token.Trim()))
            {
                tokens.Add(token.Trim());
            }
        }

        return tokens.ToArray();
    }

    private static HashSet<string> BuildSeriesPreferenceKeysForPaths(IEnumerable<string> paths)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!TryReadStrmRequest(path, out var request, out _))
            {
                continue;
            }

            foreach (var key in BuildSeriesPreferenceKeys(request))
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    keys.Add(key.Trim().ToLowerInvariant());
                }
            }
        }

        return keys;
    }

    private static IEnumerable<string> BuildSeriesPreferenceKeys(YummyStreamRequest request)
    {
        switch (request.Provider)
        {
            case YummyStreamProviderKind.Alloha:
            case YummyStreamProviderKind.Cvh:
                if (request.AnimeId > 0)
                {
                    yield return $"yummy:{request.AnimeId}";
                    yield return $"alloha:{request.AnimeId}";
                    yield return $"cvh:{request.AnimeId}";
                }
                break;
            case YummyStreamProviderKind.Kodik:
                if (!string.IsNullOrWhiteSpace(request.KodikId))
                {
                    yield return KodikPlaybackSelector.BuildSeriesKey(request.KodikIdType, request.KodikId);
                }
                break;
        }
    }

    private static IEnumerable<string> EnumerateSavedPreferenceTokens(
        PluginConfiguration cfg,
        ISet<string> seriesKeys)
    {
        // A merged episode can contain STRMs identified by different provider keys, for example
        // both cvh:{animeId} and shikimori:{id}. There is no timestamp in the persisted
        // preference schema, but its list order is stable and new preference records are
        // appended. Treat the last applicable record as the current choice, rather than
        // allowing an older provider-specific value to win merely because it appears first.
        // User preferences still take precedence over the legacy global list.
        foreach (var pref in (cfg.UserSeriesPreferredTranslations ?? new List<UserSeriesTranslationPreference>()).AsEnumerable().Reverse())
        {
            var key = NormalizePreferenceKey(pref?.SeriesKey);
            if (key.Length > 0 && seriesKeys.Contains(key))
            {
                var token = (pref?.TranslationId ?? string.Empty).Trim();
                if (token.Length > 0)
                {
                    yield return token;
                }
            }
        }

        foreach (var pref in (cfg.SeriesPreferredTranslations ?? new List<SeriesTranslationPreference>()).AsEnumerable().Reverse())
        {
            var key = NormalizePreferenceKey(pref?.SeriesKey);
            if (key.Length > 0 && seriesKeys.Contains(key))
            {
                var token = (pref?.TranslationId ?? string.Empty).Trim();
                if (token.Length > 0)
                {
                    yield return token;
                }
            }
        }
    }

    private static string NormalizePreferenceKey(string? key)
    {
        return (key ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static bool TryReadStrmRequest(
        string? path,
        out YummyStreamRequest request,
        out IReadOnlyDictionary<string, string> query)
    {
        request = new YummyStreamRequest();
        query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(path) ||
            !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path))
        {
            return false;
        }

        try
        {
            var line = File.ReadLines(path)
                .Select(x => (x ?? string.Empty).Trim())
                .FirstOrDefault(x => x.Length > 0);

            if (string.IsNullOrWhiteSpace(line) ||
                !YummyKodikStreamUri.TryParseRequest(line, out request))
            {
                return false;
            }

            if (Uri.TryCreate(line, UriKind.Absolute, out var uri))
            {
                query = YummyKodikStreamUri.ParseQueryToDictionary(uri.Query);
            }

            return true;
        }
        catch
        {
            request = new YummyStreamRequest();
            query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return false;
        }
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
            // We still compare as sets: quick exit on count mismatch is ok because desired ids are unique.
            // existing might contain duplicates, so set compare is needed.
        }

        var a = existing
            .Where(x => x?.ItemId.HasValue == true)
            .Select(x => x!.ItemId!.Value)
            .ToHashSet();
        var b = desired
            .Where(x => x?.ItemId.HasValue == true)
            .Select(x => x!.ItemId!.Value)
            .ToHashSet();

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

    private void EndRefreshBatch()
    {
        var remaining = Interlocked.Decrement(ref _activeRefreshBatches);
        if (remaining < 0)
        {
            Interlocked.Exchange(ref _activeRefreshBatches, 0);
            return;
        }

        if (remaining != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _authoritativeRefreshMergeCompleted, 0) == 1)
        {
            // The post-refresh pass intentionally absorbs ItemAdded/ScanCompleted requests that
            // arrived while the managed batch was settling or merging.
            _mergeRequested = false;
            return;
        }

        if (_mergeRequested && !_stopping)
        {
            RequestMerge(TimeSpan.FromSeconds(3), "RefreshBatchReleased");
        }
    }

    private sealed class RefreshBatchScope : IDisposable
    {
        private YummyKodikEpisodeVersionsMergeHostedService? _owner;

        public RefreshBatchScope(YummyKodikEpisodeVersionsMergeHostedService owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.EndRefreshBatch();
        }
    }

    private readonly record struct EpisodeGroupKey(string SeasonDir, int EpisodeNumber, int? EpisodeNumberEnd);
}
