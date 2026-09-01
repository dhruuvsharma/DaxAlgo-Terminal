using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Layout;

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
    /// Below this, a unit has not drawn a picture — it has drawn a token. Applies only to frames that
    /// are more than text: an explanatory frame is judged by <c>requirePicture</c> instead.
    ///
    /// <para>The reward-hacking case is real and specific: a single <c>Line</c> call satisfies "it draws
    /// something" while conveying nothing. Any genuine chart emits a series, an axis, or a shape per data
    /// point, so it clears this immediately; the only things that fail are units that were never going to
    /// show anything.</para>
    /// </summary>
    public const int MinimumMeaningfulPrimitives = 3;

    /// <summary>
    /// The instant the probe draws its frame at, matching the clock <c>SyntheticDrive</c> runs the unit
    /// on, so a unit that stamped an event while being driven can compute its age here.
    ///
    /// <para>Thirty seconds AFTER the drive's clock, not <see cref="DateTime.MinValue"/>, and the
    /// difference is not cosmetic. A unit computes <c>Now - stampedAt</c>; at the origin that is
    /// negative by two thousand years, so a fade would come out at an enormous alpha and a position
    /// derived from it would be non-finite — the probe would report a unit broken by the probe. Thirty
    /// seconds is also long enough that a transient effect has finished, which is right: what must not
    /// be blank is the steady picture, not a flash.</para>
    /// </summary>
    public static DateTime ProbeInstant { get; } = new(2026, 1, 1, 0, 0, 30, DateTimeKind.Utc);

    /// <summary>Runs the probe against a unit that has already been started and fed data.</summary>
    /// <param name="draw">The unit's draw call.</param>
    /// <param name="mustDraw">True for a visualizer, whose whole job is the picture. False for a strategy,
    /// where drawing nothing is a legitimate choice — plenty are pure signal logic.</param>
    /// <param name="requirePicture">
    /// True once the unit has been fed data. Before that, a frame consisting only of text is the
    /// <b>correct</b> output — it is what the guidance asks for, because a blank panel is
    /// indistinguishable from a broken host — so an explanatory frame must not be judged as a failed
    /// picture. After data has arrived, text alone means the unit is still explaining itself when it
    /// should be drawing.
    /// </param>
    /// <param name="requirePanel">
    /// Whether the callback owes the frame a panel scope.
    ///
    /// <para>True for <c>Draw</c>, which is handed the whole body: without a panel there is no clip and
    /// no title, and both exemplars open one. <b>False for a panel callback in a
    /// <see cref="UnitLayout"/></b> — <c>AuthoredUnitLayoutHost</c> has already given that callback its
    /// own surface and drawn its header, so a scope opened there would title the region twice. Neither
    /// exemplar opens one in its panel callbacks, which is the shape a generated unit copies, so
    /// demanding it would fail every correct unit that declares a layout.</para>
    /// </param>
    public static VerificationStep Run(
        Action<IRenderSurface> draw, bool mustDraw, bool requirePicture = false, bool requirePanel = true)
    {
        ArgumentNullException.ThrowIfNull(draw);

        var findings = new List<VerificationFinding>();

        var surface = new RecordingRenderSurface(now: ProbeInstant);
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

        if (requirePanel && surface.Panels.Count == 0)
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

        // A frame that is nothing but text is the unit explaining itself — "waiting for 20 bars". That
        // is correct output before data arrives and wrong output after, so it is judged on which of
        // those the caller says this is, never on primitive count.
        var explanatoryFrame = surface.Texts.Count > 0 && surface.PrimitiveCount == surface.Texts.Count;

        if (explanatoryFrame && requirePicture)
        {
            findings.Add(new VerificationFinding(
                "draw.text-only",
                "After data arrived the unit still drew only text.",
                "Draw the values it computed. Text alone is for the warm-up, when there is genuinely "
                + "nothing to show yet."));
        }
        else if (!explanatoryFrame && surface.PrimitiveCount < MinimumMeaningfulPrimitives)
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
    /// Rung 7 against the picture the HOST actually renders.
    ///
    /// <para>A unit that declares a <see cref="UnitLayout"/> is drawn by
    /// <c>AuthoredUnitLayoutHost</c>, which builds one surface per panel and binds it to that panel's
    /// own callback. <c>Draw</c> is not called at all — the contract says so in as many words: <i>"Draw
    /// is then unused, because the panels do the drawing."</i></para>
    ///
    /// <para><b>The probe judged <c>Draw</c> regardless, and that was wrong in both directions.</b> A
    /// unit that declared a layout and left <c>Draw</c> at its default — which the contract explicitly
    /// permits — failed with <c>draw.blank</c> while rendering perfectly; and a unit whose visible panel
    /// threw, emitted NaN or blew the frame budget passed, because the only thing examined was a
    /// fallback nobody calls. The second direction is the expensive one: <c>AuthoringJudge</c> turns a
    /// rung failure into a repair turn, so a false failure spends a generation rewriting working code,
    /// and a false pass ships a broken window.</para>
    ///
    /// <para>Every panel is judged on its own and the findings are named by panel, because "something
    /// in this window draws nothing" is not actionable and "the Book panel draws nothing" is.</para>
    /// </summary>
    /// <param name="layout">The unit's declared layout. Null or <see cref="UnitLayout.IsSingle"/> falls
    /// through to <paramref name="draw"/>, which is the path almost every unit takes.</param>
    /// <param name="draw">The unit's <c>Draw</c>, used when there is no layout to walk.</param>
    public static VerificationStep RunLayout(
        UnitLayout? layout, Action<IRenderSurface> draw, bool mustDraw, bool requirePicture = false)
    {
        ArgumentNullException.ThrowIfNull(draw);

        if (layout is null || layout.IsSingle || layout.Root is null)
            return Merge(Run(draw, mustDraw, requirePicture), RunDegenerate(draw));

        var panels = new List<PanelNode>();
        Collect(layout.Root, panels);

        // A layout with no panel in it cannot happen through the SDK's own factories, but a malformed
        // tree falls back to a single panel elsewhere rather than throwing, so this does the same.
        if (panels.Count == 0) return Merge(Run(draw, mustDraw, requirePicture), RunDegenerate(draw));

        var findings = new List<VerificationFinding>();
        var anyPassed = false;

        foreach (var panel in panels)
        {
            // requirePanel: false — the host already owns this panel's region and header.
            var step = Run(panel.Draw, mustDraw, requirePicture, requirePanel: false);
            if (step.Outcome == VerificationOutcome.Passed) anyPassed = true;

            var name = string.IsNullOrWhiteSpace(panel.Title) ? "an untitled panel" : $"'{panel.Title}'";
            foreach (var finding in step.Findings)
                findings.Add(finding with { Message = $"Panel {name}: {finding.Message}" });

            foreach (var finding in RunDegenerate(panel.Draw).Findings)
                findings.Add(finding with { Message = $"Panel {name}: {finding.Message}" });
        }

        if (findings.Count > 0)
            return new VerificationStep(VerificationRung.DrawProbe, VerificationOutcome.Failed, findings);

        // Every panel skipped: a strategy that declares panels and paints nothing in any of them. Not a
        // failure — a strategy is allowed to be pure signal logic — but nothing was checked either.
        return anyPassed ? VerificationStep.Pass(VerificationRung.DrawProbe) : VerificationStep.Skip(VerificationRung.DrawProbe);
    }

    /// <summary>
    /// One rung, two passes: the real frame and the zero-sized one.
    ///
    /// <para>They are the same rung because they answer the same question — is the picture sound — and
    /// a report with two <c>DrawProbe</c> steps in it would make every consumer decide which one it
    /// meant. The degenerate pass carries no verdict of its own, only findings.</para>
    /// </summary>
    private static VerificationStep Merge(VerificationStep frame, VerificationStep degenerate)
    {
        if (degenerate.Findings.Count == 0) return frame;

        return new VerificationStep(
            VerificationRung.DrawProbe,
            VerificationOutcome.Failed,
            [.. frame.Findings, .. degenerate.Findings]);
    }

    private static void Collect(LayoutNode node, List<PanelNode> into)
    {
        switch (node)
        {
            case PanelNode panel:
                into.Add(panel);
                break;
            case SplitNode split:
                foreach (var child in split.Children) Collect(child, into);
                break;
        }
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
