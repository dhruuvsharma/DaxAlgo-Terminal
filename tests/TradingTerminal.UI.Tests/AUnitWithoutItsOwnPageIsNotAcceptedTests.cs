using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The terminal accepts strategies and visualizers that draw their own HTML/CSS page, and nothing else
/// (owner decision, 2026-09-15 — see <see cref="UnitPageRule"/>). This is the widget SDK's end of it:
/// a unit drawn through the terminal's controls still compiles and still packages, and is refused when
/// it tries to be installed or loaded.
///
/// <para>It replaces the suite that asserted the opposite — a widget-SDK unit surviving a restart. What
/// that suite was really protecting, an authored unit still being in the catalog after a restart, is
/// asserted for Blocks units in <c>ABlocksUnitSurvivesARestartTests</c>, end to end and with a page.</para>
/// </summary>
public sealed class AUnitWithoutItsOwnPageIsNotAcceptedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "daxalgo-no-page-" + Guid.NewGuid().ToString("N"));

    private string Units => Path.Combine(_root, "units");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private const string Kernel = """
        using System.Threading;
        using System.Threading.Tasks;
        using DaxAlgo.Sdk;
        using DaxAlgo.Sdk.Drawing;
        using TradingTerminal.Core.Domain;
        using TradingTerminal.Core.Strategies;
        using TradingTerminal.Core.Strategies.Parameters;

        public sealed class WidgetKernel : IStrategyKernel
        {
            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 14, min: 2, max: 200));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct)
            {
                _ = c.Parameters.GetInt("lookback");
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext c, CancellationToken ct) => Task.CompletedTask;

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Widget", RenderPanelKind.Chart);
                Plot.Waiting(surface);
            }
        }
        """;

    /// <summary>Compiles the unit and returns what a package would carry: the assembly image.</summary>
    private static StrategyCompileResult Compile(string id)
    {
        var compiled = new RoslynStrategyCompiler().Compile(
            new StrategyScript(id, id, [new StrategyFile("Unit.cs", Kernel)]));

        Assert.True(compiled.Success, string.Join("; ", compiled.Errors.Select(e => e.Message)));
        return compiled;
    }

    /// <summary>Puts a compiled widget unit in a units folder the way an older build's install left one.</summary>
    private string PlaceInUnitsFolder(string name)
    {
        var compiled = Compile(name);
        var folder = Path.Combine(Units, name);
        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, name + ".dll"), compiled.Authored!.Image);
        File.WriteAllText(
            Path.Combine(folder, "plugin.json"),
            $$"""{"id":"{{name}}","name":"{{name}}","version":"1.0.0","targetSdkVersion":"{{DaxAlgo.Sdk.SdkInfo.Version}}"}""");

        return folder;
    }

    [Fact]
    public void ThePackagedWidgetUnitIsRefusedByTheInstaller()
    {
        var compiled = Compile("widget.unit");
        var script = new StrategyScript("widget.unit", "Widget unit", [new StrategyFile("Unit.cs", Kernel)]);
        var artifact = AuthoredArtifact.Write(script, compiled, Path.Combine(_root, "authored"));
        Assert.True(artifact.Success, artifact.Message);

        var install = PluginInstaller.InstallFromArtifact(
            artifact.Path!, Units, PluginTrustPolicy.Permissive, NoSignature.Instance, scanMode: PluginScanMode.Enforce);

        Assert.False(install.Success);
        // The manifest has no page payload, so it is refused before the assembly is read; the widget-SDK
        // wording belongs to the staged-folder rule, which only a package WITH a page ever reaches.
        Assert.Contains("no page", install.Message, StringComparison.Ordinal);
        Assert.Contains(UnitPageRule.Entry, install.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(Units, "widget.unit")), "nothing is written for a refused unit");
    }

    [Fact]
    public void OneAlreadyInTheUnitsFolderIsSkippedAtStartWithItsReason()
    {
        // Every unit installed before the rule is in exactly this state, so the start that follows the
        // upgrade has to say what happened rather than losing the card in silence.
        PlaceInUnitsFolder("legacy.widget");

        var report = PluginLoader.LoadSandboxedWithReport(
            new ServiceCollection(), Units, DaxAlgo.Sdk.SdkInfo.Version,
            PluginTrustPolicy.Permissive, new PluginStateStore(Units));

        Assert.Empty(report.Loaded);
        var problem = Assert.Single(report.Problems);
        Assert.Equal(PluginLoadOutcome.NoPage, problem.Outcome);
        Assert.Contains(UnitPageRule.Entry, problem.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsNotQuarantined()
    {
        // A quarantine is for something unsafe, and it persists: the unit would stay refused after it
        // grew a page. Nothing here is unsafe — it is simply not a unit this terminal shows.
        PlaceInUnitsFolder("legacy.widget");
        var state = new PluginStateStore(Units);

        PluginLoader.LoadSandboxedWithReport(
            new ServiceCollection(), Units, DaxAlgo.Sdk.SdkInfo.Version, PluginTrustPolicy.Permissive, state);

        Assert.Null(state.QuarantineFor("legacy.widget"));
    }

    [Fact]
    public void ABlocksUnitWithNoPageIsRefusedForTheSameReason()
    {
        // Not a widget-SDK rule: a Blocks unit that draws nothing is equally unshowable, and the message
        // says which half is missing rather than talking about the SDK it was written against.
        var folder = Path.Combine(_root, "blocks-unit");
        Directory.CreateDirectory(folder);
        var assembly = Path.Combine(folder, "unit.dll");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TradingTerminal.Blocks.Runtime.dll"), assembly);

        var refusal = UnitPageRule.RefuseFolder(folder, assembly);

        Assert.NotNull(refusal);
        Assert.Contains("no page", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("widget SDK", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMarketplacePluginsRootIsUnaffected()
    {
        // The rule is about the units a user authors here, which is what the sandboxed root holds. The
        // ordinary plugins root — where an IStrategyPlugin brings its own window — is loaded by the other
        // entry point and is not asked for a page.
        PlaceInUnitsFolder("legacy.widget");

        var report = PluginLoader.LoadWithReport(
            new ServiceCollection(), Units, DaxAlgo.Sdk.SdkInfo.Version,
            PluginTrustPolicy.Permissive, NoSignature.Instance);

        Assert.Single(report.Loaded);
        Assert.Empty(report.Problems);
    }

    /// <summary>A plugin whose signature is never inspected — what Permissive means in practice.</summary>
    private sealed class NoSignature : IPluginSignatureInspector
    {
        public static NoSignature Instance { get; } = new();

        public PluginSignature Inspect(string assemblyPath) => PluginSignature.Unsigned;
    }
}
