namespace DaxAlgo.Sdk;

/// <summary>
/// Version marker for the DaxAlgo plugin SDK. A plugin can read <see cref="Version"/> to assert it
/// was built against a compatible SDK; the host's plugin loader (Phase B) compares its own SDK
/// version against the plugin's declared target to gate loading.
/// <para>
/// The SDK is a curated façade over the host's contract assemblies (TradingTerminal.Core via this
/// package, the WPF UI bases via DaxAlgo.Sdk.Wpf) and owns the stable
/// <see cref="IStrategyEngineFactory"/> activation seam for packaged engines. As the surface is
/// narrowed, more canonical plugin contracts will move behind this package's public API.
/// </para>
/// </summary>
public static class SdkInfo
{
    /// <summary>Semantic version of this SDK build. Bump on any breaking change to the plugin contract.</summary>
    public const string Version = "0.2.0-alpha";
}
