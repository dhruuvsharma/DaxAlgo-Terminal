using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.UI.Controls;
using Xunit;

namespace TradingTerminal.Tests.Controls;

public sealed class BrokerLogoTests
{
    [Fact]
    public void Every_real_broker_has_a_packaged_logo()
    {
        WpfTestApp.Run(() =>
        {
            foreach (var broker in Enum.GetValues<BrokerKind>().Where(x => x != BrokerKind.Simulated))
            {
                var logo = new BrokerLogo { Broker = broker };
                logo.Source.Should().NotBeNull($"{broker} is an implemented external broker");
            }
        });
    }

    [Fact]
    public void Simulated_feed_uses_the_callers_fallback_instead_of_a_broker_mark()
    {
        WpfTestApp.Run(() =>
        {
            var logo = new BrokerLogo { Broker = BrokerKind.Simulated };

            logo.Source.Should().BeNull();
        });
    }
}
