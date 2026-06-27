using Microsoft.Extensions.DependencyInjection;

namespace DaxAlgo.Sdk;

/// <summary>
/// The surface a plugin uses inside <see cref="IStrategyPlugin.Register"/> to contribute services
/// into the host. Today it exposes the host <see cref="IServiceCollection"/> directly — so a plugin's
/// <c>Register</c> body is line-for-line identical to a first-party <c>AddXxxStrategy()</c> — and
/// carries read-only context about the plugin. It is a named seam (rather than passing
/// <see cref="IServiceCollection"/> raw) so the host can later observe or constrain what a plugin
/// registers without changing plugin code.
/// </summary>
public interface IPluginRegistrar
{
    /// <summary>The host service collection the plugin registers its strategy / view / backtest
    /// option into (e.g. <c>Services.AddSingleton&lt;ITradingStrategy, MyStrategy&gt;()</c>).</summary>
    IServiceCollection Services { get; }

    /// <summary>Metadata about the plugin currently being registered.</summary>
    PluginContext Context { get; }
}

/// <summary>Read-only context about the plugin being registered (for logging / diagnostics).</summary>
/// <param name="Name">The plugin's declared <see cref="IStrategyPlugin.Name"/>.</param>
/// <param name="AssemblyPath">Absolute path of the loaded plugin assembly (empty for in-process tests).</param>
/// <param name="TargetSdkVersion">The plugin's declared <see cref="IStrategyPlugin.TargetSdkVersion"/>.</param>
public sealed record PluginContext(string Name, string AssemblyPath, string TargetSdkVersion);
