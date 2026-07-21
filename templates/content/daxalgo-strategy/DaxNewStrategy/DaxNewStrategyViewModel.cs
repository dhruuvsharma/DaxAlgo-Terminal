using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.UI;

namespace DaxNewStrategy;

/// <summary>
/// The live-window view-model. It inherits everything a strategy window needs from
/// <see cref="LiveSignalStrategyViewModelBase"/> — the instrument picker, warm-up bar loading,
/// start/stop, the signal feed, presets, and the Activity Log — so a plugin only supplies two things:
/// the <see cref="DataRequirement"/> pills it needs, and the engine kernel to run (the SAME
/// <see cref="Engine.DaxNewStrategyKernel"/> the backtest uses).
/// <para>
/// The base's constructor dependencies are all provided by the host: <see cref="LiveStrategyHostServices"/>
/// (hub + ingest + store + broker selector + Activity Log), the notification publisher, the clock, and
/// the router factory. The plugin registers this VM with <c>AddTransient</c> and the host resolves it.
/// </para>
/// </summary>
public sealed class DaxNewStrategyViewModel : LiveSignalStrategyViewModelBase
{
    public DaxNewStrategyViewModel(
        LiveStrategyHostServices services,
        INotificationPublisher notifications,
        IClock clock,
        ISignalGeneratorRouterFactory routerFactory,
        ILogger<DaxNewStrategyViewModel> logger)
        : base(
            strategyId: "dax.new.strategy",
            strategyDisplayName: "DaxNewStrategy",
            services, notifications, clock, routerFactory, logger)
    {
    }

    /// <summary>The market-data streams this strategy consumes — drives the pills on the setup screen
    /// and tells the host which pumps to start. The EMA demo only needs Level-1 quotes and bars.</summary>
    protected override StrategyDataRequirement DataRequirement =>
        StrategyDataRequirement.L1 | StrategyDataRequirement.Bars;

    /// <summary>Build the engine that runs live. It is the identical kernel the backtest registers, so
    /// live and backtest can never diverge — the one rule that keeps a strategy honest.</summary>
    protected override IBacktestStrategy BuildStrategy(Contract contract) =>
        new Engine.DaxNewStrategyFactory().Create(contract);
}
