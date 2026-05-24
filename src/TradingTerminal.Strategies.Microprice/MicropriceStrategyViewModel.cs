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

namespace TradingTerminal.Strategies.Microprice;

/// <summary>
/// Live signal-mode VM for Microprice deviation (microstructure). Parameter <c>[ObservableProperty]</c>s mirror
/// the underlying <see cref="MicropriceStrategy"/> constructor â€” add / rename / re-bind to your
/// own XAML to expose more knobs.
/// </summary>
public sealed partial class MicropriceStrategyViewModel : LiveSignalStrategyViewModelBase
{
    [ObservableProperty] private double _entryThreshold = 0.001;
    [ObservableProperty] private int _holdTicks = 50;
    [ObservableProperty] private long _quantity = 1;

    public MicropriceStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<MicropriceStrategyViewModel> logger)
        : base(
            strategyId: "microprice.deviation",
            strategyDisplayName: "Microprice deviation (microstructure)",
            services, notifications, clock, routerFactory, logger)
    {
    }

    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new TradingTerminal.Infrastructure.Backtest.Strategies.MicropriceStrategy(contract, EntryThreshold, HoldTicks, Quantity);

    protected override string? ValidateSetup()
    {
        if (EntryThreshold <= 0) return "Entry threshold must be positive.";
        if (HoldTicks < 1) return "Hold ticks must be positive.";
        if (Quantity <= 0) return "Quantity must be positive.";
        return null;
    }
}