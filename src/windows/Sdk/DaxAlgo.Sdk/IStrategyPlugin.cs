namespace DaxAlgo.Sdk;

/// <summary>
/// Entry point a strategy-plugin assembly exposes. The host's plugin loader finds the single public
/// parameterless <see cref="IStrategyPlugin"/> implementation in each plugin assembly, checks
/// <see cref="TargetSdkVersion"/> against the host SDK (<see cref="SdkInfo.Version"/>), then calls
/// <see cref="Register"/> so the plugin can contribute its strategy, view/view-model and backtest
/// option into the host.
/// <para>
/// A plugin's <see cref="Register"/> body is identical to a first-party <c>AddXxxStrategy()</c>
/// extension — register the <c>ITradingStrategy</c>, the view + view-model, the
/// <c>StrategyFactoryRegistration</c> and the <c>BacktestStrategyOption</c> on
/// <see cref="IPluginRegistrar.Services"/>.
/// </para>
/// </summary>
public interface IStrategyPlugin
{
    /// <summary>Human-readable plugin name (logging + the future marketplace UI).</summary>
    string Name { get; }

    /// <summary>The <c>DaxAlgo.Sdk</c> version this plugin was built against — normally
    /// <see cref="SdkInfo.Version"/>. The loader refuses a plugin whose version is incompatible with
    /// the host SDK (pre-1.0: exact major.minor; post-1.0: matching semver major).</summary>
    string TargetSdkVersion { get; }

    /// <summary>Contributes the plugin's services into the host via <paramref name="registrar"/>.</summary>
    void Register(IPluginRegistrar registrar);
}
