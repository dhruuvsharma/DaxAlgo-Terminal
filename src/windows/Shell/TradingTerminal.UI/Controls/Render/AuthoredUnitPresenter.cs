using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.UI.Controls.Render;

/// <summary>One line in the unit's activity log.</summary>
/// <param name="TimestampUtc">When it happened.</param>
/// <param name="Source">What produced it — the unit, the runtime, or the book.</param>
/// <param name="Message">The line itself.</param>
public readonly record struct AuthoredUnitLogLine(DateTime TimestampUtc, string Source, string Message);

/// <summary>
/// One verb, as the window binds it: what the button says, what it says on hover, and the id sent
/// back to the unit when it is pressed.
///
/// <para>A class rather than the SDK's <see cref="UnitAction"/> record struct, because WPF binds to
/// reference types and a record struct in an <c>ObservableCollection</c> is a needless box on every
/// command parameter. The SDK type stays the contract; this is its presentation.</para>
/// </summary>
public sealed class UnitActionButton(string id, string label, string? detail)
{
    public string Id { get; } = id;

    public string Label { get; } = label;

    public string? Detail { get; } = detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>The virtual book summary shown beneath a strategy's picture.</summary>
/// <param name="PositionUnits">Signed position; zero means flat.</param>
/// <param name="AverageEntryPrice">Average entry, or zero while flat.</param>
/// <param name="RealizedProfitAndLoss">Realised P&amp;L for the session.</param>
/// <param name="Equity">Current model equity.</param>
/// <param name="OpenOrders">Resting orders the book is waiting on.</param>
public readonly record struct AuthoredUnitBook(
    double PositionUnits,
    double AverageEntryPrice,
    double RealizedProfitAndLoss,
    double Equity,
    int OpenOrders);

/// <summary>
/// What the host needs in order to show an authored strategy or visualizer.
///
/// <para>The anatomy is deliberately the same for both, because a strategy IS a visualizer that can
/// also trade: parameters on top, the author's own picture in the middle, and — for a strategy — the
/// virtual book and trade log beneath. The author draws the middle and nothing else, so the chrome is
/// identical every time and cannot be omitted, mis-styled, or forgotten.</para>
///
/// <para><see cref="HasBook"/> is the only thing that differs between the two kinds, which is the
/// point: the difference is a capability, not a different way of building a window.</para>
/// </summary>
public sealed partial class AuthoredUnitPresenter : ObservableObject
{
    /// <summary>How many log lines are kept. Bounded because a chatty unit runs for hours.</summary>
    public const int MaximumLogLines = 500;

    /// <summary>Display name shown on the parameter expander.</summary>
    [ObservableProperty]
    private string _title = string.Empty;

    /// <summary>
    /// The author's frame callback — normally the unit's <c>Draw</c>. Null renders an empty surface,
    /// which is what a unit that draws nothing legitimately looks like.
    /// </summary>
    [ObservableProperty]
    private Action<IRenderSurface>? _draw;

    /// <summary>
    /// How the body is divided into panels. <see cref="DaxAlgo.Sdk.Layout.UnitLayout.Single"/> — the
    /// default — is one panel drawn by <see cref="Draw"/>, which is what almost every unit wants.
    /// </summary>
    [ObservableProperty]
    private DaxAlgo.Sdk.Layout.UnitLayout _layout = DaxAlgo.Sdk.Layout.UnitLayout.Single;

    /// <summary>True for a strategy, false for a visualizer. Drives the book row only.</summary>
    [ObservableProperty]
    private bool _hasBook;

    [ObservableProperty]
    private AuthoredUnitBook _book;

    /// <summary>Whether the parameter expander starts open. Closed once a unit is running.</summary>
    [ObservableProperty]
    private bool _isSetupExpanded = true;

    // -- Run state -------------------------------------------------------------------------------

    /// <summary>
    /// What the unit is doing, in one word: Live, Paused, Stopped, or why it is not running.
    ///
    /// <para>A window that draws is not necessarily a window that is receiving data. A frozen picture
    /// and a quiet market look identical, and until this existed the only way to tell them apart was
    /// to read the activity log.</para>
    /// </summary>
    [ObservableProperty]
    private string _runState = "Live";

    /// <summary>True while the unit is taking market data. Drives the run indicator and which of
    /// pause/resume is offered.</summary>
    [ObservableProperty]
    private bool _isLive = true;

    /// <summary>True when the host supports pausing this unit — false leaves the control out rather
    /// than showing one that does nothing.</summary>
    [ObservableProperty]
    private bool _canPause;

    /// <summary>Raised when the user asks to pause or resume. The host owns the runtime; this only
    /// says what was asked for.</summary>
    public event EventHandler<bool>? PauseRequested;

