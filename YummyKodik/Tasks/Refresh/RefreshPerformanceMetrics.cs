using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace YummyKodik.Tasks.Refresh;

internal sealed class RefreshPerformanceMetrics
{
    private readonly Stopwatch _total = Stopwatch.StartNew();
    private readonly Dictionary<string, long> _durationsMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly RefreshRunMetrics? _runMetrics;

    public RefreshPerformanceMetrics(bool enabled, RefreshRunMetrics? runMetrics = null)
    {
        Enabled = enabled;
        _runMetrics = runMetrics;
    }

    public bool Enabled { get; }

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
        _runMetrics?.AddDuration(key, elapsed);
        if (_durationsMs.TryGetValue(key, out var current))
        {
            _durationsMs[key] = current + elapsedMs;
            return;
        }

        _durationsMs[key] = elapsedMs;
    }

    public void AddCount(string key, int delta = 1)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(key) || delta == 0)
        {
            return;
        }

        _runMetrics?.AddCount(key, delta);

        if (_counts.TryGetValue(key, out var current))
        {
            _counts[key] = current + delta;
            return;
        }

        _counts[key] = delta;
    }

    public void LogSummary(ILogger logger, string title, string key)
    {
        if (!Enabled)
        {
            return;
        }

        var stageSummary = _durationsMs.Count == 0
            ? "-"
            : string.Join(
                ", ",
                _durationsMs
                    .OrderByDescending(x => x.Value)
                    .Select(x => $"{x.Key}={x.Value}ms"));

        var countSummary = _counts.Count == 0
            ? "-"
            : string.Join(
                ", ",
                _counts
                    .OrderBy(x => x.Key, StringComparer.Ordinal)
                    .Select(x => $"{x.Key}={x.Value}"));

        logger.LogWarning(
            "[YummyKodik][perf] Refresh '{Title}' (key='{Key}') took {ElapsedMs}ms. stages: {Stages}. counts: {Counts}",
            title,
            key,
            _total.ElapsedMilliseconds,
            stageSummary,
            countSummary);
    }

    public readonly struct MeasureScope : IDisposable
    {
        private readonly RefreshPerformanceMetrics? _owner;
        private readonly string? _key;
        private readonly long _startedAt;

        public MeasureScope(RefreshPerformanceMetrics owner, string key)
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

            var elapsed = Stopwatch.GetElapsedTime(_startedAt);
            _owner.AddDuration(_key, elapsed);
        }
    }
}
