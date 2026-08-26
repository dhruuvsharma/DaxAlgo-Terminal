using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The Strategy/Visualizer switch, now that it does something (#43 phase 4).
///
/// <para>It used to be decoration: the pane kept the choice with the session, said so in a notice, and
/// sent an identical prompt either way. A user who asked for a visualizer got a strategy and had to
/// notice for themselves.</para>
/// </summary>
public sealed class AuthoringKindTests
{
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

    private const string Visualizer = """
        public sealed class KindViz : IVisualizer
        {
            public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
            public Task OnStartAsync(IVisualizerContext c, CancellationToken ct) => Task.CompletedTask;
            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Viz", RenderPanelKind.Chart);
                Plot.Waiting(surface, "no data");
            }
        }
        """;

    private const string Kernel = """
        public sealed class KindKernel : IStrategyKernel
        {
            public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
            public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct) => Task.CompletedTask;
            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Kernel", RenderPanelKind.Chart);
                Plot.Waiting(surface, "no data");
            }
        }
        """;

    // ── the prompt ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AVisualizerSessionIsToldItHasNoBook()
    {
        // The one structural difference between the contracts, and the one a model gets wrong: it writes
        // context.Book into a visualizer, where no such member exists.
        var brief = AuthoringKindBrief.For(AuthoringKind.Visualizer);

        brief.Should().Contain("IVisualizer");
        brief.Should().Contain("no `Book`");
    }

    [Fact]
    public void AStrategySessionIsToldWhereItsPositionsGo()
    {
        var brief = AuthoringKindBrief.For(AuthoringKind.Strategy);

        brief.Should().Contain("IStrategyKernel");
        brief.Should().Contain("Book");
    }

    [Fact]
    public void TheTwoBriefsAreDifferent()
    {
        // The whole defect in one assertion: before this, the prompt was identical either way.
        AuthoringKindBrief.For(AuthoringKind.Visualizer)
            .Should().NotBe(AuthoringKindBrief.For(AuthoringKind.Strategy));
    }

    [Fact]
    public void TheSharedPackIsKeptWholeAndTheBlockIsAppended()
    {
        // The shared text is the cached prefix. Prepending or interleaving would change it per kind and
        // throw away the cache the system-prompt split exists to earn.
        const string pack = "SHARED PACK TEXT";
        var composed = AuthoringKindBrief.Compose(pack, AuthoringKind.Visualizer);

        composed.Should().StartWith(pack);
        composed.Should().Contain("VISUALIZER");
    }

    // ── the enforcement ─────────────────────────────────────────────────────────────────────────

    private static StrategyBuildSession Session(AuthoringKind kind) =>
        new StrategyCodegenOrchestrator(new RoslynStrategyCompiler())
            .CreateSession(
                new SilentClient(), "PACK", "kind.test", "Kind test", maxFixAttempts: 0, kind: kind);

    private static StrategyCompileResult Compile(string body) =>
        new RoslynStrategyCompiler().Compile(
            new StrategyScript("kind.test", "Kind test", [new StrategyFile("Unit.cs", Ambient + "\n" + body)]));

    [Fact]
    public void TheSessionRemembersWhichKindItIsWriting()
    {
        Session(AuthoringKind.Visualizer).Kind.Should().Be(AuthoringKind.Visualizer);
        Session(AuthoringKind.Strategy).Kind.Should().Be(AuthoringKind.Strategy);
    }

    [Fact]
    public void TheKindBlockReachesTheSystemPrompt()
    {
        Session(AuthoringKind.Visualizer).SystemContext.Should().Contain("VISUALIZER");
        Session(AuthoringKind.Strategy).SystemContext.Should().Contain("STRATEGY");
    }

    [Fact]
    public void AVisualizerCompilesAsAVisualizer()
    {
        Compile(Visualizer).Unit!.Kind.Should().Be(AuthoringKind.Visualizer);
    }

    [Fact]
    public void AKernelCompilesAsAStrategy()
    {
        Compile(Kernel).Unit!.Kind.Should().Be(AuthoringKind.Strategy);
    }

    /// <summary>A client that never answers — these tests drive the session's own logic, not a model.</summary>
    private sealed class SilentClient : IStrategyCodegenClient
    {
        public string ProviderId => "silent";
        public string DisplayName => "Silent";
        public bool IsAvailable => false;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
