using DaxAlgo.Sdk;
using DaxAlgo.Sdk.Drawing;
using DaxAlgo.Sdk.Layout;
using FluentAssertions;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The ladder was judging the picture nobody looks at.
///
/// <para>A unit that declares a <see cref="UnitLayout"/> is rendered by
/// <c>AuthoredUnitLayoutHost</c>, which builds one surface per panel and binds it to that panel's own
/// callback. <c>Draw</c> is never called — the SDK's own documentation says so: <i>"Draw is then
/// unused, because the panels do the drawing."</i></para>
///
/// <para>Rung 7 called <c>Draw</c> and nothing else. So for every unit with a layout — which is every
/// unit worth benchmarking, both exemplars and the control included — the ladder judged a fallback the
/// host does not use and never touched the three callbacks it does. Two consequences, and the second
/// one costs money: a correct unit that omits the fallback FAILS, and <c>AuthoringJudge</c> turns a
/// failed rung into a repair turn, so the agent path spends a generation rewriting working code.</para>
/// </summary>
public sealed class LayoutPanelsAreVerifiedTests
{
    [Fact]
    public void A_unit_that_draws_only_through_its_layout_passes()
    {
        // The contract says Draw is unused once a layout is declared, so this unit is CORRECT. It is
        // also what a model produces the moment it stops writing a fallback nobody calls.
        var report = AuthoredUnitVerifier.Verify(Unit(typeof(LayoutOnlyVisualizer)));

        Step(report, VerificationRung.DrawProbe).Outcome.Should().Be(
            VerificationOutcome.Passed,
            because: string.Join(" | ", report.Findings.Select(f => f.ToString())));
    }

    [Fact]
    public void A_broken_layout_panel_is_caught_even_when_the_fallback_is_fine()
    {
        // The other direction, and the one that matters more: the fallback paints a perfectly good
        // picture and the panel the user actually sees throws. Judging Draw alone reports a pass.
        var report = AuthoredUnitVerifier.Verify(Unit(typeof(BrokenPanelVisualizer)));

        Step(report, VerificationRung.DrawProbe).Outcome.Should().Be(VerificationOutcome.Failed);
        report.Findings.Should().Contain(f => f.Code == "draw.threw");
    }

    [Fact]
    public void A_unit_with_no_layout_is_still_judged_on_Draw()
    {
        // Most units declare no layout at all, and for them Draw IS the picture. The change must not
        // quietly stop checking them.
        var report = AuthoredUnitVerifier.Verify(Unit(typeof(BlankSingleVisualizer)));

        Step(report, VerificationRung.DrawProbe).Outcome.Should().Be(VerificationOutcome.Failed);
        report.Findings.Should().Contain(f => f.Code == "draw.blank");
    }

    [Fact]
    public void A_panel_that_divides_by_the_viewport_is_caught()
    {
        // DrawProbe.RunDegenerate was written for this, tested, and then called by NO verifier — only by
        // its own unit tests. A collapsed panel, a window restored from minimised, a layout pass before
        // measurement: all report zero size, and the crash lands on the render thread of a running
        // application rather than here.
        //
        // Measured before wiring it in: it changes no verdict on either exemplar, the control, or the
        // two units a live model produced. It costs nothing to be right about.
        var report = AuthoredUnitVerifier.Verify(Unit(typeof(ViewportDividingVisualizer)));

        Step(report, VerificationRung.DrawProbe).Outcome.Should().Be(VerificationOutcome.Failed);
        report.Findings.Should().Contain(f => f.Code.StartsWith("draw.degenerate", StringComparison.Ordinal));
    }

    [Fact]
    public void A_panel_callback_that_opens_its_own_panel_is_caught()
    {
        // Found by looking at a screenshot rather than at a report: ImbalanceHeatFront came back with
        // "Order book heat front" printed twice, once by the host and once by the unit.
        //
        // Every rung passed on it, which is the point — the primitives ARE inside a panel, finite and
        // theme-coloured. requirePanel only knew what a MISSING panel looked like.
        //
        // Not an exemplar defect: all three layout-declaring exemplars split each panel in two, so that
        // `Draw` opens the scope and delegates to DrawChart(surface, area) while the layout binds the
        // DrawChart(surface) overload. A model that collapses the two overloads moves the scope into the
        // callback, and no wording of an exemplar prevents that. Ownership of the region is a fact, so
        // it is checked rather than taught.
        var report = AuthoredUnitVerifier.Verify(Unit(typeof(DoubledPanelVisualizer)));

        Step(report, VerificationRung.DrawProbe).Outcome.Should().Be(VerificationOutcome.Failed);
        report.Findings.Should().Contain(f => f.Code == "draw.double-panel");
    }

