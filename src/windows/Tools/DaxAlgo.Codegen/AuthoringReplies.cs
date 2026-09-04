namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// One canned reply a turn offers, as a button in the app and as a flag on the CLI.
///
/// <para><b>Why the affordance is unconditional.</b> A turn that ends waiting — with a specification,
/// or with questions — used to leave the user with an empty composer and a paragraph to re-read. So
/// whenever a turn ends waiting, the obvious replies are offered. The model cooperating with the
/// question format adds options; it not cooperating no longer leaves the user typing.</para>
///
/// <para><b>Why this lives here rather than in the pane that draws it.</b> The reply text is
/// <i>prompt</i> — the pack instructs the model to honour the escape, so the sentence that triggers it
/// is part of the contract, not a label. It was in the UI project, and the CLI could only have got it
/// by writing its own wording: a second escape phrase, drifting from the one the pack describes. This
/// area has produced enough of that shape already.</para>
/// </summary>
/// <param name="Label">What the button says.</param>
/// <param name="Reply">
/// The message it sends. Empty means "do not send anything" — put the cursor in the composer instead,
/// which is what "I want changes" needs: the user has something specific to say and a canned sentence
/// would be put in their mouth.
/// </param>
/// <param name="IsPrimary">True for the accept action, which is styled as the default.</param>
public readonly record struct AuthoringAction(string Label, string Reply, bool IsPrimary = false)
{
    /// <summary>The replies offered when a turn ends with a specification rather than a question.</summary>
    public static IReadOnlyList<AuthoringAction> Default { get; } =
    [
        new("Looks right — build it",
            "That specification is right. Build it exactly as described.",
            IsPrimary: true),

        new("Build it, but simpler",
            "Build it, but keep the first version minimal — the smallest thing that demonstrates the "
            + "idea working. Leave out anything optional and say what you left out."),

        // No reply text: this one hands the composer back rather than answering for the user.
        new("I want changes", string.Empty),
    ];

    /// <summary>
    /// The replies offered when the model asked something with options — the interview shape.
    ///
    /// <para><b>"Looks right — build it" does not belong here.</b> Beside "which instrument?" it sends
    /// "that specification is right", which answers a question nobody asked. The buttons a turn offers
    /// have to match the shape of the turn, or they are noise the user has to read past.</para>
    ///
    /// <para>What this shape needs instead is a way OUT. The pack now tells the model to ask as many
    /// questions as the job needs, in as many rounds as it needs — which is right for a window with a
    /// book, a heatmap and a strip, and intolerable without a one-click end to it. The escape is the
    /// other half of that instruction, and the model is told to honour it.</para>
    /// </summary>
    public static IReadOnlyList<AuthoringAction> WhenAsked { get; } =
    [
        // Deliberately asks for the assumptions back. "Just build it" without them leaves the user
        // holding a unit whose open questions were settled invisibly.
        new("Just build it",
            "Stop asking and build it now. Choose sensible defaults for anything still open, and list "
            + "what you assumed so I can correct it after I have seen it run.",
            IsPrimary: true),

        new("I want changes", string.Empty),
    ];

    /// <summary>The replies for a turn, chosen by its shape.</summary>
    /// <param name="asked">Whether the model offered structured questions this turn.</param>
    public static IReadOnlyList<AuthoringAction> For(bool asked) => asked ? WhenAsked : Default;

    /// <summary>
    /// The escape, on its own — what a non-interactive caller sends to end an interview.
    ///
    /// <para>The CLI is one-shot: it cannot show three questions and wait. <c>--just-build</c> sends
    /// exactly this, so a scripted run and a user pressing the button say the same words to the
    /// model.</para>
    /// </summary>
    public static string JustBuildIt => WhenAsked[0].Reply;

    /// <summary>
    /// Whether a user turn is one of these canned replies — that is, the user has pressed a button that
    /// means "stop interviewing and build it".
    ///
    /// <para>The agent path routes on <c>RoutingState.HasSpec</c>, and a model is free to keep asking
    /// however plainly it is told not to. When the user has pressed the escape the decision is no longer
    /// the model's, so the state is advanced here rather than hoped for in a reply. The escape has to
    /// actually escape.</para>
    /// </summary>
    public static bool EndsTheInterview(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = message.Trim();
        return Default.Concat(WhenAsked)
            .Where(action => action.Reply.Length > 0)
            .Any(action => text.StartsWith(action.Reply, StringComparison.OrdinalIgnoreCase));
    }
}
