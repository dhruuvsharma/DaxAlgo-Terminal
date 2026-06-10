using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ScottPlot.WPF;
using TradingTerminal.Core.Domain;
using TradingTerminal.UI;
using Engine = TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

public partial class ApexScalperStrategyWindow : StrategyWindowBase
{
    private const int LadderDepth = 10;
    private const double LadderBarMaxWidth = 90.0;

    /// <summary>Composite score range mapped onto the gauge track. The score itself is a
    /// weighted average that rarely exceeds ±2 — ±3 leaves headroom without making the centre
    /// look squashed.</summary>
    private const double GaugeRange = 3.0;

    private static readonly SolidColorBrush AskFull = new(Color.FromRgb(200, 50, 50));
    private static readonly SolidColorBrush AskDim = new(Color.FromRgb(110, 25, 25));
    private static readonly SolidColorBrush BidFull = new(Color.FromRgb(0, 160, 130));
    private static readonly SolidColorBrush BidDim = new(Color.FromRgb(0, 80, 65));

    private static readonly SolidColorBrush BullCandleFill = new(Color.FromRgb(0, 200, 83));
    private static readonly SolidColorBrush BearCandleFill = new(Color.FromRgb(255, 23, 68));

    private readonly LadderRow[] _askRows = new LadderRow[LadderDepth];
    private readonly LadderRow[] _bidRows = new LadderRow[LadderDepth];
    private TextBlock? _spreadLabel;

    private readonly DispatcherTimer _redrawTimer;
    private bool _chartDirty;

    public ApexScalperStrategyWindow()
    {
        InitializeComponent();
        BuildLadder();

        _redrawTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            // ~30 FPS — fast enough to feel live, slow enough not to swamp ScottPlot when ticks
            // arrive at hundreds-per-second on liquid FX.
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _redrawTimer.Tick += OnRedrawTimer;
        Loaded += (_, _) => _redrawTimer.Start();
        Closed += (_, _) => _redrawTimer.Stop();

        // Re-render the gauge when the track resizes so the needle stays anchored.
        GaugeTrackHost.SizeChanged += (_, _) => { _chartDirty = true; };
    }

    protected override IEnumerable<WpfPlot> ChartHosts => new[]
    {
        DeltaPlot, VpinPlot,
        ObiShallowPlot, ObiDeepPlot,
        FootprintPlot, AbsorptionPlot,
        HvpPlot, TapeSpeedPlot,
    };

