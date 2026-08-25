using DaxAlgo.Sdk;
using FluentAssertions;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Rung 7 of the verification ladder (#46).
///
/// <para>Half of these assert that a bad picture is <b>rejected</b>, and that half matters more. The
/// probe's whole job is to be hard to satisfy by accident: reward is computed from it, so anything it
/// waves through is something a model will learn to produce.</para>
/// </summary>
public sealed class DrawProbeTests
{
    /// <summary>A picture with the properties a real one has: panel, theme roles, several primitives.</summary>
    private static void GoodPicture(IRenderSurface surface)
    {
        using var panel = surface.Panel("Delta", RenderPanelKind.Chart);
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
        surface.AxisX(0d, 10d);
        using var series = surface.Series("Delta", RenderSeriesKind.Line);
        for (var i = 0; i < 10; i++) surface.Push(i, i * 1.5d);
    }

    [Fact]
    public void ARealPicturePasses()
    {
        DrawProbe.Run(GoodPicture, mustDraw: true).Outcome.Should().Be(VerificationOutcome.Passed);
    }

    // ── The failures it exists to catch ──────────────────────────────────────────────────────────

    [Fact]
    public void AVisualizerThatPaintsNothingFails()
    {
        // The failure that made the whole epic necessary, and the one no other rung can see.
        var step = DrawProbe.Run(_ => { }, mustDraw: true);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().ContainSingle().Which.Code.Should().Be("draw.blank");
    }

    [Fact]
    public void AStrategyThatPaintsNothingIsSkippedRatherThanPassed()
    {
        // Drawing is optional for a strategy — plenty are pure signal logic. But "not applicable" must
        // never be recorded as "passed", or a unit earns credit for a check that never ran.
        var step = DrawProbe.Run(_ => { }, mustDraw: false);

        step.Outcome.Should().Be(VerificationOutcome.NotApplicable);
        step.Outcome.Should().NotBe(VerificationOutcome.Passed);
    }

    [Fact]
    public void OneLonelyLineDoesNotCountAsAPicture()
    {
        // The reward-hacking case, stated plainly: a single stroke satisfies "it drew something" while
        // conveying nothing at all. If this passed, it is what a model would converge on.
        var step = DrawProbe.Run(
            surface =>
            {
                using var panel = surface.Panel("Chart", RenderPanelKind.Chart);
                surface.Theme(RenderThemeColor.Accent);
                surface.Line(0d, 0d, 10d, 10d);
            },
            mustDraw: true);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().Contain(f => f.Code == "draw.trivial");
    }

    [Fact]
    public void LiteralColoursAreRejected()
    {
        // A colour that reads well on a dark ground is invisible on a light one, and the unit cannot ask
        // which theme is active — deliberately. Resolving no theme role at all is the tell.
        var step = DrawProbe.Run(
            surface =>
            {
                using var panel = surface.Panel("Chart", RenderPanelKind.Chart);
                surface.SetStyle(new RenderStyle(new RenderColor(255, 0, 0)));
                using var series = surface.Series("s", RenderSeriesKind.Line);
                for (var i = 0; i < 5; i++) surface.Push(i, i);
            },
            mustDraw: true);

        step.Findings.Should().Contain(f => f.Code == "draw.literal-colours");
    }

    [Fact]
    public void NonFiniteCoordinatesAreCaught()
    {
        // Almost always an average over an empty window, or a division by a zero-width range. One of
        // these can take out a whole frame in a real renderer.
        var step = DrawProbe.Run(
            surface =>
            {
                using var panel = surface.Panel("Chart", RenderPanelKind.Chart);
                surface.Theme(RenderThemeColor.Accent);
                using var series = surface.Series("s", RenderSeriesKind.Line);
                surface.Push(0d, 1d);
                surface.Push(1d, 0d / 0d);
                surface.Push(2d, double.PositiveInfinity);
            },
            mustDraw: true);

        step.Findings.Should().Contain(f => f.Code == "draw.non-finite");
    }

    [Fact]
    public void DrawingOutsideAPanelIsCaught()
    {
        var step = DrawProbe.Run(
            surface =>
            {
                surface.Theme(RenderThemeColor.Accent);
                for (var i = 0; i < 5; i++) surface.Rect(i, 0d, 1d, 1d);
            },
            mustDraw: true);

        step.Findings.Should().Contain(f => f.Code == "draw.no-panel");
    }

    [Fact]
    public void AFrameOverTheBudgetIsCaught()
    {
        var step = DrawProbe.Run(
            surface =>
            {
                using var panel = surface.Panel("Chart", RenderPanelKind.Chart);
                surface.Theme(RenderThemeColor.Accent);
                using var series = surface.Series("s", RenderSeriesKind.Line);
                for (var i = 0; i <= DrawProbe.PrimitiveBudget; i++) surface.Push(i, i);
            },
            mustDraw: true);

        step.Findings.Should().Contain(f => f.Code == "draw.over-budget");
    }

    [Fact]
    public void AThrowingDrawFailsEvenWhenDrawingWasOptional()
    {
        // Draw runs on the render thread. Throwing there takes the UI with it, so a strategy gets no
        // latitude here even though it was free not to draw at all.
        var step = DrawProbe.Run(_ => throw new InvalidOperationException("empty history"), mustDraw: false);

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().ContainSingle().Which.Code.Should().Be("draw.threw");
    }

    // ── The degenerate viewport ──────────────────────────────────────────────────────────────────

    [Fact]
    public void ScalingByAZeroSizedViewportIsCaught()
    {
        // Real hosts produce this: a collapsed panel, a minimised window, a layout pass before
        // measurement. The crash would otherwise land on a live render thread rather than here.
        var step = DrawProbe.RunDegenerate(surface =>
        {
            using var panel = surface.Panel("Chart", RenderPanelKind.Chart);
            using var series = surface.Series("s", RenderSeriesKind.Line);
            for (var i = 0; i < 5; i++) surface.Push(i, i / surface.Viewport.Height);
        });

        step.Outcome.Should().Be(VerificationOutcome.Failed);
        step.Findings.Should().ContainSingle().Which.Code.Should().Be("draw.degenerate-non-finite");
    }

    [Fact]
    public void AUnitThatChecksTheViewportSurvivesIt()
    {
        DrawProbe.RunDegenerate(surface =>
        {
            if (surface.Viewport.Height <= 0d) return;
            using var panel = surface.Panel("Chart", RenderPanelKind.Chart);
            surface.Push(0d, 1d / surface.Viewport.Height);
        }).Outcome.Should().Be(VerificationOutcome.Passed);
    }

    // ── Diagnostics are for a repair agent, not a log ────────────────────────────────────────────

    [Fact]
    public void EveryFindingCarriesARemedy()
    {
        // A diagnostic that only names the symptom sends a model looking for the problem instead of
        // fixing it, which costs a round trip of the user's money every time.
        var probes = new (Action<IRenderSurface> Draw, bool MustDraw)[]
        {
            (_ => { }, true),
            (s => { using var p = s.Panel("c", RenderPanelKind.Chart); s.Theme(RenderThemeColor.Accent); s.Line(0, 0, 1, 1); }, true),
            (_ => throw new InvalidOperationException("x"), true),
        };

        foreach (var (draw, mustDraw) in probes)
        {
            foreach (var finding in DrawProbe.Run(draw, mustDraw).Findings)
            {
                finding.Remedy.Should().NotBeNullOrWhiteSpace($"'{finding.Code}' must say what to change");
                finding.Code.Should().StartWith("draw.").And.NotContain(" ");
            }
        }
    }
}
