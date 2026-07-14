using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using TradingTerminal.Core.Quant;

namespace TradingTerminal.VolumeFootprint;

/// <summary>
/// The Volume Footprint, as an embeddable control: the bid/ask cluster grid and everything the
/// standalone window layers over it — imbalances, value area, composite session profile, POC connectors,
/// regression fits, the virtual and ML forecasts, the crosshair read-out — with each part switchable via
/// <see cref="Features"/>.
/// <para>
/// The window is now a thin host around this. An authored strategy embeds it too, but usually with the
/// chrome, the fits and the ML off: a panel is only as heavy as the features it is asked for, and a
/// strategy that just reads order flow should not be paying to train a next-bar forecaster.
/// </para>
/// <para>
/// Pure presentation: it draws onto a <see cref="Canvas"/> in response to
/// <see cref="VolumeFootprintViewModel.FootprintChanged"/>, the same render-in-code-behind convention
/// the Order Flow Cube window uses. The VM owns all data and the trade subscription.
/// </para>
/// </summary>
public partial class VolumeFootprintPanel : UserControl
{
    /// <summary>Which parts of the panel are switched on. Set it before the control loads (it is read
    /// once, on load, to decide what to draw and what the view-model should bother computing).</summary>
    public static readonly DependencyProperty FeaturesProperty = DependencyProperty.Register(
        nameof(Features), typeof(VolumeFootprintPanelFeatures), typeof(VolumeFootprintPanel),
        new PropertyMetadata(VolumeFootprintPanelFeatures.Full));

    public VolumeFootprintPanelFeatures Features
    {
        get => (VolumeFootprintPanelFeatures)GetValue(FeaturesProperty);
        set => SetValue(FeaturesProperty, value);
    }

    private const double LeftAxisWidth = 66;
    private const double ColumnWidth = 104;
    private const double BaseRowHeight = 17;
    private const double HeaderHeight = 22;
    private const double FooterHeight = 40;
    private const double ProfileWidth = 96; // right-edge composite-profile gutter

