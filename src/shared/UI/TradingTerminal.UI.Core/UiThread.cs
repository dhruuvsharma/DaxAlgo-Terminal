namespace TradingTerminal.UI;

/// <summary>
/// Tiny marshaling helper for VMs that subscribe to background-threaded sources (hub subjects,
/// channels fed from ingest pumps) and need to mutate observable state on the UI thread.
///
/// WPF-free and shared by both UI heads: set <see cref="Marshal"/> once at startup — the WPF shell
/// points it at its Dispatcher, the Avalonia shell at <c>Dispatcher.UIThread</c>. The default runs
/// inline, which is also the correct no-op for unit tests on a worker thread.
/// </summary>
public static class UiThread
{
    /// <summary>
    /// Runs an action on the UI thread and returns a task for its completion. Assigned once during
    /// app startup by whichever UI head is hosting; defaults to inline execution.
    /// </summary>
    public static Func<Func<Task>, Task> Marshal { get; set; } = static action => action();

    /// <summary>Runs <paramref name="action"/> on the UI thread, awaiting its completion.</summary>
    public static Task RunAsync(Func<Task> action) => Marshal(action);

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public static Task RunAsync(Action action) => Marshal(() => { action(); return Task.CompletedTask; });

    /// <summary>
    /// Creates and starts a coalescing render timer firing <paramref name="tick"/> every
    /// <paramref name="interval"/>; the returned <see cref="IDisposable"/> stops and releases it.
    /// WPF-free so both UI heads share the VMs: the default uses a <see cref="System.Threading.Timer"/>
    /// whose ticks are marshalled onto the UI thread via <see cref="Marshal"/>, so the tick body runs
    /// where it can safely touch bound state (and inline under tests). A head may override this with
    /// its native dispatcher timer if precise UI-thread cadence matters.
    /// </summary>
    public static Func<TimeSpan, Action, IDisposable> CreateRenderTimer { get; set; } = DefaultRenderTimer;

    // Ownership: the timer is RETURNED as the IDisposable — the caller (the VM) owns it and stops it
    // by disposing the handle in its own Dispose()/teardown (see the consuming VMs' StopRenderTimer:
    // `_renderTimer?.Dispose()`). The factory cannot dispose it here (it would never fire), so the
    // teardown lives in the returned handle.
    private static IDisposable DefaultRenderTimer(TimeSpan interval, Action tick)
    {
        var timer = new System.Threading.Timer(_ => Marshal(() => { tick(); return Task.CompletedTask; }), null, interval, interval);
        return new TimerHandle(timer);
    }

    /// <summary>Owns the render timer; disposing the handle stops + releases it.</summary>
    private sealed class TimerHandle(System.Threading.Timer timer) : IDisposable
    {
        public void Dispose() => timer.Dispose();
    }
}