    [RelayCommand]
    private void TogglePause()
    {
        if (!CanPause) return;

        // The argument is "pause?", so a LIVE unit asks for true. Written as !IsLive it read
        // plausibly and did the opposite, which is the sort of inversion a picture cannot show you.
        PauseRequested?.Invoke(this, IsLive);
    }

    // -- Parameters ------------------------------------------------------------------------------

    /// <summary>
    /// Whether the parameter rows can be edited and applied.
    ///
    /// <para>False unless the host supplied a way to apply them, which is what keeps a read-only
    /// window honest: an editable box over a value nothing reads is worse than a label.</para>
    /// </summary>
    [ObservableProperty]
    private bool _canEditParameters;

    /// <summary>What the last apply did, or what is stopping the next one.</summary>
    [ObservableProperty]
    private string _parameterStatus = string.Empty;

    /// <summary>True while an apply is in flight — the unit is being rebuilt.</summary>
    [ObservableProperty]
    private bool _isApplying;

    /// <summary>True when at least one row differs from what the unit is running with.</summary>
    public bool HasPendingChanges => Parameters.Any(p => p.IsDirty);

    /// <summary>
    /// Raised when the user applies edited parameters, carrying the parsed values.
    ///
    /// <para>An event rather than a direct call because restarting a unit means disposing a sandbox
    /// runtime and building another, which is the shell's business. This library deliberately knows
    /// nothing about the runtime — see <c>AuthoredUnitHost</c>.</para>
    /// </summary>
    public event EventHandler<IReadOnlyDictionary<string, object?>>? ApplyRequested;

    /// <summary>Re-evaluates <see cref="HasPendingChanges"/> after a row is edited.</summary>
    internal void OnParameterEdited() => OnPropertyChanged(nameof(HasPendingChanges));

    /// <summary>
    /// Validates every row and, if they all parse, asks the host to run with them.
    ///
    /// <para>Every row is checked before any is applied. Applying the valid half would leave the unit
    /// running a mixture the user never asked for, and the picture would be evidence for a
    /// configuration that exists nowhere.</para>
    /// </summary>
    [RelayCommand]
    private void ApplyParameters()
    {
        if (!CanEditParameters || IsApplying) return;

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);
        var invalid = 0;

        foreach (var parameter in Parameters)
        {
            if (parameter.TryParse(out var value)) values[parameter.Key] = value;
            else invalid++;
        }

        if (invalid > 0)
        {
            ParameterStatus = invalid == 1
                ? "One parameter is not valid — see the highlighted row."
                : $"{invalid} parameters are not valid.";
            return;
        }

        IsApplying = true;
        ParameterStatus = "Applying…";
        ApplyRequested?.Invoke(this, values);
    }

    /// <summary>Called by the host once the unit is running with the applied values.</summary>
    public void ParametersApplied(string? message = null)
    {
        foreach (var parameter in Parameters) parameter.Commit();
        IsApplying = false;
        ParameterStatus = message ?? "Applied.";
        OnParameterEdited();
    }

    /// <summary>Called by the host when the apply failed. The edits are kept: the user typed them,
    /// and throwing them away would be the second thing to go wrong.</summary>
    public void ParametersFailed(string message)
    {
        IsApplying = false;
        ParameterStatus = message;
    }

    /// <summary>Puts every row back to the value the unit is running with.</summary>
    [RelayCommand]
    private void ResetParameters()
    {
        foreach (var parameter in Parameters) parameter.Revert();
        ParameterStatus = string.Empty;
        OnParameterEdited();
    }

    /// <summary>Parameters the unit declared, as label/value pairs the expander renders.</summary>
    public ObservableCollection<AuthoredUnitParameter> Parameters { get; } = [];

    // -- Actions ---------------------------------------------------------------------------------

    /// <summary>
    /// Verbs the unit declared, rendered as buttons beside the parameters.
    ///
    /// <para>Separate from <see cref="Parameters"/> because they are a different kind of thing: a
    /// parameter is a value you set and apply, an action is a thing that happens when you press it.
    /// Folding a verb into the parameter list would give it Apply and Reset, which mean nothing for
    /// it, and would make it look like a setting the user had to remember to commit.</para>
    /// </summary>
    public ObservableCollection<UnitActionButton> Actions { get; } = [];

    /// <summary>True when the unit declared any verbs.</summary>
    public bool HasActions => Actions.Count > 0;

    /// <summary>
    /// Whether the setup expander is shown at all: parameters OR verbs.
    ///
    /// <para>Was <c>HasParameters</c> alone, which was right while parameters were the only thing in
    /// there. A visualizer that declares no parameters and one action — the ordinary shape for
    /// "clear the tape" on a picture with nothing to tune — would have had its button built, bound and
    /// never shown, which is the defect this whole area keeps producing.</para>
    /// </summary>
    public bool HasSetup => HasParameters || HasActions;

