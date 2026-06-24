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
}
