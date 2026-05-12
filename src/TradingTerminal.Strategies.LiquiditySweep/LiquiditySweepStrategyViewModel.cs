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

namespace TradingTerminal.Strategies.LiquiditySweep;

/// <summary>
/// Live signal-mode VM for Liquidity-sweep detector (L2). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="LiquiditySweepStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class LiquiditySweepStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _lookback = 100;
    [ObservableProperty] private double _sweepRatio = 0.40;
    [ObservableProperty] private int _holdTicks = 50;
    [ObservableProperty] private long _quantity = 1;

    public LiquiditySweepStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<LiquiditySweepStrategyViewModel> logger)
        : base(
            strategyId: "liquidity.sweep",
            strategyDisplayName: "Liquidity-sweep detector (L2)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.LiquiditySweepStrategy(contract, Lookback, SweepRatio, HoldTicks, Quantity);
}