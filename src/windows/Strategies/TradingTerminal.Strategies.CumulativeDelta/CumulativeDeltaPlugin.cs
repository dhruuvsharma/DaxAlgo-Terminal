using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.CumulativeDelta;

/// <summary>
/// Plugin entry point — lets this strategy load as an external DaxAlgo plugin (dropped into the
/// host's <c>plugins/</c> folder) instead of being compile-referenced by the app. Its
/// <see cref="Register"/> calls the same <see cref="DependencyInjection.AddCumulativeDeltaStrategy"/>
/// the app used to call directly, so a first-party strategy and a third-party plugin register
/// through one identical seam.
/// </summary>
public sealed class CumulativeDeltaPlugin : IStrategyPlugin
{
    public string Name => "Cumulative Delta Scalper";

    public string TargetSdkVersion => SdkInfo.Version;

    public void Register(IPluginRegistrar registrar) => registrar.Services.AddCumulativeDeltaStrategy();
}
