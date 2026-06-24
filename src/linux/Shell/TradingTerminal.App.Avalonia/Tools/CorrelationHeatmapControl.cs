using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TradingTerminal.Core.Analytics;

namespace TradingTerminal.App.Avalonia.Tools;

/// <summary>
/// Custom-drawn correlation-matrix heatmap for Avalonia — an N×N grid coloured by Pearson
/// correlation (green ⇒ +1, red ⇒ −1, black ⇒ 0) with row/column labels and per-cell values.
/// Bound to a Core <see cref="CorrelationMatrix"/>; redraws on assignment.
/// </summary>
public sealed class CorrelationHeatmapControl : Control
{
    private const double LabelW = 86;
    private const double HeaderH = 24;
    private const double CellW = 60;
    private const double CellH = 26;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#000000"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush TextHdr = new SolidColorBrush(Color.Parse("#FFB000"));
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#2A2A2A")), 1);
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, monospace");

    public static readonly DirectProperty<CorrelationHeatmapControl, CorrelationMatrix?> MatrixProperty =
        AvaloniaProperty.RegisterDirect<CorrelationHeatmapControl, CorrelationMatrix?>(
            nameof(Matrix), o => o.Matrix, (o, v) => o.Matrix = v);

    private CorrelationMatrix? _matrix;
    public CorrelationMatrix? Matrix
    {
        get => _matrix;
        set { SetAndRaise(MatrixProperty, ref _matrix, value); InvalidateMeasure(); InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size a)
    {
        int n = _matrix?.Size ?? 1;
        return new Size(LabelW + n * CellW + 8, HeaderH + n * CellH + 8);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        var m = _matrix;
        if (m is null || m.Size == 0) { DrawText(ctx, "Computing correlations…", new Point(12, 12), TextDim, 12); return; }

        int n = m.Size;
        for (int j = 0; j < n; j++)
            DrawText(ctx, Trim(m.Labels[j]), new Point(LabelW + j * CellW + 4, 4), TextHdr, 10);

        for (int i = 0; i < n; i++)
        {
            double y = HeaderH + i * CellH;
            DrawText(ctx, Trim(m.Labels[i]), new Point(4, y + 6), TextHdr, 10);
            for (int j = 0; j < n; j++)
            {
                double x = LabelW + j * CellW;
                double v = m.Values[i, j];
                ctx.FillRectangle(Heat(v), new Rect(x, y, CellW, CellH));
                ctx.DrawRectangle(null, GridPen, new Rect(x, y, CellW, CellH));
                DrawText(ctx, v.ToString("0.00"), new Point(x + 10, y + 6),
                    Math.Abs(v) > 0.55 ? Bg : new SolidColorBrush(Color.Parse("#E8E8E8")), 11);
            }
        }
    }

    private static string Trim(string s) => s.Length <= 10 ? s : s[..10];

    private static IBrush Heat(double v)
    {
        v = Math.Clamp(v, -1, 1);
        // 0 → near-black; +1 → green #00C853; −1 → red #FF1744.
        if (v >= 0)
            return new SolidColorBrush(Color.FromRgb((byte)(0x14 * (1 - v)), (byte)(0x14 + (0xC8 - 0x14) * v), (byte)(0x14 + (0x53 - 0x14) * v)));
        double a = -v;
        return new SolidColorBrush(Color.FromRgb((byte)(0x14 + (0xFF - 0x14) * a), (byte)(0x14 * (1 - a)), (byte)(0x14 + (0x44 - 0x14) * a)));
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, size, brush);
        ctx.DrawText(ft, at);
    }
}
