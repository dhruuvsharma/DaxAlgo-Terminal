using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// A brief that asks for a picture must arrive with the widget catalogue attached.
///
/// <para><b>Six live builds produced 28 <c>SetStyle</c> calls, 15 <c>Text</c>, 12 <c>Rect</c> and 4
/// <c>Line</c> — and one widget call between them.</b> The order-book unit hand-rolled a depth ladder
/// out of rectangles while <c>Ladder</c> sat in the library; the footprint unit hand-rolled a footprint
/// while <c>Footprint</c> did. The drawing pack exists, opens with "reach for a widget before you draw
/// anything by hand", and lists both in a table.</para>
///
/// <para>So the first question is not "why did the model ignore it" but "was it there at all" — and
/// that is a pure function of the brief, answerable without spending a turn. The skill budget is real:
/// three packs, 24,000 characters, and the drawing pack alone is 13,582 of them, so it is the one most
/// likely to be squeezed out by a brief that also pulls order flow and instruments.</para>
/// </summary>
public sealed class EveryPictureBriefLoadsTheWidgetsTests(ITestOutputHelper output)
{
    [Fact]
    public void The_drawing_pack_reaches_every_brief_that_asks_for_a_picture()
    {
        var library = StrategySkillLibrary.Load();
        var missing = new List<string>();

        foreach (var brief in HyperionBriefs.All)
        {
            var selected = library.SelectFor(brief.Text);
            var names = selected.Select(s => s.Id).ToArray();

            output.WriteLine($"{brief.Id,-30} {string.Join(", ", names)}");

            if (!names.Contains("drawing", StringComparer.OrdinalIgnoreCase)) missing.Add(brief.Id);
        }

        Assert.True(
            missing.Count == 0,
            "Every one of these briefs names a picture — a grid, a ladder, a chart, a footprint. A brief "
            + "that asks for one and arrives without the widget catalogue gets a unit that draws "
            + "rectangles by hand. Missing for: " + string.Join(", ", missing));
    }
}
