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

namespace TradingTerminal.Strategies.AvellanedaStoikov;

/// <summary>
/// Live signal-mode VM for Avellaneda-Stoikov market maker. Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="AvellanedaStoikovStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class AvellanedaStoikovStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private double _gamma = 0.1;
    [ObservableProperty] private double _k = 1.5;
    [ObservableProperty] private double _varianceHalfLife = 200.0;
    [ObservableProperty] private long _quoteSize = 1;
    [ObservableProperty] private long _maxInventory = 5;
    [ObservableProperty] private int _horizonTicks = 5000;
    [ObservableProperty] private int _requoteEveryTicks = 100;

    public AvellanedaStoikovStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<AvellanedaStoikovStrategyViewModel> logger)
        : base(
            strategyId: "avellaneda.stoikov",
            strategyDisplayName: "Avellaneda-Stoikov market maker",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.AvellanedaStoikovStrategy(contract, Gamma, K, VarianceHalfLife, QuoteSize, MaxInventory, HorizonTicks, RequoteEveryTicks);
}