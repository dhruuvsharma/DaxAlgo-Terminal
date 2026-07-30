using System.Text.Json.Serialization;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Core.Accounts;

/// <summary>
/// Versioned transport contract emitted by the product platform for one device-bound offline
/// entitlement lease. Only the authenticated <see cref="EncodedPayload"/> is authoritative;
/// duplicated typed fields are pre-validation hints and cannot grant access.
/// </summary>
public sealed record EntitlementLeaseWireDto
{
    public const int CurrentSchemaVersion = 1;

    public static readonly TimeSpan MaximumLeaseDuration = TimeSpan.FromDays(7);

    public EntitlementLeaseWireDto(
        int schemaVersion,
        string leaseId,
        SubscriptionEntitlementState state,
        AppEdition edition,
        string productAccountId,
        Guid deviceId,
        string issuer,
        string audience,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset notBeforeUtc,
        DateTimeOffset expiresAtUtc,
        string keyId,
        string algorithm,
        string encodedPayload,
        string encodedSignature)
    {
        if (schemaVersion != CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Unsupported entitlement lease schema version.");
        }

        if (!Enum.IsDefined(typeof(SubscriptionEntitlementState), state))
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown entitlement state.");

        AccountContractGuards.ValidateEdition(edition, nameof(edition));
        if (deviceId == Guid.Empty)
            throw new ArgumentException("Device ID cannot be empty.", nameof(deviceId));

        var issued = AccountContractGuards.AsUtc(issuedAtUtc);
        var notBefore = AccountContractGuards.AsUtc(notBeforeUtc);
        var expires = AccountContractGuards.AsUtc(expiresAtUtc);
        if (notBefore < issued || expires <= notBefore)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                expiresAtUtc,
                "Lease times must satisfy issued <= not-before < expires.");
        }

        if (expires - issued > MaximumLeaseDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                expiresAtUtc,
                "Entitlement leases cannot exceed seven days.");
        }

        SchemaVersion = schemaVersion;
        LeaseId = AccountContractGuards.NormalizeRequired(leaseId, nameof(leaseId));
        State = state;
        Edition = edition;
        ProductAccountId = AccountContractGuards.NormalizeRequired(
            productAccountId,
            nameof(productAccountId));
        DeviceId = deviceId;
        Issuer = AccountContractGuards.NormalizeRequired(issuer, nameof(issuer));
        Audience = AccountContractGuards.NormalizeRequired(audience, nameof(audience));
        IssuedAtUtc = issued;
        NotBeforeUtc = notBefore;
        ExpiresAtUtc = expires;
        KeyId = AccountContractGuards.NormalizeRequired(keyId, nameof(keyId));
        Algorithm = AccountContractGuards.NormalizeRequired(algorithm, nameof(algorithm));
        EncodedPayload = AccountContractGuards.RequireOpaque(encodedPayload, nameof(encodedPayload));
        EncodedSignature = AccountContractGuards.RequireOpaque(encodedSignature, nameof(encodedSignature));
    }

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; }

    [JsonPropertyName("leaseId")]
    public string LeaseId { get; }

    [JsonPropertyName("state")]
    [JsonConverter(typeof(JsonStringEnumConverter<SubscriptionEntitlementState>))]
    public SubscriptionEntitlementState State { get; }

    [JsonPropertyName("edition")]
    [JsonConverter(typeof(JsonStringEnumConverter<AppEdition>))]
    public AppEdition Edition { get; }

    [JsonPropertyName("productAccountId")]
    public string ProductAccountId { get; }

    [JsonPropertyName("deviceId")]
    public Guid DeviceId { get; }

    [JsonPropertyName("issuer")]
    public string Issuer { get; }

    [JsonPropertyName("audience")]
    public string Audience { get; }

    [JsonPropertyName("issuedAtUtc")]
    public DateTimeOffset IssuedAtUtc { get; }

    [JsonPropertyName("notBeforeUtc")]
    public DateTimeOffset NotBeforeUtc { get; }

    [JsonPropertyName("expiresAtUtc")]
    public DateTimeOffset ExpiresAtUtc { get; }

    [JsonPropertyName("keyId")]
    public string KeyId { get; }

    [JsonPropertyName("algorithm")]
    public string Algorithm { get; }

    [JsonPropertyName("encodedPayload")]
    public string EncodedPayload { get; }

    [JsonPropertyName("encodedSignature")]
    public string EncodedSignature { get; }

    public bool IsBoundTo(string productAccountId, Guid deviceId) =>
        string.Equals(
            ProductAccountId,
            AccountContractGuards.NormalizeRequired(productAccountId, nameof(productAccountId)),
            StringComparison.Ordinal)
        && DeviceId == deviceId;

    public SignedOfflineLeaseEnvelope ToSignedEnvelope() =>
        new(EncodedPayload, EncodedSignature, KeyId, Algorithm);
}
