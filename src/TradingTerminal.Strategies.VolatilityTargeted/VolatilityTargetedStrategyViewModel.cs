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

namespace TradingTerminal.Strategies.VolatilityTargeted;

/// <summary>
/// Live signal-mode VM for Volatility targeting (index). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="VolatilityTargetedStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class VolatilityTargetedStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private double _targetVol = 0.001;
    [ObservableProperty] private double _volHalfLife = 200.0;
    [ObservableProperty] private long _maxQuantity = 10;
    [ObservableProperty] private int _rebalanceEveryTicks = 100;

    public VolatilityTargetedStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<VolatilityTargetedStrategyViewModel> logger)
        : base(
            strategyId: "vol.targeted",
            strategyDisplayName: "Volatility targeting (index)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.VolatilityTargetedStrategy(contract, TargetVol, VolHalfLife, MaxQuantity, RebalanceEveryTicks);

    protected override string? ValidateSetup()
    {
        if (TargetVol <= 0) return "Target vol must be positive.";
        if (VolHalfLife <= 0) return "Vol half-life must be positive.";
        if (MaxQuantity <= 0) return "Max quantity must be positive.";
        if (RebalanceEveryTicks < 1) return "Rebalance cadence must be positive.";
        return null;
    }
}