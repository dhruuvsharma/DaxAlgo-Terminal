using ScottPlot.WPF;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Centralises the ScottPlot drawing for every heatmap window so each window's code-behind is a
/// one-liner (<c>HeatmapRenderer.Render(HeatmapPlot, vm.CurrentFrame)</c>). It (re)asserts the dark
/// theme on every render so the plot can never fall back to ScottPlot's light default, then draws the
/// frame: a gridded <see cref="HeatmapFrame"/> (Turbo / zero-centred Balance colormap, optional mid
/// overlay, optional instrument row labels) or a scatter <see cref="BubbleFrame"/> (volume bubbles).
/// </summary>
internal static class HeatmapRenderer
{
    // Dark palette — matches TradingTerminal.UI.StrategyChartHelpers so heatmaps sit in the app theme.
    private static readonly ScottPlot.Color Background = ScottPlot.Color.FromHex("#1E1E1E");
    private static readonly ScottPlot.Color Grid       = ScottPlot.Color.FromHex("#3F3F46");
    private static readonly ScottPlot.Color Text        = ScottPlot.Color.FromHex("#DCDCDC");
    private static readonly ScottPlot.Color BuyColor    = new(38, 166, 154, 190);   // #26A69A
    private static readonly ScottPlot.Color SellColor   = new(239, 83, 80, 190);    // #EF5350
    private static readonly ScottPlot.Color NeutralBubble = new(160, 160, 160, 170);

    public static void Render(WpfPlot host, IHeatmapFrame? frame)
    {
        var plot = host.Plot;
        plot.Clear();
        ApplyDarkTheme(plot);

        switch (frame)
        {
            case HeatmapFrame grid:
                RenderGrid(plot, grid);
                break;
            case BubbleFrame bubbles:
                RenderBubbles(plot, bubbles);
                break;
        }

        host.Refresh();
    }

    /// <summary>Force the dark figure/data/axis/grid colours. Idempotent and cheap — run every render
    /// so nothing in the draw path can leave the plot on its light default.</summary>
    private static void ApplyDarkTheme(ScottPlot.Plot plot)
    {
        plot.FigureBackground.Color = Background;
        plot.DataBackground.Color = Background;
        plot.Axes.Color(Text);
        plot.Grid.MajorLineColor = Grid;
    }

    private static void RenderGrid(ScottPlot.Plot plot, HeatmapFrame frame)
    {
        if (frame.Cells.GetLength(0) == 0 || frame.Cells.GetLength(1) == 0) return;

        int rows = frame.Cells.GetLength(0);
        int cols = frame.Cells.GetLength(1);

        var hm = plot.Add.Heatmap(frame.Cells);
        hm.Extent = new ScottPlot.CoordinateRect(frame.XMin, frame.XMax, frame.YMin, frame.YMax);

        if (frame.Palette == HeatmapPalette.Diverging)
        {
            hm.Colormap = new ScottPlot.Colormaps.Balance();
            double m = 0;
            foreach (var v in frame.Cells) { double a = Math.Abs(v); if (!double.IsNaN(a) && a > m) m = a; }
            if (m > 0) hm.ManualRange = new ScottPlot.Range(-m, m);
        }
        else
        {
            hm.Colormap = new ScottPlot.Colormaps.Turbo();
        }

        if (frame.Overlay is { } ov && ov.Length == cols)
        {
            double colW = (frame.XMax - frame.XMin) / cols;
            var xs = new List<double>(cols);
            var ys = new List<double>(cols);
            for (int c = 0; c < cols; c++)
            {
                double y = ov[c];
                if (!double.IsNaN(y)) { xs.Add(frame.XMin + (c + 0.5) * colW); ys.Add(y); }
            }
            if (ys.Count >= 2)
            {
                var line = plot.Add.Scatter(xs.ToArray(), ys.ToArray());
                line.LineWidth = 1f;
                line.MarkerSize = 0;
                line.Color = new ScottPlot.Color(235, 235, 235, 180);
            }
        }

        if (frame.RowLabels is { } labels && labels.Count == rows)
        {
            double rowH = (frame.YMax - frame.YMin) / rows;
            var ticks = new ScottPlot.Tick[rows];
            for (int r = 0; r < rows; r++)
                ticks[r] = new ScottPlot.Tick(frame.YMax - (r + 0.5) * rowH, labels[r]); // row 0 = top
            plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(ticks);
        }

        plot.Axes.SetLimits(frame.XMin, frame.XMax, frame.YMin, frame.YMax);
    }

    private static void RenderBubbles(ScottPlot.Plot plot, BubbleFrame frame)
    {
        plot.Axes.DateTimeTicksBottom();

        foreach (var b in frame.Bubbles)
        {
            var color = b.Side switch
            {
                BubbleSide.Buy => BuyColor,
                BubbleSide.Sell => SellColor,
                _ => NeutralBubble,
            };
            plot.Add.Marker(b.X, b.Price, ScottPlot.MarkerShape.FilledCircle, b.SizePx, color);
        }

        if (frame.Bubbles.Count > 0)
            plot.Axes.SetLimits(frame.XMin, frame.XMax, frame.YMin, frame.YMax);
    }
}
