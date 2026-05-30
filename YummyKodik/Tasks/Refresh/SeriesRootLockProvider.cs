using System.Collections.Concurrent;

namespace YummyKodik.Tasks.Refresh;

internal sealed class SeriesRootLockProvider
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _seriesRootLocks = new(StringComparer.OrdinalIgnoreCase);

    public async Task<SeriesRootLockReleaser> AcquireAsync(string seriesRoot, CancellationToken cancellationToken)
    {
        var key = NormalizeSeriesRootLockKey(seriesRoot);
        var gate = _seriesRootLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new SeriesRootLockReleaser(gate);
    }

    private static string NormalizeSeriesRootLockKey(string seriesRoot)
    {
        return Path.GetFullPath(seriesRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    public readonly struct SeriesRootLockReleaser : IDisposable
    {
        private readonly SemaphoreSlim _gate;

        public SeriesRootLockReleaser(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            _gate.Release();
        }
    }
}
