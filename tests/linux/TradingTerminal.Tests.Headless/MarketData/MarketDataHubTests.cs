using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.MarketData;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

public sealed class MarketDataHubTests
{
    [Fact]
    public void Quote_published_reaches_subscriber_for_same_instrument()
    {
        var hub = new MarketDataHub();
        var id = new InstrumentId(11);
        Quote? received = null;

        using var sub = hub.Quotes(id).Subscribe(q => received = q);
        hub.PublishQuote(new Quote(id, DateTime.UtcNow, DateTime.UtcNow, 1, 2, 0, 0, BrokerKind.Alpaca, 1, false));

        received.Should().NotBeNull();
        received!.Ask.Should().Be(2);
    }

    [Fact]
    public void Subscriber_does_not_receive_other_instruments()
    {
        var hub = new MarketDataHub();
        var count = 0;

        using var sub = hub.Quotes(new InstrumentId(1)).Subscribe(_ => count++);
        hub.PublishQuote(new Quote(new InstrumentId(2), DateTime.UtcNow, DateTime.UtcNow, 1, 2, 0, 0, BrokerKind.Alpaca, 1, false));

        count.Should().Be(0);
    }

    [Fact]
    public void Bars_are_keyed_by_size()
    {
        var hub = new MarketDataHub();
        var id = new InstrumentId(3);
        var oneMin = 0;
        var fiveMin = 0;

        using var s1 = hub.Bars(id, BarSize.OneMinute).Subscribe(_ => oneMin++);
        using var s5 = hub.Bars(id, BarSize.FiveMinutes).Subscribe(_ => fiveMin++);
        hub.PublishBar(new OhlcvBar(id, BarSize.OneMinute, DateTime.UtcNow, 1, 1, 1, 1, 1, BrokerKind.Alpaca, true));

        oneMin.Should().Be(1);
        fiveMin.Should().Be(0);
    }
}
