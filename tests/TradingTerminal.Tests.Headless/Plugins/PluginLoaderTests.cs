using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Plugins;
using Xunit;

namespace TradingTerminal.Tests.Plugins;

/// <summary>
/// Covers the host plugin loader's discovery + version-gating + registration path
/// (<see cref="PluginLoader.RegisterFromAssembly"/>) using an in-assembly sample plugin. The
/// file/ALC-loading wrapper (<see cref="PluginLoader.LoadInto"/>) is exercised separately when a
/// real plugin DLL is dropped into the plugins folder; here we prove a discovered plugin's
/// <see cref="IStrategyPlugin.Register"/> wires its strategy + backtest option through the same DI
/// seam as a first-party strategy, and that an SDK-version mismatch is refused.
/// </summary>
public sealed class PluginLoaderTests
{
    [Fact]
    public void RegisterFromAssembly_registers_plugin_strategy_and_backtest_option()
    {
        var services = new ServiceCollection();

        var meta = PluginLoader.RegisterFromAssembly(typeof(SampleStrategyPlugin).Assembly, services, SdkInfo.Version);

        meta.Should().NotBeNull();
        meta!.Name.Should().Be("Sample Plugin");
        meta.TargetSdkVersion.Should().Be(SdkInfo.Version);

        using var provider = services.BuildServiceProvider();
        provider.GetServices<ITradingStrategy>().Should().ContainSingle(s => s.Id == "sample.plugin");
        provider.GetServices<BacktestStrategyOption>().Should().ContainSingle(o => o.Id == "sample.plugin");
    }

    [Fact]
    public void RegisterFromAssembly_throws_on_incompatible_sdk_version()
    {
        var services = new ServiceCollection();

        var act = () => PluginLoader.RegisterFromAssembly(typeof(SampleStrategyPlugin).Assembly, services, hostSdkVersion: "0.2.0");

        act.Should().Throw<PluginIncompatibleException>();
        services.Should().BeEmpty("a rejected plugin must not register anything");
    }

    [Theory]
    [InlineData("0.1.0-alpha", "0.1.0-alpha", true)]  // exact, pre-1.0
    [InlineData("0.1.5", "0.1.0", true)]              // pre-1.0: same major.minor compatible
    [InlineData("0.2.0", "0.1.0", false)]             // pre-1.0: minor bump is breaking
    [InlineData("1.0.0", "0.1.0", false)]             // crossing 1.0 is breaking
    [InlineData("1.4.0", "1.2.0", true)]              // post-1.0: matching major compatible
    [InlineData("2.0.0", "1.9.0", false)]             // post-1.0: major bump is breaking
    public void IsCompatible_follows_semver_rules(string plugin, string host, bool expected) =>
        PluginLoader.IsCompatible(plugin, host).Should().Be(expected);

    // ── In-assembly sample plugin (stands in for an external plugin DLL) ───────────────────────────

    public sealed class SampleStrategyPlugin : IStrategyPlugin
    {
        public string Name => "Sample Plugin";
        public string TargetSdkVersion => SdkInfo.Version;

        public void Register(IPluginRegistrar registrar)
        {
            registrar.Services.AddSingleton<ITradingStrategy, SampleStrategy>();
            registrar.Services.AddSingleton(new BacktestStrategyOption(
                Id: "sample.plugin",
                DisplayName: "Sample Plugin Strategy",
                Build: _ => new SampleBacktestStrategy()));
        }
    }

    private sealed class SampleStrategy : ITradingStrategy
    {
        public string Id => "sample.plugin";
        public string DisplayName => "Sample Plugin Strategy";
        public string Description => "Test-fixture strategy used to exercise the plugin loader.";
    }

    private sealed class SampleBacktestStrategy : IBacktestStrategy
    {
        public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
    }
}
