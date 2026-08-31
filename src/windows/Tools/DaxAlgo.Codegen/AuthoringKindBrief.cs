using System.Text;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The block that tells the model which of the two things it is writing.
///
/// <para>Without it the Strategy/Visualizer switch was decoration: the pane kept the choice with the
/// session, said so in a notice, and sent an identical prompt either way — so a user who asked for a
/// visualizer got a strategy and had to notice for themselves.</para>
///
/// <para><b>A short block rather than a second context pack.</b> The issue called for a visualizer pack,
/// and the shared one already teaches both contracts — a full second copy would have duplicated some
/// ninety percent of it, and the duplicate half is exactly the half that drifts. What actually differs
/// between the two is small and sharp, so that is what is said, appended after the shared text so the
/// cached prefix stays whole.</para>
/// </summary>
public static class AuthoringKindBrief
{
    /// <summary>The block for a kind, or empty when there is nothing kind-specific to add.</summary>
    public static string For(AuthoringKind kind) => kind switch
    {
        AuthoringKind.Visualizer => Visualizer,
        _ => Strategy,
    };

    /// <summary>Repeated in every kind block on purpose. It is in the shared pack too, but that
    /// sits far earlier in the prompt; a model asked to produce a long specification reliably
    /// forgot it by the time it finished writing one. This is the last instruction before the
    /// answer.</summary>
    private const string OfferAnswers = """
        ## If you need to ask, offer the answers

        When you stop without code — to ask something, or to put a specification up for approval —
        add a `questions` block so the builder can render your options as buttons. Prose above it is
        shown; the block is not.

        ```questions
        [
          { "id": "confirm", "question": "Build this specification?", "kind": "single",
            "options": [ { "label": "Yes, as specified" },
                         { "label": "Yes, but minimal first" },
                         { "label": "Change the indicators" } ] }
        ]
        ```

        This applies to a written-out spec as much as to a genuine question: "here is what I will
        build, confirm it" IS a question, and it has obvious options. A reply that ends by asking for
        confirmation and offers none leaves the user re-reading a paragraph to work out what to type.
        """;

    /// <summary>Folds the kind block, the reminder, and the exemplar into a system prompt.</summary>
    /// <param name="brief">The user's own words, when known, so the exemplar can match the question the
    /// way the skills already do. Null keeps the default exemplar.</param>
    public static string Compose(string systemContext, AuthoringKind kind, string? brief = null)
    {
        var block = For(kind);
        if (string.IsNullOrEmpty(block)) return systemContext;

        // Everything below goes AFTER the shared context, which is the cached prefix. Anything inserted
        // before it changes the prefix and costs a full cache miss on every session.
        //
        // Order is deliberate: what you are writing, then how to ask if you must, then a finished
        // example. The reminder to offer answers sits last-but-one because a model that has just read a
        // complete unit is about to write one, and the instruction it needs at that moment is what to
        // do if it cannot.
        var composed = new StringBuilder(systemContext.TrimEnd())
            .Append("\n\n").Append(block.TrimEnd())
            .Append('\n').Append(OfferAnswers.TrimEnd());

        var exemplar = AuthoringExemplar.Block(kind, brief);
        if (!string.IsNullOrWhiteSpace(exemplar))
            composed.Append("\n\n").Append(exemplar.TrimEnd());

        return composed.ToString();
    }

    private const string Visualizer = """
        ## What you are writing right now: a VISUALIZER

        The user asked for a visualizer, not a strategy. Implement `IVisualizer`.

        - Your context is `IVisualizerContext`. **It has no `Book`.** A visualizer cannot take a
          position, cannot set a target, cannot place an order — there is no API through which to do
          any of it, so do not write code that tries.
        - Drawing is the entire point. A visualizer that paints nothing has no purpose and is rejected;
          verification checks this specifically for visualizers rather than merely preferring it. That
          check counts every panel, so a visualizer that declares a `UnitLayout` and leaves its own
          `Draw` empty is fine — the panels are doing the painting.
        - Everything else is the same: compute in the data callbacks, keep only what the picture needs
          in a bounded buffer, read that field in `Draw`.

        If the brief actually describes something that takes positions, say so plainly rather than
        quietly implementing `IStrategyKernel` — the user chose the kind on purpose, and a strategy
        delivered under a visualizer's name is worse than a question.
        """;

    private const string Strategy = """
        ## What you are writing right now: a STRATEGY

        The user asked for a strategy, not a visualizer. Implement `IStrategyKernel`.

        - Your context is `IStrategyRuntimeContext`, which has `Book` — the virtual book is the only
          route to a position, and it is what makes paper and live the same code path.
        - `Draw` is optional but nearly always worth writing: a strategy that took a position and shows
          no picture of the signal it acted on cannot be argued with.
        """;
}
