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

namespace TradingTerminal.Strategies.LondonOpenBreakout;

/// <summary>
/// Live signal-mode VM for London-open breakout (forex). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="LondonOpenBreakoutStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class LondonOpenBreakoutStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private int _londonOpenHourUtc = 8;
    [ObservableProperty] private int _londonCloseHourUtc = 16;
    [ObservableProperty] private double _atrStopMultiplier = 2.0;
    [ObservableProperty] private int _atrPeriod = 50;
    [ObservableProperty] private long _quantity = 1;

    public LondonOpenBreakoutStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<LondonOpenBreakoutStrategyViewModel> logger)
        : base(
            strategyId: "london.open.breakout",
            strategyDisplayName: "London-open breakout (forex)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.LondonOpenBreakoutStrategy(contract, LondonOpenHourUtc, LondonCloseHourUtc, AtrStopMultiplier, AtrPeriod, Quantity);

    protected override string? ValidateSetup()
    {
        if (LondonOpenHourUtc < 0 || LondonOpenHourUtc > 23) return "London open hour must be in [0, 23].";
        if (LondonCloseHourUtc <= LondonOpenHourUtc || LondonCloseHourUtc > 23) return "London close hour must follow open and be ≤ 23.";
        if (AtrStopMultiplier <= 0) return "ATR stop multiplier must be positive.";
        if (AtrPeriod < 2) return "ATR period must be at least 2.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}