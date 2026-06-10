using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Strategies.OrderFlowPressureMap;
using Xunit;

namespace TradingTerminal.Tests.OrderFlowPressureMap;

public sealed class PressureMapCalculatorTests
{
    private static readonly OrderFlowPressureMapOptions Opt = new();

    private static Bar Bar(double o, double h, double l, double c) => new(DateTime.UtcNow, o, h, l, c, 0);

    // ── CandlePosition ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CandlePosition_high_equals_low_is_half()
        => PressureMapCalculator.CandlePosition(100, 100, 100).Should().Be(0.5);

    [Fact]
    public void CandlePosition_close_on_high_is_one()
        => PressureMapCalculator.CandlePosition(101, 99, 101).Should().Be(1.0);

    [Fact]
    public void CandlePosition_close_on_low_is_zero()
        => PressureMapCalculator.CandlePosition(101, 99, 99).Should().Be(0.0);

    [Fact]
    public void CandlePosition_mid_range_is_half()
        => PressureMapCalculator.CandlePosition(101, 99, 100).Should().BeApproximately(0.5, 1e-12);

    // ── PriceImpact ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void PriceImpact_zero_atr_is_zero()
        => PressureMapCalculator.PriceImpact(100, 105, 0).Should().Be(0);

    [Fact]
    public void PriceImpact_is_body_over_atr()
        => PressureMapCalculator.PriceImpact(100, 102, 4).Should().BeApproximately(0.5, 1e-12);

    // ── BookImbalance ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BookImbalance_empty_book_is_zero()
        => PressureMapCalculator.BookImbalance(0, 0).Should().Be(0);

    [Fact]
    public void BookImbalance_all_bid_is_plus_one()
        => PressureMapCalculator.BookImbalance(500, 0).Should().Be(1.0);

    [Fact]
    public void BookImbalance_all_ask_is_minus_one()
        => PressureMapCalculator.BookImbalance(0, 500).Should().Be(-1.0);

    [Fact]
    public void BookImbalance_balanced_is_zero()
        => PressureMapCalculator.BookImbalance(250, 250).Should().Be(0);

    // ── ATR ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Atr_needs_at_least_two_bars()
        => PressureMapCalculator.Atr(new[] { Bar(100, 101, 99, 100) }, 14).Should().Be(0);

    [Fact]
    public void Atr_averages_true_range()
    {
        // Two bars after the seed: TR each = high-low = 2 (prev close inside range), ATR = 2.
        var bars = new[] { Bar(100, 100, 100, 100), Bar(100, 101, 99, 100), Bar(100, 101, 99, 100) };
        PressureMapCalculator.Atr(bars, 14).Should().BeApproximately(2.0, 1e-12);
    }

    // ── Intensity bands ─────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1.0, 0.15)]
    [InlineData(1.7, 0.35)]
    [InlineData(2.5, 0.60)]
    [InlineData(4.0, 0.80)]
    [InlineData(7.0, 1.00)]
    public void Intensity_bands_scale_with_relative_volume(double relVol, double expected)
        => PressureMapCalculator.Intensity(relVol).Should().Be(expected);

    // ── Classify (each branch) ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Classify_below_min_relvol_is_neutral()
        => PressureMapCalculator.Classify(1.0, 0.9, 0.8, 0.5, 100, 101, 2.0, Opt)
            .Should().Be(PressureSignal.Neutral);

    [Fact]
    public void Classify_bullish_breakthrough()
        => PressureMapCalculator.Classify(3.0, 0.80, 0.70, 0.0, 100, 101, 2.0, Opt)
            .Should().Be(PressureSignal.BullishBreakthrough);

    [Fact]
    public void Classify_bearish_breakdown()
        => PressureMapCalculator.Classify(3.0, 0.20, 0.70, 0.0, 101, 100, 2.0, Opt)
            .Should().Be(PressureSignal.BearishBreakdown);

    [Fact]
    public void Classify_bullish_absorption()
        => PressureMapCalculator.Classify(3.0, 0.60, 0.20, 0.20, 100, 100, 2.0, Opt)
            .Should().Be(PressureSignal.BullishAbsorption);

    [Fact]
    public void Classify_bearish_absorption()
        => PressureMapCalculator.Classify(3.0, 0.40, 0.20, -0.20, 100, 100, 2.0, Opt)
            .Should().Be(PressureSignal.BearishAbsorption);

    [Fact]
    public void Classify_no_conditions_met_is_neutral()
        => PressureMapCalculator.Classify(3.0, 0.50, 0.50, 0.0, 100, 100, 2.0, Opt)
            .Should().Be(PressureSignal.Neutral);

    [Fact]
    public void Classify_breakthrough_requires_close_above_open()
        // Right candle position + impact but close == open → not a breakthrough.
        => PressureMapCalculator.Classify(3.0, 0.80, 0.70, 0.0, 100, 100, 2.0, Opt)
            .Should().Be(PressureSignal.Neutral);

    // ── Evaluate (end-to-end) ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Evaluate_zero_baseline_yields_neutral()
    {
        var bar = new OhlcvBar(new InstrumentId(1), BarSize.OneMinute, DateTime.UtcNow,
            100, 101, 99, 100.8, 300, BrokerKind.InteractiveBrokers, IsFinal: true);
        var cell = PressureMapCalculator.Evaluate(bar, atr14: 5, baselineVolume: 0,
            bidDepth: 200, askDepth: 100, minRelVol: 2.0, Opt);
        cell.RelativeVolume.Should().Be(0);
        cell.Signal.Should().Be(PressureSignal.Neutral);
    }

    [Fact]
    public void Evaluate_full_bullish_absorption()
    {
        // Vol 300 vs baseline 100 → relVol 3; close near high but small body vs ATR; bid-heavy book.
        var bar = new OhlcvBar(new InstrumentId(1), BarSize.OneMinute, DateTime.UtcNow,
            100, 101, 99.9, 100.8, 300, BrokerKind.InteractiveBrokers, IsFinal: true);
        var cell = PressureMapCalculator.Evaluate(bar, atr14: 5, baselineVolume: 100,
            bidDepth: 200, askDepth: 100, minRelVol: 2.0, Opt);

        cell.RelativeVolume.Should().BeApproximately(3.0, 1e-9);
        cell.CandlePosition.Should().BeApproximately(0.9 / 1.1, 1e-9);
        cell.PriceImpact.Should().BeApproximately(0.8 / 5.0, 1e-9);
        cell.BookImbalance.Should().BeApproximately(1.0 / 3.0, 1e-9);
        cell.Intensity.Should().Be(0.80);
        cell.Signal.Should().Be(PressureSignal.BullishAbsorption);
    }
}
