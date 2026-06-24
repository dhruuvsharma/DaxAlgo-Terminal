using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Minimal reusable line/series chart for Avalonia — auto-scaled polyline of a numeric series in the
/// terminal theme (amber trace on black, dim zero/baseline grid). Used by the Machine Learning
/// windows (and any future series plot). Bound to <see cref="Series"/>; redraws on assignment.
/// </summary>
public sealed class LineChartControl : Control
{
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#000000"));
    private static readonly IPen Trace = new Pen(new SolidColorBrush(Color.Parse("#FFB000")), 1.3);
    private static readonly IPen Zero = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, monospace");
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public static readonly DirectProperty<LineChartControl, IReadOnlyList<double>> SeriesProperty =
        AvaloniaProperty.RegisterDirect<LineChartControl, IReadOnlyList<double>>(
            nameof(Series), o => o.Series, (o, v) => o.Series = v);

    private IReadOnlyList<double> _series = Array.Empty<double>();
    public IReadOnlyList<double> Series
    {
        get => _series;
        set { SetAndRaise(SeriesProperty, ref _series, value); InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        var s = _series;
        if (s.Count < 2)
        {
            DrawText(ctx, "Run to plot the series…", new Point(12, 12), TextDim, 12);
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        foreach (var v in s) { if (v < min) min = v; if (v > max) max = v; }
        if (max <= min) max = min + 1;
        double w = Bounds.Width, h = Bounds.Height;
        double range = max - min;
        double X(int i) => i / (double)(s.Count - 1) * w;
        double Y(double v) => h - (v - min) / range * (h - 8) - 4;

        // zero baseline (if the series straddles 0)
        if (min < 0 && max > 0) { double y0 = Y(0); ctx.DrawLine(Zero, new Point(0, y0), new Point(w, y0)); }

        var fig = new PathFigure { StartPoint = new Point(X(0), Y(s[0])), IsClosed = false };
        for (int i = 1; i < s.Count; i++)
            fig.Segments!.Add(new LineSegment { Point = new Point(X(i), Y(s[i])) });
        var geo = new PathGeometry();
        geo.Figures!.Add(fig);
        ctx.DrawGeometry(null, Trace, geo);

        DrawText(ctx, max.ToString("0.####"), new Point(4, 2), TextDim, 10);
        DrawText(ctx, min.ToString("0.####"), new Point(4, h - 14), TextDim, 10);
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, size, brush);
        ctx.DrawText(ft, at);
    }
}
