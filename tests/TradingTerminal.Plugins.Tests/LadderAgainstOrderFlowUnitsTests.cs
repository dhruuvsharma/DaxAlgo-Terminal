using DaxAlgo.Sandbox.Samples;
using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The ladder against units that live on DEPTH and the TAPE rather than on bars.
///
/// <para>This is the class the goal loop is about — an order book, a footprint, an imbalance monitor —
/// and it is the class <see cref="SyntheticDrive"/> could not drive. The drive fed bars and quotes and
/// nothing else: <c>OnDepthAsync</c> and <c>OnTradeAsync</c> were never called, <c>LatestDepth</c>
/// returned null and <c>RecentTrades</c> returned empty. So for every such unit:</para>
///
/// <list type="bullet">
/// <item>rung 6 passed by never entering the only callbacks that mattered, so a throw or a NaN in
/// the depth handler was invisible;</item>
/// <item>rung 7 passed on the unit's own "waiting for depth" message, which is exactly what a
/// completely broken order-flow unit also draws;</item>
/// <item>rung 5 FAILED a correct unit whose parameters are read where its data arrives — and a false
/// failure is the expensive kind. It sends a repair agent to rewrite working code and teaches the
/// router that the agent who wrote it is unreliable.</item>
/// </list>
///
/// <para>The fixtures below are the evidence. <see cref="ThrowsOnDepth"/> is broken in the one place
/// that matters, and <see cref="ReadsParametersWhereItsDataArrives"/> is correct in the one way the
/// drive could not see.</para>
/// </summary>
public sealed class LadderAgainstOrderFlowUnitsTests
{
    [Fact]
    public void TheDriveReachesTheDepthAndTradeCallbacks()
    {
        // The root assertion. Everything below is a consequence of this being false.
        var unit = new CountingVisualizer();

        SyntheticDrive.Run(unit);

        unit.Depths.Should().BeGreaterThan(0, "an order-flow unit is driven by depth, not by bars");
        unit.Trades.Should().BeGreaterThan(0, "signed flow needs prints");
    }

    [Fact]
    public void ABrokenDepthHandlerIsCaughtRatherThanSteppedOver()
    {
        // Rung 6 against a unit that throws in OnDepthAsync. It used to pass: the callback was never
        // entered, so the fault had nowhere to surface.
        LifecycleProbe.Run(
            () => SyntheticDrive.Run(new ThrowsOnDepth()), phase: "the visualizer lifecycle")
            .Outcome.Should().Be(VerificationOutcome.Failed);
    }

