using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// Six briefs, written the way a user types them, kept where a run can name one instead of somebody
/// retyping a sentence.
///
/// <para><b>Three strategies and three visualizers</b>, chosen so the six between them touch every
/// stream the sandbox serves — bars, the depth book, and the trade tape — rather than six variations
/// on the one that is easiest to satisfy. The strategies are named techniques with published rules, so
/// what comes back can be judged against something other than taste: either it warms up before it acts
/// and computes the thing the technique is, or it does not.</para>
///
/// <para>Each brief says what the window <b>is</b> and almost nothing about how to build it. That is
/// the claim under test — that a short brief is enough — and padding them with implementation notes
/// would measure the notes instead.</para>
/// </summary>
public static class HyperionBriefs
{
    /// <param name="Id">Slug: the session id, the output folder, and what a run is asked for by name.</param>
    /// <param name="DisplayName">What the catalog card will read.</param>
    /// <param name="Kind">Strategy or visualizer — the contract the unit must implement.</param>
    /// <param name="Brief">What gets typed into the composer, verbatim.</param>
    /// <param name="Streams">What it needs to work, for the report. Not sent to the model: the unit
    /// declares its own DataRequirement, and telling it the answer would hide a real failure.</param>
    public sealed record Brief(
        string Id, string DisplayName, AuthoringKind Kind, string Text, string Streams);

    // ── strategies ──────────────────────────────────────────────────────────────────────────────

    /// <summary>Opening-range breakout — the chart strategy. Bars, sessions, and a level that must be
    /// fixed before it can be broken, which is where look-ahead usually creeps in.</summary>
    public static readonly Brief OpeningRangeBreakout = new(
        "hyperion.orb",
        "Opening Range Breakout",
        AuthoringKind.Strategy,
        "An opening range breakout strategy: mark the high and low of the first 30 minutes of the "
        + "session, go long when price closes above the range and short when it closes below, stop at "
        + "the opposite side of the range, and draw the range and the day's bars.",
        "Bars");

    /// <summary>Order-book imbalance — the depth strategy. The classic microstructure signal, and the
    /// one that cannot be faked from bars.</summary>
    public static readonly Brief BookImbalance = new(
        "hyperion.book-imbalance",
        "Order Book Imbalance",
        AuthoringKind.Strategy,
        "An order-book imbalance strategy: measure the resting size on the bid against the ask across "
        + "the top levels of the book, smooth it, and take a position when the imbalance holds on one "
        + "side, showing the imbalance and the position on a chart.",
        "Depth + L1");

    /// <summary>Market-profile value-area reversion — the volume-profile strategy. Needs the tape
    /// bucketed by price, a point of control and a 70% value area, then a rule on top of them.</summary>
    public static readonly Brief ValueAreaReversion = new(
        "hyperion.value-area",
        "Value Area Reversion",
        AuthoringKind.Strategy,
        "A market profile strategy: build the session's volume profile from the trade tape, find the "
        + "point of control and the 70% value area, and fade moves that leave the value area and fail "
        + "to hold outside it, drawing the profile beside the price.",
        "TradeTape + L1");

    // ── visualizers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The correlation matrix. The one that has to reach across instruments — the sandbox
    /// serves a whole set, and a unit that only ever looks at one has missed the brief.</summary>
    public static readonly Brief CorrelationMatrix = new(
        "hyperion.correlation-matrix",
        "Correlation Matrix",
        AuthoringKind.Visualizer,
        "A correlation matrix of the instruments in the window: rolling return correlation between "
        + "every pair, drawn as a colour-coded grid from blue for negative through to red for "
        + "positive, with the instrument names on both axes and the value in each cell.",
        "Bars, multi-instrument");

    /// <summary>The volume footprint. The hardest picture of the three: a nested layout, two numbers
    /// per price per bar, and colour that has to mean something.</summary>
    public static readonly Brief VolumeFootprint = new(
        "hyperion.volume-footprint",
        "Volume Footprint",
        AuthoringKind.Visualizer,
        "A volume footprint chart: inside each time bar, the volume traded at the bid and at the ask "
        + "clustered by price level, with the delta per price, the point of control marked, and the "
        + "bar's own high and low outlined.",
        "TradeTape + L1");

    /// <summary>The depth ladder. The picture everybody already knows, which is what makes it a fair
    /// test — a wrong one is obvious on sight.</summary>
    public static readonly Brief OrderBookLadder = new(
        "hyperion.order-book",
        "Order Book Ladder",
        AuthoringKind.Visualizer,
        "An order book chart: the depth ladder with bid and ask size as horizontal bars either side of "
        + "the price, the spread marked in the middle, and the cumulative depth on each side drawn "
        + "behind it.",
        "Depth + L1");

    /// <summary>All six, in the order a run takes them: the strategies first, because a strategy that
    /// fails the ladder fails it on rungs a visualizer never reaches.</summary>
    public static readonly IReadOnlyList<Brief> All =
    [
        OpeningRangeBreakout,
        BookImbalance,
        ValueAreaReversion,
        CorrelationMatrix,
        VolumeFootprint,
        OrderBookLadder,
    ];

    /// <summary>The brief with this id, or null. Ids are matched loosely — <c>orb</c> finds
    /// <c>hyperion.orb</c> — because these are typed at a command line.</summary>
    public static Brief? Find(string id) =>
        All.FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? All.FirstOrDefault(b => b.Id.EndsWith("." + id, StringComparison.OrdinalIgnoreCase));
}
