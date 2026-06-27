using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData.AdvancedRegime;
using Xunit;

namespace TradingTerminal.Tests.Regime;

public sealed class AdvancedRegimeCalculatorTests
{
    private static readonly DateTime T0 = new(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc);
    private static readonly AdvancedTimeframe Tf1m = new("1m", TimeSpan.FromMinutes(1), Enabled: true);

    /// <summary>Strictly rising up-candles, 1m apart, all within one UTC session day.</summary>
    private static List<Bar> Rising(int n, double start = 100, double step = 0.5)
    {
        var bars = new List<Bar>(n);
        for (var i = 0; i < n; i++)
        {
            var close = start + step * i;
            bars.Add(new Bar(T0.AddMinutes(i), close - 0.4, close + 0.1, close - 0.5, close, 100));
        }
        return bars;
    }

    /// <summary>Strictly falling down-candles, 1m apart, all within one UTC session day.</summary>
    private static List<Bar> Falling(int n, double start = 200, double step = 0.5)
    {
        var bars = new List<Bar>(n);
        for (var i = 0; i < n; i++)
        {
            var close = start - step * i;
            bars.Add(new Bar(T0.AddMinutes(i), close + 0.4, close + 0.5, close - 0.1, close, 100));
        }
        return bars;
    }

    private static AdvancedRegimeColumn Compute(IReadOnlyList<Bar> bars, AdvancedRegimeSettings? settings = null)
    {
        var snapshot = AdvancedRegimeCalculator.Compute(
            "TEST",
            new[] { (Tf1m, bars) },
            settings ?? AdvancedRegimeSettings.Default,
            DateTime.UtcNow);
        snapshot.Columns.Should().HaveCount(1);
        return snapshot.Columns[0];
    }

    private static AdvancedRegimeCell Cell(AdvancedRegimeColumn column, AdvancedIndicatorRow row) =>
        column.Cells.Single(c => c.Row == row);

    // ------------------------------------------------------------------ indicators

    [Fact]
    public void Cci_of_three_equal_hlc_bars_is_exactly_100()
    {
        // TP = 1, 2, 3 → SMA 2, MAD 2/3 → CCI = (3-2)/(0.015 · 2/3) = 100.
        var bars = new List<Bar>
        {
            new(T0, 1, 1, 1, 1, 10),
            new(T0.AddMinutes(1), 2, 2, 2, 2, 10),
            new(T0.AddMinutes(2), 3, 3, 3, 3, 10),
        };
        AdvancedRegimeBarIndicators.Cci(bars, 3).Should().BeApproximately(100.0, 1e-9);
    }

    [Fact]
    public void Wilder_atr_on_constant_range_bars_equals_the_range()
    {
        // Flat closes with H-L = 2 → TR = 2 every bar → ATR = 2 from the seed bar onward.
        var bars = new List<Bar>();
        for (var i = 0; i < 30; i++)
            bars.Add(new Bar(T0.AddMinutes(i), 100, 101, 99, 100, 10));

        var atr = AdvancedRegimeBarIndicators.TrueRangeAtr(bars, 14);

        atr[12].Should().Be(double.NaN);
        atr[13].Should().BeApproximately(2.0, 1e-9);
        atr[^1].Should().BeApproximately(2.0, 1e-9);
    }

    [Fact]
    public void Macd_of_constant_series_is_zero()
    {
        var closes = Enumerable.Repeat(100.0, 40).ToArray();
        var (line, signal, hist) = AdvancedRegimeBarIndicators.Macd(closes, 12, 26, 9);

        line.Should().BeApproximately(0, 1e-9);
        signal.Should().BeApproximately(0, 1e-9);
        hist.Should().BeApproximately(0, 1e-9);
    }

    [Fact]
    public void Macd_line_is_positive_on_a_rising_series()
    {
        var closes = Enumerable.Range(0, 60).Select(i => 100.0 + i).ToArray();
        var (line, _, _) = AdvancedRegimeBarIndicators.Macd(closes, 12, 26, 9);
        line.Should().BePositive();
    }

