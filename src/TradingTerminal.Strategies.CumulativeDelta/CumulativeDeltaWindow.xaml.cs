using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using MahApps.Metro.Controls;
using ScottPlot;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Strategies.CumulativeDelta;

public partial class CumulativeDeltaWindow : MetroWindow
{
    private CumulativeDeltaViewModel? _vm;

    // ── Footprint canvas geometry / colours (compact variant of the Apex renderer) ──
    private const double FpAxisWidth = 60;
    private const double FpColWidth = 92;
    private const double FpRowHeight = 14;
    private const double FpHeaderHeight = 16;
    private const double FpFooterHeight = 16;

    private static readonly SolidColorBrush FpBuyBrush = new(System.Windows.Media.Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush FpSellBrush = new(System.Windows.Media.Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush FpPocPen = new(System.Windows.Media.Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly SolidColorBrush FpTextBrush = new(System.Windows.Media.Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly SolidColorBrush FpDimText = new(System.Windows.Media.Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush FpAskImbPen = new(System.Windows.Media.Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly SolidColorBrush FpBidImbPen = new(System.Windows.Media.Color.FromRgb(0xFF, 0x52, 0x52));
    private static readonly SolidColorBrush BullBrush = new(System.Windows.Media.Color.FromRgb(0x26, 0xA6, 0x9A));
    private static readonly SolidColorBrush BearBrush = new(System.Windows.Media.Color.FromRgb(0xEF, 0x53, 0x50));

    static CumulativeDeltaWindow()
    {
        foreach (var b in new Brush[]
        {
            FpBuyBrush, FpSellBrush, FpPocPen, FpTextBrush, FpDimText,
            FpAskImbPen, FpBidImbPen, BullBrush, BearBrush,
        })
            b.Freeze();
    }

    public CumulativeDeltaWindow()
    {
        InitializeComponent();
        ConfigurePricePlot();
        ConfigureDeltaPlot();

        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void ConfigurePricePlot()
    {
        StyleDarkPlot(PricePlot.Plot);
        PricePlot.Plot.Axes.DateTimeTicksBottom();
        PricePlot.Refresh();
    }

    private void ConfigureDeltaPlot()
    {
        StyleDarkPlot(DeltaPlot.Plot);
        DeltaPlot.Plot.Axes.DateTimeTicksBottom();
        DeltaPlot.Refresh();
    }

    private static void StyleDarkPlot(Plot plot)
    {
        var bg   = ScottPlot.Color.FromHex("#1E1E1E");
        var grid = ScottPlot.Color.FromHex("#3F3F46");
        var text = ScottPlot.Color.FromHex("#DCDCDC");
        plot.FigureBackground.Color = bg;
        plot.DataBackground.Color   = bg;
        plot.Axes.Color(text);
        plot.Grid.MajorLineColor    = grid;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.BarsChanged      -= OnBarsChanged;
            _vm.DeltasChanged    -= OnDeltasChanged;
            _vm.FootprintChanged -= OnFootprintChanged;
        }

        _vm = e.NewValue as CumulativeDeltaViewModel;

        if (_vm is not null)
        {
            _vm.BarsChanged      += OnBarsChanged;
            _vm.DeltasChanged    += OnDeltasChanged;
            _vm.FootprintChanged += OnFootprintChanged;
            RedrawPrice();
            RedrawDeltas();
            RenderFootprint();
        }
    }

    private void OnBarsChanged(object? sender, EventArgs e)
    {
        RedrawPrice();
        SyncDeltaXAxis();
    }

    private void OnDeltasChanged(object? sender, EventArgs e) => RedrawDeltas();
    private void OnFootprintChanged(object? sender, EventArgs e) => RenderFootprint();

    private void RedrawPrice()
    {
        if (_vm is null) return;
        var plot = PricePlot.Plot;
        plot.Clear();

        if (_vm.Bars.Count == 0)
        {
            PricePlot.Refresh();
            return;
        }

        // "Bars shown" axis control = how many trailing bars to draw (X zoom).
        var total = _vm.Bars.Count;
        var shown = Math.Min(total, Math.Max(10, _vm.ChartBarsShown));
        var startIdx = total - shown;

        var xs = new double[shown];
        var ys = new double[shown];
        for (var i = 0; i < shown; i++)
        {
            xs[i] = _vm.Bars[startIdx + i].TimestampUtc.ToOADate();
            ys[i] = _vm.Bars[startIdx + i].Close;
        }

        var line = plot.Add.Scatter(xs, ys);
        line.Color = ScottPlot.Color.FromHex("#007ACC");
        line.LineWidth = 2;
        line.MarkerStyle.IsVisible = false;

        if (_vm.LastEmaHtf is { } ema)
        {
            var emaLine = plot.Add.HorizontalLine(ema, color: ScottPlot.Color.FromHex("#FFCA28"));
            emaLine.LineStyle.Pattern = LinePattern.Dashed;
            emaLine.Text = $"EMA15m {ema:F5}";
        }

        plot.Axes.AutoScale();
        // Manual Y scale overrides the auto Y range (X stays auto) when enabled and valid.
        if (!_vm.PriceAutoScale && _vm.PriceAxisMax > _vm.PriceAxisMin)
            plot.Axes.SetLimitsY(_vm.PriceAxisMin, _vm.PriceAxisMax);
        PricePlot.Refresh();
    }

    private void RedrawDeltas()
    {
        if (_vm is null) return;
        var plot = DeltaPlot.Plot;
        plot.Clear();

        if (_vm.DeltaPoints.Count == 0)
        {
            DeltaPlot.Refresh();
            return;
        }

        var pts = _vm.DeltaPoints;
        var n = pts.Count;

        // Bar width in OADate days from the median spacing (falls back to 1 minute).
        var widthDays = n >= 2
            ? (pts[^1].TimeUtc - pts[0].TimeUtc).TotalDays / Math.Max(1, n - 1) * 0.7
            : TimeSpan.FromMinutes(1).TotalDays * 0.7;

        // Per-bar delta bars, coloured by sign, on the shared time axis.
        var bars = new List<ScottPlot.Bar>(n);
        var cumXs = new double[n];
        var cumYs = new double[n];
        for (var i = 0; i < n; i++)
        {
            var x = pts[i].TimeUtc.ToOADate();
            cumXs[i] = x;
            cumYs[i] = pts[i].WindowCum;
            bars.Add(new ScottPlot.Bar
            {
                Position = x,
                Value = pts[i].BarDelta,
                Size = widthDays,
                FillColor = pts[i].BarDelta >= 0
                    ? ScottPlot.Color.FromHex("#26A69A").WithAlpha(0.55)
                    : ScottPlot.Color.FromHex("#EF5350").WithAlpha(0.55),
            });
        }
        var barPlot = plot.Add.Bars(bars);
        barPlot.Horizontal = false;

        // Windowed cumulative delta as a real series (was a horizontal line before — a bug).
        var cum = plot.Add.Scatter(cumXs, cumYs);
        cum.Color = ScottPlot.Color.FromHex("#FFCA28");
        cum.LineWidth = 2;
        cum.MarkerStyle.IsVisible = false;

        // ±effective-threshold trigger levels.
        var thr = Math.Max(1, _vm.EffectiveThreshold);
        var up = plot.Add.HorizontalLine(thr, color: ScottPlot.Color.FromHex("#26A69A"));
        up.LineStyle.Pattern = LinePattern.Dotted;
        up.Text = $"+{thr}";
        var dn = plot.Add.HorizontalLine(-thr, color: ScottPlot.Color.FromHex("#EF5350"));
        dn.LineStyle.Pattern = LinePattern.Dotted;
        dn.Text = $"-{thr}";

        plot.Axes.AutoScale();
        SyncDeltaXAxis(refresh: false);
        DeltaPlot.Refresh();
    }

    /// <summary>Keeps the delta pane on the price chart's X range so the panes read as one chart.</summary>
    private void SyncDeltaXAxis(bool refresh = true)
    {
        if (_vm is null || _vm.Bars.Count == 0 || _vm.DeltaPoints.Count == 0) return;
        var lim = PricePlot.Plot.Axes.GetLimits();
        DeltaPlot.Plot.Axes.SetLimitsX(lim.Left, lim.Right);
        if (refresh) DeltaPlot.Refresh();
    }

    // ── Footprint cluster (Core FootprintBar list — tape mode only) ──────────────────────────

    private void RenderFootprint()
    {
        FootprintCanvas.Children.Clear();
        var bars = _vm?.FootprintBars;
        if (bars is null || bars.Count == 0)
        {
            FootprintCanvas.Width = 0;
            FootprintCanvas.Height = 0;
            FootprintStatus.Text = _vm?.FeedMode == "Real tape" ? "building first bar…" : "awaiting trade tape";
            return;
        }

        // Shared price axis across the visible bars, high → low.
        var prices = new SortedSet<double>();
        long maxCellVol = 1;
        foreach (var bar in bars)
            foreach (var row in bar.Rows)
            {
                prices.Add(row.Price);
                if (row.TotalVolume > maxCellVol) maxCellVol = row.TotalVolume;
            }
        var rowsDesc = prices.Reverse().ToList();
        var rowIndex = new Dictionary<double, int>(rowsDesc.Count);
        for (var i = 0; i < rowsDesc.Count; i++) rowIndex[rowsDesc[i]] = i;
        var decimals = PriceDecimals(rowsDesc);

        FootprintCanvas.Width = FpAxisWidth + bars.Count * FpColWidth;
        FootprintCanvas.Height = FpHeaderHeight + rowsDesc.Count * FpRowHeight + FpFooterHeight;

        for (var r = 0; r < rowsDesc.Count; r++)
            AddFpText(rowsDesc[r].ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture),
                0, FpHeaderHeight + r * FpRowHeight, FpAxisWidth - 6, FpRowHeight, FpDimText, 9.5, TextAlignment.Right);

        for (var b = 0; b < bars.Count; b++)
            DrawFootprintColumn(bars[b], b, rowIndex, rowsDesc.Count, maxCellVol);

        var last = bars[^1];
        FootprintStatus.Text = $"{bars.Count} bars · stack ↑{last.StackedBuy} ↓{last.StackedSell} · POC {last.PocPrice.ToString("N" + decimals)}";
        FootprintScroll.ScrollToRightEnd();
    }

    private void DrawFootprintColumn(FootprintBar bar, int colIndex,
        IReadOnlyDictionary<double, int> rowIndex, int rowCount, long maxCellVol)
    {
        var x = FpAxisWidth + colIndex * FpColWidth;

        AddFpText(bar.StartUtc.ToLocalTime().ToString("HH:mm"),
            x, 0, FpColWidth, FpHeaderHeight, FpDimText, 9.5, TextAlignment.Center);

        var halfW = (FpColWidth - 2) / 2.0;
        foreach (var row in bar.Rows)
        {
            if (!rowIndex.TryGetValue(row.Price, out var r)) continue;
            var y = FpHeaderHeight + r * FpRowHeight;

            AddFpCellHalf(x + 1, y, halfW, row.SellVolume, maxCellVol, FpSellBrush, isLeft: true);
            AddFpCellHalf(x + 1 + halfW, y, halfW, row.BuyVolume, maxCellVol, FpBuyBrush, isLeft: false);

            if (row.AskImbalance)
                FootprintCanvas.Children.Add(FpPlace(new System.Windows.Shapes.Rectangle
                {
                    Width = halfW, Height = FpRowHeight,
                    Stroke = FpAskImbPen, StrokeThickness = 1.1, Fill = Brushes.Transparent,
                }, x + 1 + halfW, y));
            if (row.BidImbalance)
                FootprintCanvas.Children.Add(FpPlace(new System.Windows.Shapes.Rectangle
                {
                    Width = halfW, Height = FpRowHeight,
                    Stroke = FpBidImbPen, StrokeThickness = 1.1, Fill = Brushes.Transparent,
                }, x + 1, y));

            if (!double.IsNaN(bar.PocPrice) && Math.Abs(row.Price - bar.PocPrice) < 1e-9)
                FootprintCanvas.Children.Add(FpPlace(new System.Windows.Shapes.Rectangle
                {
                    Width = FpColWidth - 2, Height = FpRowHeight,
                    Stroke = FpPocPen, StrokeThickness = 1.2, Fill = Brushes.Transparent,
                }, x + 1, y));
        }

        var fy = FpHeaderHeight + rowCount * FpRowHeight;
        var deltaBrush = bar.Delta >= 0 ? BullBrush : BearBrush;
        AddFpText($"Δ {bar.Delta:+#;-#;0}  Σ {Compact(bar.TotalVolume)}",
            x, fy + 1, FpColWidth, 14, deltaBrush, 9.5, TextAlignment.Center);
    }

    private void AddFpCellHalf(double x, double y, double w, long vol, long maxVol, SolidColorBrush baseBrush, bool isLeft)
    {
        if (vol <= 0) return;
        var alpha = (byte)(36 + 170.0 * Math.Min(1.0, (double)vol / maxVol));
        var c = baseBrush.Color;
        var fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
        fill.Freeze();
        FootprintCanvas.Children.Add(FpPlace(
            new System.Windows.Shapes.Rectangle { Width = w, Height = FpRowHeight, Fill = fill }, x, y));
        AddFpText(Compact(vol), x, y, w - 3, FpRowHeight, FpTextBrush, 9,
            isLeft ? TextAlignment.Right : TextAlignment.Left, leftPad: isLeft ? 0 : 3);
    }

    private void AddFpText(string text, double x, double y, double w, double h, Brush brush, double size,
        TextAlignment align, double leftPad = 0)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontSize = size,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Width = w,
            Height = h,
            TextAlignment = align,
            Padding = new Thickness(leftPad, 0, 3, 0),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y + (h - size - 4) / 2.0);
        FootprintCanvas.Children.Add(tb);
    }

    private static UIElement FpPlace(UIElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        return el;
    }

    private static string Compact(long v)
    {
        var a = Math.Abs(v);
        return a >= 1_000_000 ? $"{v / 1e6:0.#}M" : a >= 10_000 ? $"{v / 1e3:0.#}K" : v.ToString("N0");
    }

    private static int PriceDecimals(List<double> rowsDescending)
    {
        var minDiff = double.MaxValue;
        for (var i = 1; i < rowsDescending.Count; i++)
        {
            var d = rowsDescending[i - 1] - rowsDescending[i];
            if (d > 1e-9 && d < minDiff) minDiff = d;
        }
        if (minDiff == double.MaxValue) return 2;
        var decimals = 0;
        while (minDiff < 0.999 && decimals < 5) { minDiff *= 10; decimals++; }
        return decimals;
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_vm is null) return;
        _vm.BarsChanged      -= OnBarsChanged;
        _vm.DeltasChanged    -= OnDeltasChanged;
        _vm.FootprintChanged -= OnFootprintChanged;
        await _vm.StopStreamAsync();
        _vm.Dispose();
    }
}
