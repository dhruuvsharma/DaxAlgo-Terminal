using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.Controls;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.OrderBook;

/// <summary>
/// Hosts the standalone Order Book window. Pure view: the <see cref="OrderBookViewModel"/> owns the
/// depth/trade subscriptions and all analytics; this code-behind only renders the liquidity
/// <b>heatmap</b> onto a <see cref="Canvas"/> off the VM's <see cref="OrderBookViewModel.BookChanged"/>
/// event — the same render-in-code-behind convention the Volume Footprint / Order Flow Cube windows
/// use. The ladder + microstructure strip are plain data-bound XAML. No business logic lives here.
///
/// <para>Heatmap layout: a left price-axis gutter, the main time×price liquidity field (resting size →
/// cell intensity, teal bid / red ask), best-bid/ask + microprice lines and aggressor-coloured trade
/// dots over the top, and a bottom imbalance lane (per-column cumulative imbalance bars + the VM's OLS
/// trend line).</para>
/// </summary>
public partial class OrderBookWindow : MetroWindow
{
    private const double AxisWidth = 56;
    private const double LaneHeight = 64;   // bottom imbalance lane (0 when the toggle is off)
    private const double ColumnWidth = 4;   // px per captured time column
    private const int MaxLevelsPerSide = 30; // cap drawn depth so a deep book stays performant

