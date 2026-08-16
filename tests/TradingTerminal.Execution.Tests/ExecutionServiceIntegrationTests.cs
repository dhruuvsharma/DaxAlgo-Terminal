using System.Collections.Concurrent;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Ipc;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Execution.Service;

namespace TradingTerminal.Execution.Tests;

[Collection(SqliteOrderEventStoreCollection.Name)]
public sealed class ExecutionServiceIntegrationTests
{
    private static readonly TimeSpan TestTimeout = TestTimeouts.Deadlock;

    [Fact]
    public async Task RealNamedPipe_SubmitStreamsSimulatedFillAndLedgerEvents()
    {
        using var directory = new ServiceTestDirectory();
        var clock = Clock();
        var clientOrderId = new ClientOrderId("pipe-filled-order");
        var plan = new VenueSubmitPlan(
            clientOrderId,
            VenueSubmitOutcome.Accepted,
            [new FillExecution(
                new ScaledQuantity(200, 2),
                new ScaledPrice(10_125, 2),
                new ScaledMoney(125, 2),
                LiquidityFlag.Taker)]);
        using var runtime = ExecutionServiceRuntime.Create(
            directory.File("filled.db"),
            clock,
            [plan],
            new ExecutionLeaseId("pipe-filled-lease"));
        var secretStore = new FixedSecretStore(0x71);
        var pipeName = PipeName();
        using var server = new ExecutionNamedPipeServer(runtime.Engine, secretStore, pipeName);
        using var timeout = new CancellationTokenSource(TestTimeout);
        var serverTask = server.RunOneConnectionAsync(timeout.Token);
        await using var client = await ExecutionNamedPipeClient.ConnectAsync(
            secretStore,
            pipeName,
            cancellationToken: timeout.Token);

        var status = await client.ExchangeAsync(
            Request(runtime, "status", ExecutionServiceRequestKind.Status),
            timeout.Token);
        Assert.True(status.Response.IsSuccess, status.Response.Reason);
        var instruction = WithLease(OmsTestData.Instruction(clientOrderId.Value), status.Response);
        var submit = await client.ExchangeAsync(
            Request(
                runtime,
                "submit-filled",
                ExecutionServiceRequestKind.Submit,
                status.Response,
                afterOutboxSequence: status.Response.LastOutboxSequence,
                submit: new ExecutionSubmitRequest(instruction, OmsTestData.RiskSnapshot())),
            timeout.Token);

        Assert.True(submit.Response.IsSuccess, submit.Response.Reason);
        Assert.Equal(OrderLifecycleState.Filled, submit.Response.State);
        Assert.Contains(submit.Events, item => item.Event.Kind == OrderEventKind.SubmissionRecorded);
        Assert.Contains(submit.Events, item => item.Event.Kind == OrderEventKind.VenueAcknowledged);
        Assert.Contains(submit.Events, item => item.Event.Kind == OrderEventKind.FillReceived);
        Assert.Contains(submit.Events, item => item.Event.Kind == OrderEventKind.CommissionObserved);
        Assert.Contains(submit.Events, item => item.Event.Kind == OrderEventKind.PositionObserved);
        Assert.All(submit.Events, item => Assert.Equal(clientOrderId, item.Event.AggregateId));
        var fill = Assert.Single(submit.Events, item => item.Event.Kind == OrderEventKind.FillReceived).Event.Fill;
        Assert.Equal(new ScaledQuantity(200, 2), fill!.Value.Quantity);
        Assert.Equal(new ScaledPrice(10_125, 2), fill.Value.Price);
        Assert.Equal(new ScaledMoney(125, 2), fill.Value.Fee);

        await client.DisposeAsync();
        var handshake = await serverTask;
        Assert.True(handshake.IsAuthenticated);
    }

