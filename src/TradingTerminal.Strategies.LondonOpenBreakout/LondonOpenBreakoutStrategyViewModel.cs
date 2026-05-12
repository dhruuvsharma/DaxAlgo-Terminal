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
        IMarketDataRepository repository,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<LondonOpenBreakoutStrategyViewModel> logger)
        : base(
            strategyId: "london.open.breakout",
            strategyDisplayName: "London-open breakout (forex)",
            repository, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.LondonOpenBreakoutStrategy(contract, LondonOpenHourUtc, LondonCloseHourUtc, AtrStopMultiplier, AtrPeriod, Quantity);
}