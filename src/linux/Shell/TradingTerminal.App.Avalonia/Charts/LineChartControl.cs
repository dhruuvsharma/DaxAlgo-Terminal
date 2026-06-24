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
    private static readonly IPen Trace2 = new Pen(new SolidColorBrush(Color.Parse("#00BFFF")), 1.3);
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

    public static readonly DirectProperty<LineChartControl, IReadOnlyList<double>> Series2Property =
        AvaloniaProperty.RegisterDirect<LineChartControl, IReadOnlyList<double>>(
            nameof(Series2), o => o.Series2, (o, v) => o.Series2 = v);

    private IReadOnlyList<double> _series2 = Array.Empty<double>();
    /// <summary>Optional overlay series (cyan), e.g. a filtered/forecast trace over the raw series.</summary>
    public IReadOnlyList<double> Series2
    {
        get => _series2;
        set { SetAndRaise(Series2Property, ref _series2, value); InvalidateVisual(); }
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
        void Span(IReadOnlyList<double> a) { foreach (var v in a) { if (v < min) min = v; if (v > max) max = v; } }
        Span(s); Span(_series2);
        if (max <= min) max = min + 1;
        double w = Bounds.Width, h = Bounds.Height;
        int n = Math.Max(s.Count, _series2.Count);
        double range = max - min;
        double X(int i) => i / (double)(n - 1) * w;
        double Y(double v) => h - (v - min) / range * (h - 8) - 4;

        // zero baseline (if the data straddles 0)
        if (min < 0 && max > 0) { double y0 = Y(0); ctx.DrawLine(Zero, new Point(0, y0), new Point(w, y0)); }

        void Draw(IReadOnlyList<double> a, IPen pen)
        {
            if (a.Count < 2) return;
            var fig = new PathFigure { StartPoint = new Point(X(0), Y(a[0])), IsClosed = false };
            for (int i = 1; i < a.Count; i++) fig.Segments!.Add(new LineSegment { Point = new Point(X(i), Y(a[i])) });
            var geo = new PathGeometry();
            geo.Figures!.Add(fig);
            ctx.DrawGeometry(null, pen, geo);
        }
        Draw(s, Trace);
        Draw(_series2, Trace2);

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