    [Fact]
    public async Task DisconnectKeepsWorkingOrder_AndReconnectResyncsDurableLedger()
    {
        using var directory = new ServiceTestDirectory();
        using var runtime = ExecutionServiceRuntime.Create(
            directory.File("working.db"),
            Clock(),
            leaseId: new ExecutionLeaseId("pipe-working-lease"));
        var secretStore = new FixedSecretStore(0x72);
        var pipeName = PipeName();
        using var server = new ExecutionNamedPipeServer(runtime.Engine, secretStore, pipeName);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var firstServer = server.RunOneConnectionAsync(timeout.Token);
        var firstClient = await ExecutionNamedPipeClient.ConnectAsync(
            secretStore,
            pipeName,
            cancellationToken: timeout.Token);
        var status = await firstClient.ExchangeAsync(
            Request(runtime, "status-working", ExecutionServiceRequestKind.Status),
            timeout.Token);
        var instruction = WithLease(OmsTestData.Instruction("pipe-working-order"), status.Response);
        var submitted = await firstClient.ExchangeAsync(
            Request(
                runtime,
                "submit-working",
                ExecutionServiceRequestKind.Submit,
                status.Response,
                submit: new ExecutionSubmitRequest(instruction, OmsTestData.RiskSnapshot())),
            timeout.Token);
        Assert.True(submitted.Response.IsSuccess, submitted.Response.Reason);
        Assert.Equal(OrderLifecycleState.Working, submitted.Response.State);

        await firstClient.DisposeAsync();
        Assert.True((await firstServer).IsAuthenticated);
        Assert.Equal(
            OrderLifecycleState.Working,
            runtime.Oms.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);

        var secondServer = server.RunOneConnectionAsync(timeout.Token);
        await using var secondClient = await ExecutionNamedPipeClient.ConnectAsync(
            secretStore,
            pipeName,
            cancellationToken: timeout.Token);
        var resync = await secondClient.ExchangeAsync(
            Request(runtime, "resync-working", ExecutionServiceRequestKind.Resync),
            timeout.Token);

        Assert.True(resync.Response.IsSuccess, resync.Response.Reason);
        Assert.Contains(resync.Events, item =>
            item.Event.AggregateId == instruction.Identity.ClientOrderId &&
            item.Event.Kind == OrderEventKind.VenueAcknowledged &&
            item.Event.StateAfter == OrderLifecycleState.Working);

        await secondClient.DisposeAsync();
        Assert.True((await secondServer).IsAuthenticated);
    }

