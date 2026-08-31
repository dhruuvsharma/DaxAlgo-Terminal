using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.UI.Strategies;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The whole of #44 phase 6, in one pass: author a unit, package it, install it, then load it the way a
/// fresh start would and check it reaches the catalog.
///
/// <para>This is the test the feature exists for. Everything upstream — compiling, verifying, drawing,
/// registering — was equally true of a strategy that vanished when the user closed the terminal, and a
/// strategy you have to regenerate every morning is not a strategy anyone will trust with money.</para>
///
/// <para>Nothing is stubbed. Real Roslyn, the real package writer and reader, the real installer with its
/// trust and scan gates, the real loader with its own assembly load context, and the real registries the
/// catalog reads.</para>
/// </summary>
public sealed class InstalledUnitSurvivesRestartTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daxalgo-restart-" + Guid.NewGuid().ToString("N"));

    private string Plugins => Path.Combine(_root, "plugins");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Ambient = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using DaxAlgo.Sdk;
        using DaxAlgo.Sdk.Drawing;
        using TradingTerminal.Core.Domain;
        using TradingTerminal.Core.Strategies;
        using TradingTerminal.Core.Strategies.Parameters;
        """;

    private const string Kernel = """
        public sealed class RestartKernel : IStrategyKernel
        {
            private readonly System.Collections.Generic.List<double> _closes = new(32);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 14, min: 2, max: 200));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct)
            {
                _ = c.Parameters.GetInt("lookback");
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext c, CancellationToken ct)
            {
                if (_closes.Count == 32) _closes.RemoveAt(0);
                _closes.Add(bar.Close);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Restart", RenderPanelKind.Chart);
                if (_closes.Count == 0) { Plot.Waiting(surface); return; }
                Series.Draw(surface, "Close", _closes);
            }
        }
        """;

    /// <summary>Author it, package it, install it — everything that happens before the user quits.</summary>
    private string InstallAuthoredUnit(string id = "restart.unit", string displayName = "Restart unit")
    {
        var script = new StrategyScript(id, displayName, [new StrategyFile("Unit.cs", Ambient + "\n" + Kernel)]);
        var compiled = new RoslynStrategyCompiler().Compile(script);
        Assert.True(compiled.Success, string.Join("; ", compiled.Errors.Select(e => e.Message)));

        var artifact = AuthoredArtifact.Write(script, compiled, Path.Combine(_root, "authored"));
        Assert.True(artifact.Success, artifact.Message);

        var install = PluginInstaller.InstallFromArtifact(
            artifact.Path!, Plugins, PluginTrustPolicy.Permissive, NoSignature.Instance,
            scanMode: PluginScanMode.Enforce);
        Assert.True(install.Success, install.Message);

        return artifact.Path!;
    }

    /// <summary>Everything that happens on the next launch: load the plugins folder, then bind whatever
    /// came back into the registries the catalog reads.</summary>
    private (PluginLoadReport Report, StrategyKernelRegistry Kernels, VisualizerRegistry Visualizers) Restart()
    {
        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(),
            Plugins,
            DaxAlgo.Sdk.SdkInfo.Version,
            PluginTrustPolicy.Permissive,
            NoSignature.Instance);

        var kernels = new StrategyKernelRegistry();
        var visualizers = new VisualizerRegistry();

        PluginUnitBinder.Bind(
            report.Loaded
                .Where(p => p.Image is not null)
                .Select(p =>
                {
                    var manifest = ManifestOf(p);
                    return (
                        PluginId: manifest?.Id ?? Path.GetFileNameWithoutExtension(p.AssemblyPath),
                        DisplayName: manifest?.Name,
                        Image: p.Image!);
                }),
            kernels,
            visualizers);

        return (report, kernels, visualizers);
    }

    /// <summary>The manifest that travelled inside the package — both halves of the unit's identity,
    /// read exactly as the shell reads them.</summary>
    private static PluginManifest? ManifestOf(LoadedPlugin plugin) =>
        PluginManifest.TryRead(Path.GetDirectoryName(plugin.AssemblyPath)!);

    // ── the point of the whole thing ────────────────────────────────────────────────────────────

    [Fact]
    public void AnAuthoredStrategyIsStillThereAfterARestart()
    {
        InstallAuthoredUnit();

        var (_, kernels, _) = Restart();

        Assert.Single(kernels.All);
        Assert.NotNull(kernels.Find("restart.unit"));
    }

    [Fact]
    public void TheReloadedStrategyBecomesACatalogCard()
    {
        // The LAST hop, and the one that was missing for months on the authored side: a registration
        // is not a card. Nothing in the tree read IStrategyKernelRegistry — the sink wrote to it, this
        // binder wrote to it, DI built it, and no reader existed — so an installed strategy reached
        // the registry and stopped there.
        //
        // The card construction is also where an authored kernel crashed with a NullReferenceException
        // (`Strategy?.DisplayName ?? Visualizer!.DisplayName`, safe with two backings and not with
        // three). An installed package produces the same kind of registration, so it would have
        // crashed identically — and this route had no test to catch it.
        InstallAuthoredUnit(id: "shelf.momentum", displayName: "Shelf momentum");

        var (_, kernels, _) = Restart();
        var card = new StrategyCatalogItemViewModel(kernels.Find("shelf.momentum")!);

        Assert.Equal(CatalogItemKind.Strategy, card.Kind);
        Assert.Equal("shelf.momentum", card.Id);
        Assert.Equal("Shelf momentum", card.Name);
        Assert.NotNull(card.Kernel);
        Assert.Equal("Open", card.PrimaryActionLabel);

        // Quick backtest is a plugin-strategy affordance and the engine was archived, so an authored
        // kernel — installed or not — must not offer it.
        Assert.False(card.HasQuickBacktest);
    }

    [Fact]
    public void EveryInstalledUnitBuildsACardWithoutThrowing()
    {
        // Two packages whose types share a name, which is what single-file generated units look like.
        // Building every row is the assertion: the catalog constructs all of them at start-up, so one
        // that throws takes the whole window down rather than losing a card.
        InstallAuthoredUnit(id: "shelf.one", displayName: "Shelf one");
        InstallAuthoredUnit(id: "shelf.two", displayName: "Shelf two");

        var (_, kernels, _) = Restart();

        var cards = kernels.All.Select(r => new StrategyCatalogItemViewModel(r)).ToList();

        Assert.Equal(2, cards.Count);
        Assert.Equal(["Shelf one", "Shelf two"], cards.Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.All(cards, c => Assert.False(string.IsNullOrWhiteSpace(c.Id)));
    }

    [Fact]
    public void TheCardCarriesTheDataTheInstalledUnitDeclared()
    {
        // The pills on a card are how a user knows which brokers can feed a strategy before opening
        // it. They have to survive the package, the staging folder and a fresh load context.
        InstallAuthoredUnit(id: "shelf.bars", displayName: "Shelf bars");

        var (_, kernels, _) = Restart();
        var card = new StrategyCatalogItemViewModel(kernels.Find("shelf.bars")!);

        Assert.Contains(card.DataRequirementTags, tag => tag.Contains("Bars", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheReloadedStrategyCanActuallyBeConstructed()
    {
        // A card with nothing behind it is the failure the registry was built to end. The factory has to
        // work after the assembly has been through a package, a staging folder and a fresh load context.
        InstallAuthoredUnit();

        var (_, kernels, _) = Restart();
        var created = kernels.Find("restart.unit")!.Create();

        Assert.NotNull(created);
        Assert.Equal("RestartKernel", created.GetType().Name);
    }

    [Fact]
    public void TheCardKeepsTheNameTheAuthorGaveItRatherThanTheTypeName()
    {
        // Both halves of the identity survive because the plugin manifest travels inside the package.
        //
        // This test asserted only the ID and was named as though it asserted the NAME — and the name
        // was in fact wrong the whole time: the binder passed the id on and nothing else, so
        // `FromType` humanised the type name and a strategy the author called "My momentum" appeared
        // in the catalog as "Restart". A test that checks the cheap half under the name of the
        // expensive half is worse than no test, because it reads as covered.
        InstallAuthoredUnit(id: "my.momentum", displayName: "My momentum");

        var (_, kernels, _) = Restart();
        var registration = kernels.Find("my.momentum");

        Assert.NotNull(registration);
        Assert.Equal("My momentum", registration!.Descriptor.DisplayName);
        Assert.DoesNotContain("Restart", registration.Descriptor.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLoaderRecordsAKernelOnlyAssemblyAsLoaded()
    {
        // The specific defect: an assembly with no IStrategyPlugin used to be dropped without a word, so
        // an installed authored strategy was a folder the next start walked past in silence.
        InstallAuthoredUnit();

        var (report, _, _) = Restart();

        Assert.Single(report.Loaded);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void TwoStrategiesWithTheSameTypeNameDoNotReplaceEachOther()
    {
        // A model writing single-file units produces type names like "MomentumKernel" in no namespace,
        // over and over. Keyed by type name the second install would silently take the first one's place
        // in the catalog; keyed by the package id they coexist, which is what the user meant.
        InstallAuthoredUnit(id: "alpha.one", displayName: "Alpha one");
        InstallAuthoredUnit(id: "alpha.two", displayName: "Alpha two");

        var (_, kernels, _) = Restart();

        Assert.Equal(2, kernels.All.Count);
        Assert.NotNull(kernels.Find("alpha.one"));
        Assert.NotNull(kernels.Find("alpha.two"));
    }

    [Fact]
    public void TheInstalledFolderKeepsTheManifestSoIdentitySurvives()
    {
        InstallAuthoredUnit(id: "kept.identity", displayName: "Kept identity");

        var folder = Directory.EnumerateDirectories(Plugins).Single();
        var manifest = PluginManifest.TryRead(folder);

        Assert.NotNull(manifest);
        Assert.Equal("kept.identity", manifest!.Id);
        Assert.Equal("Kept identity", manifest.Name);
    }

    [Fact]
    public void TheManifestDeclaresNoPermissionsOnTheAuthorsBehalf()
    {
        // Declared permissions downgrade warn-level scan findings to "disclosed". A generated unit has no
        // business disclosing capabilities for its author; silence means the scanner reports what it finds.
        InstallAuthoredUnit();

        var folder = Directory.EnumerateDirectories(Plugins).Single();

        Assert.Null(PluginManifest.TryRead(folder)!.Permissions);
    }

    [Fact]
    public void TheSourceTravelsWithTheInstalledStrategy()
    {
        // What makes an installed strategy reviewable a year later by someone who was not there.
        InstallAuthoredUnit();

        var folder = Directory.EnumerateDirectories(Plugins).Single();
        var sources = Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories).ToArray();

        Assert.NotEmpty(sources);
        Assert.Contains("class RestartKernel", File.ReadAllText(sources[0]));
    }

    /// <summary>A plugin whose signature is never inspected — what Permissive means in practice.</summary>
    private sealed class NoSignature : IPluginSignatureInspector
    {
        public static NoSignature Instance { get; } = new();

        public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
    }
}