    // Fixed palette (canvas drawing is theme-independent, like the footprint window).
    private static readonly Color BidColor = Color.FromRgb(0x26, 0xA6, 0x9A);
    private static readonly Color AskColor = Color.FromRgb(0xEF, 0x53, 0x50);
    private static readonly Brush BidLine = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0xA6, 0x9A)));
    private static readonly Brush AskLine = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x53, 0x50)));
    private static readonly Brush MicroLine = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xCA, 0x28)));
    private static readonly Brush BuyDot = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0x00, 0xE6, 0x76)));
    private static readonly Brush SellDot = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0x52, 0x52)));
    private static readonly Brush LaneUp = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x4C, 0xAF, 0x50)));
    private static readonly Brush LaneDown = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0xE5, 0x73, 0x73)));
    private static readonly Brush TrendLine = Freeze(new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)));
    private static readonly Brush DimText = Freeze(new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E)));
    private static readonly Brush GridPen = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)));
    private static readonly FontFamily MonoFont = new("Consolas");

    // Pre-baked, frozen liquidity-cell brushes bucketed by intensity. The heatmap redraws every frame
    // and paints thousands of cells, so allocating a SolidColorBrush per cell (the old path) churned
    // the GC hard. We quantise the size→alpha ramp into a fixed palette and reuse the frozen brushes.
    private const int CellAlphaBuckets = 48;
    private static readonly Brush[] BidCells = BuildCellBrushes(BidColor);
    private static readonly Brush[] AskCells = BuildCellBrushes(AskColor);

    private static Brush[] BuildCellBrushes(Color baseColor)
    {
        var brushes = new Brush[CellAlphaBuckets];
        for (var i = 0; i < CellAlphaBuckets; i++)
        {
            var frac = (double)i / (CellAlphaBuckets - 1);
            var alpha = (byte)(30 + 200.0 * frac);
            brushes[i] = Freeze(new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B)));
        }
        return brushes;
    }

    private OrderBookViewModel? _vm;
    private double _w, _h; // host canvas size (from the border's SizeChanged)

    public OrderBookWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.BookChanged -= OnBookChanged;
        _vm = e.NewValue as OrderBookViewModel;
        if (_vm is not null)
        {
            _vm.BookChanged += OnBookChanged;
            Redraw();
        }
    }

    private void HeatHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _w = e.NewSize.Width;
        _h = e.NewSize.Height;
        HeatCanvas.Width = _w;
        HeatCanvas.Height = _h;
        Redraw();
    }

    private void OnBookChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        HeatCanvas.Children.Clear();
        if (_vm is null || _w <= AxisWidth + 8 || _h <= 40) return;
        if (!_vm.ShowHeatmap)
        {
            AddText("Heatmap off", AxisWidth + 8, 8, 200, 18, DimText, 11);
            return;
        }

        var columns = _vm.HeatColumns;
        if (columns.Count == 0)
        {
            AddText("Waiting for depth…", AxisWidth + 8, 8, 240, 18, DimText, 11);
            return;
        }

        var laneH = _vm.ShowImbalanceLane ? LaneHeight : 0;
        var plotW = _w - AxisWidth;
        var plotH = _h - laneH;
        if (plotH <= 20) return;

        // Right-aligned scroll: draw the most recent columns that fit.
        var visible = Math.Min(columns.Count, Math.Max(1, (int)(plotW / ColumnWidth)));
        var start = columns.Count - visible;

        // Price range + max size over the visible window (for the y-map and alpha scaling).
        double pMin = double.MaxValue, pMax = double.MinValue;
        long maxSize = 1;
        for (var i = start; i < columns.Count; i++)
        {
            var c = columns[i];
            ScanSide(c.Bids, ref pMin, ref pMax, ref maxSize);
            ScanSide(c.Asks, ref pMin, ref pMax, ref maxSize);
        }
        if (pMax <= pMin) return;

        var range = pMax - pMin;
        var tick = InferTick(columns[^1]);
        var decimals = DecimalsFor(tick);
        var cellH = Math.Max(2.0, tick / range * plotH);

        double Y(double price) => Math.Clamp((pMax - price) / range, 0, 1) * plotH;
        double X(int colIdx) => AxisWidth + (colIdx - start) * ColumnWidth;

        DrawPriceAxis(pMin, pMax, plotH, decimals);

        // ── Liquidity field ────────────────────────────────────────────────────────────────────
        for (var i = start; i < columns.Count; i++)
        {
            var c = columns[i];
            var x = X(i);
            DrawSideCells(c.Bids, x, Y, cellH, maxSize, BidCells);
            DrawSideCells(c.Asks, x, Y, cellH, maxSize, AskCells);
        }

        // ── Best bid / ask + microprice lines ───────────────────────────────────────────────────
        DrawLine(columns, start, X, Y, c => c.BestBid, BidLine, 1.0, dashed: false);
        DrawLine(columns, start, X, Y, c => c.BestAsk, AskLine, 1.0, dashed: false);
        if (_vm.ShowMicropriceLine)
            DrawLine(columns, start, X, Y, c => c.Microprice, MicroLine, 1.2, dashed: true);

        // ── Trade dots ────────────────────────────────────────────────────────────────────────
        if (_vm.ShowTrades)
            DrawTrades(columns, start, X, Y, maxSize);

        // ── Bottom imbalance lane ───────────────────────────────────────────────────────────────
        if (laneH > 0) DrawImbalanceLane(columns, start, X, plotH, laneH);
    }

    private static void ScanSide(IReadOnlyList<DepthLevel> side, ref double pMin, ref double pMax, ref long maxSize)
    {
        var n = Math.Min(MaxLevelsPerSide, side.Count);
        for (var i = 0; i < n; i++)
        {
            var l = side[i];
            if (l.Price < pMin) pMin = l.Price;
            if (l.Price > pMax) pMax = l.Price;
            if (l.Size > maxSize) maxSize = l.Size;
        }
    }

    private void DrawSideCells(IReadOnlyList<DepthLevel> side, double x, Func<double, double> y,
        double cellH, long maxSize, Brush[] cellBrushes)
    {
        var n = Math.Min(MaxLevelsPerSide, side.Count);
        for (var i = 0; i < n; i++)
        {
            var l = side[i];
            if (l.Size <= 0) continue;
            var bucket = (int)(Math.Min(1.0, (double)l.Size / maxSize) * (CellAlphaBuckets - 1));
            var rect = new Rectangle { Width = ColumnWidth, Height = cellH, Fill = cellBrushes[bucket] };
            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y(l.Price) - cellH / 2.0);
            HeatCanvas.Children.Add(rect);
        }
    }

    private void DrawLine(IReadOnlyList<HeatColumn> columns, int start, Func<int, double> x,
        Func<double, double> y, Func<HeatColumn, double> selector, Brush brush, double thickness, bool dashed)
    {
        var pts = new PointCollection();
        for (var i = start; i < columns.Count; i++)
        {
            var v = selector(columns[i]);
            if (!double.IsFinite(v) || v <= 0) continue;
            pts.Add(new Point(x(i) + ColumnWidth / 2.0, y(v)));
        }
        if (pts.Count < 2) return;
        var line = new Polyline { Points = pts, Stroke = brush, StrokeThickness = thickness };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 3, 3 };
        HeatCanvas.Children.Add(line);
    }

    private void DrawTrades(IReadOnlyList<HeatColumn> columns, int start, Func<int, double> x,
        Func<double, double> y, long maxSize)
    {
        for (var i = start; i < columns.Count; i++)
        {
            var trades = columns[i].Trades;
            if (trades is null) continue;
            var cx = x(i) + ColumnWidth / 2.0;
            foreach (var t in trades)
            {
                var r = Math.Clamp(2.0 + 4.0 * Math.Sqrt(Math.Min(1.0, (double)t.Size / maxSize)), 2.0, 6.0);
                var dot = new Ellipse
                {
                    Width = r * 2, Height = r * 2,
                    Fill = t.Side == AggressorSide.Sell ? SellDot : BuyDot,
                };
                Canvas.SetLeft(dot, cx - r);
                Canvas.SetTop(dot, y(t.Price) - r);
                HeatCanvas.Children.Add(dot);
            }
        }
    }

    private void DrawImbalanceLane(IReadOnlyList<HeatColumn> columns, int start,
        Func<int, double> x, double plotH, double laneH)
    {
        var zeroY = plotH + laneH / 2.0;
        var half = laneH / 2.0 - 2;

        // Zero baseline + caption.
        HeatCanvas.Children.Add(new Line { X1 = AxisWidth, Y1 = zeroY, X2 = _w, Y2 = zeroY, Stroke = GridPen, StrokeThickness = 1 });
        AddText("IMBALANCE", AxisWidth + 4, plotH + 2, 90, 12, DimText, 9);

        for (var i = start; i < columns.Count; i++)
        {
            var imb = Math.Clamp(columns[i].Imbalance, -1, 1);
            if (Math.Abs(imb) < 1e-4) continue;
            var h = Math.Abs(imb) * half;
            var rect = new Rectangle { Width = ColumnWidth, Height = h, Fill = imb >= 0 ? LaneUp : LaneDown };
            Canvas.SetLeft(rect, x(i));
            Canvas.SetTop(rect, imb >= 0 ? zeroY - h : zeroY);
            HeatCanvas.Children.Add(rect);
        }

        // OLS trend line from the VM (intercept + slope·globalIndex), mapped into the lane.
        if (_vm is { HasImbalanceTrend: true })
        {
            double LaneY(double v) => zeroY - Math.Clamp(v, -1, 1) * half;
            var p1 = new Point(x(start) + ColumnWidth / 2.0, LaneY(_vm.ImbalanceIntercept + _vm.ImbalanceSlope * start));
            var last = columns.Count - 1;
            var p2 = new Point(x(last) + ColumnWidth / 2.0, LaneY(_vm.ImbalanceIntercept + _vm.ImbalanceSlope * last));
            HeatCanvas.Children.Add(new Line
            {
                X1 = p1.X, Y1 = p1.Y, X2 = p2.X, Y2 = p2.Y,
                Stroke = TrendLine, StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 5, 3 },
            });
        }
    }

    private void DrawPriceAxis(double pMin, double pMax, double plotH, int decimals)
    {
        const int ticks = 6;
        for (var i = 0; i <= ticks; i++)
        {
            var frac = (double)i / ticks;
            var price = pMax - frac * (pMax - pMin);
            var y = frac * plotH;
            HeatCanvas.Children.Add(new Line { X1 = AxisWidth - 4, Y1 = y, X2 = _w, Y2 = y, Stroke = GridPen, StrokeThickness = 0.5 });
            AddText(price.ToString("N" + decimals, CultureInfo.InvariantCulture), 2, y - 7, AxisWidth - 6, 14, DimText, 9.5, TextAlignment.Right);
        }
    }

    private void AddText(string text, double x, double y, double w, double h, Brush brush, double size,
        TextAlignment align = TextAlignment.Left)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = brush, FontSize = size, FontFamily = MonoFont,
            Width = w, Height = h, TextAlignment = align,
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        HeatCanvas.Children.Add(tb);
    }

    private static double InferTick(HeatColumn column)
    {
        var min = double.MaxValue;
        MinGap(column.Bids, ref min);
        MinGap(column.Asks, ref min);
        return min is > 0 and < double.MaxValue ? min : 0.01;

        static void MinGap(IReadOnlyList<DepthLevel> side, ref double min)
        {
            var n = Math.Min(MaxLevelsPerSide, side.Count);
            for (var i = 1; i < n; i++)
            {
                var g = Math.Abs(side[i].Price - side[i - 1].Price);
                if (g > 1e-12 && g < min) min = g;
            }
        }
    }

    private static int DecimalsFor(double tick)
    {
        if (tick >= 1) return 0;
        var decimals = 0;
        while (tick < 1 && decimals < 8) { tick *= 10; decimals++; }
        return decimals;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.BookChanged -= OnBookChanged;
        _vm.Dispose();
    }
}
