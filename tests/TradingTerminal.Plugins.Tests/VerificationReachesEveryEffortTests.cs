using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A unit whose picture never paints must be reported at the effort the user actually chose.
///
/// <para>The ladder was gated behind <c>Verify</c>, which only Deep and Max set — so at Quick and
/// Standard, which is the default and what most builds use, a visualizer that compiles and paints
/// nothing was handed over silently. The check spends no tokens: it is local, calls no provider and
/// costs milliseconds, so it was never the sort of thing the effort dial is for.</para>
///
/// <para><b>Measured, not hypothesised.</b> The benchmark's second live run produced exactly that unit
/// — 677 lines, compiled first try, main panel dead — on the strongest model available, at Standard.
/// See <c>docs/authored-unit-gaps-model-half.md</c>.</para>
/// </summary>
public sealed class VerificationReachesEveryEffortTests
{
    /// <summary>A visualizer that compiles, runs, and paints nothing. The failure rung 7 exists for,
    /// and the one no other rung can see.</summary>
    private const string SilentVisualizer = """
        ```csharp
        // file: SilentVisualizer.cs
        public sealed class SilentVisualizer : IVisualizer
        {
            public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
            public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
            public void Draw(IRenderSurface surface) { }
        }
        ```
        """;

    [Theory]
    [InlineData(StrategyBuildEffort.Quick)]
    [InlineData(StrategyBuildEffort.Standard)]
    [InlineData(StrategyBuildEffort.Deep)]
    [InlineData(StrategyBuildEffort.Max)]
    public async Task A_unit_that_paints_nothing_is_reported_at_every_effort(StrategyBuildEffort effort)
    {
        var turn = await BuildAsync(effort);

        turn.Success.Should().BeTrue("it compiles — the point is that compiling is not enough");
        turn.Compile!.Diagnostics
            .Should().Contain(
                d => d.Id == "draw.blank",
                because: $"a silent visualizer must be reported at {effort}, and the ladder is free");
    }

    [Fact]
    public async Task A_unit_that_paints_is_reported_clean()
    {
        // The other direction, so the assertion above cannot pass by warning about everything.
        var turn = await BuildAsync(StrategyBuildEffort.Standard, FakeCodegenClient.DefaultVisualizer);

        turn.Success.Should().BeTrue();
        turn.Compile!.Diagnostics.Should().NotContain(d => d.Id.StartsWith("draw.", StringComparison.Ordinal));
    }

    [Fact]
    public void The_effort_dial_only_carries_things_that_cost_a_generation()
    {
        // The rule the mis-gating broke. Skills, fix attempts, self-review and the agent committee all
        // spend a generation; the ladder does not, so it is not on the dial.
        foreach (var effort in Enum.GetValues<StrategyBuildEffort>())
            StrategyBuildProfile.For(effort).Verify.Should().BeTrue($"{effort} pays nothing for it");
    }

    private static async Task<StrategyBuildTurn> BuildAsync(
        StrategyBuildEffort effort, string reply = SilentVisualizer)
    {
        var profile = StrategyBuildProfile.For(effort);
        var session = new StrategyCodegenOrchestrator(new RoslynStrategyCompiler())
            .CreateSession(
                new FakeCodegenClient(reply), "pack", "silent", "Silent",
                profile.MaxFixAttempts, profile: profile, kind: AuthoringKind.Visualizer);

        return await session.SendAsync("a visualizer");
    }
}
