using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.Controls;
using TradingTerminal.Core.Quant;

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
    private static readonly Brush PredictShade = new SolidColorBrush(Color.FromArgb(0x12, 0x29, 0xB6, 0xF6)); // forecast region wash
    private static readonly Brush GhostUpFill = new SolidColorBrush(Color.FromArgb(0x38, 0x2E, 0x7D, 0x32));
    private static readonly Brush GhostDownFill = new SolidColorBrush(Color.FromArgb(0x38, 0xC6, 0x28, 0x28));

    static VolumeFootprintWindow()
    {
        foreach (var b in new[] { BuyBrush, SellBrush, PocPen, GridPen, TextBrush, DimText, UpBrush, DownBrush, PocLineBrush, RegLineBrush, BuyPocBrush, SellPocBrush, PredictShade, GhostUpFill, GhostDownFill })
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
                // Core's FootprintFeatureRow.TotalVolume replaces the old FootprintCell.Total.
                if (cell.TotalVolume > maxCellVol) maxCellVol = cell.TotalVolume;
            }
        // Predicted (virtual) columns extend the price axis: snap each consensus price to the tick
        // grid so the ghost candles get real rows even above/below the traded range.
        var predicted = _vm.Predicted;
        var tick = ParseTick(_vm.TickSizeText);
        foreach (var p in predicted)
            foreach (var v in new[] { p.Poc, p.BuyPoc, p.SellPoc })
                if (double.IsFinite(v))
                    prices.Add(Math.Round(Math.Round(v / tick) * tick, 10));

        var rows = prices.Reverse().ToList(); // descending price (top = highest)
        var rowIndex = new Dictionary<double, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++) rowIndex[rows[i]] = i;

        var decimals = DecimalsFor(tick);

        FootprintCanvas.Width = LeftAxisWidth + (bars.Count + predicted.Count) * ColumnWidth;
        FootprintCanvas.Height = HeaderHeight + rows.Count * RowHeight + FooterHeight;

        DrawPriceAxis(rows, decimals);

        for (var b = 0; b < bars.Count; b++)
            DrawBar(bars[b], b, rowIndex, maxCellVol, decimals);

        // POC connectors, fit curves and predicted columns drawn last so they sit on top of the cells.
        DrawPocOverlay(bars, rows, rowIndex, decimals);
    }

    /// <summary>Draws the connector lines for the total / buy / sell points-of-control plus every
    /// enabled regression-fit curve. All ride above the cluster cells (added last). Fitted curves
    /// come pre-sampled (one price per column) from <see cref="VolumeFootprintViewModel.FitCurves"/>.</summary>
    private void DrawPocOverlay(List<RenderBar> bars, List<double> rows, IReadOnlyDictionary<double, int> rowIndex, int decimals)
    {
        if (_vm is null) return;
        DrawPocSeries(bars, rowIndex, b => b.PointOfControl, PocLineBrush, 2.5);
        DrawPocSeries(bars, rowIndex, b => b.BuyPointOfControl, BuyPocBrush, 2.0);
        DrawPocSeries(bars, rowIndex, b => b.SellPointOfControl, SellPocBrush, 2.0);
        var predicted = _vm.Predicted;
        DrawFitCurves(bars.Count + predicted.Count, rows);
        DrawPredictedBars(bars, predicted, rows, decimals);
    }

    /// <summary>Draws the virtual predictor: a dashed boundary at "now", a faint wash over the
    /// forecast region, and one ghost candle per future column — body spanning the consensus
    /// buy/sell POCs, a dashed orange tick at the consensus total POC, predicted POC in the footer.
    /// All values come pre-computed from <see cref="VolumeFootprintViewModel.Predicted"/>.</summary>
    private void DrawPredictedBars(List<RenderBar> bars, IReadOnlyList<PredictedBar> predicted,
        List<double> rows, int decimals)
    {
        if (predicted.Count == 0 || rows.Count == 0) return;

        var x0 = LeftAxisWidth + bars.Count * ColumnWidth;
        var height = FootprintCanvas.Height;

        FootprintCanvas.Children.Add(Place(new Rectangle
        {
            Width = predicted.Count * ColumnWidth,
            Height = height,
            Fill = PredictShade,
        }, x0, 0));
        FootprintCanvas.Children.Add(new Line
        {
            X1 = x0, Y1 = 0, X2 = x0, Y2 = height,
            Stroke = DimText, StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 4, 4 },
        });

        var prevPoc = bars.Count > 0 ? bars[^1].PointOfControl : double.NaN;
        var fy = HeaderHeight + rows.Count * RowHeight;
        for (var j = 0; j < predicted.Count; j++)
        {
            var p = predicted[j];
            var x = x0 + j * ColumnWidth;
            AddText($"+{j + 1}", x, 0, ColumnWidth, HeaderHeight, DimText, 10.5, TextAlignment.Center);

            var up = !double.IsFinite(p.Poc) || !double.IsFinite(prevPoc) || p.Poc >= prevPoc;
            if (double.IsFinite(p.BuyPoc) && double.IsFinite(p.SellPoc))
            {
                var yTop = PriceToY(Math.Max(p.BuyPoc, p.SellPoc), rows);
                var yBottom = PriceToY(Math.Min(p.BuyPoc, p.SellPoc), rows);
                var bodyW = ColumnWidth * 0.5;
                FootprintCanvas.Children.Add(Place(new Rectangle
                {
                    Width = bodyW,
                    Height = Math.Max(3, yBottom - yTop),
                    Fill = up ? GhostUpFill : GhostDownFill,
                    Stroke = up ? UpBrush : DownBrush,
                    StrokeThickness = 1.2,
                    StrokeDashArray = new DoubleCollection { 3, 2 },
                }, x + (ColumnWidth - bodyW) / 2.0, yTop));
            }

            if (double.IsFinite(p.Poc))
            {
                var y = PriceToY(p.Poc, rows);
                var tickW = ColumnWidth * 0.72;
                FootprintCanvas.Children.Add(new Line
                {
                    X1 = x + (ColumnWidth - tickW) / 2.0, Y1 = y,
                    X2 = x + (ColumnWidth + tickW) / 2.0, Y2 = y,
                    Stroke = PocLineBrush, StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 2, 2 },
                });
                AddText(p.Poc.ToString("N" + decimals, CultureInfo.InvariantCulture),
                    x, fy + 3, ColumnWidth, 16, DimText, 10.5, TextAlignment.Center);
                prevPoc = p.Poc;
            }
        }
    }

    /// <summary>Draws one POC flavour: a polyline through the centre of each bar's selected POC cell
    /// (dots per vertex). The regression overlays are drawn separately by <see cref="DrawFitCurves"/>.</summary>
    private void DrawPocSeries(List<RenderBar> bars, IReadOnlyDictionary<double, int> rowIndex,
        Func<RenderBar, double> pocSelector, Brush connectorBrush, double dotRadius)
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
    }

    /// <summary>Draws every enabled fit curve from the VM, sampled once per column. Brush follows
    /// the POC series (total/buy/sell); the dash pattern distinguishes the fit kind (LOWESS solid).</summary>
    private void DrawFitCurves(int columns, List<double> rows)
    {
        if (_vm is null || columns < 2 || rows.Count == 0) return;
        foreach (var curve in _vm.FitCurves)
        {
            var pts = new PointCollection();
            var count = Math.Min(columns, curve.Prices.Count);
            for (var i = 0; i < count; i++)
            {
                var price = curve.Prices[i];
                if (!double.IsFinite(price)) continue;
                pts.Add(new Point(
                    LeftAxisWidth + i * ColumnWidth + ColumnWidth / 2.0,
                    PriceToY(price, rows)));
            }
            if (pts.Count < 2) continue;

            var line = new Polyline
            {
                Points = pts,
                Stroke = curve.Series switch
                {
                    PocSeries.Buy => BuyPocBrush,
                    PocSeries.Sell => SellPocBrush,
                    _ => RegLineBrush,
                },
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
            };
            var dash = DashFor(curve.Kind);
            if (dash is not null) line.StrokeDashArray = dash;
            FootprintCanvas.Children.Add(line);
        }
    }

    private static DoubleCollection? DashFor(CurveFitKind kind) => kind switch
    {
        CurveFitKind.Linear => new DoubleCollection { 5, 3 },
        CurveFitKind.Quadratic => new DoubleCollection { 2, 2 },
        CurveFitKind.Cubic => new DoubleCollection { 7, 2, 2, 2 },
        CurveFitKind.TheilSen => new DoubleCollection { 12, 3 },
        CurveFitKind.Exponential => new DoubleCollection { 1, 3 },
        CurveFitKind.Logarithmic => new DoubleCollection { 4, 2, 1, 2 },
        _ => null, // Lowess: solid, it's the only smoothed (non-parametric) curve
    };

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

    private void DrawBar(RenderBar bar, int colIndex, IReadOnlyDictionary<double, int> rowIndex,
        long maxCellVol, int decimals)
    {
        var x = LeftAxisWidth + colIndex * ColumnWidth;

        // Column header: bar start time.
        AddText(bar.StartUtc.ToLocalTime().ToString("HH:mm:ss"), x, 0, ColumnWidth, HeaderHeight,
            DimText, 10.5, TextAlignment.Center);

        // Cells (Core FootprintFeatureRow: SellVolume left / BuyVolume right).
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
