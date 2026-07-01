using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

/// <summary>Plugin entry point — loads this strategy (engine + window) as an external DaxAlgo plugin
/// through the same seam the app used to call directly. See <see cref="DependencyInjection.AddOrderFlowSurfaceSpikeStrategy"/>.</summary>
public sealed class OrderFlowSurfaceSpikePlugin : IStrategyPlugin
{
    public string Name => "Order-Flow Surface Spike";
    public string TargetSdkVersion => SdkInfo.Version;
    public void Register(IPluginRegistrar registrar) => registrar.Services.AddOrderFlowSurfaceSpikeStrategy();
}
