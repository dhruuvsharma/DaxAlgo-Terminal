namespace TradingTerminal.Execution.Oms;

/// <summary>Stable user or strategy decision identity from roadmap section 6.2.</summary>
public readonly record struct IntentId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Optional parent release-group identity from roadmap section 6.2.</summary>
public readonly record struct BucketId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>One venue allocation identity from roadmap section 6.2.</summary>
public readonly record struct LegId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Stable native-order idempotency key from roadmap section 6.2.</summary>
public readonly record struct ClientOrderId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Optional venue-assigned order identity from roadmap section 6.2.</summary>
public readonly record struct BrokerOrderId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Optional exchange-assigned order identity from roadmap section 6.2.</summary>
public readonly record struct ExchangeOrderId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>End-to-end trace identity from roadmap section 6.2.</summary>
public readonly record struct CorrelationId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identity of the command or fact that caused a transition, per roadmap section 6.2.</summary>
public readonly record struct CausationId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>In-process execution-resource lease identity from ADR D7.</summary>
public readonly record struct ExecutionLeaseId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Monotonically increasing execution-resource fence carried with an instruction.</summary>
public readonly record struct FencingToken(long Value)
{
    /// <summary>Gets whether the token can represent an admitted lease generation.</summary>
    public bool IsValid => Value > 0;

    /// <summary>Gets whether this token supersedes an older generation for the same lease.</summary>
    public bool IsNewerThan(FencingToken older) => IsValid && Value > older.Value;
}

/// <summary>Stable inbox identity used to deduplicate at-least-once commands and venue callbacks.</summary>
public readonly record struct DeduplicationKey(string Value)
{
    /// <summary>Gets whether the key is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <summary>Creates a deterministic child key for one fact caused by a larger command.</summary>
    public DeduplicationKey Derive(string suffix) => new($"{Value}:{suffix}");

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Identity of a durable reconciliation case, whose live implementation is deferred.</summary>
public readonly record struct ReconciliationCaseId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>All strongly typed identities carried by one economic instruction.</summary>
public readonly record struct OrderIdentity(
    IntentId IntentId,
    BucketId? BucketId,
    LegId LegId,
    ClientOrderId ClientOrderId,
    BrokerOrderId? BrokerOrderId,
    ExchangeOrderId? ExchangeOrderId,
    CorrelationId CorrelationId,
    CausationId CausationId,
    ExecutionLeaseId ExecutionLeaseId,
    FencingToken FencingToken)
{
    /// <summary>Gets whether all required identities and optional external identities are valid.</summary>
    public bool IsValid =>
        IntentId.IsValid &&
        (!BucketId.HasValue || BucketId.Value.IsValid) &&
        LegId.IsValid &&
        ClientOrderId.IsValid &&
        (!BrokerOrderId.HasValue || BrokerOrderId.Value.IsValid) &&
        (!ExchangeOrderId.HasValue || ExchangeOrderId.Value.IsValid) &&
        CorrelationId.IsValid &&
        CausationId.IsValid &&
        ExecutionLeaseId.IsValid &&
        FencingToken.IsValid;
}

internal static class IdentityValue
{
    internal static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 256;
}
