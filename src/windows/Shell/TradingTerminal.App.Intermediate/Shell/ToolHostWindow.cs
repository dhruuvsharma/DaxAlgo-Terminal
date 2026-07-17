using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;
using TradingTerminal.UI.Controls;

namespace TradingTerminal.App.Shell;

/// <summary>
/// Generic single-content host window for tools that used to render as AvalonDock
/// document tabs. The docking framework was removed (every tool, strategy and chart
/// is its own window now), so these tools are wrapped in a plain themed
/// <see cref="MetroWindow"/> instead of a dock document.
/// </summary>
public sealed class ToolHostWindow : MetroWindow
{
    private ToolHostWindow() { }

    /// <summary>Builds a themed host window around an already-DataContext'd tool view.</summary>
    /// <summary>The size a full workspace tool opens at. Small panels (e.g. the recorder) pass their
    /// own; everything else inherits these.</summary>
    public const double DefaultWidth = 1100;
    public const double DefaultHeight = 760;

    public static ToolHostWindow Create(string title, FrameworkElement content,
        double width = DefaultWidth, double height = DefaultHeight)
    {
        var window = new ToolHostWindow
        {
            Title = title,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            // Top an amber "SIMULATED DATA" strip above the tool view — collapsed unless the
            // Simulated broker is connected, so a synthetic feed is never mistaken for a live one.
            Content = SimulatedDataBanner.WrapTop(new ContentControl { Content = content }),
        };

        // Match the shell chrome — the brushes/fonts are app-wide merged dictionaries.
        window.SetResourceReference(BackgroundProperty, "Background.Primary");
        window.SetResourceReference(ForegroundProperty, "Text.Primary");
        window.SetResourceReference(FontFamilyProperty, "Font.Mono");
        return window;
    }
}
