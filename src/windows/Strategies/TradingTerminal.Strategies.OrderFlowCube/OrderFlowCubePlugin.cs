using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.OrderFlowCube;

/// <summary>Plugin entry point — loads this strategy (engine + window) as an external DaxAlgo plugin
/// through the same seam the app used to call directly. See <see cref="DependencyInjection.AddOrderFlowCubeStrategy"/>.</summary>
public sealed class OrderFlowCubePlugin : IStrategyPlugin
{
    public string Name => "Order-Flow Cube";
    public string TargetSdkVersion => SdkInfo.Version;
    public void Register(IPluginRegistrar registrar) => registrar.Services.AddOrderFlowCubeStrategy();
}
