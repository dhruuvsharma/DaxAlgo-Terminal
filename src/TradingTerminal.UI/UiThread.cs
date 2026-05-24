using System.Windows;

namespace TradingTerminal.UI;

/// <summary>
/// Tiny marshaling helper for VMs that subscribe to background-threaded sources (hub subjects,
/// channels fed from ingest pumps) and need to mutate observable state on the WPF dispatcher.
/// Runs the action synchronously when already on the UI thread, and silently no-ops the
/// marshaling when there's no WPF <see cref="Application"/> (e.g., unit tests on a worker thread).
/// </summary>
public static class UiThread
{
    /// <summary>Runs <paramref name="action"/> on the UI thread, awaiting its completion.</summary>
    public static Task RunAsync(Func<Task> action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) return action();
        return d.InvokeAsync(action).Task.Unwrap();
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    public static Task RunAsync(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) { action(); return Task.CompletedTask; }
        return d.InvokeAsync(action).Task;
    }
}
