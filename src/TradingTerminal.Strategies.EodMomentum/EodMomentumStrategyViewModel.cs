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

namespace TradingTerminal.Strategies.EodMomentum;

/// <summary>
/// Live signal-mode VM for End-of-day momentum (index). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="EndOfDayMomentumStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class EodMomentumStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private double _lastFractionOfDay = 0.10;
    [ObservableProperty] private double _minDayReturn = 0.0005;
    [ObservableProperty] private int _sessionStartHourUtc = 13;
    [ObservableProperty] private int _sessionEndHourUtc = 20;
    [ObservableProperty] private long _quantity = 1;

    public EodMomentumStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<EodMomentumStrategyViewModel> logger)
        : base(
            strategyId: "eod.momentum",
            strategyDisplayName: "End-of-day momentum (index)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.EndOfDayMomentumStrategy(contract, LastFractionOfDay, MinDayReturn, SessionStartHourUtc, SessionEndHourUtc, Quantity);

    protected override string? ValidateSetup()
    {
        if (LastFractionOfDay <= 0 || LastFractionOfDay >= 1) return "Last fraction of day must be in (0, 1).";
        if (MinDayReturn <= 0) return "Min day return must be positive.";
        if (SessionStartHourUtc < 0 || SessionStartHourUtc > 23) return "Session start must be in [0, 23].";
        if (SessionEndHourUtc <= SessionStartHourUtc || SessionEndHourUtc > 23) return "Session end must follow start and be ≤ 23.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}