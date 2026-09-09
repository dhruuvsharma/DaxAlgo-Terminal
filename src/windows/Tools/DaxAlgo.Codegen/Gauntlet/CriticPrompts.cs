using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

/// <summary>One critic's definition: who it is, and what it is told to look for.</summary>
/// <param name="Id">Stable, and the prefix of every finding code it emits.</param>
/// <param name="Panel">Picture or Quant.</param>
/// <param name="NeedsPicture">True when reading the drawing commands is not a substitute for looking.</param>
/// <param name="AppliesTo">Null for both kinds; otherwise the one kind this critic is about.</param>
/// <param name="Instruction">What it looks for, in its own words.</param>
public sealed record CriticDefinition(
    string Id,
    CriticPanel Panel,
    bool NeedsPicture,
    AuthoringKind? AppliesTo,
    string Instruction);

/// <summary>
/// What each critic is told.
///
/// <para><b>The output contract is identical for all of them</b>, and that is what lets their findings
/// flow into the repair path the ladder already feeds. A critic that invented its own reporting shape
/// would need its own router and its own fixer.</para>
///
/// <para>Every one of them is told the same three things about restraint: report what is WRONG rather
/// than what could be different, say where, and stay silent when there is nothing. A critic rewarded
/// for finding something always finds something, and a repair turn spent on a preference is a turn the
/// user paid for and did not want.</para>
/// </summary>
public static class CriticPrompts
{
    /// <summary>The shared contract. Findings are JSON so they parse; the rest is prose so they think.</summary>
    public const string OutputContract = """

        HOW TO ANSWER

        One ```json block, and nothing after it:

        {
          "verdict": "one line: what you concluded",
          "findings": [
            { "code": "short-kebab-slug", "problem": "what is wrong", "remedy": "what to change",
              "file": "Which.cs, or omit when it is not about one file" }
          ]
        }

        RULES:

        1. Report what is WRONG, not what could be different. A preference is not a finding, and a
           repair turn spent on one is a turn the user paid for and did not ask for.
        2. If nothing is wrong, return an EMPTY findings array. That is a real and common answer, and
           saying so plainly is worth more than inventing something.
        3. At most FIVE findings, ordered worst first. A list of fifteen produces a repair that changes
           fifteen things and improves none of them.
        4. `code` is a short stable slug — "no-price-axis", "lookahead-on-close". Never a sentence.
        5. `remedy` says what to change. A finding that only describes the symptom sends a repair
           looking for the problem instead of fixing it.
        """;

