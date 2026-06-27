using FluentAssertions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Trading;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

public sealed class TransactionCostAnalysisTests
{
    private static FillRecord Fill(OrderSide side, double price, double mid, long qty = 1, int hour = 14,
        LiquidityFlag liquidity = LiquidityFlag.Taker) =>
        new(new DateTime(2026, 5, 12, hour, 0, 0, DateTimeKind.Utc), "o", side, qty, price, mid, liquidity);

    [Fact]
    public void EmptyFills_ReturnsZeroReport()
    {
        var r = TransactionCostAnalysis.Compute(Array.Empty<FillRecord>());
        r.FillCount.Should().Be(0);
        r.ImplementationShortfall.Should().Be(0);
        r.ByHourUtc.Should().BeEmpty();
    }

    [Fact]
    public void BuySlippage_IsPositiveWhenFillAboveMid()
    {
        var fills = new[] { Fill(OrderSide.Buy, price: 100.05, mid: 100.00) };
        var r = TransactionCostAnalysis.Compute(fills);
        r.MeanSlippage.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void SellSlippage_IsPositiveWhenFillBelowMid()
    {
        var fills = new[] { Fill(OrderSide.Sell, price: 99.95, mid: 100.00) };
        var r = TransactionCostAnalysis.Compute(fills);
        r.MeanSlippage.Should().BeApproximately(0.05, 1e-9);
    }

    [Fact]
    public void MakerTakerMix_WeightedByQuantity()
    {
        var fills = new[]
        {
            Fill(OrderSide.Buy, 100, 100, qty: 1, liquidity: LiquidityFlag.Maker),
            Fill(OrderSide.Buy, 100, 100, qty: 3, liquidity: LiquidityFlag.Taker),
        };
        var r = TransactionCostAnalysis.Compute(fills);
        r.MakerFraction.Should().BeApproximately(0.25, 1e-9);
        r.TakerFraction.Should().BeApproximately(0.75, 1e-9);
    }

    [Fact]
    public void ImplementationShortfall_EqualsVwapMinusTwap()
    {
        var fills = new[]
        {
            Fill(OrderSide.Buy, price: 100.10, mid: 100.00),
            Fill(OrderSide.Buy, price: 100.20, mid: 100.10),
        };
        // TWAP mid = (100.00 + 100.10) / 2 = 100.05
        // VWAP fill = (100.10 + 100.20) / 2 = 100.15
        // IS = 0.10
        var r = TransactionCostAnalysis.Compute(fills);
        r.TwapMid.Should().BeApproximately(100.05, 1e-9);
        r.VwapFill.Should().BeApproximately(100.15, 1e-9);
        r.ImplementationShortfall.Should().BeApproximately(0.10, 1e-9);
    }

    [Fact]
    public void HourBuckets_GroupAndAverageCorrectly()
    {
        var fills = new[]
        {
            Fill(OrderSide.Buy, 100.10, 100.00, hour: 14),
            Fill(OrderSide.Buy, 100.20, 100.00, hour: 14),
            Fill(OrderSide.Sell, 99.80, 100.00, hour: 15),
        };
        var r = TransactionCostAnalysis.Compute(fills);
        r.ByHourUtc.Should().HaveCount(2);
        var h14 = r.ByHourUtc.First(b => b.Hour == 14);
        h14.Fills.Should().Be(2);
        h14.MeanSlippage.Should().BeApproximately(0.15, 1e-9);
        var h15 = r.ByHourUtc.First(b => b.Hour == 15);
        h15.MeanSlippage.Should().BeApproximately(0.20, 1e-9);
    }
}
