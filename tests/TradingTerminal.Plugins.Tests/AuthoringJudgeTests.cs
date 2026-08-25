using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Agents;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The judge (#48) — real source, the real Roslyn compiler, and all eight rungs in one pass.
///
/// <para>Both halves of the ladder existed before this: rungs 1–4 in the compiler, 5–8 in the verifier.
/// Nothing had joined them, so no code path had ever run the whole thing.</para>
/// </summary>
public sealed class AuthoringJudgeTests
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

    /// <summary>Reads the parameter it declares and draws a real frame, so the rungs that check those
    /// have something to find.</summary>
    private const string GoodKernel = """
        public sealed class TestKernel : IStrategyKernel
        {
            private int _lookback;
            private readonly System.Collections.Generic.List<double> _closes = new(64);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Int("lookback", "Look-back", 20, min: 2, max: 200));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext c, CancellationToken ct)
            {
                _lookback = c.Parameters.GetInt("lookback");
                _closes.Clear();
                return Task.CompletedTask;
            }

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext c, CancellationToken ct)
            {
                if (_closes.Count == 64) _closes.RemoveAt(0);
                _closes.Add(bar.Close);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Test", RenderPanelKind.Chart);
                if (_closes.Count == 0)
                {
                    surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.TextSecondary)));
                    surface.Text(8d, 20d, "Waiting for bars…");
                    return;
                }

                var range = PlotRange.Empty;
                for (var i = 0; i < _closes.Count; i++) range = range.Include(_closes[i]);
                Plot.HorizontalGrid(surface, range.Padded());
                surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
                using var series = surface.Series("Close", RenderSeriesKind.Line);
                for (var i = 0; i < _closes.Count; i++) surface.Push(i, _closes[i]);
            }
        }
        """;

    private static AuthoringJudge Judge(RoutingState? start = null) => new(
        new RoslynStrategyCompiler(),
        "test.unit",
        "Test unit",
        start ?? new RoutingState(HasSpec: true));

    /// <summary>Wraps source the way a model returns it, so the extractor is exercised too.</summary>
    private static string Fenced(string body) =>
        "```csharp" + Environment.NewLine
        + "// file: Unit.cs" + Environment.NewLine
        + body + Environment.NewLine
        + "```";

    private static IReadOnlyList<StrategyFile> Files(string body) =>
        [new StrategyFile("Unit.cs", Ambient + "\n" + body)];

    [Fact]
    public void AGoodKernelClearsTheWholeLadder()
    {
        var verdict = Judge().Judge(Files(GoodKernel));

        verdict.Report.Passed.Should().BeTrue(
            string.Join("; ", verdict.Report.Findings.Select(f => f.ToString())));
        verdict.State.Compiles.Should().BeTrue();
    }

    [Fact]
    public void TheEarlyRungsAreRecordedRatherThanOmitted()
    {
        // A report that leaves out compile, policy and shape understates how much was checked — and the
        // reward is computed from exactly that count.
        var verdict = Judge().Judge(Files(GoodKernel));

        verdict.Report.Steps.Select(s => s.Rung).Should().Contain(
            [VerificationRung.Compile, VerificationRung.Policy, VerificationRung.Shape]);
        verdict.Report.RungsCleared.Should().BeGreaterThan(3);
    }

    [Fact]
    public void CodeThatDoesNotCompileFailsAtTheCompileRungWithUsableFindings()
    {
        var verdict = Judge().Judge(Files("public sealed class Broken : IStrategyKernel { }"));

        verdict.Report.Passed.Should().BeFalse();
        verdict.Report.FailedAt.Should().Be(VerificationRung.Compile);
        verdict.Report.Findings.Should().NotBeEmpty();
        verdict.Report.Findings.Should().OnlyContain(f => f.Remedy != null);
    }

    [Fact]
    public void ACompileFailureRoutesTheNextTurnToTheFixer()
    {
        // The join working end to end: a real compiler error becomes a routing decision.
        var judge = Judge();
        var verdict = judge.Judge(Files("public sealed class Broken : IStrategyKernel { }"));

        AgentRouter.Choose(verdict.State, new AgentReliability())!.Role.Should().Be(AgentRole.Fixer);
    }

    [Fact]
    public void AVisualizerIsRecordedAsOwingAPicture()
    {
        // Taken from the resolved type, not the brief — what the author actually wrote is the only
        // reliable answer to whether a picture is owed.
        var verdict = Judge().Judge(Files("""
            public sealed class TestViz : IVisualizer
            {
                public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
                public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
                public Task OnStartAsync(IVisualizerContext c, CancellationToken ct) => Task.CompletedTask;
            }
            """));

        verdict.State.MustDraw.Should().BeTrue();
        verdict.Report.Passed.Should().BeFalse("a visualizer that paints nothing has no other purpose");
    }

    [Fact]
    public void ReviewedIsAHumansWordAndNeverTheLaddersS()
    {
        // Otherwise a clean verdict would route straight past the Reviewer, and the one agent covering
        // what verification cannot see would never run.
        var judge = Judge();

        judge.Judge(Files(GoodKernel)).State.Reviewed.Should().BeFalse();

        judge.MarkReviewed();
        judge.State.Reviewed.Should().BeTrue();
    }

    [Fact]
    public void AGoodKernelFinishesTheRunOnceReviewed()
    {
        var judge = Judge();
        judge.Judge(Files(GoodKernel));
        judge.MarkReviewed();

        AgentRouter.Choose(judge.State, new AgentReliability())
            .Should().BeNull("nothing is left to do");
    }

    [Fact]
    public void TheCompileResultIsKeptSoRegistrationNeedNotCompileAgain()
    {
        var judge = Judge();
        judge.Judge(Files(GoodKernel));

        judge.Latest.Should().NotBeNull();
        judge.Latest!.Unit.Should().NotBeNull();
        judge.Latest.Unit!.Kind.Should().Be(AuthoringKind.Strategy);
    }

    // ── the whole thing, composed ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnAgentRunDeliversARealCompiledVerifiedUnit()
    {
        // The capstone: routing picks the agent, a reply is compiled by the real compiler, driven up all
        // eight rungs, and the verdict decides what happens next. Every piece built this session, joined.
        var judge = Judge();
        var client = new OneShotClient(Fenced(Ambient + Environment.NewLine + GoodKernel));

        var loop = new AgentLoop(client, files =>
        {
            var verdict = judge.Judge(files);
            // A human would say this; the test stands in for one so the run can finish.
            if (verdict.Report.Passed) judge.MarkReviewed();
            return new VerdictAndState(verdict.Report, judge.State);
        });

        var run = await loop.RunAsync("an EMA of the close", "PACK", judge.State);

        run.Outcome.Should().Be(AgentRunOutcome.Delivered);
        run.Turns.Should().ContainSingle().Which.Reward.Should().BeGreaterThan(0.5d);
        judge.Latest!.Unit!.Type.Name.Should().Be("TestKernel");
    }

    [Fact]
    public async Task ARunThatCannotSucceedStopsAtItsBudgetRatherThanForever()
    {
        // Under a token budget this is the property that matters most: the honest end to an
        // unsatisfiable brief is "here is what did not work", not another attempt.
        var judge = Judge();
        var client = new OneShotClient(Fenced("public sealed class Broken : IStrategyKernel { }"));

        var run = await new AgentLoop(client, judge.Judge).RunAsync("brief", "PACK", judge.State, maxTurns: 3);

        run.Outcome.Should().Be(AgentRunOutcome.BudgetExhausted);
        run.Turns.Should().HaveCount(3);
        run.Turns.Should().OnlyContain(t => t.Reward == 0d);
    }

    /// <summary>Returns the same reply every turn, which is what a model that cannot fix its own mistake
    /// looks like from here.</summary>
    private sealed class OneShotClient(string reply) : IStrategyCodegenClient
    {
        public string ProviderId => "one-shot";
        public string DisplayName => "One shot";
        public bool IsAvailable => true;

        public Task<StrategyCodegenResponse> GenerateAsync(
            StrategyCodegenRequest request, CancellationToken ct = default) =>
            Task.FromResult(new StrategyCodegenResponse(
                Success: true,
                Code: null,
                RawText: reply,
                Error: null,
                Files: CodegenCodeExtractor.ExtractFiles(reply)));
    }

    [Fact]
    public void AGoodKernelEarnsMoreThanABrokenOne()
    {
        // The reward the router learns from, computed over a real run rather than a scripted report.
        var good = LadderFeedback.RewardFor(Judge().Judge(Files(GoodKernel)).Report);
        var bad = LadderFeedback.RewardFor(
            Judge().Judge(Files("public sealed class Broken : IStrategyKernel { }")).Report);

        good.Should().BeGreaterThan(bad);
        bad.Should().Be(0d);
    }
}
