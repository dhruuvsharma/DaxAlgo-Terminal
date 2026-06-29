using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

/// <summary>
/// Plugin entry point — lets this strategy load as an external DaxAlgo plugin (dropped into the
/// host's <c>plugins/</c> folder) instead of being compile-referenced by the app. Its
/// <see cref="Register"/> simply calls the same <see cref="DependencyInjection.AddIndexRegimeGraphStrategy"/>
/// the app used to call directly, so a first-party strategy and a third-party plugin register through
/// one identical seam. The strategy consumes the host-registered Core
/// <c>IAdvancedRegimeProvider</c>, so it needs no host-internal reference.
/// </summary>
public sealed class IndexRegimeGraphPlugin : IStrategyPlugin
{
    public string Name => "Index Regime Graph";

    public string TargetSdkVersion => SdkInfo.Version;

    public void Register(IPluginRegistrar registrar) => registrar.Services.AddIndexRegimeGraphStrategy();
}
