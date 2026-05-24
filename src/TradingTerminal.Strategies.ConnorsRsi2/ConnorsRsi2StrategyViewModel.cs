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

namespace TradingTerminal.Strategies.ConnorsRsi2;

/// <summary>
/// Live signal-mode VM for Connors RSI(2) reversion (forex). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="RsiTwoPeriodStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class ConnorsRsi2StrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _rsiPeriod = 2;
    [ObservableProperty] private double _entryRsi = 10;
    [ObservableProperty] private double _exitRsi = 90;
    [ObservableProperty] private int _exitSmaPeriod = 5;
    [ObservableProperty] private long _quantity = 1;

    public ConnorsRsi2StrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<ConnorsRsi2StrategyViewModel> logger)
        : base(
            strategyId: "connors.rsi2",
            strategyDisplayName: "Connors RSI(2) reversion (forex)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.RsiTwoPeriodStrategy(contract, RsiPeriod, EntryRsi, ExitRsi, ExitSmaPeriod, Quantity);

    protected override string? ValidateSetup()
    {
        if (RsiPeriod < 1) return "RSI period must be positive.";
        if (EntryRsi >= ExitRsi) return "Entry RSI must be below exit RSI.";
        if (EntryRsi < 0 || ExitRsi > 100) return "RSI thresholds must be in [0, 100].";
        if (ExitSmaPeriod < 1) return "Exit SMA period must be positive.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}