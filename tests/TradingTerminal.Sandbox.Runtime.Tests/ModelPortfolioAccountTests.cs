namespace TradingTerminal.Sandbox.Runtime.Tests;

public sealed class ModelPortfolioAccountTests
{
    private static readonly InstrumentId Instrument = new(42);

    [Fact]
    public void ReconcilesLatestTargetsAsDeltasAcrossFlatIncreaseNoOpCloseAndFlip()
    {
        var account = CreateAccount();

        ApplyTarget(account, 100d, 2d);
        Assert.Equal(2d, account.Snapshot.PositionUnits);

        var beforeNoOp = account.Snapshot;
        ApplyTarget(account, 101d, 2d);
        Assert.Equal(2d, account.Snapshot.PositionUnits);
        Assert.Equal(beforeNoOp.CommissionTotal, account.Snapshot.CommissionTotal);
        Assert.Equal(beforeNoOp.SlippageTotal, account.Snapshot.SlippageTotal);

        ApplyTarget(account, 102d, 0d);
        Assert.Equal(0d, account.Snapshot.PositionUnits);

        ApplyTarget(account, 103d, -1d);
        Assert.Equal(-1d, account.Snapshot.PositionUnits);
    }

    [Theory]
    [InlineData(95d, 0L, 1L, -1L)]
    [InlineData(110d, 1L, 0L, 1L)]
    public void AbsoluteStopAndTargetTriggerOnLaterBars(
        double triggerPrice,
        long expectedWins,
        long expectedLosses,
        long expectedStreak)
    {
        var account = CreateAccount();
        ApplyTarget(account, 100d, 1d, 95d, 110d);

        ApplyNoTarget(account, 105d);
        Assert.Equal(1d, account.Snapshot.PositionUnits);

        ApplyNoTarget(account, triggerPrice);

        Assert.Equal(0d, account.Snapshot.PositionUnits);
        Assert.Equal(1L, account.Snapshot.LifetimeClosedTripCount);
        Assert.Equal(expectedWins, account.Snapshot.LifetimeWinningTripCount);
        Assert.Equal(expectedLosses, account.Snapshot.LifetimeLosingTripCount);
        Assert.Equal(expectedStreak, account.Snapshot.Streak);
        Assert.Equal(1L, account.Snapshot.RetainedTradeCount);
        Assert.NotEqual(100_000d, account.Snapshot.Equity);
    }

    [Fact]
    public void SnapshotCarriesCommittedExitPricesUntilThePositionIsFlat()
    {
        var account = CreateAccount();

        ApplyTarget(account, 100d, 2d, stop: 90.25d, target: 110.5d);

        Assert.Equal(90.25d, account.Snapshot.ProtectiveStopPrice);
        Assert.Equal(110.5d, account.Snapshot.ProfitTargetPrice);

        ApplyNoTarget(account, 101d);

        Assert.Equal(90.25d, account.Snapshot.ProtectiveStopPrice);
        Assert.Equal(110.5d, account.Snapshot.ProfitTargetPrice);

        ApplyTarget(account, 102d, 0d);

        Assert.Null(account.Snapshot.ProtectiveStopPrice);
        Assert.Null(account.Snapshot.ProfitTargetPrice);
    }

    [Fact]
    public void InvalidExitFaultDoesNotEscapeAndCommitRollsBackStagedTarget()
    {
        var account = CreateAccount();
        ApplyTarget(account, 100d, 1d);

        account.BeginBar(101d);
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
        var committedBeforeReconcile = account.Snapshot;
        account.Book.SetTargetPosition(Instrument, 2d, protectiveStopPrice: 101d);

        var exception = Record.Exception(() =>
        {
            account.ReconcileToTargets();
            account.Commit();
        });

        Assert.Null(exception);
        Assert.Equal(ModelPortfolioFault.ExitOnWrongSide, account.LastFault);
        Assert.Equal(committedBeforeReconcile, account.Snapshot);
    }

