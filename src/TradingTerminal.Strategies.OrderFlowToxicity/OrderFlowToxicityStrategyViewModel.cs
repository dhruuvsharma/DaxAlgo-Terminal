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

namespace TradingTerminal.Strategies.OrderFlowToxicity;

/// <summary>
/// Live signal-mode VM for Order-flow toxicity / VPIN-style (L2). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="OrderFlowToxicityStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class OrderFlowToxicityStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _windowTicks = 200;
    [ObservableProperty] private double _toxicityThreshold = 0.55;
    [ObservableProperty] private int _holdTicks = 100;
    [ObservableProperty] private long _quantity = 1;

    public OrderFlowToxicityStrategyViewModel(
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<OrderFlowToxicityStrategyViewModel> logger)
        : base(
            strategyId: "order.flow.toxicity",
            strategyDisplayName: "Order-flow toxicity / VPIN-style (L2)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.OrderFlowToxicityStrategy(contract, WindowTicks, ToxicityThreshold, HoldTicks, Quantity);

    protected override string? ValidateSetup()
    {
        if (WindowTicks < 10) return "Window ticks must be at least 10.";
        if (ToxicityThreshold <= 0.5 || ToxicityThreshold >= 1) return "Toxicity threshold must be in (0.5, 1).";
        if (HoldTicks < 1) return "Hold ticks must be positive.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}