    /// <summary>
    /// Raised when the user presses one, carrying its id.
    ///
    /// <para>An event rather than a delegate the presenter holds, matching parameters and pause: the
    /// presenter is what the window binds to, and it must not know what running an action means.</para>
    /// </summary>
    public event EventHandler<string>? ActionRequested;

    [RelayCommand]
    private void InvokeAction(UnitActionButton? action)
    {
        if (action is { Id.Length: > 0 }) ActionRequested?.Invoke(this, action.Id);
    }

    /// <summary>
    /// Whether the parameter expander is shown at all.
    ///
    /// <para>Keyed off what the unit <b>declares</b>, not off its kind. A strategy always takes
    /// parameters so its expander is always there; a visualizer usually takes none, so its window is
    /// the picture and the activity log — the log-only shape a visualizer is supposed to have. Keying
    /// it off the kind instead would swallow the parameters of a visualizer that genuinely has some,
    /// and the author would have no way to tell why they never appeared.</para>
    /// </summary>
    public bool HasParameters => Parameters.Count > 0;

    public AuthoredUnitPresenter()
    {
        // A computed property over a collection notifies nobody on its own. Without this the expander
        // keeps whatever visibility it had when the window was built, so a unit whose parameters are
        // populated after construction — which is the ordinary order — would show an empty expander or
        // no expander at all, depending on timing.
        Parameters.CollectionChanged += (_, e) =>
        {
            foreach (var added in e.NewItems?.OfType<AuthoredUnitParameter>() ?? [])
                added.Owner = this;

            OnPropertyChanged(nameof(HasParameters));
            OnPropertyChanged(nameof(HasSetup));
            OnParameterEdited();
        };

        // Same reason, and the same failure if it is missing: the actions are added after
        // construction, so a strip that never re-evaluated its visibility would stay hidden and the
        // buttons would be declared, built, bound and invisible.
        Actions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasActions));
            OnPropertyChanged(nameof(HasSetup));
        };
    }

    /// <summary>
    /// Asks the view for a repaint.
    ///
    /// <para>An event rather than a property because a frame is not state: the picture the unit would
    /// draw has changed, and nothing about the presenter has. Raised by whatever paces the unit — see
    /// <see cref="AuthoredUnitHost"/> — so the presenter never owns a timer of its own.</para>
    /// </summary>
    public event EventHandler? FrameRequested;

    /// <summary>Raises <see cref="FrameRequested"/>.</summary>
    public void RequestFrame() => FrameRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>The unit's own log, newest last.</summary>
    public ObservableCollection<AuthoredUnitLogLine> Log { get; } = [];

    /// <summary>
    /// Appends a log line, dropping the oldest past <see cref="MaximumLogLines"/>. Trimming here
    /// rather than in the view means a unit left running overnight cannot grow the window's memory
    /// without bound.
    /// </summary>
    public void Append(AuthoredUnitLogLine line)
    {
        Log.Add(line);
        while (Log.Count > MaximumLogLines)
            Log.RemoveAt(0);
    }
}

/// <summary>
/// One row in the parameter expander — editable when the host can apply it, and typed enough to
/// refuse a value the unit could not run with.
///
/// <para>This was a label and a string. Changing a look-back and watching the picture move is the
/// single most common thing anyone does with a trading tool, and it was the one thing an authored
/// window could not do; issue #42 specified an MT5-style parameter panel and what existed was an
/// MT5-style parameter <i>display</i>.</para>
///
/// <para><b>Validation happens here, not at the runtime.</b> A bad value that reaches the sandbox
/// comes back as a failed start, which reads to the user as the unit being broken rather than the
/// number being wrong.</para>
/// </summary>
public sealed partial class AuthoredUnitParameter : ObservableObject
{
    /// <summary>The presenter this row belongs to, set as it is added, so an edit can announce
    /// itself without every row holding a subscription.</summary>
    internal AuthoredUnitPresenter? Owner { get; set; }

    /// <summary>The schema key this row edits. Empty for a row with no schema behind it.</summary>
    public string Key { get; init; } = string.Empty;

    /// <summary>What kind of value this is — decides the editor and the parse.</summary>
    public ParameterKind Kind { get; init; } = ParameterKind.Text;

    /// <summary>The choices, for a Choice/Enum parameter.</summary>
    public IReadOnlyList<string> Choices { get; init; } = [];

    /// <summary>True when this row is a fixed set rather than free text.</summary>
    public bool IsChoice => Kind == ParameterKind.Choice && Choices.Count > 0;

