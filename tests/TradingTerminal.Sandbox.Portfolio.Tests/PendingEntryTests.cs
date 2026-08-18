namespace TradingTerminal.Sandbox.Portfolio.Tests;

/// <summary>
/// Resting entries: the four pending orders a trader expects (buy limit, sell limit, buy stop, sell
/// stop), the rule that a trigger may not sit on the side that would fire immediately, and the fact
/// that an armed entry survives bars until its price arrives.
/// </summary>
public sealed class PendingEntryTests
{
    private const double Reference = 100d;

    [Theory]
    // A buy waits below for a limit and above for a stop; a sell is the mirror of both.
    [InlineData(+2d, false, 95d)]   // buy limit
    [InlineData(-2d, false, 105d)]  // sell limit
    [InlineData(+2d, true, 105d)]   // buy stop
    [InlineData(-2d, true, 95d)]    // sell stop
    public void EntryOnTheRestingSide_IsArmedAndWaits(double units, bool isStop, double trigger)
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPendingEntry(trigger, units, isStop));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        var snapshot = simulator.CommittedSnapshot;
        Assert.True(snapshot.HasPendingEntry);
        Assert.Equal(trigger, snapshot.PendingEntryPrice);
        Assert.Equal(units, snapshot.PendingEntryUnits);
        Assert.Equal(isStop, snapshot.PendingEntryIsStop);

        // Armed is not filled: nothing happened to the position.
        Assert.Equal(0d, snapshot.PositionUnits);
    }

    [Theory]
    // Each of these would fire on the very next price, so it is a market order, not a pending one.
    [InlineData(+2d, false, 105d)]  // buy limit above the market
    [InlineData(-2d, false, 95d)]   // sell limit below the market
    [InlineData(+2d, true, 95d)]    // buy stop below the market
    [InlineData(-2d, true, 105d)]   // sell stop above the market
    [InlineData(+2d, false, 100d)]  // exactly at the market
    public void EntryOnTheSideThatWouldFireImmediately_IsRefused(double units, bool isStop, double trigger)
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));

        Assert.Equal(
            ModelPortfolioFault.PendingEntryOnWrongSide,
            simulator.MpPendingEntry(trigger, units, isStop));
    }

    [Theory]
    [InlineData(+2d, false, 95d, 96d, 95d)]   // buy limit: not at 96, fills at 95
    [InlineData(-2d, false, 105d, 104d, 105d)] // sell limit: not at 104, fills at 105
    [InlineData(+2d, true, 105d, 104d, 105d)]  // buy stop: not at 104, fills at 105
    [InlineData(-2d, true, 95d, 96d, 95d)]     // sell stop: not at 96, fills at 95
    public void ArmedEntry_FiresOnlyWhenItsPriceArrives(
        double units,
        bool isStop,
        double trigger,
        double priceThatMustNotFire,
        double priceThatFires)
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPendingEntry(trigger, units, isStop));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(priceThatMustNotFire));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
        Assert.True(simulator.CommittedSnapshot.HasPendingEntry);

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(priceThatFires));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        var filled = simulator.CommittedSnapshot;
        Assert.Equal(units, filled.PositionUnits);
        Assert.False(filled.HasPendingEntry);
    }

    [Fact]
    public void ArmedEntry_SurvivesBarsUntilItsPriceArrives()
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPendingEntry(95d, 2d, isStop: false));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        foreach (var price in new[] { 99d, 101d, 98d, 97d, 96d })
        {
            Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(price));
            Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
            Assert.True(simulator.CommittedSnapshot.HasPendingEntry);
            Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
        }

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(94d));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
        Assert.Equal(2d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void CancelledEntry_NeverFires()
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPendingEntry(95d, 2d, isStop: false));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(99d));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpCancelPendingEntry());
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
        Assert.False(simulator.CommittedSnapshot.HasPendingEntry);

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(90d));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
    }

    [Fact]
    public void EntryArmedWhileInPosition_IsRefused()
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpMarket(2d, out _));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(
            ModelPortfolioFault.PendingEntryWhileInPosition,
            simulator.MpPendingEntry(95d, 2d, isStop: false));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void NonFiniteOrNonPositiveTrigger_IsRefused(double trigger)
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));

        Assert.Equal(
            ModelPortfolioFault.InvalidPendingEntryPrice,
            simulator.MpPendingEntry(trigger, 2d, isStop: false));
    }

    [Fact]
    public void ZeroTargetUnits_IsRefused()
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));

        Assert.Equal(
            ModelPortfolioFault.InvalidPendingEntryUnits,
            simulator.MpPendingEntry(95d, 0d, isStop: false));
    }

    [Fact]
    public void ReArming_ReplacesThePreviousEntry()
    {
        var simulator = Create();
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(Reference));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPendingEntry(95d, 2d, isStop: false));
        Assert.Equal(ModelPortfolioFault.None, simulator.MpPendingEntry(90d, 3d, isStop: false));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());

        var snapshot = simulator.CommittedSnapshot;
        Assert.Equal(90d, snapshot.PendingEntryPrice);
        Assert.Equal(3d, snapshot.PendingEntryUnits);

        // The replaced 95 must not fire.
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(94d));
        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
        Assert.Equal(0d, simulator.CommittedSnapshot.PositionUnits);
    }

    private static ModelPortfolioSimulator Create()
    {
        var fault = ModelPortfolioSimulator.TryCreate(
            maxAbsoluteUnits: 10,
            retainedClosedTrips: 4,
            out var simulator);

        Assert.Equal(ModelPortfolioFault.None, fault);
        return Assert.IsType<ModelPortfolioSimulator>(simulator);
    }
}
