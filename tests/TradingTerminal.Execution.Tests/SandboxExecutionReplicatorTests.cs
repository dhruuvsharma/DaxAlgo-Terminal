using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Domain;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.Execution.Tests;

public sealed class SandboxExecutionReplicatorTests
{
    private static readonly InstrumentId Instrument = new(42);

    [Fact]
    public async Task DisabledBindingDoesNotObserveOrSubmitTargets()
    {
        var source = new ManualPortfolioSource();
        var intake = new RecordingIntake(ExecutionTargetSubmissionResult.Success("accepted"));
        await using var replicator = new SandboxExecutionReplicator(
            source,
            intake,
            // Explicit since 2026-08-17: replication is enabled BY DEFAULT now that the virtual book
            // is a strategy's only route to execution, so a test for the disabled path has to opt out
            // deliberately rather than lean on the default.
            new SandboxExecutionReplicationOptions("book-1", "sandbox-42", Enabled: false));

        source.Publish(Snapshot(2d, 90.25d, 110.5d));
        await Task.Delay(75);

        Assert.False(replicator.IsEnabled);
        Assert.Empty(intake.Intents);
        Assert.Null(replicator.LastOutcome);
    }

    [Fact]
    public async Task EnabledBindingMapsWholeUnitsAndExitPricesExactlyWithoutResizing()
    {
        var source = new ManualPortfolioSource();
        var intake = new RecordingIntake(ExecutionTargetSubmissionResult.Success("accepted"));
        await using var replicator = new SandboxExecutionReplicator(
            source,
            intake,
            new SandboxExecutionReplicationOptions(
                "book-1",
                "sandbox-42",
                Enabled: true,
                PolicyVersion: "sandbox-policy-v7"));

        source.Publish(Snapshot(2d, 90.25d, 110.5d));
        var outcome = await intake.NextOutcome.Task.WaitAsync(TestTimeouts.Deadlock);

        Assert.True(outcome.IsSuccess, outcome.Message);
        var intent = Assert.Single(intake.Intents);
        Assert.Equal(Instrument, intent.Instrument);
        Assert.Equal(TradeIntentQuantityMode.TargetPosition, intent.QuantityMode);
        Assert.True(intent.SignedUnits.TryGetWholeUnits(out var units));
        Assert.Equal(2, units);
        Assert.Equal(new ScaledPrice(9_025, 2), intent.ProtectiveStopPrice);
        Assert.Equal(new ScaledPrice(1_105, 1), intent.ProfitTargetPrice);
        Assert.Equal("sandbox-42", intent.StrategyId);
        Assert.Equal("sandbox-policy-v7", intent.PolicyVersion);
        Assert.Equal(ScaledMoney.Zero, intent.EstimatedRoundTripCostPerUnit);
    }

    [Fact]
    public async Task EnabledBindingStillCannotConstructLiveRouteWithoutFullAuthorization()
    {
        var factoryCalls = 0;
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddAlpacaExecution(
            options =>
            {
                options.Enabled = true;
                options.Mode = ExecutionMode.Live;
                options.BaseUrl = AlpacaExecutionOptions.LiveBaseUrl;
                options.AllowLiveExecution = false;
                options.KeyId = "real-key";
                options.SecretKey = "real-secret";
                options.ExpectedAccountId = "live-account-42";
                options.Symbol = "AAPL";
                options.CanonicalInstrumentId = Instrument.Value;
            },
            (_, _) =>
            {
                factoryCalls++;
                throw new InvalidOperationException("The live transport factory must not run.");
            },
            confirmationStore: new InMemoryLiveExecutionConfirmationStore()));
        Assert.Equal(0, factoryCalls);

        var source = new ManualPortfolioSource();
        var intake = new RecordingIntake(
            ExecutionTargetSubmissionResult.Failure("No fully authorized live book route exists."));
        await using var replicator = new SandboxExecutionReplicator(
            source,
            intake,
            new SandboxExecutionReplicationOptions("live-book", "sandbox-42", Enabled: true));

        source.Publish(Snapshot(2d, 90d, 110d));
        var outcome = await intake.NextOutcome.Task.WaitAsync(TestTimeouts.Deadlock);

