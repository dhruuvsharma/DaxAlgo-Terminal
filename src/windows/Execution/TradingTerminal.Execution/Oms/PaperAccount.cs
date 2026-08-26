using TradingTerminal.Core.Execution;

namespace TradingTerminal.Execution.Oms;

/// <summary>
/// The terminal's paper account, as the execution engine sees it.
///
/// <para>Turns <see cref="PaperAccountOptions"/> into the identity and the opening balance a
/// simulated adapter is built from. One account, not one per broker — the whole point of moving
/// paper trading inside the terminal is that a user should not have to open, fund and keep straight a
/// separate paper account at every venue they want to try a strategy against.</para>
/// </summary>
public static class PaperAccount
{
    /// <summary>The account every paper book trades under.</summary>
    public static BrokerExecutionAccount Identity { get; } = new(
        new ExecutionAdapterId(PaperAccountOptions.AdapterId),
        new BrokerAccountId(PaperAccountOptions.AccountId));

    /// <summary>
    /// The opening cash the paper book is created with.
    ///
    /// <para>Replaces what the simulated adapter defaulted to — zero, in a currency called
    /// <c>SIM</c> — which is why an in-process book could accept an order and then have nothing to
    /// settle it with.</para>
    /// </summary>
    /// <param name="options">Where the balance and currency come from.</param>
    /// <param name="observedAtUtc">The snapshot's observation time.</param>
    public static BrokerCashSnapshot OpeningCash(PaperAccountOptions options, DateTime observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(options);

        var amount = Money(options.EffectiveStartingBalance);

        // Total and available are the same at open: nothing is committed to a working order yet.
        return new BrokerCashSnapshot(options.EffectiveCurrency, amount, amount, observedAtUtc);
    }

    /// <summary>
    /// The reconciliation basis for the paper account.
    ///
    /// <para><b>It must match <see cref="OpeningCash"/> exactly.</b> The reconciler compares what the
    /// adapter reports against what the ledger implies, and the opening balance is the ledger's half of
    /// that sum. Seeding the adapter with a hundred thousand while leaving the engine on the old zero
    /// basis makes every paper book open with a reconciliation break reading "adapter subject is absent
    /// from the local ledger" — which is the engine correctly noticing two different claims about the
    /// same money. That is exactly the failure it exists to catch, and it caught this one.</para>
    ///
    /// <para><c>CompareAvailable</c> is false, matching the broker path. Available cash was compared
    /// first, on the reasoning that a simulated venue extends no margin so the figure must be exactly
    /// derivable from fills — that reasoning was wrong, and the guarded-intake replication test said so
    /// by never converging. A working order reserves against available before it fills, so the two
    /// sides disagree for as long as anything is live, and every such moment opened a case that paused
    /// the intake. Total is the figure that must reconcile; available is a working number.</para>
    /// </summary>
    public static ReconciliationCashBasis CashBasis(PaperAccountOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var amount = Money(options.EffectiveStartingBalance);
        return new ReconciliationCashBasis(
            options.EffectiveCurrency, amount, amount, CompareAvailable: false);
    }

    /// <summary>
    /// A decimal balance as the engine's exact money type.
    ///
    /// <para>Two decimal places, chosen rather than inherited. The engine's money is a
    /// coefficient-and-scale pair precisely so that cash never travels as a binary float, and a
    /// starting balance is the one figure in the system a human types — so it is rounded once, here,
    /// to the precision cash actually has, rather than carrying whatever a config file happened to
    /// contain into every later arithmetic.</para>
    /// </summary>
    public static ScaledMoney Money(decimal amount)
    {
        const byte scale = 2;
        var rounded = Math.Round(amount, scale, MidpointRounding.ToZero);
        return new ScaledMoney((long)(rounded * 100m), scale);
    }
}
