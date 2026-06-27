using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace DaxAlgo.SamplePlugin;

/// <summary>
/// Minimal example strategy plugin. The host's <c>PluginLoader</c> discovers this single public
/// <see cref="IStrategyPlugin"/>, checks <see cref="TargetSdkVersion"/> against the host SDK, then
/// calls <see cref="Register"/> — whose body is identical to a first-party <c>AddXxxStrategy()</c>.
/// </summary>
public sealed class SamplePlugin : IStrategyPlugin
{
    public string Name => "Sample Strategy Plugin";

    // A plugin asserts the SDK it was built against; the host refuses incompatible plugins.
    public string TargetSdkVersion => SdkInfo.Version;

    public void Register(IPluginRegistrar registrar)
    {
        // Catalog metadata for the Strategies pane.
        registrar.Services.AddSingleton<ITradingStrategy, SampleStrategy>();

        // Backtestable engine entry — registered into the same IBacktestStrategyRegistry the host
        // aggregates, so it shows up in Backtest Studio with no host recompile.
        registrar.Services.AddSingleton(new BacktestStrategyOption(
            Id: "sample.plugin",
            DisplayName: "Sample Plugin Strategy (skeleton)",
            Build: contract => new SampleBacktestStrategy(contract)));
    }
}

/// <summary>Catalog descriptor — the metadata the Strategies pane renders.</summary>
public sealed class SampleStrategy : ITradingStrategy
{
    public string Id => "sample.plugin";
    public string? BacktestStrategyId => "sample.plugin";
    public string DisplayName => "Sample Plugin Strategy";
    public string Description =>
        "A headless skeleton strategy shipped as an external plugin to demonstrate the DaxAlgo SDK. " +
        "Replace the engine logic with your own signal/exit rules.";
}

/// <summary>
/// Skeleton backtest engine. Intentionally inert (places no orders) — it exists to prove the plugin
/// wiring end-to-end; a real plugin implements its signal logic in these callbacks via the router.
/// </summary>
public sealed class SampleBacktestStrategy(Contract contract) : IBacktestStrategy
{
    private readonly Contract _contract = contract;

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
    public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
    public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
}
