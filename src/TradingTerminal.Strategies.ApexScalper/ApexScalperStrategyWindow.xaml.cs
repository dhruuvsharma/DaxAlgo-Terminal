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
    private static readonly SolidColorBrush WickStroke = new(Color.FromRgb(220, 220, 220));

    private readonly LadderRow[] _askRows = new LadderRow[LadderDepth];
    private readonly LadderRow[] _bidRows = new LadderRow[LadderDepth];
    private TextBlock? _spreadLabel;

    private Line? _candleWick;
    private Rectangle? _candleBody;

    private readonly DispatcherTimer _redrawTimer;
    private bool _chartDirty;

    public ApexScalperStrategyWindow()
    {
        InitializeComponent();
        BuildLadder();
        BuildLiveCandleVisual();

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
        LiveCandleCanvas.SizeChanged += (_, _) => { _chartDirty = true; };
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
        // "Max candles" is render-only — repaint on the next timer tick when it changes.
        else if (e.PropertyName == nameof(ApexScalperStrategyViewModel.MaxChartCandles))
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
        RenderLiveCandle(engine?.LiveCandle);
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

    // ── Live candle ────────────────────────────────────────────────────────────────

    private void BuildLiveCandleVisual()
    {
        _candleWick = new Line
        {
            Stroke = WickStroke,
            StrokeThickness = 1.5,
        };
        LiveCandleCanvas.Children.Add(_candleWick);

        _candleBody = new Rectangle
        {
            Fill = BullCandleFill,
            Stroke = WickStroke,
            StrokeThickness = 1,
            Width = 22,
        };
        LiveCandleCanvas.Children.Add(_candleBody);
    }

    private void RenderLiveCandle(Engine.ApexLiveCandle? candle)
    {
        if (candle is null || _candleWick is null || _candleBody is null)
        {
            ClearLiveCandle();
            return;
        }

        var w = LiveCandleCanvas.ActualWidth;
        var h = LiveCandleCanvas.ActualHeight;
        if (w <= 0 || h <= 0) return;

        // Map [Low, High] vertically onto [h-2, 2] (Y grows downward in WPF Canvas).
        double range = Math.Max(candle.High - candle.Low, 1e-9);
        double topPad = 4, bottomPad = 4;
        double drawH = Math.Max(h - topPad - bottomPad, 1);
        double Y(double price) => topPad + (1.0 - (price - candle.Low) / range) * drawH;

        var cx = w * 0.5;
        _candleWick.X1 = cx; _candleWick.X2 = cx;
        _candleWick.Y1 = Y(candle.High);
        _candleWick.Y2 = Y(candle.Low);

        var bullish = candle.Close >= candle.Open;
        _candleBody.Fill = bullish ? BullCandleFill : BearCandleFill;
        var bodyTop = Y(Math.Max(candle.Open, candle.Close));
        var bodyBottom = Y(Math.Min(candle.Open, candle.Close));
        _candleBody.Width = Math.Min(w - 8, 22);
        _candleBody.Height = Math.Max(bodyBottom - bodyTop, 1);
        Canvas.SetLeft(_candleBody, cx - _candleBody.Width * 0.5);
        Canvas.SetTop(_candleBody, bodyTop);

        // Stats
        LiveOpen.Text = candle.Open.ToString("F5");
        LiveHigh.Text = candle.High.ToString("F5");
        LiveLow.Text = candle.Low.ToString("F5");
        LiveClose.Text = candle.Close.ToString("F5");
        LiveVolume.Text = candle.Volume.ToString("N0");
        LiveDelta.Text = candle.Delta.ToString("+0;-0;0");
        LiveDelta.Foreground = candle.Delta switch
        {
            > 0 => BullCandleFill,
            < 0 => BearCandleFill,
            _ => (Brush?)TryFindResource("Text.Primary") ?? Brushes.Gainsboro,
        };
        LiveBuySell.Text = $"{candle.BuyVolume:N0} / {candle.SellVolume:N0}";
        LiveDeltaEff.Text = candle.DeltaEfficiency.ToString("F2");

        var elapsed = DateTime.UtcNow - candle.OpenTimeUtc;
        if (elapsed.TotalSeconds < 0) elapsed = TimeSpan.Zero;
        LiveCandleStatus.Text = $"{candle.OpenTimeUtc:HH:mm:ss}  +{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}";
    }

    private void ClearLiveCandle()
    {
        if (_candleWick is not null) { _candleWick.X1 = _candleWick.X2 = 0; _candleWick.Y1 = _candleWick.Y2 = 0; }
        if (_candleBody is not null) { _candleBody.Width = 0; _candleBody.Height = 0; }
        LiveOpen.Text = LiveHigh.Text = LiveLow.Text = LiveClose.Text = "—";
        LiveVolume.Text = LiveDelta.Text = LiveBuySell.Text = LiveDeltaEff.Text = "—";
        LiveCandleStatus.Text = "awaiting tick";
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
