using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using TradingTerminal.UI;

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

    /// <summary>Latest [NumSlices, NumLevels] surface. Row 0 oldest, last row current.
    /// The Window subscribes to <see cref="SurfaceChanged"/> to redraw.</summary>
    public double[,]? Surface { get; private set; }

    public event EventHandler? SurfaceChanged;

    private ImbalanceHeatFrontCalculator? _calc;

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
            ? ImbalanceHeatFrontStrategy.RidgeMode.Momentum
            : ImbalanceHeatFrontStrategy.RidgeMode.MeanReversion;

        return new ImbalanceHeatFrontStrategy(
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
        return Task.CompletedTask;
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

        Surface = _calc.GetSurface();
        SurfaceChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string TrendArrow(int trend) => trend switch
    {
        +1 => "↑ growing",
        -1 => "↓ shrinking",
        _ => "→ steady",
    };
}
