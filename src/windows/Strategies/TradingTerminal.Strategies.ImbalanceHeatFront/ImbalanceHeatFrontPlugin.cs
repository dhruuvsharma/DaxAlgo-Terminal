using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.ImbalanceHeatFront;

/// <summary>Plugin entry point — loads this strategy (engine + window) as an external DaxAlgo plugin
/// through the same seam the app used to call directly. See <see cref="DependencyInjection.AddImbalanceHeatFrontStrategy"/>.</summary>
public sealed class ImbalanceHeatFrontPlugin : IStrategyPlugin
{
    public string Name => "Imbalance Heat Front";
    public string TargetSdkVersion => SdkInfo.Version;
    public void Register(IPluginRegistrar registrar) => registrar.Services.AddImbalanceHeatFrontStrategy();
}
