namespace TradingTerminal.Sandbox.Portfolio.Tests;

public sealed class ModelPortfolioSimulatorTests
{
    private const double StartingEquity = 100_000d;
    private const double BasisPoint = 0.0001d;

    [Fact]
    public void TryCreate_rejects_a_null_declaration()
    {
        var fault = ModelPortfolioSimulator.TryCreate(null, null, out var simulator);

        Assert.Equal(ModelPortfolioFault.InvalidConfiguration, fault);
        Assert.Null(simulator);
    }

    [Theory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(101, 1)]
    [InlineData(1, -1)]
    [InlineData(1, 0)]
    [InlineData(1, 257)]
    public void TryCreate_rejects_out_of_range_bounds(int maxAbsoluteUnits, int retainedClosedTrips)
    {
        var fault = ModelPortfolioSimulator.TryCreate(
            maxAbsoluteUnits,
            retainedClosedTrips,
            out var simulator);

        Assert.Equal(ModelPortfolioFault.InvalidConfiguration, fault);
        Assert.Null(simulator);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 256)]
    public void TryCreate_accepts_boundary_configuration(int maxAbsoluteUnits, int retainedClosedTrips)
    {
        var simulator = Create(maxAbsoluteUnits, retainedClosedTrips);

        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(StartingEquity, simulator.CommittedSnapshot.Equity);
        Assert.Equal(StartingEquity, simulator.CommittedSnapshot.EquityPeak);
        Assert.False(simulator.CommittedSnapshot.IsCompleted);
    }

    [Fact]
    public void Flat_and_empty_history_reads_return_normative_zero_values()
    {
        var simulator = Create();

        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var position));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpEntry(out var entry));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpBarsHeld(out var barsHeld));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpEquity(out var equity));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeCount(out var tradeCount));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStreak(out var streak));

        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(position));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(entry));
        Assert.Equal(0L, barsHeld);
        Assert.Equal(StartingEquity, equity);
        Assert.Equal(0L, tradeCount);
        Assert.Equal(0L, streak);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MpMarket_invalid_units_fault_the_callback_and_rollback_all_staged_work(double units)
    {
        var simulator = Create();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));

        Assert.Equal(ModelPortfolioFault.InvalidMarketUnits, simulator.MpMarket(units, out var fill));
        Assert.Equal(ModelPortfolioFault.InvalidMarketUnits, simulator.MpPosition(out _));
        Assert.Equal(ModelPortfolioFault.InvalidMarketUnits, simulator.CommitCallback());
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(fill));

        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var committedPosition));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(committedPosition));
        Assert.Equal(0d, simulator.CommittedSnapshot.CommissionTotal);
        Assert.Equal(0L, simulator.CommittedSnapshot.RetainedTradeCount);
    }

    [Fact]
    public void RollbackCallback_discards_staged_mutations_and_reads_revert_to_committed_state()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(2d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var stagedPosition));
        Assert.Equal(2d, stagedPosition);
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);

        simulator.RollbackCallback();

        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var committedPosition));
        Assert.Equal(0d, committedPosition);
        Assert.Equal(StartingEquity, simulator.CommittedSnapshot.Equity);
        Assert.Equal(0d, simulator.CommittedSnapshot.CommissionTotal);
    }

    [Fact]
    public void Faulted_OnBar_preserves_the_step_four_bar_increment_but_discards_staged_fills()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 1d, 100d);
        BeginBar(simulator, 101d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));

        Assert.Equal(
            ModelPortfolioFault.InvalidMarketUnits,
            simulator.MpMarket(double.NaN, out _));
        Assert.Equal(ModelPortfolioFault.InvalidMarketUnits, simulator.CommitCallback());

        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(1L, simulator.CommittedSnapshot.BarsHeld);
        Assert.Equal(0d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Assert.Equal(0L, simulator.CommittedSnapshot.RetainedTradeCount);
    }

    [Fact]
    public void Faulted_callback_does_not_advance_staged_lifetime_trip_counters()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 1d, 100d);
        BeginBar(simulator, 101d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));

        Assert.Equal(
            ModelPortfolioFault.InvalidMarketUnits,
            simulator.MpMarket(double.NaN, out _));
        Assert.Equal(ModelPortfolioFault.InvalidMarketUnits, simulator.CommitCallback());

        var snapshot = simulator.CommittedSnapshot;
        Assert.Equal(1d, snapshot.PositionUnits);
        Assert.Equal(0L, snapshot.LifetimeClosedTripCount);
        Assert.Equal(0L, snapshot.LifetimeWinningTripCount);
        Assert.Equal(0L, snapshot.LifetimeLosingTripCount);
        Assert.Equal(0L, snapshot.RetainedTradeCount);

        BeginBar(simulator, 101d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));
        Commit(simulator);

        snapshot = simulator.CommittedSnapshot;
        Assert.Equal(1L, snapshot.LifetimeClosedTripCount);
        Assert.Equal(1L, snapshot.LifetimeWinningTripCount);
        Assert.Equal(0L, snapshot.LifetimeLosingTripCount);
    }

    [Theory]
    [InlineData(-0.1d)]
    [InlineData(0d)]
    [InlineData(1.0000000000000002d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MpClose_invalid_fraction_faults_and_preserves_the_committed_position(double fraction)
    {
        var simulator = Create();
        OpenAndCommit(simulator, 1d, 100d);
        BeginBar(simulator, 100d);

        Assert.Equal(
            ModelPortfolioFault.InvalidCloseFraction,
            simulator.MpClose(fraction, out _));
        Assert.Equal(ModelPortfolioFault.InvalidCloseFraction, simulator.CommitCallback());

        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var position));
        Assert.Equal(1d, position);
        Assert.Equal(0L, simulator.CommittedSnapshot.RetainedTradeCount);
    }

    [Fact]
    public void MpClose_while_flat_faults()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.CloseWhileFlat, simulator.MpClose(1d, out var fill));
        Assert.Equal(ModelPortfolioFault.CloseWhileFlat, simulator.CommitCallback());
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(fill));
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void Trade_reads_reject_negative_and_out_of_range_indices()
    {
        var simulator = Create();

        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeUnits(-1, out _));
        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeUnits(0, out _));
        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeBars(-1, out _));
        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeBars(0, out _));

        ExecuteRoundTrip(simulator, 1d, 100d, 101d);

        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeUnits(1, out _));
        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeBars(1, out _));
    }

    [Fact]
    public void Maximum_units_soft_rejection_returns_zero_and_preserves_staged_state()
    {
        var simulator = Create(maxAbsoluteUnits: 3);
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(3d, out var acceptedFill));
        Assert.Equal(100d, acceptedFill);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out var rejectedFill));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(rejectedFill));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var stagedPosition));
        Assert.Equal(3d, stagedPosition);
        Commit(simulator);

        Assert.Equal(3d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(30d, simulator.CommittedSnapshot.PositionQuantity);
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void BeginOnBar_rejects_invalid_reference_prices_without_mutation(double referencePrice)
    {
        var simulator = Create();

        Assert.Equal(ModelPortfolioFault.InvalidReferencePrice, simulator.BeginOnBar(referencePrice));
        Assert.Equal(StartingEquity, simulator.CommittedSnapshot.Equity);
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void BeginOnTick_rejects_crossed_quotes_and_an_invalid_fallback()
    {
        var crossed = Create();
        var invalid = Create();

        Assert.Equal(ModelPortfolioFault.CrossedQuote, crossed.BeginOnTick(101d, 100d, 99d));
        Assert.Equal(
            ModelPortfolioFault.InvalidReferencePrice,
            invalid.BeginOnTick(double.NaN, double.PositiveInfinity, 0d));
        Assert.Equal(0d, crossed.CommittedSnapshot.PositionUnits);
        Assert.Equal(0d, invalid.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void BeginOnTick_uses_the_exact_overflow_safe_midpoint_and_valid_last_fallback()
    {
        var midpointSimulator = Create();
        var bid = double.MaxValue / 2d;
        var ask = double.MaxValue;
        var expectedMidpoint = bid + ((ask - bid) / 2d);

        Assert.True(double.IsFinite(expectedMidpoint));
        Assert.Equal(ModelPortfolioFault.None, midpointSimulator.BeginOnTick(bid, ask, 1d));
        Assert.Equal(ModelPortfolioFault.None, midpointSimulator.MpMarket(1d, out var midpointFill));
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(expectedMidpoint),
            BitConverter.DoubleToUInt64Bits(midpointFill));
        Commit(midpointSimulator);

        var fallbackSimulator = Create();
        Assert.Equal(
            ModelPortfolioFault.None,
            fallbackSimulator.BeginOnTick(double.NaN, 5d, 123d));
        Assert.Equal(ModelPortfolioFault.None, fallbackSimulator.MpMarket(1d, out var fallbackFill));
        Assert.Equal(123d, fallbackFill);
        Commit(fallbackSimulator);
    }

    [Fact]
    public void Open_and_scale_in_track_units_quantity_average_price_costs_and_equity()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 2d, 100d);

        var firstQuantity = 2d * 1_000d / 100d;
        var firstCost = FillCost(firstQuantity, 100d);
        Assert.Equal(2d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(firstQuantity, simulator.CommittedSnapshot.PositionQuantity);
        Assert.Equal(100d, simulator.CommittedSnapshot.AverageEntryPrice);
        Assert.Equal(firstCost, simulator.CommittedSnapshot.CommissionTotal);
        Assert.Equal(firstCost, simulator.CommittedSnapshot.SlippageTotal);

        BeginBar(simulator, 200d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpEquity(out var stagedEquity));
        Commit(simulator);

        var secondQuantity = 1d * 1_000d / 200d;
        var totalQuantity = firstQuantity + secondQuantity;
        var averageEntry = ((firstQuantity * 100d) + (secondQuantity * 200d)) / totalQuantity;
        var secondCost = FillCost(secondQuantity, 200d);
        var totalCost = firstCost + secondCost;
        var expectedEquity = MarkedEquity(0d, totalQuantity, 200d, averageEntry, totalCost, totalCost);
        var preScalePeak = MarkedEquity(0d, firstQuantity, 200d, 100d, firstCost, firstCost);
        var postOpenEquity = MarkedEquity(0d, firstQuantity, 100d, 100d, firstCost, firstCost);
        var expectedDrawdown = Math.Max(
            StartingEquity - postOpenEquity,
            preScalePeak - expectedEquity);

        Assert.Equal(3d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(totalQuantity, simulator.CommittedSnapshot.PositionQuantity);
        Assert.Equal(averageEntry, simulator.CommittedSnapshot.AverageEntryPrice);
        Assert.Equal(totalCost, simulator.CommittedSnapshot.CommissionTotal);
        Assert.Equal(totalCost, simulator.CommittedSnapshot.SlippageTotal);
        Assert.Equal(expectedEquity, stagedEquity);
        Assert.Equal(expectedEquity, simulator.CommittedSnapshot.Equity);
        Assert.Equal(preScalePeak, simulator.CommittedSnapshot.EquityPeak);
        Assert.Equal(expectedDrawdown, simulator.CommittedSnapshot.MaximumDrawdown);
    }

    [Fact]
    public void Partial_reductions_record_no_trip_until_exact_flat()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 4d, 100d);

        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(0.25d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeCount(out var stagedTradeCount));
        Assert.Equal(0L, stagedTradeCount);
        Commit(simulator);

        Assert.Equal(3d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(30d, simulator.CommittedSnapshot.PositionQuantity);
        Assert.Equal(100d, simulator.CommittedSnapshot.AverageEntryPrice);
        Assert.Equal(100d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Assert.Equal(0L, simulator.CommittedSnapshot.RetainedTradeCount);

        BeginBar(simulator, 120d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));
        Commit(simulator);

        var entryCost = FillCost(40d, 100d);
        var firstExitCost = FillCost(10d, 110d);
        var finalExitCost = FillCost(30d, 120d);
        var totalCost = (entryCost + firstExitCost) + finalExitCost;
        var expectedEquity = MarkedEquity(700d, 0d, 120d, 0d, totalCost, totalCost);

        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.PositionUnits));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.PositionQuantity));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.AverageEntryPrice));
        Assert.Equal(700d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Assert.Equal(totalCost, simulator.CommittedSnapshot.CommissionTotal);
        Assert.Equal(totalCost, simulator.CommittedSnapshot.SlippageTotal);
        Assert.Equal(expectedEquity, simulator.CommittedSnapshot.Equity);
        Assert.Equal(1L, simulator.CommittedSnapshot.RetainedTradeCount);
        Assert.Equal(1L, simulator.CommittedSnapshot.Streak);
        AssertTrade(simulator, 0, 4d, 2L);
    }

    [Fact]
    public void Reversal_records_exactly_one_trip_and_opens_the_remainder_at_the_fill_price()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 3d, 100d);

        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(-5d, out var fill));
        Assert.Equal(110d, fill);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeCount(out var stagedTradeCount));
        Assert.Equal(1L, stagedTradeCount);
        Commit(simulator);

        Assert.Equal(-2d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(-2d * 1_000d / 110d, simulator.CommittedSnapshot.PositionQuantity);
        Assert.Equal(110d, simulator.CommittedSnapshot.AverageEntryPrice);
        Assert.Equal(300d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Assert.Equal(1L, simulator.CommittedSnapshot.RetainedTradeCount);
        AssertTrade(simulator, 0, 3d, 1L);
    }

    [Fact]
    public void Position_units_remain_anchored_when_only_the_reference_price_moves()
    {
        var simulator = Create(maxAbsoluteUnits: 3);
        OpenAndCommit(simulator, 3d, 91d);
        var quantity = simulator.CommittedSnapshot.PositionQuantity;

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnTick(1_000d, 1_002d, 1_001d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var stagedPosition));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpEntry(out var stagedEntry));
        Commit(simulator);

        Assert.Equal(3d, stagedPosition);
        Assert.Equal(91d, stagedEntry);
        Assert.Equal(3d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(quantity, simulator.CommittedSnapshot.PositionQuantity);
        Assert.Equal(91d, simulator.CommittedSnapshot.AverageEntryPrice);
    }

    [Fact]
    public void Bars_held_increment_only_on_OnBar_while_open_and_are_recorded_on_close()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 1d, 100d);
        Assert.Equal(0L, simulator.CommittedSnapshot.BarsHeld);

        BeginTick(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpBarsHeld(out var afterTick));
        Assert.Equal(0L, afterTick);
        Commit(simulator);

        BeginBar(simulator, 101d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpBarsHeld(out var afterFirstBar));
        Assert.Equal(1L, afterFirstBar);
        Commit(simulator);

        BeginTick(simulator, 101d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpBarsHeld(out var afterSecondTick));
        Assert.Equal(1L, afterSecondTick);
        Commit(simulator);

        BeginBar(simulator, 102d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpBarsHeld(out var beforeClose));
        Assert.Equal(2L, beforeClose);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));
        Commit(simulator);

        Assert.Equal(0L, simulator.CommittedSnapshot.BarsHeld);
        AssertTrade(simulator, 0, 1d, 2L);
    }

    [Fact]
    public void Closed_trip_ring_evicts_oldest_and_indexes_newest_first()
    {
        var simulator = Create(retainedClosedTrips: 2);

        ExecuteRoundTrip(simulator, 1d, 100d, 101d);
        ExecuteRoundTrip(simulator, 2d, 100d, 102d);
        ExecuteRoundTrip(simulator, 3d, 100d, 103d);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeCount(out var count));
        Assert.Equal(2L, count);
        AssertTrade(simulator, 0, 3d, 1L);
        AssertTrade(simulator, 1, 2d, 1L);
        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeUnits(2, out _));
        Assert.Equal(ModelPortfolioFault.TradeIndexOutOfRange, simulator.MpTradeBars(2, out _));
    }

    [Fact]
    public void Lifetime_trip_counters_keep_advancing_after_the_retained_ring_saturates()
    {
        var simulator = Create(retainedClosedTrips: 2);

        ExecuteRoundTrip(simulator, 1d, 100d, 101d);
        ExecuteRoundTrip(simulator, 1d, 100d, 99d);
        ExecuteRoundTrip(simulator, 0.01d, 469d, 469.1876375275055d);
        ExecuteRoundTrip(simulator, 1d, 100d, 102d);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeCount(out var retainedCount));
        Assert.Equal(2L, retainedCount);

        var snapshot = simulator.CommittedSnapshot;
        Assert.Equal(2L, snapshot.RetainedTradeCount);
        Assert.Equal(4L, snapshot.LifetimeClosedTripCount);
        Assert.Equal(2L, snapshot.LifetimeWinningTripCount);
        Assert.Equal(1L, snapshot.LifetimeLosingTripCount);
    }

    [Fact]
    public void Streak_tracks_direction_changes_and_an_exact_zero_net_trip_resets_it()
    {
        var simulator = Create(retainedClosedTrips: 8);

        ExecuteRoundTrip(simulator, 1d, 100d, 101d);
        AssertStreak(simulator, 1L);
        ExecuteRoundTrip(simulator, 1d, 100d, 102d);
        AssertStreak(simulator, 2L);
        ExecuteRoundTrip(simulator, 1d, 100d, 99d);
        AssertStreak(simulator, -1L);
        ExecuteRoundTrip(simulator, 1d, 100d, 98d);
        AssertStreak(simulator, -2L);

        // This vector evaluates to exactly zero only under the frozen left-to-right net expression.
        ExecuteRoundTrip(simulator, 0.01d, 469d, 469.1876375275055d);

        AssertStreak(simulator, 0L);
    }

    [Fact]
    public void Equity_and_drawdown_sample_before_bytecode_and_after_committed_fills()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 1d, 100d);

        var entryCost = FillCost(10d, 100d);
        var postOpenEquity = MarkedEquity(0d, 10d, 100d, 100d, entryCost, entryCost);
        Assert.Equal(
            StartingEquity - postOpenEquity,
            simulator.CommittedSnapshot.MaximumDrawdown);

        BeginBar(simulator, 110d);
        Commit(simulator);
        var peak = MarkedEquity(0d, 10d, 110d, 100d, entryCost, entryCost);
        Assert.Equal(peak, simulator.CommittedSnapshot.EquityPeak);

        BeginBar(simulator, 90d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpEquity(out var beforeClose));
        var expectedBeforeClose = MarkedEquity(0d, 10d, 90d, 100d, entryCost, entryCost);
        Assert.Equal(expectedBeforeClose, beforeClose);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpEquity(out var afterClose));
        var exitCost = FillCost(10d, 90d);
        var totalCost = entryCost + exitCost;
        var expectedAfterClose = MarkedEquity(-100d, 0d, 90d, 0d, totalCost, totalCost);
        Assert.Equal(expectedAfterClose, afterClose);
        Commit(simulator);

        Assert.Equal(expectedAfterClose, simulator.CommittedSnapshot.Equity);
        Assert.Equal(peak, simulator.CommittedSnapshot.EquityPeak);
        Assert.Equal(peak - expectedAfterClose, simulator.CommittedSnapshot.MaximumDrawdown);
    }

    [Fact]
    public void CompleteRun_liquidates_at_the_most_recent_valid_price_after_an_invalid_callback()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 2d, 100d);

        BeginBar(simulator, 110d);
        Commit(simulator);
        Assert.Equal(ModelPortfolioFault.InvalidReferencePrice, simulator.BeginOnBar(double.NaN));

        Assert.Equal(ModelPortfolioFault.None, simulator.CompleteRun());

        var entryCost = FillCost(20d, 100d);
        var exitCost = FillCost(20d, 110d);
        var totalCost = entryCost + exitCost;
        var expectedEquity = MarkedEquity(200d, 0d, 110d, 0d, totalCost, totalCost);
        Assert.True(simulator.CommittedSnapshot.IsCompleted);
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.PositionUnits));
        Assert.Equal(200d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Assert.Equal(totalCost, simulator.CommittedSnapshot.CommissionTotal);
        Assert.Equal(totalCost, simulator.CommittedSnapshot.SlippageTotal);
        Assert.Equal(expectedEquity, simulator.CommittedSnapshot.Equity);
        Assert.Equal(1L, simulator.CommittedSnapshot.LifetimeClosedTripCount);
        Assert.Equal(1L, simulator.CommittedSnapshot.LifetimeWinningTripCount);
        Assert.Equal(0L, simulator.CommittedSnapshot.LifetimeLosingTripCount);
        Assert.Equal(1L, simulator.CommittedSnapshot.RetainedTradeCount);
        AssertTrade(simulator, 0, 2d, 1L);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var position));
        Assert.Equal(0d, position);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeCount(out var count));
        Assert.Equal(1L, count);
    }

    [Fact]
    public void MpClose_lowers_fraction_to_units_before_proportional_quantity_reduction()
    {
        var simulator = Create();
        OpenAndCommit(simulator, 3d, 91d);

        BeginBar(simulator, 92d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(0.1d, out _));
        Commit(simulator);

        Assert.Equal(
            0x400A5FA5FA5FA5FBUL,
            BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.RealizedGrossProfitLoss));
        Assert.Equal(2.7d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void MpClose_valid_subnormal_fraction_that_lowers_to_zero_is_a_successful_no_op()
    {
        var simulator = Create();
        OpenAndCommit(simulator, double.Epsilon, 1d);
        var quantity = simulator.CommittedSnapshot.PositionQuantity;

        BeginBar(simulator, 1d);
        Assert.Equal(
            ModelPortfolioFault.None,
            simulator.MpClose(double.Epsilon, out var fill));
        Assert.Equal(1d, fill);
        Commit(simulator);

        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(double.Epsilon),
            BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.PositionUnits));
        Assert.Equal(
            BitConverter.DoubleToUInt64Bits(quantity),
            BitConverter.DoubleToUInt64Bits(simulator.CommittedSnapshot.PositionQuantity));
        Assert.Equal(0L, simulator.CommittedSnapshot.RetainedTradeCount);
    }

    [Fact]
    public void Finite_inputs_that_overflow_an_intermediate_fault_and_rollback()
    {
        var simulator = Create();
        BeginBar(simulator, double.Epsilon);

        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(StartingEquity, simulator.CommittedSnapshot.Equity);
    }

    [Fact]
    public void Callback_lifecycle_faults_are_values_and_completion_is_terminal_for_writes()
    {
        var simulator = Create();

        Assert.Equal(ModelPortfolioFault.InvalidCallbackState, simulator.CommitCallback());
        Assert.Equal(ModelPortfolioFault.InvalidCallbackState, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.CompleteRun());
        Assert.True(simulator.CommittedSnapshot.IsCompleted);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var position));
        Assert.Equal(0d, position);
        Assert.Equal(ModelPortfolioFault.RunCompleted, simulator.BeginOnBar(100d));
        Assert.Equal(ModelPortfolioFault.RunCompleted, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.RunCompleted, simulator.CompleteRun());
    }

    [Fact]
    public void Repeated_identical_runs_are_bit_deterministic()
    {
        var expected = RunDeterministicScenario();

        for (var iteration = 0; iteration < 32; iteration++)
            Assert.Equal(expected, RunDeterministicScenario());
    }


    private static ModelPortfolioSimulator Create(
        int maxAbsoluteUnits = 10,
        int retainedClosedTrips = 4)
    {
        var fault = ModelPortfolioSimulator.TryCreate(
            maxAbsoluteUnits,
            retainedClosedTrips,
            out var simulator);

        Assert.Equal(ModelPortfolioFault.None, fault);
        return Assert.IsType<ModelPortfolioSimulator>(simulator);
    }

    private static void BeginBar(ModelPortfolioSimulator simulator, double referencePrice) =>
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(referencePrice));

    private static void BeginTick(ModelPortfolioSimulator simulator, double referencePrice) =>
        Assert.Equal(
            ModelPortfolioFault.None,
            simulator.BeginOnTick(referencePrice, referencePrice, referencePrice));

    private static void Commit(ModelPortfolioSimulator simulator) =>
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

    private static void OpenAndCommit(
        ModelPortfolioSimulator simulator,
        double units,
        double referencePrice)
    {
        BeginBar(simulator, referencePrice);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(units, out var fill));
        Assert.Equal(referencePrice, fill);
        Commit(simulator);
    }

    private static void ExecuteRoundTrip(
        ModelPortfolioSimulator simulator,
        double units,
        double entryPrice,
        double exitPrice)
    {
        OpenAndCommit(simulator, units, entryPrice);
        BeginBar(simulator, exitPrice);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out var fill));
        Assert.Equal(exitPrice, fill);
        Commit(simulator);
    }

    private static void AssertTrade(
        ModelPortfolioSimulator simulator,
        long index,
        double units,
        long bars)
    {
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeUnits(index, out var actualUnits));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeBars(index, out var actualBars));
        Assert.Equal(units, actualUnits);
        Assert.Equal(bars, actualBars);
    }

    private static void AssertStreak(ModelPortfolioSimulator simulator, long expected)
    {
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStreak(out var actual));
        Assert.Equal(expected, actual);
    }

    private static double FillCost(double quantity, double referencePrice)
    {
        var notional = Math.Abs(quantity) * referencePrice;
        var basisPointNotional = notional * 1d;
        return basisPointNotional * BasisPoint;
    }

    private static double MarkedEquity(
        double realizedGross,
        double quantity,
        double referencePrice,
        double averageEntryPrice,
        double commissionTotal,
        double slippageTotal)
    {
        var unrealized = quantity == 0d ? 0d : quantity * (referencePrice - averageEntryPrice);
        var equity = StartingEquity + realizedGross;
        equity += unrealized;
        equity -= commissionTotal;
        equity -= slippageTotal;
        return equity;
    }

    private static long[] RunDeterministicScenario()
    {
        var simulator = Create(maxAbsoluteUnits: 5, retainedClosedTrips: 3);

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnTick(99d, 101d, 100d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(2d, out _));
        Commit(simulator);

        BeginBar(simulator, 105d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(0.25d, out _));
        Commit(simulator);

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnTick(90d, 92d, 91d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(-4d, out _));
        Commit(simulator);

        BeginBar(simulator, 88d);
        Commit(simulator);
        Assert.Equal(ModelPortfolioFault.None, simulator.CompleteRun());

        var snapshot = simulator.CommittedSnapshot;
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeUnits(0, out var newestUnits));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeUnits(1, out var priorUnits));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeBars(0, out var newestBars));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeBars(1, out var priorBars));

        return
        [
            BitConverter.DoubleToInt64Bits(snapshot.PositionUnits),
            BitConverter.DoubleToInt64Bits(snapshot.PositionQuantity),
            BitConverter.DoubleToInt64Bits(snapshot.AverageEntryPrice),
            snapshot.BarsHeld,
            BitConverter.DoubleToInt64Bits(snapshot.Equity),
            BitConverter.DoubleToInt64Bits(snapshot.RealizedGrossProfitLoss),
            BitConverter.DoubleToInt64Bits(snapshot.CommissionTotal),
            BitConverter.DoubleToInt64Bits(snapshot.SlippageTotal),
            BitConverter.DoubleToInt64Bits(snapshot.EquityPeak),
            BitConverter.DoubleToInt64Bits(snapshot.MaximumDrawdown),
            snapshot.RetainedTradeCount,
            snapshot.Streak,
            snapshot.IsCompleted ? 1L : 0L,
            BitConverter.DoubleToInt64Bits(newestUnits),
            BitConverter.DoubleToInt64Bits(priorUnits),
            newestBars,
            priorBars,
        ];
    }
}
