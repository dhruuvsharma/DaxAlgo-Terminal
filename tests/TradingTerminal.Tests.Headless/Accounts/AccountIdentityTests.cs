using FluentAssertions;
using TradingTerminal.Core.Accounts;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class AccountIdentityTests
{
    private static readonly DateTimeOffset AuthenticatedAt =
        new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Identity_normalizes_product_owned_fields()
    {
        var identity = new AccountIdentity("  account-42  ", "  Ada  ", "  ada@example.test  ");

        identity.AccountId.Should().Be("account-42");
        identity.DisplayName.Should().Be("Ada");
        identity.EmailAddress.Should().Be("ada@example.test");
    }

    [Fact]
    public void Identity_turns_blank_optional_claims_into_null()
    {
        var identity = new AccountIdentity("account-42", " ", null);

        identity.DisplayName.Should().BeNull();
        identity.EmailAddress.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Identity_rejects_blank_account_id(string accountId)
    {
        var act = () => new AccountIdentity(accountId);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Session_uses_inclusive_start_and_exclusive_expiry()
    {
        var session = Session(expiresAtUtc: AuthenticatedAt.AddHours(1));

        session.IsActiveAt(AuthenticatedAt.AddTicks(-1)).Should().BeFalse();
        session.IsActiveAt(AuthenticatedAt).Should().BeTrue();
        session.IsActiveAt(AuthenticatedAt.AddHours(1).AddTicks(-1)).Should().BeTrue();
        session.IsActiveAt(AuthenticatedAt.AddHours(1)).Should().BeFalse();
    }

    [Fact]
    public void Session_without_expiry_remains_active_after_its_start()
    {
        var session = Session();

        session.IsActiveAt(AuthenticatedAt.AddYears(20)).Should().BeTrue();
    }

    [Fact]
    public void Session_normalizes_offsets_to_utc()
    {
        var authenticated = new DateTimeOffset(2026, 7, 21, 14, 0, 0, TimeSpan.FromHours(2));
        var expires = authenticated.AddHours(1);

        var session = new AccountSessionSnapshot(
            "session-1",
            new AccountIdentity("account-42"),
            authenticated,
            expires);

        session.AuthenticatedAtUtc.Should().Be(AuthenticatedAt);
        session.AuthenticatedAtUtc.Offset.Should().Be(TimeSpan.Zero);
        session.ExpiresAtUtc.Should().Be(AuthenticatedAt.AddHours(1));
        session.ExpiresAtUtc!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Session_rejects_non_positive_lifetime(int expiryOffsetSeconds)
    {
        var act = () => Session(AuthenticatedAt.AddSeconds(expiryOffsetSeconds));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static AccountSessionSnapshot Session(DateTimeOffset? expiresAtUtc = null) =>
        new("session-1", new AccountIdentity("account-42"), AuthenticatedAt, expiresAtUtc);
}
