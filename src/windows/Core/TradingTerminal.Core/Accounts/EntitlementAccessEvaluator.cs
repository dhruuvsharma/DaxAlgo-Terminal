using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Core.Accounts;

/// <summary>Stable, provider-neutral explanation for an edition-access decision.</summary>
public enum EntitlementAccessReason
{
    Granted,
    GrantedDuringGracePeriod,
    EntitlementMissing,
    AccountMismatch,
    EntitlementNotYetValid,
    EntitlementExpired,
    EntitlementSuspended,
    EntitlementRevoked,
    EditionInsufficient,
    OfflineLeaseInvalid,
    OfflineLeaseNotYetValid,
    OfflineLeaseExpired,
    OfflineLeaseDeviceMismatch,
}

/// <summary>All non-environmental inputs needed for a deterministic access decision.</summary>
public sealed record EntitlementAccessRequest
{
    public EntitlementAccessRequest(
        string accountId,
        AppEdition requiredEdition,
        DateTimeOffset currentUtc)
    {
        AccountContractGuards.ValidateEdition(requiredEdition, nameof(requiredEdition));
        AccountId = AccountContractGuards.NormalizeRequired(accountId, nameof(accountId));
        RequiredEdition = requiredEdition;
        CurrentUtc = AccountContractGuards.AsUtc(currentUtc);
    }

    public string AccountId { get; }

    public AppEdition RequiredEdition { get; }

    public DateTimeOffset CurrentUtc { get; }
}

/// <summary>Immutable result of evaluating one required edition at one explicit instant.</summary>
public sealed record EntitlementAccessDecision
{
    internal EntitlementAccessDecision(
        bool isGranted,
        AppEdition? grantedEdition,
        EntitlementAccessReason reason,
        EntitlementAccessRequest request)
    {
        IsGranted = isGranted;
        GrantedEdition = grantedEdition;
        Reason = reason;
        RequiredEdition = request.RequiredEdition;
        EvaluatedAtUtc = request.CurrentUtc;
    }

    public bool IsGranted { get; }

    /// <summary>
    /// The currently recognized edition. It is present for a time-valid entitlement, including an
    /// insufficient lower edition, and absent when no edition can safely be granted.
    /// </summary>
    public AppEdition? GrantedEdition { get; }

    public EntitlementAccessReason Reason { get; }

    public AppEdition RequiredEdition { get; }

    public DateTimeOffset EvaluatedAtUtc { get; }
}

/// <summary>
/// Pure access policy for online subscription snapshots and authenticated offline leases. Callers
/// provide the current time explicitly; expiry instants and grace-period ends are exclusive.
/// </summary>
public static class EntitlementAccessEvaluator
{
    public static EntitlementAccessDecision Evaluate(
        EntitlementAccessRequest request,
        SubscriptionEntitlement? entitlement)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (entitlement is null)
            return Denied(request, EntitlementAccessReason.EntitlementMissing);

        if (!string.Equals(request.AccountId, entitlement.AccountId, StringComparison.Ordinal))
            return Denied(request, EntitlementAccessReason.AccountMismatch);

        switch (entitlement.State)
        {
            case SubscriptionEntitlementState.Revoked:
                return Denied(request, EntitlementAccessReason.EntitlementRevoked);
            case SubscriptionEntitlementState.Suspended:
                return Denied(request, EntitlementAccessReason.EntitlementSuspended);
            case SubscriptionEntitlementState.Active:
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(entitlement),
                    entitlement.State,
                    "Unknown entitlement state.");
        }

        if (request.CurrentUtc < entitlement.ValidFromUtc)
            return Denied(request, EntitlementAccessReason.EntitlementNotYetValid);

        var inGracePeriod = false;
        if (entitlement.ExpiresAtUtc is { } expires && request.CurrentUtc >= expires)
        {
            if (entitlement.GraceEndsAtUtc is not { } graceEnd || request.CurrentUtc >= graceEnd)
                return Denied(request, EntitlementAccessReason.EntitlementExpired);

            inGracePeriod = true;
        }

        if ((int)entitlement.Edition < (int)request.RequiredEdition)
        {
            return Denied(
                request,
                EntitlementAccessReason.EditionInsufficient,
                entitlement.Edition);
        }

        return new EntitlementAccessDecision(
            true,
            entitlement.Edition,
            inGracePeriod
                ? EntitlementAccessReason.GrantedDuringGracePeriod
                : EntitlementAccessReason.Granted,
            request);
    }

    public static EntitlementAccessDecision EvaluateOffline(
        EntitlementAccessRequest request,
        string expectedDeviceId,
        OfflineLeaseValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(validation);
        var deviceId = AccountContractGuards.NormalizeRequired(
            expectedDeviceId,
            nameof(expectedDeviceId));

        if (!validation.IsValid || validation.Lease is not { } lease)
            return Denied(request, EntitlementAccessReason.OfflineLeaseInvalid);

        if (!string.Equals(request.AccountId, lease.Entitlement.AccountId, StringComparison.Ordinal))
            return Denied(request, EntitlementAccessReason.AccountMismatch);

        if (!string.Equals(deviceId, lease.DeviceId, StringComparison.Ordinal))
            return Denied(request, EntitlementAccessReason.OfflineLeaseDeviceMismatch);

        if (request.CurrentUtc < lease.NotBeforeUtc)
            return Denied(request, EntitlementAccessReason.OfflineLeaseNotYetValid);

        if (request.CurrentUtc >= lease.ExpiresAtUtc)
            return Denied(request, EntitlementAccessReason.OfflineLeaseExpired);

        return Evaluate(request, lease.Entitlement);
    }

    private static EntitlementAccessDecision Denied(
        EntitlementAccessRequest request,
        EntitlementAccessReason reason,
        AppEdition? recognizedEdition = null) =>
        new(false, recognizedEdition, reason, request);
}
