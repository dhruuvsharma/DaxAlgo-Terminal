using System.Windows;
using System.Windows.Controls;
using MahApps.Metro.Controls;

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
    public static ToolHostWindow Create(string title, FrameworkElement content)
    {
        var window = new ToolHostWindow
        {
            Title = title,
            Width = 1100,
            Height = 760,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ContentControl { Content = content },
        };

        // Match the shell chrome — the brushes/fonts are app-wide merged dictionaries.
        window.SetResourceReference(BackgroundProperty, "Background.Primary");
        window.SetResourceReference(ForegroundProperty, "Text.Primary");
        window.SetResourceReference(FontFamilyProperty, "Font.Mono");
        return window;
    }
}
