using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.UI;
using Engine = TradingTerminal.Infrastructure.Backtest.Strategies;

namespace TradingTerminal.Strategies.ApexScalper;

/// <summary>
/// Live signal-mode VM for the APEX microstructure scalper. Setup form exposes the big-knobs
/// (window size, composite threshold, min signals agree, risk %, daily-loss cap, allowed
/// sessions, max chart candles). After Continue, the dashboard binds to
/// <see cref="LatestSnapshot"/> which is refreshed each time a chart bar rolls.
/// </summary>
public sealed partial class ApexScalperStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _windowSize = 20;
    [ObservableProperty] private double _compositeThreshold = 1.80;
    [ObservableProperty] private int _minSignalsAgree = 4;
    [ObservableProperty] private double _riskPercent = 0.5;
    [ObservableProperty] private bool _useDynamicSizing = true;
    [ObservableProperty] private long _fixedQuantity = 1;
    [ObservableProperty] private double _maxDailyLossPercent = 2.0;
    [ObservableProperty] private double _maxSpreadPriceUnits = 0.003;
    [ObservableProperty] private bool _tradeAsian;
    [ObservableProperty] private bool _tradeLondon = true;
    [ObservableProperty] private bool _tradeNewYork = true;
    [ObservableProperty] private bool _tradeLondonNy = true;

    /// <summary>Number of recent internal candles to draw on every per-indicator chart.</summary>
    [ObservableProperty] private int _maxChartCandles = 60;

    /// <summary>Latest flat strategy snapshot. Bound by the dashboard panel; updates on each
    /// completed chart bar (every 15s by the base class default).</summary>
    [ObservableProperty] private Engine.ApexSnapshot? _latestSnapshot;

    private Engine.ApexScalperStrategy? _engine;

    /// <summary>The active engine instance, exposed for the window's chart redraw to pull
    /// <see cref="Engine.ApexScalperStrategy.History"/>. <c>null</c> before Continue is
    /// pressed and after Stop.</summary>
    public Engine.ApexScalperStrategy? EngineStrategy => _engine;

    public ApexScalperStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<ApexScalperStrategyViewModel> logger)
        : base(
            strategyId: "apex.scalper",
            strategyDisplayName: "APEX microstructure scalper",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract)
    {
        _engine = new Engine.ApexScalperStrategy(
            contract,
            windowSize: WindowSize,
            compositeThreshold: CompositeThreshold,
            minSignalsAgree: MinSignalsAgree,
            riskPercent: RiskPercent,
            useDynamicSizing: UseDynamicSizing,
            fixedQuantity: FixedQuantity,
            maxDailyLossPercent: MaxDailyLossPercent,
            maxSpreadPriceUnits: MaxSpreadPriceUnits,
            tradeAsian: TradeAsian,
            tradeLondon: TradeLondon,
            tradeNewYork: TradeNewYork,
            tradeLondonNy: TradeLondonNy);
        return _engine;
    }

    protected override void OnBarsUpdated()
    {
        LatestSnapshot = _engine?.Latest;
        if (LatestSnapshot is { } s)
        {
            var dir = s.CompositeDirection switch { > 0 => "LONG", < 0 => "SHORT", _ => "FLAT" };
            Log("APEX", $"composite={s.Composite:F2} dir={dir} agree={s.SignalsAgree}/8 regime={s.Regime} trade={(s.TradeAllowed ? "ok" : "blocked")}");
        }
    }

    /// <summary>Pull enough warm-up bars to fill the user's analysis window AND the visible
    /// chart tail — whichever is larger — so the per-indicator charts have context the moment
    /// Continue is pressed.</summary>
    protected override int WarmupBarCount => Math.Max(WindowSize, MaxChartCandles);

    /// <summary>Seed the engine's snapshot history with mid prices from the warm-up bars so the
    /// price chart isn't blank waiting for the first live candle to roll. Real indicator values
    /// fill in as live ticks land.</summary>
    protected override Task OnWarmupBarsLoadedAsync(IReadOnlyList<Bar> bars)
    {
        _engine?.SeedFromBars(bars);
        return Task.CompletedTask;
    }

    protected override string? ValidateSetup()
    {
        if (WindowSize < 5) return "Window size must be ≥ 5.";
        if (CompositeThreshold <= 0 || CompositeThreshold > 3) return "Composite threshold must be in (0, 3].";
        if (MinSignalsAgree < 1 || MinSignalsAgree > 8) return "Min signals agree must be in [1, 8].";
        if (RiskPercent <= 0 || RiskPercent > 100) return "Risk % must be in (0, 100].";
        if (FixedQuantity <= 0) return "Quantity must be positive.";
        if (MaxDailyLossPercent <= 0) return "Daily-loss cap must be positive.";
        if (MaxChartCandles < 10 || MaxChartCandles > 500) return "Max chart candles must be in [10, 500].";
        if (!TradeAsian && !TradeLondon && !TradeNewYork && !TradeLondonNy)
            return "Enable at least one session.";
        return null;
    }
}
