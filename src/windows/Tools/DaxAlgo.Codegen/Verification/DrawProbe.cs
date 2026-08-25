using DaxAlgo.Sdk;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Rung 7 — drives <c>Draw</c> and inspects what came out.
///
/// <para>This is the rung that catches the failure this whole effort exists for: a unit that compiles,
/// runs, and paints nothing. It is invisible in every other check, and invisible to the user too — a
/// blank panel reads as a broken application rather than an empty visualizer, so it is not even reported
/// as a bug in the unit.</para>
///
/// <para>Every probe here is a property a real picture has and a fake one does not, which is what makes
/// it hard to satisfy by accident. Drawing is <b>never</b> rewarded merely for happening.</para>
/// </summary>
public static class DrawProbe
{
    /// <summary>The per-frame ceiling. The host bounds what one frame may emit and throttles rather than
    /// trusts; a unit at this level is already unreadable to a human, so this is a ceiling and not a
    /// target.</summary>
    public const int PrimitiveBudget = 20_000;

    /// <summary>
    /// Below this, a unit has not drawn a picture — it has drawn a token.
    ///
    /// <para>The reward-hacking case is real and specific: a single <c>Line</c> call satisfies "it draws
    /// something" while conveying nothing. Any genuine chart emits a series, an axis, or a shape per data
    /// point, so it clears this immediately; the only things that fail are units that were never going to
    /// show anything.</para>
    /// </summary>
    public const int MinimumMeaningfulPrimitives = 3;

    /// <summary>Runs the probe against a unit that has already been started and fed data.</summary>
    /// <param name="draw">The unit's draw call.</param>
    /// <param name="mustDraw">True for a visualizer, whose whole job is the picture. False for a strategy,
    /// where drawing nothing is a legitimate choice — plenty are pure signal logic.</param>
    public static VerificationStep Run(Action<IRenderSurface> draw, bool mustDraw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        var findings = new List<VerificationFinding>();

        var surface = new RecordingRenderSurface();
        try
        {
            draw(surface);
        }
        catch (Exception ex)
        {
            // A throwing Draw takes the render thread with it, so this is a hard failure regardless of
            // whether the unit was obliged to draw at all.
            return VerificationStep.Fail(
                VerificationRung.DrawProbe,
                new VerificationFinding(
                    "draw.threw",
                    $"Draw threw {ex.GetType().Name}: {ex.Message}",
                    "Draw runs on the render thread and must not throw. Guard against an empty history "
                    + "and against dividing by a span of zero."));
        }

        if (surface.IsBlank)
        {
            return mustDraw
                ? VerificationStep.Fail(
                    VerificationRung.DrawProbe,
                    new VerificationFinding(
                        "draw.blank",
                        "Draw emitted nothing. A visualizer that paints nothing is indistinguishable "
                        + "from a broken host.",
                        "Open a panel and draw. If there is not enough data yet, say so with Text "
                        + "rather than returning silently."))
                : VerificationStep.Skip(VerificationRung.DrawProbe);
        }

        if (surface.Panels.Count == 0)
        {
            findings.Add(new VerificationFinding(
                "draw.no-panel",
                "Primitives were emitted outside any panel.",
                "Open a panel first: using var panel = surface.Panel(\"Title\", RenderPanelKind.Chart);"));
        }

        if (surface.HasNonFiniteCoordinate)
        {
            findings.Add(new VerificationFinding(
                "draw.non-finite",
                "A coordinate was NaN or infinite.",
                "Usually an average over an empty window or a division by a zero-width range. Guard the "
                + "warm-up, and use PlotRange.Padded() which gives a flat range a usable width."));
        }

        if (surface.PrimitiveCount > PrimitiveBudget)
        {
            findings.Add(new VerificationFinding(
                "draw.over-budget",
                $"{surface.PrimitiveCount} primitives in one frame exceeds the {PrimitiveBudget} budget.",
                "Aggregate before drawing. A frame this dense is unreadable to a person as well as "
                + "expensive to paint."));
        }

        if (surface.PrimitiveCount < MinimumMeaningfulPrimitives)
        {
            findings.Add(new VerificationFinding(
                "draw.trivial",
                $"Only {surface.PrimitiveCount} primitive(s) were emitted — that is a token, not a picture.",
                "Draw the data the unit computed, not a placeholder."));
        }

        if (surface.ThemeTokens.Count == 0)
        {
            findings.Add(new VerificationFinding(
                "draw.literal-colours",
                "No theme role was resolved, so the unit is painting with literal colours.",
                "Take colours from surface.Theme(RenderThemeColor.…). A literal that reads well on one "
                + "background is invisible on the other."));
        }

        return findings.Count == 0
            ? VerificationStep.Pass(VerificationRung.DrawProbe)
            : new VerificationStep(VerificationRung.DrawProbe, VerificationOutcome.Failed, findings);
    }

    /// <summary>
    /// A second pass against a degenerate viewport — zero width and height.
    ///
    /// <para>Real hosts produce this: a panel collapsed to nothing, a window restored minimised, a layout
    /// pass before measurement. A unit that scales to the viewport divides by it, and the crash lands on
    /// the render thread of a running application rather than here.</para>
    /// </summary>
    public static VerificationStep RunDegenerate(Action<IRenderSurface> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);

        var surface = new RecordingRenderSurface(new RenderViewport(0d, 0d, 1d));
        try
        {
            draw(surface);
        }
        catch (Exception ex)
        {
            return VerificationStep.Fail(
                VerificationRung.DrawProbe,
                new VerificationFinding(
                    "draw.degenerate-viewport",
                    $"Draw threw {ex.GetType().Name} against a zero-sized viewport: {ex.Message}",
                    "A collapsed or unmeasured panel reports zero size. Return early when "
                    + "surface.Viewport has no area."));
        }

        return surface.HasNonFiniteCoordinate
            ? VerificationStep.Fail(
                VerificationRung.DrawProbe,
                new VerificationFinding(
                    "draw.degenerate-non-finite",
                    "A zero-sized viewport produced NaN or infinite coordinates.",
                    "Return early when the viewport has no area rather than scaling by it."))
            : VerificationStep.Pass(VerificationRung.DrawProbe);
    }
}
