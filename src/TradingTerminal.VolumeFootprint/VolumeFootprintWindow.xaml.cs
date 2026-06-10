using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.Controls;

namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// Hosts the Volume Footprint cluster grid. Pure presentation: it draws the bid/ask footprint onto a
/// <see cref="Canvas"/> in response to <see cref="VolumeFootprintViewModel.FootprintChanged"/>, the
/// same render-in-code-behind convention the Order Flow Cube window uses. The VM owns all data and the
/// trade subscription; no business logic lives here (MVVM rule).
/// </summary>
public partial class VolumeFootprintWindow : MetroWindow
{
    private const double LeftAxisWidth = 66;
    private const double ColumnWidth = 104;
    private const double RowHeight = 17;
    private const double HeaderHeight = 22;
    private const double FooterHeight = 40;

    // Fixed palette (canvas drawing is theme-independent).
    private static readonly Brush BuyBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly Brush SellBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly Brush PocPen = new SolidColorBrush(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly Brush GridPen = new SolidColorBrush(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly Brush DimText = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly Brush UpBrush = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush DownBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x73, 0x73));
    private static readonly Brush PocLineBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA7, 0x26)); // total POC connector
    private static readonly Brush RegLineBrush = new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)); // total POC regression
    private static readonly Brush BuyPocBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xE6, 0x76));  // buy POC line + regression
    private static readonly Brush SellPocBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x52, 0x52)); // sell POC line + regression

    static VolumeFootprintWindow()
    {
        foreach (var b in new[] { BuyBrush, SellBrush, PocPen, GridPen, TextBrush, DimText, UpBrush, DownBrush, PocLineBrush, RegLineBrush, BuyPocBrush, SellPocBrush })
            b.Freeze();
    }

    private VolumeFootprintViewModel? _vm;

    public VolumeFootprintWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.FootprintChanged -= OnFootprintChanged;
        _vm = e.NewValue as VolumeFootprintViewModel;
        if (_vm is not null)
        {
            _vm.FootprintChanged += OnFootprintChanged;
            Redraw();
        }
    }

    private void OnFootprintChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        FootprintCanvas.Children.Clear();
        if (_vm is null) return;

        var bars = _vm.Bars.ToList();
        if (bars.Count == 0)
        {
            FootprintCanvas.Width = 0;
            FootprintCanvas.Height = 0;
            return;
        }

        // Shared price axis: union of every traded bucket across visible bars, high → low.
        var prices = new SortedSet<double>();
        long maxCellVol = 1;
        foreach (var bar in bars)
            foreach (var cell in bar.Cells)
            {
                prices.Add(cell.Price);
                if (cell.Total > maxCellVol) maxCellVol = cell.Total;
            }
        var rows = prices.Reverse().ToList(); // descending price (top = highest)
        var rowIndex = new Dictionary<double, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++) rowIndex[rows[i]] = i;

        var decimals = DecimalsFor(ParseTick(_vm.TickSizeText));

        FootprintCanvas.Width = LeftAxisWidth + bars.Count * ColumnWidth;
        FootprintCanvas.Height = HeaderHeight + rows.Count * RowHeight + FooterHeight;

        DrawPriceAxis(rows, decimals);

        for (var b = 0; b < bars.Count; b++)
            DrawBar(bars[b], b, rowIndex, maxCellVol, decimals);

        // POC connector + regression line drawn last so they sit on top of the cells.
        DrawPocOverlay(bars, rows, rowIndex);
    }

    /// <summary>Draws the connector + regression lines for the total / buy / sell points-of-control.
    /// All ride above the cluster cells (added last). Regression slopes/intercepts come from the VM.</summary>
    private void DrawPocOverlay(List<FootprintBar> bars, List<double> rows, IReadOnlyDictionary<double, int> rowIndex)
    {
        if (_vm is null) return;
        DrawPocSeries(bars, rows, rowIndex, b => b.PointOfControl,
            _vm.HasRegression, _vm.PocSlope, _vm.PocIntercept, PocLineBrush, RegLineBrush, 2.5);
        DrawPocSeries(bars, rows, rowIndex, b => b.BuyPointOfControl,
            _vm.HasBuyRegression, _vm.BuyPocSlope, _vm.BuyPocIntercept, BuyPocBrush, BuyPocBrush, 2.0);
        DrawPocSeries(bars, rows, rowIndex, b => b.SellPointOfControl,
            _vm.HasSellRegression, _vm.SellPocSlope, _vm.SellPocIntercept, SellPocBrush, SellPocBrush, 2.0);
    }

    /// <summary>Draws one POC flavour: a polyline through the centre of each bar's selected POC cell
    /// (dots per vertex), plus its dashed least-squares regression line from the first to last column.</summary>
    private void DrawPocSeries(List<FootprintBar> bars, List<double> rows, IReadOnlyDictionary<double, int> rowIndex,
        Func<FootprintBar, double> pocSelector, bool hasRegression, double slope, double intercept,
        Brush connectorBrush, Brush regressionBrush, double dotRadius)
    {
        var pts = new PointCollection();
        var dots = new List<Point>();
        for (var b = 0; b < bars.Count; b++)
        {
            var poc = pocSelector(bars[b]);
            if (double.IsNaN(poc) || !rowIndex.TryGetValue(poc, out var r)) continue;
            var p = new Point(
                LeftAxisWidth + b * ColumnWidth + ColumnWidth / 2.0,
                HeaderHeight + r * RowHeight + RowHeight / 2.0);
            pts.Add(p);
            dots.Add(p);
        }

        if (pts.Count >= 2)
            FootprintCanvas.Children.Add(new Polyline
            {
                Points = pts,
                Stroke = connectorBrush,
                StrokeThickness = 1.8,
                StrokeLineJoin = PenLineJoin.Round,
            });

        var d2 = dotRadius * 2;
        foreach (var d in dots)
            FootprintCanvas.Children.Add(Place(
                new Ellipse { Width = d2, Height = d2, Fill = connectorBrush }, d.X - dotRadius, d.Y - dotRadius));

        // Regression line: from column 0 to the last column at the fitted POC price.
        if (hasRegression && bars.Count >= 2 && rows.Count > 0)
        {
            var lastCol = bars.Count - 1;
            FootprintCanvas.Children.Add(new Line
            {
                X1 = LeftAxisWidth + ColumnWidth / 2.0,
                Y1 = PriceToY(intercept, rows),
                X2 = LeftAxisWidth + lastCol * ColumnWidth + ColumnWidth / 2.0,
                Y2 = PriceToY(intercept + slope * lastCol, rows),
                Stroke = regressionBrush,
                StrokeThickness = 1.6,
                StrokeDashArray = new DoubleCollection { 5, 3 },
            });
        }
    }

    /// <summary>Maps a continuous price to a canvas Y by interpolating between the discrete price rows
    /// (which may be non-uniformly spaced where price gaps exist). Clamps outside the traded range.</summary>
    private static double PriceToY(double price, List<double> rows)
    {
        if (price >= rows[0]) return HeaderHeight + RowHeight / 2.0;
        if (price <= rows[^1]) return HeaderHeight + (rows.Count - 1) * RowHeight + RowHeight / 2.0;
        for (var r = 0; r < rows.Count - 1; r++)
        {
            if (rows[r] >= price && price >= rows[r + 1])
            {
                var frac = (rows[r] - price) / (rows[r] - rows[r + 1]);
                return HeaderHeight + (r + frac) * RowHeight + RowHeight / 2.0;
            }
        }
        return HeaderHeight + RowHeight / 2.0;
    }

    private void DrawPriceAxis(List<double> rows, int decimals)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            var y = HeaderHeight + r * RowHeight;
            AddText(rows[r].ToString("N" + decimals, CultureInfo.InvariantCulture),
                0, y, LeftAxisWidth - 6, RowHeight, DimText, 10.5, TextAlignment.Right);
        }
    }

    private void DrawBar(FootprintBar bar, int colIndex, IReadOnlyDictionary<double, int> rowIndex,
        long maxCellVol, int decimals)
    {
        var x = LeftAxisWidth + colIndex * ColumnWidth;

        // Column header: bar start time.
        AddText(bar.StartUtc.ToLocalTime().ToString("HH:mm:ss"), x, 0, ColumnWidth, HeaderHeight,
            DimText, 10.5, TextAlignment.Center);

        // Cells.
        var halfW = (ColumnWidth - 2) / 2.0;
        foreach (var cell in bar.Cells)
        {
            if (!rowIndex.TryGetValue(cell.Price, out var r)) continue;
            var y = HeaderHeight + r * RowHeight;

            // Sell volume on the left (red), buy volume on the right (green). Background alpha scales
            // with volume so heavy levels pop. POC level gets a yellow outline.
            AddCellHalf(x + 1, y, halfW, cell.SellVolume, maxCellVol, SellBrush, isLeft: true);
            AddCellHalf(x + 1 + halfW, y, halfW, cell.BuyVolume, maxCellVol, BuyBrush, isLeft: false);

            var isPoc = !double.IsNaN(bar.PointOfControl) && AreClose(cell.Price, bar.PointOfControl);
            if (isPoc)
                FootprintCanvas.Children.Add(Place(new Rectangle
                {
                    Width = ColumnWidth - 2,
                    Height = RowHeight,
                    Stroke = PocPen,
                    StrokeThickness = 1.4,
                    Fill = Brushes.Transparent,
                }, x + 1, y));
        }

        // Footer: bar delta, total volume, running CVD.
        var fy = HeaderHeight + (FootprintCanvasRows()) * RowHeight;
        FootprintCanvas.Children.Add(Place(new Line
        {
            X1 = x, Y1 = fy + 2, X2 = x + ColumnWidth, Y2 = fy + 2,
            Stroke = GridPen, StrokeThickness = 1,
        }, 0, 0));

        var deltaBrush = bar.Delta >= 0 ? UpBrush : DownBrush;
        AddText($"Δ {bar.Delta:+#;-#;0}", x, fy + 3, ColumnWidth, 16, deltaBrush, 11, TextAlignment.Center);
        AddText($"Σ {bar.TotalVolume:N0}", x, fy + 19, ColumnWidth, 14, DimText, 10, TextAlignment.Center);
    }

    private int FootprintCanvasRows() =>
        (int)Math.Round((FootprintCanvas.Height - HeaderHeight - FooterHeight) / RowHeight);

    private void AddCellHalf(double x, double y, double w, long vol, long maxVol, Brush baseBrush, bool isLeft)
    {
        if (vol > 0)
        {
            var alpha = (byte)(40 + 180.0 * Math.Min(1.0, (double)vol / maxVol));
            var c = ((SolidColorBrush)baseBrush).Color;
            var fill = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
            fill.Freeze();
            FootprintCanvas.Children.Add(Place(new Rectangle { Width = w, Height = RowHeight, Fill = fill }, x, y));
            AddText(vol.ToString("N0", CultureInfo.InvariantCulture), x, y, w - 3, RowHeight,
                TextBrush, 10, isLeft ? TextAlignment.Right : TextAlignment.Left, leftPad: isLeft ? 0 : 3);
        }
    }

    private void AddText(string text, double x, double y, double w, double h, Brush brush, double size,
        TextAlignment align, double leftPad = 0)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontFamily = new FontFamily("Consolas"),
            Width = w,
            Height = h,
            TextAlignment = align,
            Padding = new Thickness(leftPad, 0, 3, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        // Vertically centre the (single-line) text within the row.
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y + (h - size - 4) / 2.0);
        FootprintCanvas.Children.Add(tb);
    }

    private static UIElement Place(UIElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        return el;
    }

    private static bool AreClose(double a, double b) => Math.Abs(a - b) < 1e-9;

    private static double ParseTick(string s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var t) && t > 0 ? t : 0.25;

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
        _vm.FootprintChanged -= OnFootprintChanged;
        _vm.Dispose();
    }
}
