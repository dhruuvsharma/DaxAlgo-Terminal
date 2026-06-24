using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Custom-drawn Volume Footprint cluster grid for Avalonia — the cross-platform port of the WPF
/// Canvas renderer. Draws one column per <see cref="FootprintBar"/> and one row per price bucket,
/// with sell volume on the left, buy volume on the right, diagonal-imbalance highlights, and the
/// point-of-control row outlined. Bound to <see cref="Bars"/>; redraws on assignment.
/// </summary>
public sealed class FootprintControl : Control
{
    private const double LeftAxis = 64;
    private const double ColumnWidth = 96;
    private const double RowHeight = 16;
    private const double HeaderHeight = 26;

    // Bloomberg palette (kept in sync with Themes/Palette.axaml).
    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#000000"));
    private static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#1C1C1C"));
    private static readonly IBrush TextDim = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush Buy = new SolidColorBrush(Color.Parse("#00C853"));
    private static readonly IBrush Sell = new SolidColorBrush(Color.Parse("#FF1744"));
    private static readonly IBrush BuySoft = new SolidColorBrush(Color.Parse("#2600C853"));
    private static readonly IBrush SellSoft = new SolidColorBrush(Color.Parse("#26FF1744"));
    private static readonly IPen PocPen = new Pen(new SolidColorBrush(Color.Parse("#FFB000")), 1.4);
    private static readonly IPen GridPen = new Pen(Grid, 1);
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, monospace");

    public static readonly DirectProperty<FootprintControl, IReadOnlyList<FootprintBar>> BarsProperty =
        AvaloniaProperty.RegisterDirect<FootprintControl, IReadOnlyList<FootprintBar>>(
            nameof(Bars), o => o.Bars, (o, v) => o.Bars = v);

    private IReadOnlyList<FootprintBar> _bars = Array.Empty<FootprintBar>();
    public IReadOnlyList<FootprintBar> Bars
    {
        get => _bars;
        set { SetAndRaise(BarsProperty, ref _bars, value); InvalidateMeasure(); InvalidateVisual(); }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int rowCount = 0;
        var seen = new HashSet<double>();
        foreach (var bar in _bars)
            foreach (var r in bar.Rows)
                if (seen.Add(r.Price)) rowCount++;
        double w = LeftAxis + Math.Max(1, _bars.Count) * ColumnWidth;
        double h = HeaderHeight + Math.Max(1, rowCount) * RowHeight + 4;
        return new Size(w, h);
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        var bars = _bars;
        if (bars.Count == 0)
        {
            DrawText(ctx, "Waiting for trades…", new Point(12, 12), TextDim, 12);
            return;
        }

        // Union of price levels across visible bars, high → low; pick a tick from the densest bar.
        var prices = new SortedSet<double>(Comparer<double>.Create((a, b) => b.CompareTo(a)));
        long maxCell = 1;
        foreach (var bar in bars)
            foreach (var r in bar.Rows)
            {
                prices.Add(r.Price);
                if (r.TotalVolume > maxCell) maxCell = r.TotalVolume;
            }
        var rows = prices.ToList();
        var rowIndex = new Dictionary<double, int>();
        for (int i = 0; i < rows.Count; i++) rowIndex[rows[i]] = i;

        // Price axis
        for (int i = 0; i < rows.Count; i++)
        {
            double y = HeaderHeight + i * RowHeight;
            DrawText(ctx, rows[i].ToString("0.00"), new Point(4, y + 1), TextDim, 11);
            ctx.DrawLine(GridPen, new Point(LeftAxis, y), new Point(Bounds.Width, y));
        }

        for (int c = 0; c < bars.Count; c++)
        {
            var bar = bars[c];
            double x = LeftAxis + c * ColumnWidth;
            double half = ColumnWidth / 2.0;

            // Column header: time + delta
            var deltaBrush = bar.Delta >= 0 ? Buy : Sell;
            DrawText(ctx, bar.StartUtc.ToString("HH:mm:ss"), new Point(x + 4, 2), TextDim, 10);
            DrawText(ctx, (bar.Delta >= 0 ? "+" : "") + bar.Delta, new Point(x + 4, 13), deltaBrush, 11);
            ctx.DrawLine(GridPen, new Point(x, 0), new Point(x, Bounds.Height));

            foreach (var r in bar.Rows)
            {
                if (!rowIndex.TryGetValue(r.Price, out int ri)) continue;
                double y = HeaderHeight + ri * RowHeight;

                // Heat fill proportional to volume; imbalance brightens the dominant side.
                if (r.SellVolume > 0)
                    ctx.FillRectangle(r.BidImbalance ? SellSoft : Grid,
                        new Rect(x, y, half, RowHeight - 1));
                if (r.BuyVolume > 0)
                    ctx.FillRectangle(r.AskImbalance ? BuySoft : Grid,
                        new Rect(x + half, y, half, RowHeight - 1));

                DrawText(ctx, r.SellVolume.ToString(), new Point(x + 4, y), Sell, 10);
                DrawText(ctx, r.BuyVolume.ToString(), new Point(x + half + 4, y), Buy, 10);
            }

            // Point-of-control outline
            if (rowIndex.TryGetValue(bar.PocPrice, out int pocRi))
                ctx.DrawRectangle(null, PocPen,
                    new Rect(x, HeaderHeight + pocRi * RowHeight, ColumnWidth, RowHeight - 1));
        }
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, size, brush);
        ctx.DrawText(ft, at);
    }
}
