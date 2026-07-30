using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class AccountGateViewModelLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Development_mode_presents_the_local_profile_explicitly()
    {
        var session = Session("development-session", "development-account");
        var viewModel = ViewModel(
            new SwitchingAuthenticationService(session, session),
            new AccountEntitlements(new Dictionary<string, AppEdition>
            {
                ["development-account"] = AppEdition.Professional,
            }),
            AppEdition.Professional,
            AccountGateProviderMode.Development);

        viewModel.StatusTitle.Should().Be("Local developer access");
        viewModel.PrimaryActionText.Should().Be("Continue as local developer");
        viewModel.StatusMessage.Should().Contain("No external account service");
        viewModel.HasEnvironmentNotice.Should().BeTrue();
    }

    [Fact]
    public void Development_mode_with_google_presents_google_as_primary_and_keeps_local_escape()
    {
        var session = Session("development-session", "development-account");
        var viewModel = ViewModel(
            new SwitchingAuthenticationService(session, session),
            new AccountEntitlements(new Dictionary<string, AppEdition>
            {
                ["development-account"] = AppEdition.Professional,
            }),
            AppEdition.Professional,
            AccountGateProviderMode.Development,
            hasGoogleAuthentication: true);

        viewModel.StatusTitle.Should().Be("Sign in with Google");
        viewModel.PrimaryActionText.Should().Be("Sign in with Google");
        viewModel.StatusMessage.Should().Contain("system browser");
        viewModel.HasEnvironmentNotice.Should().BeTrue();
        viewModel.HasLocalDeveloperAccess.Should().BeTrue();
    }

    [Fact]
    public async Task Denied_account_can_sign_out_and_force_interactive_authentication()
    {
        var denied = Session("denied-session", "denied-account");
        var allowed = Session("allowed-session", "allowed-account");
        var authentication = new SwitchingAuthenticationService(denied, allowed);
        var entitlements = new AccountEntitlements(new Dictionary<string, AppEdition>
        {
            ["denied-account"] = AppEdition.Basic,
            ["allowed-account"] = AppEdition.Professional,
        });
        var viewModel = ViewModel(authentication, entitlements, AppEdition.Professional);
        var completions = new List<bool>();
        viewModel.Completed += completions.Add;

        await viewModel.ContinueCommand.ExecuteAsync(null);

        viewModel.CanUseAnotherAccount.Should().BeTrue();
        completions.Should().BeEmpty();

        await viewModel.UseAnotherAccountCommand.ExecuteAsync(null);

        authentication.SignOutCount.Should().Be(1);
        viewModel.CanUseAnotherAccount.Should().BeFalse();
        viewModel.PrimaryActionText.Should().Be("Sign in or create account");

        await viewModel.ContinueCommand.ExecuteAsync(null);

        authentication.AuthenticateCount.Should().Be(1);
        completions.Should().Equal(true);
        entitlements.RequestedAccountIds.Should().Equal("denied-account", "allowed-account");
    }

    [Fact]
    public async Task Cancel_during_authentication_cancels_the_token_and_rejects_a_late_grant()
    {
        var authentication = new PendingAuthenticationService();
        var viewModel = ViewModel(
            authentication,
            new AccountEntitlements(new Dictionary<string, AppEdition>
            {
                ["allowed-account"] = AppEdition.Professional,
            }),
            AppEdition.Professional);
        var completions = new List<bool>();
        viewModel.Completed += completions.Add;

        var pending = viewModel.ContinueCommand.ExecuteAsync(null);
        await authentication.Started.Task;

        viewModel.ContinueCommand.CanExecute(null).Should().BeFalse();
        viewModel.CancelCommand.Execute(null);

        authentication.CapturedToken.IsCancellationRequested.Should().BeTrue();
        completions.Should().Equal(false);

        authentication.Complete(Session("allowed-session", "allowed-account"));
        await pending;

        completions.Should().Equal(false);
    }

    [Fact]
    public async Task Cancel_during_entitlement_lookup_cancels_the_token_and_rejects_a_late_grant()
    {
        var session = Session("current-session", "allowed-account");
        var entitlements = new PendingEntitlementService();
        var viewModel = ViewModel(
            new SwitchingAuthenticationService(session, session),
            entitlements,
            AppEdition.Professional);
        var completions = new List<bool>();
        viewModel.Completed += completions.Add;

        var pending = viewModel.ContinueCommand.ExecuteAsync(null);
        await entitlements.Started.Task;

        viewModel.CancelCommand.Execute(null);

        entitlements.CapturedToken.IsCancellationRequested.Should().BeTrue();
        completions.Should().Equal(false);

        entitlements.Complete(Entitlement("allowed-account", AppEdition.Professional));
        await pending;

        completions.Should().Equal(false);
    }

    [Fact]
    public async Task Window_close_disposal_prevents_late_completion()
    {
        var authentication = new PendingAuthenticationService();
        var viewModel = ViewModel(
            authentication,
            new AccountEntitlements(new Dictionary<string, AppEdition>
            {
                ["allowed-account"] = AppEdition.Professional,
            }),
            AppEdition.Professional);
        var completions = new List<bool>();
        viewModel.Completed += completions.Add;

        var pending = viewModel.ContinueCommand.ExecuteAsync(null);
        await authentication.Started.Task;

        viewModel.Dispose();

        authentication.CapturedToken.IsCancellationRequested.Should().BeTrue();
        authentication.Complete(Session("allowed-session", "allowed-account"));
        await pending;

        completions.Should().BeEmpty();
    }

    private static AccountGateViewModel ViewModel(
        IAccountAuthenticationService authentication,
        IEntitlementService entitlements,
        AppEdition requiredEdition,
        AccountGateProviderMode providerMode = AccountGateProviderMode.Production,
        bool hasGoogleAuthentication = false)
    {
        var coordinator = new AccountGateCoordinator(
            authentication,
            entitlements,
            requiredEdition,
            new FixedTimeProvider(Now));
        return new AccountGateViewModel(
            coordinator,
            AccountGateEditionProfile.For(requiredEdition),
            providerMode,
            hasGoogleAuthentication);
    }

    private static AccountSessionSnapshot Session(string sessionId, string accountId) => new(
        sessionId,
        new AccountIdentity(accountId),
        Now.AddMinutes(-5),
        Now.AddHours(1));

    private static SubscriptionEntitlement Entitlement(string accountId, AppEdition edition) => new(
        accountId,
        edition,
        SubscriptionEntitlementState.Active,
        Now.AddDays(-1),
        Now.AddDays(1));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SwitchingAuthenticationService(
        AccountSessionSnapshot current,
        AccountSessionSnapshot interactive) : IAccountAuthenticationService
    {
        public int AuthenticateCount { get; private set; }

        public int SignOutCount { get; private set; }

        public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<AccountSessionSnapshot?>(current);
        }

        public Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            AuthenticateCount++;
            return Task.FromResult(interactive);
        }

        public Task SignOutAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            SignOutCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class PendingAuthenticationService : IAccountAuthenticationService
    {
        private readonly TaskCompletionSource<AccountSessionSnapshot> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CapturedToken { get; private set; }

        public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default) =>
            Task.FromResult<AccountSessionSnapshot?>(null);

        public Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default)
        {
            CapturedToken = ct;
            Started.TrySetResult();
            return _completion.Task;
        }

        public Task SignOutAsync(CancellationToken ct = default) => Task.CompletedTask;

        public void Complete(AccountSessionSnapshot session) => _completion.TrySetResult(session);
    }

    private sealed class AccountEntitlements(IReadOnlyDictionary<string, AppEdition> editions)
        : IEntitlementService
    {
        public List<string> RequestedAccountIds { get; } = new();

        public Task<SubscriptionEntitlement?> GetEntitlementAsync(
            AccountSessionSnapshot session,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            RequestedAccountIds.Add(session.Account.AccountId);
            var entitlement = Entitlement(
                session.Account.AccountId,
                editions[session.Account.AccountId]);
            return Task.FromResult<SubscriptionEntitlement?>(entitlement);
        }
    }

    private sealed class PendingEntitlementService : IEntitlementService
    {
        private readonly TaskCompletionSource<SubscriptionEntitlement?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken CapturedToken { get; private set; }

        public Task<SubscriptionEntitlement?> GetEntitlementAsync(
            AccountSessionSnapshot session,
            CancellationToken ct = default)
        {
            CapturedToken = ct;
            Started.TrySetResult();
            return _completion.Task;
        }

        public void Complete(SubscriptionEntitlement entitlement) =>
            _completion.TrySetResult(entitlement);
    }
}