    [Fact]
    public async Task SilentClient_HandshakeDeadlineFailsClosedAndIsLogged()
    {
        using var directory = new ServiceTestDirectory();
        using var runtime = ExecutionServiceRuntime.Create(
            directory.File("silent-client.db"),
            Clock(),
            leaseId: new ExecutionLeaseId("silent-client-lease"));
        var log = new ConcurrentQueue<string>();
        var pipeName = PipeName();
        using var server = new ExecutionNamedPipeServer(
            runtime.Engine,
            new FixedSecretStore(0x73),
            pipeName,
            log: log.Enqueue,
            handshakeTimeout: TimeSpan.FromMilliseconds(150));
        using var silentClient = SecureExecutionNamedPipe.CreateLocalClient(pipeName);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var serverTask = server.RunOneConnectionAsync(timeout.Token);
        await silentClient.ConnectAsync(timeout.Token);
        var handshake = await serverTask;

        Assert.False(handshake.IsAuthenticated);
        Assert.Equal(ExecutionHandshakeFailure.AuthenticationFailed, handshake.Failure);
        Assert.Contains("timed out", handshake.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("proof was absent", handshake.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, item => item.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        var buffer = new byte[1];
        Assert.Equal(0, await silentClient.ReadAsync(buffer, timeout.Token));
    }

    [Fact]
    public async Task SilentService_DesktopHandshakeDeadlineFailsClosedAndClosesClient()
    {
        var pipeName = PipeName();
        var log = new ConcurrentQueue<string>();
        using var silentServer = SecureExecutionNamedPipe.CreateServer(pipeName);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var clientTask = ExecutionNamedPipeClient.ConnectAsync(
            new FixedSecretStore(0x74),
            pipeName,
            log: log.Enqueue,
            handshakeTimeout: TimeSpan.FromMilliseconds(150),
            cancellationToken: timeout.Token);
        await silentServer.WaitForConnectionAsync(timeout.Token);
        await using var serverTransport = new StreamExecutionFrameTransport(silentServer, leaveOpen: true);
        var hello = await serverTransport.ReadAsync<ExecutionClientHello>(timeout.Token);
        Assert.Equal(ExecutionIpcProtocol.Version1, hello.ProtocolVersion);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await clientTask);

        Assert.Contains("AuthenticationFailed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connection or proof was absent", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, item => item.Contains("timed out", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<EndOfStreamException>(async () =>
            await serverTransport.ReadAsync<ExecutionClientProof>(timeout.Token));
    }

    [Fact]
    public async Task MissingService_ClientDeadlineCoversConnectAndFailsClosed()
    {
        var log = new ConcurrentQueue<string>();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ExecutionNamedPipeClient.ConnectAsync(
                new FixedSecretStore(0x75),
                PipeName(),
                log: log.Enqueue,
                handshakeTimeout: TimeSpan.FromMilliseconds(150)));

        Assert.Contains("AuthenticationFailed", failure.Message, StringComparison.Ordinal);
        Assert.Contains("timed out", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connection or proof was absent", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(log, item => item.Contains("timed out", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void StaleTokenAndLeaseLoss_BlockMutation_WhileResyncRemainsAvailable()
    {
        using var directory = new ServiceTestDirectory();
        using var runtime = ExecutionServiceRuntime.Create(
            directory.File("stale.db"),
            Clock(),
            leaseId: new ExecutionLeaseId("stale-request-lease"));
        var current = runtime.Engine.LeaseGrant;
        var staleInstruction = WithLease(
            OmsTestData.Instruction("stale-request-order"),
            current.LeaseId,
            new FencingToken(current.FencingToken.Value + 1));
        var staleRequests = new[]
        {
            new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                "stale-submit",
                ExecutionServiceRequestKind.Submit,
                current.Account,
                staleInstruction.Identity.ExecutionLeaseId,
                staleInstruction.Identity.FencingToken,
                Submit: new ExecutionSubmitRequest(staleInstruction, OmsTestData.RiskSnapshot())),
            new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                "stale-cancel",
                ExecutionServiceRequestKind.Cancel,
                current.Account,
                staleInstruction.Identity.ExecutionLeaseId,
                staleInstruction.Identity.FencingToken),
            new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                "stale-replace",
                ExecutionServiceRequestKind.Replace,
                current.Account,
                staleInstruction.Identity.ExecutionLeaseId,
                staleInstruction.Identity.FencingToken),
            new ExecutionServiceRequest(
                ExecutionServiceProtocol.CurrentVersion,
                "stale-reconcile",
                ExecutionServiceRequestKind.Reconcile,
                current.Account,
                staleInstruction.Identity.ExecutionLeaseId,
                staleInstruction.Identity.FencingToken),
        };

        Assert.All(
            staleRequests,
            item => Assert.Equal(
                ExecutionServiceFault.StaleFencingToken,
                runtime.Engine.Handle(item).Response.Fault));
        Assert.Empty(runtime.Ledger.Read(staleInstruction.Identity.ClientOrderId));

        runtime.Lease.MarkLost();
        var currentInstruction = WithLease(
            OmsTestData.Instruction("lost-lease-order"),
            current.LeaseId,
            current.FencingToken);
        var blocked = runtime.Engine.Handle(new ExecutionServiceRequest(
            ExecutionServiceProtocol.CurrentVersion,
            "lost-submit",
            ExecutionServiceRequestKind.Submit,
            current.Account,
            current.LeaseId,
            current.FencingToken,
            Submit: new ExecutionSubmitRequest(currentInstruction, OmsTestData.RiskSnapshot())));
        var resync = runtime.Engine.Handle(Request(runtime, "resync-after-loss", ExecutionServiceRequestKind.Resync));

        Assert.Equal(ExecutionServiceFault.LeaseLost, blocked.Response.Fault);
        Assert.Empty(runtime.Ledger.Read(currentInstruction.Identity.ClientOrderId));
        Assert.True(resync.Response.IsSuccess, resync.Response.Reason);
    }

    [Fact]
    public void LeaseLoss_ReleasesDurableWriterForTakeover_AndKeepsWorkingOrderVisible()
    {
        using var directory = new ServiceTestDirectory();
        var databasePath = directory.File("takeover.db");
        var clock = Clock();
        using var staleRuntime = ExecutionServiceRuntime.Create(
            databasePath,
            clock,
            leaseId: new ExecutionLeaseId("takeover-stale-lease"));
        var status = staleRuntime.Engine.Handle(
            Request(staleRuntime, "takeover-status", ExecutionServiceRequestKind.Status));
        var instruction = WithLease(
            OmsTestData.Instruction("takeover-working-order"),
            status.Response);
        var submitted = staleRuntime.Engine.Handle(new ExecutionServiceRequest(
            ExecutionServiceProtocol.CurrentVersion,
            "takeover-submit",
            ExecutionServiceRequestKind.Submit,
            staleRuntime.Engine.Account,
            status.Response.ExecutionLeaseId,
            status.Response.FencingToken,
            Submit: new ExecutionSubmitRequest(instruction, OmsTestData.RiskSnapshot())));
        Assert.True(submitted.Response.IsSuccess, submitted.Response.Reason);
        Assert.Equal(OrderLifecycleState.Working, submitted.Response.State);

        var staleGrant = staleRuntime.Lease.Grant;
        staleRuntime.Lease.MarkLost();
        clock.SetTo(clock.UtcNow.AddSeconds(1));
        using var replacementRuntime = ExecutionServiceRuntime.Create(
            databasePath,
            clock,
            leaseId: new ExecutionLeaseId("takeover-replacement-lease"));

        Assert.True(replacementRuntime.Lease.Grant.FencingToken.Value > staleGrant.FencingToken.Value);
        Assert.False(staleRuntime.Lease.CanAdmitNewOrders);
        Assert.True(replacementRuntime.Lease.CanAdmitNewOrders);
        Assert.True(replacementRuntime.Oms.GetProjection(instruction.Identity.ClientOrderId).IsSuccess);

        var resync = staleRuntime.Engine.Handle(
            Request(staleRuntime, "takeover-resync", ExecutionServiceRequestKind.Resync));
        Assert.True(resync.Response.IsSuccess, resync.Response.Reason);
        Assert.Contains(
            resync.Events,
            item => item.Event.AggregateId == instruction.Identity.ClientOrderId);
    }

    [Fact]
    public void AdapterCallback_IsRejectedWhenDurableFenceBecomesStale()
    {
        using var directory = new ServiceTestDirectory();
        var clock = Clock();
        var instructionId = "stale-callback-order";
        var plan = new VenueSubmitPlan(
            new ClientOrderId(instructionId),
            VenueSubmitOutcome.Accepted,
            [new FillExecution(
                ScaledQuantity.FromWhole(2),
                new ScaledPrice(100, 0),
                ScaledMoney.Zero,
                LiquidityFlag.Taker)]);
        using var runtime = ExecutionServiceRuntime.Create(
            directory.File("callback.db"),
            clock,
            [plan],
            new ExecutionLeaseId("stale-callback-lease"));
        var instruction = WithLease(
            OmsTestData.Instruction(instructionId),
            runtime.Lease.Grant.LeaseId,
            runtime.Lease.Grant.FencingToken);

        var released = runtime.Lease.Execute(
            runtime.Lease.Grant,
            () =>
            {
                Assert.True(runtime.Oms.CreateDraft(instruction, Context("callback-create")).IsSuccess);
                Assert.True(runtime.Coordinator.Validate(
                    runtime.Adapter.Account,
                    instruction.Identity.ClientOrderId,
                    OmsTestData.RiskSnapshot(),
                    Context("callback-validate")).IsSuccess);
                Assert.True(runtime.Oms.Prepare(
                    instruction.Identity.ClientOrderId,
                    Context("callback-prepare")).IsSuccess);
                Assert.True(runtime.Coordinator.Arm(
                    runtime.Adapter.Account,
                    instruction.Identity.ClientOrderId,
                    Context("callback-arm")).IsSuccess);
                return runtime.Coordinator.ReleaseAsync(
                    runtime.Adapter.Account,
                    instruction.Identity.ClientOrderId,
                    Context("callback-release")).GetAwaiter().GetResult();
            });
        Assert.True(released.IsSuccess, released.Reason);
        Assert.True(released.Value.IsSuccess, released.Value.Reason);
        Assert.True(runtime.Scheduler.PendingCount > 0);

        clock.SetTo(clock.UtcNow.AddSeconds(1));
        var newer = runtime.Ledger.Acquire(
            runtime.Adapter.Account,
            new ExecutionLeaseId("newer-callback-authority"),
            clock.UtcNow);
        Assert.True(newer.IsSuccess, newer.Reason);
        runtime.Scheduler.RunAll();

        var callback = runtime.Coordinator.GetLastCallbackResult(runtime.Adapter.Account);
        var projection = runtime.Oms.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.NotNull(callback);
        Assert.Equal(OmsCommandFault.LeaseRejected, callback.Value.Fault);
        Assert.Equal(OrderLifecycleState.Acknowledging, projection.State);
        Assert.DoesNotContain(
            runtime.Ledger.Read(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.FillReceived);
        Assert.False(runtime.Lease.CanAdmitNewOrders);
    }

    private static ExecutionServiceRequest Request(
        ExecutionServiceRuntime runtime,
        string requestId,
        ExecutionServiceRequestKind kind,
        ExecutionServiceResponse? lease = null,
        long afterOutboxSequence = 0,
        ExecutionSubmitRequest? submit = null) =>
        new(
            ExecutionServiceProtocol.CurrentVersion,
            requestId,
            kind,
            runtime.Engine.Account,
            lease?.ExecutionLeaseId ?? default,
            lease?.FencingToken ?? default,
            afterOutboxSequence,
            submit);

    private static CanonicalOrderInstruction WithLease(
        CanonicalOrderInstruction instruction,
        ExecutionServiceResponse response) =>
        WithLease(instruction, response.ExecutionLeaseId, response.FencingToken);

    private static CanonicalOrderInstruction WithLease(
        CanonicalOrderInstruction instruction,
        ExecutionLeaseId leaseId,
        FencingToken fencingToken) =>
        instruction with
        {
            Identity = instruction.Identity with
            {
                ExecutionLeaseId = leaseId,
                FencingToken = fencingToken,
            },
        };

    private static OrderCommandContext Context(string suffix) =>
        new(new CausationId($"cause-{suffix}"), new DeduplicationKey($"dedup-{suffix}"));

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private static string PipeName() => $"DaxAlgo.Execution.Service.Tests.{Guid.NewGuid():N}";

    private sealed class FixedSecretStore(byte value) : IExecutionServiceSecretStore
    {
        private readonly byte[] _secret =
            Enumerable.Repeat(value, DpapiExecutionServiceSecretStore.SecretSize).ToArray();

        public byte[] LoadOrCreate() => (byte[])_secret.Clone();
    }

    private sealed class ServiceTestDirectory : IDisposable
    {
        internal ServiceTestDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DaxAlgo-ExecutionServiceTests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