    [Fact]
    public void SuperTrend_is_bullish_below_price_on_rising_and_bearish_above_on_falling()
    {
        var (lineUp, bullUp) = AdvancedRegimeBarIndicators.SuperTrend(Rising(60), 3.0, 10);
        bullUp.Should().BeTrue();
        lineUp.Should().BeLessThan(Rising(60)[^1].Close);

        var (lineDown, bullDown) = AdvancedRegimeBarIndicators.SuperTrend(Falling(60), 3.0, 10);
        bullDown.Should().BeFalse();
        lineDown.Should().BeGreaterThan(Falling(60)[^1].Close);
    }

    [Fact]
    public void Session_vwap_is_volume_weighted_and_ignores_prior_days()
    {
        var bars = new List<Bar>
        {
            // Prior-day bar with huge volume must be excluded from the session.
            new(T0.AddDays(-1), 100, 100, 100, 100, 1_000_000),
            new(T0, 10, 10, 10, 10, 100),
            new(T0.AddMinutes(1), 20, 20, 20, 20, 300),
        };
        // (10·100 + 20·300) / 400 = 17.5
        AdvancedRegimeBarIndicators.SessionVwap(bars).Should().BeApproximately(17.5, 1e-9);
    }

    [Fact]
    public void Poc_is_hlc3_of_the_highest_volume_bar_in_the_lookback()
    {
        var bars = new List<Bar>
        {
            new(T0, 1, 1, 1, 1, 50),
            new(T0.AddMinutes(1), 5, 6, 4, 5, 900),
            new(T0.AddMinutes(2), 9, 9, 9, 9, 100),
        };
        AdvancedRegimeBarIndicators.PocApprox(bars, 3).Should().BeApproximately(5.0, 1e-9);
    }

    [Fact]
    public void Range_position_is_100_at_the_top_and_50_when_flat()
    {
        var rising = Enumerable.Range(1, 20)
            .Select(i => new Bar(T0.AddMinutes(i), i, i, i, i, 10))
            .ToList();
        AdvancedRegimeBarIndicators.RangePosition(rising, 20).Should().BeApproximately(100.0, 1e-9);

        var flat = Enumerable.Range(0, 20)
            .Select(i => new Bar(T0.AddMinutes(i), 5, 5, 5, 5, 10))
            .ToList();
        AdvancedRegimeBarIndicators.RangePosition(flat, 20).Should().Be(50);
    }

    [Fact]
    public void Bar_delta_is_signed_volume_by_candle_direction()
    {
        AdvancedRegimeBarIndicators.BarDelta(new Bar(T0, 10, 12, 9, 11, 500)).Should().Be(500);
        AdvancedRegimeBarIndicators.BarDelta(new Bar(T0, 11, 12, 9, 10, 500)).Should().Be(-500);
        AdvancedRegimeBarIndicators.BarDelta(new Bar(T0, 10, 12, 9, 10, 500)).Should().Be(0);
    }

    // ------------------------------------------------------------------ composite trend

    [Fact]
    public void All_bullish_series_scores_plus_8_with_needle_at_plus_90()
    {
        var column = Compute(Rising(80));

        column.TrendScore.Should().Be(8);
        column.NeedleAngleDegrees.Should().Be(90);

        var trend = Cell(column, AdvancedIndicatorRow.Trend);
        trend.Glyph.Should().Be("▲▲▲ +90°");
        trend.Signal.Should().Be(CellSignal.StrongUp);
        trend.Value.Should().Be(8);
        trend.ValueSuffix.Should().Be("/8");
    }

    [Fact]
    public void All_bearish_series_scores_minus_8_with_needle_at_minus_90()
    {
        var column = Compute(Falling(80));

        column.TrendScore.Should().Be(-8);
        column.NeedleAngleDegrees.Should().Be(-90);

        var trend = Cell(column, AdvancedIndicatorRow.Trend);
        trend.Glyph.Should().Be("▼▼▼ -90°");
        trend.Signal.Should().Be(CellSignal.StrongDown);
        trend.Value.Should().Be(-8);
    }

    // ------------------------------------------------------------------ cell classification

