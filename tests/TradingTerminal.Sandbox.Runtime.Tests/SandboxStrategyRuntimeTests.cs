using System.Collections.Concurrent;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.Sandbox;

namespace TradingTerminal.Sandbox.Runtime.Tests;

public sealed class SandboxStrategyRuntimeTests
{
    private static readonly InstrumentId Instrument = new(42);
    private static readonly DateTime Epoch = new(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);
    /// <summary>
    /// Deadlock detector, NOT a performance assertion. Nothing here is asserting that the runtime is
    /// fast — every wait is on a signal that arrives in milliseconds when the code is correct, so the
    /// only question this bound answers is "did it hang?". It is deliberately generous because these
    /// tests share a machine with every other test assembly during a full-solution run, and a 5 s
    /// bound turned ordinary CPU contention into random red builds. If a wait ever reaches 60 s,
    /// something is genuinely deadlocked.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    [Fact]
    public async Task BarsDriveKernelTargetsIntoExactCommittedPortfolioEvolution()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new ThirdBarLongSixthBarFlatKernel(schema);
        var expected = BuildExpectedSnapshots();

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => new ModelPortfolioAccount(instruments),
            retentionBound: 16);

        await runtime.RunAsync(CancellationToken.None);
        Assert.Equal(SandboxStrategyRuntimeState.Running, runtime.State);

        for (var number = 1; number <= 3; number++)
            hub.PublishBar(Bar(number, 100d + number));

        await WaitUntilAsync(
            () => kernel.BarCount == 3 && runtime.CurrentSnapshot?.PositionUnits == 1d,
            "the third bar should commit the long target");
        AssertPortfolio(expected.AfterThirdBar, runtime.CurrentSnapshot);

        for (var number = 4; number <= 6; number++)
            hub.PublishBar(Bar(number, 100d + number));

        await WaitUntilAsync(
            () => kernel.BarCount == 6 &&
                  runtime.CurrentSnapshot?.LifetimeClosedTripCount == 1,
            "the sixth bar should flatten the model portfolio");
        AssertPortfolio(expected.AfterSixthBar, runtime.CurrentSnapshot);
        Assert.Equal(0d, runtime.CurrentSnapshot!.PositionUnits);
        Assert.Equal(1L, runtime.CurrentSnapshot.LifetimeClosedTripCount);
    }

    [Fact]
    public async Task EnabledReplicationConvergesSimulatedBookOneToOneThenFlatThroughGuardedIntake()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new TwoThenFlatKernel(schema);
        using var client = new InProcessExecutionClient();
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Sandbox Replica",
            "simulated",
            Array.AsReadOnly(["sandbox-strategy-42"]),
            Instrument,
            "SBX42"));
        Assert.True(created.IsSuccess, created.Message);
        var bookId = Assert.Single(client.GetSnapshot().Books, item => item.Name == "Sandbox Replica").Id;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => new ModelPortfolioAccount(instruments),
            retentionBound: 16);
        await using var replicator = new SandboxExecutionReplicator(
            runtime,
            client,
            new SandboxExecutionReplicationOptions(
                bookId,
                "sandbox-strategy-42",
                Enabled: true,
                PolicyVersion: "sandbox-replication-test-v1"));
        var outcomes = new ConcurrentQueue<SandboxExecutionReplicationOutcome>();
        replicator.SubmissionCompleted += outcomes.Enqueue;

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, 100d));

        await WaitUntilAsync(
            () => runtime.CurrentSnapshot?.PositionUnits == 2d &&
                  RealQuantity(client, bookId) == "+2" &&
                  outcomes.Any(item => item.Intent is { } mapped &&
                      mapped.SignedUnits.TryGetWholeUnits(out var units) &&
                      units == 2 &&
                      item.Result.IsSuccess),
            "the +2 sandbox target should converge the Simulated book 1:1");
        var longOutcome = Assert.Single(
            outcomes,
            item => item.Intent is { } mapped &&
                    mapped.SignedUnits.TryGetWholeUnits(out var units) &&
                    units == 2 &&
                    item.Result.IsSuccess);
        Assert.Equal(TradeIntentQuantityMode.TargetPosition, longOutcome.Intent!.Value.QuantityMode);
        Assert.Equal(new ScaledPrice(9_025, 2), longOutcome.Intent.Value.ProtectiveStopPrice);
        Assert.Equal(new ScaledPrice(1_105, 1), longOutcome.Intent.Value.ProfitTargetPrice);
        Assert.Equal("sandbox-strategy-42", longOutcome.Intent.Value.StrategyId);
        Assert.Equal("sandbox-replication-test-v1", longOutcome.Intent.Value.PolicyVersion);

        hub.PublishBar(Bar(2, 101d));

        await WaitUntilAsync(
            () => runtime.CurrentSnapshot?.PositionUnits == 0d && RealQuantity(client, bookId) == "0",
            "the flat sandbox target should converge the Simulated book to zero");
        var book = Assert.Single(client.GetSnapshot().Books, item => item.Id == bookId);
        Assert.Equal(0, book.OpenRealPositionCount);
        Assert.Contains(book.Orders, item => item.State == "Filled");
        Assert.Equal(
            ExecutionMode.Paper,
            Assert.Single(client.GetSnapshot().Adapters, item => item.Id == "simulated").Mode);
    }

    [Fact]
    public async Task IntakePauseAndKillStopEnabledReplicationUntilAnExplicitRetry()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new AlwaysTwoKernel(schema);
        using var client = new InProcessExecutionClient();
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Sandbox Guarded",
            "simulated",
            Array.AsReadOnly(["sandbox-guarded-42"]),
            Instrument,
            "SBX42"));
        Assert.True(created.IsSuccess, created.Message);
        var bookId = Assert.Single(client.GetSnapshot().Books, item => item.Name == "Sandbox Guarded").Id;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => new ModelPortfolioAccount(instruments),
            retentionBound: 16);
        await using var replicator = new SandboxExecutionReplicator(
            runtime,
            client,
            new SandboxExecutionReplicationOptions(bookId, "sandbox-guarded-42", Enabled: true));
        var outcomes = new ConcurrentQueue<SandboxExecutionReplicationOutcome>();
        replicator.SubmissionCompleted += outcomes.Enqueue;

        await runtime.RunAsync(CancellationToken.None);
        Assert.True((await client.SetIntakePausedAsync(bookId, paused: true)).IsSuccess);
        hub.PublishBar(Bar(1, 100d));

        await WaitUntilAsync(
            () => outcomes.Any(item =>
                item.Intent is { } mapped &&
                mapped.SignedUnits.TryGetWholeUnits(out var units) &&
                units == 2 &&
                !item.Result.IsSuccess),
            "paused intake should reject the +2 sandbox target");
        Assert.Equal("0", RealQuantity(client, bookId));

        Assert.True((await client.SetIntakePausedAsync(bookId, paused: false)).IsSuccess);
        Assert.True(replicator.ReplicateCurrent());
        await WaitUntilAsync(
            () => RealQuantity(client, bookId) == "+2",
            "an explicit retry after resume should converge the current target");

        var killed = await client.KillAsync(bookId);
        Assert.True(killed.IsSuccess, killed.Message);
        Assert.Equal("0", RealQuantity(client, bookId));
        Assert.True(Assert.Single(client.GetSnapshot().Books, item => item.Id == bookId).IsIntakePaused);

        var rejectedBeforeRetry = outcomes.Count(item =>
            item.Intent is { } mapped &&
            mapped.SignedUnits.TryGetWholeUnits(out var units) &&
            units == 2 &&
            !item.Result.IsSuccess);
        Assert.True(replicator.ReplicateCurrent());
        await WaitUntilAsync(
            () => outcomes.Count(item =>
                item.Intent is { } mapped &&
                mapped.SignedUnits.TryGetWholeUnits(out var units) &&
                units == 2 &&
                !item.Result.IsSuccess) > rejectedBeforeRetry,
            "kill-paused intake should reject an explicit retry of the unchanged model target");
        Assert.Equal("0", RealQuantity(client, bookId));

        Assert.True((await client.SetIntakePausedAsync(bookId, paused: false)).IsSuccess);
        Assert.True(replicator.ReplicateCurrent());
        await WaitUntilAsync(
            () => RealQuantity(client, bookId) == "+2",
            "an explicit retry after kill resume should not be suppressed as a duplicate target");
    }

    [Fact]
    public async Task PumpIsSerializedAndDropOldestQueueKeepsOnlyNewestBurstEvents()
    {
        const int capacity = 3;
        const int eventCount = 20;
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new BlockingFirstBarKernel(schema);
        TrackingAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new TrackingAccount(instruments),
            retentionBound: capacity);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, 101d));
        await kernel.FirstBarEntered.Task.WaitAsync(Timeout);

        for (var number = 2; number <= eventCount; number++)
            hub.PublishBar(Bar(number, 100d + number));

        Assert.Equal(capacity, runtime.QueueCapacity);
        Assert.Equal(eventCount - 1 - capacity, runtime.DroppedEventCount);

        kernel.ReleaseFirstBar.TrySetResult();
        await WaitUntilAsync(
            () => kernel.ProcessedCloses.Contains(120d),
            "the newest queued bar should survive drop-oldest overflow");

        Assert.Equal(new[] { 101d, 118d, 119d, 120d }, kernel.ProcessedCloses.ToArray());
        Assert.Equal(1, kernel.MaximumConcurrentHandlers);
        Assert.NotNull(account);
        Assert.False(account.OverlapDetected);
        Assert.Equal(4, account.CommitCount);
    }

    [Fact]
    public async Task PausePreservesSnapshotAndResumeRebuildsWithEditedParameters()
    {
        var schema = LifecycleSchema();
        var hub = new FakeMarketDataHub();
        var kernels = new ConcurrentQueue<LifecycleKernel>();
        var accounts = new ConcurrentQueue<TrackingAccount>();

        await using var runtime = CreateRuntime(
            () =>
            {
                var kernel = new LifecycleKernel(schema);
                kernels.Enqueue(kernel);
                return kernel;
            },
            schema,
            hub,
            instruments =>
            {
                var account = new TrackingAccount(instruments);
                accounts.Enqueue(account);
                return account;
            },
            retentionBound: 16);

        await runtime.RunAsync(CancellationToken.None);
        var firstKernel = Assert.Single(kernels);
        var firstAccount = Assert.Single(accounts);
        var runningSubscriptionCount = hub.ActiveSubscriptions;
        Assert.True(runningSubscriptionCount > 0);
        Assert.Equal(1, firstKernel.StartedUnits);

        hub.PublishBar(Bar(1, 100d));
        await WaitUntilAsync(
            () => runtime.CurrentSnapshot?.PositionUnits == 1d,
            "the first kernel should commit its configured unit target");

        runtime.Pause();
        var pausedSnapshot = runtime.CurrentSnapshot;
        Assert.Equal(SandboxStrategyRuntimeState.Paused, runtime.State);
        Assert.True(runtime.IsPaused);
        Assert.True(runtime.IsRunning);
        Assert.Equal(1d, pausedSnapshot!.PositionUnits);

        hub.PublishBar(Bar(2, 110d));
        runtime.SetParameter("units", 2);
        await runtime.ResumeAsync(CancellationToken.None);

        var rebuiltKernels = kernels.ToArray();
        var rebuiltAccounts = accounts.ToArray();
        Assert.Equal(2, rebuiltKernels.Length);
        Assert.Equal(2, rebuiltAccounts.Length);
        Assert.NotSame(rebuiltKernels[0], rebuiltKernels[1]);
        Assert.True(firstKernel.Disposed);
        Assert.True(firstAccount.Disposed);
        Assert.Equal(1, firstKernel.StopCount);
        Assert.Equal(2, rebuiltKernels[1].StartedUnits);
        Assert.Equal(runningSubscriptionCount, hub.ActiveSubscriptions);
        Assert.Equal(SandboxStrategyRuntimeState.Running, runtime.State);

        hub.PublishBar(Bar(3, 120d));
        await WaitUntilAsync(
            () => rebuiltKernels[1].ReceivedCloses.Contains(120d) &&
                  runtime.CurrentSnapshot?.PositionUnits == 2d,
            "the rebuilt kernel should use the edited parameter value");

        Assert.DoesNotContain(110d, rebuiltKernels[0].ReceivedCloses);
        Assert.DoesNotContain(110d, rebuiltKernels[1].ReceivedCloses);

        await runtime.StopAsync(CancellationToken.None);

        Assert.Equal(SandboxStrategyRuntimeState.Stopped, runtime.State);
        Assert.False(runtime.IsRunning);
        Assert.Equal(0, hub.ActiveSubscriptions);
        Assert.All(kernels, static kernel => Assert.True(kernel.Disposed));
        Assert.All(accounts, static account => Assert.True(account.Disposed));
        Assert.Equal(1, rebuiltKernels[1].StopCount);
    }

    [Fact]
    public async Task KernelExceptionRollsBackOnlyThatEventAndPumpContinues()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var logs = new ConcurrentQueue<string>();
        var kernel = new ThrowOnceKernel(schema);
        TrackingAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new TrackingAccount(instruments),
            retentionBound: 8,
            logs: logs);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, 100d));
        await WaitUntilAsync(
            () => account?.RollbackCount == 1,
            "the throwing callback should roll back its open account window");
        Assert.Equal(0d, runtime.CurrentSnapshot!.PositionUnits);

        hub.PublishBar(Bar(2, 101d));
        await WaitUntilAsync(
            () => kernel.BarCount == 2 && runtime.CurrentSnapshot?.PositionUnits == 1d,
            "the event after a kernel fault should still commit");

        Assert.Equal(SandboxStrategyRuntimeState.Running, runtime.State);
        Assert.Equal(1, account!.RollbackCount);
        Assert.Equal(1, account.CommitCount);
        Assert.Contains(logs, static message => message.Contains("event was skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AccountFaultRollsBackOnlyThatEventAndPumpContinues()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var logs = new ConcurrentQueue<string>();
        var kernel = new AlwaysLongKernel(schema);
        FaultOnceAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new FaultOnceAccount(instruments),
            retentionBound: 8,
            logs: logs);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, 100d));
        await WaitUntilAsync(
            () => account?.RollbackCount == 1,
            "the injected account fault should roll back its event");
        Assert.Equal(0d, runtime.CurrentSnapshot!.PositionUnits);

        hub.PublishBar(Bar(2, 101d));
        await WaitUntilAsync(
            () => kernel.BarCount == 2 && runtime.CurrentSnapshot?.PositionUnits == 1d,
            "the account should accept the event after its injected fault");

        Assert.Equal(SandboxStrategyRuntimeState.Running, runtime.State);
        Assert.Equal(1, account!.RollbackCount);
        Assert.Equal(1, account.CommitCount);
        Assert.Contains(logs, static message => message.Contains("account rejected", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InvalidReferenceStillReachesKernelAndDefersItsTargetToNextPricedEvent()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new DeferredTargetKernel(schema);
        TrackingAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new TrackingAccount(instruments),
            retentionBound: 8);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, double.NaN));
        await WaitUntilAsync(
            () => kernel.BarCount == 1,
            "the unpriced event should still be delivered to the kernel");

        Assert.NotNull(account);
        Assert.Equal(0, account.BeginCount);
        Assert.Equal(0d, runtime.CurrentSnapshot!.PositionUnits);

        hub.PublishBar(Bar(2, 100d));
        await WaitUntilAsync(
            () => kernel.BarCount == 2 && runtime.CurrentSnapshot?.PositionUnits == 1d,
            "the pending target should reconcile in the next priced window");

        Assert.Equal(1, account.BeginCount);
        Assert.Equal(1, account.CommitCount);
    }

    [Fact]
    public async Task AuthorizedStreamsOpenWindowsWithTheirDeclaredReferencePrices()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new AllStreamsKernel(schema);
        TrackingAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new TrackingAccount(instruments),
            retentionBound: 16);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishQuote(Quote(100d, 102d, sequence: 1));
        hub.PublishTrade(Trade(103d, sequence: 2));
        hub.PublishDepth(
            Instrument,
            new DepthSnapshot(
                Epoch.AddSeconds(3),
                new[] { new DepthLevel(104d, 10) },
                new[] { new DepthLevel(106d, 12) }));
        hub.PublishBar(Bar(4, 107d));

        await WaitUntilAsync(
            () => kernel.EventCount == 4 && account?.CommitCount == 4,
            "all four authorized stream types should be serialized through the account");

        Assert.Equal(
            new[]
            {
                new ReferenceCall("Tick", 100d, 102d, 0d),
                new ReferenceCall("Tick", 0d, 0d, 103d),
                new ReferenceCall("Tick", 104d, 106d, 0d),
                new ReferenceCall("Bar", 0d, 0d, 107d),
            },
            account!.ReferenceCalls.ToArray());
        Assert.False(account.OverlapDetected);
    }

    [Fact]
    public async Task SnapshotNotificationsAreCoalescedAndExposeLatestPortfolio()
    {
        const int eventCount = 20;
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new AlternatingTargetKernel(schema);
        var finalNotification = new TaskCompletionSource<IModelPortfolio>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationCount = 0;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => new ModelPortfolioAccount(instruments),
            retentionBound: 32);

        runtime.SnapshotChanged += snapshot =>
        {
            Interlocked.Increment(ref notificationCount);
            if (snapshot.PositionUnits == 0d &&
                snapshot.LifetimeClosedTripCount == eventCount / 2)
            {
                finalNotification.TrySetResult(snapshot);
            }
        };

        await runtime.RunAsync(CancellationToken.None);
        for (var number = 1; number <= eventCount; number++)
            hub.PublishBar(Bar(number, 100d + number));

        await WaitUntilAsync(
            () => kernel.BarCount == eventCount &&
                  runtime.CurrentSnapshot?.LifetimeClosedTripCount == eventCount / 2,
            "the full hot-feed burst should reach the committed snapshot");
        var notified = await finalNotification.Task.WaitAsync(Timeout);

        Assert.InRange(Volatile.Read(ref notificationCount), 1, eventCount - 1);
        Assert.Equal(runtime.CurrentSnapshot!.PositionUnits, notified.PositionUnits);
        Assert.Equal(runtime.CurrentSnapshot.Equity, notified.Equity);
        Assert.Equal(runtime.CurrentSnapshot.LifetimeClosedTripCount, notified.LifetimeClosedTripCount);
    }

    [Fact]
    public async Task ThrowingCancellationRegistrationCannotInterruptStopTeardown()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var logs = new ConcurrentQueue<string>();
        var kernel = new ThrowingCancellationKernel(schema);
        TrackingAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new TrackingAccount(instruments),
            retentionBound: 8,
            logs: logs);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, 100d));
        await WaitUntilAsync(() => kernel.BarCount == 1, "the kernel should register its cancellation callback");

        await runtime.StopAsync(CancellationToken.None);

        Assert.Equal(SandboxStrategyRuntimeState.Stopped, runtime.State);
        Assert.Equal(0, hub.ActiveSubscriptions);
        Assert.True(kernel.Disposed);
        Assert.True(account!.Disposed);
        Assert.Equal(1, kernel.StopCount);
        Assert.Contains(logs, static message => message.Contains("cancellation callback failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowSnapshotListenerIsSerializedAndReceivesTheLatestCoalescedState()
    {
        const int eventCount = 10;
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new AlternatingTargetKernel(schema);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latestReceived = new TaskCompletionSource<IModelPortfolio>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var activeCallbacks = 0;
        var maximumCallbacks = 0;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => new ModelPortfolioAccount(instruments),
            retentionBound: 16);

        runtime.SnapshotChanged += snapshot =>
        {
            var active = Interlocked.Increment(ref activeCallbacks);
            UpdateMaximum(ref maximumCallbacks, active);
            try
            {
                if (firstEntered.TrySetResult())
                    releaseFirst.Task.GetAwaiter().GetResult();

                if (snapshot.LifetimeClosedTripCount == eventCount / 2)
                    latestReceived.TrySetResult(snapshot);
            }
            finally
            {
                Interlocked.Decrement(ref activeCallbacks);
            }
        };

        await runtime.RunAsync(CancellationToken.None);
        await firstEntered.Task.WaitAsync(Timeout);
        try
        {
            for (var number = 1; number <= eventCount; number++)
                hub.PublishBar(Bar(number, 100d + number));

            await WaitUntilAsync(
                () => kernel.BarCount == eventCount &&
                      runtime.CurrentSnapshot?.LifetimeClosedTripCount == eventCount / 2,
                "the pump should keep processing while the first snapshot listener is blocked");
            Assert.Equal(1, Volatile.Read(ref maximumCallbacks));
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        var latest = await latestReceived.Task.WaitAsync(Timeout);
        Assert.Equal(1, Volatile.Read(ref maximumCallbacks));
        Assert.Equal(runtime.CurrentSnapshot!.Equity, latest.Equity);
        Assert.Equal(eventCount / 2, latest.LifetimeClosedTripCount);
    }

    [Fact]
    public async Task DeferredOnStartTargetSurvivesOneFaultingPricedCallback()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new DeferredStartThenThrowKernel(schema);
        TrackingAccount? account = null;

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            instruments => account = new TrackingAccount(instruments),
            retentionBound: 8);

        await runtime.RunAsync(CancellationToken.None);
        hub.PublishBar(Bar(1, 100d));
        await WaitUntilAsync(
            () => account?.RollbackCount == 1,
            "the first priced callback should roll back without consuming the deferred target");

        hub.PublishBar(Bar(2, 101d));
        await WaitUntilAsync(
            () => kernel.BarCount == 2 && runtime.CurrentSnapshot?.PositionUnits == 1d,
            "the preserved OnStart target should reconcile on the next successful priced callback");

        Assert.Equal(1, account!.CommitCount);
        Assert.Equal(1d, runtime.CurrentSnapshot!.PositionUnits);
    }

    [Fact]
    public async Task FailedBuildDisposesReadViewSubscriptionsAndOwnedObjects()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var kernel = new DisposableNoOpKernel(schema);
        var account = new ThrowingBookAccount(Instrument);

        await using var runtime = CreateRuntime(
            () => kernel,
            schema,
            hub,
            _ => account,
            retentionBound: 8);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.RunAsync(CancellationToken.None));

        Assert.Equal(SandboxStrategyRuntimeState.Idle, runtime.State);
        Assert.Equal(0, hub.ActiveSubscriptions);
        Assert.True(kernel.Disposed);
        Assert.True(account.Disposed);
    }

    [Fact]
    public async Task SnapshotListenerCanDisposeRuntimeWithoutDeadlocking()
    {
        var schema = InstrumentSchema();
        var hub = new FakeMarketDataHub();
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = CreateRuntime(
            () => new AlwaysLongKernel(schema),
            schema,
            hub,
            instruments => new ModelPortfolioAccount(instruments),
            retentionBound: 8);

        try
        {
            runtime.SnapshotChanged += _ =>
            {
                runtime.Dispose();
                disposed.TrySetResult();
            };

            await runtime.RunAsync(CancellationToken.None);
            await disposed.Task.WaitAsync(Timeout);

            Assert.Equal(SandboxStrategyRuntimeState.Stopped, runtime.State);
            Assert.Equal(0, hub.ActiveSubscriptions);
        }
        finally
        {
            await runtime.DisposeAsync();
        }
    }

    private static SandboxStrategyRuntime CreateRuntime(
        Func<IStrategyKernel> kernelFactory,
        StrategyParameterSchema schema,
        FakeMarketDataHub hub,
        Func<IReadOnlySet<InstrumentId>, IModelPortfolioAccount> accountFactory,
        int retentionBound,
        ConcurrentQueue<string>? logs = null) =>
        new(
            kernelFactory,
            schema,
            currentValues: null,
            hub,
            new TestClock(Epoch),
            accountFactory,
            (source, level, message) => logs?.Enqueue($"{source}:{level}:{message}"),
            static _ => { },
            retentionBound);

    private static StrategyParameterSchema InstrumentSchema() =>
        new(StrategyParameter.Instrument("instrument", "Instrument", Instrument));

    private static StrategyParameterSchema LifecycleSchema() =>
        new(
            StrategyParameter.Instrument("instrument", "Instrument", Instrument),
            StrategyParameter.Int("units", "Units", 1, min: 1, max: 10));

    private static OhlcvBar Bar(int number, double close) =>
        new(
            Instrument,
            BarSize.OneMinute,
            Epoch.AddMinutes(number),
            close,
            close,
            close,
            close,
            number * 10L,
            BrokerKind.Simulated,
            IsFinal: true);

    private static Quote Quote(double bid, double ask, long sequence) =>
        new(
            Instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            bid,
            ask,
            10,
            12,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static TradePrint Trade(double price, long sequence) =>
        new(
            Instrument,
            Epoch.AddSeconds(sequence),
            Epoch.AddSeconds(sequence),
            price,
            5,
            AggressorSide.Buy,
            BrokerKind.Simulated,
            sequence,
            EventTimeApproximate: false);

    private static ExpectedSnapshots BuildExpectedSnapshots()
    {
        var account = new ModelPortfolioAccount(Instrument);
        SandboxPortfolioSnapshot afterThird = default;
        SandboxPortfolioSnapshot afterSixth = default;

        for (var number = 1; number <= 6; number++)
        {
            account.BeginBar(100d + number);
            if (number == 3)
                account.Book.SetTargetPosition(Instrument, 1d, protectiveStopPrice: 90d);
            else if (number == 6)
                account.Book.SetTargetPosition(Instrument, 0d);

            account.ReconcileToTargets();
            account.Commit();
            Assert.Equal(ModelPortfolioFault.None, account.LastFault);

            if (number == 3)
                afterThird = account.Snapshot;
            else if (number == 6)
                afterSixth = account.Snapshot;
        }

        return new ExpectedSnapshots(afterThird, afterSixth);
    }

    private static void AssertPortfolio(SandboxPortfolioSnapshot expected, IModelPortfolio? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Instrument, actual.Instrument);
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
        Assert.Equal(expected.IsComplete, actual.IsComplete);
    }

    private static string RealQuantity(InProcessExecutionClient client, string bookId) =>
        Assert.Single(Assert.Single(client.GetSnapshot().Books, item => item.Id == bookId).Positions).RealQuantity;

    private static async Task WaitUntilAsync(Func<bool> condition, string failureMessage)
    {
        if (condition())
            return;

        using var timeout = new CancellationTokenSource(Timeout);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(5));
        try
        {
            while (await timer.WaitForNextTickAsync(timeout.Token))
            {
                if (condition())
                    return;
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            // The assertion below reports the test-specific condition rather than a generic timeout.
        }

        Assert.True(condition(), failureMessage);
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        var current = Volatile.Read(ref target);
        while (candidate > current)
        {
            var observed = Interlocked.CompareExchange(ref target, candidate, current);
            if (observed == current)
                return;
            current = observed;
        }
    }

    private sealed record ExpectedSnapshots(
        SandboxPortfolioSnapshot AfterThirdBar,
        SandboxPortfolioSnapshot AfterSixthBar);

    private readonly record struct ReferenceCall(
        string Kind,
        double Bid,
        double Ask,
        double Last);

    private abstract class TestKernel(
        StrategyParameterSchema schema,
        StrategyDataRequirement requirement) : IStrategyKernel
    {
        public StrategyParameterSchema Schema { get; } = schema;
        public StrategyDataRequirement DataRequirement { get; } = requirement;

        public virtual Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
            Task.CompletedTask;

        public virtual Task OnQuoteAsync(
            Quote quote,
            IStrategyRuntimeContext context,
            CancellationToken ct) => Task.CompletedTask;

        public virtual Task OnTradeAsync(
            TradePrint trade,
            IStrategyRuntimeContext context,
            CancellationToken ct) => Task.CompletedTask;

        public virtual Task OnDepthAsync(
            InstrumentId instrument,
            DepthSnapshot depth,
            IStrategyRuntimeContext context,
            CancellationToken ct) => Task.CompletedTask;

        public virtual Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct) => Task.CompletedTask;

        public virtual Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ThirdBarLongSixthBarFlatKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;
        public int BarCount => Volatile.Read(ref _barCount);

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            var number = Interlocked.Increment(ref _barCount);
            if (number == 3)
                context.Book.SetTargetPosition(Instrument, 1d, protectiveStopPrice: 90d);
            else if (number == 6)
                context.Book.SetTargetPosition(Instrument, 0d);
            return Task.CompletedTask;
        }
    }

    private sealed class TwoThenFlatKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _barCount);
            if (count == 1)
                context.Book.SetTargetPosition(Instrument, 2d, 90.25d, 110.5d);
            else if (count == 2)
                context.Book.SetTargetPosition(Instrument, 0d);
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysTwoKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            context.Book.SetTargetPosition(Instrument, 2d);
            return Task.CompletedTask;
        }
    }

    private sealed class BlockingFirstBarKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _activeHandlers;
        private int _maximumConcurrentHandlers;

        public TaskCompletionSource FirstBarEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstBar { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<double> ProcessedCloses { get; } = new();
        public int MaximumConcurrentHandlers => Volatile.Read(ref _maximumConcurrentHandlers);

        public override async Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            var active = Interlocked.Increment(ref _activeHandlers);
            UpdateMaximum(ref _maximumConcurrentHandlers, active);
            try
            {
                ProcessedCloses.Enqueue(bar.Close);
                if (bar.Close == 101d)
                {
                    FirstBarEntered.TrySetResult();
                    await ReleaseFirstBar.Task.WaitAsync(ct);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeHandlers);
            }
        }

        private static void UpdateMaximum(ref int target, int candidate)
        {
            var current = Volatile.Read(ref target);
            while (candidate > current)
            {
                var observed = Interlocked.CompareExchange(ref target, candidate, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }

    private sealed class LifecycleKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars), IDisposable
    {
        public ConcurrentQueue<double> ReceivedCloses { get; } = new();
        public int StartedUnits { get; private set; }
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }

        public override Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            StartedUnits = context.Parameters.GetInt("units");
            return Task.CompletedTask;
        }

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            ReceivedCloses.Enqueue(bar.Close);
            context.Book.SetTargetPosition(Instrument, StartedUnits);
            return Task.CompletedTask;
        }

        public override Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowOnceKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;
        public int BarCount => Volatile.Read(ref _barCount);

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _barCount);
            context.Book.SetTargetPosition(Instrument, 1d);
            if (count == 1)
                throw new InvalidOperationException("Injected kernel failure.");
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysLongKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;
        public int BarCount => Volatile.Read(ref _barCount);

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _barCount);
            context.Book.SetTargetPosition(Instrument, 1d);
            return Task.CompletedTask;
        }
    }

    private sealed class DeferredTargetKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;
        public int BarCount => Volatile.Read(ref _barCount);

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _barCount) == 1)
                context.Book.SetTargetPosition(Instrument, 1d);
            return Task.CompletedTask;
        }
    }

    private sealed class AllStreamsKernel(StrategyParameterSchema schema)
        : TestKernel(
            schema,
            StrategyDataRequirement.L1 |
            StrategyDataRequirement.TradeTape |
            StrategyDataRequirement.Depth |
            StrategyDataRequirement.Bars)
    {
        private int _eventCount;
        public int EventCount => Volatile.Read(ref _eventCount);

        public override Task OnQuoteAsync(
            Quote quote,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _eventCount);
            return Task.CompletedTask;
        }

        public override Task OnTradeAsync(
            TradePrint trade,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _eventCount);
            return Task.CompletedTask;
        }

        public override Task OnDepthAsync(
            InstrumentId instrument,
            DepthSnapshot depth,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _eventCount);
            return Task.CompletedTask;
        }

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _eventCount);
            return Task.CompletedTask;
        }
    }

    private sealed class AlternatingTargetKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;
        public int BarCount => Volatile.Read(ref _barCount);

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _barCount);
            context.Book.SetTargetPosition(Instrument, count % 2 == 1 ? 1d : 0d);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingCancellationKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars), IDisposable
    {
        private CancellationTokenRegistration _registration;
        private int _barCount;

        public int BarCount => Volatile.Read(ref _barCount);
        public int StopCount { get; private set; }
        public bool Disposed { get; private set; }

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            _registration = ct.Register(
                static () => throw new InvalidOperationException("Injected cancellation callback failure."));
            Interlocked.Increment(ref _barCount);
            return Task.CompletedTask;
        }

        public override Task OnStopAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            StopCount++;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _registration.Dispose();
            Disposed = true;
        }
    }

    private sealed class DeferredStartThenThrowKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars)
    {
        private int _barCount;
        public int BarCount => Volatile.Read(ref _barCount);

        public override Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct)
        {
            context.Book.SetTargetPosition(Instrument, 1d);
            return Task.CompletedTask;
        }

        public override Task OnBarAsync(
            OhlcvBar bar,
            IStrategyRuntimeContext context,
            CancellationToken ct)
        {
            if (Interlocked.Increment(ref _barCount) == 1)
                throw new InvalidOperationException("Injected first priced callback failure.");
            return Task.CompletedTask;
        }
    }

    private sealed class DisposableNoOpKernel(StrategyParameterSchema schema)
        : TestKernel(schema, StrategyDataRequirement.Bars), IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class TrackingAccount : IModelPortfolioAccount, IDisposable
    {
        private readonly ModelPortfolioAccount _inner;
        private int _windowOpen;

        public TrackingAccount(IReadOnlySet<InstrumentId> instruments) =>
            _inner = new ModelPortfolioAccount(instruments);

        public IVirtualBook Book => _inner.Book;
        public ModelPortfolioFault LastFault => _inner.LastFault;
        public SandboxPortfolioSnapshot Snapshot => _inner.Snapshot;
        public ConcurrentQueue<ReferenceCall> ReferenceCalls { get; } = new();
        public int BeginCount { get; private set; }
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }
        public bool OverlapDetected { get; private set; }
        public bool Disposed { get; private set; }

        public void BeginBar(double close)
        {
            OpenWindow();
            BeginCount++;
            ReferenceCalls.Enqueue(new ReferenceCall("Bar", 0d, 0d, close));
            _inner.BeginBar(close);
        }

        public void BeginTick(double bid, double ask, double last)
        {
            OpenWindow();
            BeginCount++;
            ReferenceCalls.Enqueue(new ReferenceCall("Tick", bid, ask, last));
            _inner.BeginTick(bid, ask, last);
        }

        public void ReconcileToTargets() => _inner.ReconcileToTargets();

        public void Commit()
        {
            _inner.Commit();
            CommitCount++;
            Interlocked.Exchange(ref _windowOpen, 0);
        }

        public void Rollback()
        {
            _inner.Rollback();
            RollbackCount++;
            Interlocked.Exchange(ref _windowOpen, 0);
        }

        public void Complete() => _inner.Complete();

        public void Dispose() => Disposed = true;

        private void OpenWindow()
        {
            if (Interlocked.Exchange(ref _windowOpen, 1) != 0)
                OverlapDetected = true;
        }
    }

    private sealed class FaultOnceAccount : IModelPortfolioAccount, IDisposable
    {
        private readonly ModelPortfolioAccount _inner;
        private ModelPortfolioFault _lastFault;
        private int _faultInjected;

        public FaultOnceAccount(IReadOnlySet<InstrumentId> instruments) =>
            _inner = new ModelPortfolioAccount(instruments);

        public IVirtualBook Book => _inner.Book;
        public ModelPortfolioFault LastFault => _lastFault;
        public SandboxPortfolioSnapshot Snapshot => _inner.Snapshot;
        public int CommitCount { get; private set; }
        public int RollbackCount { get; private set; }

        public void BeginBar(double close)
        {
            _inner.BeginBar(close);
            _lastFault = _inner.LastFault;
        }

        public void BeginTick(double bid, double ask, double last)
        {
            _inner.BeginTick(bid, ask, last);
            _lastFault = _inner.LastFault;
        }

        public void ReconcileToTargets()
        {
            if (Interlocked.Exchange(ref _faultInjected, 1) == 0)
            {
                _lastFault = ModelPortfolioFault.InvalidCallbackState;
                return;
            }

            _inner.ReconcileToTargets();
            _lastFault = _inner.LastFault;
        }

        public void Commit()
        {
            _inner.Commit();
            _lastFault = _inner.LastFault;
            CommitCount++;
        }

        public void Rollback()
        {
            _inner.Rollback();
            RollbackCount++;
        }

        public void Complete()
        {
            _inner.Complete();
            _lastFault = _inner.LastFault;
        }

        public void Dispose() { }
    }

    private sealed class ThrowingBookAccount : IModelPortfolioAccount, IDisposable
    {
        private readonly SandboxPortfolioSnapshot _snapshot;

        public ThrowingBookAccount(InstrumentId instrument) =>
            _snapshot = new SandboxPortfolioSnapshot(
                instrument,
                0d,
                0d,
                0d,
                0L,
                100_000d,
                0d,
                0d,
                0d,
                100_000d,
                0d,
                0L,
                0L,
                0L,
                0L,
                0L,
                false);

        public IVirtualBook Book => throw new InvalidOperationException("Injected Book getter failure.");
        public ModelPortfolioFault LastFault => ModelPortfolioFault.None;
        public SandboxPortfolioSnapshot Snapshot => _snapshot;
        public bool Disposed { get; private set; }

        public void BeginBar(double close) { }
        public void BeginTick(double bid, double ask, double last) { }
        public void ReconcileToTargets() { }
        public void Commit() { }
        public void Rollback() { }
        public void Complete() { }
        public void Dispose() => Disposed = true;
    }

    private sealed class FakeMarketDataHub : IMarketDataHub
    {
        private readonly ConcurrentDictionary<InstrumentId, TestObservable<Quote>> _quotes = new();
        private readonly ConcurrentDictionary<InstrumentId, TestObservable<TradePrint>> _trades = new();
        private readonly ConcurrentDictionary<BarStreamKey, TestObservable<OhlcvBar>> _bars = new();
        private readonly ConcurrentDictionary<InstrumentId, TestObservable<DepthSnapshot>> _depth = new();

        public int ActiveSubscriptions =>
            _quotes.Values.Sum(static stream => stream.ActiveSubscriptions) +
            _trades.Values.Sum(static stream => stream.ActiveSubscriptions) +
            _bars.Values.Sum(static stream => stream.ActiveSubscriptions) +
            _depth.Values.Sum(static stream => stream.ActiveSubscriptions);

        public IObservable<Quote> Quotes(InstrumentId instrumentId) =>
            _quotes.GetOrAdd(instrumentId, static _ => new TestObservable<Quote>());

        public IObservable<TradePrint> Trades(InstrumentId instrumentId) =>
            _trades.GetOrAdd(instrumentId, static _ => new TestObservable<TradePrint>());

        public IObservable<OhlcvBar> Bars(InstrumentId instrumentId, BarSize size) =>
            _bars.GetOrAdd(new BarStreamKey(instrumentId, size), static _ => new TestObservable<OhlcvBar>());

        public IObservable<DepthSnapshot> Depth(InstrumentId instrumentId) =>
            _depth.GetOrAdd(instrumentId, static _ => new TestObservable<DepthSnapshot>());

        public void PublishQuote(Quote quote) =>
            _quotes.GetOrAdd(quote.InstrumentId, static _ => new TestObservable<Quote>()).Publish(quote);

        public void PublishTrade(TradePrint trade) =>
            _trades.GetOrAdd(trade.InstrumentId, static _ => new TestObservable<TradePrint>()).Publish(trade);

        public void PublishBar(OhlcvBar bar) =>
            _bars.GetOrAdd(
                new BarStreamKey(bar.InstrumentId, bar.Size),
                static _ => new TestObservable<OhlcvBar>()).Publish(bar);

        public void PublishDepth(InstrumentId instrumentId, DepthSnapshot snapshot) =>
            _depth.GetOrAdd(instrumentId, static _ => new TestObservable<DepthSnapshot>()).Publish(snapshot);

        private readonly record struct BarStreamKey(InstrumentId Instrument, BarSize Size);
    }

    private sealed class TestObservable<T> : IObservable<T>
    {
        private readonly object _gate = new();
        private readonly Dictionary<long, IObserver<T>> _observers = new();
        private long _nextId;

        public int ActiveSubscriptions
        {
            get
            {
                lock (_gate)
                    return _observers.Count;
            }
        }

        public IDisposable Subscribe(IObserver<T> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_gate)
            {
                var id = ++_nextId;
                _observers.Add(id, observer);
                return new Subscription(this, id);
            }
        }

        public void Publish(T value)
        {
            IObserver<T>[] observers;
            lock (_gate)
                observers = _observers.Values.ToArray();

            foreach (var observer in observers)
                observer.OnNext(value);
        }

        private void Remove(long id)
        {
            lock (_gate)
                _observers.Remove(id);
        }

        private sealed class Subscription(TestObservable<T> owner, long id) : IDisposable
        {
            private TestObservable<T>? _owner = owner;

            public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Remove(id);
        }
    }

    private sealed class TestClock(DateTime utcNow) : IClock
    {
        public DateTime UtcNow { get; } = utcNow;
    }
}
