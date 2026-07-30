using System.Net.Http;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Accounts;

internal enum AccountGateProviderMode
{
    Production,
    Development,
    Unavailable,
}

internal sealed record AccountGateServices(
    IAccountAuthenticationService Authentication,
    IEntitlementService Entitlements,
    AccountGateProviderMode Mode,
    bool HasGoogleAuthentication);

internal static class AccountGateServiceFactory
{
    private static readonly HttpClient GoogleHttpClient = new();
    private static readonly HttpClient PlatformHttpClient = new();

#if DEBUG
    private const bool CurrentBuildIsDebug = true;
#else
    private const bool CurrentBuildIsDebug = false;
#endif

    public static AccountGateServices Create(
        string environmentName,
        IAccountAuthenticationService? authentication = null,
        IEntitlementService? entitlements = null,
        TimeProvider? timeProvider = null,
        bool? isDebugBuild = null,
        GoogleAuthOptions? googleAuthOptions = null,
        IGoogleIdentityProvider? googleIdentityProvider = null,
        IDevelopmentAccountSessionStore? accountSessionStore = null,
        bool forceFreshAuthentication = false,
        PlatformOptions? platformOptions = null,
        HttpClient? platformHttpClient = null,
        IDeviceIdentityProvider? deviceIdentityProvider = null,
        IOfflineLeaseValidator? offlineLeaseValidator = null)
    {
        if (authentication is not null && entitlements is not null)
        {
            return new AccountGateServices(
                authentication,
                entitlements,
                AccountGateProviderMode.Production,
                false);
        }

        if (authentication is not null || entitlements is not null)
        {
            return Unavailable(
                "Product account configuration is incomplete. Access remains locked until both " +
                "authentication and entitlement services are configured.");
        }

        if (DevelopmentAccountProviderPolicy.CanUseLocalAdapter(
                environmentName,
                isDebugBuild ?? CurrentBuildIsDebug))
        {
            var clock = timeProvider ?? TimeProvider.System;
            var googleAuthentication = googleIdentityProvider;
            if (googleAuthentication is null &&
                !string.IsNullOrWhiteSpace(googleAuthOptions?.ClientId))
            {
                googleAuthentication = new GoogleOAuthClient(
                    googleAuthOptions,
                    GoogleHttpClient,
                    clock);
            }

            return new AccountGateServices(
                new DevelopmentAccountAuthenticationService(
                    clock,
                    accountSessionStore ?? DevelopmentAccountSessionStore.CreateDefault(),
                    googleAuthentication,
                    forceFreshAuthentication),
                new DevelopmentEntitlementService(),
                AccountGateProviderMode.Development,
                googleAuthentication is not null);
        }

        if (platformOptions is not null &&
            PlatformEndpoint.TryCreateBaseUri(platformOptions.BaseUrl, out var platformBaseUri) &&
            platformBaseUri is not null &&
            platformOptions.TimeoutSeconds is > 0 and <= 300)
        {
            var clock = timeProvider ?? TimeProvider.System;
            var googleAuthentication = googleIdentityProvider;
            if (googleAuthentication is null &&
                !string.IsNullOrWhiteSpace(googleAuthOptions?.ClientId))
            {
                googleAuthentication = new GoogleOAuthClient(
                    googleAuthOptions,
                    GoogleHttpClient,
                    clock);
            }

            if (googleAuthentication is not null)
            {
                var productionAuthentication = new GoogleAccountAuthenticationService(
                    googleAuthentication,
                    clock);
                var validator = offlineLeaseValidator ?? new Es256OfflineLeaseValidator(
                    platformOptions.EntitlementLeasePublicKey);
                return new AccountGateServices(
                    productionAuthentication,
                    new PlatformEntitlementService(
                        platformHttpClient ?? PlatformHttpClient,
                        platformBaseUri,
                        productionAuthentication,
                        productionAuthentication,
                        deviceIdentityProvider ?? PersistedDeviceIdentityProvider.CreateDefault(),
                        validator,
                        clock,
                        TimeSpan.FromSeconds(platformOptions.TimeoutSeconds)),
                    AccountGateProviderMode.Production,
                    true);
            }
        }

        return Unavailable(
            "Product account sign-in is not configured in this build. Access remains locked; " +
            "configure the production identity and entitlement provider before distribution.");
    }

