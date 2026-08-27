using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Tasks.Refresh;

internal sealed class RefreshRunMetrics
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<string, long> _durationsMs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);

    public RefreshRunMetrics(bool enabled)
    {
        Enabled = enabled;
        RunId = Guid.NewGuid().ToString("N")[..8];
    }

    public bool Enabled { get; }

    public string RunId { get; }

    public MeasureScope Measure(string key)
    {
        return Enabled ? new MeasureScope(this, key) : default;
    }

    public void AddDuration(string key, TimeSpan elapsed)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var elapsedMs = Math.Max(0L, (long)Math.Round(elapsed.TotalMilliseconds));
        _durationsMs.AddOrUpdate(key, elapsedMs, (_, current) => current + elapsedMs);
    }

    public void AddCount(string key, long delta = 1)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(key) || delta == 0)
        {
            return;
        }

        _counts.AddOrUpdate(key, delta, (_, current) => current + delta);
    }

    public void LogSummary(ILogger logger, string outcome = "completed")
    {
        if (!Enabled)
        {
            return;
        }

        var durationSnapshot = _durationsMs.ToArray();
        var countSnapshot = _counts.ToArray();
        var phaseSummary = durationSnapshot.Length == 0
            ? "-"
            : string.Join(
                ", ",
                durationSnapshot
                    .OrderByDescending(x => x.Value)
                    .ThenBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}={x.Value}ms"));
        var countSummary = countSnapshot.Length == 0
            ? "-"
            : string.Join(
                ", ",
                countSnapshot
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}={x.Value}"));

        var kodikHttpRequests = SumCounts(countSnapshot, key => key == "kodik.http_requests");
        var cacheHits = SumCounts(countSnapshot, key => key.EndsWith(".cache_hits", StringComparison.Ordinal));
        var filesChecked = SumCounts(countSnapshot, key => key.EndsWith(".files_checked", StringComparison.Ordinal));
        var filesChanged = SumCounts(
            countSnapshot,
            key => key.StartsWith("io.", StringComparison.Ordinal) &&
                   (key.EndsWith("_created", StringComparison.Ordinal) ||
                    key.EndsWith("_updated", StringComparison.Ordinal) ||
                    key == "io.file_deleted"));

        // Performance summaries deliberately use Warning as a narrow bridge through the
        // configured Warning minimum. They are emitted only when the existing performance flag is on.
        logger.LogWarning(
            "[YummyKodik][perf] Run {RunId} outcome={Outcome} took {ElapsedMs}ms. totals: kodikHttpRequests={KodikHttpRequests}, cacheHits={CacheHits}, filesChecked={FilesChecked}, filesChanged={FilesChanged}. phases: {Phases}. counts: {Counts}",
            RunId,
            outcome,
            _total.ElapsedMilliseconds,
            kodikHttpRequests,
            cacheHits,
            filesChecked,
            filesChanged,
            phaseSummary,
            countSummary);
    }

    private static long SumCounts(
        IReadOnlyList<KeyValuePair<string, long>> counts,
        Func<string, bool> predicate)
    {
        return counts.Where(x => predicate(x.Key)).Sum(x => x.Value);
    }

    public readonly struct MeasureScope : IDisposable
    {
        private readonly RefreshRunMetrics? _owner;
        private readonly string? _key;
        private readonly long _startedAt;

        public MeasureScope(RefreshRunMetrics owner, string key)
        {
            _owner = owner;
            _key = key;
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_owner == null || string.IsNullOrWhiteSpace(_key))
            {
                return;
            }

            _owner.AddDuration(_key, Stopwatch.GetElapsedTime(_startedAt));
        }
    }
}
