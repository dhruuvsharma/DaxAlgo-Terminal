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

namespace TradingTerminal.Strategies.OrnsteinUhlenbeck;

/// <summary>
/// Live signal-mode VM for Ornstein-Uhlenbeck mean reversion. Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="OrnsteinUhlenbeckStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class OrnsteinUhlenbeckStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _lookback = 500;
    [ObservableProperty] private int _refitEvery = 50;
    [ObservableProperty] private double _entryZ = 2.0;
    [ObservableProperty] private double _exitZ = 0.25;
    [ObservableProperty] private double _stopZ = 4.0;
    [ObservableProperty] private long _quantity = 1;

    public OrnsteinUhlenbeckStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<OrnsteinUhlenbeckStrategyViewModel> logger)
        : base(
            strategyId: "ornstein.uhlenbeck",
            strategyDisplayName: "Ornstein-Uhlenbeck mean reversion",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.OrnsteinUhlenbeckStrategy(contract, Lookback, RefitEvery, EntryZ, ExitZ, StopZ, Quantity);
}