using DaxAlgo.Blocks;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Sandbox.Runtime;
using Xunit;

namespace TradingTerminal.Blocks.Runtime.Tests;

/// <summary>
/// A running Blocks strategy's book in the one-instrument shape execution books copy: the instrument it holds or runs
/// on, the position in it, and no instrument at all for a basket a book could not copy.
/// </summary>
public sealed class BlocksModelBookTests
{
    private static readonly InstrumentId LegA = new(1);
    private static readonly InstrumentId LegB = new(2);

    [Fact]
    public async Task A_strategy_on_one_instrument_names_it_while_flat_and_reports_what_it_then_holds()
    {
        var hub = new FakeHub();
        var unit = new LambdaUnit(
            new UnitInfo("Buy two", Settings: [StrategyParameter.Instrument("instrument", "Instrument", LegA)]),
            context =>
            {
                var instrument = context.Settings.Instrument("instrument");
                context.Market.OnQuote(instrument, _ =>
                {
                    if (context.Portfolio.Position(instrument).Units == 0d)
                        context.Orders.SetTarget(instrument, 2d);
                });
                return Task.CompletedTask;
            });

        await using var runtime = new BlocksUnitRuntime(() => unit, "buy-two", Host.For(hub));
        using var book = new BlocksModelBook(runtime);
        var published = new List<IModelPortfolio>();
        book.SnapshotChanged += published.Add;
        await runtime.StartAsync();
        book.Refresh();

        published.Should().ContainSingle();
        published[0].Instrument.Should().Be(LegA, "a flat strategy on one instrument still says which");
        published[0].PositionUnits.Should().Be(0d);

        hub.PublishQuote(FakeHub.Quote(1, 100.0, 100.2));
        hub.PublishQuote(FakeHub.Quote(1, 100.0, 100.2));
        await Host.WaitUntil(() => book.CurrentSnapshot!.PositionUnits == 2d, "the unit should hold two units");

        book.CurrentSnapshot!.Instrument.Should().Be(LegA);
        book.CurrentSnapshot.IsComplete.Should().BeTrue();
        published.Should().Contain(snapshot => snapshot.PositionUnits == 2d, "every portfolio change is published");
    }

    [Fact]
    public async Task A_basket_names_no_instrument_and_a_strategy_that_closes_out_still_names_where_it_was()
    {
        var hub = new FakeHub();
        var closeOut = false;
        var unit = new LambdaUnit(
            new UnitInfo("Pair", Settings:
            [
                StrategyParameter.Instrument("legA", "Leg A", LegA),
                StrategyParameter.Instrument("legB", "Leg B", LegB),
            ]),
            context =>
            {
                var legA = context.Settings.Instrument("legA");
                var legB = context.Settings.Instrument("legB");
                context.Market.OnQuote(legA, _ => context.Orders.SetTarget(legA, closeOut ? 0d : 1d));
                context.Market.OnQuote(legB, _ => context.Orders.SetTarget(legB, closeOut ? 0d : -1d));
                return Task.CompletedTask;
            });

        await using var runtime = new BlocksUnitRuntime(() => unit, "pair", Host.For(hub));
        using var book = new BlocksModelBook(runtime);
        await runtime.StartAsync();
        book.CurrentSnapshot!.Instrument.IsNone.Should().BeTrue("it watches two instruments and holds neither");

        for (var i = 0; i < 2; i++)
        {
            hub.PublishQuote(FakeHub.Quote(1, 100.0, 100.2));
            hub.PublishQuote(FakeHub.Quote(2, 50.0, 50.2));
        }

        await Host.WaitUntil(() => runtime.Portfolio.Positions.Count(p => p.Units != 0d) == 2, "both legs should fill");
        book.CurrentSnapshot!.Instrument.IsNone.Should().BeTrue("a basket has no single instrument a book could copy");

        // Close leg B, keep A: one instrument held again.
        closeOut = true;
        for (var i = 0; i < 2; i++)
            hub.PublishQuote(FakeHub.Quote(2, 50.0, 50.2));
        await Host.WaitUntil(() => runtime.Portfolio.Positions.Count(p => p.Units != 0d) == 1, "leg B should close");
        book.CurrentSnapshot!.Instrument.Should().Be(LegA);

        // Close A too: flat, watching two, and it last held A — so the book is told to go flat on A.
        for (var i = 0; i < 2; i++)
            hub.PublishQuote(FakeHub.Quote(1, 100.0, 100.2));
        await Host.WaitUntil(() => runtime.Portfolio.Positions.All(p => p.Units == 0d), "leg A should close");
        book.CurrentSnapshot!.Instrument.Should().Be(LegA);
        book.CurrentSnapshot.PositionUnits.Should().Be(0d);
    }
}
