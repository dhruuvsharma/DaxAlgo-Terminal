using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TradingTerminal.UI.Controls;

/// <summary>
/// Slim amber "SIMULATED DATA — not a live feed" strip shown at the top of any tool, chart or
/// strategy window while the Simulated broker is connected (see <see cref="SimulatedDataState"/>).
/// It collapses itself when the feed is live, and unsubscribes on unload so it never leaks.
/// </summary>
/// <remarks>
/// The amber (<c>#B45309</c>) and copy match the persistent banner the shell already shows on the
/// MainWindow, so a synthetic feed reads the same everywhere it surfaces. Built in code (no XAML) so
/// hosts can wrap an existing window in one call via <see cref="WrapTop"/> / <see cref="AttachTo"/>.
/// </remarks>
public sealed class SimulatedDataBanner : Border
{
    public SimulatedDataBanner()
    {
        Background = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)); // amber-700, matches the shell banner
        Padding = new Thickness(12, 5, 12, 5);
        Child = new TextBlock
        {
            Text = "⚠  SIMULATED DATA — not a live feed",
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Visibility = Visibility.Collapsed;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // WPF raises Loaded again after a reparent (StrategyWindowBase re-hosts content under its
        // busy-overlay grid), so drop any prior handler first to stay single-subscribed.
        SimulatedDataState.Changed -= OnStateChanged;
        SimulatedDataState.Changed += OnStateChanged;
        Sync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => SimulatedDataState.Changed -= OnStateChanged;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess()) Sync();
        else Dispatcher.Invoke(Sync);
    }

    private void Sync() => Visibility = SimulatedDataState.IsActive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Docks a fresh banner above <paramref name="content"/> and returns the composed host,
    /// so a caller can wrap a window's content in a single expression.</summary>
    public static BannerHost WrapTop(UIElement content)
    {
        var host = new BannerHost();
        var banner = new SimulatedDataBanner();
        DockPanel.SetDock(banner, Dock.Top);
        host.Children.Add(banner);
        host.Children.Add(content); // LastChildFill → fills the space under the banner
        return host;
    }

    /// <summary>Wraps a window's content with the banner, in place. Idempotent — a window whose
    /// content is already a <see cref="BannerHost"/> is left untouched, so it is safe to call from
    /// several open paths that may overlap.</summary>
    public static void AttachTo(Window window)
    {
        if (window.Content is UIElement content and not BannerHost)
        {
            // Detach the existing content from the window before re-parenting it under the banner —
            // WPF forbids an element having two logical parents, so wrapping in place would throw.
            window.Content = null;
            window.Content = WrapTop(content);
        }
    }

    /// <summary>Marker panel produced by <see cref="WrapTop"/> so <see cref="AttachTo"/> can tell an
    /// already-wrapped window from a fresh one.</summary>
    public sealed class BannerHost : DockPanel { }
}