    [Fact]
    public void AUnitThatReadsItsParametersWhereItsDataArrivesPassesRungFive()
    {
        // The false failure, directly. This unit is correct — it reads every declared parameter — but
        // reads two of them in OnDepthAsync and OnTradeAsync, which the drive never called.
        var unit = new ReadsParametersWhereItsDataArrives();
        var drive = SyntheticDrive.Run(unit);

        SchemaCoherenceProbe.Run(
                unit.Schema, drive.Parameters.KeysRead, drivenToCompletion: drive.Completed)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void TheOrderFlowExemplarClearsTheLadderItIsShownAsAnExampleOf()
    {
        // BookPressureVisualizer is what a model is given as the worked example for any order-flow
        // brief, and nothing had ever driven it — CI compiled it and stopped there. An exemplar that
        // cannot clear the ladder teaches a shape the verifier then rejects.
        var exemplar = new BookPressureVisualizer();
        var drive = SyntheticDrive.Run(exemplar);

        LifecycleProbe.Run(() => SyntheticDrive.Run(new BookPressureVisualizer()), phase: "the exemplar")
            .Outcome.Should().Be(VerificationOutcome.Passed);

        SchemaCoherenceProbe.Run(
                exemplar.Schema, drive.Parameters.KeysRead, drivenToCompletion: drive.Completed)
            .Outcome.Should().Be(VerificationOutcome.Passed);

        DrawProbe.Run(exemplar.Draw, mustDraw: true, requirePicture: true)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void TheExemplarsOwnVerbActuallyWorks()
    {
        // The exemplar teaches the pattern, so every unit generated from an order-flow brief copies
        // it. A verb that compiles and does nothing would be copied just as faithfully.
        var exemplar = new BookPressureVisualizer();
        SyntheticDrive.Run(exemplar);

        var before = new RecordingRenderSurface();
        exemplar.Draw(before);
        before.Texts.Should().NotContain(
            t => t.Text.Contains("Waiting for depth", StringComparison.Ordinal),
            "the drive supplies depth, so there is something to forget");

        exemplar.OnActionAsync(
                BookPressureVisualizer.ResetFlowAction, context: null!, CancellationToken.None)
            .GetAwaiter().GetResult();

        var after = new RecordingRenderSurface();
        exemplar.Draw(after);
        after.Texts.Should().Contain(
            t => t.Text.Contains("Waiting for depth", StringComparison.Ordinal),
            "resetting the flow forgets the history, so the picture is back to its empty state");
    }

    [Fact]
    public void TheExemplarIgnoresAnIdItNeverDeclared()
    {
        // What it teaches about unknown ids has to be true of it too.
        var exemplar = new BookPressureVisualizer();
        SyntheticDrive.Run(exemplar);

        exemplar.OnActionAsync("never-declared", context: null!, CancellationToken.None)
            .GetAwaiter().GetResult();

        var surface = new RecordingRenderSurface();
        exemplar.Draw(surface);
        surface.Texts.Should().NotContain(
            t => t.Text.Contains("Waiting for depth", StringComparison.Ordinal));
    }

    [Fact]
    public void TheExemplarDrawsItsRealPictureRatherThanItsWaitingMessage()
    {
        // The sharper version of the rung-7 point. "Waiting for depth…" is a picture, so rung 7 passed
        // on a unit that had received no depth at all — the same frame a completely broken one draws.
        var exemplar = new BookPressureVisualizer();
        SyntheticDrive.Run(exemplar);

        var surface = new RecordingRenderSurface();
        exemplar.Draw(surface);

        surface.Texts.Should().NotContain(
            text => text.Text.Contains("Waiting for depth", StringComparison.Ordinal),
            "the drive supplies depth, so the exemplar should be past its empty state");
    }

    [Fact]
    public void TheBenchmarkUnitClearsTheLadderToo()
    {
        // The goal loop's control: a hand-written answer to the same brief as the OrderBook window,
        // written with full knowledge of the SDK. If this cannot clear the ladder, no generated unit
        // can, and the benchmark would be measuring the verifier rather than Hyperion.
        var unit = new LiquidityBookVisualizer();
        var drive = SyntheticDrive.Run(unit);

        LifecycleProbe.Run(() => SyntheticDrive.Run(new LiquidityBookVisualizer()), phase: "the benchmark")
            .Outcome.Should().Be(VerificationOutcome.Passed);

        SchemaCoherenceProbe.Run(
                unit.Schema, drive.Parameters.KeysRead, drivenToCompletion: drive.Completed)
            .Outcome.Should().Be(VerificationOutcome.Passed);

        DrawProbe.Run(unit.Draw, mustDraw: true, requirePicture: true)
            .Outcome.Should().Be(VerificationOutcome.Passed);
    }

    [Fact]
    public void ABarOnlyUnitIsNotHandedDepthItNeverAskedFor()
    {
        // The drive is shaped by DataRequirement, so a bar strategy costs nothing for a feed it does
        // not read — and, more importantly, is not silently given a stream it never declared.
        var unit = new CountingVisualizer { Requirement = StrategyDataRequirement.Bars };

        SyntheticDrive.Run(unit);

        unit.Depths.Should().Be(0);
        unit.Trades.Should().Be(0);
        unit.Bars.Should().BeGreaterThan(0);
    }

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Counts what it was actually handed.</summary>
    private sealed class CountingVisualizer : IVisualizer
    {
        public int Bars { get; private set; }
        public int Depths { get; private set; }
        public int Trades { get; private set; }

        public StrategyDataRequirement Requirement { get; init; } =
            StrategyDataRequirement.L1 | StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;

        public StrategyParameterSchema Schema { get; } = new();

        public StrategyDataRequirement DataRequirement => Requirement;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public Task OnBarAsync(OhlcvBar bar, IVisualizerContext context, CancellationToken ct)
        {
            Bars++;
            return Task.CompletedTask;
        }

        public Task OnDepthAsync(
            InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct)
        {
            Depths++;
            return Task.CompletedTask;
        }

        public Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct)
        {
            Trades++;
            return Task.CompletedTask;
        }

        public void Draw(IRenderSurface surface) => surface.Text(4d, 12d, $"{Depths} depths");
    }

    /// <summary>Broken in the one place a bar-only drive could never look.</summary>
    private sealed class ThrowsOnDepth : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = new();

        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 | StrategyDataRequirement.Depth;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public Task OnDepthAsync(
            InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct) =>
            throw new InvalidOperationException("the book handler is wrong");

        public void Draw(IRenderSurface surface) => surface.Text(4d, 12d, "waiting");
    }

    /// <summary>Correct, and unverifiable before the drive carried depth and prints.</summary>
    private sealed class ReadsParametersWhereItsDataArrives : IVisualizer
    {
        public const string LevelsParameter = "levels";
        public const string MinimumSizeParameter = "minimumSize";

        public StrategyParameterSchema Schema { get; } = new(
            StrategyParameter.Int(LevelsParameter, "Levels", 5, min: 1, max: 25),
            StrategyParameter.Number(MinimumSizeParameter, "Minimum size", 10d, min: 1d, max: 1000d));

        public StrategyDataRequirement DataRequirement =>
            StrategyDataRequirement.L1 | StrategyDataRequirement.Depth | StrategyDataRequirement.TradeTape;

        private int _levels;
        private double _minimum;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public Task OnDepthAsync(
            InstrumentId instrument, DepthSnapshot depth, IVisualizerContext context, CancellationToken ct)
        {
            _levels = context.Parameters.GetInt(LevelsParameter);
            return Task.CompletedTask;
        }

        public Task OnTradeAsync(TradePrint trade, IVisualizerContext context, CancellationToken ct)
        {
            _minimum = context.Parameters.GetDouble(MinimumSizeParameter);
            return Task.CompletedTask;
        }

        public void Draw(IRenderSurface surface) => surface.Text(4d, 12d, $"{_levels} / {_minimum}");
    }
}
