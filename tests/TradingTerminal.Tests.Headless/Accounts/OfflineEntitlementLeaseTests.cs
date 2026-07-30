using FluentAssertions;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class OfflineEntitlementLeaseTests
{
    private const string DeviceId = "device-1";

    private static readonly DateTimeOffset Now =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Envelope_preserves_opaque_signed_values()
    {
        var envelope = new SignedOfflineLeaseEnvelope(
            " payload-with-exact-spacing ",
            " signature-with-exact-spacing ",
            " key-7 ",
            " algorithm-1 ");

        envelope.EncodedPayload.Should().Be(" payload-with-exact-spacing ");
        envelope.EncodedSignature.Should().Be(" signature-with-exact-spacing ");
        envelope.KeyId.Should().Be(" key-7 ");
        envelope.Algorithm.Should().Be(" algorithm-1 ");
    }

    [Theory]
    [InlineData("", "signature", "key", "algorithm")]
    [InlineData("payload", " ", "key", "algorithm")]
    [InlineData("payload", "signature", "", "algorithm")]
    [InlineData("payload", "signature", "key", "   ")]
    public void Envelope_rejects_blank_required_values(
        string payload,
        string signature,
        string keyId,
        string algorithm)
    {
        var act = () => new SignedOfflineLeaseEnvelope(payload, signature, keyId, algorithm);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Lease_uses_inclusive_issue_and_exclusive_expiry()
    {
        var validation = OfflineLeaseValidationResult.Valid(
            Lease(issuedAtUtc: Now, expiresAtUtc: Now.AddHours(1)));

        var atIssue = EvaluateOffline(Request(), validation);
        var beforeExpiry = EvaluateOffline(
            Request(currentUtc: Now.AddHours(1).AddTicks(-1)),
            validation);
        var atExpiry = EvaluateOffline(
            Request(currentUtc: Now.AddHours(1)),
            validation);

        atIssue.IsGranted.Should().BeTrue();
        beforeExpiry.IsGranted.Should().BeTrue();
        atExpiry.IsGranted.Should().BeFalse();
        atExpiry.Reason.Should().Be(EntitlementAccessReason.OfflineLeaseExpired);
    }

    [Fact]
    public void Lease_is_not_valid_before_authenticated_not_before_time()
    {
        var validation = OfflineLeaseValidationResult.Valid(
            Lease(
                issuedAtUtc: Now.AddMinutes(-1),
                notBeforeUtc: Now.AddMinutes(1),
                expiresAtUtc: Now.AddHours(1)));

        var decision = EvaluateOffline(Request(), validation);

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(EntitlementAccessReason.OfflineLeaseNotYetValid);
    }

    [Fact]
    public void Invalid_signature_result_is_mapped_to_provider_neutral_access_reason()
    {
        var validation = OfflineLeaseValidationResult.Invalid(
            OfflineLeaseValidationFailure.InvalidSignature);

        var decision = EvaluateOffline(Request(), validation);

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(EntitlementAccessReason.OfflineLeaseInvalid);
        decision.GrantedEdition.Should().BeNull();
    }

    [Fact]
    public void Lease_for_another_account_is_denied_before_time_checks()
    {
        var validation = OfflineLeaseValidationResult.Valid(
            Lease(
                accountId: "another-account",
                issuedAtUtc: Now.AddMinutes(1),
                expiresAtUtc: Now.AddHours(1)));

        var decision = EvaluateOffline(Request(), validation);

        decision.Reason.Should().Be(EntitlementAccessReason.AccountMismatch);
        decision.GrantedEdition.Should().BeNull();
    }

    [Fact]
    public void Signed_lease_can_carry_subscription_grace_access()
    {
        var validation = OfflineLeaseValidationResult.Valid(
            Lease(
                issuedAtUtc: Now.AddDays(-2),
                expiresAtUtc: Now.AddDays(2),
                entitlementExpiresAtUtc: Now.AddDays(-1),
                graceEndsAtUtc: Now.AddDays(1)));

        var decision = EvaluateOffline(Request(), validation);

        decision.IsGranted.Should().BeTrue();
        decision.Reason.Should().Be(EntitlementAccessReason.GrantedDuringGracePeriod);
        decision.GrantedEdition.Should().Be(AppEdition.Professional);
    }

    [Fact]
    public void Lease_expiry_caps_a_longer_entitlement_grace_period()
    {
        var validation = OfflineLeaseValidationResult.Valid(
            Lease(
                issuedAtUtc: Now.AddDays(-2),
                expiresAtUtc: Now,
                entitlementExpiresAtUtc: Now.AddDays(-1),
                graceEndsAtUtc: Now.AddDays(2)));

        var decision = EvaluateOffline(Request(), validation);

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(EntitlementAccessReason.OfflineLeaseExpired);
    }

    [Fact]
    public void Lease_rejects_non_positive_lifetime()
    {
        var atIssue = () => Lease(issuedAtUtc: Now, expiresAtUtc: Now);
        var beforeIssue = () => Lease(issuedAtUtc: Now, expiresAtUtc: Now.AddTicks(-1));

        atIssue.Should().Throw<ArgumentOutOfRangeException>();
        beforeIssue.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Authenticated_device_claim_must_match_expected_device()
    {
        var validation = OfflineLeaseValidationResult.Valid(
            Lease(
                issuedAtUtc: Now,
                expiresAtUtc: Now.AddHours(1),
                deviceId: "another-device"));

        var decision = EvaluateOffline(Request(), validation);

        decision.IsGranted.Should().BeFalse();
        decision.Reason.Should().Be(EntitlementAccessReason.OfflineLeaseDeviceMismatch);
    }

    [Fact]
    public void Authenticated_lease_claims_reject_lifetimes_over_seven_days()
    {
        var act = () => Lease(
            issuedAtUtc: Now,
            expiresAtUtc: Now.AddDays(7).AddTicks(1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Validation_result_enforces_valid_and_invalid_shapes()
    {
        var lease = Lease(issuedAtUtc: Now, expiresAtUtc: Now.AddHours(1));

        var valid = OfflineLeaseValidationResult.Valid(lease);
        var invalid = OfflineLeaseValidationResult.Invalid(OfflineLeaseValidationFailure.Revoked);
        var noFailure = () => OfflineLeaseValidationResult.Invalid(OfflineLeaseValidationFailure.None);
        var unknownFailure = () => OfflineLeaseValidationResult.Invalid((OfflineLeaseValidationFailure)99);

        valid.IsValid.Should().BeTrue();
        valid.Lease.Should().BeSameAs(lease);
        valid.Failure.Should().Be(OfflineLeaseValidationFailure.None);
        invalid.IsValid.Should().BeFalse();
        invalid.Lease.Should().BeNull();
        invalid.Failure.Should().Be(OfflineLeaseValidationFailure.Revoked);
        noFailure.Should().Throw<ArgumentOutOfRangeException>();
        unknownFailure.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static EntitlementAccessRequest Request(DateTimeOffset? currentUtc = null) =>
        new("account-42", AppEdition.Basic, currentUtc ?? Now);

    private static EntitlementAccessDecision EvaluateOffline(
        EntitlementAccessRequest request,
        OfflineLeaseValidationResult validation,
        string expectedDeviceId = DeviceId) =>
        EntitlementAccessEvaluator.EvaluateOffline(request, expectedDeviceId, validation);

    private static OfflineEntitlementLease Lease(
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc,
        string accountId = "account-42",
        string deviceId = DeviceId,
        DateTimeOffset? notBeforeUtc = null,
        DateTimeOffset? entitlementExpiresAtUtc = null,
        DateTimeOffset? graceEndsAtUtc = null)
    {
        var entitlement = new SubscriptionEntitlement(
            accountId,
            AppEdition.Professional,
            SubscriptionEntitlementState.Active,
            issuedAtUtc.AddDays(-30),
            entitlementExpiresAtUtc,
            graceEndsAtUtc);

        return new OfflineEntitlementLease(
            "lease-1",
            entitlement,
            deviceId,
            issuedAtUtc,
            notBeforeUtc ?? issuedAtUtc,
            expiresAtUtc);
    }
}
