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

namespace TradingTerminal.Strategies.OnlineRegressionAlpha;

/// <summary>
/// Live signal-mode VM for Online-regression alpha (RLS). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="OnlineRegressionAlphaStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class OnlineRegressionAlphaStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _holdTicks = 50;
    [ObservableProperty] private double _entryThreshold = 0.0001;
    [ObservableProperty] private double _volHalfLife = 100.0;
    [ObservableProperty] private double _lambda = 0.99;
    [ObservableProperty] private long _quantity = 1;

    public OnlineRegressionAlphaStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<OnlineRegressionAlphaStrategyViewModel> logger)
        : base(
            strategyId: "online.regression.alpha",
            strategyDisplayName: "Online-regression alpha (RLS)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.OnlineRegressionAlphaStrategy(contract, HoldTicks, EntryThreshold, VolHalfLife, Lambda, Quantity);
}