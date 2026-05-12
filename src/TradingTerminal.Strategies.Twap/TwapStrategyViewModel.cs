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

namespace TradingTerminal.Strategies.Twap;

/// <summary>
/// Live signal-mode VM for TWAP buy execution. Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="TwapExecutionStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class TwapStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private OrderSide _side = OrderSide.Buy;
    [ObservableProperty] private long _parentQuantity = 100;
    [ObservableProperty] private int _slices = 10;

    public TwapStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<TwapStrategyViewModel> logger)
        : base(
            strategyId: "twap.execution",
            strategyDisplayName: "TWAP buy execution",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.TwapExecutionStrategy(contract, Side, ParentQuantity, Slices);
}