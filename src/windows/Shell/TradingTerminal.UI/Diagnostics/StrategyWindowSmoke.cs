using System.Diagnostics;
using System.IO;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Threading;
using TradingTerminal.Core.Strategies;

namespace TradingTerminal.UI.Diagnostics;

/// <summary>
/// Dev/CI smoke sweep: opens every catalog strategy window once through the real
/// <see cref="IStrategyFactory"/> path (the same code a catalog double-click runs), waits for the
/// load + render passes, then closes it. Exists to prove the cross-ALC plugin windows — XAML/BAML,
/// MahApps resources, and plugin-private dependencies like HelixToolkit — construct and render
/// against the host's shared WPF assemblies. Each shell wires it behind the
/// <c>--smoke-strategies</c> command-line switch; it never runs in a user flow.
/// <para>
/// The report records each view's <see cref="AssemblyLoadContext"/> name, so a PASS line reading
/// <c>ctx=Plugin:…</c> is positive evidence the window type really came from a plugin context
/// rather than a compile-time reference.
/// </para>
/// </summary>
public static class StrategyWindowSmoke
{
    /// <summary>Opens every strategy in <paramref name="factory"/>, writes a plain-text report to
    /// <paramref name="reportPath"/>, and returns a process exit code — 0 only when every window
    /// opened (and the catalog wasn't empty, so a plugin-load wipeout can't masquerade as a pass).</summary>
    public static async Task<int> RunAsync(
        IStrategyFactory factory,
        string reportPath,
        IEnumerable<string>? loadedPluginNames = null)
    {
        var lines = new List<string>
        {
            $"Strategy window smoke — {DateTime.Now:yyyy-MM-dd HH:mm:ss} — {AppDomain.CurrentDomain.FriendlyName}",
        };
        if (loadedPluginNames is not null)
            lines.Add($"Plugins loaded: {string.Join(", ", loadedPluginNames)}");
        lines.Add($"Catalog strategies: {factory.All.Count}");
        lines.Add(string.Empty);

        var failures = 0;

        // Post-Show faults (async Loaded handlers, first render of a 3D viewport) surface on the
        // dispatcher, not on our call stack — capture them so a window that opens but immediately
        // faults still counts as FAIL. CrashGuard's own handler keeps running; we only observe.
        Exception? dispatcherFault = null;
        DispatcherUnhandledExceptionEventHandler capture = (_, args) => dispatcherFault ??= args.Exception;
        Application.Current.DispatcherUnhandledException += capture;
        try
        {
            foreach (var strategy in factory.All)
            {
                dispatcherFault = null;
                var sw = Stopwatch.StartNew();
                Window? window = null;
                object? viewModel = null;
                try
                {
                    var host = factory.Create(strategy.Id);
                    viewModel = host.ViewModel;
                    // Most strategies ship their own MetroWindow; UserControl views get a bare host
                    // window — InitializeComponent has already run either way, which is the risk point.
                    window = host.View as Window
                        ?? new Window { Title = host.DisplayName, Content = host.View, Width = 1200, Height = 800 };
                    var ctx = AssemblyLoadContext.GetLoadContext(host.View.GetType().Assembly)?.Name ?? "default";

                    window.Show();
                    await PumpAsync(window);
                    if (dispatcherFault is not null) throw dispatcherFault;

                    lines.Add($"PASS  {strategy.Id,-34} {sw.ElapsedMilliseconds,5} ms  view={host.View.GetType().Name}  ctx={ctx}");
                }
                catch (Exception ex)
                {
                    failures++;
                    lines.Add($"FAIL  {strategy.Id,-34} {sw.ElapsedMilliseconds,5} ms  {Flatten(ex)}");
                }
                finally
                {
                    try { window?.Close(); }
                    catch (Exception ex) { lines.Add($"WARN  {strategy.Id,-34} close failed: {Flatten(ex)}"); }
                    try { (viewModel as IDisposable)?.Dispose(); }
                    catch (Exception ex) { lines.Add($"WARN  {strategy.Id,-34} dispose failed: {Flatten(ex)}"); }
                    // Let Closed handlers drain before the next window; a teardown fault is a WARN,
                    // not a FAIL — the window itself opened.
                    dispatcherFault = null;
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (dispatcherFault is not null)
                        lines.Add($"WARN  {strategy.Id,-34} fault during close: {Flatten(dispatcherFault)}");
                }
            }
        }
        finally
        {
            Application.Current.DispatcherUnhandledException -= capture;
        }

        lines.Add(string.Empty);
        lines.Add(failures == 0 && factory.All.Count > 0
            ? $"RESULT: PASS ({factory.All.Count} windows opened)"
            : $"RESULT: FAIL ({failures} of {factory.All.Count} failed)");
        File.WriteAllLines(reportPath, lines);
        return failures == 0 && factory.All.Count > 0 ? 0 : 1;
    }

    private static async Task PumpAsync(Window window)
    {
        // Loaded → Render → a real delay (async Loaded continuations, 3D viewport spin-up,
        // plugin-private demand-loads like HelixToolkit) → Background drain.
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Loaded);
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
        await Task.Delay(400);
        await window.Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background);
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null && parts.Count < 5; e = e.InnerException)
            parts.Add($"{e.GetType().Name}: {e.Message}");
        return string.Join(" <- ", parts);
    }
}
