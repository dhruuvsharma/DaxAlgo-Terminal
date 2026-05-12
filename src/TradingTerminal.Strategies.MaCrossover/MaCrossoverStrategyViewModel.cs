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

namespace TradingTerminal.Strategies.MaCrossover;

/// <summary>
/// Live signal-mode VM for MA crossover / golden cross (forex). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="MovingAverageCrossoverStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class MaCrossoverStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _fastPeriod = 50;
    [ObservableProperty] private int _slowPeriod = 200;
    [ObservableProperty] private long _quantity = 1;

    public MaCrossoverStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<MaCrossoverStrategyViewModel> logger)
        : base(
            strategyId: "ma.crossover",
            strategyDisplayName: "MA crossover / golden cross (forex)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.MovingAverageCrossoverStrategy(contract, FastPeriod, SlowPeriod, Quantity);
}