using Microsoft.Extensions.DependencyInjection;

namespace DaxAlgo.Sdk;

/// <summary>
/// The surface a plugin uses inside <see cref="IStrategyPlugin.Register"/> to contribute services
/// into the host. A plugin's <c>Register</c> body is line-for-line identical to a first-party
/// <c>AddXxxStrategy()</c>, and carries read-only context about the plugin.
/// <para>
/// <b>The collection is add-only.</b> <see cref="Services"/> is not the raw host collection: the host
/// hands each plugin a guarded view that stages registrations and commits them only if
/// <c>Register</c> returns cleanly. A plugin may register new service types of its own, plus
/// additional <c>ITradingStrategy</c> / <c>BacktestStrategyOption</c> / <c>StrategyFactoryRegistration</c>
/// entries. Registering, replacing, or removing a service the host already provides (e.g.
/// <c>ICredentialStore</c>, <c>IBrokerSelector</c>) is refused, and the plugin is quarantined with
/// nothing registered. <c>TryAdd*()</c> keeps its usual no-op semantics.
/// </para>
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
