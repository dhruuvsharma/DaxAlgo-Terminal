namespace TradingTerminal.Core.Execution;

/// <summary>Where a strategy's orders are allowed to go.</summary>
public enum TradingMode
{
    /// <summary>
    /// Real-money dispatch is disarmed. Orders bound for a broker's <b>live</b> endpoint are refused;
    /// they are recorded and monitored in the ledger and go no further. This is the default and it is
    /// what the app starts in every single time.
    ///
    /// <para><b>A broker's own paper endpoint still works in this mode</b>, and deliberately so. The
    /// doc here read "nothing reaches a broker" until 2026-08-27, which if enforced literally would
    /// have meant arming <i>real</i> trading just to use Alpaca paper — pushing users to arm live money
    /// for an activity that involves none. The switch guards real money, not network traffic.</para>
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
/// <para><b>Where "BOTH" is actually enforced:</b> <c>ExecutionCoordinator.TryAuthorizeLiveDispatch</c>,
/// the single point every adapter Submit, Cancel and Replace passes through. Only Submit and Replace
/// are gated by this switch; <b>Cancel never is</b>, because disarming while live orders are working at
/// a broker must not strand them.</para>
///
/// <para>That enforcement did not exist until 2026-08-27. This class was defined, registered, and wired
/// to the login toggle, and nothing on the execution path read it — the sentence above claiming a live
/// order required both gates was, for as long as it had been written, false. A coordinator built
/// without one of these <b>refuses live dispatch</b> rather than allowing it, so a composition that
/// forgets to supply the switch fails closed and loudly instead of quietly ungated.</para>
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

    /// <summary>
    /// True only when real trading has been deliberately armed this session.
    ///
    /// <para><b>The gate tests this rather than <see cref="IsPaper"/>, and the difference is the point.</b>
    /// They are inverses today because there are two modes. If a third is ever added — a broker's demo
    /// endpoint, a shadow mode, anything — then <c>!IsPaper</c> would silently start permitting live
    /// dispatch under it, while <c>!IsReal</c> keeps refusing until someone deliberately says otherwise.
    /// Asking "is this armed?" fails closed; asking "is this not paper?" fails open.</para>
    /// </summary>
    public bool IsReal => Mode == TradingMode.Real;

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
