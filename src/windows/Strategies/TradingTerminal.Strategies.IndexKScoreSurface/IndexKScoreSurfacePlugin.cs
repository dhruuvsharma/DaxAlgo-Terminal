using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.IndexKScoreSurface;

/// <summary>Plugin entry point — loads this strategy (engine + window) as an external DaxAlgo plugin
/// through the same seam the app used to call directly. See <see cref="DependencyInjection.AddIndexKScoreSurfaceStrategy"/>.</summary>
public sealed class IndexKScoreSurfacePlugin : IStrategyPlugin
{
    public string Name => "Index K-Score Surface";
    public string TargetSdkVersion => SdkInfo.Version;
    public void Register(IPluginRegistrar registrar) => registrar.Services.AddIndexKScoreSurfaceStrategy();
}
