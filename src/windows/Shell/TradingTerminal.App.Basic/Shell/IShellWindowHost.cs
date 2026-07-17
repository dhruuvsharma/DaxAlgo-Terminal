using System.Windows;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Generic single-instance window machinery shared by the shell view-model and the per-edition
/// tier-exclusive launch commands (<see cref="IShellExtendedToolCommands"/>). Lifted out of
/// <c>MainWindowViewModel</c> so a Professional-only launcher can live outside App.Core yet still
/// reuse the exact same open/focus/dispose behaviour and the shell's "Opening…" loading curtain.
/// Registered as a singleton by <c>AddShell()</c>.
/// </summary>
public interface IShellWindowHost
{
    /// <summary>The presenter that paints the shell "Opening…" curtain. Wired by the shell VM once the
    /// main window exists; null before that (openers still work, just with no curtain).</summary>
    IShellOverlayPresenter? OverlayPresenter { get; set; }

    /// <summary>Focuses an already-open window for <paramref name="windowId"/>; returns false if none.</summary>
    bool TryActivate(string windowId);

    /// <summary>Whether a single-instance window for <paramref name="windowId"/> is currently open.</summary>
    bool IsOpen(string windowId);

    /// <summary>Tracks <paramref name="window"/> under <paramref name="windowId"/> (single-instance registry).</summary>
    void Register(string windowId, Window window);

    /// <summary>Removes the tracked window for <paramref name="windowId"/>.</summary>
    void Unregister(string windowId);

    /// <summary>Shows the loading curtain (<paramref name="title"/>/<paramref name="detail"/>), then runs
    /// <paramref name="build"/> on a Background dispatch so the curtain paints before the synchronous view
    /// construction freezes the UI thread. The curtain is always taken down afterwards, even on throw.</summary>
    void OpenWithOverlay(string title, string detail, Action build);

    /// <summary>Opens (or focuses) a single-instance tool whose view is a <see cref="FrameworkElement"/>
    /// (a UserControl), wrapped in a themed <see cref="ToolHostWindow"/>. VM disposed on close.</summary>
    void OpenHostedTool<TVm, TView>(string windowId, string title, string detail,
        double width = ToolHostWindow.DefaultWidth, double height = ToolHostWindow.DefaultHeight)
        where TVm : class
        where TView : FrameworkElement;

    /// <summary>Opens (or focuses) a single-instance tool that ships its own <see cref="Window"/>.
    /// VM disposed on close.</summary>
    void OpenWindowTool<TVm, TWindow>(string windowId, string title, string detail)
        where TVm : class
        where TWindow : Window;
}

/// <summary>
/// Seam the shell view-model implements so the window host can drive the shell's busy-overlay
/// (the "Opening …" curtain bound in MainWindow) without the host owning any bindable state.
/// </summary>
public interface IShellOverlayPresenter
{
    /// <summary>Show the curtain with the given headline + detail.</summary>
    void Show(string title, string detail);

    /// <summary>Hide the curtain.</summary>
    void Hide();
}