    /// <summary>Re-render the order book whenever LatestDepth changes; everything else
    /// piggy-backs on the redraw timer.</summary>
    protected override void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        base.OnVmPropertyChanged(sender, e);
        if (sender is not LiveSignalStrategyViewModelBase vm) return;
        if (e.PropertyName == nameof(LiveSignalStrategyViewModelBase.LatestDepth))
            RenderOrderBook(vm.LatestDepth);
        // "Max candles" / "FP bars" are render-only — repaint on the next timer tick when they change.
        else if (e.PropertyName is nameof(ApexScalperStrategyViewModel.MaxChartCandles)
                              or nameof(ApexScalperStrategyViewModel.FootprintBarsVisible))
            _chartDirty = true;
    }

    protected override void OnRedrawCharts(LiveSignalStrategyViewModelBase baseVm)
    {
        EnsureTickSubscription(baseVm);
        if (baseVm is not ApexScalperStrategyViewModel vm) return;
        var engine = vm.EngineStrategy;
        var history = engine?.History;
        var maxN = Math.Max(10, vm.MaxChartCandles);

        // Build the tail: trimmed history + the live Latest snapshot appended so the rightmost
        // point on each indicator chart moves every tick. If Latest is already in history
        // (snapshot frozen after a candle roll) skip the append.
        Engine.ApexSnapshot[] tail;
        if (history is null || history.Count == 0)
        {
            tail = engine?.Latest is { } latestOnly ? new[] { latestOnly } : Array.Empty<Engine.ApexSnapshot>();
        }
        else
        {
            var live = engine?.Latest;
            var liveIsTailOfHistory = live is not null && ReferenceEquals(live, history[^1]);
            var historyTake = liveIsTailOfHistory ? maxN : Math.Max(0, maxN - 1);
            var trimmed = history.Skip(Math.Max(0, history.Count - historyTake)).ToArray();

            if (live is null || liveIsTailOfHistory)
                tail = trimmed;
            else
            {
                tail = new Engine.ApexSnapshot[trimmed.Length + 1];
                Array.Copy(trimmed, tail, trimmed.Length);
                tail[^1] = live;
            }
        }

        DrawSeries(DeltaPlot, tail, s => s.Delta.Score, signed: true);
        DrawSeries(VpinPlot, tail, s => s.Vpin.Score, signed: true);
        DrawSeries(ObiShallowPlot, tail, s => s.ObiShallow.Score, signed: true);
        DrawSeries(ObiDeepPlot, tail, s => s.ObiDeep.Score, signed: true);
        DrawSeries(FootprintPlot, tail, s => s.Footprint.Score, signed: true);
        DrawSeries(AbsorptionPlot, tail, s => s.Absorption.Score, signed: true);
        DrawSeries(HvpPlot, tail, s => s.Hvp.Score, signed: true);
        DrawSeries(TapeSpeedPlot, tail, s => s.TapeSpeed.Score, signed: true);

        RenderOrderBook(baseVm.LatestDepth);
        RenderGauge(engine?.Latest?.Composite, vm.CompositeThreshold);
        RenderFootprint(engine, vm.FootprintBarsVisible, vm.CompositeThreshold);
    }

    private LiveSignalStrategyViewModelBase? _tickVm;

    private void EnsureTickSubscription(LiveSignalStrategyViewModelBase vm)
    {
        if (ReferenceEquals(_tickVm, vm)) return;
        if (_tickVm is not null) _tickVm.TickProcessed -= OnTickProcessed;
        _tickVm = vm;
        _tickVm.TickProcessed += OnTickProcessed;
    }

    private void OnTickProcessed(object? sender, EventArgs e) => _chartDirty = true;

    private void OnRedrawTimer(object? sender, EventArgs e)
    {
        if (!_chartDirty || _tickVm is null) return;
        _chartDirty = false;
        OnRedrawCharts(_tickVm);
    }

    /// <summary>Draws a single time-series. When <paramref name="signed"/> is true,
    /// adds a dotted zero-line.</summary>
    private static void DrawSeries(WpfPlot host, IReadOnlyList<Engine.ApexSnapshot> tail,
        Func<Engine.ApexSnapshot, double> pick, bool signed)
    {
        var plot = host.Plot;
        plot.Clear();
        if (tail.Count == 0) { host.Refresh(); return; }

        var xs = new double[tail.Count];
        var ys = new double[tail.Count];
        for (var i = 0; i < tail.Count; i++)
        {
            xs[i] = tail[i].TimestampUtc.ToOADate();
            ys[i] = pick(tail[i]);
        }
        var scatter = plot.Add.Scatter(xs, ys);
        scatter.Color = StrategyChartHelpers.AccentColor;
        scatter.LineWidth = 1.5f;
        scatter.MarkerStyle.IsVisible = false;

        if (signed)
        {
            var zero = plot.Add.HorizontalLine(0, color: StrategyChartHelpers.MutedColor);
            zero.LineStyle.Pattern = ScottPlot.LinePattern.Dotted;
        }

        plot.Axes.DateTimeTicksBottom();
        plot.Axes.AutoScale();
        host.Refresh();
    }

    // ── Composite sentiment gauge ──────────────────────────────────────────────────

    private void RenderGauge(double? composite, double threshold)
    {
        var trackWidth = GaugeTrackHost.ActualWidth;
        if (trackWidth <= 0) return;

        if (composite is null)
        {
            GaugeLabel.Text = "composite —";
            Canvas.SetLeft(GaugeNeedle, trackWidth * 0.5);
        }
        else
        {
            var score = composite.Value;
            GaugeLabel.Text = $"composite {score:+0.00;-0.00;0.00}";
            var normalised = Math.Clamp((score + GaugeRange) / (2.0 * GaugeRange), 0.0, 1.0);
            Canvas.SetLeft(GaugeNeedle, normalised * trackWidth);
        }

        // Threshold ticks: short vertical lines at ±threshold on the track.
        GaugeTicks.Children.Clear();
        if (threshold > 0 && threshold < GaugeRange)
        {
            var leftPos = (-threshold + GaugeRange) / (2.0 * GaugeRange) * trackWidth;
            var rightPos = (threshold + GaugeRange) / (2.0 * GaugeRange) * trackWidth;
            AddTick(leftPos);
            AddTick(rightPos);
        }
    }

    private void AddTick(double x)
    {
        var line = new Line
        {
            X1 = x, X2 = x,
            Y1 = 0, Y2 = 16,
            Stroke = Brushes.White,
            StrokeThickness = 1,
            Opacity = 0.6,
        };
        GaugeTicks.Children.Add(line);
    }

    // ── Footprint cluster ──────────────────────────────────────────────────────────
    // Rendering convention copied from VolumeFootprintWindow (sell|buy halves per price level,
    // volume-scaled alpha, POC outline, shared price axis), then customised for the strategy:
    // the data is the engine's own tick-rule footprint (what the Footprint signal scores), cells
    // flagged by the engine's 3:1 diagonal imbalance rule get a bright outline, and each bar's
    // footer carries Δ / Σ plus the composite it closed on and a ▲/▼ marker where the trade
    // gate actually fired.

    private const double FpAxisWidth = 64;
    private const double FpColWidth = 96;
    private const double FpRowHeight = 15;
    private const double FpHeaderHeight = 18;
    private const double FpFooterHeight = 46;

    private static readonly SolidColorBrush FpBuyBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));
    private static readonly SolidColorBrush FpSellBrush = new(Color.FromRgb(0xC6, 0x28, 0x28));
    private static readonly SolidColorBrush FpPocPen = new(Color.FromRgb(0xFF, 0xD5, 0x4F));
    private static readonly SolidColorBrush FpGridPen = new(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
    private static readonly SolidColorBrush FpTextBrush = new(Color.FromRgb(0xE0, 0xE0, 0xE0));
    private static readonly SolidColorBrush FpDimText = new(Color.FromRgb(0x9E, 0x9E, 0x9E));
    private static readonly SolidColorBrush FpAskImbPen = new(Color.FromRgb(0x00, 0xE6, 0x76));
    private static readonly SolidColorBrush FpBidImbPen = new(Color.FromRgb(0xFF, 0x52, 0x52));
    private static readonly SolidColorBrush FpLiveHeader = new(Color.FromRgb(0x29, 0xB6, 0xF6));

    static ApexScalperStrategyWindow()
    {
        foreach (var b in new Brush[]
        {
            FpBuyBrush, FpSellBrush, FpPocPen, FpGridPen, FpTextBrush, FpDimText,
            FpAskImbPen, FpBidImbPen, FpLiveHeader,
        })
            b.Freeze();
    }

    private void RenderFootprint(Engine.ApexScalperStrategy? engine, int visibleBars, double threshold)
    {
        ApexFootprintCanvas.Children.Clear();
        var all = engine?.FootprintBars;
        if (all is null || all.Count == 0)
        {
            ApexFootprintCanvas.Width = 0;
            ApexFootprintCanvas.Height = 0;
            FootprintStatus.Text = "awaiting ticks";
            return;
        }

        var skip = Math.Max(0, all.Count - Math.Max(4, visibleBars));
        var bars = new List<Engine.ApexFootprintBar>(all.Count - skip);
        for (var i = skip; i < all.Count; i++) bars.Add(all[i]);

        // Shared price axis: union of every traded level across the visible bars, high → low.
        var prices = new SortedSet<double>();
        long maxCellVol = 1;
        foreach (var bar in bars)
            foreach (var row in bar.Rows)
            {
                prices.Add(row.Price);
                var total = row.BuyVolume + row.SellVolume;
                if (total > maxCellVol) maxCellVol = total;
            }
        var rows = prices.Reverse().ToList();
        var rowIndex = new Dictionary<double, int>(rows.Count);
        for (var i = 0; i < rows.Count; i++) rowIndex[rows[i]] = i;

        var decimals = FpDecimals(rows);

        ApexFootprintCanvas.Width = FpAxisWidth + bars.Count * FpColWidth;
        ApexFootprintCanvas.Height = FpHeaderHeight + rows.Count * FpRowHeight + FpFooterHeight;

        for (var r = 0; r < rows.Count; r++)
            AddFpText(rows[r].ToString("N" + decimals, System.Globalization.CultureInfo.InvariantCulture),
                0, FpHeaderHeight + r * FpRowHeight, FpAxisWidth - 6, FpRowHeight, FpDimText, 10, TextAlignment.Right);

        for (var b = 0; b < bars.Count; b++)
            DrawFootprintBar(bars[b], b, rowIndex, rows.Count, maxCellVol);

        var lastCompleted = bars.LastOrDefault(x => !x.IsLive);
        FootprintStatus.Text = lastCompleted is null
            ? $"{bars.Count} bars"
            : $"{bars.Count} bars · stack ↑{lastCompleted.StackedBull} ↓{lastCompleted.StackedBear} · gate ±{threshold:0.00}";

        FootprintScroll.ScrollToRightEnd();
    }

    private void DrawFootprintBar(Engine.ApexFootprintBar bar, int colIndex,
        IReadOnlyDictionary<double, int> rowIndex, int rowCount, long maxCellVol)
    {
        var x = FpAxisWidth + colIndex * FpColWidth;

        AddFpText(bar.IsLive ? bar.StartUtc.ToLocalTime().ToString("HH:mm:ss") + " •" : bar.StartUtc.ToLocalTime().ToString("HH:mm:ss"),
            x, 0, FpColWidth, FpHeaderHeight, bar.IsLive ? FpLiveHeader : FpDimText, 10, TextAlignment.Center);

        var halfW = (FpColWidth - 2) / 2.0;
        foreach (var row in bar.Rows)
        {
            if (!rowIndex.TryGetValue(row.Price, out var r)) continue;
            var y = FpHeaderHeight + r * FpRowHeight;

            AddFpCellHalf(x + 1, y, halfW, row.SellVolume, maxCellVol, FpSellBrush, isLeft: true);
            AddFpCellHalf(x + 1 + halfW, y, halfW, row.BuyVolume, maxCellVol, FpBuyBrush, isLeft: false);

            // Strategy's 3:1 diagonal imbalances — the raw input to the stacked-imbalance signal.
            if (row.AskImbalance)
                ApexFootprintCanvas.Children.Add(FpPlace(new Rectangle
                {
                    Width = halfW, Height = FpRowHeight,
                    Stroke = FpAskImbPen, StrokeThickness = 1.2, Fill = Brushes.Transparent,
                }, x + 1 + halfW, y));
            if (row.BidImbalance)
                ApexFootprintCanvas.Children.Add(FpPlace(new Rectangle
                {
                    Width = halfW, Height = FpRowHeight,
                    Stroke = FpBidImbPen, StrokeThickness = 1.2, Fill = Brushes.Transparent,
                }, x + 1, y));

            var isPoc = !double.IsNaN(bar.PocPrice) && Math.Abs(row.Price - bar.PocPrice) < 1e-9;
            if (isPoc)
                ApexFootprintCanvas.Children.Add(FpPlace(new Rectangle
                {
                    Width = FpColWidth - 2, Height = FpRowHeight,
                    Stroke = FpPocPen, StrokeThickness = 1.3, Fill = Brushes.Transparent,
                }, x + 1, y));
        }

        // Footer: Δ / Σ, the composite the bar closed on, and the fired-signal marker.
        var fy = FpHeaderHeight + rowCount * FpRowHeight;
        ApexFootprintCanvas.Children.Add(new Line
        {
            X1 = x, Y1 = fy + 1, X2 = x + FpColWidth, Y2 = fy + 1,
            Stroke = FpGridPen, StrokeThickness = 1,
        });

        var deltaBrush = bar.Delta >= 0 ? BullCandleFill : BearCandleFill;
        AddFpText($"Δ {bar.Delta:+#;-#;0}  Σ {FpCompact(bar.TotalVolume)}",
            x, fy + 2, FpColWidth, 14, deltaBrush, 10, TextAlignment.Center);

        if (bar.IsLive)
        {
            AddFpText("forming…", x, fy + 16, FpColWidth, 14, FpDimText, 10, TextAlignment.Center);
        }
        else
        {
            var compBrush = bar.Composite > 0 ? BullCandleFill : bar.Composite < 0 ? BearCandleFill : FpDimText;
            var marker = bar.SignalDirection > 0 ? "  ▲ LONG" : bar.SignalDirection < 0 ? "  ▼ SHORT" : string.Empty;
            AddFpText($"C {bar.Composite:+0.00;-0.00;0.00}{marker}",
                x, fy + 16, FpColWidth, 14,
                bar.SignalDirection != 0 ? compBrush : FpDimText, 10, TextAlignment.Center);
        }
    }

    private void AddFpCellHalf(double x, double y, double w, long vol, long maxVol, SolidColorBrush baseBrush, bool isLeft)
    {
        if (vol <= 0) return;
        var alpha = (byte)(36 + 170.0 * Math.Min(1.0, (double)vol / maxVol));
        var c = baseBrush.Color;
        var fill = new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B));
        fill.Freeze();
        ApexFootprintCanvas.Children.Add(FpPlace(new Rectangle { Width = w, Height = FpRowHeight, Fill = fill }, x, y));
        AddFpText(FpCompact(vol), x, y, w - 3, FpRowHeight, FpTextBrush, 9.5,
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
            FontFamily = new FontFamily("Consolas"),
            Width = w,
            Height = h,
            TextAlignment = align,
            Padding = new Thickness(leftPad, 0, 3, 0),
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y + (h - size - 4) / 2.0);
        ApexFootprintCanvas.Children.Add(tb);
    }

    private static UIElement FpPlace(UIElement el, double x, double y)
    {
        Canvas.SetLeft(el, x);
        Canvas.SetTop(el, y);
        return el;
    }

    private static string FpCompact(long v)
    {
        var a = Math.Abs(v);
        return a >= 1_000_000 ? $"{v / 1e6:0.#}M" : a >= 10_000 ? $"{v / 1e3:0.#}K" : v.ToString("N0");
    }

    /// <summary>Decimals for the price axis: derived from the smallest gap between traded levels
    /// (the engine buckets at 5 decimals, so this lands on 2 for equities, 4-5 for FX).</summary>
    private static int FpDecimals(List<double> rowsDescending)
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

    // ── Order book ladder ──────────────────────────────────────────────────────────

    /// <summary>Pre-builds the 21 ladder rows (10 asks, 1 spread, 10 bids) once. Updates on
    /// depth changes mutate the existing Rectangle widths + label text rather than tearing
    /// down the visual tree.</summary>
    private void BuildLadder()
    {
        // Asks rendered top-down, tightest at the bottom (closest to the spread row).
        for (var i = LadderDepth - 1; i >= 0; i--)
        {
            var row = new LadderRow(AskDim);
            _askRows[i] = row;
            OrderBookLadder.Children.Add(row.Root);
        }

        var spread = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 25, 40)),
            Padding = new Thickness(6, 2, 6, 2),
            Height = 18,
        };
        _spreadLabel = new TextBlock
        {
            Text = "—",
            FontFamily = (FontFamily?)TryFindResource("Font.Mono") ?? new FontFamily("Consolas"),
            FontSize = 11,
            Foreground = (Brush?)TryFindResource("Text.Header") ?? Brushes.Orange,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        spread.Child = _spreadLabel;
        OrderBookLadder.Children.Add(spread);

        for (var i = 0; i < LadderDepth; i++)
        {
            var row = new LadderRow(BidDim);
            _bidRows[i] = row;
            OrderBookLadder.Children.Add(row.Root);
        }
    }

    private void RenderOrderBook(DepthSnapshot? depth)
    {
        if (depth is null || (depth.Bids.Count == 0 && depth.Asks.Count == 0))
        {
            for (var i = 0; i < LadderDepth; i++)
            {
                _askRows[i].Clear();
                _bidRows[i].Clear();
            }
            if (_spreadLabel is not null) _spreadLabel.Text = "—";
            OrderBookStatus.Text = "awaiting depth";
            return;
        }

        long maxSize = 1;
        long largestAskIdx = -1; long largestBidIdx = -1;
        long maxAskSize = 0; long maxBidSize = 0;
        for (var i = 0; i < Math.Min(depth.Asks.Count, LadderDepth); i++)
        {
            var size = depth.Asks[i].Size;
            if (size > maxSize) maxSize = size;
            if (size > maxAskSize) { maxAskSize = size; largestAskIdx = i; }
        }
        for (var i = 0; i < Math.Min(depth.Bids.Count, LadderDepth); i++)
        {
            var size = depth.Bids[i].Size;
            if (size > maxSize) maxSize = size;
            if (size > maxBidSize) { maxBidSize = size; largestBidIdx = i; }
        }

        for (var i = 0; i < LadderDepth; i++)
        {
            if (i < depth.Asks.Count && depth.Asks[i].Size > 0)
            {
                var level = depth.Asks[i];
                var widthFraction = (double)level.Size / maxSize;
                _askRows[i].Set(level.Price, level.Size, widthFraction * LadderBarMaxWidth,
                    i == largestAskIdx ? AskFull : AskDim);
            }
            else
            {
                _askRows[i].Clear();
            }
        }

        for (var i = 0; i < LadderDepth; i++)
        {
            if (i < depth.Bids.Count && depth.Bids[i].Size > 0)
            {
                var level = depth.Bids[i];
                var widthFraction = (double)level.Size / maxSize;
                _bidRows[i].Set(level.Price, level.Size, widthFraction * LadderBarMaxWidth,
                    i == largestBidIdx ? BidFull : BidDim);
            }
            else
            {
                _bidRows[i].Clear();
            }
        }

        var bestAsk = depth.Asks.Count > 0 ? (double?)depth.Asks[0].Price : null;
        var bestBid = depth.Bids.Count > 0 ? (double?)depth.Bids[0].Price : null;
        if (bestBid is double bb && bestAsk is double ba)
        {
            var mid = (bb + ba) * 0.5;
            var spread = ba - bb;
            if (_spreadLabel is not null) _spreadLabel.Text = $"mid {mid:F5}  spr {spread:F5}";
            OrderBookStatus.Text = $"L={depth.Asks.Count + depth.Bids.Count}  ts {depth.TimestampUtc:HH:mm:ss}";
        }
        else
        {
            if (_spreadLabel is not null) _spreadLabel.Text = "—";
            OrderBookStatus.Text = $"L={depth.Asks.Count + depth.Bids.Count}";
        }
    }

    /// <summary>One row of the depth ladder — a bar (Rectangle) growing leftward from the left
    /// edge plus a price + size label on the right. Pre-allocated; rendering only mutates
    /// width / fill / text.</summary>
    private sealed class LadderRow
    {
        public Grid Root { get; }
        private readonly Rectangle _bar;
        private readonly TextBlock _label;

        public LadderRow(Brush dimBrush)
        {
            Root = new Grid { Height = 16, Margin = new Thickness(0, 1, 0, 0) };
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LadderBarMaxWidth, GridUnitType.Pixel) });
            Root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _bar = new Rectangle
            {
                Fill = dimBrush,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 0,
            };
            Grid.SetColumn(_bar, 0);
            Root.Children.Add(_bar);

            _label = new TextBlock
            {
                FontFamily = new FontFamily("Consolas"),
                FontSize = 10,
                Foreground = Brushes.Gainsboro,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0),
                Text = string.Empty,
            };
            Grid.SetColumn(_label, 1);
            Root.Children.Add(_label);
        }

        public void Set(double price, long size, double barWidth, Brush brush)
        {
            _bar.Width = Math.Max(0, Math.Min(LadderBarMaxWidth, barWidth));
            _bar.Fill = brush;
            _label.Text = $"{price:F5}  {size}";
        }

        public void Clear()
        {
            _bar.Width = 0;
            _label.Text = string.Empty;
        }
    }
}
