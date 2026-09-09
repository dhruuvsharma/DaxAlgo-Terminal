using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Default <see cref="IShellWindowHost"/>: owns the single-instance window registry and the generic
/// open/focus/dispose behaviour behind the shell "Opening…" loading curtain.
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed while opening {Title}", title);

                // Say so. This used to log and return, so a window that failed to construct produced
                // no window and no message - the user clicked a menu item and nothing whatsoever
                // happened, with the only evidence buried in the Activity Log. A failure the user
                // triggered has to be visible where they triggered it.
                MessageBox.Show(
                    Application.Current?.MainWindow!,
                    $"{title} could not be opened.\n\n{ex.Message}\n\n" +
                    "The Activity Log has the full detail.",
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally { OverlayPresenter?.Hide(); }
        }));
    }

    /// <summary>
    /// Opens ANOTHER instance of a tool, however many are already open.
    ///
    /// <para>For the tools where a second window is a second piece of work rather than a duplicate of
    /// the first. Hyperion is the case that forced it: the builder was a single window on a fixed id,
    /// so opening it twice re-focused the one you already had, and its view-model was a singleton, so
    /// even a second window would have shared one conversation, one session and one Stop button. There
    /// was no way to have two strategies in flight — which is the normal way anybody works.</para>
    ///
    /// <para>Each gets a numbered id so the host can still track and close it, and its own view-model
    /// instance, which is what actually separates the two.</para>
    /// </summary>
    public void OpenAnotherHostedTool<TVm, TView>(string windowId, string title, string detail,
        double width = ToolHostWindow.DefaultWidth, double height = ToolHostWindow.DefaultHeight)
        where TVm : class
        where TView : FrameworkElement
    {
        var n = 1;
        while (_openWindows.ContainsKey($"{windowId}#{n}")) n++;

        OpenHostedTool<TVm, TView>($"{windowId}#{n}", n == 1 ? title : $"{title} {n}", detail, width, height);
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