        Assert.False(outcome.IsSuccess);
        Assert.Single(intake.Intents);
        Assert.Equal(0, factoryCalls);
        Assert.DoesNotContain("IBrokerExecutionAdapter", typeof(SandboxExecutionReplicator)
            .GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Select(field => field.FieldType.Name));
    }

    [Theory]
    // The four MT5-style pending entries, each mirrored to the venue as a genuine resting order
    // rather than watched locally and converted to a market order when it triggers.
    [InlineData(95d, +2d, false, true)]   // buy limit  -> entry limit price
    [InlineData(105d, -2d, false, true)]  // sell limit -> entry limit price
    [InlineData(105d, +2d, true, false)]  // buy stop   -> entry stop price
    [InlineData(95d, -2d, true, false)]   // sell stop  -> entry stop price
    public async Task RestingEntry_IsMirroredToTheVenueWithItsExactTriggerPrice(
        double triggerPrice,
        double signedTargetUnits,
        bool isStop,
        bool expectLimitPrice)
    {
        var source = new ManualPortfolioSource();
        var intake = new RecordingIntake(ExecutionTargetSubmissionResult.Success("accepted"));
        await using var replicator = new SandboxExecutionReplicator(
            source,
            intake,
            new SandboxExecutionReplicationOptions("book-1", "sandbox-42", Enabled: true));

        source.Publish(PendingSnapshot(triggerPrice, signedTargetUnits, isStop));
        var outcome = await intake.NextOutcome.Task.WaitAsync(TestTimeouts.Deadlock);
        Assert.True(outcome.IsSuccess, outcome.Message);

        var intent = Assert.Single(intake.Intents);

        // The target comes from the ARMED ENTRY, not from the book's current position - the book is
        // still flat and waiting, so replicating its position would place no order at all.
        Assert.True(intent.SignedUnits.TryGetWholeUnits(out var units));
        Assert.Equal((long)signedTargetUnits, units);

        // Exact prices are normalised, so a whole-number trigger has scale 0 - not 1050/1.
        var expected = new ScaledPrice((long)triggerPrice, 0);
        if (expectLimitPrice)
        {
            Assert.Equal(expected, intent.EntryLimitPrice);
            Assert.Null(intent.EntryStopPrice);
        }
        else
        {
            Assert.Equal(expected, intent.EntryStopPrice);
            Assert.Null(intent.EntryLimitPrice);
        }
    }

    [Fact]
    public async Task BookWithNoRestingEntry_StillReplicatesAsAMarketEntry()
    {
        var source = new ManualPortfolioSource();
        var intake = new RecordingIntake(ExecutionTargetSubmissionResult.Success("accepted"));
        await using var replicator = new SandboxExecutionReplicator(
            source,
            intake,
            new SandboxExecutionReplicationOptions("book-1", "sandbox-42", Enabled: true));

        source.Publish(Snapshot(2d, null, null));
        Assert.True((await intake.NextOutcome.Task.WaitAsync(TestTimeouts.Deadlock)).IsSuccess);

        var intent = Assert.Single(intake.Intents);
        Assert.Null(intent.EntryLimitPrice);
        Assert.Null(intent.EntryStopPrice);
        Assert.Equal(CanonicalOrderType.Market, CanonicalOrderInstruction.EntryOrderTypeOf(intent));
    }

    private static SandboxPortfolioSnapshot PendingSnapshot(
        double triggerPrice,
        double signedTargetUnits,
        bool isStop) =>
        Snapshot(0d, null, null) with
        {
            PendingEntry = new PendingEntryState(triggerPrice, signedTargetUnits, isStop),
        };

    private static SandboxPortfolioSnapshot Snapshot(
        double units,
        double? protectiveStop,
        double? profitTarget) =>
        new(
            Instrument,
            units,
            units,
            100d,
            1,
            100_000d,
            0d,
            0d,
            0d,
            100_000d,
            0d,
            0,
            0,
            0,
            0,
            0,
            false,
            protectiveStop,
            profitTarget);

    private sealed class ManualPortfolioSource : IModelPortfolioSource
    {
        public IModelPortfolio? CurrentSnapshot { get; private set; }

        public event Action<IModelPortfolio>? SnapshotChanged;

        public void Publish(IModelPortfolio snapshot)
        {
            CurrentSnapshot = snapshot;
            SnapshotChanged?.Invoke(snapshot);
        }
    }

    private sealed class RecordingIntake(ExecutionTargetSubmissionResult result) : IExecutionBookTargetIntake
    {
        public List<TradeIntent> Intents { get; } = [];

        public TaskCompletionSource<ExecutionTargetSubmissionResult> NextOutcome { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<ExecutionTargetSubmissionResult> SubmitTargetAsync(
            string bookId,
            TradeIntent intent,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Intents.Add(intent);
            NextOutcome.TrySetResult(result);
            return ValueTask.FromResult(result);
        }
    }
}
