using System.Windows;
using System.Windows.Threading;

namespace TradingTerminal.Tests.Controls;

/// <summary>
/// One STA thread, one <see cref="Application"/>, shared by every test that realizes a real WPF tree.
/// Call <see cref="Run"/> and your code runs there.
/// <para>
/// This exists because WPF's two hard rules collide with xUnit's threading. There may be exactly one
/// Application per AppDomain, ever — and it, and everything in its resource dictionaries, belongs to the
/// thread that created it. But <c>[WpfFact]</c> spins up a <b>fresh</b> STA thread per test, so an
/// Application created by the first WPF test is untouchable from the second ("The calling thread cannot
/// access this object because a different thread owns it"), and re-creating one is banned ("Cannot create
/// more than one System.Windows.Application instance in the same AppDomain"). Worse, the default
/// <see cref="ShutdownMode.OnLastWindowClose"/> means the first test to close its host window shuts the
/// Application down — so which test blew up depended on discovery order, and looked like flakiness in a
/// test that had nothing to do with it.
/// </para>
/// <para>
/// So the tests that need an Application take this thread instead of xUnit's, and use a plain
/// <c>[Fact]</c>. Nothing here is ever shut down; the thread is a background thread and dies with the run.
/// </para>
/// </summary>
internal static class WpfTestApp
{
    /// <summary>Any style the panels resolve by StaticResource — its presence means the theme is merged.</summary>
    private const string ThemeProbeKey = "App.HeaderBar";

    private static readonly string[] ThemeDictionaries =
    [
        "pack://application:,,,/MahApps.Metro;component/Styles/Themes/Dark.Blue.xaml",
        "pack://application:,,,/MahApps.Metro;component/Styles/Controls.xaml",
        "pack://application:,,,/MahApps.Metro;component/Styles/Fonts.xaml",
        "pack://application:,,,/TradingTerminal.UI;component/Themes/TvDark.xaml",
        "pack://application:,,,/TradingTerminal.UI;component/Themes/Dark.xaml",
        "pack://application:,,,/TradingTerminal.UI;component/Themes/Components.xaml",
        "pack://application:,,,/TradingTerminal.UI;component/Themes/StrategyShellStyles.xaml",
    ];

    private static readonly Lazy<Dispatcher> UiThread = new(Start, isThreadSafe: true);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            foreach (var source in ThemeDictionaries)
                app.Resources.MergedDictionaries.Add(
                    new ResourceDictionary { Source = new Uri(source, UriKind.Absolute) });

            ready.SetResult(Dispatcher.CurrentDispatcher);
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "WpfTestApp",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return ready.Task.GetAwaiter().GetResult();
    }

    /// <summary>Runs <paramref name="action"/> on the shared UI thread, rethrowing anything it throws on
    /// the caller's thread so assertions and expected exceptions behave normally.</summary>
    internal static void Run(Action action) => UiThread.Value.Invoke(action);

    /// <summary>The shared Application, themed with the shell's dictionaries. Only valid inside
    /// <see cref="Run"/> — it belongs to that thread.</summary>
    internal static Application Current
    {
        get
        {
            var app = Application.Current
                      ?? throw new InvalidOperationException("Use WpfTestApp.Run — the Application lives on its thread.");
            if (!app.Resources.Contains(ThemeProbeKey))
                throw new InvalidOperationException($"Theme dictionaries missing ('{ThemeProbeKey}' unresolved).");
            return app;
        }
    }
}
