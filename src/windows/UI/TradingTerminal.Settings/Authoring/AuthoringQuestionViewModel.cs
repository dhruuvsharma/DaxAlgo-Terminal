using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Infrastructure.Strategies.Authoring;

namespace TradingTerminal.App.Authoring;

/// <summary>One selectable answer.</summary>
public sealed partial class AuthoringOptionViewModel : ObservableObject
{
    public AuthoringOptionViewModel(AuthoringOption option, Action<AuthoringOptionViewModel> onToggled)
    {
        Label = option.Label;
        Detail = option.Detail;
        _onToggled = onToggled;
    }

    private readonly Action<AuthoringOptionViewModel> _onToggled;

    public string Label { get; }

    public string Detail { get; }

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value) => _onToggled(this);
}

/// <summary>
/// One question, as buttons.
///
/// <para>Single-choice questions deselect their siblings on pick rather than using radio groups: the
/// options are generated, so there is no stable group name to bind to, and a wrong one silently makes
/// every question on screen share a selection.</para>
/// </summary>
public sealed partial class AuthoringQuestionViewModel : ObservableObject
{
    private bool _updating;

    public AuthoringQuestionViewModel(AuthoringQuestion question)
    {
        Model = question;
        Options = new ObservableCollection<AuthoringOptionViewModel>(
            question.Options.Select(option => new AuthoringOptionViewModel(option, OnOptionToggled)));
    }

    public AuthoringQuestion Model { get; }

    public string Prompt => Model.Prompt;

    public bool AllowOther => Model.AllowOther;

    public bool IsMultiple => Model.Mode == AuthoringAnswerMode.Multiple;

    /// <summary>Shown beside the prompt so the interaction is obvious before the first click.</summary>
    public string ModeHint => IsMultiple ? "choose any" : "choose one";

    public ObservableCollection<AuthoringOptionViewModel> Options { get; }

    /// <summary>Free text, offered unless the model said its options are exhaustive.</summary>
    [ObservableProperty]
    private string _other = string.Empty;

    partial void OnOtherChanged(string value) => OnPropertyChanged(nameof(Answer));

    /// <summary>
    /// This question's answer, or empty when unanswered.
    ///
    /// <para>Free text is appended to the selected options rather than replacing them: someone who
    /// picks two options and then types a qualifier means all three things, and dropping the chips
    /// would silently discard two deliberate clicks.</para>
    /// </summary>
    public string Answer
    {
        get
        {
            var picked = Options.Where(o => o.IsSelected).Select(o => o.Label).ToList();
            if (!string.IsNullOrWhiteSpace(Other)) picked.Add(Other.Trim());
            return string.Join(", ", picked);
        }
    }

    public bool IsAnswered => !string.IsNullOrWhiteSpace(Answer);

    private void OnOptionToggled(AuthoringOptionViewModel toggled)
    {
        if (_updating) return;

        // Only a selection displaces the others; deselecting the current pick just leaves the question
        // unanswered, which is a legitimate thing to do on the way to typing something instead.
        if (!IsMultiple && toggled.IsSelected)
        {
            _updating = true;
            try
            {
                // Clear every OTHER option. Keyed off the one just clicked rather than the last
                // selected in collection order — those differ the moment someone picks the third
                // option and then the first, and the collection-order version would clear the click
                // they just made.
                foreach (var option in Options)
                {
                    if (!ReferenceEquals(option, toggled)) option.IsSelected = false;
                }
            }
            finally
            {
                _updating = false;
            }
        }

        OnPropertyChanged(nameof(Answer));
        OnPropertyChanged(nameof(IsAnswered));
    }
}

/// <summary>
/// A one-click reply shown whenever the builder is waiting on the user.
///
/// <para><b>Why these exist alongside the model's own options.</b> A model that stops without code has
/// not necessarily asked a multiple-choice question. Far more often it writes a specification and waits
/// for approval — "here is what I am about to build, say so if you want it to trade instead". There are
/// no options to enumerate there, so the structured-question format does not apply, and the user was
/// left with an empty composer and a paragraph to re-read.</para>
///
/// <para>So the affordance is unconditional: whenever a turn ends waiting, the obvious replies are
/// buttons. The model cooperating with the question format adds options; it not cooperating no longer
/// leaves the user typing.</para>
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
}
