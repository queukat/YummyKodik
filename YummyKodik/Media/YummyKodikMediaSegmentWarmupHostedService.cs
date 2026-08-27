using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using YummyKodik.Logging;

namespace YummyKodik.Media;

/// <summary>
/// Warms Jellyfin media segments for newly scanned YummyKodik STRM items.
/// Jellyfin normally runs media segment providers from its own scheduled task,
/// which can leave new episodes without skip buttons until the next daily pass.
/// </summary>
public sealed class YummyKodikMediaSegmentWarmupHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan ItemChangedDebounce = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ScanCompletedDebounce = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ScanPollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StartupDebounce = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan MissingSegmentPollInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MissingSegmentRetryDelay = TimeSpan.FromHours(2);
    private const int MaxMissingItemsPerFullPass = 200;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _mediaSegmentManager;
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<YummyKodikMediaSegmentWarmupHostedService> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _pendingItemIds = new();
    private readonly ConcurrentDictionary<Guid, DateTime> _nextRetryUtcByItemId = new();
    private readonly SemaphoreSlim _workerLock = new(1, 1);
    private readonly CancellationTokenSource _stopCts = new();
    private readonly object _timerLock = new();

    private Timer? _debounceTimer;
    private Timer? _scanPollTimer;
    private Timer? _missingSegmentsTimer;

    private volatile bool _fullPassRequested;
    private volatile bool _stopping;
    private bool _wasScanRunning;
    private string _lastReason = "Startup";

    private EventInfo? _scanCompletedEvent;
    private Delegate? _scanCompletedHandler;

    public YummyKodikMediaSegmentWarmupHostedService(
        ILibraryManager libraryManager,
        IMediaSegmentManager mediaSegmentManager,
        IFileSystem fileSystem,
        ILogger<YummyKodikMediaSegmentWarmupHostedService> logger)
    {
        _libraryManager = libraryManager;
        _mediaSegmentManager = mediaSegmentManager;
        _fileSystem = fileSystem;
        _logger = new YummyKodikLogger<YummyKodikMediaSegmentWarmupHostedService>(logger);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[YummyKodik] Media segment warmup service starting.");

        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;

        TryHookScanCompletedEvent();

        _wasScanRunning = SafeIsScanRunning();
        _scanPollTimer = new Timer(_ => ScanPollTick(), null, ScanPollInterval, ScanPollInterval);
        _missingSegmentsTimer = new Timer(
            _ => RequestFullPass(TimeSpan.Zero, "PeriodicMissingSegments"),
            null,
            MissingSegmentPollInterval,
            MissingSegmentPollInterval);

        RequestFullPass(StartupDebounce, "Startup");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[YummyKodik] Media segment warmup service stopping.");

        _stopping = true;
        _stopCts.Cancel();

        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;

        UnhookScanCompletedEvent();

        _debounceTimer?.Dispose();
        _scanPollTimer?.Dispose();
        _missingSegmentsTimer?.Dispose();

        _debounceTimer = null;
        _scanPollTimer = null;
        _missingSegmentsTimer = null;

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        try { _debounceTimer?.Dispose(); } catch { /* ignore */ }
        try { _scanPollTimer?.Dispose(); } catch { /* ignore */ }
        try { _missingSegmentsTimer?.Dispose(); } catch { /* ignore */ }
        try { _stopCts.Cancel(); } catch { /* ignore */ }

        _workerLock.Dispose();
        _stopCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (_stopping)
        {
            return;
        }

        try
        {
            if (!IsEligibleEpisode(e.Item))
            {
                return;
            }

            _pendingItemIds[e.Item.Id] = 0;
            ScheduleWorker(ItemChangedDebounce, "ItemChanged");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[YummyKodik] Media segment warmup item handler failure (ignored).");
        }
    }

    private void OnScanCompleted(object? sender, EventArgs e)
    {
        RequestFullPass(ScanCompletedDebounce, "ScanCompleted");
    }

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
            RequestFullPass(ScanCompletedDebounce, "ScanCompleted(poll)");
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

    private void RequestFullPass(TimeSpan delay, string reason)
    {
        if (_stopping)
        {
            return;
        }

        _fullPassRequested = true;
        ScheduleWorker(delay, reason);
    }

    private void ScheduleWorker(TimeSpan delay, string reason)
    {
        if (_stopping)
        {
            return;
        }

        lock (_timerLock)
        {
            _lastReason = reason;

            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(
                    _ => WarmupWorkerAsync().ConfigureAwait(false),
                    null,
                    delay,
                    Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task WarmupWorkerAsync()
    {
        if (_stopping)
        {
            return;
        }

        if (!await _workerLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        var reason = _lastReason;

        try
        {
            if (_fullPassRequested)
            {
                _fullPassRequested = false;
                EnqueueMissingSegmentItems(reason);
            }

            await ProcessPendingItemsAsync(reason, _stopCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[YummyKodik] Media segment warmup failed. reason={Reason}", reason);
        }
        finally
        {
            _workerLock.Release();
        }

        if ((_fullPassRequested || !_pendingItemIds.IsEmpty) && !_stopping)
        {
            ScheduleWorker(TimeSpan.FromSeconds(5), "PendingMediaSegments");
        }
    }

    private void EnqueueMissingSegmentItems(string reason)
    {
        var root = (Plugin.Instance.Configuration.OutputRootPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var candidates = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            Recursive = true
        })
            .OfType<Episode>()
            .Where(IsEligibleEpisode)
            .Where(item => !_mediaSegmentManager.HasSegments(item.Id))
            .Where(item => CanRetryMissingItem(item.Id, now))
            .Take(MaxMissingItemsPerFullPass)
            .ToArray();

        foreach (var item in candidates)
        {
            _pendingItemIds[item.Id] = 0;
        }

        if (candidates.Length > 0)
        {
            _logger.LogInformation(
                "[YummyKodik] Queued media segment warmup for {Count} YummyKodik episode items. reason={Reason}",
                candidates.Length,
                reason);
        }
    }

    private async Task ProcessPendingItemsAsync(string reason, CancellationToken cancellationToken)
    {
        var ids = _pendingItemIds.Keys.ToArray();
        if (ids.Length == 0)
        {
            return;
        }

        var options = BuildYummyKodikSegmentOptions();
        var processed = 0;
        var created = 0;

        foreach (var itemId in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_pendingItemIds.TryRemove(itemId, out _))
            {
                continue;
            }

            if (!CanRetryMissingItem(itemId, DateTime.UtcNow))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(itemId);
            if (item is not Episode episode || !IsEligibleEpisode(episode))
            {
                continue;
            }

            if (_mediaSegmentManager.HasSegments(itemId))
            {
                _nextRetryUtcByItemId.TryRemove(itemId, out _);
                continue;
            }

            processed++;

            await _mediaSegmentManager
                .RunSegmentPluginProviders(episode, options, forceOverwrite: false, cancellationToken)
                .ConfigureAwait(false);

            if (_mediaSegmentManager.HasSegments(itemId))
            {
                _nextRetryUtcByItemId.TryRemove(itemId, out _);
                created++;
            }
            else
            {
                _nextRetryUtcByItemId[itemId] = DateTime.UtcNow.Add(MissingSegmentRetryDelay);
            }
        }

        if (processed > 0)
        {
            _logger.LogInformation(
                "[YummyKodik] Media segment warmup finished. processed={Processed} created={Created} reason={Reason}",
                processed,
                created,
                reason);
        }
    }

    private static LibraryOptions BuildYummyKodikSegmentOptions()
    {
        return new LibraryOptions
        {
            MediaSegmentProviderOrder = new[] { YummyKodikMediaSegmentProvider.ProviderName },
            DisabledMediaSegmentProviders = new[] { "Intro Skipper" }
        };
    }

    private bool CanRetryMissingItem(Guid itemId, DateTime nowUtc)
    {
        return !_nextRetryUtcByItemId.TryGetValue(itemId, out var nextRetryUtc) ||
               nextRetryUtc <= nowUtc;
    }

    private bool IsEligibleEpisode(BaseItem? item)
    {
        if (item is not Episode ep)
        {
            return false;
        }

        var root = (Plugin.Instance.Configuration.OutputRootPath ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(root) ||
            string.IsNullOrWhiteSpace(ep.Path) ||
            !ep.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return IsUnderRoot(root, ep.Path);
    }

    private bool IsUnderRoot(string root, string itemPath)
    {
        try
        {
            return _fileSystem.ContainsSubPath(root, itemPath);
        }
        catch
        {
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

    private void TryHookScanCompletedEvent()
    {
        var names = new[] { "ScanCompleted", "LibraryScanCompleted" };
        var type = _libraryManager.GetType();

        foreach (var name in names)
        {
            try
            {
                var ev = type.GetEvent(name, BindingFlags.Instance | BindingFlags.Public);
                var handlerType = ev?.EventHandlerType;
                if (ev == null || handlerType == null)
                {
                    continue;
                }

                var invoke = handlerType.GetMethod("Invoke");
                var parameters = invoke?.GetParameters();
                if (parameters == null ||
                    parameters.Length != 2 ||
                    !typeof(EventArgs).IsAssignableFrom(parameters[1].ParameterType))
                {
                    continue;
                }

                MethodInfo method;
                if (parameters[1].ParameterType == typeof(EventArgs))
                {
                    method = GetType().GetMethod(nameof(OnScanCompleted), BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new MissingMethodException(nameof(OnScanCompleted));
                }
                else
                {
                    var generic = GetType().GetMethod(nameof(OnScanCompletedGeneric), BindingFlags.Instance | BindingFlags.NonPublic)
                                  ?? throw new MissingMethodException(nameof(OnScanCompletedGeneric));
                    method = generic.MakeGenericMethod(parameters[1].ParameterType);
                }

                var del = Delegate.CreateDelegate(handlerType, this, method);
                ev.AddEventHandler(_libraryManager, del);

                _scanCompletedEvent = ev;
                _scanCompletedHandler = del;
                _logger.LogInformation("[YummyKodik] Hooked library event for media segment warmup: {EventName}", name);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[YummyKodik] Failed to hook {EventName} for media segment warmup (ignored).", name);
            }
        }

        _logger.LogInformation("[YummyKodik] ScanCompleted event not found for media segment warmup (will use IsScanRunning polling fallback).");
    }

    private void UnhookScanCompletedEvent()
    {
        try
        {
            if (_scanCompletedEvent != null && _scanCompletedHandler != null)
            {
                _scanCompletedEvent.RemoveEventHandler(_libraryManager, _scanCompletedHandler);
                _logger.LogInformation("[YummyKodik] Unhooked library ScanCompleted event for media segment warmup.");
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
}
