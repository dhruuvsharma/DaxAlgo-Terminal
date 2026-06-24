using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.App.Avalonia.Charts;

/// <summary>
/// Custom-drawn order-book depth ladder (DOM) for Avalonia — the cross-platform port of the WPF
/// OrderBook window. Renders a price ladder centred on the spread: asks (red) in the upper rows,
/// bids (green) in the lower rows, each with a size histogram bar proportional to level size and a
/// centred price column. Bound to <see cref="Book"/>; redraws on assignment.
/// </summary>
public sealed class DepthLadderControl : Control
{
    private const double RowHeight = 18;
    private const double PriceColWidth = 78;

    private static readonly IBrush Bg = new SolidColorBrush(Color.Parse("#000000"));
    private static readonly IBrush Grid = new SolidColorBrush(Color.Parse("#1C1C1C"));
    private static readonly IBrush TextPrimary = new SolidColorBrush(Color.Parse("#E8E8E8"));
    private static readonly IBrush BidBar = new SolidColorBrush(Color.Parse("#2600C853"));
    private static readonly IBrush AskBar = new SolidColorBrush(Color.Parse("#26FF1744"));
    private static readonly IBrush Bid = new SolidColorBrush(Color.Parse("#00C853"));
    private static readonly IBrush Ask = new SolidColorBrush(Color.Parse("#FF1744"));
    private static readonly IBrush Spread = new SolidColorBrush(Color.Parse("#14FFB000"));
    private static readonly IPen GridPen = new Pen(Grid, 1);
    private static readonly Typeface Mono = new("Cascadia Mono, Consolas, monospace");

    public static readonly DirectProperty<DepthLadderControl, DepthSnapshot?> BookProperty =
        AvaloniaProperty.RegisterDirect<DepthLadderControl, DepthSnapshot?>(
            nameof(Book), o => o.Book, (o, v) => o.Book = v);

    private DepthSnapshot? _book;
    public DepthSnapshot? Book
    {
        get => _book;
        set { SetAndRaise(BookProperty, ref _book, value); InvalidateMeasure(); InvalidateVisual(); }
    }

    private int RowCount => _book is null ? 1 : _book.Bids.Count + _book.Asks.Count + 1;

    protected override Size MeasureOverride(Size a) => new(Math.Max(a.Width, 360), RowCount * RowHeight + 4);

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(Bg, new Rect(Bounds.Size));
        var book = _book;
        if (book is null || (book.Bids.Count == 0 && book.Asks.Count == 0))
        {
            DrawText(ctx, "Waiting for depth…", new Point(12, 12), TextPrimary, 12);
            return;
        }

        double w = Bounds.Width;
        double priceLeft = (w - PriceColWidth) / 2;
        double priceRight = priceLeft + PriceColWidth;
        long max = 1;
        foreach (var l in book.Bids) if (l.Size > max) max = l.Size;
        foreach (var l in book.Asks) if (l.Size > max) max = l.Size;

        // Rows: asks highest→best (top), then bids best→lowest.
        int row = 0;
        for (int i = book.Asks.Count - 1; i >= 0; i--) DrawAsk(ctx, book.Asks[i], row++, priceLeft, priceRight, w, max);

        // Spread separator
        double sy = row * RowHeight;
        ctx.FillRectangle(Spread, new Rect(0, sy, w, RowHeight));
        DrawText(ctx, $"spread {book.BestAsk - book.BestBid:0.00}", new Point(priceLeft + 4, sy + 1),
            new SolidColorBrush(Color.Parse("#FFB000")), 11);
        row++;

        foreach (var l in book.Bids) DrawBid(ctx, l, row++, priceLeft, priceRight, w, max);
    }

    private void DrawAsk(DrawingContext ctx, DepthLevel l, int row, double pl, double pr, double w, long max)
    {
        double y = row * RowHeight;
        double bar = (double)l.Size / max * (w - pr);
        ctx.FillRectangle(AskBar, new Rect(pr, y, bar, RowHeight - 1));
        ctx.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        DrawText(ctx, l.Price.ToString("0.00"), new Point(pl + 6, y + 1), TextPrimary, 11);
        DrawText(ctx, l.Size.ToString(), new Point(pr + 6, y + 1), Ask, 11);
    }

    private void DrawBid(DrawingContext ctx, DepthLevel l, int row, double pl, double pr, double w, long max)
    {
        double y = row * RowHeight;
        double bar = (double)l.Size / max * pl;
        ctx.FillRectangle(BidBar, new Rect(pl - bar, y, bar, RowHeight - 1));
        ctx.DrawLine(GridPen, new Point(0, y), new Point(w, y));
        DrawText(ctx, l.Price.ToString("0.00"), new Point(pl + 6, y + 1), TextPrimary, 11);
        DrawText(ctx, l.Size.ToString(), new Point(pl - 44, y + 1), Bid, 11);
    }

    private static void DrawText(DrawingContext ctx, string text, Point at, IBrush brush, double size)
    {
        var ft = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Mono, size, brush);
        ctx.DrawText(ft, at);
    }
}
