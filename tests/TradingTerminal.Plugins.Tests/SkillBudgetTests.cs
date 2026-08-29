using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The skill packs Hyperion loads into its system prompt, and the ceiling on them.
///
/// <para><b>Why this is worth a test file of its own.</b> The budget is enforced by skipping whole
/// packs, and skipping is silent. It has already gone wrong once: a ceiling of 12,000 meant a brief
/// explicitly asking for a picture got no drawing guidance at all, and the model hand-rolled widgets
/// that already existed. Nothing failed, nothing logged — the output was just worse.</para>
///
/// <para>So the invariant is asserted rather than reasoned about in a comment, and it is asserted
/// against the packs as they actually are, so that adding or growing one trips this rather than
/// quietly eating another pack's place.</para>
/// </summary>
public sealed class SkillBudgetTests
{
    private static StrategySkillLibrary Library() => StrategySkillLibrary.Load();

    [Fact]
    public void The_three_heaviest_packs_fit_together()
    {
        // A real brief, not a contrived one: order flow, drawn as a picture, with maths behind it.
        // If the heaviest three stop fitting, the ceiling has become one that "nearly fits", which the
        // library's own remarks call out as worse than one that comfortably does.
        var heaviest = Library().All
            .OrderByDescending(skill => skill.Body.Length)
            .Take(3)
            .Sum(skill => skill.Body.Length);

        heaviest.Should().BeLessThanOrEqualTo(
            StrategySkillLibrary.MaxCharacters,
            "a brief that wants all three must not silently lose one of them");
    }

    [Fact]
    public void No_single_pack_can_ever_be_too_big_to_load()
    {
        // A pack larger than the whole budget can never be selected, at any effort level, for any
        // brief. It would sit in the repository looking maintained and reach the model never.
        foreach (var skill in Library().All)
        {
            skill.Body.Length.Should().BeLessThanOrEqualTo(
                StrategySkillLibrary.MaxCharacters,
                $"'{skill.Id}' would be unloadable");
        }
    }

    [Fact]
    public void The_ceiling_still_binds()
    {
        // If every pack fits at once the budget has stopped doing anything, and the next pack to be
        // added would be the one that silently breaks it.
        Library().All.Sum(skill => skill.Body.Length)
            .Should().BeGreaterThan(StrategySkillLibrary.MaxCharacters);
    }

    // ── the layout pack, added for issue #42 ────────────────────────────────────────────────────

    [Theory]
    [InlineData("show two charts side by side")]
    [InlineData("a dashboard with the order book and the tape")]
    [InlineData("arbitrage between two venues with the spread in between")]
    [InlineData("split the window into panels")]
    public void A_multi_panel_brief_loads_the_layout_pack(string brief)
    {
        var chosen = Library().SelectFor(brief, StrategySkillLibrary.MaxSkillsPerSession);

        chosen.Should().Contain(
            skill => skill.Id == "layout",
            "the model cannot compose a layout it was never told exists");
    }

    [Fact]
    public void A_single_panel_brief_does_not_pay_for_the_layout_pack()
    {
        // One chart is the default and needs no layout guidance. Loading it anyway would spend context
        // on a capability the brief does not use.
        var chosen = Library().SelectFor(
            "an exponential moving average crossover on one instrument",
            StrategySkillLibrary.MaxSkillsPerSession);

        chosen.Should().NotContain(skill => skill.Id == "layout");
    }

    // ── kind isolation ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_visualizer_session_is_not_taught_an_api_it_does_not_have()
    {
        // A visualizer has no book: it cannot take a position, set a target or place an order. The
        // risk pack is stops, sizing and flattening — loading it here spends context teaching an API
        // that is not there, and invites code that fails to compile and burns a fix generation.
        var brief = "flatten on a trailing stop with a max drawdown limit and position sizing";

        var forVisualizer = Library().SelectFor(brief, 5, AuthoringKind.Visualizer);
        var forStrategy = Library().SelectFor(brief, 5, AuthoringKind.Strategy);

        forVisualizer.Should().NotContain(skill => skill.Id == "risk-and-exits");
        forStrategy.Should().Contain(
            skill => skill.Id == "risk-and-exits",
            "the same brief is exactly what a strategy needs it for");
    }

    [Fact]
    public void Kind_agnostic_packs_reach_both()
    {
        // Drawing and layout read the same whichever kind you are writing, and tagging them would
        // halve their usefulness for no benefit.
        var brief = "draw a candlestick chart with the order book beside it";

        foreach (var kind in new[] { AuthoringKind.Strategy, AuthoringKind.Visualizer })
        {
            Library().SelectFor(brief, 5, kind)
                .Should().Contain(skill => skill.Id == "drawing", $"for {kind}");
        }
    }

    [Fact]
    public void Selection_without_a_kind_still_considers_everything()
    {
        // The un-narrowed overload is what non-session callers use; narrowing it by default would
        // silently change their results.
        Library().SelectFor("trailing stop and drawdown limits", 5)
            .Should().Contain(skill => skill.Id == "risk-and-exits");
    }

    [Fact]
    public void A_drawing_brief_still_gets_the_widget_catalogue_first()
    {
        // The catalogue is the one pack that reduces OUTPUT tokens, billed at several times the rate
        // of the cached input it occupies. Layout must not crowd it out of a drawing brief.
        var chosen = Library().SelectFor(
            "draw a candlestick chart with a volume histogram beneath it",
            StrategySkillLibrary.MaxSkillsPerSession);

        chosen.Should().Contain(skill => skill.Id == "drawing");
    }
}