    /// <summary>The panel, in the order they run.</summary>
    public static IReadOnlyList<CriticDefinition> All { get; } =
    [
        new(Critics.Picture, CriticPanel.Picture, NeedsPicture: true, AppliesTo: null,
            """
            YOUR ROLE: Picture critic. You are looking at what the user will see.

            If reference pictures accompany this message, COMPARE DIRECTLY and say which is better. If
            ours loses, name the LARGEST meaningful gap — one thing, the one that would most change a
            trader's impression — rather than cataloguing every difference.

            The bar is a compass, not a ceiling. A generated panel will not match a hand-built terminal
            window and is not expected to; the demanding standard is there to keep it improving, not to
            be reached before you may say it is fine.

            Judge: does it read as a professional trading panel at a glance, is the information
            legible, is anything empty, cramped, overlapping or floating unexplained.
            """),

        new(Critics.ChartCraft, CriticPanel.Picture, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Chart-craft critic. The conventions a trading panel is READ by.

            Look for:
            - No axis, or an axis with no labels. A chart of thousands of correct primitives and no
              y-axis is unreadable, and every rung below you passed it.
            - A scale that does not fit its data — a y-axis of 0..1 on an instrument priced in
              thousands, or a range so wide the series is a flat line.
            - Series drawn with no legend and no way to tell them apart.
            - Labels that collide with each other or run outside their panel.
            - COLOUR SEMANTICS. In this domain colour carries meaning: bid versus ask, buy versus sell
              aggression, positive versus negative delta, a value area against everything outside it.
              A panel that colours them arbitrarily, or that uses the same colour for two opposed
              things, is misreading waiting to happen.
            - Literal colours instead of theme tokens: correct in one theme, unreadable in the other.
            - Density: a heatmap of four cells, or a ladder of two rows, is a placeholder.
            """),

        new(Critics.MarketLogic, CriticPanel.Quant, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Market-logic critic. Read the code. This is the panel that catches what
            verification structurally cannot: an indicator that is simply the wrong indicator.

            An RSI smoothed with an EMA instead of Wilder's compiles, runs, draws and trades. It is
            not an RSI. No rung can tell.

            Look for:
            - LOOK-AHEAD. Using a bar's close to decide something at that bar's open; indexing forward;
              reading a value that would not exist yet in real time.
            - REPAINTING: a level or signal that moves after the fact, so a backtest shows something
              live never showed.
            - WARM-UP. Emitting a signal before the estimator has the data it needs, or seeding at zero
              so the first N values are wrong in a way that quietly biases everything after.
            - Denominators that can be zero, and standard deviations that can be negative under
              floating point.
            - Wrong maths for the named indicator, wrong smoothing constant, a window off by one.
            - Sessions and time zones: a daily boundary assumed to be midnight UTC, a gap treated as a
              move.
            - Tick size and contract multiplier, where the unit does anything with price differences.
            """),

        new(Critics.DataContract, CriticPanel.Quant, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Data-contract critic. Whether the unit lives inside what it declared.

            Look for:
            - Using data it did not declare. A unit declaring Bars that reads depth or the tape gets
              nothing at runtime and silently draws an empty panel.
            - THE WALL CLOCK. DateTime.Now, Stopwatch, Environment.TickCount. A unit must read the host
              clock it is given, or it behaves differently in a replay than it did live, and no
              backtest of it means anything.
            - UNBOUNDED BUFFERS. A list appended to per tick and never trimmed. This is the defect that
              does not appear until hour four of a live session, when nobody is watching for it — and
              it has taken this product to twenty gigabytes before.
            - Allocation per frame in the draw path, or per tick in the data path.
            - Anything owning a disposable resource without disposing it.
            """),

        new(Critics.Book, CriticPanel.Quant, NeedsPicture: false, AppliesTo: AuthoringKind.Strategy,
            """
            YOUR ROLE: Book critic. What this strategy does to a position, and what it would do to a
            real account.

            Look for:
            - Size that is not bounded — a position that can grow without limit, or that scales with
              something unbounded.
            - No exit: an entry condition with no rule that ever closes it.
            - Stops or targets in the wrong units — points where the price is in ticks, percent where
              it is absolute.
            - Reversing without flattening, or stacking entries where one was intended.
            - Signals on every bar, so the thing trades continuously and pays the spread forever.
            - Nothing that flattens when the unit stops.
            """),

        new(Critics.Integration, CriticPanel.Quant, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Integration critic. The unit as ONE thing.

            These are the failures BETWEEN the parts, which is exactly what a builder writing one file
            cannot see.

            Look for:
            - A parameter declared in the schema and then never read, or read but ignored in spirit —
              a "period" that changes nothing.
            - Panels that disagree with each other: two panels showing the same thing, a layout
              declaring a panel nothing draws into, a panel drawn that the layout never declared.
            - A helper computed and never used, or computed twice by two different files.
            - The picture disagreeing with the logic: a level drawn at a price the code never
              calculates.
            - Anything the unit's own name or title promises that it does not do.
            """),
    ];

    /// <summary>The critics that apply to a unit of this kind.</summary>
    public static IReadOnlyList<CriticDefinition> For(AuthoringKind kind) =>
        [.. All.Where(c => c.AppliesTo is null || c.AppliesTo == kind)];
}