    [Fact]
    public void Rising_series_classifies_rsi_overbought_and_stacked_mas()
    {
        var column = Compute(Rising(80));

        // Monotonic gains → RSI 100 → overbought is a SELL warning (strong down).
        var rsi = Cell(column, AdvancedIndicatorRow.Rsi);
        rsi.Glyph.Should().Be("▼ OB");
        rsi.Signal.Should().Be(CellSignal.StrongDown);

        Cell(column, AdvancedIndicatorRow.Ma9).Signal.Should().Be(CellSignal.Up);
        Cell(column, AdvancedIndicatorRow.Ma21).Signal.Should().Be(CellSignal.Up);
        Cell(column, AdvancedIndicatorRow.Ma50).Signal.Should().Be(CellSignal.Up);

        var tripleMa = Cell(column, AdvancedIndicatorRow.TripleMa);
        tripleMa.Glyph.Should().Be("▲▲ Stack");
        tripleMa.Signal.Should().Be(CellSignal.StrongUp);

        Cell(column, AdvancedIndicatorRow.Vwap).Signal.Should().Be(CellSignal.Up);
        Cell(column, AdvancedIndicatorRow.SuperTrend).Glyph.Should().Be("▲ Bull");

        // Linear rise → CCI ≈ +126 (> 100) → strong.
        Cell(column, AdvancedIndicatorRow.Cci).Glyph.Should().Be("▲▲ Strong");

        // All up-candles → positive delta, cumulative delta, and buy-side volume.
        Cell(column, AdvancedIndicatorRow.Delta).Signal.Should().Be(CellSignal.Up);
        Cell(column, AdvancedIndicatorRow.CumulativeDelta).Signal.Should().Be(CellSignal.Up);
        Cell(column, AdvancedIndicatorRow.VolumeBuySell).Glyph.Should().Be("▲ Buy");

        // Close pinned at the top of the 20-bar range with bullish 3-candle bias.
        var trd = Cell(column, AdvancedIndicatorRow.TrendRange);
        trd.Glyph.Should().Be("▲▲ Top");
        trd.Signal.Should().Be(CellSignal.StrongUp);
    }

    [Fact]
    public void Falling_series_classifies_rsi_oversold_and_bearish_stack()
    {
        var column = Compute(Falling(80));

        // Monotonic losses → RSI 0 → oversold is a BUY warning (strong up).
        var rsi = Cell(column, AdvancedIndicatorRow.Rsi);
        rsi.Glyph.Should().Be("▲ OS");
        rsi.Signal.Should().Be(CellSignal.StrongUp);

        Cell(column, AdvancedIndicatorRow.TripleMa).Glyph.Should().Be("▼▼ Stack");
        Cell(column, AdvancedIndicatorRow.SuperTrend).Glyph.Should().Be("▼ Bear");
        Cell(column, AdvancedIndicatorRow.Cci).Glyph.Should().Be("▼▼ Strong");
        Cell(column, AdvancedIndicatorRow.VolumeBuySell).Glyph.Should().Be("▼ Sell");

        var trd = Cell(column, AdvancedIndicatorRow.TrendRange);
        trd.Glyph.Should().Be("▼▼ Bot");
        trd.Signal.Should().Be(CellSignal.StrongDown);
    }

    [Fact]
    public void Insufficient_data_yields_neutral_em_dash_cells()
    {
        var column = Compute(Rising(5));

        var rsi = Cell(column, AdvancedIndicatorRow.Rsi);
        rsi.Glyph.Should().Be("—");
        rsi.Value.Should().BeNull();
        rsi.Signal.Should().Be(CellSignal.Neutral);

        Cell(column, AdvancedIndicatorRow.Ma50).Glyph.Should().Be("—");
        Cell(column, AdvancedIndicatorRow.Trend).Glyph.Should().Be("—");
        column.TrendScore.Should().Be(0);
        column.NeedleAngleDegrees.Should().Be(0);
    }

    [Fact]
    public void Disabled_rows_are_omitted_and_order_follows_the_enum()
    {
        var settings = AdvancedRegimeSettings.Default;
        settings.EnableRsi = false;
        settings.EnableSuperTrend = false;

        var column = Compute(Rising(80), settings);

        column.Cells.Should().HaveCount(16);
        column.Cells.Select(c => c.Row).Should().NotContain(AdvancedIndicatorRow.Rsi);
        column.Cells.Select(c => c.Row).Should().NotContain(AdvancedIndicatorRow.SuperTrend);
        column.Cells.Select(c => c.Row).Should().BeInAscendingOrder();
    }

    [Fact]
    public void All_rows_enabled_emits_18_cells_in_enum_order()
    {
        var column = Compute(Rising(80));
        column.Cells.Should().HaveCount(18);
        column.Cells.Select(c => c.Row).Should().BeInAscendingOrder();
    }
}
