using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Core.Accounts;

/// <summary>
/// Provider-neutral condition of a normalized subscription entitlement. Cancellation at a billing
/// provider need not appear here while the paid-through entitlement remains active.
/// </summary>
public enum SubscriptionEntitlementState
{
    Active,
    Suspended,
    Revoked,
}

/// <summary>
/// Product subscription claim granting an account an ordered <see cref="AppEdition"/>. Provider
/// adapters translate their own plans and billing states into this small normalized contract.
/// </summary>
public sealed record SubscriptionEntitlement
{
    public SubscriptionEntitlement(
        string accountId,
        AppEdition edition,
        SubscriptionEntitlementState state,
        DateTimeOffset validFromUtc,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? graceEndsAtUtc = null,
        string? subscriptionReference = null)
    {
        AccountContractGuards.ValidateEdition(edition, nameof(edition));

        if (!Enum.IsDefined(typeof(SubscriptionEntitlementState), state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown entitlement state.");

        var validFrom = AccountContractGuards.AsUtc(validFromUtc);
        var expires = expiresAtUtc is { } expiry
            ? AccountContractGuards.AsUtc(expiry)
            : (DateTimeOffset?)null;
        var graceEnds = graceEndsAtUtc is { } graceEnd
            ? AccountContractGuards.AsUtc(graceEnd)
            : (DateTimeOffset?)null;

        if (expires is { } end && end <= validFrom)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                expiresAtUtc,
                "Entitlement expiry must be later than its validity start.");
        }

        if (graceEnds is not null && expires is null)
        {
            throw new ArgumentException(
                "A grace period requires an entitlement expiry.",
                nameof(graceEndsAtUtc));
        }

        if (graceEnds is { } grace && expires is { } entitlementEnd && grace <= entitlementEnd)
        {
            throw new ArgumentOutOfRangeException(
                nameof(graceEndsAtUtc),
                graceEndsAtUtc,
                "Grace-period end must be later than entitlement expiry.");
        }

        AccountId = AccountContractGuards.NormalizeRequired(accountId, nameof(accountId));
        Edition = edition;
        State = state;
        ValidFromUtc = validFrom;
        ExpiresAtUtc = expires;
        GraceEndsAtUtc = graceEnds;
        SubscriptionReference = AccountContractGuards.NormalizeOptional(subscriptionReference);
    }

    public string AccountId { get; }

    public AppEdition Edition { get; }

    public SubscriptionEntitlementState State { get; }

    /// <summary>Inclusive start of the entitlement window.</summary>
    public DateTimeOffset ValidFromUtc { get; }

    /// <summary>Exclusive normal-access expiry, or <see langword="null"/> for no scheduled expiry.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>Exclusive grace-period end. It is valid only when <see cref="ExpiresAtUtc"/> exists.</summary>
    public DateTimeOffset? GraceEndsAtUtc { get; }

    /// <summary>
    /// Optional opaque product subscription reference for diagnostics and refresh correlation.
    /// It carries no provider-specific meaning in Core.
    /// </summary>
    public string? SubscriptionReference { get; }
}
