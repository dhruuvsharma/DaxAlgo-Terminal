using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Proves the loader's real file/ALC path: <see cref="PluginLoader.LoadInto"/> against a GENUINE
/// external plugin DLL (DaxAlgo.SamplePlugin) staged on disk and never referenced by this test
/// assembly. This exercises what the in-assembly <see cref="PluginLoaderTests"/> cannot — loading the
/// plugin into its own collectible <see cref="PluginLoadContext"/> and resolving its contract
/// references (TradingTerminal.Core / DaxAlgo.Sdk) back to the host's already-loaded copies so the
/// plugin's <c>ITradingStrategy</c> is the SAME type the host sees.
/// </summary>
public sealed class ExternalPluginLoadTests
{
    private static string StagedPluginsRoot =>
        Path.Combine(AppContext.BaseDirectory, "TestPlugins");

    [Fact]
    public void Staged_sample_plugin_dll_is_present()
    {
        // Guards the MSBuild StageSamplePlugin copy step — if this fails the rest can't run.
        var dll = Path.Combine(StagedPluginsRoot, "DaxAlgo.SamplePlugin", "DaxAlgo.SamplePlugin.dll");
        File.Exists(dll).Should().BeTrue($"the sample plugin should be staged at {dll}");
    }

    [Fact]
    public void LoadInto_loads_external_plugin_and_shares_host_contract_identity()
    {
        var services = new ServiceCollection();

        var loaded = PluginLoader.LoadInto(services, StagedPluginsRoot, SdkInfo.Version);

        loaded.Should().ContainSingle(p => p.Name == "Sample Strategy Plugin");

        using var provider = services.BuildServiceProvider();

        // The strategy registered by the external DLL resolves AS the host's ITradingStrategy type —
        // this only works because PluginLoadContext shared TradingTerminal.Core with the default context.
        var strategy = provider.GetServices<ITradingStrategy>().Should().ContainSingle(s => s.Id == "sample.plugin").Subject;
        provider.GetServices<BacktestStrategyOption>().Should().ContainSingle(o => o.Id == "sample.plugin");

        // The strategy instance comes from the external assembly, loaded in a non-default context —
        // confirming we really crossed a load-context boundary rather than picking up an in-test type.
        var ctx = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(strategy.GetType().Assembly);
        ctx.Should().NotBeNull();
        ctx!.Name.Should().StartWith("Plugin:", "the plugin must load in its own PluginLoadContext, not the default one");
    }

    [Fact]
    public void LoadInto_missing_directory_is_a_noop()
    {
        var services = new ServiceCollection();

        var loaded = PluginLoader.LoadInto(services, Path.Combine(AppContext.BaseDirectory, "no-such-plugins-dir"), SdkInfo.Version);

        loaded.Should().BeEmpty();
        services.Should().BeEmpty();
    }
}
