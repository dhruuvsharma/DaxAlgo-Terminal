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

namespace TradingTerminal.Strategies.ThinBookFilter;

/// <summary>
/// Live signal-mode VM for Thin-book breakout filter (L2). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="ThinBookFilterStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class ThinBookFilterStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _breakoutLookback = 100;
    [ObservableProperty] private int _depthLookback = 200;
    [ObservableProperty] private double _minDepthRatio = 1.0;
    [ObservableProperty] private int _holdTicks = 200;
    [ObservableProperty] private long _quantity = 1;

    public ThinBookFilterStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<ThinBookFilterStrategyViewModel> logger)
        : base(
            strategyId: "thin.book.filter",
            strategyDisplayName: "Thin-book breakout filter (L2)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.ThinBookFilterStrategy(contract, BreakoutLookback, DepthLookback, MinDepthRatio, HoldTicks, Quantity);

    protected override string? ValidateSetup()
    {
        if (BreakoutLookback < 10) return "Breakout lookback must be at least 10.";
        if (DepthLookback < 10) return "Depth lookback must be at least 10.";
        if (MinDepthRatio <= 0) return "Min depth ratio must be positive.";
        if (HoldTicks < 1) return "Hold ticks must be positive.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}