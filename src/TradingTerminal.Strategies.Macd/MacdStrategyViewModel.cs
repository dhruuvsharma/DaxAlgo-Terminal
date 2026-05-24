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

namespace TradingTerminal.Strategies.Macd;

/// <summary>
/// Live signal-mode VM for MACD signal crossover (forex). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="MacdCrossoverStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class MacdStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _fastPeriod = 12;
    [ObservableProperty] private int _slowPeriod = 26;
    [ObservableProperty] private int _signalPeriod = 9;
    [ObservableProperty] private long _quantity = 1;

    public MacdStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<MacdStrategyViewModel> logger)
        : base(
            strategyId: "macd.crossover",
            strategyDisplayName: "MACD signal crossover (forex)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.MacdCrossoverStrategy(contract, FastPeriod, SlowPeriod, SignalPeriod, Quantity);

    protected override string? ValidateSetup()
    {
        if (FastPeriod < 2) return "Fast period must be at least 2.";
        if (SlowPeriod <= FastPeriod) return "Slow period must be greater than fast period.";
        if (SignalPeriod < 1) return "Signal period must be positive.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}