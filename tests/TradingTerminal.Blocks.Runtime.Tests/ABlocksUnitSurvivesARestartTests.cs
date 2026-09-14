using System.IO;
using DaxAlgo.Package;
using DaxAlgo.Sdk;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>
/// A Blocks unit kept the way a widget-SDK unit is: written as a <c>.daxalgostrategy</c> or
/// <c>.daxalgovisualizer</c>, installed through the ordinary installer and trust policy into the authored
/// units root, loaded by the ordinary loader on the next start, and put back in the catalog with its page.
/// </summary>
public sealed class ABlocksUnitSurvivesARestartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "daxalgo-blocks-restart-" + Guid.NewGuid().ToString("N"));
    private readonly BlocksUnitCompiler _compiler = new();

    private const string Page = """<body><div id="v">waiting</div><script>dax.on('state', s => v.textContent = s.spread); dax.ready();</script></body>""";

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_strategy_and_a_visualizer_are_written_as_their_own_kinds_with_the_page_inside()
    {
        var strategy = Package("spread-arb", "SpreadArbitrage.cs", BlocksUnitCompilerTests.ArbitrageSource);
        var visualizer = Package("odds", "PredictionMarketOdds.cs", BlocksUnitCompilerTests.PollerSource);

        Path.GetExtension(strategy.Path).Should().Be(DaxPackage.ExtensionFor(DaxPackageKind.Strategy));
        Path.GetExtension(visualizer.Path).Should().Be(DaxPackage.ExtensionFor(DaxPackageKind.Visualizer));

        var contents = DaxPackage.Read(strategy.Path!);
        contents.Manifest.Payloads.Should().Contain(p => p.Role == DaxPayloadRole.Assembly);
        contents.Manifest.Payloads.Should().Contain(p => p.Role == DaxPayloadRole.Source && p.Path.EndsWith("SpreadArbitrage.cs"));
        contents.Manifest.Payloads.Should().Contain(p => p.Role == DaxPayloadRole.Ui && p.Path == "payload/ui/index.html");
        contents.Manifest.Payloads.Should().Contain(p => p.Role == DaxPayloadRole.Ui && p.Path == "payload/ui/js/app.js");
    }

    [Fact]
    public void Installed_units_load_on_the_next_start_and_return_to_the_catalog_with_their_page()
    {
        var units = Path.Combine(_root, "units");
        var state = new PluginStateStore(units);

        foreach (var (id, file, source) in new[]
                 {
                     ("spread-arb", "SpreadArbitrage.cs", BlocksUnitCompilerTests.ArbitrageSource),
                     ("odds", "PredictionMarketOdds.cs", BlocksUnitCompilerTests.PollerSource),
                 })
        {
            var install = PluginInstaller.InstallFromArtifact(
                Package(id, file, source).Path!, units, PluginTrustPolicy.Permissive, NoSignature.Instance,
                state, PluginScanMode.Enforce, PluginScanProfile.Sandbox);

            install.Success.Should().BeTrue($"{id}: {install.Message} — the network a Blocks unit may use is not a reason to refuse it");
        }

        // The next start: the loader and profile the shells use for the authored-units root.
        var report = PluginLoader.LoadSandboxedWithReport(new ServiceCollection(), units, SdkInfo.Version, PluginTrustPolicy.Permissive, state);

        report.Problems.Should().BeEmpty(string.Join(" / ", report.Problems.Select(p => $"{p.PluginFolderName}: {p}")));
        report.Loaded.Should().HaveCount(2);

        var registry = new BlocksUnitRegistry();
        var skipped = new List<string>();
        BlocksPackage.Register(report.Loaded, registry, skipped.Add).Should().Be(2, string.Join(" / ", skipped));

        var strategy = registry.Find("spread-arb")!;
        strategy.IsStrategy.Should().BeTrue("the kind is read from the compiled code");
        strategy.DisplayName.Should().Be("Spread arbitrage", "a package named only by its id shows the name the unit gives itself");
        strategy.PageFiles.Select(f => f.Name).Should().BeEquivalentTo("ui/index.html", "ui/js/app.js");
        strategy.PageFiles.Single(f => f.Name == "ui/index.html").Content.Should().Be(Page);
        strategy.Sources.Should().Contain(f => f.Name == "SpreadArbitrage.cs");
        strategy.Create().Info.Name.Should().Be("Spread arbitrage");

        registry.Find("odds")!.IsStrategy.Should().BeFalse();
    }

    [Fact]
    public void A_Blocks_assembly_is_recognised_from_its_metadata_and_a_widget_assembly_is_not()
    {
        var compiled = _compiler.Compile("recognised", [new StrategyFile("SpreadArbitrage.cs", BlocksUnitCompilerTests.ArbitrageSource)]);
        Directory.CreateDirectory(_root);
        var blocks = Path.Combine(_root, "blocks.dll");
        File.WriteAllBytes(blocks, compiled.Image!);

        BlocksPackage.IsBlocksAssembly(blocks).Should().BeTrue();
        BlocksPackage.UsesOrders(blocks).Should().BeTrue();
        BlocksPackage.IsBlocksAssembly(typeof(IStrategyKernel).Assembly.Location).Should().BeFalse();
        BlocksPackage.IsBlocksAssembly(Path.Combine(_root, "missing.dll")).Should().BeFalse();
    }

    private AuthoredArtifactResult Package(string id, string file, string source)
    {
        var files = new[]
        {
            new StrategyFile(file, source),
            new StrategyFile("ui/index.html", Page),
            new StrategyFile("ui/js/app.js", "dax.ready();"),
        };

        var compiled = _compiler.Compile(id, files);
        compiled.Success.Should().BeTrue(string.Join(" / ", compiled.Errors));

        var result = AuthoredArtifact.Write(new StrategyScript(id, id, files), compiled, Path.Combine(_root, "artifacts"));
        result.Success.Should().BeTrue(result.Message);
        return result;
    }

    private sealed class NoSignature : IPluginSignatureInspector
    {
        public static NoSignature Instance { get; } = new();

        public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
    }
}
