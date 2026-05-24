using Microsoft.Extensions.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest.Strategies;
using TradingTerminal.UI;

namespace TradingTerminal.Strategies.GapFade;

/// <summary>
/// Live signal-mode VM for Overnight gap fade (index). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="GapFadeStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class GapFadeStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private double _overnightGapMinutes = 60;
    [ObservableProperty] private double _minGapPct = 0.002;
    [ObservableProperty] private double _stopGapMultiples = 1.5;
    [ObservableProperty] private int _maxHoldTicks = 1000;
    [ObservableProperty] private long _quantity = 1;

    public GapFadeStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<GapFadeStrategyViewModel> logger)
        : base(
            strategyId: "gap.fade",
            strategyDisplayName: "Overnight gap fade (index)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.GapFadeStrategy(contract, OvernightGapMinutes, MinGapPct, StopGapMultiples, MaxHoldTicks, Quantity);

    protected override string? ValidateSetup()
    {
        if (OvernightGapMinutes <= 0) return "Gap window must be positive.";
        if (MinGapPct <= 0) return "Min gap % must be positive.";
        if (StopGapMultiples <= 0) return "Stop gap multiplier must be positive.";
        if (MaxHoldTicks < 1) return "Max hold ticks must be positive.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}