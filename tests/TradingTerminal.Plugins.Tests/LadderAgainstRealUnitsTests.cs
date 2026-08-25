using DaxAlgo.Sandbox.Samples;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The ladder run against the shipped samples — units that are known to be correct, CI-compiled, and
/// exactly what a model is shown as an example.
///
/// <para><b>These matter more than the negative cases.</b> A verifier that fails a good unit sends a
/// repair agent to rewrite working code: it burns the user's tokens, and it teaches the router that the
/// agent which produced correct work is unreliable. A false pass costs one bad artifact; a false
/// failure poisons the reward signal.</para>
///
/// <para>If an exemplar ever stops clearing the ladder, one of the two is wrong and both are worth
/// looking at.</para>
/// </summary>
public sealed class LadderAgainstRealUnitsTests
{
    [Fact]
    public void TheSampleVisualizerDrawsWellEnoughToPassRungSeven()
    {
        var visualizer = new SpreadBandVisualizer();
        Feed(visualizer, [99d, 100d, 101d, 102d, 103d]);

        DrawProbe.Run(visualizer.Draw, mustDraw: true, requirePicture: true)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void TheSampleStrategyDrawsWellEnoughToPassRungSeven()
    {
        var kernel = new MovingAverageCrossKernel();
        Feed(kernel, [104d, 103d, 102d, 101d, 100d, 106d, 112d, 118d]);

        DrawProbe.Run(kernel.Draw, mustDraw: false, requirePicture: true)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void AnUnstartedUnitStillSaysSomethingRatherThanPaintingNothing()
    {
        // The first seconds of every session. The samples draw a "waiting" message, which is the
        // difference between an empty panel and one that looks broken — so the probe must see a real
        // picture here too, not a blank.
        DrawProbe.Run(new SpreadBandVisualizer().Draw, mustDraw: true)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void BothSamplesSurviveACollapsedPanel()
    {
        var visualizer = new SpreadBandVisualizer();
        Feed(visualizer, [99d, 100d, 101d, 102d]);

        DrawProbe.RunDegenerate(visualizer.Draw).Outcome.Should().Be(VerificationOutcome.Passed);
        DrawProbe.RunDegenerate(new MovingAverageCrossKernel().Draw).Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void TheSampleStrategyReadsEveryParameterItDeclares()
    {
        // Rung 5 against a unit that genuinely reads all five — including two it only touches inside
        // OnBarAsync, which is why the drive has to reach the data callbacks and not just OnStartAsync.
        var kernel = new MovingAverageCrossKernel();
        var recorded = Feed(kernel, [104d, 103d, 102d, 101d, 100d, 106d, 112d, 118d]);

        SchemaCoherenceProbe.Run(kernel.Schema, recorded.Parameters.KeysRead)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void TheSampleVisualizerReadsEveryParameterItDeclares()
    {
        var visualizer = new SpreadBandVisualizer();
        var recorded = Feed(visualizer, [99d, 100d, 101d, 102d]);

        SchemaCoherenceProbe.Run(visualizer.Schema, recorded.Parameters.KeysRead)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void RungFiveWouldCatchTheSampleStrategyIfAParameterStoppedBeingRead()
    {
        // Proves the pass above is not vacuous: bolt an extra declared parameter onto the real schema
        // that nothing reads, and rung 5 fails exactly as it should.
        var kernel = new MovingAverageCrossKernel();
        var recorded = Feed(kernel, [104d, 103d, 102d, 101d, 100d, 106d, 112d, 118d]);

        var inflated = new StrategyParameterSchema(
            [.. kernel.Schema.Parameters, StrategyParameter.Int("neverRead", "Never read", 5)]);

        var step = SchemaCoherenceProbe.Run(inflated, recorded.Parameters.KeysRead);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().ContainSingle().Which.Message.Should().Contain("neverRead");
    }

    [Fact]
    public void TheSampleStrategySurvivesBeingDriven()
    {
        // Rung 6 against the real thing: start, eight bars, and the callbacks in the order the host
        // calls them.
        var kernel = new MovingAverageCrossKernel();

        LifecycleProbe.Run(
            () => Feed(kernel, [104d, 103d, 102d, 101d, 100d, 106d, 112d, 118d]),
            phase: "the strategy drive")
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void TheSampleStrategyTradesCoherently()
    {
        // Rung 8. The sample attaches a protective stop below its long entry, so a sign error here
        // would surface as replay.stop-wrong-side rather than as a passing test.
        var kernel = new MovingAverageCrossKernel();
        var drive = Feed(kernel, [104d, 103d, 102d, 101d, 100d, 106d, 112d, 118d]);

        drive.Book.Intents.Should().NotBeEmpty("the series crosses");
        ReplayProbe.Run(drive.Book.Intents, drive.Instruments)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void ThePictureAndTheBookTellTheSameStory()
    {
        // The strongest assertion the ladder can make, and it spans two rungs: every position the
        // strategy took is a mark on its chart. A strategy whose picture disagrees with its book is
        // worse than one that draws nothing, because it is confidently wrong.
        var kernel = new MovingAverageCrossKernel();
        var drive = Feed(kernel, [104d, 103d, 102d, 101d, 100d, 106d, 112d, 118d]);

        var surface = new RecordingRenderSurface();
        kernel.Draw(surface);

        surface.Markers.Should().HaveCount(drive.Book.Intents.Count);
    }

    [Fact]
    public void TheSampleVisualizerNeverTrades()
    {
        // A visualizer has no book at all, so this is really a check on the harness: if a visualizer
        // ever reached one, the contract split would have broken somewhere upstream.
        var visualizer = new SpreadBandVisualizer();

        Feed(visualizer, [99d, 100d, 101d, 102d]).Book.Intents.Should().BeEmpty();
    }

    // ── driving ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Periods short enough that a readable series actually reaches the trading path. The
    /// shipped defaults are a 10/30 cross, which on eight bars returns before it decides anything.</summary>
    private static readonly Dictionary<string, object?> FastPeriods = new()
    {
        [MovingAverageCrossKernel.FastPeriodParameter] = 2,
        [MovingAverageCrossKernel.SlowPeriodParameter] = 3,
    };

    private static readonly Dictionary<string, object?> ShortLookback = new()
    {
        [SpreadBandVisualizer.LookbackParameter] = 3,
    };

    private static SampleDrive.Drive Feed(MovingAverageCrossKernel kernel, double[] closes) =>
        SampleDrive.Run(
            kernel.Schema,
            (context, ct) => kernel.OnStartAsync(context, ct),
            (bar, context, ct) => kernel.OnBarAsync(bar, context, ct),
            closes,
            FastPeriods);

    private static SampleDrive.Drive Feed(SpreadBandVisualizer visualizer, double[] closes) =>
        SampleDrive.RunVisualizer(
            visualizer.Schema,
            (context, ct) => visualizer.OnStartAsync(context, ct),
            (bar, context, ct) => visualizer.OnBarAsync(bar, context, ct),
            closes,
            ShortLookback);
}
