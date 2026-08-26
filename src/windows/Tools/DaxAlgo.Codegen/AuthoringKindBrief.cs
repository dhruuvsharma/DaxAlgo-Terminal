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

    /// <summary>Folds the block into a system prompt.</summary>
    public static string Compose(string systemContext, AuthoringKind kind)
    {
        var block = For(kind);
        return string.IsNullOrEmpty(block) ? systemContext : systemContext.TrimEnd() + "\n\n" + block;
    }

    private const string Visualizer = """
        ## What you are writing right now: a VISUALIZER

        The user asked for a visualizer, not a strategy. Implement `IVisualizer`.

        - Your context is `IVisualizerContext`. **It has no `Book`.** A visualizer cannot take a
          position, cannot set a target, cannot place an order — there is no API through which to do
          any of it, so do not write code that tries.
        - `Draw` is the entire point. A visualizer that draws nothing has no purpose and is rejected;
          verification checks this specifically for visualizers rather than merely preferring it.
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
