using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The gate — real source, the real Roslyn compiler, and all eight rungs in one pass.
///
/// <para>Both halves of the ladder existed before this: rungs 1–4 in the compiler, 5–8 in the verifier.
/// Nothing had joined them, so no code path had ever run the whole thing.</para>
///
/// <para><b>It answers one question now.</b> As <c>AuthoringJudge</c> it also carried a private
/// <c>RoutingState</c> and advanced it on every verdict, so the judge and its caller each held half a
/// truth about the same session. The routing went with the committee; what is left is "is this
/// candidate wrong, and where" — the cheap deterministic gate everything expensive runs behind.</para>
/// </summary>
public sealed class UnitGateTests
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

    private static UnitGate Gate() => new(new RoslynStrategyCompiler(), "test.unit", "Test unit");

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
        var result = Gate().Run(Files(GoodKernel));

        result.Report.Passed.Should().BeTrue(
            string.Join("; ", result.Report.Findings.Select(f => f.ToString())));
        result.Compile!.Success.Should().BeTrue();
    }

    [Fact]
    public void TheEarlyRungsAreRecordedRatherThanOmitted()
    {
        // A report that leaves out compile, policy and shape understates how much was checked — and the
        // reward is computed from exactly that count.
        var result = Gate().Run(Files(GoodKernel));

        result.Report.Steps.Select(s => s.Rung).Should().Contain(
            [VerificationRung.Compile, VerificationRung.Policy, VerificationRung.Shape]);
        result.Report.RungsCleared.Should().BeGreaterThan(3);
    }

    [Fact]
    public void CodeThatDoesNotCompileFailsAtTheCompileRungWithUsableFindings()
    {
        var result = Gate().Run(Files("public sealed class Broken : IStrategyKernel { }"));

        result.Report.Passed.Should().BeFalse();
        result.Report.FailedAt.Should().Be(VerificationRung.Compile);
        result.Report.Findings.Should().NotBeEmpty();
        result.Report.Findings.Should().OnlyContain(f => f.Remedy != null);
    }

    [Fact]
    public void AVisualizerThatPaintsNothingIsRefused()
    {
        // Taken from the resolved type, not the brief — what the author actually wrote is the only
        // reliable answer to whether a picture is owed, and the verifier reads it from the interface
        // the unit implements.
        var result = Gate().Run(Files("""
            public sealed class TestViz : IVisualizer
            {
                public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;
                public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;
                public Task OnStartAsync(IVisualizerContext c, CancellationToken ct) => Task.CompletedTask;
            }
            """));

        result.Report.Passed.Should().BeFalse("a visualizer that paints nothing has no other purpose");
        result.Report.FailedAt.Should().Be(VerificationRung.DrawProbe);
    }

    [Fact]
    public void TheCompileResultIsKeptSoRegistrationNeedNotCompileAgain()
    {
        var gate = Gate();
        gate.Run(Files(GoodKernel));

        gate.Latest.Should().NotBeNull();
        gate.Latest!.Unit.Should().NotBeNull();
        gate.Latest.Unit!.Kind.Should().Be(AuthoringKind.Strategy);
    }

    [Fact]
    public void AGoodKernelEarnsMoreThanABrokenOne()
    {
        // The score the trajectory log records and the stall detector compares, computed over a real
        // run rather than a scripted report.
        var good = LadderScore.RewardFor(Gate().Run(Files(GoodKernel)).Report);
        var bad = LadderScore.RewardFor(
            Gate().Run(Files("public sealed class Broken : IStrategyKernel { }")).Report);

        good.Should().BeGreaterThan(bad);
        bad.Should().Be(0d);
    }
}