    [Fact]
    public void OneSidedDeclarativeExitReplacesThePriorExitPair()
    {
        var targetOnly = CreateAccount();
        ApplyTarget(targetOnly, 100d, 1d, 95d, 110d);
        ApplyTarget(targetOnly, 105d, 1d, target: 112d);
        ApplyNoTarget(targetOnly, 94d);
        Assert.Equal(1d, targetOnly.Snapshot.PositionUnits);
        ApplyNoTarget(targetOnly, 112d);
        Assert.Equal(0d, targetOnly.Snapshot.PositionUnits);
        Assert.Equal(1L, targetOnly.Snapshot.LifetimeWinningTripCount);

        var stopOnly = CreateAccount();
        ApplyTarget(stopOnly, 100d, 1d, 95d, 110d);
        ApplyTarget(stopOnly, 105d, 1d, stop: 96d);
        ApplyNoTarget(stopOnly, 111d);
        Assert.Equal(1d, stopOnly.Snapshot.PositionUnits);
        ApplyNoTarget(stopOnly, 96d);
        Assert.Equal(0d, stopOnly.Snapshot.PositionUnits);
        Assert.Equal(1L, stopOnly.Snapshot.LifetimeLosingTripCount);
    }

    [Fact]
    public void SoftLimitRejectionPreservesTheExistingPositionAndExits()
    {
        var account = CreateAccount(new ModelPortfolioAccountConfig(1, 4));
        ApplyTarget(account, 100d, 1d, 90d, 110d);

        ApplyTarget(account, 101d, 2d);

        Assert.Equal(1d, account.Snapshot.PositionUnits);
        ApplyNoTarget(account, 110d);
        Assert.Equal(0d, account.Snapshot.PositionUnits);
        Assert.Equal(1L, account.Snapshot.LifetimeWinningTripCount);
    }

    [Fact]
    public void FailedNestedBeginRollsBackThePriorWindowAndPreservesItsFault()
    {
        var account = CreateAccount();
        account.BeginBar(100d);
        account.Book.SetTargetPosition(Instrument, 1d);
        account.ReconcileToTargets();
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);

        account.BeginBar(101d);
        Assert.Equal(ModelPortfolioFault.InvalidCallbackState, account.LastFault);

        account.Commit();

