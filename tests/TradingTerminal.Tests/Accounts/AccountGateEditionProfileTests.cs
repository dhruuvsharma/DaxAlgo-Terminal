using FluentAssertions;
using TradingTerminal.Accounts;
using TradingTerminal.Core.Configuration;
using Xunit;

namespace TradingTerminal.Tests.Accounts;

public sealed class AccountGateEditionProfileTests
{
    [Theory]
    [InlineData(AppEdition.Basic, "Basic", "Free")]
    [InlineData(AppEdition.Professional, "Professional", "$79 / month")]
    public void Edition_maps_to_the_distribution_plan(
        AppEdition edition,
        string planName,
        string price)
    {
        var profile = AccountGateEditionProfile.For(edition);

        profile.Edition.Should().Be(edition);
        profile.PlanName.Should().Be(planName);
        profile.Price.Should().Be(price);
        profile.Summary.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Unknown_edition_is_rejected()
    {
        var act = () => AccountGateEditionProfile.For((AppEdition)99);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
