namespace TradingTerminal.Core.Accounts;

/// <summary>
/// Typed claims recovered only after an offline envelope has been authenticated. The lease window
/// caps use of the embedded subscription entitlement even when that entitlement lasts longer.
/// </summary>
public sealed record OfflineEntitlementLease
{
    public static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromDays(7);

    public OfflineEntitlementLease(
        string leaseId,
        SubscriptionEntitlement entitlement,
        string deviceId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(entitlement);

        var issued = AccountContractGuards.AsUtc(issuedAtUtc);
        var notBefore = AccountContractGuards.AsUtc(notBeforeUtc);
        var expires = AccountContractGuards.AsUtc(expiresAtUtc);
        if (notBefore < issued || expires <= notBefore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                expiresAtUtc,
                "Offline lease times must satisfy issued <= not-before < expires.");
        }

        if (expires - issued > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                expiresAtUtc,
                "Offline lease lifetime cannot exceed seven days.");
        }

        LeaseId = AccountContractGuards.NormalizeRequired(leaseId, nameof(leaseId));
        Entitlement = entitlement;
        DeviceId = AccountContractGuards.NormalizeRequired(deviceId, nameof(deviceId));
        IssuedAtUtc = issued;
        NotBeforeUtc = notBefore;
        ExpiresAtUtc = expires;
    }

    public string LeaseId { get; }

    public SubscriptionEntitlement Entitlement { get; }

    /// <summary>Device installation identifier authenticated as part of the signed payload.</summary>
    public string DeviceId { get; }

    /// <summary>Time at which the platform issued the signed lease.</summary>
    public DateTimeOffset IssuedAtUtc { get; }

    /// <summary>Inclusive start of the signed lease validity window.</summary>
    public DateTimeOffset NotBeforeUtc { get; }

    /// <summary>Exclusive end of the signed lease window.</summary>
    public DateTimeOffset ExpiresAtUtc { get; }
}

/// <summary>
/// Transport-safe signed lease. Payload and signature encodings are deliberately opaque so Core
/// does not select a serialization format, signing algorithm, key store, or crypto package.
/// </summary>
public sealed record SignedOfflineLeaseEnvelope
{
    public SignedOfflineLeaseEnvelope(
        string encodedPayload,
        string encodedSignature,
        string keyId,
        string algorithm)
    {
        EncodedPayload = AccountContractGuards.RequireOpaque(encodedPayload, nameof(encodedPayload));
        EncodedSignature = AccountContractGuards.RequireOpaque(encodedSignature, nameof(encodedSignature));
        KeyId = AccountContractGuards.RequireOpaque(keyId, nameof(keyId));
        Algorithm = AccountContractGuards.RequireOpaque(algorithm, nameof(algorithm));
    }

    public string EncodedPayload { get; }

    public string EncodedSignature { get; }

    public string KeyId { get; }

    public string Algorithm { get; }
}

/// <summary>Provider-neutral reason an offline envelope could not yield trusted lease claims.</summary>
public enum OfflineLeaseValidationFailure
{
    None,
    MalformedEnvelope,
    UnsupportedAlgorithm,
    UnknownSigningKey,
    InvalidSignature,
    InvalidPayload,
    Revoked,
}

/// <summary>Result returned by an external signature-and-payload validator.</summary>
public sealed record OfflineLeaseValidationResult
{
    private OfflineLeaseValidationResult(
        OfflineEntitlementLease? lease,
        OfflineLeaseValidationFailure failure)
    {
        Lease = lease;
        Failure = failure;
    }

    public bool IsValid => Failure == OfflineLeaseValidationFailure.None && Lease is not null;

    public OfflineEntitlementLease? Lease { get; }

    public OfflineLeaseValidationFailure Failure { get; }

    public static OfflineLeaseValidationResult Valid(OfflineEntitlementLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new OfflineLeaseValidationResult(lease, OfflineLeaseValidationFailure.None);
    }

    public static OfflineLeaseValidationResult Invalid(OfflineLeaseValidationFailure failure)
    {
        if (!Enum.IsDefined(typeof(OfflineLeaseValidationFailure), failure) ||
            failure == OfflineLeaseValidationFailure.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failure),
                failure,
                "An invalid lease result requires a concrete validation failure.");
        }

        return new OfflineLeaseValidationResult(null, failure);
    }
}

/// <summary>
/// Validation seam implemented outside Core. An implementation authenticates the exact opaque
/// payload, decodes it, and returns typed claims; Core supplies no cryptographic implementation.
/// Time-based access remains the responsibility of <see cref="EntitlementAccessEvaluator"/>.
/// </summary>
public interface IOfflineLeaseValidator
{
    Task<OfflineLeaseValidationResult> ValidateAsync(
        SignedOfflineLeaseEnvelope envelope,
        CancellationToken ct = default);
}
