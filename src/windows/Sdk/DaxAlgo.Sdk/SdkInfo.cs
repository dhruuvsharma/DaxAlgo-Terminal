namespace DaxAlgo.Sdk;

/// <summary>
/// Version marker for the DaxAlgo plugin SDK. A plugin can read <see cref="Version"/> to assert it
/// was built against a compatible SDK; the host's plugin loader (Phase B) compares its own SDK
/// version against the plugin's declared target to gate loading.
/// <para>
/// The SDK itself carries no types beyond this marker today — it is a curated façade that re-exports
/// the host's contract assemblies (TradingTerminal.Core via this package, the WPF UI bases via
/// DaxAlgo.Sdk.Wpf). As the surface is narrowed, the canonical plugin contracts (ITradingStrategy,
/// StrategyFactoryRegistration, IStrategyKernel, BacktestStrategyOption, the parameter schema) will
/// be exposed through this package's public API.
/// </para>
/// </summary>
public static class SdkInfo
{
    /// <summary>Semantic version of this SDK build. Bump on any breaking change to the plugin contract.</summary>
    public const string Version = "0.1.0-alpha";
}