    private static AccountGateServices Unavailable(string message) => new(
        new UnavailableAccountAuthenticationService(message),
        new UnavailableEntitlementService(),
        AccountGateProviderMode.Unavailable,
        false);
}

internal static class DevelopmentAccountProviderPolicy
{
    private static readonly HashSet<string> AllowedEnvironments = new(StringComparer.OrdinalIgnoreCase)
    {
        "Development",
        "DevLogin",
        "DevSimLogin",
        "DevNewUser",
        "DevNoStrategies",
        "DevSim",
        "DevReplay",
        "DevLive",
    };

    public static bool CanUseLocalAdapter(string environmentName, bool isDebugBuild) =>
        isDebugBuild && AllowedEnvironments.Contains(environmentName);
}

internal interface IDevelopmentAccountAuthenticationService : IAccountAuthenticationService
{
    Task<AccountSessionSnapshot> AuthenticateLocallyAsync(CancellationToken ct = default);
}

internal sealed class GoogleAccountAuthenticationService(
    IGoogleIdentityProvider googleIdentityProvider,
    TimeProvider timeProvider)
    : IAccountAuthenticationService,
      IGoogleIdTokenSource,
      IPlatformAccountIdentityBinder,
      ICanonicalAccountSessionSource
{
    private AccountSessionSnapshot? _session;
    private string? _idToken;

    public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var session = _session;
        if (session is not null &&
            _idToken is not null &&
            session.IsActiveAt(timeProvider.GetUtcNow()))
        {
            return Task.FromResult<AccountSessionSnapshot?>(session);
        }

        _session = null;
        _idToken = null;
        return Task.FromResult<AccountSessionSnapshot?>(null);
    }

    public async Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
    {
        var identity = await googleIdentityProvider.AuthenticateAsync(ct);
        var now = timeProvider.GetUtcNow();
        if (identity.TokenExpiresAtUtc <= now || string.IsNullOrWhiteSpace(identity.IdToken))
        {
            throw new AccountProviderUnavailableException(
                "Google sign-in did not return an active identity token. Retry.");
        }

        _session = new AccountSessionSnapshot(
            "google-" + Guid.NewGuid().ToString("N"),
            new AccountIdentity(
                "google:" + identity.Subject,
                identity.DisplayName,
                identity.EmailAddress),
            now,
            identity.TokenExpiresAtUtc);
        _idToken = identity.IdToken;
        return _session;
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _session = null;
        _idToken = null;
        return Task.CompletedTask;
    }

    public string? GetIdToken(AccountSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _session is not null &&
               string.Equals(_session.SessionId, session.SessionId, StringComparison.Ordinal) &&
               session.IsActiveAt(timeProvider.GetUtcNow())
            ? _idToken
            : null;
    }

    public bool TryBindPlatformAccount(
        AccountSessionSnapshot session,
        string productAccountId)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (_session is null ||
            _idToken is null ||
            !string.Equals(_session.SessionId, session.SessionId, StringComparison.Ordinal) ||
            !session.IsActiveAt(timeProvider.GetUtcNow()))
        {
            return false;
        }

        _session = new AccountSessionSnapshot(
            session.SessionId,
            new AccountIdentity(
                productAccountId,
                session.Account.DisplayName,
                session.Account.EmailAddress),
            session.AuthenticatedAtUtc,
            session.ExpiresAtUtc);
        return true;
    }

    public AccountSessionSnapshot GetCanonicalSession(AccountSessionSnapshot session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return _session is not null &&
               string.Equals(_session.SessionId, session.SessionId, StringComparison.Ordinal)
            ? _session
            : session;
    }
}

