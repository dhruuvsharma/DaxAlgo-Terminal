using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.OrderFlowPressureMap;

/// <summary>Plugin entry point — loads this live-only monitor strategy as an external DaxAlgo plugin
/// through the same seam the app used to call directly. See <see cref="DependencyInjection.AddOrderFlowPressureMapStrategy"/>.</summary>
public sealed class OrderFlowPressureMapPlugin : IStrategyPlugin
{
    public string Name => "1-Minute Order-Flow Pressure Map";
    public string TargetSdkVersion => SdkInfo.Version;
    public void Register(IPluginRegistrar registrar) => registrar.Services.AddOrderFlowPressureMapStrategy();
}
