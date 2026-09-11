using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Which widget paints a panel is a contract decision, not a builder's whim.
///
/// <para><b>The catalogue alone did not work, and it was measured rather than guessed.</b> Six live
/// builds all loaded the drawing pack — verified as a pure function of each brief, not assumed — and
/// its opening line is "reach for a widget before you draw anything by hand". Between them they
/// produced 28 <c>SetStyle</c> calls, 15 <c>Text</c>, 12 <c>Rect</c>, 4 <c>Line</c>, and ONE widget
/// call. A depth ladder was built from rectangles beside <c>Ladder</c>; a footprint beside
/// <c>Footprint</c>; a correlation grid beside <c>Heatmap</c>.</para>
///
/// <para>The one unit that did call <c>PriceChart.Draw</c> came back with candles, a price gutter, a
/// time axis, a crosshair, the opening range shaded across the bars and the entries marked on them —
/// the whole trading chart, with the strategy's own work on top. That is the difference this makes, and
/// it was luck.</para>
///
/// <para>So the choice moves to where every other cross-file agreement already lives: fixed in the
/// contract before the fan-out, and restated in the role instruction of the builder that has to act on
/// it. Guidance a builder is free to skip is guidance it skips.</para>
/// </summary>
public sealed class APanelIsDrawnWithAWidgetTests
{
    private static UnitContract Contract(params PanelSpec[] panels) =>
        UnitContract.Minimal("Unit", AuthoringKind.Visualizer) with { Panels = panels };

    private static BuildTask Panel(string file) =>
        new("t1", "The picture", TaskKind.Panel, file, "paint it", []);

    [Fact]
    public void The_planner_is_asked_for_one_and_shown_what_they_look_like()
    {
        var planner = SwarmPrompts.Planner(AuthoringKind.Visualizer, maxTasks: 8);

        planner.Should().Contain("\"widget\"", "the shape it must return has the field in it");
        planner.Should().Contain("PriceChart.Draw");
        planner.Should().Contain("Ladder.Draw");
        planner.Should().Contain("Heatmap.Draw");
    }

    [Fact]
    public void The_contract_every_builder_reads_names_it()
    {
        // The contract is the one thing builders working at the same time agree on, and it is rendered
        // into every one of their prompts. A decision recorded anywhere else is a decision half of them
        // never see.
        var rendered = SwarmPrompts.Contract(Contract(
            new PanelSpec("ladder", "Depth ladder", "resting size per price", "LadderPanel", "Ladder.Draw")));

        rendered.Should().Contain("DRAW IT WITH Ladder.Draw");
    }

    [Fact]
    public void The_builder_that_paints_it_is_told_which_call_to_make()
    {
        var instruction = SwarmPrompts.Builder(
            Panel("LadderPanel.cs"),
            Contract(new PanelSpec(
                "ladder", "Depth ladder", "resting size per price", "LadderPanel", "Ladder.Draw")));

        instruction.Should().Contain("DRAW WITH THE WIDGET THE CONTRACT NAMES");
        instruction.Should().Contain("Ladder.Draw");
        instruction.Should().Contain("add your own marks ON TOP",
            "a widget is the floor of the picture, not the ceiling — the strategy's own work goes over it");
    }

    [Fact]
    public void A_chart_is_told_it_means_a_trading_chart()
    {
        // The specific thing a user asked for: "make the charts look and behave and interact like a
        // trading chart". PriceChart.Draw already is one — candles, gutter, time axis, volume, last
        // price, legend, crosshair, and the host's wheel and drag wired through. Saying so at the point
        // of writing is what stops a model assembling a mock-up of a chart out of rectangles.
        var instruction = SwarmPrompts.Builder(
            Panel("SessionChart.cs"),
            Contract(new PanelSpec("price", "Price", "candles", "SessionChart", "PriceChart.Draw")));

        instruction.Should().Contain("A chart means a TRADING chart");
        instruction.Should().Contain("PriceChart.Draw(surface, bars)");
        instruction.Should().Contain("crosshair");
    }

    [Fact]
    public void A_task_that_paints_nothing_is_not_lectured_about_pictures()
    {
        // Maths and schema builders get a prompt about the file they are actually writing. Padding every
        // role with drawing advice is how a prompt stops being read.
        var maths = SwarmPrompts.Builder(
            new BuildTask("t2", "The maths", TaskKind.Maths, "Imbalance.cs", "compute it", []),
            Contract(new PanelSpec("p", "P", "s", "Panel", "Ladder.Draw")));

        maths.Should().NotContain("A chart means a TRADING chart");
    }

    [Fact]
    public void A_picture_with_no_widget_says_so_rather_than_naming_a_wrong_one()
    {
        // The pack has a section for this: some pictures genuinely have no widget, and forcing one would
        // be worse than the hand-rolling this is meant to stop.
        var rendered = SwarmPrompts.Contract(Contract(
            new PanelSpec("scene", "Battlefield", "orders as soldiers", "ScenePanel")));

        rendered.Should().Contain("no widget fits");

        var instruction = SwarmPrompts.Builder(Panel("ScenePanel.cs"), Contract(
            new PanelSpec("scene", "Battlefield", "orders as soldiers", "ScenePanel")));

        instruction.Should().Contain("Reach for a widget before drawing by hand");
    }
}
