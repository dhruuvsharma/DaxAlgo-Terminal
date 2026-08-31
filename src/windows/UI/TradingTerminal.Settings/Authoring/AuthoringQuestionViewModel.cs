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
