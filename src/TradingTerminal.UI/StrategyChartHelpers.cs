using ScottPlot;
using ScottPlot.WPF;

namespace TradingTerminal.UI;

/// <summary>
/// Shared helpers for the dark-themed ScottPlot setup every strategy window uses.
/// Centralising the colour palette + axis configuration keeps the 22 strategy
/// code-behinds focused on indicator math, not chart bookkeeping.
/// </summary>
public static class StrategyChartHelpers
{
    public static readonly ScottPlot.Color BackgroundColor = ScottPlot.Color.FromHex("#1E1E1E");
    public static readonly ScottPlot.Color GridColor       = ScottPlot.Color.FromHex("#3F3F46");
    public static readonly ScottPlot.Color TextColor       = ScottPlot.Color.FromHex("#DCDCDC");
    public static readonly ScottPlot.Color AccentColor     = ScottPlot.Color.FromHex("#007ACC");
    public static readonly ScottPlot.Color BullishColor    = ScottPlot.Color.FromHex("#26A69A");
    public static readonly ScottPlot.Color BearishColor    = ScottPlot.Color.FromHex("#EF5350");
    public static readonly ScottPlot.Color WarningColor    = ScottPlot.Color.FromHex("#F1C40F");
    public static readonly ScottPlot.Color MutedColor      = ScottPlot.Color.FromHex("#9D9D9D");

    /// <summary>Apply the standard dark style to a ScottPlot WPF host.</summary>
    public static void ConfigureDarkPlot(WpfPlot host, bool dateTimeBottom = true)
    {
        var plot = host.Plot;
        plot.FigureBackground.Color = BackgroundColor;
        plot.DataBackground.Color   = BackgroundColor;
        plot.Axes.Color(TextColor);
        plot.Grid.MajorLineColor    = GridColor;
        if (dateTimeBottom) plot.Axes.DateTimeTicksBottom();
        host.Refresh();
    }
}
