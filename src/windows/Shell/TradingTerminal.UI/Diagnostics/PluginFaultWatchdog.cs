using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Threading;

namespace TradingTerminal.UI.Diagnostics;

/// <summary>
/// Runtime safety net for strategy plugins: observes unhandled dispatcher and unobserved task
/// exceptions, walks the faulting stack (and inner/aggregate chains), and attributes the fault to
/// the plugin whose <see cref="AssemblyLoadContext"/> owns a frame — plugin contexts are named
/// <c>Plugin:&lt;assembly&gt;</c> by the loader's convention. Every attributed fault is logged;
/// <see cref="PluginFaultTracker.StrikeLimit"/> faults in one session fire the strike-out callback
/// once (the shells persist a quarantine there, so the plugin runs again only after the user
/// re-enables it). Purely an observer: CrashGuard keeps owning whether the app survives the fault.
/// </summary>
public static class PluginFaultWatchdog
{
    private const string PluginContextPrefix = "Plugin:"; // PluginLoadContext naming convention

    /// <summary>Attaches to <paramref name="app"/>'s dispatcher + the task scheduler. Returns a
    /// disposable that detaches (normally lives for the app's lifetime).</summary>
    public static IDisposable Attach(
        Application app,
        int strikeLimit,
        Action<string, string> onStrikeOut,
        Action<string, string, string>? log = null)
    {
        var tracker = new PluginFaultTracker(strikeLimit);

        DispatcherUnhandledExceptionEventHandler onDispatcher = (_, e) =>
            Observe(e.Exception, tracker, onStrikeOut, log);
        EventHandler<UnobservedTaskExceptionEventArgs> onTask = (_, e) =>
            Observe(e.Exception, tracker, onStrikeOut, log);

        app.DispatcherUnhandledException += onDispatcher;
        TaskScheduler.UnobservedTaskException += onTask;
        return new Detach(() =>
        {
            app.DispatcherUnhandledException -= onDispatcher;
            TaskScheduler.UnobservedTaskException -= onTask;
        });
    }

    private static void Observe(
        Exception exception,
        PluginFaultTracker tracker,
        Action<string, string> onStrikeOut,
        Action<string, string, string>? log)
    {
        try
        {
            if (!TryAttribute(exception, out var plugin)) return;

            var (strikes, struckOutNow) = tracker.RecordFault(plugin);
            var summary = $"{exception.GetType().Name}: {exception.Message}";
            log?.Invoke("Plugins", "Warning",
                $"Unhandled fault #{strikes} attributed to strategy plugin '{plugin}': {summary}");
            if (struckOutNow)
                onStrikeOut(plugin, $"{strikes} unhandled faults this session; last: {summary}");
        }
        catch
        {
            // The watchdog must never make a fault worse.
        }
    }

    /// <summary>Finds the plugin (folder name) whose load context owns a frame of
    /// <paramref name="exception"/> or any inner/aggregate exception.</summary>
    internal static bool TryAttribute(Exception exception, out string plugin)
    {
        for (Exception? e = exception; e is not null; e = e.InnerException)
        {
            foreach (var frame in new StackTrace(e).GetFrames())
            {
                var assembly = frame?.GetMethod()?.Module.Assembly;
                if (assembly is null) continue;
                var contextName = AssemblyLoadContext.GetLoadContext(assembly)?.Name;
                if (contextName?.StartsWith(PluginContextPrefix, StringComparison.Ordinal) == true)
                {
                    plugin = contextName[PluginContextPrefix.Length..];
                    return true;
                }
            }

            if (e is AggregateException aggregate)
                foreach (var inner in aggregate.InnerExceptions)
                    if (TryAttribute(inner, out plugin))
                        return true;
        }

        plugin = string.Empty;
        return false;
    }

    private sealed class Detach(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