internal sealed class DevelopmentAccountAuthenticationService
    : IDevelopmentAccountAuthenticationService
{
    private readonly TimeProvider _timeProvider;
    private readonly IDevelopmentAccountSessionStore _sessionStore;
    private readonly IGoogleIdentityProvider? _googleIdentityProvider;
    private bool _skipStoredSession;
    private bool _storedSessionRead;
    private AccountSessionSnapshot? _session;

    public DevelopmentAccountAuthenticationService(TimeProvider timeProvider)
        : this(
            timeProvider,
            DevelopmentAccountSessionStore.CreateDefault(),
            null,
            false)
    {
    }

    public DevelopmentAccountAuthenticationService(
        TimeProvider timeProvider,
        IDevelopmentAccountSessionStore sessionStore,
        IGoogleIdentityProvider? googleIdentityProvider,
        bool skipStoredSession)
    {
        _timeProvider = timeProvider;
        _sessionStore = sessionStore;
        _googleIdentityProvider = googleIdentityProvider;
        _skipStoredSession = skipStoredSession;
    }

    public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_session is not null) return Task.FromResult<AccountSessionSnapshot?>(_session);
        if (_storedSessionRead) return Task.FromResult<AccountSessionSnapshot?>(null);

        _storedSessionRead = true;
        if (_skipStoredSession)
        {
            _skipStoredSession = false;
            return Task.FromResult<AccountSessionSnapshot?>(null);
        }

        var stored = _sessionStore.Load();
        if (stored is not null && stored.IsActiveAt(_timeProvider.GetUtcNow()))
        {
            _session = stored;
            return Task.FromResult<AccountSessionSnapshot?>(_session);
        }

        if (stored is not null) _sessionStore.Clear();
        return Task.FromResult<AccountSessionSnapshot?>(null);
    }

    public async Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_googleIdentityProvider is null)
            return await AuthenticateLocallyAsync(ct);

        var identity = await _googleIdentityProvider.AuthenticateAsync(ct);
        var now = _timeProvider.GetUtcNow();
        _session = new AccountSessionSnapshot(
            "google-development-" + Guid.NewGuid().ToString("N"),
            new AccountIdentity(
                "google:" + identity.Subject,
                identity.DisplayName,
                identity.EmailAddress),
            now,
            now.AddHours(8));
        _storedSessionRead = true;
        _sessionStore.Save(_session);
        return _session;
    }

    public Task<AccountSessionSnapshot> AuthenticateLocallyAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var now = _timeProvider.GetUtcNow();
        _session = new AccountSessionSnapshot(
            "development-session",
            new AccountIdentity("development-account", "Local developer", "developer@localhost"),
            now,
            now.AddHours(8));
        _storedSessionRead = true;
        return Task.FromResult(_session);
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _session = null;
        _storedSessionRead = true;
        _sessionStore.Clear();
        return Task.CompletedTask;
    }
}

internal sealed class DevelopmentEntitlementService : IEntitlementService
{
    public Task<SubscriptionEntitlement?> GetEntitlementAsync(
        AccountSessionSnapshot session,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubscriptionEntitlement entitlement = new(
            session.Account.AccountId,
            AppEdition.Professional,
            SubscriptionEntitlementState.Active,
            session.AuthenticatedAtUtc,
            session.ExpiresAtUtc);
        return Task.FromResult<SubscriptionEntitlement?>(entitlement);
    }
}

internal sealed class UnavailableAccountAuthenticationService(string message)
    : IAccountAuthenticationService
{
    public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<AccountSessionSnapshot?>(null);
    }

    public Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromException<AccountSessionSnapshot>(new AccountProviderUnavailableException(message));
    }

    public Task SignOutAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

internal sealed class UnavailableEntitlementService : IEntitlementService
{
    public Task<SubscriptionEntitlement?> GetEntitlementAsync(
        AccountSessionSnapshot session,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<SubscriptionEntitlement?>(null);
    }
}
