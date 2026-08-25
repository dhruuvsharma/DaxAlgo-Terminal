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
    /// <summary>
    /// Semantic version of this SDK build. Bump on any breaking change to the plugin contract.
    ///
    /// <para><b>0.4.0</b> renamed the backtest engine's leftovers. <c>IBacktestStrategy</c> became
    /// <c>IOrderRoutedStrategy</c> and is <c>[Obsolete]</c>; <c>BacktestStrategyOption</c> became
    /// <c>StrategyCatalogEntry</c>; <c>TradingTerminal.Core.Backtest</c> is gone. Plugins built against
    /// 0.3 must be rebuilt. The names were not cosmetic: the old ones sent people looking for a
    /// backtester archived on 2026-08-17, and told authors following current guidance that their
    /// correct code was wrong.</para>
    ///
    /// <para>This constant and the <c>DaxAlgoSdkVersion</c> MSBuild property are one number in two
    /// places, so <c>SdkVersionTests</c> asserts they agree — they had already been allowed to disagree
    /// once, when the shipped AI context pack claimed 0.2.0-alpha against an SDK at 0.3.0.</para>
    /// </summary>
    public const string Version = "0.4.0";
}
