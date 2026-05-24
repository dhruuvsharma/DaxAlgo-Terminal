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

public sealed partial class BollingerStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _period = 20;
    [ObservableProperty] private double _entryStd = 2.0;
    [ObservableProperty] private double _stopBandMultiplier = 3.0;
    [ObservableProperty] private long _quantity = 1;

    public BollingerStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<BollingerStrategyViewModel> logger)
        : base(
            strategyId: "bollinger.reversion",
            strategyDisplayName: "Bollinger band reversion (forex)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new BollingerReversionStrategy(contract, Period, EntryStd, StopBandMultiplier, Quantity);

    protected override string? ValidateSetup()
    {
        if (Period < 2) return "Period must be at least 2.";
        if (EntryStd <= 0) return "Entry σ must be positive.";
        if (StopBandMultiplier <= EntryStd) return "Stop band ×σ must exceed entry σ.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}
