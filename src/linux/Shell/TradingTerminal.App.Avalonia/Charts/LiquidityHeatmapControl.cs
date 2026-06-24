using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Bookmap-style liquidity heatmap for Avalonia — a price × time grid where each cell's brightness
/// encodes resting depth size at that price/time. Columns are successive <see cref="DepthSnapshot"/>s
/// (oldest left → newest right); the best bid/ask trace is overlaid. Cross-platform port of the WPF
/// Heatmap window's core visual, reusing the broker-neutral Core depth type. Bound to <see cref="Columns"/>.
/// </summary>
public sealed class LiquidityHeatmapControl : Control
{
    private const double ColWidth = 8;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#000000"));
    private static readonly Color Cold = Color.Parse("#0A0A0A");
    private static readonly Color Hot = Color.Parse("#FFB000");   // amber liquidity heat
    private static readonly IBrush BidTrace = new SolidColorBrush(Color.Parse("#00C853"));
    private static readonly IBrush AskTrace = new SolidColorBrush(Color.Parse("#FF1744"));
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, monospace");
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#8A8A8A"));

    public static readonly DirectProperty<LiquidityHeatmapControl, IReadOnlyList<DepthSnapshot>> ColumnsProperty =
        AvaloniaProperty.RegisterDirect<LiquidityHeatmapControl, IReadOnlyList<DepthSnapshot>>(
            nameof(Columns), o => o.Columns, (o, v) => o.Columns = v);

    private IReadOnlyList<DepthSnapshot> _columns = Array.Empty<DepthSnapshot>();
    public IReadOnlyList<DepthSnapshot> Columns
    {
        get => _columns;
        set { SetAndRaise(ColumnsProperty, ref _columns, value); InvalidateVisual(); }
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        var cols = _columns;
        if (cols.Count == 0)
        {
            DrawText(ctx, "Waiting for depth…", new Point(12, 12), TextDim, 12);
            return;
        }

        // Global price range + max size across the window for stable mapping.
        double pMin = double.MaxValue, pMax = double.MinValue;
        long maxSize = 1;
        foreach (var s in cols)
        {
            foreach (var l in s.Bids) { pMin = Math.Min(pMin, l.Price); pMax = Math.Max(pMax, l.Price); if (l.Size > maxSize) maxSize = l.Size; }
            foreach (var l in s.Asks) { pMin = Math.Min(pMin, l.Price); pMax = Math.Max(pMax, l.Price); if (l.Size > maxSize) maxSize = l.Size; }
        }
        if (pMax <= pMin) { pMax = pMin + 1; }

        double h = Bounds.Height;
        double range = pMax - pMin;
        double cellH = Math.Max(2.0, h / (range / EstimateTick(cols) + 1));
        double Y(double price) => h - (price - pMin) / range * h;

        for (int c = 0; c < cols.Count; c++)
        {
            var s = cols[c];
            double x = c * ColWidth;
            foreach (var l in s.Bids) DrawCell(ctx, x, Y(l.Price), cellH, Heat(l.Size, maxSize));
            foreach (var l in s.Asks) DrawCell(ctx, x, Y(l.Price), cellH, Heat(l.Size, maxSize));
            // best bid/ask trace dots
            if (s.BestBid > 0) ctx.FillRectangle(BidTrace, new Rect(x, Y(s.BestBid) - 1, ColWidth, 2));
            if (s.BestAsk > 0) ctx.FillRectangle(AskTrace, new Rect(x, Y(s.BestAsk) - 1, ColWidth, 2));
        }

        // price axis labels (right edge)
        DrawText(ctx, pMax.ToString("0.00"), new Point(Bounds.Width - 56, 2), TextDim, 10);
        DrawText(ctx, pMin.ToString("0.00"), new Point(Bounds.Width - 56, h - 14), TextDim, 10);
    }

    private static double EstimateTick(IReadOnlyList<DepthSnapshot> cols)
    {
        foreach (var s in cols)
            if (s.Asks.Count >= 2) return Math.Max(0.0001, Math.Abs(s.Asks[1].Price - s.Asks[0].Price));
        return 0.25;
    }

    private static void DrawCell(DrawingContext ctx, double x, double y, double hgt, IBrush brush) =>
        ctx.FillRectangle(brush, new Rect(x, y - hgt / 2, ColWidth, hgt));

    private static IBrush Heat(long size, long max)
    {
        double t = Math.Clamp(Math.Sqrt((double)size / max), 0, 1); // sqrt for perceptual lift
        byte L(byte a, byte b) => (byte)(a + (b - a) * t);
        return new SolidColorBrush(Color.FromRgb(L(Cold.R, Hot.R), L(Cold.G, Hot.G), L(Cold.B, Hot.B)));
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, size, brush);
        ctx.DrawText(ft, at);
    }
}
