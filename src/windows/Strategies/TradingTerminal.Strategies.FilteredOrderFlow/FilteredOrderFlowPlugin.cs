using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.FilteredOrderFlow;

/// <summary>Plugin entry point — loads this strategy (engine + window) as an external DaxAlgo plugin
/// through the same seam the app used to call directly. See <see cref="DependencyInjection.AddFilteredOrderFlowStrategy"/>.</summary>
public sealed class FilteredOrderFlowPlugin : IStrategyPlugin
{
    public string Name => "Filtered Order-Flow Imbalance";
    public string TargetSdkVersion => SdkInfo.Version;
    public void Register(IPluginRegistrar registrar) => registrar.Services.AddFilteredOrderFlowStrategy();
}
