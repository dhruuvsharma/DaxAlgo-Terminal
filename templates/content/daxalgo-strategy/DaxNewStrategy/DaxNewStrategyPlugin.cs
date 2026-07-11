using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;

namespace DaxNewStrategy;

//#if (ui)
// This plugin ships a live window: the view-model + view are registered below and tied to the strategy
// id by a StrategyFactoryRegistration, exactly as a first-party strategy does.
//#endif

/// <summary>
/// The plugin entry point. DaxAlgo Terminal's loader discovers this single public
/// <see cref="IStrategyPlugin"/> in the assembly, checks <see cref="TargetSdkVersion"/> against the
/// host SDK, then calls <see cref="Register"/> — whose body is identical to a first-party
/// <c>AddXxxStrategy()</c> extension.
/// </summary>
public sealed class DaxNewStrategyPlugin : IStrategyPlugin
{
    public string Name => "DaxNewStrategy";

    // Assert the SDK this plugin was built against; the host refuses incompatible plugins
    // (pre-1.0: exact major.minor match required).
    public string TargetSdkVersion => SdkInfo.Version;

    public void Register(IPluginRegistrar registrar)
    {
        // Catalog metadata — what the Strategies pane shows.
        registrar.Services.AddSingleton<ITradingStrategy, DaxNewStrategyDescriptor>();

        // Backtestable engine entry — aggregated into the same registry the host uses, so it shows
        // up in Backtest Studio and the daxalgo-backtest CLI with no host recompile.
        registrar.Services.AddSingleton(new BacktestStrategyOption(
            Id: "dax.new.strategy",
            DisplayName: "DaxNewStrategy",
            Build: contract => new Engine.DaxNewStrategyKernel(contract)));

#if (ui)
        // Live window: the VM (on LiveSignalStrategyViewModelBase) + its view, tied to the strategy id.
        // The host resolves these when the user opens the strategy from the catalog.
        registrar.Services.AddTransient<DaxNewStrategyViewModel>();
        registrar.Services.AddTransient<DaxNewStrategyWindow>();
        registrar.Services.AddSingleton(new StrategyFactoryRegistration(
            StrategyId: "dax.new.strategy",
            ViewFactory: sp => sp.GetRequiredService<DaxNewStrategyWindow>(),
            ViewModelFactory: sp => sp.GetRequiredService<DaxNewStrategyViewModel>()));
#endif
    }
}

/// <summary>Catalog descriptor — replace the description (and add DataRequirement / asset-class
/// tags) once your strategy's real needs are known.</summary>
public sealed class DaxNewStrategyDescriptor : ITradingStrategy
{
    public string Id => "dax.new.strategy";
    public string? BacktestStrategyId => "dax.new.strategy";
    public string DisplayName => "DaxNewStrategy";
    public string Description =>
        "EMA-cross demo strategy scaffolded by `dotnet new daxalgo-strategy` — replace the kernel " +
        "math in Engine/ with your own signal and exit rules.";
}
