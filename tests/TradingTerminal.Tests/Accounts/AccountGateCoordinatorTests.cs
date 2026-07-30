using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class AccountGateCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Valid_current_session_and_sufficient_entitlement_are_granted()
    {
        var session = Session();
        var authentication = new StubAuthentication(session, Session("interactive-session"));
        var entitlements = new StubEntitlements(Entitlement(AppEdition.Professional));
        var coordinator = Coordinator(
            authentication,
            entitlements,
            AppEdition.Basic);

        var attempt = await coordinator.AcquireAccessAsync();

        attempt.IsGranted.Should().BeTrue();
        attempt.Decision!.RequiredEdition.Should().Be(AppEdition.Basic);
        attempt.Decision.GrantedEdition.Should().Be(AppEdition.Professional);
        authentication.AuthenticateCount.Should().Be(0);
        entitlements.LastSession.Should().BeSameAs(session);
    }

    [Fact]
    public async Task Missing_session_uses_provider_action_then_denies_an_insufficient_plan()
    {
        var interactiveSession = Session("interactive-session");
        var authentication = new StubAuthentication(null, interactiveSession);
        var coordinator = Coordinator(
            authentication,
            new StubEntitlements(Entitlement(AppEdition.Basic)),
            AppEdition.Professional);

        var attempt = await coordinator.AcquireAccessAsync();

        attempt.IsGranted.Should().BeFalse();
        attempt.Failure.Should().Be(AccountGateAttemptFailure.EntitlementDenied);
        attempt.Decision!.Reason.Should().Be(EntitlementAccessReason.EditionInsufficient);
        attempt.FailureMessage.Should().Contain("Upgrade to Professional ($79 / month)");
        authentication.AuthenticateCount.Should().Be(1);
    }

    [Fact]
    public async Task Release_build_without_a_production_provider_fails_closed()
    {
        var services = AccountGateServiceFactory.Create(
            "Development",
            timeProvider: new FixedTimeProvider(Now),
            isDebugBuild: false);
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Basic,
            new FixedTimeProvider(Now));

        var attempt = await coordinator.AcquireAccessAsync();

        services.Mode.Should().Be(AccountGateProviderMode.Unavailable);
        attempt.IsGranted.Should().BeFalse();
        attempt.Failure.Should().Be(AccountGateAttemptFailure.ProviderUnavailable);
        attempt.FailureMessage.Should().Contain("Access remains locked");
    }

    [Fact]
    public void Default_factory_follows_the_compiled_build_mode()
    {
        var services = AccountGateServiceFactory.Create(
            "Development",
            timeProvider: new FixedTimeProvider(Now));

#if DEBUG
        services.Mode.Should().Be(AccountGateProviderMode.Development);
#else
        services.Mode.Should().Be(AccountGateProviderMode.Unavailable);
#endif
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("DevLogin")]
    [InlineData("DevSimLogin")]
    [InlineData("DevNewUser")]
    [InlineData("DevNoStrategies")]
    [InlineData("DevSim")]
    [InlineData("DevReplay")]
    [InlineData("DevLive")]
    public async Task Debug_development_profiles_use_the_ephemeral_local_adapter(string environmentName)
    {
        var clock = new FixedTimeProvider(Now);
        var services = AccountGateServiceFactory.Create(
            environmentName,
            timeProvider: clock,
            isDebugBuild: true);
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Professional,
            clock);

        var attempt = await coordinator.AcquireAccessAsync();

        services.Mode.Should().Be(AccountGateProviderMode.Development);
        attempt.IsGranted.Should().BeTrue();
        attempt.Session!.Account.AccountId.Should().Be("development-account");
    }

    [Fact]
    public async Task Google_identity_reuses_the_existing_local_professional_entitlement_issuer()
    {
        var clock = new FixedTimeProvider(Now);
        var identityProvider = new StubGoogleIdentityProvider();
        var store = new MemoryAccountSessionStore();
        var services = AccountGateServiceFactory.Create(
            "DevLogin",
            timeProvider: clock,
            isDebugBuild: true,
            googleIdentityProvider: identityProvider,
            accountSessionStore: store);
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Professional,
            clock);

        var attempt = await coordinator.AcquireAccessAsync();

        services.HasGoogleAuthentication.Should().BeTrue();
        attempt.IsGranted.Should().BeTrue();
        attempt.Decision!.GrantedEdition.Should().Be(AppEdition.Professional);
        attempt.Session!.Account.AccountId.Should().Be("google:subject-42");
        attempt.Session.Account.EmailAddress.Should().Be("person@example.com");
        identityProvider.AuthenticateCount.Should().Be(1);
        store.Session.Should().BeSameAs(attempt.Session);
    }

    [Fact]
    public async Task Stored_google_session_is_reused_without_another_oauth_flow()
    {
        var clock = new FixedTimeProvider(Now);
        var stored = new AccountSessionSnapshot(
            "stored-session",
            new AccountIdentity("google:subject-42", "Example Person", "person@example.com"),
            Now.AddHours(-1),
            Now.AddHours(7));
        var store = new MemoryAccountSessionStore { Session = stored };
        var identityProvider = new StubGoogleIdentityProvider();
        var services = AccountGateServiceFactory.Create(
            "DevLogin",
            timeProvider: clock,
            isDebugBuild: true,
            googleIdentityProvider: identityProvider,
            accountSessionStore: store);
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Professional,
            clock);

        var attempt = await coordinator.AcquireAccessAsync();

        attempt.IsGranted.Should().BeTrue();
        attempt.Session.Should().BeSameAs(stored);
        identityProvider.AuthenticateCount.Should().Be(0);
    }

    [Fact]
    public async Task Forced_fresh_authentication_ignores_a_stored_google_session()
    {
        var clock = new FixedTimeProvider(Now);
        var stored = new AccountSessionSnapshot(
            "stored-session",
            new AccountIdentity("google:old-subject"),
            Now.AddHours(-1),
            Now.AddHours(7));
        var store = new MemoryAccountSessionStore { Session = stored };
        var identityProvider = new StubGoogleIdentityProvider();
        var services = AccountGateServiceFactory.Create(
            "DevNewUser",
            timeProvider: clock,
            isDebugBuild: true,
            googleIdentityProvider: identityProvider,
            accountSessionStore: store,
            forceFreshAuthentication: true);
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Professional,
            clock);

        var attempt = await coordinator.AcquireAccessAsync();

        attempt.IsGranted.Should().BeTrue();
        attempt.Session!.Account.AccountId.Should().Be("google:subject-42");
        identityProvider.AuthenticateCount.Should().Be(1);
    }

    [Fact]
    public async Task Configured_google_flow_keeps_the_local_developer_escape_path()
    {
        var clock = new FixedTimeProvider(Now);
        var identityProvider = new StubGoogleIdentityProvider();
        var services = AccountGateServiceFactory.Create(
            "DevLogin",
            timeProvider: clock,
            isDebugBuild: true,
            googleIdentityProvider: identityProvider,
            accountSessionStore: new MemoryAccountSessionStore());
        var coordinator = new AccountGateCoordinator(
            services.Authentication,
            services.Entitlements,
            AppEdition.Professional,
            clock);

        var attempt = await coordinator.AcquireLocalDevelopmentAccessAsync();

        attempt.IsGranted.Should().BeTrue();
        attempt.Session!.Account.AccountId.Should().Be("development-account");
        identityProvider.AuthenticateCount.Should().Be(0);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData("")]
    public void Debug_local_adapter_is_not_available_outside_explicit_development_profiles(
        string environmentName)
    {
        DevelopmentAccountProviderPolicy.CanUseLocalAdapter(environmentName, isDebugBuild: true)
            .Should().BeFalse();
    }

    private static AccountGateCoordinator Coordinator(
        IAccountAuthenticationService authentication,
        IEntitlementService entitlements,
        AppEdition edition) => new(
        authentication,
        entitlements,
        edition,
        new FixedTimeProvider(Now));

    private static AccountSessionSnapshot Session(string id = "current-session") => new(
        id,
        new AccountIdentity("account-42", "Test account", "test@example.invalid"),
        Now.AddMinutes(-5),
        Now.AddHours(1));

    private static SubscriptionEntitlement Entitlement(AppEdition edition) => new(
        "account-42",
        edition,
        SubscriptionEntitlementState.Active,
        Now.AddDays(-1),
        Now.AddDays(1));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubGoogleIdentityProvider : IGoogleIdentityProvider
    {
        public int AuthenticateCount { get; private set; }

        public Task<GoogleIdentity> AuthenticateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AuthenticateCount++;
            return Task.FromResult(new GoogleIdentity(
                "subject-42",
                "person@example.com",
                "Example Person",
                Now.AddHours(1),
                "google-id-token"));
        }
    }

    private sealed class MemoryAccountSessionStore : IDevelopmentAccountSessionStore
    {
        public AccountSessionSnapshot? Session { get; set; }

        public AccountSessionSnapshot? Load() => Session;

        public bool Save(AccountSessionSnapshot session)
        {
            Session = session;
            return true;
        }

        public bool Clear()
        {
            Session = null;
            return true;
        }
    }

    private sealed class StubAuthentication(
        AccountSessionSnapshot? current,
        AccountSessionSnapshot authenticated) : IAccountAuthenticationService
    {
        public int AuthenticateCount { get; private set; }

        public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default) =>
            Task.FromResult(current);

        public Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
        {
            AuthenticateCount++;
            return Task.FromResult(authenticated);
        }

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubEntitlements(SubscriptionEntitlement? entitlement) : IEntitlementService
    {
        public AccountSessionSnapshot? LastSession { get; private set; }

        public Task<SubscriptionEntitlement?> GetEntitlementAsync(
            AccountSessionSnapshot session,
            CancellationToken ct = default)
        {
            LastSession = session;
            return Task.FromResult(entitlement);
        }
    }
}
