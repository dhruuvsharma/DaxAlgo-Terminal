using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class AccountGateDiagnosticsTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 21, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Unexpected_provider_failure_emits_only_category_and_correlation()
    {
        const string sensitiveProviderMessage =
            "token=secret-token email=person@example.com account=customer-42";
        var diagnostics = new CollectingDiagnostics();
        var coordinator = new AccountGateCoordinator(
            new ThrowingAuthenticationService(sensitiveProviderMessage),
            new NullEntitlementService(),
            AppEdition.Basic,
            new FixedTimeProvider(Now),
            diagnostics);

        var attempt = await coordinator.AcquireAccessAsync();

        attempt.IsGranted.Should().BeFalse();
        attempt.Failure.Should().Be(AccountGateAttemptFailure.UnexpectedProviderFailure);
        attempt.FailureMessage.Should().NotContain("secret-token");
        attempt.FailureMessage.Should().NotContain("person@example.com");
        diagnostics.Signals.Should().ContainSingle();
        diagnostics.Signals[0].Category.Should()
            .Be(AccountGateDiagnosticCategory.UnexpectedProviderFailure);
        diagnostics.Signals[0].CorrelationId.Should().Be(attempt.CorrelationId);
        diagnostics.Signals[0].ToString().Should().NotContain("customer-42");
    }

    [Fact]
    public async Task Diagnostic_sink_failure_does_not_change_fail_closed_result()
    {
        var coordinator = new AccountGateCoordinator(
            new ThrowingAuthenticationService("provider internals"),
            new NullEntitlementService(),
            AppEdition.Basic,
            new FixedTimeProvider(Now),
            new ThrowingDiagnostics());

        var attempt = await coordinator.AcquireAccessAsync();

        attempt.IsGranted.Should().BeFalse();
        attempt.Failure.Should().Be(AccountGateAttemptFailure.UnexpectedProviderFailure);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class CollectingDiagnostics : IAccountGateDiagnostics
    {
        public List<AccountGateDiagnosticSignal> Signals { get; } = new();

        public void Record(AccountGateDiagnosticSignal signal) => Signals.Add(signal);
    }

    private sealed class ThrowingDiagnostics : IAccountGateDiagnostics
    {
        public void Record(AccountGateDiagnosticSignal signal) =>
            throw new InvalidOperationException("diagnostic sink failure");
    }

    private sealed class ThrowingAuthenticationService(string message) : IAccountAuthenticationService
    {
        public Task<AccountSessionSnapshot?> GetCurrentSessionAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(message);

        public Task<AccountSessionSnapshot> AuthenticateAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(message);

        public Task SignOutAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException(message);
    }

    private sealed class NullEntitlementService : IEntitlementService
    {
        public Task<SubscriptionEntitlement?> GetEntitlementAsync(
            AccountSessionSnapshot session,
            CancellationToken ct = default) =>
            Task.FromResult<SubscriptionEntitlement?>(null);
    }
}
