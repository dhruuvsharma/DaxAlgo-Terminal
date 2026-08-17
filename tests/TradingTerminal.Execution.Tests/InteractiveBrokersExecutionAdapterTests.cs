using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Core.Time;
using TradingTerminal.Execution.InteractiveBrokers;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class InteractiveBrokersExecutionAdapterTests
{
    private const string AccountId = "DU1234567";

    [Fact]
    public void Options_DefaultToPaperDisabledAndDistinctExecutionClientId()
    {
        var options = new InteractiveBrokersExecutionOptions();

        Assert.False(options.Enabled);
        Assert.Equal(ExecutionMode.Paper, options.Mode);
        Assert.False(options.AllowLiveExecution);
        Assert.Equal(InteractiveBrokersExecutionOptions.TwsPaperPort, options.Port);
        Assert.Equal(2, options.ClientId);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("credentials")]
    [InlineData("confirmation")]
    public void LiveEndpoint_RefusesBeforeTransportFactoryWhenAnyFoundationConditionIsMissing(string missing)
    {
        var options = LiveOptions();
        var store = LiveConfirmations(options);
        if (missing == "allow")
            options.AllowLiveExecution = false;
        else if (missing == "credentials")
            options.AccountId = string.Empty;
        else
            store = new InMemoryLiveExecutionConfirmationStore();

        var factoryCalls = 0;
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddInteractiveBrokersExecution(
            configured => Copy(options, configured),
            (_, endpoint) =>
            {
                factoryCalls++;
                return new MockTransport(endpoint);
            },
            store));
        Assert.Equal(0, factoryCalls);
    }

    [Fact]
    public void Registration_IsInertUnlessExplicitlyEnabled()
    {
        var services = new ServiceCollection();

        services.AddInteractiveBrokersExecution();

        Assert.Empty(services);
    }

    [Fact]
    public async Task PaperSession_AuthenticatesExactAccountAndDiscoversNativeAndCanonicalCapabilities()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Equal(ExecutionMode.Paper, harness.Adapter.Mode);
        Assert.True(harness.Transport.Endpoint.IsPaper);
        Assert.True(harness.Adapter.Session.CanExecute);
        Assert.Equal(AccountId, harness.Adapter.NativeAccountId);
        Assert.Contains(InteractiveBrokersNativeOrderType.TrailingStop, harness.Adapter.NativeCapabilities.OrderTypes);
        Assert.Contains(InteractiveBrokersNativeTimeInForce.MarketOnOpen, harness.Adapter.NativeCapabilities.TimeInForce);
        Assert.Equal("STK", harness.Adapter.NativeCapabilities.SelectedAssetClass);
        Assert.Equal(new ScaledQuantity(1), harness.Adapter.NativeCapabilities.MinimumOrderQuantity);
        Assert.Equal(new ScaledQuantity(1), harness.Adapter.NativeCapabilities.QuantityIncrement);
        Assert.Equal(new ScaledPrice(1, 2), harness.Adapter.NativeCapabilities.MinimumPriceIncrement);
        Assert.Equal(SupportedOrderTypes.All, harness.Adapter.Capabilities.CanonicalCapabilities.OrderTypes);
        Assert.Equal(SupportedTimeInForce.All, harness.Adapter.Capabilities.CanonicalCapabilities.TimeInForce);
        Assert.True(harness.Adapter.Capabilities.TradingHours.IsOpen(OmsTestData.TimestampUtc));
        Assert.Equal(1, harness.Transport.ConnectCount);
        Assert.Equal(1, harness.Transport.CapabilityRequestCount);
        Assert.Equal(1, harness.Transport.ReconciliationRequestCount);
    }

    [Fact]
    public async Task LiveWithAllFoundationConditions_UsesMockOnlyAndDirectCommandsStillRequireCoordinatorAdmission()
    {
        var options = LiveOptions();
        var store = LiveConfirmations(options);
        var endpoint = InteractiveBrokersExecutionEndpointGate.Resolve(options, store);
        var transport = new MockTransport(endpoint, isPaperAccount: false);
        var scheduler = new ControllableAdapterEventScheduler();
        await using var adapter = new InteractiveBrokersExecutionAdapter(options, transport, Clock(), scheduler, store);
        await adapter.ConnectAsync();

        var instruction = Instruction("ib-live-direct");
        var result = adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("ib-live-direct"),
            adapter.Capabilities.Version));

        Assert.Equal(ExecutionMode.Live, adapter.Mode);
        Assert.True(adapter.Session.CanExecute);
        Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, result.Status);
        Assert.Equal(BrokerAdapterCommandFault.ExecutionUnavailable, result.Fault);
        Assert.Empty(transport.PlacedOrders);
    }

    [Fact]
    public async Task PriceThatCannotRoundTripThroughNativeDouble_IsRejectedBeforeDispatch()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "ib-inexact-native-price",
            CanonicalOrderType.Limit,
            new ScaledPrice(long.MaxValue, 2));

        var result = harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("ib-inexact-native-price"),
            harness.Adapter.Capabilities.Version));

        Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, result.Status);
        Assert.Equal(BrokerAdapterCommandFault.UnsupportedCapability, result.Fault);
        Assert.Contains("round-trip", result.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Transport.PlacedOrders);
    }

    [Fact]
    public async Task OrderOpenExecutionCommissionAndPosition_FlowThroughCoordinatorIntoExactLedger()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction("ib-ledger-fill");
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));
        Assert.True(released.IsSuccess, released.Reason);
        await WaitUntilAsync(() => harness.Transport.PlacedOrders.Count == 1);
        Assert.True(harness.Scheduler.RunAll() > 0);
        await WaitUntilAsync(() =>
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection?.State == OrderLifecycleState.Working);

        var request = Assert.Single(harness.Transport.PlacedOrders);
        harness.Transport.PublishFill(request, new ScaledMoney(125, 2));
        // Pump the serialized scheduler until the full fill flow lands: the transport enqueues the
        // execution, commission, and position callbacks asynchronously, so drain-until-condition rather
        // than a brittle one-shot RunAll count (fixes an intermittent flake under the parallel suite).
        await WaitUntilAsync(() =>
        {
            harness.Scheduler.RunAll();
            var pending = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection;
            if (pending?.State != OrderLifecycleState.Filled)
                return false;
            var events = harness.Store.Read(instruction.Identity.ClientOrderId);
            return events.Any(item => item.Kind == OrderEventKind.CommissionObserved)
                && events.Any(item => item.Kind == OrderEventKind.PositionObserved);
        });

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var ledger = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(ScaledQuantity.FromWhole(2), projection.FilledQuantity);
        Assert.Equal(new ScaledMoney(125, 2), projection.TotalFees);
        Assert.Equal(new BrokerOrderId(request.OrderId.ToString()), projection.BrokerOrderId);
        Assert.Single(ledger, item => item.Kind == OrderEventKind.CommissionObserved);
        Assert.Single(ledger, item => item.Kind == OrderEventKind.PositionObserved);
        Assert.True(OrderEventChainVerifier.Verify(ledger).IsValid);
        Assert.Equal(
            ScaledQuantity.FromWhole(2),
            Assert.Single(harness.Adapter.CaptureReconciliationSnapshot().Positions).Quantity);
    }

    [Fact]
    public async Task ModifyAndCancel_UseSameNativeOrderIdAndBothLookupForms()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "ib-modify-cancel",
            CanonicalOrderType.Limit,
            new ScaledPrice(10_025, 2));

        var submit = harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("ib-submit"),
            harness.Adapter.Capabilities.Version));
        Assert.True(submit.IsDispatched);
        await WaitUntilAsync(() => harness.Transport.PlacedOrders.Count == 1);
        harness.Scheduler.RunAll();
        var placed = Assert.Single(harness.Transport.PlacedOrders);
        var brokerId = new BrokerOrderId(placed.OrderId.ToString());

        var replacement = instruction.Terms with { LimitPrice = new ScaledPrice(10_050, 2) };
        var replace = harness.Adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByBrokerId(brokerId),
            replacement,
            OmsTestData.Causation("ib-replace"),
            harness.Adapter.Capabilities.Version));
        Assert.True(replace.IsDispatched);
        await WaitUntilAsync(() => harness.Transport.ModifiedOrders.Count == 1);
        harness.Scheduler.RunAll();
        Assert.Equal(placed.OrderId, Assert.Single(harness.Transport.ModifiedOrders).OrderId);
        Assert.Equal(
            replacement,
            harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.CurrentTerms);

        var cancel = harness.Adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
            OmsTestData.Causation("ib-cancel")));
        Assert.True(cancel.IsDispatched);
        await WaitUntilAsync(() => harness.Transport.CancelledOrders.Count == 1);
        harness.Scheduler.RunAll();
        Assert.Equal(placed.OrderId, Assert.Single(harness.Transport.CancelledOrders).OrderId);
        Assert.Equal(
            OrderLifecycleState.Cancelled,
            harness.Adapter.Query(BrokerOrderQuery.ByBrokerId(brokerId)).Order!.State);
    }

    [Fact]
    public async Task CorrelatedNativeRejection_IsMappedWithoutARealSocket()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction("ib-rejected");
        var observed = new List<BrokerAdapterEvent>();
        harness.Adapter.EventReceived += observed.Add;
        var submit = harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("ib-rejected"),
            harness.Adapter.Capabilities.Version));
        Assert.True(submit.IsDispatched);
        await WaitUntilAsync(() => harness.Transport.PlacedOrders.Count == 1);
        harness.Scheduler.RunAll();
        var placed = Assert.Single(harness.Transport.PlacedOrders);

        harness.Transport.PublishError(
            placed with { ClientOrderId = "ib-another-order" },
            201,
            "Mismatched callback must be ignored");
        harness.Scheduler.RunAll();
        Assert.Equal(
            OrderLifecycleState.Working,
            harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.State);

        harness.Transport.PublishError(placed, 201, "Order rejected by deterministic peer");
        harness.Scheduler.RunAll();

        var rejection = Assert.IsType<BrokerOrderEvent>(Assert.Single(
            observed,
            item => item is BrokerOrderEvent { VenueEvent.Kind: VenueEventKind.Rejected }));
        Assert.Contains("deterministic peer", rejection.VenueEvent.Reason, StringComparison.Ordinal);
        Assert.Equal(
            OrderLifecycleState.Rejected,
            harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.State);
    }

    [Fact]
    public async Task ReconciliationSnapshot_ContainsOpenCompletedPositionAndCashEvidence()
    {
        var options = PaperOptions();
        var endpoint = InteractiveBrokersExecutionEndpointGate.Resolve(options);
        var nativeSnapshot = Snapshot(options);
        var unrelatedContract = Contract(options) with { ContractId = 999_999, Symbol = "MSFT" };
        var transport = new MockTransport(endpoint)
        {
            Reconciliation = nativeSnapshot with
            {
                OpenOrders = Array.AsReadOnly(nativeSnapshot.OpenOrders
                    .Append(NativeOrder(
                        7003,
                        "ib-other-contract",
                        options.AccountId,
                        unrelatedContract,
                        InteractiveBrokersNativeOrderStatus.Submitted,
                        0))
                    .ToArray()),
            },
        };
        await using var adapter = new InteractiveBrokersExecutionAdapter(
            options,
            transport,
            Clock(),
            new ControllableAdapterEventScheduler());

        await adapter.ConnectAsync();
        var snapshot = adapter.CaptureReconciliationSnapshot();

        Assert.Equal(adapter.Account, snapshot.Account);
        Assert.Equal(OmsTestData.TimestampUtc, snapshot.CapturedAtUtc);
        Assert.Equal(OrderLifecycleState.Working, Assert.Single(snapshot.OpenOrders).State);
        Assert.Equal(OrderLifecycleState.Filled, Assert.Single(snapshot.CompletedOrders).State);
        Assert.Equal(ScaledQuantity.FromWhole(2), Assert.Single(snapshot.Positions).Quantity);
        var cash = Assert.Single(snapshot.Cash);
        Assert.Equal("USD", cash.Currency);
        Assert.Equal(new ScaledMoney(100_000, 2), cash.Total);
        Assert.Equal(new ScaledMoney(90_000, 2), cash.Available);
    }

    private static InteractiveBrokersExecutionOptions PaperOptions() => new()
    {
        Enabled = true,
        Mode = ExecutionMode.Paper,
        Host = InteractiveBrokersExecutionOptions.DefaultHost,
        Port = InteractiveBrokersExecutionOptions.TwsPaperPort,
        ClientId = 2,
        AccountId = AccountId,
        Symbol = "AAPL",
        SecurityType = "STK",
        Exchange = "SMART",
        PrimaryExchange = "NASDAQ",
        Currency = "USD",
        ContractId = 265598,
        CanonicalInstrumentId = 9001,
    };

    private static InteractiveBrokersExecutionOptions LiveOptions()
    {
        var options = PaperOptions();
        options.Mode = ExecutionMode.Live;
        options.AllowLiveExecution = true;
        options.Port = InteractiveBrokersExecutionOptions.TwsLivePort;
        options.AccountId = "U1234567";
        return options;
    }

    private static void Copy(
        InteractiveBrokersExecutionOptions source,
        InteractiveBrokersExecutionOptions target)
    {
        target.Enabled = source.Enabled;
        target.Mode = source.Mode;
        target.AllowLiveExecution = source.AllowLiveExecution;
        target.Host = source.Host;
        target.Port = source.Port;
        target.ClientId = source.ClientId;
        target.AccountId = source.AccountId;
        target.Symbol = source.Symbol;
        target.SecurityType = source.SecurityType;
        target.Exchange = source.Exchange;
        target.PrimaryExchange = source.PrimaryExchange;
        target.Currency = source.Currency;
        target.ContractId = source.ContractId;
        target.CanonicalInstrumentId = source.CanonicalInstrumentId;
        target.OutsideRegularTradingHours = source.OutsideRegularTradingHours;
        target.MaximumCommandsPerSecond = source.MaximumCommandsPerSecond;
        target.RequestTimeoutMilliseconds = source.RequestTimeoutMilliseconds;
        target.MaximumTrackedOrders = source.MaximumTrackedOrders;
    }

    private static InMemoryLiveExecutionConfirmationStore LiveConfirmations(
        InteractiveBrokersExecutionOptions options)
    {
        var store = new InMemoryLiveExecutionConfirmationStore();
        store.Save(new LiveExecutionConfirmation(
            InteractiveBrokersExecutionOptions.BrokerId,
            options.AccountId,
            LiveExecutionConfirmation.RequiredAcknowledgement,
            OmsTestData.TimestampUtc,
            "test-owner"));
        return store;
    }

    private static InteractiveBrokersReconciliationSnapshot Snapshot(
        InteractiveBrokersExecutionOptions options)
    {
        var contract = Contract(options);
        return new InteractiveBrokersReconciliationSnapshot(
            options.AccountId,
            Array.AsReadOnly([
                NativeOrder(7001, "ib-snapshot-open", options.AccountId, contract, InteractiveBrokersNativeOrderStatus.Submitted, 0),
            ]),
            Array.AsReadOnly([
                NativeOrder(7002, "ib-snapshot-filled", options.AccountId, contract, InteractiveBrokersNativeOrderStatus.Filled, 2),
            ]),
            Array.AsReadOnly([
                new InteractiveBrokersPositionSnapshot(
                    options.AccountId,
                    contract,
                    ScaledQuantity.FromWhole(2),
                    new ScaledPrice(10_025, 2),
                    OmsTestData.TimestampUtc),
            ]),
            Array.AsReadOnly([
                new InteractiveBrokersCashSnapshot(
                    options.AccountId,
                    "USD",
                    new ScaledMoney(100_000, 2),
                    new ScaledMoney(90_000, 2),
                    OmsTestData.TimestampUtc),
            ]),
            OmsTestData.TimestampUtc);
    }

    private static InteractiveBrokersOrderSnapshot NativeOrder(
        int orderId,
        string clientOrderId,
        string accountId,
        InteractiveBrokersContract contract,
        InteractiveBrokersNativeOrderStatus status,
        int filled) => new(
            orderId,
            orderId + 100_000L,
            clientOrderId,
            accountId,
            contract,
            "BUY",
            InteractiveBrokersNativeOrderType.Market,
            InteractiveBrokersNativeTimeInForce.GoodTillCancelled,
            status,
            status.ToString(),
            ScaledQuantity.FromWhole(2),
            ScaledQuantity.FromWhole(filled),
            ScaledQuantity.FromWhole(2 - filled),
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            OmsTestData.TimestampUtc);

    private static InteractiveBrokersContract Contract(InteractiveBrokersExecutionOptions options) => new(
        options.ContractId,
        options.Symbol,
        options.SecurityType,
        options.Exchange,
        options.PrimaryExchange,
        options.Currency);

    private static CanonicalOrderInstruction Instruction(
        string clientOrderId,
        CanonicalOrderType type = CanonicalOrderType.Market,
        ScaledPrice? limit = null) =>
        OmsTestData.Instruction(
            clientOrderId,
            target: 2,
            type,
            CanonicalTimeInForce.GoodTillCancelled,
            limit);

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private static OrderCommandContext Context(CanonicalOrderInstruction instruction, string suffix) => new(
        OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-{suffix}"),
        OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-{suffix}"));

    private static void DraftValidatePrepareAndArm(Harness harness, CanonicalOrderInstruction instruction)
    {
        Assert.True(harness.Service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);
        Assert.True(harness.Coordinator.Validate(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate")).IsSuccess);
        Assert.True(harness.Service.Prepare(
            instruction.Identity.ClientOrderId,
            Context(instruction, "prepare")).IsSuccess);
        Assert.True(harness.Coordinator.Arm(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "arm")).IsSuccess);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TestTimeouts.Deadlock);
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            MockTransport transport,
            ControllableAdapterEventScheduler scheduler,
            InteractiveBrokersExecutionAdapter adapter,
            InMemoryOrderEventStore store,
            OrderManagementService service,
            ExecutionCoordinator coordinator)
        {
            Transport = transport;
            Scheduler = scheduler;
            Adapter = adapter;
            Store = store;
            Service = service;
            Coordinator = coordinator;
        }

        internal MockTransport Transport { get; }
        internal ControllableAdapterEventScheduler Scheduler { get; }
        internal InteractiveBrokersExecutionAdapter Adapter { get; }
        internal InMemoryOrderEventStore Store { get; }
        internal OrderManagementService Service { get; }
        internal ExecutionCoordinator Coordinator { get; }

        internal static async Task<Harness> CreateAsync()
        {
            var options = PaperOptions();
            var transport = new MockTransport(InteractiveBrokersExecutionEndpointGate.Resolve(options));
            var scheduler = new ControllableAdapterEventScheduler();
            var adapter = new InteractiveBrokersExecutionAdapter(options, transport, Clock(), scheduler);
            await adapter.ConnectAsync();
            scheduler.RunAll();
            var store = new InMemoryOrderEventStore();
            var clock = Clock();
            var service = new OrderManagementService(
                store,
                OmsTestData.RiskEngine(),
                new DeterministicSimulatedVenue(clock),
                clock);
            var coordinator = new ExecutionCoordinator(service, adapter);
            return new Harness(transport, scheduler, adapter, store, service, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Adapter.DisposeAsync();
        }
    }

    private sealed class MockTransport : IInteractiveBrokersExecutionTransport
    {
        private readonly object _gate = new();
        private readonly bool _isPaperAccount;
        private int _nextOrderId = 1_000;
        private bool _disposed;

        internal MockTransport(InteractiveBrokersExecutionEndpoint endpoint, bool? isPaperAccount = null)
        {
            Endpoint = endpoint;
            _isPaperAccount = isPaperAccount ?? endpoint.IsPaper;
            Reconciliation = new InteractiveBrokersReconciliationSnapshot(
                endpoint.IsLive ? "U1234567" : AccountId,
                Array.AsReadOnly<InteractiveBrokersOrderSnapshot>([]),
                Array.AsReadOnly<InteractiveBrokersOrderSnapshot>([]),
                Array.AsReadOnly<InteractiveBrokersPositionSnapshot>([]),
                Array.AsReadOnly<InteractiveBrokersCashSnapshot>([]),
                OmsTestData.TimestampUtc);
        }

        public InteractiveBrokersExecutionEndpoint Endpoint { get; }
        public bool IsConnected { get; private set; }
        internal int ConnectCount { get; private set; }
        internal int CapabilityRequestCount { get; private set; }
        internal int ReconciliationRequestCount { get; private set; }
        internal List<InteractiveBrokersOrderRequest> PlacedOrders { get; } = [];
        internal List<InteractiveBrokersOrderRequest> ModifiedOrders { get; } = [];
        internal List<(int OrderId, string ClientOrderId)> CancelledOrders { get; } = [];
        internal InteractiveBrokersReconciliationSnapshot Reconciliation { get; set; }

        public event Action<InteractiveBrokersOrderSnapshot>? OrderUpdated;
        public event Action<InteractiveBrokersExecutionSnapshot>? ExecutionReceived;
        public event Action<InteractiveBrokersCommissionSnapshot>? CommissionReceived;
        public event Action<InteractiveBrokersPositionSnapshot>? PositionUpdated;
        public event Action<InteractiveBrokersOrderError>? OrderError;
        public event Action<Exception>? Faulted;

        public Task<InteractiveBrokersSessionSnapshot> ConnectAsync(
            int clientId,
            string expectedAccountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            ConnectCount++;
            IsConnected = true;
            return Task.FromResult(new InteractiveBrokersSessionSnapshot(
                expectedAccountId,
                _nextOrderId,
                OmsTestData.TimestampUtc,
                _isPaperAccount));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            return Task.CompletedTask;
        }

        public Task<InteractiveBrokersNativeCapabilities> DiscoverCapabilitiesAsync(
            InteractiveBrokersContract contract,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CapabilityRequestCount++;
            return Task.FromResult(new InteractiveBrokersNativeCapabilities(
                Array.AsReadOnly([
                    InteractiveBrokersNativeOrderType.Market,
                    InteractiveBrokersNativeOrderType.Limit,
                    InteractiveBrokersNativeOrderType.Stop,
                    InteractiveBrokersNativeOrderType.StopLimit,
                    InteractiveBrokersNativeOrderType.TrailingStop,
                ]),
                Array.AsReadOnly([
                    InteractiveBrokersNativeTimeInForce.Day,
                    InteractiveBrokersNativeTimeInForce.GoodTillCancelled,
                    InteractiveBrokersNativeTimeInForce.ImmediateOrCancel,
                    InteractiveBrokersNativeTimeInForce.FillOrKill,
                    InteractiveBrokersNativeTimeInForce.MarketOnOpen,
                ]),
                Array.AsReadOnly(["STK", "OPT", "FUT", "CASH", "CFD", "BOND", "FUND", "CMDTY"]),
                contract.SecurityType,
                ScaledQuantity.FromWhole(1),
                ScaledQuantity.FromWhole(1),
                new ScaledPrice(1, 2),
                true,
                BrokerTradingHours.AlwaysOpen,
                BrokerTradingHours.AlwaysOpen,
                "20260805:0000-20260806:0000",
                "20260805:0000-20260806:0000",
                OmsTestData.TimestampUtc));
        }

        public int ReserveOrderId() => Interlocked.Increment(ref _nextOrderId) - 1;

        public Task PlaceOrderAsync(
            InteractiveBrokersOrderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                PlacedOrders.Add(request);
            OrderUpdated?.Invoke(ToOrderSnapshot(request, InteractiveBrokersNativeOrderStatus.Submitted, 0));
            return Task.CompletedTask;
        }

        public Task CancelOrderAsync(
            int orderId,
            string clientOrderId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InteractiveBrokersOrderRequest request;
            lock (_gate)
            {
                CancelledOrders.Add((orderId, clientOrderId));
                request = PlacedOrders.Concat(ModifiedOrders).Last(item => item.OrderId == orderId);
            }
            OrderUpdated?.Invoke(ToOrderSnapshot(request, InteractiveBrokersNativeOrderStatus.Cancelled, 0));
            return Task.CompletedTask;
        }

        public Task ModifyOrderAsync(
            InteractiveBrokersOrderRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                ModifiedOrders.Add(request);
            OrderUpdated?.Invoke(ToOrderSnapshot(request, InteractiveBrokersNativeOrderStatus.Submitted, 0));
            return Task.CompletedTask;
        }

        public Task<InteractiveBrokersReconciliationSnapshot> GetReconciliationSnapshotAsync(
            string accountId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReconciliationRequestCount++;
            return Task.FromResult(Reconciliation);
        }

        internal void PublishFill(InteractiveBrokersOrderRequest request, ScaledMoney fee)
        {
            const string executionId = "ib-exec-1";
            var observed = OmsTestData.TimestampUtc;
            ExecutionReceived?.Invoke(new InteractiveBrokersExecutionSnapshot(
                executionId,
                request.OrderId,
                request.OrderId + 100_000L,
                request.ClientOrderId,
                request.AccountId,
                request.Contract,
                "BOT",
                request.Quantity,
                new ScaledPrice(10_025, 2),
                request.Quantity,
                new ScaledPrice(10_025, 2),
                "20260805 09:30:01 UTC",
                observed));
            CommissionReceived?.Invoke(new InteractiveBrokersCommissionSnapshot(
                executionId,
                fee,
                "USD",
                null,
                observed));
            PositionUpdated?.Invoke(new InteractiveBrokersPositionSnapshot(
                request.AccountId,
                request.Contract,
                request.Quantity,
                new ScaledPrice(10_025, 2),
                observed));
        }

        internal void PublishError(InteractiveBrokersOrderRequest request, int code, string message) =>
            OrderError?.Invoke(new InteractiveBrokersOrderError(
                request.OrderId,
                request.ClientOrderId,
                code,
                message,
                null,
                OmsTestData.TimestampUtc.AddSeconds(1)));

        internal void PublishFault(Exception exception) => Faulted?.Invoke(exception);

        public void Dispose()
        {
            _disposed = true;
            IsConnected = false;
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        private static InteractiveBrokersOrderSnapshot ToOrderSnapshot(
            InteractiveBrokersOrderRequest request,
            InteractiveBrokersNativeOrderStatus status,
            int filled) => new(
                request.OrderId,
                request.OrderId + 100_000L,
                request.ClientOrderId,
                request.AccountId,
                request.Contract,
                request.Side,
                request.OrderType,
                request.TimeInForce,
                status,
                status.ToString(),
                request.Quantity,
                ScaledQuantity.FromWhole(filled),
                ScaledQuantity.FromWhole(2 - filled),
                request.LimitPrice,
                request.StopPrice,
                request.TrailStopPrice,
                request.TrailingPercent,
                request.OutsideRegularTradingHours,
                null,
                null,
                null,
                OmsTestData.TimestampUtc);
    }
}
