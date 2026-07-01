using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.UI;
using EngineStrategy = TradingTerminal.Strategies.FilteredOrderFlow.Engine.FilteredOrderFlowStrategy;

namespace TradingTerminal.Strategies.FilteredOrderFlow;

/// <summary>
/// Live signal-mode VM for Filtered Order-Flow Imbalance (arXiv:2507.22712). Parameter
/// <c>[ObservableProperty]</c>s mirror the engine-side <see cref="EngineStrategy"/> constructor.
/// The base owns the quote/depth/trade pumps; this VM holds a reference to the engine it builds so
/// it can mirror the live filtered/unfiltered OBI(T) into a bounded history for the chart.
/// </summary>
public sealed partial class FilteredOrderFlowViewModel : LiveSignalStrategyViewModelBase
{
    /// <summary>Chart history cap — bounded so a hot tape can't grow this without limit.</summary>
    public const int MaxObiSamples = 2_000;

    // ── Engine parameters (locked while streaming via the XAML param strip) ─────────────────────
    [ObservableProperty] private double _windowSeconds = 10.0;
    [ObservableProperty] private long _minTradeSize = 2;
    [ObservableProperty] private int _strongRegime = 3;
    [ObservableProperty] private double _holdSeconds = 5.0;
    [ObservableProperty] private long _quantity = 1;

    // ── Live readouts (throttled, display-only) ─────────────────────────────────────────────────
    [ObservableProperty] private double _filteredObi;
    [ObservableProperty] private double _unfilteredObi;
    [ObservableProperty] private int _filteredRegimeValue;
    [ObservableProperty] private string _filteredRegimeLabel = "0";
    [ObservableProperty] private long _filteredTradesInWindow;
    [ObservableProperty] private long _unfilteredTradesInWindow;

    private EngineStrategy? _engine;
    private DateTime _lastReadoutUtc = DateTime.MinValue;
    private readonly List<ObiSample> _obiHistory = new(MaxObiSamples);

    /// <summary>Bounded live history of (filtered, unfiltered) OBI(T) samples, oldest first.
    /// The window's coalesced render timer reads this — never mutate it off the UI thread.</summary>
    public IReadOnlyList<ObiSample> ObiHistory => _obiHistory;

    public FilteredOrderFlowViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<FilteredOrderFlowViewModel> logger)
        : base(
            strategyId: "filtered.orderflow.imbalance",
            strategyDisplayName: "Filtered Order-Flow Imbalance",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars | StrategyDataRequirement.TradeTape;

    protected override IBacktestStrategy BuildStrategy(Contract contract)
    {
        if (_engine is not null) _engine.Updated -= OnEngineUpdated;
        _obiHistory.Clear();
        _lastReadoutUtc = DateTime.MinValue;
        FilteredObi = UnfilteredObi = 0;
        FilteredRegimeValue = 0;
        FilteredRegimeLabel = "0";
        FilteredTradesInWindow = UnfilteredTradesInWindow = 0;

        _engine = new EngineStrategy(contract, WindowSeconds, MinTradeSize, StrongRegime, HoldSeconds, Quantity);
        _engine.Updated += OnEngineUpdated;
        return _engine;
    }

    // Fires on the UI thread (the host marshals OnTrade/OnTick). Append every sample (cheap, bounded);
    // throttle the observable readout writes so a hot tape doesn't spam PropertyChanged.
    private void OnEngineUpdated()
    {
        var e = _engine;
        if (e is null) return;

        _obiHistory.Add(new ObiSample(DateTime.UtcNow, e.FilteredObi, e.UnfilteredObi, e.FilteredRegime));
        while (_obiHistory.Count > MaxObiSamples) _obiHistory.RemoveAt(0);

        var now = DateTime.UtcNow;
        if ((now - _lastReadoutUtc).TotalMilliseconds < 100) return;
        _lastReadoutUtc = now;

        FilteredObi = e.FilteredObi;
        UnfilteredObi = e.UnfilteredObi;
        FilteredRegimeValue = e.FilteredRegime;
        FilteredRegimeLabel = OrderFlowImbalance.RegimeLabel(e.FilteredRegime);
        FilteredTradesInWindow = e.FilteredTradesInWindow;
        UnfilteredTradesInWindow = e.UnfilteredTradesInWindow;
    }

    protected override string? ValidateSetup()
    {
        if (WindowSeconds is < 1 or > 600) return "Window (s) must be between 1 and 600.";
        if (MinTradeSize < 0) return "Min trade size cannot be negative.";
        if (StrongRegime is < 1 or > 4) return "Strong regime must be between 1 and 4.";
        if (HoldSeconds is < 0.1 or > 600) return "Hold (s) must be between 0.1 and 600.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}

/// <summary>One live OBI(T) sample for the chart: filtered vs unfiltered imbalance + filtered regime.</summary>
public sealed record ObiSample(DateTime TimeUtc, double Filtered, double Unfiltered, int Regime);
