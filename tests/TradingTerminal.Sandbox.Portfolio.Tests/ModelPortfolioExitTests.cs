namespace TradingTerminal.Sandbox.Portfolio.Tests;

public sealed class ModelPortfolioExitTests
{
    [Fact]
    public void MpStop_while_flat_faults()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.MpStop(2L, 10d));
        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.CommitCallback());
    }

    [Fact]
    public void MpTarget_while_flat_faults()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.MpTarget(2L, 10d));
        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.CommitCallback());
    }

    [Fact]
    public void MpTrail_while_flat_faults()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.MpTrail(2L, 10d, 0d));
        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.CommitCallback());
    }

    [Fact]
    public void MpCancelExits_while_flat_faults()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.MpCancelExits());
        Assert.Equal(ModelPortfolioFault.ExitWhileFlat, simulator.CommitCallback());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(4L)]
    [InlineData(5L)]
    public void MpStop_rejects_unsupported_modes(long mode)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.MpStop(mode, 10d));
        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(5L)]
    public void MpTarget_rejects_unsupported_modes(long mode)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.MpTarget(mode, 10d));
        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(4L)]
    [InlineData(5L)]
    public void MpTrail_rejects_unsupported_modes(long mode)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.MpTrail(mode, 10d, 0d));
        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MpStop_rejects_non_positive_or_non_finite_values(double value)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.InvalidExitValue, simulator.MpStop(2L, value));
        Assert.Equal(ModelPortfolioFault.InvalidExitValue, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MpTarget_rejects_non_positive_or_non_finite_values(double value)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.InvalidExitValue, simulator.MpTarget(2L, value));
        Assert.Equal(ModelPortfolioFault.InvalidExitValue, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(0d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MpTrail_rejects_non_positive_or_non_finite_distances(double value)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.InvalidExitValue, simulator.MpTrail(2L, value, 0d));
        Assert.Equal(ModelPortfolioFault.InvalidExitValue, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void MpTrail_rejects_negative_or_non_finite_activation_r(double activationR)
    {
        var simulator = OpenPositionWithStop();
        BeginBar(simulator, 100d);

        Assert.Equal(
            ModelPortfolioFault.InvalidTrailActivation,
            simulator.MpTrail(2L, 10d, activationR));
        Assert.Equal(ModelPortfolioFault.InvalidTrailActivation, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void MpStop_rejects_a_long_stop_on_the_wrong_side_of_entry()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.MpStop(1L, 101d));
        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.CommitCallback());
    }

    [Fact]
    public void MpTarget_rejects_a_long_target_on_the_wrong_side_of_entry()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.MpTarget(1L, 99d));
        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.CommitCallback());
    }

    [Fact]
    public void MpStop_rejects_a_short_stop_on_the_wrong_side_of_entry()
    {
        var simulator = OpenPosition(units: -1d);
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.MpStop(1L, 99d));
        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.CommitCallback());
    }

    [Fact]
    public void MpTarget_rejects_a_short_target_on_the_wrong_side_of_entry()
    {
        var simulator = OpenPosition(units: -1d);
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.MpTarget(1L, 101d));
        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, simulator.CommitCallback());
    }

    [Fact]
    public void MpStop_exactly_at_entry_faults()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitAtEntry, simulator.MpStop(1L, 100d));
        Assert.Equal(ModelPortfolioFault.ExitAtEntry, simulator.CommitCallback());
    }

    [Fact]
    public void MpTarget_exactly_at_entry_faults()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitAtEntry, simulator.MpTarget(1L, 100d));
        Assert.Equal(ModelPortfolioFault.ExitAtEntry, simulator.CommitCallback());
    }

    [Fact]
    public void MpStop_distance_that_rounds_the_price_to_entry_faults()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.ExitAtEntry, simulator.MpStop(2L, double.Epsilon));
        Assert.Equal(ModelPortfolioFault.ExitAtEntry, simulator.CommitCallback());
    }

    [Theory]
    [InlineData(100d)]
    [InlineData(101d)]
    public void MpStop_rejects_a_non_positive_derived_percent_price_for_a_long(double percent)
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(
            ModelPortfolioFault.NonFiniteArithmetic,
            simulator.MpStop(3L, percent));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Theory]
    [InlineData(100d)]
    [InlineData(101d)]
    public void MpTarget_rejects_a_non_positive_derived_percent_price_for_a_short(double percent)
    {
        var simulator = OpenPosition(units: -1d);
        BeginBar(simulator, 100d);

        Assert.Equal(
            ModelPortfolioFault.NonFiniteArithmetic,
            simulator.MpTarget(3L, percent));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
        Assert.Equal(-1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void MpStop_accepts_a_positive_derived_percent_price()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(3L, 99d));
        Commit(simulator);
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void MpTrail_does_not_require_a_positive_effective_stop_at_declaration()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(3L, 100d, 0d));
        Commit(simulator);
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void MpOpenR_while_flat_returns_positive_zero()
    {
        var simulator = Create();

        Assert.Equal(ModelPortfolioFault.None, simulator.MpOpenR(out var openR));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(openR));
    }

    [Fact]
    public void MpOpenR_while_open_without_captured_r_faults()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.MpOpenR(out var openR));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(openR));
        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.CommitCallback());
    }

    [Fact]
    public void R_multiple_target_without_captured_r_faults()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.MpTarget(4L, 2d));
        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.CommitCallback());
    }

    [Fact]
    public void Positive_activation_trail_without_captured_r_faults()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);

        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.MpTrail(2L, 10d, 1d));
        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.CommitCallback());
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void MpTradeR_rejects_an_unavailable_index(long index)
    {
        var simulator = Create();

        Assert.Equal(
            ModelPortfolioFault.TradeIndexOutOfRange,
            simulator.MpTradeR(index, out var tradeR));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(tradeR));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(0L)]
    public void MpTradeHasR_rejects_an_unavailable_index(long index)
    {
        var simulator = Create();

        Assert.Equal(
            ModelPortfolioFault.TradeIndexOutOfRange,
            simulator.MpTradeHasR(index, out var hasR));
        Assert.Equal(0L, hasR);
    }

    [Fact]
    public void MpTradeHasR_guards_MpTradeR_for_a_trip_without_r()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));
        Commit(simulator);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeHasR(0L, out var hasR));
        Assert.Equal(0L, hasR);
        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.MpTradeR(0L, out var tradeR));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(tradeR));
    }

    [Fact]
    public void Percent_exit_multiplication_overflow_faults()
    {
        var simulator = OpenPosition(referencePrice: double.MaxValue);
        BeginBar(simulator, double.MaxValue);

        Assert.Equal(
            ModelPortfolioFault.NonFiniteArithmetic,
            simulator.MpStop(3L, 2d));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void Percent_exit_underflow_to_zero_faults()
    {
        const double tinyPrice = 1e-300d;
        var simulator = OpenPosition(referencePrice: tinyPrice);
        BeginBar(simulator, tinyPrice);

        Assert.Equal(
            ModelPortfolioFault.NonFiniteArithmetic,
            simulator.MpStop(3L, double.Epsilon));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void R_multiple_exit_multiplication_overflow_faults()
    {
        var simulator = OpenPositionWithStop();
        BeginBar(simulator, 100d);

        Assert.Equal(
            ModelPortfolioFault.NonFiniteArithmetic,
            simulator.MpTarget(4L, double.MaxValue));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
        Assert.Equal(1d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void MpOpenR_denominator_overflow_faults()
    {
        const double tinyPrice = 1e-300d;
        var simulator = OpenPosition(units: -1d, referencePrice: tinyPrice);
        BeginBar(simulator, tinyPrice);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(1L, double.MaxValue));
        Commit(simulator);

        BeginBar(simulator, tinyPrice);
        Assert.Equal(
            ModelPortfolioFault.NonFiniteArithmetic,
            simulator.MpOpenR(out var openR));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(openR));
        Assert.Equal(ModelPortfolioFault.NonFiniteArithmetic, simulator.CommitCallback());
    }

    [Fact]
    public void MpOpenR_returns_unrealised_r_after_a_stop_captures_it()
    {
        var simulator = OpenPositionWithStop();

        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpOpenR(out var openR));
        Assert.Equal(1d, openR);
        Commit(simulator);
    }

    [Fact]
    public void Zero_activation_trail_captures_r_from_its_effective_stop()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 10d, 0d));
        Commit(simulator);

        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpOpenR(out var openR));
        Assert.Equal(1d, openR);
        Commit(simulator);
    }

    [Fact]
    public void MpCancelExits_preserves_captured_r()
    {
        var simulator = OpenPositionWithStop();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpCancelExits());
        Commit(simulator);

        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpOpenR(out var openR));
        Assert.Equal(1d, openR);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Stop_and_target_prices_are_frozen_across_scale_in()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 5d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTarget(2L, 10d));
        Commit(simulator);

        BeginBar(simulator, 105d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));
        Commit(simulator);

        BeginBar(simulator, 96d);
        Assert.Equal(2d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 111d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Replacing_a_trail_resets_its_high_water_mark()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 50d, 0d));
        Commit(simulator);

        BeginBar(simulator, 140d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 120d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 10d, 0d));
        Commit(simulator);

        BeginBar(simulator, 115d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 110d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Replacing_an_armed_trail_with_positive_activation_resets_it_unarmed()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 50d, 0d));
        Commit(simulator);

        BeginBar(simulator, 120d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 10d, 1d));
        Commit(simulator);

        BeginBar(simulator, 109d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 150d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 140d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Newly_armed_trail_cannot_fire_until_the_next_callback()
    {
        var simulator = OpenPositionWithStop();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 2d, 1d));
        Commit(simulator);

        BeginBar(simulator, 110d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 108d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Zero_activation_trail_does_not_fire_in_the_callback_that_created_it()
    {
        var simulator = Create();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 10d, 0d));
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 90d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Gap_through_stop_fills_at_the_reference_price()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(1L, 95d));
        Commit(simulator);

        BeginBar(simulator, 80d);

        Assert.Equal(0d, ReadPosition(simulator));
        Assert.Equal(-200d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Commit(simulator);
    }

    [Fact]
    public void Host_exit_survives_a_later_bytecode_fault()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(1L, 95d));
        Commit(simulator);

        BeginBar(simulator, 90d);
        Assert.Equal(0d, ReadPosition(simulator));
        Assert.Equal(
            ModelPortfolioFault.InvalidMarketUnits,
            simulator.MpMarket(double.NaN, out _));
        Assert.Equal(ModelPortfolioFault.InvalidMarketUnits, simulator.CommitCallback());

        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(-100d, simulator.CommittedSnapshot.RealizedGrossProfitLoss);
        Assert.Equal(1L, simulator.CommittedSnapshot.LifetimeClosedTripCount);
        Assert.Equal(0L, simulator.CommittedSnapshot.LifetimeWinningTripCount);
        Assert.Equal(1L, simulator.CommittedSnapshot.LifetimeLosingTripCount);
        Assert.Equal(1L, simulator.CommittedSnapshot.RetainedTradeCount);
    }

    [Fact]
    public void Captured_r_is_fixed_and_closed_trade_r_uses_peak_absolute_quantity()
    {
        var simulator = OpenPositionWithStop();

        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(1d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 5d));
        Commit(simulator);

        BeginBar(simulator, 110d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpOpenR(out var openR));
        Assert.Equal(1d, openR);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(0.5d, out _));
        Commit(simulator);

        BeginBar(simulator, 120d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpClose(1d, out _));
        Commit(simulator);

        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeHasR(0L, out var hasR));
        Assert.Equal(1L, hasR);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeR(0L, out var tradeR));
        Assert.Equal(1.5d, tradeR);
    }

    [Fact]
    public void Stop_then_trail_leaves_only_the_latest_declaration()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 5d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 20d, 0d));
        Commit(simulator);

        BeginBar(simulator, 90d);
        Assert.Equal(1d, ReadPosition(simulator));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpOpenR(out var openR));
        Assert.Equal(-2d, openR);
        Commit(simulator);

        BeginBar(simulator, 80d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Trail_then_stop_leaves_only_the_latest_declaration()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTrail(2L, 5d, 0d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 20d));
        Commit(simulator);

        BeginBar(simulator, 90d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);

        BeginBar(simulator, 80d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Faulted_callback_discards_all_staged_exit_declarations()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 5d));
        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.MpTarget(0L, 10d));
        Assert.Equal(ModelPortfolioFault.InvalidExitMode, simulator.CommitCallback());

        BeginBar(simulator, 90d);
        Assert.Equal(1d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Mode_four_target_uses_captured_r_and_triggers_inclusively()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 10d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTarget(4L, 2d));
        Commit(simulator);

        BeginBar(simulator, 120d);
        Assert.Equal(0d, ReadPosition(simulator));
        Commit(simulator);
    }

    [Fact]
    public void Reversal_discards_r_for_the_new_position_but_retains_it_on_the_closed_trip()
    {
        var simulator = OpenPositionWithStop();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(-2d, out _));
        Commit(simulator);

        Assert.Equal(-1d, simulator.CommittedSnapshot.PositionUnits);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpTradeHasR(0L, out var hasR));
        Assert.Equal(1L, hasR);

        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.MpOpenR(out _));
        Assert.Equal(ModelPortfolioFault.UndefinedR, simulator.CommitCallback());
        Assert.Equal(-1d, simulator.CommittedSnapshot.PositionUnits);
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

    private static ModelPortfolioSimulator OpenPosition(
        double units = 1d,
        double referencePrice = 100d)
    {
        var simulator = Create();
        BeginBar(simulator, referencePrice);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(units, out var fill));
        Assert.Equal(referencePrice, fill);
        Commit(simulator);
        return simulator;
    }

    private static ModelPortfolioSimulator OpenPositionWithStop()
    {
        var simulator = OpenPosition();
        BeginBar(simulator, 100d);
        Assert.Equal(ModelPortfolioFault.None, simulator.MpStop(2L, 10d));
        Commit(simulator);
        return simulator;
    }

    private static void BeginBar(ModelPortfolioSimulator simulator, double referencePrice) =>
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(referencePrice));

    private static void Commit(ModelPortfolioSimulator simulator) =>
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

    private static double ReadPosition(ModelPortfolioSimulator simulator)
    {
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var position));
        return position;
    }
}
