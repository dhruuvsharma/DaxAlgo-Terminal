using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Gauntlet;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Blocks;

/// <summary>
/// The critics for a Blocks unit.
///
/// <para>Same ids, same output contract and same repair routing as the widget SDK's panel; what each is
/// told to look for is what a Blocks unit gets wrong. Chart-craft is gone — it policed widget calls and
/// primitives, and a unit that draws its own page has neither. The picture critic judges the page
/// itself, photographed while the unit fed it.</para>
/// </summary>
public static class BlocksCritics
{
    /// <summary>The panel, in the order they run.</summary>
    public static IReadOnlyList<CriticDefinition> All { get; } =
    [
        new(Critics.Picture, CriticPanel.Picture, NeedsPicture: true, AppliesTo: null,
            """
            YOUR ROLE: Picture critic. You are looking at the unit's page — what the user will see.

            If reference pictures accompany this message, COMPARE DIRECTLY and say which is better. If
            ours loses, name the LARGEST meaningful gap — the one thing that would most change a trader's
            impression.

            Judge: does it read as a professional trading panel at a glance; is every number legible and
            labelled; is anything empty, cramped, overlapping, cut off or showing "undefined", "NaN" or a
            waiting message after data arrived; do colours carry meaning (up/down, bid/ask); does a chart
            have axes a trader can read. Findings about the page name the file "ui/index.html".
            """),

        new(Critics.MarketLogic, CriticPanel.Quant, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Market-logic critic. Read the code. Catch what running it cannot: the wrong maths.

            Look for:
            - LOOK-AHEAD: deciding at a bar's open with its close; reading a value that would not exist
              yet in real time.
            - REPAINTING: a level or signal that moves after the fact.
            - WARM-UP: signals before an estimator has its data; seeding at zero.
            - Denominators that can be zero; wrong smoothing constants; windows off by one.
            - Two instruments treated as synchronised when their updates arrive separately.
            - Sessions and time zones; tick size and multiplier wherever prices are differenced.
            """),

        new(Critics.DataContract, CriticPanel.Quant, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Data-contract critic. Whether the unit lives inside the rules of its runtime.

            Look for:
            - DateTime.Now / UtcNow or Stopwatch instead of context.Clock.UtcNow.
            - A field touched from another thread: work after an awaited HTTP call, or inside a WebSocket
              loop, that does not come back through context.Schedule.Post.
            - UNBOUNDED BUFFERS: a list or dictionary appended per tick or per poll and never trimmed.
            - Network calls with no timeout, no cancellation, or a poll that can overlap itself.
            - Subscriptions and timers never disposed when a setting changes the instrument.
            - Sending deltas to the page (only the latest payload per topic arrives), or sending per tick
              where whole state on a timer would do.
            """),

        new(Critics.Book, CriticPanel.Quant, NeedsPicture: false, AppliesTo: AuthoringKind.Strategy,
            """
            YOUR ROLE: Book critic. What this strategy does to positions.

            Look for:
            - Size that is not bounded, or that grows with something unbounded.
            - An entry with no rule that ever exits it; stops or targets in the wrong units.
            - Legs of a spread or arbitrage sized or entered so one can fill without the other.
            - Targets set on every tick, so it churns and pays the spread forever.
            - Decisions that ignore what context.Portfolio says the position already is.
            """),

        new(Critics.Integration, CriticPanel.Quant, NeedsPicture: false, AppliesTo: null,
            """
            YOUR ROLE: Integration critic. The unit as ONE thing — the failures between its parts.

            Look for:
            - A setting declared in Info and never read, or read and ignored in spirit.
            - A topic the unit sends that the page never listens for, a topic the page sends that the unit
              never handles, or payload fields spelled differently on the two sides (C# PascalCase is sent
              as camelCase).
            - The page showing something the unit never sends, or a control on the page that does nothing.
            - A helper computed and never used, or computed twice.
            - Anything the unit's name or description promises that it does not do.
            """),
    ];

    /// <summary>The critics that apply to a unit of this kind.</summary>
    public static IReadOnlyList<CriticDefinition> For(AuthoringKind kind) =>
        [.. All.Where(c => c.AppliesTo is null || c.AppliesTo == kind)];
}
