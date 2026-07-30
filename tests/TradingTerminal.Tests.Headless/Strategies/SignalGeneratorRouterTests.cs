using FluentAssertions;
using System.Reactive.Linq;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Trading;
using TradingTerminal.UI;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

public sealed class SignalGeneratorRouterTests
{
    [Fact]
    public async Task Direct_signal_preserves_flat_strength_and_note_without_creating_an_order_event()
    {
        var router = new SignalGeneratorRouter();
        var orderEvents = new List<OrderEvent>();
        using var subscription = router.OrderEvents.Subscribe(orderEvents.Add);
        router.UpdateMarketContext(new Tick(
            new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc),
            Bid: 99, Ask: 101, BidSize: 10, AskSize: 12));
        SignalEntry? captured = null;
        router.SignalEmitted += signal => captured = signal;

        await router.EmitSignalAsync(new StrategySignal(StrategySignalKind.Flat, 0.625, NoteId: 17));

        captured.Should().NotBeNull();
        captured!.DirectSignal.Should().Be(new StrategySignal(StrategySignalKind.Flat, 0.625, 17));
        captured.SideText.Should().Be("FLAT");
        captured.TypeText.Should().Be("Signal");
        captured.QuantityText.Should().Be("0.625");
        captured.Price.Should().Be(100);
        captured.Note.Should().Be("note 17");
        orderEvents.Should().BeEmpty();
    }
}
