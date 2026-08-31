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
