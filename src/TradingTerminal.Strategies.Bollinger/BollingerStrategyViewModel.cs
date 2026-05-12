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

namespace TradingTerminal.Strategies.Bollinger;

/// <summary>
/// Live signal-mode VM for Bollinger band reversion (forex). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="BollingerReversionStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class BollingerStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _period = 20;
    [ObservableProperty] private double _entryStd = 2.0;
    [ObservableProperty] private double _stopBandMultiplier = 3.0;
    [ObservableProperty] private long _quantity = 1;

    public BollingerStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<BollingerStrategyViewModel> logger)
        : base(
            strategyId: "bollinger.reversion",
            strategyDisplayName: "Bollinger band reversion (forex)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.BollingerReversionStrategy(contract, Period, EntryStd, StopBandMultiplier, Quantity);
}