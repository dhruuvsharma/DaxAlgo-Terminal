using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

[Collection(SqliteOrderEventStoreCollection.Name)]
public sealed class ExecutionLeaseTests
{
    [Fact]
    public void AccountScopedMutex_GrantsOneWriter_AndRotatesFenceAfterRelease()
    {
        var account = Account();
        var store = new InMemoryExecutionLeaseStore();
        var clock = Clock();
        var firstResult = ExecutionLease.Acquire(account, store, clock, new ExecutionLeaseId("lease-first"));

        Assert.True(firstResult.IsSuccess, firstResult.Reason);
        using var first = firstResult.Lease!;
        Assert.Equal(1, first.Grant.FencingToken.Value);

        var competing = ExecutionLease.Acquire(account, store, clock, new ExecutionLeaseId("lease-competing"));

        Assert.Equal(ExecutionLeaseFault.GateUnavailable, competing.Fault);
        Assert.Null(competing.Lease);

        first.MarkLost();
        clock.SetTo(clock.UtcNow.AddSeconds(1));
        var takeoverResult = ExecutionLease.Acquire(account, store, clock, new ExecutionLeaseId("lease-takeover"));

        Assert.True(takeoverResult.IsSuccess, takeoverResult.Reason);
        using var takeover = takeoverResult.Lease!;
        Assert.Equal(2, takeover.Grant.FencingToken.Value);
        Assert.True(takeover.Grant.FencingToken.IsNewerThan(first.Grant.FencingToken));
    }

    [Fact]
    public void Execute_UsesDedicatedOwnerThread_AndSupportsReentrantValidation()
    {
        var acquired = ExecutionLease.Acquire(
            Account(),
            new InMemoryExecutionLeaseStore(),
            Clock(),
            new ExecutionLeaseId("lease-reentrant"));
        Assert.True(acquired.IsSuccess, acquired.Reason);
        using var lease = acquired.Lease!;
        var callerThread = Environment.CurrentManagedThreadId;

        var outer = lease.Execute(
            lease.Grant,
            () => lease.Execute(lease.Grant, () => Environment.CurrentManagedThreadId));

        Assert.True(outer.IsSuccess, outer.Reason);
        Assert.True(outer.Value.IsSuccess, outer.Value.Reason);
        Assert.NotEqual(callerThread, outer.Value.Value);
    }

    [Fact]
    public void Execute_RejectsPresentedStaleGrant_WithoutInvalidatingCurrentLease()
    {
        var account = Account();
        var acquired = ExecutionLease.Acquire(
            account,
            new InMemoryExecutionLeaseStore(),
            Clock(),
            new ExecutionLeaseId("lease-current"));
        Assert.True(acquired.IsSuccess, acquired.Reason);
        using var lease = acquired.Lease!;
        var invoked = false;
        var stale = lease.Grant with
        {
            FencingToken = new FencingToken(lease.Grant.FencingToken.Value + 1),
        };

        var rejected = lease.Execute(stale, () => invoked = true);

        Assert.Equal(ExecutionLeaseFault.StaleFencingToken, rejected.Fault);
        Assert.False(invoked);
        Assert.True(lease.CanAdmitNewOrders);
        Assert.True(lease.Execute(lease.Grant, () => invoked = true).IsSuccess);
        Assert.True(invoked);
    }

    [Fact]
    public void DurableNewerGeneration_RejectsOldFence_AndClosesAdmission()
    {
        var account = Account();
        var store = new InMemoryExecutionLeaseStore();
        var clock = Clock();
        var acquired = ExecutionLease.Acquire(
            account,
            store,
            clock,
            new ExecutionLeaseId("lease-stale-instance"));
        Assert.True(acquired.IsSuccess, acquired.Reason);
        using var lease = acquired.Lease!;
        clock.SetTo(clock.UtcNow.AddSeconds(1));

        // This direct store acquisition simulates a newer authority appearing despite a stale local
        // instance. The real implementation can only do this after owning the named account mutex.
        var newer = store.Acquire(account, new ExecutionLeaseId("lease-new-authority"), clock.UtcNow);
        Assert.True(newer.IsSuccess, newer.Reason);
        Assert.True(newer.Generation!.Value.Grant.FencingToken.IsNewerThan(lease.Grant.FencingToken));
        var invoked = false;

        var rejected = lease.Execute(lease.Grant, () => invoked = true);

        Assert.Equal(ExecutionLeaseFault.StaleFencingToken, rejected.Fault);
        Assert.False(invoked);
        Assert.False(lease.CanAdmitNewOrders);
    }

    [Fact]
    public void MarkLost_BlocksNewAdmissionBeforeDelegateCanRun()
    {
        var acquired = ExecutionLease.Acquire(
            Account(),
            new InMemoryExecutionLeaseStore(),
            Clock(),
            new ExecutionLeaseId("lease-loss"));
        Assert.True(acquired.IsSuccess, acquired.Reason);
        using var lease = acquired.Lease!;
        var invoked = false;

        lease.MarkLost();
        var rejected = lease.Execute(lease.Grant, () => invoked = true);

        Assert.False(lease.CanAdmitNewOrders);
        Assert.Equal(ExecutionLeaseFault.LeaseLost, rejected.Fault);
        Assert.False(invoked);
    }

    [Fact]
    public void SqliteRestart_ReacquiresWithStrictlyGreaterDurableFence()
    {
        using var directory = new LeaseTestDirectory();
        var databasePath = Path.Combine(directory.Path, "execution-ledger.db");
        var account = Account();
        var clock = Clock();
        FencingToken firstToken;

        using (var store = new SqliteOrderEventStore(databasePath, clock))
        {
            Assert.Equal(4, store.SchemaVersion);
            var acquired = ExecutionLease.Acquire(
                account,
                store,
                clock,
                new ExecutionLeaseId("lease-before-restart"));
            Assert.True(acquired.IsSuccess, acquired.Reason);
            using var lease = acquired.Lease!;
            firstToken = lease.Grant.FencingToken;
        }

        clock.SetTo(clock.UtcNow.AddSeconds(1));
        using var reopened = new SqliteOrderEventStore(databasePath, clock);
        var restarted = ExecutionLease.Acquire(
            account,
            reopened,
            clock,
            new ExecutionLeaseId("lease-after-restart"));

        Assert.True(restarted.IsSuccess, restarted.Reason);
        using var restartedLease = restarted.Lease!;
        Assert.True(restartedLease.Grant.FencingToken.IsNewerThan(firstToken));
        Assert.Equal(firstToken.Value + 1, restartedLease.Grant.FencingToken.Value);
        Assert.True(reopened.Validate(restartedLease.Grant).IsCurrent);
    }

    private static BrokerExecutionAccount Account() =>
        new(
            new ExecutionAdapterId("simulated"),
            new BrokerAccountId($"lease-test-{Guid.NewGuid():N}"));

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private sealed class LeaseTestDirectory : IDisposable
    {
        internal LeaseTestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DaxAlgo-ExecutionLeaseTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