    // Fixed palette (canvas drawing is theme-independent).
    private static readonly Color BuyColor = Color.FromRgb(0x2E, 0x7D, 0x32);
    private static readonly Color SellColor = Color.FromRgb(0xC6, 0x28, 0x28);
    private static readonly Brush BuyBrush = new SolidColorBrush(BuyColor);
    private static readonly Brush SellBrush = new SolidColorBrush(SellColor);
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
    private static readonly Brush MlGhostStroke = new SolidColorBrush(Color.FromRgb(0xB3, 0x88, 0xFF));      // ML ghost outline (violet)
    private static readonly Brush MlGhostUpFill = new SolidColorBrush(Color.FromArgb(0x30, 0x7C, 0xB3, 0x42)); // violet-tinted buy wash
    private static readonly Brush MlGhostDownFill = new SolidColorBrush(Color.FromArgb(0x30, 0xD3, 0x2F, 0x5A)); // violet-tinted sell wash
    private static readonly Brush ImbBuyPen = new SolidColorBrush(Color.FromRgb(0x69, 0xF0, 0xAE));  // ask-imbalance (stacked buying)
    private static readonly Brush ImbSellPen = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); // bid-imbalance (stacked selling)
    private static readonly Brush ValueAreaFill = new SolidColorBrush(Color.FromArgb(0x14, 0x90, 0xCA, 0xF9));
    private static readonly Brush ValueAreaEdge = new SolidColorBrush(Color.FromArgb(0x66, 0x90, 0xCA, 0xF9));
    private static readonly Brush CrosshairPen = new SolidColorBrush(Color.FromArgb(0xB0, 0xBB, 0xBB, 0xBB));
    private static readonly Brush TooltipBack = new SolidColorBrush(Color.FromArgb(0xEE, 0x21, 0x25, 0x2B));
    private static readonly Brush TooltipBorder = new SolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x5A, 0x60));

    // Shared, immutable font reused by every TextBlock — the canvas is rebuilt many times a second,
    // so allocating a FontFamily per text element churns the GC for nothing.
    private static readonly FontFamily MonoFont = new("Consolas");

    // Pre-baked, frozen cell brushes bucketed by intensity, one palette per base colour. The cluster
    // grid is rebuilt many times a second; allocating a SolidColorBrush per cell (the old path) churned
    // the GC. We quantise the volume→alpha ramp (40 + 180·frac) into a fixed palette and reuse it.
    // Built in the static ctor (not a field initializer) so RegLineColor — declared lower in the file —
    // is already initialized when its palette is baked.
    private const int CellAlphaBuckets = 48;
    private static readonly Brush[] BuyCells;
    private static readonly Brush[] SellCells;
    private static readonly Brush[] VolumeCells;

    private static Brush[] BuildCellBrushes(Color baseColor)
    {
        var brushes = new Brush[CellAlphaBuckets];
        for (var i = 0; i < CellAlphaBuckets; i++)
        {
            var frac = (double)i / (CellAlphaBuckets - 1);
            var alpha = (byte)(40 + 180.0 * frac);
            var b = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            b.Freeze();
            brushes[i] = b;
        }
        return brushes;
    }

    private static Brush[] CellPaletteFor(Color baseColor) =>
        baseColor == BuyColor ? BuyCells : baseColor == SellColor ? SellCells : VolumeCells;

    static VolumeFootprintPanel()
    {
        foreach (var b in new[] { BuyBrush, SellBrush, PocPen, GridPen, TextBrush, DimText, UpBrush, DownBrush, PocLineBrush, RegLineBrush, BuyPocBrush, SellPocBrush, PredictShade, GhostUpFill, GhostDownFill, MlGhostStroke, MlGhostUpFill, MlGhostDownFill, ImbBuyPen, ImbSellPen, ValueAreaFill, ValueAreaEdge, CrosshairPen, TooltipBack, TooltipBorder })
            b.Freeze();

        BuyCells = BuildCellBrushes(BuyColor);
        SellCells = BuildCellBrushes(SellColor);
        VolumeCells = BuildCellBrushes(RegLineColor);
    }

    private VolumeFootprintViewModel? _vm;

    // ── Per-redraw layout state, captured so the crosshair handler can map cursor → cell ────────
    private double _rowH = BaseRowHeight;
    private List<RenderBar> _renderBars = new();
    private List<double> _rows = new();
    private int _decimals = 2;
    private double _tick = 0.25;
    private readonly List<UIElement> _crosshair = new();

    public VolumeFootprintPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        FootprintCanvas.MouseMove += OnCanvasMouseMove;
        FootprintCanvas.MouseLeave += OnCanvasMouseLeave;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyFeatureGates();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.FootprintChanged -= OnFootprintChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.FootprintChanged -= OnFootprintChanged;
        _vm = e.NewValue as VolumeFootprintViewModel;
        if (_vm is null) return;

        ApplyFeatureGates();
        _vm.FootprintChanged += OnFootprintChanged;
        Redraw();
    }

    /// <summary>
    /// Pushes the build-time gates down onto the view-model's user toggles. Both the renderer and the
    /// analytics read those toggles, so switching one off here is what actually skips the work —
    /// collapsing an overlay in XAML would leave the fits and the forecast being computed for nobody.
    /// <see cref="VolumeFootprintViewModel.MlEnabled"/> goes further and stops the model being built.
    /// </summary>
    private void ApplyFeatureGates()
    {
        if (_vm is null) return;
        var features = Features;

        _vm.MlEnabled = features.MlForecast;
        if (!features.MlForecast) _vm.ShowMlPrediction = false;

        if (!features.Regression)
        {
            _vm.ShowLinearFit = false;
            _vm.ShowQuadraticFit = false;
            _vm.ShowCubicFit = false;
            _vm.ShowTheilSenFit = false;
            _vm.ShowExponentialFit = false;
            _vm.ShowLogarithmicFit = false;
            _vm.ShowLowessFit = false;
            _vm.ShowPredictedBars = false;
        }

        if (!features.Imbalances) _vm.ShowImbalances = false;
        if (!features.ValueArea) _vm.ShowValueArea = false;
        if (!features.VolumeProfile) _vm.ShowVolumeProfile = false;
    }

    private void OnFootprintChanged(object? sender, EventArgs e) => Redraw();

    private void Redraw()
    {
        ClearCrosshair();
        FootprintCanvas.Children.Clear();
        if (_vm is null) return;

        _rowH = BaseRowHeight * Math.Clamp(_vm.Zoom, 0.4, 4.0);
        var mode = _vm.SelectedDisplayMode;

        var bars = _vm.Bars.ToList();
        _renderBars = bars;
        if (bars.Count == 0)
        {
            FootprintCanvas.Width = 0;
            FootprintCanvas.Height = 0;
            _rows = new List<double>();
            return;
        }

        // Shared price axis: union of every traded bucket across visible bars, high → low. We also
        // accumulate the composite (session) volume profile per price for the right-edge gutter.
        var prices = new SortedSet<double>();
        var profileBuy = new Dictionary<double, long>();
        var profileSell = new Dictionary<double, long>();
        long maxCellVol = 1;
        long maxProfileVol = 1;
        foreach (var bar in bars)
            foreach (var cell in bar.Cells)
            {
                prices.Add(cell.Price);
                if (cell.TotalVolume > maxCellVol) maxCellVol = cell.TotalVolume;
                var pb = profileBuy.GetValueOrDefault(cell.Price) + cell.BuyVolume;
                var ps = profileSell.GetValueOrDefault(cell.Price) + cell.SellVolume;
                profileBuy[cell.Price] = pb;
                profileSell[cell.Price] = ps;
                if (pb + ps > maxProfileVol) maxProfileVol = pb + ps;
            }

        // Predicted (virtual) columns extend the price axis: snap each consensus/ML price to the
        // tick grid so the ghost candles get real rows even above/below the traded range. The
        // ghost region is the wider of the regression and ML forecasts, so ML ghosts still render
        // when the regression predictor is toggled off.
        var predicted = _vm.Predicted;
        var mlPredicted = _vm.MlPredicted;
        var ghostCols = Math.Max(predicted.Count, mlPredicted.Count);
        var tick = ParseTick(_vm.TickSizeText);
        _tick = tick;
        foreach (var p in predicted)
            foreach (var v in new[] { p.Poc, p.BuyPoc, p.SellPoc })
                if (double.IsFinite(v))
                    prices.Add(Math.Round(Math.Round(v / tick) * tick, 10));
        foreach (var p in mlPredicted)
            foreach (var v in new[] { p.Poc, p.BuyPoc, p.SellPoc })
                if (double.IsFinite(v))
                    prices.Add(Math.Round(Math.Round(v / tick) * tick, 10));

        var rows = prices.Reverse().ToList(); // descending price (top = highest)
        _rows = rows;
        var rowIndex = new Dictionary<double, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++) rowIndex[rows[i]] = i;

        var decimals = DecimalsFor(tick);
        _decimals = decimals;

        var showProfile = _vm.ShowVolumeProfile;
        FootprintCanvas.Width = LeftAxisWidth + (bars.Count + ghostCols) * ColumnWidth
                                + (showProfile ? ProfileWidth : 0);
        FootprintCanvas.Height = HeaderHeight + rows.Count * _rowH + FooterHeight;

        DrawPriceAxis(rows, decimals);

        for (var b = 0; b < bars.Count; b++)
            DrawBar(bars[b], b, rowIndex, maxCellVol, decimals, mode);

        if (showProfile)
            DrawCompositeProfile(bars, rows, rowIndex, profileBuy, profileSell, maxProfileVol);

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
        DrawMlPredictedBars(bars, _vm.MlPredicted, predicted.Count, rows, decimals);
    }

    /// <summary>Right-edge composite session volume profile: a horizontal bid/ask histogram of total
    /// volume per price across every visible bar, sell (red) growing left-to-right then buy (green)
    /// continuing right. The session-POC row gets a yellow outline.</summary>
    private void DrawCompositeProfile(List<RenderBar> bars, List<double> rows,
        IReadOnlyDictionary<double, int> rowIndex, Dictionary<double, long> profileBuy,
        Dictionary<double, long> profileSell, long maxProfileVol)
    {
        var x0 = LeftAxisWidth + (bars.Count + Math.Max(_vm!.Predicted.Count, _vm.MlPredicted.Count)) * ColumnWidth;
        const double pad = 4;
        var usableW = ProfileWidth - pad * 2;

        // Separator + heading.
        FootprintCanvas.Children.Add(new Line
        {
            X1 = x0, Y1 = 0, X2 = x0, Y2 = HeaderHeight + rows.Count * _rowH,
            Stroke = GridPen, StrokeThickness = 1,
        });
        AddText("PROFILE", x0, 0, ProfileWidth, HeaderHeight, DimText, 9.5, TextAlignment.Center);

        long sessionPocVol = -1;
        var sessionPocPrice = double.NaN;
        foreach (var kv in profileBuy)
        {
            var total = kv.Value + profileSell.GetValueOrDefault(kv.Key);
            if (total > sessionPocVol) { sessionPocVol = total; sessionPocPrice = kv.Key; }
        }

        for (var r = 0; r < rows.Count; r++)
        {
            var price = rows[r];
            var buy = profileBuy.GetValueOrDefault(price);
            var sell = profileSell.GetValueOrDefault(price);
            if (buy + sell == 0) continue;
            var y = HeaderHeight + r * _rowH + 1;
            var h = Math.Max(1, _rowH - 2);
            var sellW = usableW * sell / maxProfileVol;
            var buyW = usableW * buy / maxProfileVol;

            if (sellW > 0)
                FootprintCanvas.Children.Add(Place(new Rectangle { Width = sellW, Height = h, Fill = SellBrush }, x0 + pad, y));
            if (buyW > 0)
                FootprintCanvas.Children.Add(Place(new Rectangle { Width = buyW, Height = h, Fill = BuyBrush }, x0 + pad + sellW, y));

            if (!double.IsNaN(sessionPocPrice) && AreClose(price, sessionPocPrice))
                FootprintCanvas.Children.Add(Place(new Rectangle
                {
                    Width = Math.Max(2, sellW + buyW), Height = h,
                    Stroke = PocPen, StrokeThickness = 1.2, Fill = Brushes.Transparent,
                }, x0 + pad, y));
        }
    }

    /// <summary>Draws the virtual predictor: a dashed boundary at "now", a faint wash over the
    /// forecast region, and one ghost candle per future column — body spanning the consensus
    /// buy/sell POCs, a dashed orange tick at the consensus total POC, predicted POC in the footer.</summary>
    private void DrawPredictedBars(List<RenderBar> bars, IReadOnlyList<PredictedBar> predicted,
        List<double> rows, int decimals)
    {
        if (predicted.Count == 0 || rows.Count == 0) return;

        var x0 = LeftAxisWidth + bars.Count * ColumnWidth;
        var height = HeaderHeight + rows.Count * _rowH + FooterHeight;

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
        var fy = HeaderHeight + rows.Count * _rowH;
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

    /// <summary>Draws the ML forecast as violet <em>dotted</em> ghost candles overlaid on the same
    /// future columns as the regression ghosts (which are dashed): body spans the predicted
    /// buy/sell POCs, its <b>width scales with the predicted volume</b> (vs the visible bars'
    /// mean), its <b>tint follows the predicted delta sign</b>, a dotted violet tick marks the
    /// predicted total POC and the footer prints the predicted delta. When the regression
    /// predictor is off (or shorter), this also supplies the forecast wash / boundary / column
    /// headers for the columns the regression didn't draw.</summary>
    private void DrawMlPredictedBars(List<RenderBar> bars, IReadOnlyList<MlPredictedBar> ml,
        int regressionCols, List<double> rows, int decimals)
    {
        if (ml.Count == 0 || rows.Count == 0) return;

        var x0 = LeftAxisWidth + bars.Count * ColumnWidth;
        var height = HeaderHeight + rows.Count * _rowH + FooterHeight;

        if (ml.Count > regressionCols)
        {
            FootprintCanvas.Children.Add(Place(new Rectangle
            {
                Width = (ml.Count - regressionCols) * ColumnWidth,
                Height = height,
                Fill = PredictShade,
            }, x0 + regressionCols * ColumnWidth, 0));
            if (regressionCols == 0)
                FootprintCanvas.Children.Add(new Line
                {
                    X1 = x0, Y1 = 0, X2 = x0, Y2 = height,
                    Stroke = DimText, StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 4 },
                });
        }

        double meanVolume = 0;
        foreach (var bar in bars) meanVolume += bar.TotalVolume;
        meanVolume = bars.Count > 0 ? meanVolume / bars.Count : 0;

        var fy = HeaderHeight + rows.Count * _rowH;
        for (var j = 0; j < ml.Count; j++)
        {
            var p = ml[j];
            var x = x0 + j * ColumnWidth;
            if (j >= regressionCols)
                AddText($"+{j + 1}", x, 0, ColumnWidth, HeaderHeight, DimText, 10.5, TextAlignment.Center);

            if (double.IsFinite(p.BuyPoc) && double.IsFinite(p.SellPoc))
            {
                var yTop = PriceToY(Math.Max(p.BuyPoc, p.SellPoc), rows);
                var yBottom = PriceToY(Math.Min(p.BuyPoc, p.SellPoc), rows);
                var widthFrac = meanVolume > 0 && double.IsFinite(p.Volume)
                    ? Math.Clamp(0.22 + 0.5 * p.Volume / meanVolume, 0.22, 0.86)
                    : 0.34;
                var bodyW = ColumnWidth * widthFrac;
                FootprintCanvas.Children.Add(Place(new Rectangle
                {
                    Width = bodyW,
                    Height = Math.Max(3, yBottom - yTop),
                    Fill = p.Delta >= 0 ? MlGhostUpFill : MlGhostDownFill,
                    Stroke = MlGhostStroke,
                    StrokeThickness = 1.4,
                    StrokeDashArray = new DoubleCollection { 1, 3 },
                }, x + (ColumnWidth - bodyW) / 2.0, yTop));
            }

            if (double.IsFinite(p.Poc))
            {
                var y = PriceToY(p.Poc, rows);
                var tickW = ColumnWidth * 0.6;
                FootprintCanvas.Children.Add(new Line
                {
                    X1 = x + (ColumnWidth - tickW) / 2.0, Y1 = y,
                    X2 = x + (ColumnWidth + tickW) / 2.0, Y2 = y,
                    Stroke = MlGhostStroke, StrokeThickness = 2,
                    StrokeDashArray = new DoubleCollection { 1, 2 },
                });
                if (j >= regressionCols)
                    AddText(p.Poc.ToString("N" + decimals, CultureInfo.InvariantCulture),
                        x, fy + 3, ColumnWidth, 16, DimText, 10.5, TextAlignment.Center);
            }

            AddText($"Δ̂ {p.Delta:+#,##0;-#,##0;0}", x, fy + 19, ColumnWidth, 14,
                MlGhostStroke, 10, TextAlignment.Center);
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
                HeaderHeight + r * _rowH + _rowH / 2.0);
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
    private double PriceToY(double price, List<double> rows)
    {
        if (price >= rows[0]) return HeaderHeight + _rowH / 2.0;
        if (price <= rows[^1]) return HeaderHeight + (rows.Count - 1) * _rowH + _rowH / 2.0;
        for (var r = 0; r < rows.Count - 1; r++)
        {
            if (rows[r] >= price && price >= rows[r + 1])
            {
                var frac = (rows[r] - price) / (rows[r] - rows[r + 1]);
                return HeaderHeight + (r + frac) * _rowH + _rowH / 2.0;
            }
        }
        return HeaderHeight + _rowH / 2.0;
    }

    private void DrawPriceAxis(List<double> rows, int decimals)
    {
        for (var r = 0; r < rows.Count; r++)
        {
            var y = HeaderHeight + r * _rowH;
            AddText(rows[r].ToString("N" + decimals, CultureInfo.InvariantCulture),
                0, y, LeftAxisWidth - 6, _rowH, DimText, 10.5, TextAlignment.Right);
        }
    }

    private void DrawBar(RenderBar bar, int colIndex, IReadOnlyDictionary<double, int> rowIndex,
        long maxCellVol, int decimals, CellDisplayMode mode)
    {
        var x = LeftAxisWidth + colIndex * ColumnWidth;
        var showText = _vm!.ShowCellText;

        // Column header: bar start time.
        AddText(bar.StartUtc.ToLocalTime().ToString("HH:mm:ss"), x, 0, ColumnWidth, HeaderHeight,
            DimText, 10.5, TextAlignment.Center);

        // Value-area band behind this column's cells (VAH ↔ VAL).
        if (_vm.ShowValueArea && !double.IsNaN(bar.ValueAreaHigh) && !double.IsNaN(bar.ValueAreaLow)
            && rowIndex.TryGetValue(bar.ValueAreaHigh, out var rHigh)
            && rowIndex.TryGetValue(bar.ValueAreaLow, out var rLow))
        {
            var yTop = HeaderHeight + rHigh * _rowH;
            var bandH = (rLow - rHigh + 1) * _rowH;
            FootprintCanvas.Children.Add(Place(new Rectangle
            {
                Width = ColumnWidth - 2, Height = bandH, Fill = ValueAreaFill,
                Stroke = ValueAreaEdge, StrokeThickness = 1,
            }, x + 1, yTop));
        }

        var halfW = (ColumnWidth - 2) / 2.0;
        var showImb = _vm.ShowImbalances;
        foreach (var cell in bar.Cells)
        {
            if (!rowIndex.TryGetValue(cell.Price, out var r)) continue;
            var y = HeaderHeight + r * _rowH;

            switch (mode)
            {
                case CellDisplayMode.BidAsk:
                    // Sell volume on the left (red), buy volume on the right (green).
                    AddCellHalf(x + 1, y, halfW, cell.SellVolume, maxCellVol, SellColor, isLeft: true, showText);
                    AddCellHalf(x + 1 + halfW, y, halfW, cell.BuyVolume, maxCellVol, BuyColor, isLeft: false, showText);
                    // Imbalance flags outline the dominant half so stacked runs read as columns.
                    if (showImb && cell.AskImbalance)
                        FootprintCanvas.Children.Add(Place(new Rectangle
                        { Width = halfW, Height = _rowH, Stroke = ImbBuyPen, StrokeThickness = 1.6, Fill = Brushes.Transparent }, x + 1 + halfW, y));
                    if (showImb && cell.BidImbalance)
                        FootprintCanvas.Children.Add(Place(new Rectangle
                        { Width = halfW, Height = _rowH, Stroke = ImbSellPen, StrokeThickness = 1.6, Fill = Brushes.Transparent }, x + 1, y));
                    break;

                case CellDisplayMode.Delta:
                {
                    var delta = cell.Delta;
                    var baseColor = delta >= 0 ? BuyColor : SellColor;
                    AddFullCell(x + 1, y, ColumnWidth - 2, Math.Abs(delta), maxCellVol, baseColor,
                        showText ? (delta).ToString("+#;-#;0", CultureInfo.InvariantCulture) : null);
                    if (showImb && (cell.AskImbalance || cell.BidImbalance))
                        FootprintCanvas.Children.Add(Place(new Rectangle
                        { Width = ColumnWidth - 2, Height = _rowH, Stroke = cell.AskImbalance ? ImbBuyPen : ImbSellPen, StrokeThickness = 1.4, Fill = Brushes.Transparent }, x + 1, y));
                    break;
                }

                default: // Volume
                {
                    AddFullCell(x + 1, y, ColumnWidth - 2, cell.TotalVolume, maxCellVol, RegLineColor,
                        showText ? cell.TotalVolume.ToString("N0", CultureInfo.InvariantCulture) : null);
                    if (showImb && (cell.AskImbalance || cell.BidImbalance))
                        FootprintCanvas.Children.Add(Place(new Rectangle
                        { Width = ColumnWidth - 2, Height = _rowH, Stroke = cell.AskImbalance ? ImbBuyPen : ImbSellPen, StrokeThickness = 1.4, Fill = Brushes.Transparent }, x + 1, y));
                    break;
                }
            }

            var isPoc = !double.IsNaN(bar.PointOfControl) && AreClose(cell.Price, bar.PointOfControl);
            if (isPoc)
                FootprintCanvas.Children.Add(Place(new Rectangle
                {
                    Width = ColumnWidth - 2,
                    Height = _rowH,
                    Stroke = PocPen,
                    StrokeThickness = 1.4,
                    Fill = Brushes.Transparent,
                }, x + 1, y));
        }

        // Footer: bar delta, total volume, and stacked-imbalance markers.
        var fy = HeaderHeight + rowIndex.Count * _rowH;
        FootprintCanvas.Children.Add(Place(new Line
        {
            X1 = x, Y1 = fy + 2, X2 = x + ColumnWidth, Y2 = fy + 2,
            Stroke = GridPen, StrokeThickness = 1,
        }, 0, 0));

        var deltaBrush = bar.Delta >= 0 ? UpBrush : DownBrush;
        AddText($"Δ {bar.Delta:+#;-#;0}", x, fy + 3, ColumnWidth, 16, deltaBrush, 11, TextAlignment.Center);

        if (_vm.ShowImbalances && (bar.StackedBuy > 0 || bar.StackedSell > 0))
        {
            var sb = bar.StackedBuy > 0 ? $"▲{bar.StackedBuy}" : "";
            var ss = bar.StackedSell > 0 ? $"▼{bar.StackedSell}" : "";
            var stackedBrush = bar.StackedBuy >= bar.StackedSell ? ImbBuyPen : ImbSellPen;
            AddText($"Σ {bar.TotalVolume:N0}  {sb}{(sb.Length > 0 && ss.Length > 0 ? " " : "")}{ss}",
                x, fy + 19, ColumnWidth, 14, stackedBrush, 10, TextAlignment.Center);
        }
        else
        {
            AddText($"Σ {bar.TotalVolume:N0}", x, fy + 19, ColumnWidth, 14, DimText, 10, TextAlignment.Center);
        }
    }

    private static readonly Color RegLineColor = Color.FromRgb(0x29, 0xB6, 0xF6);

    /// <summary>A half-width cell (bid×ask mode). Background alpha scales with volume so heavy
    /// levels pop; the figure is right/left aligned toward the column centre.</summary>
    private void AddCellHalf(double x, double y, double w, long vol, long maxVol, Color baseColor, bool isLeft, bool showText)
    {
        if (vol <= 0) return;
        AddVolumeRect(x, y, w, vol, maxVol, baseColor);
        if (showText)
            AddText(vol.ToString("N0", CultureInfo.InvariantCulture), x, y, w - 3, _rowH,
                TextBrush, 10, isLeft ? TextAlignment.Right : TextAlignment.Left, leftPad: isLeft ? 0 : 3);
    }

    /// <summary>A full-width cell (delta / volume mode), centred figure.</summary>
    private void AddFullCell(double x, double y, double w, long magnitude, long maxVol, Color baseColor, string? text)
    {
        if (magnitude > 0) AddVolumeRect(x, y, w, magnitude, maxVol, baseColor);
        if (text is not null && magnitude != 0)
            AddText(text, x, y, w - 4, _rowH, TextBrush, 10, TextAlignment.Center);
    }

    private void AddVolumeRect(double x, double y, double w, long vol, long maxVol, Color baseColor)
    {
        var frac = Math.Min(1.0, (double)vol / Math.Max(1, maxVol));
        var fill = CellPaletteFor(baseColor)[(int)(frac * (CellAlphaBuckets - 1))];
        FootprintCanvas.Children.Add(Place(new Rectangle { Width = Math.Max(1, w), Height = _rowH, Fill = fill }, x, y));
    }

    private void AddText(string text, double x, double y, double w, double h, Brush brush, double size,
        TextAlignment align, double leftPad = 0)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontFamily = MonoFont,
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

    // ── Crosshair + hover read-out ──────────────────────────────────────────────────────────────

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        ClearCrosshair();
        if (_vm is null || _rows.Count == 0) return;

        var pos = e.GetPosition(FootprintCanvas);
        var gridBottom = HeaderHeight + _rows.Count * _rowH;
        if (pos.Y < HeaderHeight || pos.Y > gridBottom || pos.X < LeftAxisWidth) return;

        var rowIdx = (int)Math.Clamp(Math.Floor((pos.Y - HeaderHeight) / _rowH), 0, _rows.Count - 1);
        var rowY = HeaderHeight + rowIdx * _rowH;
        var price = _rows[rowIdx];

        // Horizontal + vertical crosshair lines.
        AddCrosshair(new Line { X1 = LeftAxisWidth, Y1 = rowY + _rowH / 2.0, X2 = FootprintCanvas.Width, Y2 = rowY + _rowH / 2.0, Stroke = CrosshairPen, StrokeThickness = 1 });
        AddCrosshair(new Line { X1 = pos.X, Y1 = HeaderHeight, X2 = pos.X, Y2 = gridBottom, Stroke = CrosshairPen, StrokeThickness = 1 });

        // Which bar column is under the cursor (if any)?
        var col = (int)Math.Floor((pos.X - LeftAxisWidth) / ColumnWidth);
        var lines = new List<(string, Brush)> { ($"Price  {price.ToString("N" + _decimals, CultureInfo.InvariantCulture)}", TextBrush) };
        if (col >= 0 && col < _renderBars.Count)
        {
            var bar = _renderBars[col];
            lines.Add(($"{bar.StartUtc.ToLocalTime():HH:mm:ss}", DimText));
            FootprintFeatureRowOrNull(bar, price, out var buy, out var sell);
            lines.Add(($"Buy   {buy:N0}", UpBrush));
            lines.Add(($"Sell  {sell:N0}", DownBrush));
            lines.Add(($"Δ     {(buy - sell):+#;-#;0}", (buy - sell) >= 0 ? UpBrush : DownBrush));
            lines.Add(($"Total {(buy + sell):N0}", TextBrush));
        }

        DrawTooltip(pos, lines);
    }

    private static void FootprintFeatureRowOrNull(RenderBar bar, double price, out long buy, out long sell)
    {
        buy = 0; sell = 0;
        foreach (var c in bar.Cells)
            if (Math.Abs(c.Price - price) < 1e-9) { buy = c.BuyVolume; sell = c.SellVolume; return; }
    }

    private void DrawTooltip(Point cursor, List<(string Text, Brush Brush)> lines)
    {
        var panel = new StackPanel();
        foreach (var (text, brush) in lines)
            panel.Children.Add(new TextBlock
            {
                Text = text, Foreground = brush, FontFamily = MonoFont,
                FontSize = 11, Margin = new Thickness(0, 0.5, 0, 0.5),
            });
        var border = new Border
        {
            Background = TooltipBack, BorderBrush = TooltipBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 5, 7, 5), Child = panel,
        };
        // Offset from the cursor; flip left/up near the canvas edges.
        var bx = cursor.X + 16;
        var by = cursor.Y + 14;
        const double estW = 130, estH = 110;
        if (bx + estW > FootprintCanvas.Width) bx = cursor.X - estW - 8;
        if (by + estH > FootprintCanvas.Height) by = cursor.Y - estH - 8;
        AddCrosshair((UIElement)Place(border, Math.Max(0, bx), Math.Max(0, by)));
    }

    private void AddCrosshair(UIElement el)
    {
        _crosshair.Add(el);
        FootprintCanvas.Children.Add(el);
    }

    private void ClearCrosshair()
    {
        foreach (var el in _crosshair) FootprintCanvas.Children.Remove(el);
        _crosshair.Clear();
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e) => ClearCrosshair();

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

    /// <summary>Toolbar 📷: PNG snapshot of the whole window content (cluster grid + stats panel).
    /// View-side by design — the visual tree is a view concern; data exports are VM commands.</summary>
    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root) return;
        var symbol = _vm?.SelectedInstrument?.Contract.Symbol ?? "footprint";
        var path = TradingTerminal.UI.Controls.ViewExport.SavePng(
            root, $"footprint-{symbol}-{DateTime.Now:yyyyMMdd-HHmmss}");
        if (path is not null && _vm is not null) _vm.Status = $"Snapshot saved → {path}";
    }

}
