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

namespace TradingTerminal.Strategies.PullbackContinuation;

/// <summary>
/// Live signal-mode VM for Trend pullback continuation (index). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="PullbackContinuationStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class PullbackContinuationStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _trendPeriod = 200;
    [ObservableProperty] private int _pullbackWindow = 20;
    [ObservableProperty] private double _pullbackPct = 0.002;
    [ObservableProperty] private double _stopPct = 0.005;
    [ObservableProperty] private double _takeProfitPct = 0.010;
    [ObservableProperty] private long _quantity = 1;

    public PullbackContinuationStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<PullbackContinuationStrategyViewModel> logger)
        : base(
            strategyId: "pullback.continuation",
            strategyDisplayName: "Trend pullback continuation (index)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.PullbackContinuationStrategy(contract, TrendPeriod, PullbackWindow, PullbackPct, StopPct, TakeProfitPct, Quantity);

    protected override string? ValidateSetup()
    {
        if (TrendPeriod < 2) return "Trend period must be at least 2.";
        if (PullbackWindow < 1) return "Pullback window must be positive.";
        if (PullbackPct <= 0) return "Pullback % must be positive.";
        if (StopPct <= 0) return "Stop % must be positive.";
        if (TakeProfitPct <= StopPct) return "Take-profit % must exceed stop %.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}