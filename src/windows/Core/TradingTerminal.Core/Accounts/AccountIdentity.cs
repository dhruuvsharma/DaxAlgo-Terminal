namespace TradingTerminal.Core.Accounts;

/// <summary>
/// Product-owned account identity. <see cref="AccountId"/> is a stable, opaque identifier issued
/// by the product account service; it is not a broker account number or an authentication-provider
/// subject. No access token or other credential belongs in this record.
/// </summary>
public sealed record AccountIdentity
{
    public AccountIdentity(string accountId, string? displayName = null, string? emailAddress = null)
    {
        AccountId = AccountContractGuards.NormalizeRequired(accountId, nameof(accountId));
        DisplayName = AccountContractGuards.NormalizeOptional(displayName);
        EmailAddress = AccountContractGuards.NormalizeOptional(emailAddress);
    }

    public string AccountId { get; }

    public string? DisplayName { get; }

    public string? EmailAddress { get; }
}

/// <summary>
/// Immutable view of an authenticated product session. The session reference is opaque and is not
/// a bearer credential; implementations retain provider tokens outside Core.
/// </summary>
public sealed record AccountSessionSnapshot
{
    public AccountSessionSnapshot(
        string sessionId,
        AccountIdentity account,
        DateTimeOffset authenticatedAtUtc,
        DateTimeOffset? expiresAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(account);

        var authenticated = AccountContractGuards.AsUtc(authenticatedAtUtc);
        var expires = expiresAtUtc is { } value
            ? AccountContractGuards.AsUtc(value)
            : (DateTimeOffset?)null;

        if (expires is { } end && end <= authenticated)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expiresAtUtc),
                expiresAtUtc,
                "Session expiry must be later than authentication time.");
        }

        SessionId = AccountContractGuards.NormalizeRequired(sessionId, nameof(sessionId));
        Account = account;
        AuthenticatedAtUtc = authenticated;
        ExpiresAtUtc = expires;
    }

    public string SessionId { get; }

    public AccountIdentity Account { get; }

    public DateTimeOffset AuthenticatedAtUtc { get; }

    /// <summary>The exclusive session-expiry instant, or <see langword="null"/> when unspecified.</summary>
    public DateTimeOffset? ExpiresAtUtc { get; }

    /// <summary>
    /// Returns whether the snapshot is active at an explicit instant. The start is inclusive and
    /// the optional expiry is exclusive, making boundary behavior independent of a system clock.
    /// </summary>
    public bool IsActiveAt(DateTimeOffset currentUtc)
    {
        var current = AccountContractGuards.AsUtc(currentUtc);
        return current >= AuthenticatedAtUtc &&
               (ExpiresAtUtc is not { } expires || current < expires);
    }
}
