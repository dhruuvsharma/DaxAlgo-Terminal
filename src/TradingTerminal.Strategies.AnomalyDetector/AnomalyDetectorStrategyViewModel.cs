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

namespace TradingTerminal.Strategies.AnomalyDetector;

/// <summary>
/// Live signal-mode VM for Rolling z-score anomaly detector. Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="AnomalyDetectorStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class AnomalyDetectorStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _window = 200;
    [ObservableProperty] private double _zScoreThreshold = 4.0;
    [ObservableProperty] private int _cooldownTicks = 100;
    [ObservableProperty] private long _quantity = 1;

    public AnomalyDetectorStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<AnomalyDetectorStrategyViewModel> logger)
        : base(
            strategyId: "anomaly.detector",
            strategyDisplayName: "Rolling z-score anomaly detector",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.AnomalyDetectorStrategy(contract, Window, ZScoreThreshold, CooldownTicks, Quantity);

    protected override string? ValidateSetup()
    {
        if (Window < 10) return "Window must be at least 10.";
        if (ZScoreThreshold <= 0) return "z-threshold must be positive.";
        if (CooldownTicks < 0) return "Cooldown ticks must be non-negative.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}