        Assert.Equal(ModelPortfolioFault.InvalidCallbackState, account.LastFault);
        Assert.Equal(0d, account.Snapshot.PositionUnits);
    }

    [Fact]
    public void SnapshotProjectsEveryCommittedFieldExactly()
    {
        var config = new ModelPortfolioAccountConfig(10, 4);
        var account = CreateAccount(config);
        var simulator = CreateSimulator(config);

        ApplyTarget(account, 100d, 2d);
        ApplyRawTarget(simulator, 100d, 2d);
        ApplyNoTarget(account, 105d);
        ApplyRawTarget(simulator, 105d, null);
        ApplyTarget(account, 110d, 0d);
        ApplyRawTarget(simulator, 110d, 0d);
        ApplyTarget(account, 110d, -1d);
        ApplyRawTarget(simulator, 110d, -1d);
        ApplyTarget(account, 115d, 0d);
        ApplyRawTarget(simulator, 115d, 0d);
        ApplyTarget(account, 120d, 1d);
        ApplyRawTarget(simulator, 120d, 1d);
        ApplyNoTarget(account, 121d);
        ApplyRawTarget(simulator, 121d, null);

        AssertProjection(simulator.CommittedSnapshot, account.Snapshot);
    }

    [Fact]
    public void TwoInstrumentConstructionThrowsDocumentedGuard()
    {
        var instruments = new HashSet<InstrumentId>
        {
            new(1),
            new(2),
        };

        var exception = Assert.Throws<NotSupportedException>(
            () => new ModelPortfolioAccount(instruments));

        Assert.Contains("exactly one declared instrument", exception.Message, StringComparison.Ordinal);
        Assert.Contains("multi-instrument portfolios", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompleteLiquidatesAtLastReferenceAndSamplesFinalEquity()
    {
        var account = CreateAccount();
        ApplyTarget(account, 100d, 1d);
        ApplyNoTarget(account, 110d);
        var markedBeforeCompletion = account.Snapshot;

        account.Complete();

        var completed = account.Snapshot;
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
        Assert.True(completed.IsComplete);
        Assert.Equal(0d, completed.PositionUnits);
        Assert.Equal(1L, completed.LifetimeClosedTripCount);
        Assert.Equal(1L, completed.LifetimeWinningTripCount);
        Assert.NotEqual(markedBeforeCompletion.Equity, completed.Equity);
        Assert.Equal(
            100_000d + completed.RealizedGrossProfitLoss -
            completed.CommissionTotal - completed.SlippageTotal,
            completed.Equity,
            precision: 10);
    }

    private static ModelPortfolioAccount CreateAccount(
        ModelPortfolioAccountConfig? config = null) =>
        new(Instrument, config);

    private static ModelPortfolioSimulator CreateSimulator(
        ModelPortfolioAccountConfig config)
    {
        Assert.Equal(
            ModelPortfolioFault.None,
            ModelPortfolioSimulator.TryCreate(config.MaxAbsoluteUnits, config.RetainedClosedTrips, out var simulator));
        return Assert.IsType<ModelPortfolioSimulator>(simulator);
    }

    private static void ApplyTarget(
        ModelPortfolioAccount account,
        double close,
        double targetUnits,
        double? stop = null,
        double? target = null)
    {
        account.BeginBar(close);
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
        account.Book.SetTargetPosition(Instrument, targetUnits, stop, target);
        account.ReconcileToTargets();
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
        account.Commit();
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
    }

    private static void ApplyNoTarget(ModelPortfolioAccount account, double close)
    {
        account.BeginBar(close);
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
        account.ReconcileToTargets();
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
        account.Commit();
        Assert.Equal(ModelPortfolioFault.None, account.LastFault);
    }

    private static void ApplyRawTarget(
        ModelPortfolioSimulator simulator,
        double close,
        double? targetUnits)
    {
        Assert.Equal(ModelPortfolioFault.None, simulator.BeginOnBar(close));
        if (targetUnits is double target)
        {
            Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out var current));
            var delta = target - current;
            if (delta != 0d)
            {
                Assert.Equal(
                    ModelPortfolioFault.None,
                    simulator.MpMarket(delta, out _));
            }

            Assert.Equal(ModelPortfolioFault.None, simulator.MpPosition(out current));
            if (current != 0d)
            {
                Assert.Equal(
                    ModelPortfolioFault.None,
                    simulator.MpCancelExits());
            }
        }

        Assert.Equal(ModelPortfolioFault.None, simulator.CommitCallback());
    }

    private static void AssertProjection(
        ModelPortfolioSnapshot expected,
        SandboxPortfolioSnapshot actual)
    {
        Assert.Equal(Instrument, actual.Instrument);
        Assert.Equal(expected.PositionUnits, actual.PositionUnits);
        Assert.Equal(expected.PositionQuantity, actual.PositionQuantity);
        Assert.Equal(expected.AverageEntryPrice, actual.AverageEntryPrice);
        Assert.Equal(expected.BarsHeld, actual.BarsHeld);
        Assert.Equal(expected.Equity, actual.Equity);
        Assert.Equal(expected.RealizedGrossProfitLoss, actual.RealizedGrossProfitLoss);
        Assert.Equal(expected.CommissionTotal, actual.CommissionTotal);
        Assert.Equal(expected.SlippageTotal, actual.SlippageTotal);
        Assert.Equal(expected.EquityPeak, actual.EquityPeak);
        Assert.Equal(expected.MaximumDrawdown, actual.MaximumDrawdown);
        Assert.Equal(expected.LifetimeClosedTripCount, actual.LifetimeClosedTripCount);
        Assert.Equal(expected.LifetimeWinningTripCount, actual.LifetimeWinningTripCount);
        Assert.Equal(expected.LifetimeLosingTripCount, actual.LifetimeLosingTripCount);
        Assert.Equal(expected.RetainedTradeCount, actual.RetainedTradeCount);
        Assert.Equal(expected.Streak, actual.Streak);
        Assert.Equal(expected.IsCompleted, actual.IsComplete);
    }
}
