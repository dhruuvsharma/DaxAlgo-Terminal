using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.CTrader;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class CTraderExecutionAdapterTests
{
    private const long AccountId = 700_001;
    private const long SymbolId = 11_001;
    private const long NativeOrderId = 91_001;

    [Fact]
    public async Task DemoHandshake_AuthenticatesDiscoversCapabilitiesAndBuildsInitialSnapshot()
    {
        var clock = Clock();
        var options = ValidOptions();
        var transport = new ProtobufMockTransport(
            CTraderExecutionEndpointGate.Resolve(options),
            new MockOpenApiServer(AccountId, SymbolId, includeSnapshotState: true));
        var scheduler = new ControllableAdapterEventScheduler();
        await using var adapter = new CTraderExecutionAdapter(options, transport, clock, scheduler);

        var result = await adapter.ConnectAsync();

        Assert.True(result.IsSuccess, result.Reason);
        Assert.True(adapter.Session.CanExecute);
        Assert.True(adapter.Session.IsDataConnected);
        Assert.True(adapter.Session.IsExecutionAuthenticated);
        Assert.True(adapter.Session.IsExecutionCertified);
        Assert.Equal(ExecutionMode.Paper, adapter.Mode);
        Assert.Equal("ctrader-openapi-demo", adapter.Account.AdapterId.Value);
        Assert.Equal(AccountId.ToString(), adapter.Account.AccountId.Value);
        Assert.Equal((byte)2, adapter.Capabilities.PricePrecision);
        Assert.Equal(new ScaledQuantity(100, 2), adapter.Capabilities.MinimumQuantity);
        Assert.Equal(new ScaledQuantity(100_000, 2), adapter.Capabilities.MaximumQuantity);
        Assert.Equal(new ScaledQuantity(100, 2), adapter.Capabilities.LotSize);
        Assert.Equal(
            SupportedOrderTypes.Market | SupportedOrderTypes.Limit | SupportedOrderTypes.Stop,
            adapter.Capabilities.CanonicalCapabilities.OrderTypes);
        Assert.Equal(
            SupportedTimeInForce.GoodTillCancelled |
            SupportedTimeInForce.ImmediateOrCancel |
            SupportedTimeInForce.FillOrKill,
            adapter.Capabilities.CanonicalCapabilities.TimeInForce);
        Assert.Equal(new ScaledPrice(1, 2), adapter.Capabilities.TickSize);
        Assert.Equal(new ScaledPrice(1, 2), adapter.Capabilities.MinimumPrice);
        Assert.Null(adapter.Capabilities.MaximumPrice);
        Assert.True(adapter.Capabilities.TradingHours.IsValid);
        Assert.True(adapter.Capabilities.TradingHours.IsOpen(OmsTestData.TimestampUtc));
        Assert.Equal(5, adapter.Capabilities.RateLimit.MaximumCommands);
        Assert.Equal(TimeSpan.FromSeconds(1), adapter.Capabilities.RateLimit.Window);

        var snapshot = adapter.CaptureReconciliationSnapshot();
        Assert.Equal(adapter.Account, snapshot.Account);
        Assert.Equal(OmsTestData.TimestampUtc, snapshot.CapturedAtUtc);
        var openOrder = Assert.Single(snapshot.OpenOrders);
        Assert.Equal("ctrader-snapshot-open", openOrder.Instruction.Identity.ClientOrderId.Value);
        Assert.Equal(OrderLifecycleState.Working, openOrder.State);
        Assert.Equal(new BrokerOrderId("92001"), openOrder.BrokerOrderId);
        var completedOrder = Assert.Single(snapshot.CompletedOrders);
        Assert.Equal("ctrader-snapshot-filled", completedOrder.Instruction.Identity.ClientOrderId.Value);
        Assert.Equal(OrderLifecycleState.Filled, completedOrder.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), completedOrder.FilledQuantity);
        var position = Assert.Single(snapshot.Positions);
        Assert.Equal(ScaledQuantity.FromWhole(2), position.Quantity);
        var cash = Assert.Single(snapshot.Cash);
        Assert.Equal("SIM", cash.Currency);
        Assert.Equal(new ScaledMoney(10_000, 2), cash.Total);
        Assert.Equal(new ScaledMoney(10_000, 2), cash.Available);

        Assert.Equal(
            new[]
            {
                typeof(ProtoOAApplicationAuthReq),
                typeof(ProtoOAVersionReq),
                typeof(ProtoOAGetAccountListByAccessTokenReq),
                typeof(ProtoOAAccountAuthReq),
                typeof(ProtoOATraderReq),
                typeof(ProtoOASymbolByIdReq),
                typeof(ProtoOAReconcileReq),
                typeof(ProtoOAOrderListReq),
                typeof(ProtoOATraderReq),
                typeof(ProtoOAAssetListReq),
            },
            transport.Requests.Select(message => message.GetType()).ToArray());
        Assert.True(transport.ProtobufRoundTripCount >= transport.Requests.Count * 2);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("credentials")]
    [InlineData("confirmation")]
    public void LiveEndpoint_RefusesWhenAnyAuthorizationConditionIsMissing(string missing)
    {
        var options = ValidLiveOptions();
        var confirmations = LiveConfirmations(options);
        if (missing == "allow")
            options.AllowLiveExecution = false;
        else if (missing == "credentials")
            options.ClientSecret = string.Empty;
        else
            confirmations = new InMemoryLiveExecutionConfirmationStore();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            CTraderExecutionEndpointGate.Resolve(options, confirmations));

        Assert.Contains("Live", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveEndpoint_WithAllAuthorization_UsesMockTransportAndProvesLiveAccount()
    {
        var options = ValidLiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = CTraderExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new ProtobufMockTransport(
            endpoint,
            new MockOpenApiServer(AccountId, SymbolId, isLive: true));
        await using var adapter = new CTraderExecutionAdapter(
            options,
            transport,
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);

        var result = await adapter.ConnectAsync();

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(ExecutionMode.Live, endpoint.Mode);
        Assert.True(endpoint.IsLive);
        Assert.Equal(ExecutionMode.Live, adapter.Mode);
        Assert.Equal("ctrader-openapi-live", adapter.Account.AdapterId.Value);
        Assert.True(adapter.Session.CanExecute);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task LiveCertifiedAdapter_DirectCommandsAreRefusedBeforeMockTransportDispatch()
    {
        var options = ValidLiveOptions();
        var confirmations = LiveConfirmations(options);
        var transport = new ProtobufMockTransport(
            CTraderExecutionEndpointGate.Resolve(options, confirmations),
            new MockOpenApiServer(AccountId, SymbolId, isLive: true));
        await using var adapter = new CTraderExecutionAdapter(
            options,
            transport,
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);
        var connected = await adapter.ConnectAsync();
        Assert.True(connected.IsSuccess, connected.Reason);
        var requestsBeforeCommands = transport.Requests.Count;
        var instruction = Instruction("ctrader-live-direct");

        var submit = adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("ctrader-live-direct-submit"),
            adapter.Capabilities.Version));
        var cancel = adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
            OmsTestData.Causation("ctrader-live-direct-cancel")));
        var replace = adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
            instruction.Terms,
            OmsTestData.Causation("ctrader-live-direct-replace"),
            adapter.Capabilities.Version));

        Assert.All(new[] { submit, cancel, replace }, result =>
        {
            Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, result.Status);
            Assert.Equal(BrokerAdapterCommandFault.ExecutionUnavailable, result.Fault);
            Assert.Contains("guardrail admission", result.Reason, StringComparison.OrdinalIgnoreCase);
        });
        Assert.True(adapter.Session.CanExecute);
        Assert.Equal(requestsBeforeCommands, transport.Requests.Count);
    }

    [Fact]
    public async Task LiveConfirmationRevokedAfterConstruction_BlocksConnectBeforeTransport()
    {
        var options = ValidLiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = CTraderExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new ProtobufMockTransport(
            endpoint,
            new MockOpenApiServer(AccountId, SymbolId, isLive: true));
        await using var adapter = new CTraderExecutionAdapter(
            options,
            transport,
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);
        Assert.True(confirmations.Remove(CTraderExecutionOptions.BrokerId, AccountId.ToString()));

        var result = await adapter.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CTraderConnectionFault.InvalidConfiguration, result.Fault);
        Assert.False(adapter.Session.CanExecute);
        Assert.Equal(0, transport.ConnectCount);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task LiveConfirmationRevokedAfterCertification_BlocksSubmitCancelAndReplaceWithoutDispatch()
    {
        var options = ValidLiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = CTraderExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new ProtobufMockTransport(
            endpoint,
            new MockOpenApiServer(AccountId, SymbolId, isLive: true));
        var scheduler = new ControllableAdapterEventScheduler();
        await using var adapter = new CTraderExecutionAdapter(
            options,
            transport,
            Clock(),
            scheduler,
            confirmations);
        var connected = await adapter.ConnectAsync();
        Assert.True(connected.IsSuccess, connected.Reason);
        var existing = Instruction(
            "ctrader-live-revoke-existing",
            CanonicalOrderType.Limit,
            limitPrice: new ScaledPrice(10_025, 2));
        var dispatchedBeforeRevocation = transport.Requests.Count;
        Assert.True(confirmations.Remove(CTraderExecutionOptions.BrokerId, AccountId.ToString()));

        var submit = adapter.Submit(new BrokerSubmitCommand(
            Instruction("ctrader-live-revoke-submit"),
            OmsTestData.Causation("ctrader-live-revoke-submit"),
            adapter.Capabilities.Version));
        var cancel = adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(existing.Identity.ClientOrderId),
            OmsTestData.Causation("ctrader-live-revoke-cancel")));
        var replace = adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByClientId(existing.Identity.ClientOrderId),
            existing.Terms with { LimitPrice = new ScaledPrice(10_050, 2) },
            OmsTestData.Causation("ctrader-live-revoke-replace"),
            adapter.Capabilities.Version));

        Assert.All(new[] { submit, cancel, replace }, result =>
        {
            Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, result.Status);
            Assert.Equal(BrokerAdapterCommandFault.ExecutionUnavailable, result.Fault);
        });
        Assert.False(adapter.Session.CanExecute);
        Assert.Equal(dispatchedBeforeRevocation, transport.Requests.Count);
    }

    [Theory]
    [InlineData(ExecutionMode.Paper, true)]
    [InlineData(ExecutionMode.Live, false)]
    public async Task Handshake_RefusesAccountWhoseEnvironmentDoesNotMatchSelectedMode(
        ExecutionMode mode,
        bool serverReportsLive)
    {
        var options = mode == ExecutionMode.Live ? ValidLiveOptions() : ValidOptions();
        var confirmations = mode == ExecutionMode.Live ? LiveConfirmations(options) : null;
        var endpoint = CTraderExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new ProtobufMockTransport(
            endpoint,
            new MockOpenApiServer(AccountId, SymbolId, isLive: serverReportsLive));
        await using var adapter = new CTraderExecutionAdapter(
            options,
            transport,
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);

        var result = await adapter.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CTraderConnectionFault.AccountAuthenticationFailed, result.Fault);
        Assert.False(adapter.Session.CanExecute);
    }

    [Fact]
    public void LiveEndpoint_RefusesPaperHostEvenWithEveryAuthorizationCondition()
    {
        var options = ValidLiveOptions();
        options.Host = CTraderExecutionOptions.DemoHost;

        Assert.Throws<InvalidOperationException>(() =>
            CTraderExecutionEndpointGate.Resolve(options, LiveConfirmations(options)));
    }

    [Fact]
    public async Task MissingCredentials_FailsBeforeMockTransportConnects()
    {
        var options = ValidOptions();
        options.ClientSecret = string.Empty;
        var transport = new ProtobufMockTransport(
            CTraderExecutionEndpointGate.Resolve(options),
            new MockOpenApiServer(AccountId, SymbolId));
        await using var adapter = new CTraderExecutionAdapter(
            options,
            transport,
            Clock(),
            new ControllableAdapterEventScheduler());

        var result = await adapter.ConnectAsync();

        Assert.False(result.IsSuccess);
        Assert.Equal(CTraderConnectionFault.MissingCredentials, result.Fault);
        Assert.Equal(0, transport.ConnectCount);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task NewOrder_ExecutionEventsDriveCoordinatorToFilledAndLedgerExactEvidence()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction("ctrader-fill");
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));

        Assert.True(released.IsSuccess, released.Reason);
        Assert.Equal(OrderLifecycleState.Acknowledging, released.OmsResult.Projection!.State);
        var request = harness.Transport.LastRequest<ProtoOANewOrderReq>();
        Assert.Equal(AccountId, request.CtidTraderAccountId);
        Assert.Equal(SymbolId, request.SymbolId);
        Assert.Equal(ProtoOAOrderType.Market, request.OrderType);
        Assert.Equal(ProtoOATimeInForce.GoodTillCancel, request.TimeInForce);
        Assert.Equal(200, request.Volume);
        Assert.Equal(instruction.Identity.ClientOrderId.Value, request.ClientOrderId);

        harness.Transport.Publish(ExecutionEvent(
            instruction,
            ProtoOAExecutionType.OrderAccepted,
            ProtoOAOrderStatus.OrderStatusAccepted));
        Assert.True(harness.Scheduler.RunAll() > 0);
        Assert.Equal(
            OrderLifecycleState.Working,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);

        harness.Transport.Publish(FillEvent(instruction));
        Assert.True(harness.Scheduler.RunAll() > 0);

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(OrderLifecycleState.Filled, projection.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), projection.FilledQuantity);
        Assert.Equal(new ScaledMoney(5, 2), projection.TotalFees);
        Assert.Equal(new BrokerOrderId(NativeOrderId.ToString()), projection.BrokerOrderId);
        Assert.Single(events, item => item.Kind == OrderEventKind.CommissionObserved);
        Assert.Single(events, item => item.Kind == OrderEventKind.PositionObserved);
        Assert.True(
            events.ToList().FindIndex(item => item.Kind == OrderEventKind.SubmissionRecorded) <
            events.ToList().FindIndex(item => item.Kind == OrderEventKind.VenueAcknowledged));
        Assert.True(
            events.ToList().FindIndex(item => item.Kind == OrderEventKind.VenueAcknowledged) <
            events.ToList().FindIndex(item => item.Kind == OrderEventKind.FillReceived));
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
    }

    [Fact]
    public async Task UnsupportedTimeInForce_IsRejectedBeforeRiskArmingOrOrderDispatch()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = OmsTestData.Instruction(
            "ctrader-day-rejected",
            timeInForce: CanonicalTimeInForce.Day);
        Assert.True(harness.Service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);

        var validation = harness.Coordinator.Validate(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate"));
        var arming = harness.Coordinator.Arm(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "arm"));

        Assert.Equal(OmsCommandFault.UnsupportedCapability, validation.Fault);
        Assert.Equal(OrderLifecycleState.Rejected, validation.Projection!.State);
        Assert.False(arming.IsSuccess);
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.RiskAccepted);
        Assert.DoesNotContain(harness.Transport.Requests, message => message is ProtoOANewOrderReq);
    }

    [Fact]
    public async Task AmendAndCancel_UseRememberedBrokerIdAndRemainQueryableByBothIds()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "ctrader-amend-cancel",
            CanonicalOrderType.Limit,
            limitPrice: new ScaledPrice(10_025, 2));
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        harness.Transport.Publish(ExecutionEvent(
            instruction,
            ProtoOAExecutionType.OrderAccepted,
            ProtoOAOrderStatus.OrderStatusAccepted));
        harness.Scheduler.RunAll();

        var replacementTerms = instruction.Terms with { LimitPrice = new ScaledPrice(10_125, 2) };
        var replaced = await harness.Coordinator.ReplaceAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            replacementTerms,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "replace"));

        Assert.True(replaced.IsSuccess, replaced.Reason);
        var amend = harness.Transport.LastRequest<ProtoOAAmendOrderReq>();
        Assert.Equal(NativeOrderId, amend.OrderId);
        Assert.Equal(200, amend.Volume);
        Assert.Equal(101.25d, amend.LimitPrice);
        harness.Transport.Publish(ExecutionEvent(
            instruction with { Terms = replacementTerms },
            ProtoOAExecutionType.OrderReplaced,
            ProtoOAOrderStatus.OrderStatusAccepted));
        harness.Scheduler.RunAll();

        var brokerOrderId = new BrokerOrderId(NativeOrderId.ToString());
        var byClient = harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId));
        var byBroker = harness.Adapter.Query(BrokerOrderQuery.ByBrokerId(brokerOrderId));
        Assert.True(byClient.Found);
        Assert.True(byBroker.Found);
        Assert.Equal(replacementTerms, byClient.Order!.CurrentTerms);
        Assert.Equal(byClient.Order, byBroker.Order);

        var cancelled = await harness.Coordinator.CancelAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "cancel"));
        Assert.True(cancelled.IsSuccess, cancelled.Reason);
        var cancel = harness.Transport.LastRequest<ProtoOACancelOrderReq>();
        Assert.Equal(NativeOrderId, cancel.OrderId);
        harness.Transport.Publish(ExecutionEvent(
            instruction with { Terms = replacementTerms },
            ProtoOAExecutionType.OrderCancelled,
            ProtoOAOrderStatus.OrderStatusCancelled));
        harness.Scheduler.RunAll();
        Assert.Equal(
            OrderLifecycleState.Cancelled,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);
    }

    [Fact]
    public async Task StopLimit_IsRejectedBeforeDispatchRatherThanSilentlyDowngraded()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "ctrader-stop-limit-rejected",
            CanonicalOrderType.StopLimit,
            limitPrice: new ScaledPrice(10_025, 2),
            stopPrice: new ScaledPrice(10_050, 2));
        Assert.True(harness.Service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);

        var validation = harness.Coordinator.Validate(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate"));

        Assert.Equal(OmsCommandFault.UnsupportedCapability, validation.Fault);
        Assert.Equal(OrderLifecycleState.Rejected, validation.Projection!.State);
        Assert.DoesNotContain(harness.Transport.Requests, message => message is ProtoOANewOrderReq);
    }

    [Fact]
    public async Task ExactLimit_IsNotDowngradedAndCorrelatedVenueRejectionWithoutOrderIdIsLedgered()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "ctrader-rejected",
            CanonicalOrderType.Limit,
            limitPrice: new ScaledPrice(10_025, 2));
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);

        var request = harness.Transport.LastRequest<ProtoOANewOrderReq>();
        Assert.Equal(ProtoOAOrderType.Limit, request.OrderType);
        Assert.Equal(ProtoOATimeInForce.GoodTillCancel, request.TimeInForce);
        Assert.Equal(100.25d, request.LimitPrice);
        Assert.False(request.HasStopPrice);

        harness.Transport.PublishForLastRequest<ProtoOANewOrderReq>(new ProtoOAOrderErrorEvent
        {
            CtidTraderAccountId = AccountId,
            ErrorCode = "TRADING_BAD_STOPS",
            Description = "invalid stops",
        });
        harness.Scheduler.RunAll();

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(OrderLifecycleState.Rejected, projection.State);
        var rejected = Assert.Single(events, item => item.Kind == OrderEventKind.VenueRejected);
        Assert.Equal("TRADING_BAD_STOPS: invalid stops", rejected.Reason);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.FillReceived);
    }

    [Fact]
    public async Task CachedBrokerSnapshot_IsConsumedBySliceSixReconciliation()
    {
        await using var harness = await Harness.CreateAsync(includeReconciliation: true);

        var snapshot = harness.Adapter.CaptureReconciliationSnapshot();
        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);

        Assert.Equal(OmsTestData.TimestampUtc, snapshot.CapturedAtUtc);
        Assert.Single(snapshot.OpenOrders);
        Assert.Single(snapshot.CompletedOrders);
        Assert.Single(snapshot.Positions);
        Assert.Single(snapshot.Cash);
        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.True(cycle.IsAdmissionBlocked);
        Assert.Equal(2, cycle.Cases.Count(item =>
            item.SubjectKind == ReconciliationSubjectKind.Order &&
            item.Kind == ReconciliationCaseKind.LocallyMissing));
        Assert.Contains(cycle.Cases, item =>
            item.SubjectKind == ReconciliationSubjectKind.Position &&
            item.Kind == ReconciliationCaseKind.LocallyMissing);
        Assert.Contains(cycle.Cases, item =>
            item.SubjectKind == ReconciliationSubjectKind.Cash &&
            item.Kind == ReconciliationCaseKind.QuantityMismatch);
        Assert.Equal(1, harness.Transport.Requests.Count(message => message is ProtoOAReconcileReq));
    }

    [Fact]
    public async Task DiRegistration_IsAbsentByDefaultAndPresentOnlyForExplicitMockOptIn()
    {
        var defaults = new ServiceCollection();
        defaults.AddCTraderExecution();
        Assert.DoesNotContain(defaults, item => item.ServiceType == typeof(IBrokerExecutionAdapter));
        Assert.DoesNotContain(defaults, item => item.ServiceType == typeof(ICTraderExecutionTransport));

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(Clock());
        ProtobufMockTransport? mock = null;
        services.AddCTraderExecution(
            ConfigureValidOptions,
            (_, endpoint) => mock = new ProtobufMockTransport(
                endpoint,
                new MockOpenApiServer(AccountId, SymbolId)));
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(ICTraderExecutionTransport));

        await using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<IBrokerExecutionAdapter>();

        Assert.IsType<CTraderExecutionAdapter>(adapter);
        Assert.Null(provider.GetService<ICTraderExecutionTransport>());
        Assert.Equal(0, mock!.ConnectCount);
        Assert.True(mock.Endpoint.IsDemo);
    }

    private static CTraderExecutionOptions ValidOptions()
    {
        var options = new CTraderExecutionOptions();
        ConfigureValidOptions(options);
        return options;
    }

    private static CTraderExecutionOptions ValidLiveOptions()
    {
        var options = ValidOptions();
        options.Mode = ExecutionMode.Live;
        options.Host = CTraderExecutionOptions.LiveHost;
        options.AllowLiveExecution = true;
        return options;
    }

    private static InMemoryLiveExecutionConfirmationStore LiveConfirmations(CTraderExecutionOptions options)
    {
        var store = new InMemoryLiveExecutionConfirmationStore();
        store.Save(new LiveExecutionConfirmation(
            CTraderExecutionOptions.BrokerId,
            options.CtidTraderAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            LiveExecutionConfirmation.RequiredAcknowledgement,
            OmsTestData.TimestampUtc,
            "test-owner"));
        return store;
    }

    private static void ConfigureValidOptions(CTraderExecutionOptions options)
    {
        options.Enabled = true;
        options.Host = CTraderExecutionOptions.DemoHost;
        options.Port = CTraderExecutionOptions.OpenApiPort;
        options.ClientId = NonSecretValue('i');
        options.ClientSecret = NonSecretValue('s');
        options.AccessToken = NonSecretValue('t');
        options.CtidTraderAccountId = AccountId;
        options.SymbolId = SymbolId;
        options.CanonicalInstrumentId = OmsTestData.Instruction().TradeIntent.Instrument.Value;
        options.RequestTimeoutMilliseconds = 1_000;
    }

    private static string NonSecretValue(char marker) => new(marker, 16);

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private static CanonicalOrderInstruction Instruction(
        string clientOrderId,
        CanonicalOrderType orderType = CanonicalOrderType.Market,
        ScaledPrice? limitPrice = null,
        ScaledPrice? stopPrice = null) =>
        OmsTestData.Instruction(
            clientOrderId,
            target: 2,
            orderType,
            CanonicalTimeInForce.GoodTillCancelled,
            limitPrice,
            stopPrice);

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

    private static OrderCommandContext Context(CanonicalOrderInstruction instruction, string suffix) =>
        new(
            OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-{suffix}"),
            OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-{suffix}"));

    private static ProtoOAExecutionEvent ExecutionEvent(
        CanonicalOrderInstruction instruction,
        ProtoOAExecutionType executionType,
        ProtoOAOrderStatus status) =>
        new()
        {
            CtidTraderAccountId = AccountId,
            ExecutionType = executionType,
            Order = NativeOrder(instruction, status),
        };

    private static ProtoOAExecutionEvent FillEvent(CanonicalOrderInstruction instruction)
    {
        var timestamp = TimestampMilliseconds();
        return new ProtoOAExecutionEvent
        {
            CtidTraderAccountId = AccountId,
            ExecutionType = ProtoOAExecutionType.OrderFilled,
            Order = NativeOrder(instruction, ProtoOAOrderStatus.OrderStatusFilled, executedVolume: 200),
            Deal = new ProtoOADeal
            {
                DealId = 51_001,
                OrderId = NativeOrderId,
                PositionId = 61_001,
                SymbolId = SymbolId,
                Volume = 200,
                FilledVolume = 200,
                ExecutionPrice = 100.25d,
                ExecutionTimestamp = timestamp,
                TradeSide = ProtoOATradeSide.Buy,
                DealStatus = ProtoOADealStatus.Filled,
                Commission = -5,
                MoneyDigits = 2,
            },
            Position = new ProtoOAPosition
            {
                PositionId = 61_001,
                TradeData = new ProtoOATradeData
                {
                    SymbolId = SymbolId,
                    Volume = 200,
                    TradeSide = ProtoOATradeSide.Buy,
                    OpenTimestamp = timestamp,
                },
            },
        };
    }

    private static ProtoOAOrder NativeOrder(
        CanonicalOrderInstruction instruction,
        ProtoOAOrderStatus status,
        long executedVolume = 0)
    {
        var terms = instruction.Terms;
        Assert.True(terms.Quantity.TryGetWholeUnits(out var quantity));
        var order = new ProtoOAOrder
        {
            OrderId = NativeOrderId,
            ClientOrderId = instruction.Identity.ClientOrderId.Value,
            OrderType = NativeOrderType(terms.OrderType),
            OrderStatus = status,
            TimeInForce = NativeTimeInForce(terms.TimeInForce),
            ExecutedVolume = executedVolume,
            UtcLastUpdateTimestamp = TimestampMilliseconds(),
            TradeData = new ProtoOATradeData
            {
                SymbolId = SymbolId,
                Volume = checked(quantity * 100),
                TradeSide = terms.Side == OrderSide.Buy ? ProtoOATradeSide.Buy : ProtoOATradeSide.Sell,
                OpenTimestamp = TimestampMilliseconds(),
            },
        };
        if (terms.LimitPrice is { } limit)
            order.LimitPrice = ToDouble(limit);
        if (terms.StopPrice is { } stop)
            order.StopPrice = ToDouble(stop);
        return order;
    }

    private static ProtoOAOrderType NativeOrderType(CanonicalOrderType orderType) => orderType switch
    {
        CanonicalOrderType.Market => ProtoOAOrderType.Market,
        CanonicalOrderType.Limit => ProtoOAOrderType.Limit,
        CanonicalOrderType.Stop => ProtoOAOrderType.Stop,
        CanonicalOrderType.StopLimit => ProtoOAOrderType.StopLimit,
        _ => throw new ArgumentOutOfRangeException(nameof(orderType)),
    };

    private static ProtoOATimeInForce NativeTimeInForce(CanonicalTimeInForce timeInForce) => timeInForce switch
    {
        CanonicalTimeInForce.GoodTillCancelled => ProtoOATimeInForce.GoodTillCancel,
        CanonicalTimeInForce.ImmediateOrCancel => ProtoOATimeInForce.ImmediateOrCancel,
        CanonicalTimeInForce.FillOrKill => ProtoOATimeInForce.FillOrKill,
        _ => throw new ArgumentOutOfRangeException(nameof(timeInForce)),
    };

    private static double ToDouble(ScaledPrice price) =>
        (double)((decimal)price.Coefficient / (decimal)Math.Pow(10, price.Scale));

    private static long TimestampMilliseconds() =>
        new DateTimeOffset(OmsTestData.TimestampUtc).ToUnixTimeMilliseconds();

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            SimClock clock,
            ProtobufMockTransport transport,
            ControllableAdapterEventScheduler scheduler,
            CTraderExecutionAdapter adapter,
            InMemoryOrderEventStore store,
            OrderManagementService service,
            ExecutionCoordinator coordinator)
        {
            Clock = clock;
            Transport = transport;
            Scheduler = scheduler;
            Adapter = adapter;
            Store = store;
            Service = service;
            Coordinator = coordinator;
        }

        internal SimClock Clock { get; }
        internal ProtobufMockTransport Transport { get; }
        internal ControllableAdapterEventScheduler Scheduler { get; }
        internal CTraderExecutionAdapter Adapter { get; }
        internal InMemoryOrderEventStore Store { get; }
        internal OrderManagementService Service { get; }
        internal ExecutionCoordinator Coordinator { get; }

        internal static async Task<Harness> CreateAsync(bool includeReconciliation = false)
        {
            var clock = CTraderExecutionAdapterTests.Clock();
            var options = ValidOptions();
            var transport = new ProtobufMockTransport(
                CTraderExecutionEndpointGate.Resolve(options),
                new MockOpenApiServer(AccountId, SymbolId, includeSnapshotState: includeReconciliation));
            var scheduler = new ControllableAdapterEventScheduler();
            var adapter = new CTraderExecutionAdapter(options, transport, clock, scheduler);
            var connected = await adapter.ConnectAsync();
            Assert.True(connected.IsSuccess, connected.Reason);

            var store = new InMemoryOrderEventStore();
            var venue = new DeterministicSimulatedVenue(clock);
            var service = new OrderManagementService(store, OmsTestData.RiskEngine(), venue, clock);
            var coordinator = includeReconciliation
                ? new ExecutionCoordinator(
                    service,
                    adapter,
                    new ReconciliationEngine(service, new InMemoryReconciliationCaseStore(), clock))
                : new ExecutionCoordinator(service, adapter);
            return new Harness(clock, transport, scheduler, adapter, store, service, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Adapter.DisposeAsync();
        }
    }

    private sealed class ProtobufMockTransport(
        CTraderExecutionEndpoint endpoint,
        MockOpenApiServer server) : ICTraderExecutionTransport
    {
        private readonly object _gate = new();
        private readonly List<IMessage> _requests = [];
        private readonly List<(IMessage Request, string? ClientMessageId)> _requestEnvelopes = [];
        private bool _isConnected;
        private int _roundTrips;

        public CTraderExecutionEndpoint Endpoint { get; } = endpoint;

        public bool IsConnected
        {
            get
            {
                lock (_gate)
                    return _isConnected;
            }
        }

        internal int ConnectCount { get; private set; }

        internal int ProtobufRoundTripCount => Volatile.Read(ref _roundTrips);

        internal IReadOnlyList<IMessage> Requests
        {
            get
            {
                lock (_gate)
                    return _requests.ToArray();
            }
        }

        public event Action<ProtoMessage>? MessageReceived;

        public event Action<Exception>? Faulted;

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ConnectCount++;
                _isConnected = true;
            }
            return Task.CompletedTask;
        }

        public Task SendAsync(ProtoMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected)
                throw new InvalidOperationException("The in-process cTrader peer is disconnected.");

            var envelope = RoundTrip(message);
            var decoded = CTraderOpenApiProtocol.Decode(envelope) ??
                throw new InvalidDataException("The in-process peer could not decode a cTrader request.");
            lock (_gate)
            {
                _requests.Add(decoded);
                _requestEnvelopes.Add((decoded, envelope.HasClientMsgId ? envelope.ClientMsgId : null));
            }

            var response = server.Respond(decoded);
            if (response is not null)
                Deliver(response, envelope.HasClientMsgId ? envelope.ClientMsgId : null);
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                _isConnected = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
                _isConnected = false;
            return ValueTask.CompletedTask;
        }

        internal T LastRequest<T>() where T : class, IMessage =>
            Requests.OfType<T>().Last();

        internal void Publish(IMessage message)
        {
            if (!IsConnected)
                throw new InvalidOperationException("The in-process cTrader peer is disconnected.");
            Deliver(message, null);
        }

        internal void PublishForLastRequest<TRequest>(IMessage message) where TRequest : class, IMessage
        {
            string? clientMessageId;
            lock (_gate)
            {
                clientMessageId = _requestEnvelopes
                    .Last(item => item.Request is TRequest)
                    .ClientMessageId;
            }
            if (string.IsNullOrWhiteSpace(clientMessageId))
                throw new InvalidOperationException("The in-process request did not carry a client message ID.");
            Deliver(message, clientMessageId);
        }

        internal void PublishFault(Exception exception) => Faulted?.Invoke(exception);

        private void Deliver(IMessage message, string? clientMessageId)
        {
            var envelope = RoundTrip(CTraderOpenApiProtocol.Encode(message, clientMessageId));
            MessageReceived?.Invoke(envelope);
        }

        private ProtoMessage RoundTrip(ProtoMessage message)
        {
            Interlocked.Increment(ref _roundTrips);
            return ProtoMessage.Parser.ParseFrom(message.ToByteArray());
        }
    }

    private sealed class MockOpenApiServer(
        long accountId,
        long symbolId,
        bool includeSnapshotState = false,
        bool isLive = false)
    {
        internal IMessage? Respond(IMessage request) => request switch
        {
            ProtoOAApplicationAuthReq => new ProtoOAApplicationAuthRes(),
            ProtoOAVersionReq => new ProtoOAVersionRes { Version = "2.0" },
            ProtoOAGetAccountListByAccessTokenReq => AccountList(),
            ProtoOAAccountAuthReq => new ProtoOAAccountAuthRes { CtidTraderAccountId = accountId },
            ProtoOATraderReq => TraderResponse(),
            ProtoOASymbolByIdReq => SymbolResponse(),
            ProtoOAReconcileReq => ReconcileResponse(),
            ProtoOAOrderListReq => OrderListResponse(),
            ProtoOAAssetListReq => AssetResponse(),
            ProtoOANewOrderReq or ProtoOAAmendOrderReq or ProtoOACancelOrderReq => null,
            _ => throw new InvalidOperationException($"Unexpected in-process cTrader request {request.GetType().Name}."),
        };

        private ProtoOAGetAccountListByAccessTokenRes AccountList()
        {
            var response = new ProtoOAGetAccountListByAccessTokenRes
            {
                PermissionScope = ProtoOAClientPermissionScope.ScopeTrade,
            };
            response.CtidTraderAccount.Add(new ProtoOACtidTraderAccount
            {
                CtidTraderAccountId = (ulong)accountId,
                IsLive = isLive,
            });
            return response;
        }

        private ProtoOATraderRes TraderResponse() => new()
        {
            CtidTraderAccountId = accountId,
            Trader = new ProtoOATrader
            {
                CtidTraderAccountId = accountId,
                AccessRights = ProtoOAAccessRights.FullAccess,
                Balance = includeSnapshotState ? 10_000 : 0,
                MoneyDigits = 2,
                DepositAssetId = 1,
            },
        };

        private ProtoOAReconcileRes ReconcileResponse()
        {
            var response = new ProtoOAReconcileRes { CtidTraderAccountId = accountId };
            if (!includeSnapshotState)
                return response;

            var openOrder = NativeOrder(
                Instruction(
                    "ctrader-snapshot-open",
                    CanonicalOrderType.Limit,
                    limitPrice: new ScaledPrice(10_025, 2)),
                ProtoOAOrderStatus.OrderStatusAccepted);
            openOrder.OrderId = 92_001;
            response.Order.Add(openOrder);
            response.Position.Add(new ProtoOAPosition
            {
                PositionId = 62_001,
                UsedMargin = 250,
                MoneyDigits = 2,
                TradeData = new ProtoOATradeData
                {
                    SymbolId = symbolId,
                    Volume = 200,
                    TradeSide = ProtoOATradeSide.Buy,
                    OpenTimestamp = TimestampMilliseconds(),
                },
            });
            return response;
        }

        private ProtoOAOrderListRes OrderListResponse()
        {
            var response = new ProtoOAOrderListRes
            {
                CtidTraderAccountId = accountId,
                HasMore = false,
            };
            if (!includeSnapshotState)
                return response;

            var completedOrder = NativeOrder(
                Instruction("ctrader-snapshot-filled"),
                ProtoOAOrderStatus.OrderStatusFilled,
                executedVolume: 200);
            completedOrder.OrderId = 92_002;
            response.Order.Add(completedOrder);
            return response;
        }

        private ProtoOASymbolByIdRes SymbolResponse()
        {
            var response = new ProtoOASymbolByIdRes { CtidTraderAccountId = accountId };
            var symbol = new ProtoOASymbol
            {
                SymbolId = symbolId,
                Digits = 2,
                MinVolume = 100,
                MaxVolume = 100_000,
                StepVolume = 100,
                TradingMode = ProtoOATradingMode.Enabled,
                EnableShortSelling = true,
                ScheduleTimeZone = "UTC",
            };
            symbol.Schedule.Add(new ProtoOAInterval
            {
                StartSecond = 0,
                EndSecond = 7 * 24 * 60 * 60,
            });
            response.Symbol.Add(symbol);
            return response;
        }

        private ProtoOAAssetListRes AssetResponse()
        {
            var response = new ProtoOAAssetListRes { CtidTraderAccountId = accountId };
            response.Asset.Add(new ProtoOAAsset
            {
                AssetId = 1,
                Name = "SIM",
            });
            return response;
        }
    }
}
