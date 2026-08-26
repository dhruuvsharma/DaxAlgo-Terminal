namespace TradingTerminal.Core.Execution;

/// <summary>
/// The terminal's own paper account.
///
/// <para><b>One account, inside the application.</b> Paper trading used to mean "connect to your
/// broker's paper account" — an Interactive Brokers paper port, an Alpaca paper key — which meant a
/// separate account to open, fund and keep straight at every broker, and no way to paper-trade at all
/// against a venue that offers no paper environment. It also meant the app-wide Paper switch decided
/// nothing: the broker did.</para>
///
/// <para>Now the account is the terminal's. It starts at a hundred thousand, every fill is simulated
/// in-process, and the profit and loss lives here. No broker account is involved and none is needed;
/// market data still comes from whichever broker is connected, so the prices are real and only the
/// fills are not.</para>
///
/// <para><b>There is deliberately no balance stored here.</b> The order ledger already records every
/// fill durably, so the balance is derived from the starting figure plus what the ledger says
/// happened. A second money record would be a second source of truth about the same money, and the
/// two would eventually disagree — in a subsystem where disagreement is the whole failure mode.</para>
/// </summary>
public sealed class PaperAccountOptions
{
    public const string SectionName = "PaperAccount";

    /// <summary>
    /// What the account opens with. A hundred thousand by default.
    ///
    /// <para>Round and large enough that position sizing behaves like it would on a funded account —
    /// a strategy risking one percent has a thousand to work with, which prices most instruments
    /// sensibly. Small enough that a strategy which blows up still blows up, which is the point of
    /// paper trading.</para>
    /// </summary>
    public decimal StartingBalance { get; set; } = 100_000m;

    /// <summary>
    /// The account's currency. A real one, not a placeholder.
    ///
    /// <para>The simulated adapter used to open in a currency called <c>SIM</c> with a balance of
    /// zero, which is why an in-process book could accept an order and never have the money to
    /// settle it.</para>
    /// </summary>
    public string Currency { get; set; } = "USD";

    /// <summary>The account id the terminal's paper book trades under. Fixed, because there is one
    /// paper account and not one per broker.</summary>
    public const string AccountId = "paper";

    /// <summary>The adapter id behind it.</summary>
    public const string AdapterId = "paper";

    /// <summary>The starting balance, clamped to something usable. A zero or negative opening balance
    /// produces a book that rejects its first order for insufficient funds, which reads as a broken
    /// terminal rather than as a configuration mistake.</summary>
    public decimal EffectiveStartingBalance =>
        StartingBalance > 0m ? StartingBalance : 100_000m;

    /// <summary>The currency, or USD when the configured one is blank or not a currency code.</summary>
    public string EffectiveCurrency =>
        !string.IsNullOrWhiteSpace(Currency) && Currency.Trim().Length == 3
            ? Currency.Trim().ToUpperInvariant()
            : "USD";
}
