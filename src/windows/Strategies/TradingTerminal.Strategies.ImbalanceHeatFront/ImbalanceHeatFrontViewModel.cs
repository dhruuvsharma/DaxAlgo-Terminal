using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.UI;
using EngineStrategy = TradingTerminal.Strategies.ImbalanceHeatFront.Engine.ImbalanceHeatFrontStrategy;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

/// <summary>
/// Live VM for the Imbalance Heat Front strategy. Subclasses
/// <see cref="LiveSignalStrategyViewModelBase"/> for the standard instrument-picker /
/// quote-and-depth wiring; adds a <see cref="ImbalanceHeatFrontCalculator"/> driven from the
/// base's LatestDepth property for the 3D surface viz, plus a depth-capability check.
/// </summary>
public sealed partial class ImbalanceHeatFrontViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _numLevels = 5;
    [ObservableProperty] private int _numSlices = 30;
    [ObservableProperty] private int _eventsPerSlice = 10;
    [ObservableProperty] private double _ridgeThreshold = 0.75;
    [ObservableProperty] private int _ridgeWidth = 3;
    [ObservableProperty] private int _confirmationSlices = 2;
    [ObservableProperty] private int _ticksPerSlice = 20;
    [ObservableProperty] private bool _momentumMode = true;
    [ObservableProperty] private long _quantity = 1;
    [ObservableProperty] private double _stopLossPips = 2;
    [ObservableProperty] private double _takeProfitPips = 4;

    [ObservableProperty] private long _depthEventsSeen;
    [ObservableProperty] private int _ridgeSide;
    [ObservableProperty] private double _ridgeHeight;
    [ObservableProperty] private int _ridgeTrend;
    [ObservableProperty] private int _ridgeStartLevel;
    [ObservableProperty] private int _ridgeWidthDisplay;
    [ObservableProperty] private double _nearTouchImbalance;
    [ObservableProperty] private string _ridgeLabel = "—";

    /// <summary>Vertical exaggeration of the imbalance (Z) axis on the 3D surface. Render-only —
    /// it never touches strategy state, so it's live-editable while streaming. The Window reads it
    /// when building the mesh; changing it re-raises <see cref="SurfaceChanged"/> to redraw.</summary>
    [ObservableProperty] private double _surfaceHeightScale = 1.6;

    partial void OnSurfaceHeightScaleChanged(double value) => RaiseSurfaceChanged();

    /// <summary>Latest [NumSlices, NumLevels] surface. Row 0 oldest, last row current.
    /// The Window subscribes to <see cref="SurfaceChanged"/> to redraw.</summary>
    public double[,]? Surface { get; private set; }

    public event EventHandler? SurfaceChanged;

    /// <summary>Set when a surface redraw was suppressed by the display pause.</summary>
    private bool _surfaceDirty;

    /// <summary>Bespoke render event, gated on the shared display pause like the base's
    /// BarsChanged — the calculator keeps updating underneath, resume replays one redraw.</summary>
    private void RaiseSurfaceChanged()
    {
        if (IsPaused) { _surfaceDirty = true; return; }
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPauseReleased()
    {
        if (!_surfaceDirty) return;
        _surfaceDirty = false;
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private ImbalanceHeatFrontCalculator? _calc;
    private CancellationTokenSource? _watchdogCts;

    public ImbalanceHeatFrontViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<ImbalanceHeatFrontViewModel> logger)
        : base(
            strategyId: "imbalance.heatfront",
            strategyDisplayName: "Imbalance Heat Front (L2 bid-ask pressure surface)",
            services, notifications, clock, routerFactory, logger)
    {
        // LatestDepth's [ObservableProperty] partial-method hook is generated on the base class,
        // so subclasses can't intercept via `partial void OnLatestDepthChanged`. Subscribe via
        // PropertyChanged instead — same effect, fired on the UI thread by the base's depth pump.
        PropertyChanged += OnPropertyChangedHandler;
    }

    private void OnPropertyChangedHandler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LatestDepth))
            OnDepthSnapshot(LatestDepth);
    }

    /// <summary>Brokers that expose L2 depth on this build. Alpaca throws
    /// <c>NotSupportedException</c>; NinjaTrader's level2 isn't wired through the canonical
    /// pipeline today. IB (reqMktDepth) and cTrader (ProtoOADepthEvent) deliver real snapshots.</summary>
    private static bool BrokerSupportsDepth(BrokerKind broker) => broker switch
    {
        BrokerKind.InteractiveBrokers => true,
        BrokerKind.CTrader => true,
        _ => false,
    };

    protected override IBacktestStrategy BuildStrategy(Contract contract)
    {
        _calc = new ImbalanceHeatFrontCalculator(
            numLevels: NumLevels,
            numSlices: NumSlices,
            eventsPerSlice: EventsPerSlice,
            ridgeThreshold: RidgeThreshold,
            ridgeWidth: RidgeWidth);

        // Reset display state when (re)starting.
        DepthEventsSeen = 0;
        RidgeSide = 0; RidgeHeight = 0; RidgeTrend = 0;
        RidgeStartLevel = 0; RidgeWidthDisplay = 0;
        NearTouchImbalance = 0;
        RidgeLabel = "—";
        Surface = null;

        var mode = MomentumMode
            ? EngineStrategy.RidgeMode.Momentum
            : EngineStrategy.RidgeMode.MeanReversion;

        return new EngineStrategy(
            contract,
            numLevels: NumLevels,
            numSlices: NumSlices,
            ridgeThreshold: RidgeThreshold,
            ridgeWidth: RidgeWidth,
            confirmationSlices: ConfirmationSlices,
            ticksPerSlice: TicksPerSlice,
            mode: mode,
            quantity: Quantity,
            stopLossPips: StopLossPips,
            takeProfitPips: TakeProfitPips);
    }

    protected override string? ValidateSetup()
    {
        if (NumLevels < 1 || NumLevels > 20) return "Levels must be 1..20.";
        if (NumSlices < 2) return "Slices must be at least 2.";
        if (EventsPerSlice < 1) return "Events / slice must be ≥ 1.";
        if (RidgeThreshold is <= 0 or > 1) return "Ridge threshold must be in (0, 1].";
        if (RidgeWidth < 1 || RidgeWidth > NumLevels) return "Ridge width must be 1..Levels.";
        if (ConfirmationSlices < 1) return "Confirmation slices must be ≥ 1.";
        if (TicksPerSlice < 1) return "Ticks / slice must be ≥ 1.";
        if (Quantity <= 0) return "Quantity must be positive.";
        if (StopLossPips <= 0 || TakeProfitPips <= 0) return "SL / TP must be positive.";
        return null;
    }

    protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
    {
        if (SelectedInstrument?.Broker is { } broker)
        {
            if (!BrokerSupportsDepth(broker))
            {
                Log("WARN", $"{broker} has no L2 depth on this build — surface will stay empty. Switch to IB or cTrader.");
            }
            else
            {
                Log("INFO", $"L2 depth supported on {broker}; ridge detection armed.");
            }
        }
        Log("INFO", $"Params: levels={NumLevels}, slices={NumSlices}, ridge≥{RidgeThreshold:F2}×{RidgeWidth}, " +
                    $"confirm={ConfirmationSlices}, mode={(MomentumMode ? "Momentum" : "Mean-Reversion")}");

        // Start a "no-depth" watchdog. If 20 seconds elapse with zero depth events, the symbol
        // probably doesn't have L2 from this broker even though the protocol-level subscribe
        // succeeded — print a clear diagnostic. cTrader is the usual culprit: many brokers serve
        // depth for FX majors and headline CFDs but not metals / second-tier instruments.
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();
        _watchdogCts = new CancellationTokenSource();
        _ = WatchForDepthEventsAsync(SelectedInstrument?.DisplayName ?? "instrument",
            SelectedInstrument?.Broker, _watchdogCts.Token);
        return Task.CompletedTask;
    }

    private async Task WatchForDepthEventsAsync(string symbolLabel, BrokerKind? broker, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        var depthCount = DepthEventsSeen;
        var meanAbs = _lastSliceMeanAbs;

        await UiThread.RunAsync(() =>
        {
            if (depthCount == 0)
            {
                Log("WARN", $"No L2 depth events received for {symbolLabel} after 20s — surface won't update.");
                switch (broker)
                {
                    case BrokerKind.CTrader:
                        Log("WARN", "  • cTrader brokers serve L2 selectively. Many serve depth for FX majors + headline CFDs but NOT for metals (XAUUSD/XAGUSD), exotic FX, or second-tier symbols.");
                        Log("WARN", "  • Try a known-good symbol on cTrader (EURUSD, GBPUSD) to verify depth wiring, then check with your broker whether L2 is available for this symbol on your account.");
                        break;
                    case BrokerKind.InteractiveBrokers:
                        Log("WARN", "  • IB requires an explicit Level II / market-depth subscription for this exchange (Account → Market Data Subscriptions).");
                        Log("WARN", "  • Some IB symbols (ETFs / illiquid stocks) also publish very few depth updates — try ES futures or AAPL to verify wiring.");
                        break;
                    default:
                        Log("WARN", "  • This broker doesn't expose L2 in this build. Switch to IB or cTrader.");
                        break;
                }
            }
            else if (meanAbs < 0.05)
            {
                // Depth events ARE arriving but the imbalance values are essentially zero —
                // the book is balanced. The surface IS updating, but every cell rounds to
                // mid-blue so it looks like nothing's happening. This is the most common
                // "graph not updating" report for symbols whose broker provides L2 but where
                // bid and ask sizes are symmetric (XAUUSD on many cTrader brokers behaves this way
                // — the market maker quotes mirror-image sizes on both sides of the book).
                Log("INFO",
                    $"Depth flowing for {symbolLabel}: {depthCount} events in 20s, but imbalance is balanced (|mean|≈{meanAbs:F3} < 0.05).");
                Log("INFO",
                    "  • The surface IS updating — every cell is just rendering near the mid-blue plateau because the bid/ask sizes are roughly symmetric at each level.");
                Log("INFO",
                    $"  • Watch the 'Touch imbalance' readout in the status bar — if it bounces between small values, the strategy is healthy. Lower RidgeThreshold (currently {RidgeThreshold:F2}) to see weaker ridges, or wait for a directional move that breaks the symmetry.");
            }
        });
    }

    // Fires on the UI thread (base's depth pump marshals it). Feeds the calculator and raises
    // SurfaceChanged so the Window redraws. The engine strategy receives the same snapshot via
    // OnDepthAsync in parallel — separate computations, no shared mutable state.
    private void OnDepthSnapshot(DepthSnapshot? value)
    {
        if (value is null || _calc is null) return;

        var result = _calc.OnDepth(value);
        DepthEventsSeen = _calc.DepthEventsSeen;
        NearTouchImbalance = result.NearTouchImbalance;

        RidgeSide = result.Ridge.Side;
        RidgeHeight = result.Ridge.Height;
        RidgeTrend = result.Ridge.Trend;
        RidgeStartLevel = result.Ridge.StartLevel;
        RidgeWidthDisplay = result.Ridge.Width;

        RidgeLabel = result.Ridge.Side switch
        {
            +1 => $"BID ridge h={result.Ridge.Height:F2} w={result.Ridge.Width} {TrendArrow(result.Ridge.Trend)}",
            -1 => $"ASK ridge h={result.Ridge.Height:F2} w={result.Ridge.Width} {TrendArrow(result.Ridge.Trend)}",
            _ => "no ridge",
        };

        // ── Visibility: log first event + every Nth event so the user can SEE depth is flowing.
        // Without this it's impossible to distinguish "no depth events arriving" from "depth
        // events arriving but the book is balanced so imbalance ≈ 0 makes the surface look flat".
        if (DepthEventsSeen == 1)
        {
            Log("DEPTH",
                $"First depth event: bids={value.Bids.Count}, asks={value.Asks.Count}, " +
                $"top bid={value.BestBid:F4}×{value.BestBidSize}, top ask={value.BestAsk:F4}×{value.BestAskSize}, " +
                $"near-touch imbalance={result.NearTouchImbalance:+0.00;-0.00;0.00}");
            _lastSliceMeanAbs = 0;
        }
        else if (DepthEventsSeen % DepthLogInterval == 0)
        {
            var meanAbs = MeanAbs(result.LatestSlice);
            _lastSliceMeanAbs = meanAbs;
            // Two parallel lines: imbalance values, then RAW bid/ask sizes per level. If the
            // broker is sending a mirror-image book (typical for FX/CFD brokers on metals or
            // exotic CFDs), the raw line shows e.g. "bid sizes: 100 100 100 100 100  |  ask
            // sizes: 100 100 100 100 100" and the imbalance line is uniformly 0. That's a
            // data-source characteristic, not a strategy bug.
            Log("DEPTH",
                $"#{DepthEventsSeen,5}  imbalance: " +
                string.Join("  ", result.LatestSlice.Select(v => v.ToString("+0.00;-0.00;0.00"))) +
                $"  |mean|={meanAbs:F3}  ridge={RidgeLabel}");

            var bidSizes = string.Join(" ", Enumerable.Range(0, NumLevels)
                .Select(i => i < value.Bids.Count ? value.Bids[i].Size : 0L)
                .Select(s => s.ToString().PadLeft(6)));
            var askSizes = string.Join(" ", Enumerable.Range(0, NumLevels)
                .Select(i => i < value.Asks.Count ? value.Asks[i].Size : 0L)
                .Select(s => s.ToString().PadLeft(6)));
            Log("DEPTH",
                $"        raw bid sizes: {bidSizes}   |   raw ask sizes: {askSizes}");

            if (meanAbs < 1e-6 && BidsEqualAsks(value, NumLevels))
            {
                Log("INFO",
                    "        → bid sizes IDENTICAL to ask sizes at every level. This broker is publishing a synthetic mirror-image book (common on cTrader for metals / exotic CFDs). Imbalance is mathematically pinned to 0; the surface cannot show a ridge. Try IB for true L2 on metals, or pick a symbol where the broker streams asymmetric depth.");
            }
        }

        Surface = _calc.GetSurface();
        RaiseSurfaceChanged();
    }

    private const int DepthLogInterval = 50;
    private double _lastSliceMeanAbs;

    private static double MeanAbs(double[] slice)
    {
        if (slice.Length == 0) return 0;
        var s = 0.0;
        foreach (var v in slice) s += Math.Abs(v);
        return s / slice.Length;
    }

    private static bool BidsEqualAsks(DepthSnapshot d, int levels)
    {
        var n = Math.Min(levels, Math.Min(d.Bids.Count, d.Asks.Count));
        if (n == 0) return false;
        for (var i = 0; i < n; i++)
        {
            if (d.Bids[i].Size != d.Asks[i].Size) return false;
        }
        return true;
    }

    private static string TrendArrow(int trend) => trend switch
    {
        +1 => "↑ growing",
        -1 => "↓ shrinking",
        _ => "→ steady",
    };
}
