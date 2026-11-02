using System.Reflection;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using YummyKodik.Media;
using YummyKodik.Versioning;

internal static class HostedPlaybackLifecycleTests
{
    public static void StopDrainsWorkersAndDeadlineAllowsSafeUnwind()
        => VerifyAsync().GetAwaiter().GetResult();

    public class LifecycleLibraryProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => null;
    }

    private static async Task VerifyAsync()
    {
        foreach (var warmup in new[] { false, true })
        foreach (var cancelDrain in new[] { false, true })
        {
            var library = DispatchProxy.Create<ILibraryManager, LifecycleLibraryProxy>();
            IHostedService service = warmup
                ? new YummyKodikMediaSegmentWarmupHostedService(library, null!, NullLogger<YummyKodikMediaSegmentWarmupHostedService>.Instance)
                : new YummyKodikEpisodeVersionsMergeHostedService(library, NullLogger<YummyKodikEpisodeVersionsMergeHostedService>.Instance);
            var gate = (SemaphoreSlim)service.GetType().GetField(warmup ? "_workerLock" : "_mergeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
            await gate.WaitAsync(); // Hold exactly the admission gate an active host operation owns.
            using var deadline = new CancellationTokenSource();
            var stopping = service.StopAsync(deadline.Token);
            if (stopping.IsCompleted) throw new Exception("Stop must drain the active worker before returning.");
            if (cancelDrain)
            {
                deadline.Cancel();
                try { await stopping; throw new Exception("Host deadline must bound shutdown."); }
                catch (OperationCanceledException) { }
                ((IDisposable)service).Dispose();
                // The in-flight operation can still finish after a host deadline and disposal.
                gate.Release();
            }
            else
            {
                gate.Release();
                await stopping;
            }
            await service.StopAsync(CancellationToken.None);
            ((IDisposable)service).Dispose();
            ((IDisposable)service).Dispose();
        }
    }
}
