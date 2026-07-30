using FluentAssertions;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class EntitlementAccessEvaluatorTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Missing_entitlement_is_denied_without_a_granted_edition()
    {
        var decision = EntitlementAccessEvaluator.Evaluate(Request(), null);

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(EntitlementAccessReason.EntitlementMissing);
        decision.GrantedEdition.Should().BeNull();
    }

    [Fact]
    public void Account_mismatch_is_denied_before_entitlement_details_are_exposed()
    {
        var decision = EntitlementAccessEvaluator.Evaluate(
            Request(accountId: "another-account"),
            Entitlement());

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(EntitlementAccessReason.AccountMismatch);
        decision.GrantedEdition.Should().BeNull();
    }

    [Theory]
    [InlineData(SubscriptionEntitlementState.Suspended, EntitlementAccessReason.EntitlementSuspended)]
    [InlineData(SubscriptionEntitlementState.Revoked, EntitlementAccessReason.EntitlementRevoked)]
    public void Non_active_state_is_denied(
        SubscriptionEntitlementState state,
        EntitlementAccessReason expectedReason)
    {
        var decision = EntitlementAccessEvaluator.Evaluate(Request(), Entitlement(state: state));

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(expectedReason);
        decision.GrantedEdition.Should().BeNull();
    }

    [Fact]
    public void Revocation_takes_precedence_over_time_and_edition_checks()
    {
        var decision = EntitlementAccessEvaluator.Evaluate(
            Request(requiredEdition: AppEdition.Professional),
            Entitlement(
                edition: AppEdition.Basic,
                state: SubscriptionEntitlementState.Revoked,
                validFromUtc: Now.AddDays(1)));

        decision.Reason.Should().Be(EntitlementAccessReason.EntitlementRevoked);
    }

    [Fact]
    public void Validity_start_is_inclusive()
    {
        var before = EntitlementAccessEvaluator.Evaluate(
            Request(currentUtc: Now.AddTicks(-1)),
            Entitlement(validFromUtc: Now));
        var atStart = EntitlementAccessEvaluator.Evaluate(
            Request(),
            Entitlement(validFromUtc: Now));

        before.Reason.Should().Be(EntitlementAccessReason.EntitlementNotYetValid);
        atStart.IsGranted.Should().BeTrue();
        atStart.Reason.Should().Be(EntitlementAccessReason.Granted);
    }

    [Fact]
    public void Expiry_is_exclusive_when_there_is_no_grace_period()
    {
        var beforeExpiry = EntitlementAccessEvaluator.Evaluate(
            Request(currentUtc: Now.AddTicks(-1)),
            Entitlement(expiresAtUtc: Now));
        var atExpiry = EntitlementAccessEvaluator.Evaluate(
            Request(),
            Entitlement(expiresAtUtc: Now));

        beforeExpiry.IsGranted.Should().BeTrue();
        atExpiry.IsGranted.Should().BeFalse();
        atExpiry.Reason.Should().Be(EntitlementAccessReason.EntitlementExpired);
        atExpiry.GrantedEdition.Should().BeNull();
    }

    [Fact]
    public void Grace_period_starts_at_expiry_and_ends_exclusively()
    {
        var entitlement = Entitlement(
            expiresAtUtc: Now,
            graceEndsAtUtc: Now.AddDays(2));

        var atExpiry = EntitlementAccessEvaluator.Evaluate(Request(), entitlement);
        var beforeGraceEnd = EntitlementAccessEvaluator.Evaluate(
            Request(currentUtc: Now.AddDays(2).AddTicks(-1)),
            entitlement);
        var atGraceEnd = EntitlementAccessEvaluator.Evaluate(
            Request(currentUtc: Now.AddDays(2)),
            entitlement);

        atExpiry.IsGranted.Should().BeTrue();
        atExpiry.Reason.Should().Be(EntitlementAccessReason.GrantedDuringGracePeriod);
        beforeGraceEnd.Reason.Should().Be(EntitlementAccessReason.GrantedDuringGracePeriod);
        atGraceEnd.IsGranted.Should().BeFalse();
        atGraceEnd.Reason.Should().Be(EntitlementAccessReason.EntitlementExpired);
    }

    [Fact]
    public void Open_ended_entitlement_does_not_expire()
    {
        var decision = EntitlementAccessEvaluator.Evaluate(
            Request(currentUtc: Now.AddYears(50)),
            Entitlement());

        decision.IsGranted.Should().BeTrue();
    }

    [Theory]
    [InlineData(AppEdition.Basic, AppEdition.Basic, true)]
    [InlineData(AppEdition.Basic, AppEdition.Professional, false)]
    [InlineData(AppEdition.Professional, AppEdition.Basic, true)]
    [InlineData(AppEdition.Professional, AppEdition.Professional, true)]
    public void Existing_AppEdition_order_controls_access(
        AppEdition grantedEdition,
        AppEdition requiredEdition,
        bool expectedGranted)
    {
        var decision = EntitlementAccessEvaluator.Evaluate(
            Request(requiredEdition: requiredEdition),
            Entitlement(edition: grantedEdition));

        decision.IsGranted.Should().Be(expectedGranted);
        decision.GrantedEdition.Should().Be(grantedEdition);
        decision.Reason.Should().Be(
            expectedGranted
                ? EntitlementAccessReason.Granted
                : EntitlementAccessReason.EditionInsufficient);
    }

    [Fact]
    public void Expiry_takes_precedence_over_insufficient_edition()
    {
        var decision = EntitlementAccessEvaluator.Evaluate(
            Request(requiredEdition: AppEdition.Professional),
            Entitlement(edition: AppEdition.Basic, expiresAtUtc: Now));

        decision.Reason.Should().Be(EntitlementAccessReason.EntitlementExpired);
        decision.GrantedEdition.Should().BeNull();
    }

    [Fact]
    public void Request_and_decision_normalize_evaluation_time_to_utc()
    {
        var equivalentNow = new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.FromHours(2));
        var request = Request(currentUtc: equivalentNow);

        var decision = EntitlementAccessEvaluator.Evaluate(request, Entitlement());

        request.CurrentUtc.Should().Be(Now);
        request.CurrentUtc.Offset.Should().Be(TimeSpan.Zero);
        decision.EvaluatedAtUtc.Should().Be(Now);
        decision.RequiredEdition.Should().Be(AppEdition.Basic);
    }

    [Fact]
    public void Request_rejects_unknown_required_edition()
    {
        var act = () => Request(requiredEdition: (AppEdition)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static EntitlementAccessRequest Request(
        string accountId = "account-42",
        AppEdition requiredEdition = AppEdition.Basic,
        DateTimeOffset? currentUtc = null) =>
        new(accountId, requiredEdition, currentUtc ?? Now);

    private static SubscriptionEntitlement Entitlement(
        AppEdition edition = AppEdition.Professional,
        SubscriptionEntitlementState state = SubscriptionEntitlementState.Active,
        DateTimeOffset? validFromUtc = null,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? graceEndsAtUtc = null) =>
        new(
            "account-42",
            edition,
            state,
            validFromUtc ?? Now.AddDays(-30),
            expiresAtUtc,
            graceEndsAtUtc);
}
