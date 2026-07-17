using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Default <see cref="IShellWindowHost"/>: owns the single-instance window registry and the generic
/// open/focus/dispose behaviour behind the shell "Opening…" loading curtain. This is the machinery
/// lifted out of <c>MainWindowViewModel</c> so tier-exclusive launchers
/// (<see cref="IShellExtendedToolCommands"/> implementations shipped by the Professional shell) reuse it.
/// </summary>
internal sealed class ShellWindowHost : IShellWindowHost
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ShellWindowHost> _logger;
    private readonly Dictionary<string, Window> _openWindows = new(StringComparer.Ordinal);

    public ShellWindowHost(IServiceProvider services, ILogger<ShellWindowHost> logger)
    {
        _services = services;
        _logger = logger;
    }

    public IShellOverlayPresenter? OverlayPresenter { get; set; }

    public bool TryActivate(string windowId)
    {
        if (_openWindows.TryGetValue(windowId, out var existing)) { existing.Activate(); return true; }
        return false;
    }

    public bool IsOpen(string windowId) => _openWindows.ContainsKey(windowId);

    public void Register(string windowId, Window window) => _openWindows[windowId] = window;

    public void Unregister(string windowId) => _openWindows.Remove(windowId);

    public void OpenWithOverlay(string title, string detail, Action build)
    {
        OverlayPresenter?.Show(title, detail);

        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            try { build(); }
            catch (Exception ex) { _logger.LogError(ex, "Failed while opening {Title}", title); }
            finally { OverlayPresenter?.Hide(); }
        }));
    }

    public void OpenHostedTool<TVm, TView>(string windowId, string title, string detail,
        double width = ToolHostWindow.DefaultWidth, double height = ToolHostWindow.DefaultHeight)
        where TVm : class
        where TView : FrameworkElement
    {
        if (TryActivate(windowId)) return;

        OpenWithOverlay($"Opening {title}…", detail, () =>
        {
            var vm = _services.GetRequiredService<TVm>();
            var view = _services.GetRequiredService<TView>();
            view.DataContext = vm;

            var window = ToolHostWindow.Create(title, view, width, height);
            window.Owner = Application.Current.MainWindow;
            window.Closed += (_, _) =>
            {
                _openWindows.Remove(windowId);
                if (vm is IDisposable d) d.Dispose();
            };
            _openWindows[windowId] = window;
            window.Show();
            _logger.LogInformation("Opened {Title} window", title);
        });
    }

    public void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
        where TVm : class
        where TWindow : Window
    {
        if (TryActivate(windowId)) return;

        OpenWithOverlay($"Opening {title}…", detail, () =>
        {
            var vm = _services.GetRequiredService<TVm>();
            var window = _services.GetRequiredService<TWindow>();
            window.DataContext = vm;
            // Standalone tool/chart windows own their XAML, so top the same amber "SIMULATED DATA"
            // strip generically here (collapsed unless the Simulated broker is connected).
            UI.Controls.SimulatedDataBanner.AttachTo(window);
            window.Owner = Application.Current.MainWindow;
            window.Closed += (_, _) =>
            {
                _openWindows.Remove(windowId);
                if (vm is IDisposable d) d.Dispose();
            };
            _openWindows[windowId] = window;
            window.Show();
            _logger.LogInformation("Opened {Title} window", title);
        });
    }
}
