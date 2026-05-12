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

namespace TradingTerminal.Strategies.BookPressure;

/// <summary>
/// Live signal-mode VM for Order-book pressure / cumulative imbalance (L2). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="BookPressureStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class BookPressureStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private double _entryThreshold = 0.35;
    [ObservableProperty] private int _holdTicks = 50;
    [ObservableProperty] private long _quantity = 1;

    public BookPressureStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<BookPressureStrategyViewModel> logger)
        : base(
            strategyId: "book.pressure",
            strategyDisplayName: "Order-book pressure / cumulative imbalance (L2)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.BookPressureStrategy(contract, EntryThreshold, HoldTicks, Quantity);
}