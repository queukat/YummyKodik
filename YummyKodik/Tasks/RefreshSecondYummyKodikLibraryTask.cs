using MediaBrowser.Model.Tasks;

namespace YummyKodik.Tasks;

/// <summary>Independent schedule for the second folder, using the same refresh owner and run gate.</summary>
public sealed class RefreshSecondYummyKodikLibraryTask(RefreshYummyKodikLibraryTask refresh) : IScheduledTask
{
    public string Key => "YummyKodikSecondLibraryRefresh";
    public string Name => "YummyKodik: second library refresh";
    public string Description => "Updates only the second Yummy list and its output folder. Does nothing when the second list is disabled.";
    public string Category => "YummyKodik";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromDays(1).Ticks };
    }

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => refresh.ExecuteTargetAsync(1, progress, cancellationToken);
}