    [Fact]
    public void A_unit_with_no_layout_may_still_open_its_own_panel()
    {
        // The other direction, and the reason this is not simply "never open a panel". With no layout
        // there is no host-drawn header, so Draw owes the frame its own — which is what draw.no-panel
        // demands. The new finding must not turn round and punish that.
        var report = AuthoredUnitVerifier.Verify(Unit(typeof(SinglePanelVisualizer)));

        Step(report, VerificationRung.DrawProbe).Outcome.Should().Be(
            VerificationOutcome.Passed,
            because: string.Join(" | ", report.Findings.Select(f => f.ToString())));
    }

    private static VerificationStep Step(VerificationReport report, VerificationRung rung) =>
        report.Steps.Single(s => s.Rung == rung);

    private static AuthoredUnit Unit(Type type) => new(AuthoringKind.Visualizer, type);

    // ── fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Declares a two-panel layout and leaves <c>Draw</c> at its default, exactly as the
    /// contract permits.</summary>
    private sealed class LayoutOnlyVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public UnitLayout Layout => UnitLayout.Rows(
            UnitLayout.Panel("Price", DrawPrice).Star(3),
            UnitLayout.Panel("Volume", DrawVolume).Pixels(60));

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        private static void DrawPrice(IRenderSurface surface) => Line(surface);

        private static void DrawVolume(IRenderSurface surface) => Line(surface);
    }

    /// <summary>A good fallback over a panel that throws — invisible to a probe that only runs the
    /// fallback.</summary>
    private sealed class BrokenPanelVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public UnitLayout Layout => UnitLayout.Rows(
            UnitLayout.Panel("Fine", DrawFine).Star(1),
            UnitLayout.Panel("Broken", DrawBroken).Star(1));

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface)
        {
            using var panel = surface.Panel("All good", RenderPanelKind.Chart);
            Line(surface);
        }

        private static void DrawFine(IRenderSurface surface) => Line(surface);

        private static void DrawBroken(IRenderSurface surface) =>
            throw new InvalidOperationException("the panel the user looks at is broken");
    }

    /// <summary>Draws a good picture at a real size and scales by a width that can be zero. Fine on
    /// every frame the probe used to run, and a division by zero on the one it did not.</summary>
    private sealed class ViewportDividingVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public UnitLayout Layout => UnitLayout.Rows(UnitLayout.Panel("Scaled", DrawScaled).Star(1));

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        private static void DrawScaled(IRenderSurface surface)
        {
            surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));

            // The shape a first draft actually has: step across the panel in N slices. At zero width
            // every coordinate is NaN, and the host paints a frame of nothing at all.
            var step = surface.Viewport.Width / surface.Viewport.Width;
            for (var i = 0; i < 8; i++)
                surface.Line(i * step, 10d, i * step + step, 40d);
        }
    }

    /// <summary>Opens a panel inside a layout callback, so the host's header and the unit's are both
    /// drawn and every title comes out twice.</summary>
    private sealed class DoubledPanelVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public UnitLayout Layout => UnitLayout.Rows(
            UnitLayout.Panel("Price", DrawPrice).Star(3),
            UnitLayout.Panel("Volume", DrawVolume).Pixels(60));

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        private static void DrawPrice(IRenderSurface surface)
        {
            using var panel = surface.Panel("Price", RenderPanelKind.Chart);
            Line(surface);
        }

        private static void DrawVolume(IRenderSurface surface) => Line(surface);
    }

    /// <summary>No layout, and a panel opened in <c>Draw</c> — the correct shape for a single-panel
    /// unit, and the one the exemplars show.</summary>
    private sealed class SinglePanelVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;

        public void Draw(IRenderSurface surface)
        {
            using var panel = surface.Panel("Book", RenderPanelKind.Chart);
            Line(surface);
        }
    }

    /// <summary>No layout, blank Draw — the case rung 7 has always caught.</summary>
    private sealed class BlankSingleVisualizer : IVisualizer
    {
        public StrategyParameterSchema Schema { get; } = StrategyParameterSchema.Empty;

        public StrategyDataRequirement DataRequirement => StrategyDataRequirement.L1;

        public Task OnStartAsync(IVisualizerContext context, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>A picture with enough in it to clear the probe's meaningfulness floor, drawn from a
    /// theme role so it does not trip the literal-colour finding.</summary>
    private static void Line(IRenderSurface surface)
    {
        surface.SetStyle(new RenderStyle(surface.Theme(RenderThemeColor.Accent)));
        for (var i = 0; i < 8; i++)
            surface.Line(i * 10d, 10d, i * 10d + 8d, 40d);
    }
}
