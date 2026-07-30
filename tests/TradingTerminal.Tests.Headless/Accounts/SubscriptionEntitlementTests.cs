using FluentAssertions;
using TradingTerminal.Core.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class SubscriptionEntitlementTests
{
    private static readonly DateTimeOffset ValidFrom =
        new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Entitlement_normalizes_identity_reference_and_time_offsets()
    {
        var validFrom = new DateTimeOffset(2026, 7, 1, 2, 0, 0, TimeSpan.FromHours(2));
        var entitlement = new SubscriptionEntitlement(
            "  account-42  ",
            AppEdition.Professional,
            SubscriptionEntitlementState.Active,
            validFrom,
            validFrom.AddMonths(1),
            validFrom.AddMonths(1).AddDays(3),
            "  subscription-7  ");

        entitlement.AccountId.Should().Be("account-42");
        entitlement.SubscriptionReference.Should().Be("subscription-7");
        entitlement.ValidFromUtc.Offset.Should().Be(TimeSpan.Zero);
        entitlement.ExpiresAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
        entitlement.GraceEndsAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Entitlement_allows_an_open_ended_window_without_grace()
    {
        var entitlement = Create();

        entitlement.ExpiresAtUtc.Should().BeNull();
        entitlement.GraceEndsAtUtc.Should().BeNull();
    }

    [Fact]
    public void Entitlement_rejects_expiry_at_or_before_start()
    {
        var atStart = () => Create(expiresAtUtc: ValidFrom);
        var beforeStart = () => Create(expiresAtUtc: ValidFrom.AddTicks(-1));

        atStart.Should().Throw<ArgumentOutOfRangeException>();
        beforeStart.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Entitlement_rejects_grace_without_expiry()
    {
        var act = () => Create(graceEndsAtUtc: ValidFrom.AddDays(2));

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Entitlement_rejects_grace_end_at_or_before_expiry(int graceOffsetSeconds)
    {
        var expires = ValidFrom.AddDays(1);
        var act = () => Create(
            expiresAtUtc: expires,
            graceEndsAtUtc: expires.AddSeconds(graceOffsetSeconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Entitlement_rejects_unknown_edition_and_state()
    {
        var invalidEdition = () => Create(edition: (AppEdition)99);
        var invalidState = () => Create(state: (SubscriptionEntitlementState)99);

        invalidEdition.Should().Throw<ArgumentOutOfRangeException>();
        invalidState.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static SubscriptionEntitlement Create(
        AppEdition edition = AppEdition.Professional,
        SubscriptionEntitlementState state = SubscriptionEntitlementState.Active,
        DateTimeOffset? expiresAtUtc = null,
        DateTimeOffset? graceEndsAtUtc = null) =>
        new(
            "account-42",
            edition,
            state,
            ValidFrom,
            expiresAtUtc,
            graceEndsAtUtc);
}
