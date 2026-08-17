namespace TradingTerminal.Core.Execution;

/// <summary>Where a strategy's orders are allowed to go.</summary>
public enum TradingMode
{
    /// <summary>
    /// Orders are recorded and monitored inside the application and go no further. Nothing reaches a
    /// broker. This is the default and it is what the app starts in every single time.
    /// </summary>
    Paper,

    /// <summary>Orders route to real broker accounts and move real money.</summary>
    Real,
}

/// <summary>
/// The application-wide Paper/Real switch, set from the broker login window.
///
/// <para><b>This is an arm/disarm, not an authorization.</b> It is the outer of two independent
/// gates: this one says "the user has deliberately armed real trading in this session", and
/// <c>LiveExecutionConfirmation</c> in the execution engine still says "and this exact broker/account
/// binding was separately acknowledged". A live order requires BOTH. Collapsing them into one switch
/// would mean a single accidental click could route every account to real money, which is precisely
/// what the per-account gate exists to prevent.</para>
///
/// <para><b>It always starts at <see cref="TradingMode.Paper"/>.</b> Deliberately not persisted:
/// arming real trading is a decision the user should retake each session, not something a stale
/// setting file can make on their behalf after an update, a crash, or a machine handover.</para>
///
/// <para>Lives in Core because both the login window (which sets it) and the execution layer (which
/// reads it) must see it, and Core is the only project both already reference.</para>
/// </summary>
public sealed class ExecutionModeSelection
{
    /// <summary>The exact word the user must type to arm real trading. Matches the execution engine's
    /// own requirement so the two gates cannot drift apart.</summary>
    public const string RequiredAcknowledgement = "LIVE";

    private readonly object _gate = new();
    private TradingMode _mode = TradingMode.Paper;

    /// <summary>The current mode. Never <see cref="TradingMode.Real"/> unless someone typed the word.</summary>
    public TradingMode Mode
    {
        get { lock (_gate) return _mode; }
    }

    /// <summary>True while orders are confined to the application.</summary>
    public bool IsPaper => Mode == TradingMode.Paper;

    /// <summary>Raised after the mode changes, so shells can update chrome and warn loudly.</summary>
    public event EventHandler<TradingMode>? Changed;

    /// <summary>
    /// Arms real trading. Requires the literal acknowledgement, so this cannot be flipped by a stray
    /// click or a bound checkbox — the caller has to have collected an explicit typed confirmation.
    /// </summary>
    /// <param name="acknowledgement">Must equal <see cref="RequiredAcknowledgement"/>, exactly.</param>
    /// <param name="confirmedBy">Who armed it, for the record. Must not be blank.</param>
    /// <param name="reason">Why it was refused, when it was.</param>
    public bool TryEnableReal(string? acknowledgement, string? confirmedBy, out string reason)
    {
        if (!string.Equals(acknowledgement, RequiredAcknowledgement, StringComparison.Ordinal))
        {
            reason = $"Type {RequiredAcknowledgement} exactly to enable real trading.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(confirmedBy))
        {
            reason = "Real trading needs an identity on the record.";
            return false;
        }

        SetMode(TradingMode.Real);
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Returns to paper. Never refused and never requires a confirmation — disarming must always be
    /// one action away, whatever state anything else is in.
    /// </summary>
    public void SetPaper() => SetMode(TradingMode.Paper);

    private void SetMode(TradingMode mode)
    {
        bool changed;
        lock (_gate)
        {
            changed = _mode != mode;
            _mode = mode;
        }

        if (changed) Changed?.Invoke(this, mode);
    }
}
