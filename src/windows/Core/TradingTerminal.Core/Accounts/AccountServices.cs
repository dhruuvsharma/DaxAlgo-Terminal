namespace TradingTerminal.Core.Accounts;

/// <summary>
/// Provider-neutral interactive account authentication and local session lifecycle. Implementations
/// retain provider SDK objects and credentials behind this boundary.
/// </summary>
public interface IAccountAuthenticationService
{
    Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default);

    Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default);

    Task SignOutAsync(CancellationToken ct = default);
}

/// <summary>Retrieves the normalized product entitlement for an authenticated session.</summary>
public interface IEntitlementService
{
    Task<SubscriptionEntitlement?> GetEntitlementAsync(
        AccountSessionSnapshot session,
        CancellationToken ct = default);
}
