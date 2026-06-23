using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Risk;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Risk;

public sealed class RiskManagerTests
{
    private static OrderRequest Buy(string sym, long qty) => new(
        ClientOrderId: $"o-{Guid.NewGuid():N}",
        Contract: Contract.UsStock(sym),
        Side: OrderSide.Buy,
        Type: OrderType.Market,
        Quantity: qty);

    private static OrderRequest Sell(string sym, long qty) => new(
        ClientOrderId: $"o-{Guid.NewGuid():N}",
        Contract: Contract.UsStock(sym),
        Side: OrderSide.Sell,
        Type: OrderType.Market,
        Quantity: qty);

    private static OrderEvent Fill(string clientId, OrderSide side, long qty, double price, DateTime? t = null) =>
        new(
            TimestampUtc: t ?? DateTime.UtcNow,
            ClientOrderId: clientId,
            BrokerOrderId: "B",
            Side: side,
            State: OrderState.Filled,
            FilledQuantity: qty,
            AverageFillPrice: price,
            LastFillQuantity: qty,
            LastFillPrice: price);

    [Fact]
    public void NoCapsConfigured_AllowsAnything()
    {
        var rm = new RiskManager(new RiskOptions());

        rm.Evaluate(Buy("ES", 100)).Allowed.Should().BeTrue();
        rm.Evaluate(Sell("ES", 1_000_000)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void MaxPositionPerSymbol_RejectsWhenAccumulatedExceedsCap()
    {
        var rm = new RiskManager(new RiskOptions { MaxPositionPerSymbol = 5 });

        // Pretend we already filled 3 long on ES
        rm.RecordFill("ES", Fill("o1", OrderSide.Buy, 3, 100.0));

        rm.Evaluate(Buy("ES", 2)).Allowed.Should().BeTrue();
        rm.Evaluate(Buy("ES", 3)).Allowed.Should().BeFalse("would push to 6, over cap of 5");
        // Different symbol stays open
        rm.Evaluate(Buy("NQ", 5)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void DailyLossCap_RejectsAfterRealizedLossThreshold()
    {
        var rm = new RiskManager(new RiskOptions
        {
            MaxDailyLoss = 50.0,
            DefaultContractMultiplier = 1.0,
        });

        var day = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        rm.RecordFill("ES", Fill("buy", OrderSide.Buy, 1, 100.0, day));
        rm.RecordFill("ES", Fill("sell", OrderSide.Sell, 1, 40.0, day.AddMinutes(1)));

        rm.RealisedPnlToday.Should().BeApproximately(-60.0, 1e-9);
        rm.Evaluate(Buy("ES", 1)).Allowed.Should().BeFalse();
    }

    [Fact]
    public void DailyLossCap_ResetsAtUtcMidnight()
    {
        var rm = new RiskManager(new RiskOptions
        {
            MaxDailyLoss = 50.0,
            DefaultContractMultiplier = 1.0,
        });

        var d1 = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        rm.RecordFill("ES", Fill("b1", OrderSide.Buy, 1, 100.0, d1));
        rm.RecordFill("ES", Fill("s1", OrderSide.Sell, 1, 40.0, d1.AddMinutes(1)));
        rm.Evaluate(Buy("ES", 1)).Allowed.Should().BeFalse();

        // New UTC day → counters reset on the next fill
        var d2 = d1.AddDays(1);
        rm.RecordFill("ES", Fill("b2", OrderSide.Buy, 1, 50.0, d2));
        rm.RealisedPnlToday.Should().Be(0);
        rm.Evaluate(Sell("ES", 1)).Allowed.Should().BeTrue();
    }

    [Fact]
    public void Fills_AreIdempotentByClientIdAndFillQty()
    {
        var rm = new RiskManager(new RiskOptions { MaxPositionPerSymbol = 5 });

        var fill = Fill("o1", OrderSide.Buy, 3, 100.0);
        rm.RecordFill("ES", fill);
        rm.RecordFill("ES", fill);
        rm.RecordFill("ES", fill);

        rm.PositionFor("ES").Should().Be(3);
    }
}