    /// <summary>True for a toggle.</summary>
    public bool IsBoolean => Kind == ParameterKind.Boolean;

    /// <summary>True when the row is an ordinary text box — everything that is neither.</summary>
    public bool IsFreeText => !IsChoice && !IsBoolean;

    /// <summary>Declared lower bound, or null.</summary>
    public double? Minimum { get; init; }

    /// <summary>Declared upper bound, or null.</summary>
    public double? Maximum { get; init; }

    /// <summary>The unit the number is in, shown after the editor. Empty for none.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>The author's own description, shown as the row's tooltip.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The declared range, as a hint under the editor. Empty when unbounded.</summary>
    public string RangeHint => (Minimum, Maximum) switch
    {
        (null, null) => string.Empty,
        ({ } min, null) => $"≥ {min:0.####}",
        (null, { } max) => $"≤ {max:0.####}",
        ({ } min, { } max) => $"{min:0.####} … {max:0.####}",
    };

    [ObservableProperty]
    private string _label = string.Empty;

    [ObservableProperty]
    private string _value = string.Empty;

    /// <summary>The value the unit is actually running with. <see cref="Value"/> may have moved
    /// ahead of it while the user is typing.</summary>
    public string AppliedValue { get; private set; } = string.Empty;

    /// <summary>True when the row has been edited and not yet applied.</summary>
    public bool IsDirty => !string.Equals(Value, AppliedValue, StringComparison.Ordinal);

    /// <summary>Why this row will not parse, or empty.</summary>
    [ObservableProperty]
    private string _error = string.Empty;

    /// <summary>True when <see cref="Error"/> has something to say — the binding the row highlights on.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    partial void OnValueChanged(string value)
    {
        // Validated on every keystroke rather than on apply: a range violation shown while the number
        // is being typed is a correction, and the same message after pressing Apply is a rejection.
        Error = TryParse(out _) ? string.Empty : Error;
        OnPropertyChanged(nameof(IsDirty));
        Owner?.OnParameterEdited();
    }

    /// <summary>Records <see cref="Value"/> as what the unit is running with.</summary>
    public void Commit()
    {
        AppliedValue = Value;
        Error = string.Empty;
        OnPropertyChanged(nameof(IsDirty));
    }

    /// <summary>Puts the row back to the running value.</summary>
    public void Revert()
    {
        Value = AppliedValue;
        Error = string.Empty;
    }

    /// <summary>Sets both the shown and the running value — how the host seeds a row.</summary>
    public void Seed(string value)
    {
        Value = value;
        Commit();
    }

    /// <summary>
    /// Parses the row into the type the schema declares, or explains why it will not.
    ///
    /// <para>Culture-invariant on purpose. The value goes to a sandbox that compares it against
    /// literals in generated code, and a decimal comma reaching that is a parameter that silently
    /// means something else on one machine.</para>
    /// </summary>
    public bool TryParse(out object? parsed)
    {
        parsed = null;
        var text = Value?.Trim() ?? string.Empty;

        switch (Kind)
        {
            case ParameterKind.Boolean:
                if (bool.TryParse(text, out var flag)) { parsed = flag; return true; }
                if (text is "on" or "1") { parsed = true; return true; }
                if (text is "off" or "0") { parsed = false; return true; }
                Error = "Must be true or false.";
                return false;

            case ParameterKind.Integer:
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
                {
                    Error = "Must be a whole number.";
                    return false;
                }
                if (!InRange(whole)) return false;
                parsed = whole;
                return true;

            case ParameterKind.Number:
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number))
                {
                    Error = "Must be a number.";
                    return false;
                }
                if (!InRange(number)) return false;
                parsed = number;
                return true;

            case ParameterKind.Choice:
                if (Choices.Count > 0 && !Choices.Contains(text, StringComparer.Ordinal))
                {
                    Error = "Not one of the choices.";
                    return false;
                }
                parsed = text;
                return true;

            case ParameterKind.Instrument:
                if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                {
                    Error = "Must be an instrument id.";
                    return false;
                }
                parsed = new InstrumentId(id);
                return true;

            default:
                parsed = Value ?? string.Empty;
                return true;
        }
    }

    private bool InRange(double value)
    {
        // The declared bounds, enforced. A schema that says 2..500 and a box that takes -1 is a
        // schema the author wrote for nothing.
        if (Minimum is { } min && value < min)
        {
            Error = $"Must be at least {min:0.####}.";
            return false;
        }

        if (Maximum is { } max && value > max)
        {
            Error = $"Must be at most {max:0.####}.";
            return false;
        }

        Error = string.Empty;
        return true;
    }
}
