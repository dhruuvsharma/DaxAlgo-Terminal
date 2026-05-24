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

namespace TradingTerminal.Strategies.TrendFilter;

/// <summary>
/// Live signal-mode VM for 200-SMA trend filter (index). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="TrendFilterStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class TrendFilterStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _period = 200;
    [ObservableProperty] private bool _allowShort = false;
    [ObservableProperty] private long _quantity = 1;

    public TrendFilterStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<TrendFilterStrategyViewModel> logger)
        : base(
            strategyId: "trend.filter",
            strategyDisplayName: "200-SMA trend filter (index)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.TrendFilterStrategy(contract, Period, AllowShort, Quantity);

    protected override string? ValidateSetup()
    {
        if (Period < 2) return "Period must be at least 2.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}