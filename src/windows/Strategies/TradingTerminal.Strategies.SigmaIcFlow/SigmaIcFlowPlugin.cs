using DaxAlgo.Sdk;
using Microsoft.Extensions.DependencyInjection;

namespace TradingTerminal.Strategies.SigmaIcFlow;

/// <summary>
/// Plugin entry point — lets this strategy load as an external DaxAlgo plugin (dropped into the
/// host's <c>plugins/</c> folder) instead of being compile-referenced by the app. Its
/// <see cref="Register"/> simply calls the same <see cref="DependencyInjection.AddSigmaIcFlowStrategy"/>
/// the app used to call directly — proving a first-party strategy and a third-party plugin register
/// through one identical seam.
/// </summary>
public sealed class SigmaIcFlowPlugin : IStrategyPlugin
{
    public string Name => "Σ⁻¹·IC Order-Flow Optimizer";

    public string TargetSdkVersion => SdkInfo.Version;

    public void Register(IPluginRegistrar registrar) => registrar.Services.AddSigmaIcFlowStrategy();
}
