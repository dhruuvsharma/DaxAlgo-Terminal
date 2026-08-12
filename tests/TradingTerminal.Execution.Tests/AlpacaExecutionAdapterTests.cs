using System.Net;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.Oms;
using TradingTerminal.Execution.Service;

namespace TradingTerminal.Execution.Tests;

public sealed class AlpacaExecutionAdapterTests
{
    private const string AccountId = "paper-account-001";
    private const string Symbol = "AAPL";

    [Fact]
    public async Task PaperAuthentication_DiscoversNativeAndCanonicalCapabilitiesAndLatestReferencePrice()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.True(harness.Adapter.Session.CanExecute);
        Assert.Equal(ExecutionMode.Paper, harness.Adapter.Mode);
        Assert.Equal("alpaca-paper", harness.Adapter.AdapterId);
        Assert.Equal(AccountId, harness.Adapter.NativeAccountId);
        Assert.Equal(Symbol, harness.Adapter.Symbol);
        Assert.Equal(new ScaledPrice(10_025, 2), harness.Adapter.LatestReferencePrice);
        Assert.Equal(OmsTestData.TimestampUtc, harness.Adapter.LatestReferencePriceObservedAtUtc);
        Assert.Equal(OmsTestData.TimestampUtc, harness.Adapter.LatestReferencePriceFetchedAtUtc);
        Assert.Contains("trailing_stop", harness.Adapter.NativeCapabilities.OrderTypes);
        Assert.Contains("opg", harness.Adapter.NativeCapabilities.TimeInForce);
        Assert.Contains("cls", harness.Adapter.NativeCapabilities.TimeInForce);
        Assert.Contains("us_equity", harness.Adapter.NativeCapabilities.AssetClasses);
        Assert.Contains("crypto", harness.Adapter.NativeCapabilities.AssetClasses);
        Assert.True(harness.Adapter.NativeCapabilities.SupportsFractionalQuantity);
        Assert.True(harness.Adapter.NativeCapabilities.SupportsNotionalOrders);
        Assert.False(harness.Adapter.Capabilities.SupportsFractionalQuantity);
        Assert.Equal((byte)0, harness.Adapter.Capabilities.QuantityPrecision);
        Assert.Equal(ScaledQuantity.FromWhole(1), harness.Adapter.Capabilities.LotSize);
        Assert.Equal(SupportedOrderTypes.All, harness.Adapter.Capabilities.CanonicalCapabilities.OrderTypes);
        Assert.Equal(SupportedTimeInForce.All, harness.Adapter.Capabilities.CanonicalCapabilities.TimeInForce);
        Assert.True(harness.Source.IsRunning);
        Assert.Equal(1, harness.Transport.ConnectCount);
    }

    [Fact]
    public async Task TradingBlockedAccount_RemainsConnectedDataOnlyAndLatestTradeFailureDoesNotFakePrice()
    {
        var transport = new MockTransport(Endpoint())
        {
            Account = ActiveAccount() with { TradingBlocked = true },
            LatestTradeFailure = new AlpacaApiException(HttpStatusCode.Forbidden, "40310000", "data denied"),
        };
        await using var adapter = CreateAdapter(transport, new ManualTradeUpdateSource(), out _);

        await adapter.ConnectAsync("paper-key", "paper-secret");

        Assert.True(adapter.Session.IsDataConnected);
        Assert.False(adapter.Session.IsExecutionAuthenticated);
        Assert.False(adapter.Session.IsExecutionCertified);
        Assert.True(adapter.IsDataOnly);
        Assert.Null(adapter.LatestReferencePrice);
        Assert.Null(adapter.LatestReferencePriceObservedAtUtc);
        Assert.Null(adapter.LatestReferencePriceFetchedAtUtc);
    }

    [Fact]
    public async Task CryptoDiscovery_AdvertisesOnlyExactAssetSpecificCanonicalSubset()
    {
        var transport = new MockTransport(Endpoint())
        {
            Asset = new AlpacaAssetSnapshot(
                "BTC/USD",
                "crypto",
                true,
                true,
                new ScaledQuantity(1, 8),
                new ScaledQuantity(1, 8),
                new ScaledPrice(1, 2)),
        };
        var options = Options();
        options.Symbol = "BTC/USD";
        var source = new ManualTradeUpdateSource();
        var scheduler = new ControllableAdapterEventScheduler();
        await using var adapter = new AlpacaExecutionAdapter(options, transport, source, Clock(), scheduler);

        await adapter.ConnectAsync("paper-key", "paper-secret");

        Assert.Equal(
            SupportedOrderTypes.Market | SupportedOrderTypes.Limit | SupportedOrderTypes.StopLimit,
            adapter.Capabilities.CanonicalCapabilities.OrderTypes);
        Assert.Equal(
            SupportedTimeInForce.GoodTillCancelled | SupportedTimeInForce.ImmediateOrCancel,
            adapter.Capabilities.CanonicalCapabilities.TimeInForce);
        Assert.DoesNotContain("trailing_stop", adapter.NativeCapabilities.OrderTypes);
        Assert.Equal(new[] { "gtc", "ioc" }, adapter.NativeCapabilities.TimeInForce);
        Assert.True(adapter.Session.IsExecutionAuthenticated);
        Assert.False(adapter.Session.IsExecutionCertified);
        Assert.False(adapter.Session.CanExecute);
    }

    [Theory]
    [InlineData("allow")]
    [InlineData("credentials")]
    [InlineData("confirmation")]
    public void LiveEndpoint_IsRefusedBeforeTransportFactoryWhenAnyAuthorizationConditionIsMissing(string missing)
    {
        var factoryCalls = 0;
        var services = new ServiceCollection();
        var configured = LiveOptions();
        var confirmations = LiveConfirmations(configured);
        if (missing == "allow")
            configured.AllowLiveExecution = false;
        else if (missing == "credentials")
            configured.SecretKey = string.Empty;
        else
            confirmations = new InMemoryLiveExecutionConfirmationStore();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddAlpacaExecution(
            options =>
            {
                Copy(configured, options);
            },
            (_, endpoint) =>
            {
                factoryCalls++;
                return new MockTransport(endpoint);
            },
            confirmationStore: confirmations));

        Assert.Equal(0, factoryCalls);
        Assert.Contains("Live", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LiveEndpoint_WithAllAuthorization_UsesMockTransportAndExactAccountBinding()
    {
        var options = LiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = AlpacaExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new MockTransport(endpoint);
        var source = new ManualTradeUpdateSource();
        var scheduler = new ControllableAdapterEventScheduler();
        await using var adapter = new AlpacaExecutionAdapter(
            options,
            transport,
            source,
            Clock(),
            scheduler,
            confirmations);

        await adapter.ConnectAsync(options.KeyId, options.SecretKey);

        Assert.Equal(ExecutionMode.Live, endpoint.Mode);
        Assert.True(endpoint.IsLive);
        Assert.Equal(ExecutionMode.Live, adapter.Mode);
        Assert.Equal("alpaca-live", adapter.AdapterId);
        Assert.Equal(AccountId, adapter.Account.AccountId.Value);
        Assert.True(adapter.Session.CanExecute);
        Assert.Equal(1, transport.ConnectCount);
    }

    [Fact]
    public async Task LiveCertifiedAdapter_DirectCommandsAreRefusedBeforeMockTransportDispatch()
    {
        var options = LiveOptions();
        var confirmations = LiveConfirmations(options);
        var transport = new MockTransport(AlpacaExecutionEndpointGate.Resolve(options, confirmations));
        await using var adapter = new AlpacaExecutionAdapter(
            options,
            transport,
            new ManualTradeUpdateSource(),
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);
        await adapter.ConnectAsync(options.KeyId, options.SecretKey);
        var instruction = Instruction("alpaca-live-direct");

        var submit = adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-live-direct-submit"),
            adapter.Capabilities.Version));
        var cancel = adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
            OmsTestData.Causation("alpaca-live-direct-cancel")));
        var replace = adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
            instruction.Terms,
            OmsTestData.Causation("alpaca-live-direct-replace"),
            adapter.Capabilities.Version));

        Assert.All(new[] { submit, cancel, replace }, result =>
        {
            Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, result.Status);
            Assert.Equal(BrokerAdapterCommandFault.ExecutionUnavailable, result.Fault);
            Assert.Contains("guardrail admission", result.Reason, StringComparison.OrdinalIgnoreCase);
        });
        Assert.True(adapter.Session.CanExecute);
        Assert.Empty(transport.Submits);
        Assert.Empty(transport.CancelledOrderIds);
        Assert.Empty(transport.ReplacedOrderIds);
    }

    [Fact]
    public async Task LiveCertifiedAdapter_GuardedExecutionServiceEngineCommandsUseMockTransport()
    {
        var options = LiveOptions();
        var confirmations = LiveConfirmations(options);
        var transport = new MockTransport(AlpacaExecutionEndpointGate.Resolve(options, confirmations));
        var adapterScheduler = new ControllableAdapterEventScheduler();
        var source = new ManualTradeUpdateSource();
        await using var adapter = new AlpacaExecutionAdapter(
            options,
            transport,
            source,
            Clock(),
            adapterScheduler,
            confirmations);
        await adapter.ConnectAsync(options.KeyId, options.SecretKey);

        var clock = Clock();
        var ledger = new InMemoryOrderEventStore();
        var caseStore = new InMemoryReconciliationCaseStore();
        var venue = new DeterministicSimulatedVenue(
            clock,
            adapter.Capabilities.CanonicalCapabilities,
            Array.Empty<VenueSubmitPlan>());
        var oms = new OrderManagementService(ledger, OmsTestData.RiskEngine(), venue, clock);
        var openingCash = Assert.Single(adapter.CaptureReconciliationSnapshot().Cash);
        var reconciliation = new ReconciliationEngine(
            oms,
            caseStore,
            clock,
            new ReconciliationCashBasis(
                openingCash.Currency,
                openingCash.Total,
                openingCash.Available,
                CompareAvailable: false));
        var acquired = ExecutionLease.Acquire(
            adapter.Account,
            new InMemoryExecutionLeaseStore(),
            clock,
            new ExecutionLeaseId($"alpaca-live-engine-{Guid.NewGuid():N}"));
        Assert.True(acquired.IsSuccess, acquired.Reason);
        using var lease = acquired.Lease!;
        using var coordinator = new ExecutionCoordinator(
            oms,
            [adapter],
            reconciliation,
            [lease]);
        var engineScheduler = new ControllableAdapterEventScheduler();
        var engine = new ExecutionServiceEngine(ledger, oms, coordinator, engineScheduler, lease);
        var instruction = Instruction(
            "alpaca-live-engine",
            CanonicalOrderType.Limit,
            new ScaledPrice(10_025, 2));
        instruction = instruction with
        {
            Identity = instruction.Identity with
            {
                ExecutionLeaseId = lease.Grant.LeaseId,
                FencingToken = lease.Grant.FencingToken,
            },
        };

        var exchange = engine.Handle(new ExecutionServiceRequest(
            ExecutionServiceProtocol.CurrentVersion,
            "alpaca-live-engine-submit",
            ExecutionServiceRequestKind.Submit,
            adapter.Account,
            lease.Grant.LeaseId,
            lease.Grant.FencingToken,
            Submit: new ExecutionSubmitRequest(instruction, OmsTestData.RiskSnapshot())));

        Assert.True(exchange.Response.IsSuccess, exchange.Response.Reason);
        Assert.Single(transport.Submits);
        Assert.Equal(instruction.Identity.ClientOrderId.Value, transport.Submits[0].ClientOrderId);
        Assert.True(adapterScheduler.RunAll() > 0);

        var replacementTerms = instruction.Terms with { LimitPrice = new ScaledPrice(10_050, 2) };
        var replaced = engine.Handle(new ExecutionServiceRequest(
            ExecutionServiceProtocol.CurrentVersion,
            "alpaca-live-engine-replace",
            ExecutionServiceRequestKind.Replace,
            adapter.Account,
            lease.Grant.LeaseId,
            lease.Grant.FencingToken,
            Replace: new ExecutionReplaceRequest(
                instruction.Identity.ClientOrderId,
                replacementTerms,
                OmsTestData.RiskSnapshot())));
        Assert.True(replaced.Response.IsSuccess, replaced.Response.Reason);
        Assert.Single(transport.ReplacedOrderIds);

        transport.SubmitOrderId = "paper-order-cancel";
        var cancelInstruction = Instruction("alpaca-live-engine-cancel");
        cancelInstruction = cancelInstruction with
        {
            Identity = cancelInstruction.Identity with
            {
                ExecutionLeaseId = lease.Grant.LeaseId,
                FencingToken = lease.Grant.FencingToken,
            },
        };
        var cancelSubmit = engine.Handle(new ExecutionServiceRequest(
            ExecutionServiceProtocol.CurrentVersion,
            "alpaca-live-engine-cancel-submit",
            ExecutionServiceRequestKind.Submit,
            adapter.Account,
            lease.Grant.LeaseId,
            lease.Grant.FencingToken,
            Submit: new ExecutionSubmitRequest(cancelInstruction, OmsTestData.RiskSnapshot())));
        Assert.True(cancelSubmit.Response.IsSuccess, cancelSubmit.Response.Reason);
        Assert.True(adapterScheduler.RunAll() > 0);
        var cancelled = engine.Handle(new ExecutionServiceRequest(
            ExecutionServiceProtocol.CurrentVersion,
            "alpaca-live-engine-cancel",
            ExecutionServiceRequestKind.Cancel,
            adapter.Account,
            lease.Grant.LeaseId,
            lease.Grant.FencingToken,
            Cancel: new ExecutionCancelRequest(cancelInstruction.Identity.ClientOrderId)));
        Assert.True(cancelled.Response.IsSuccess, cancelled.Response.Reason);
        Assert.Single(transport.CancelledOrderIds);
    }

    [Fact]
    public async Task LiveConfirmationRevokedAfterConstruction_BlocksConnectBeforeTransport()
    {
        var options = LiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = AlpacaExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new MockTransport(endpoint);
        await using var adapter = new AlpacaExecutionAdapter(
            options,
            transport,
            new ManualTradeUpdateSource(),
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);
        Assert.True(confirmations.Remove(AlpacaExecutionOptions.BrokerId, AccountId));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            adapter.ConnectAsync(options.KeyId, options.SecretKey));

        Assert.Contains("authorization", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(adapter.Session.CanExecute);
        Assert.Equal(0, transport.ConnectCount);
    }

    [Fact]
    public async Task LiveConfirmationRevokedAfterCertification_BlocksSubmitCancelAndReplaceWithoutDispatch()
    {
        var options = LiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = AlpacaExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new MockTransport(endpoint);
        var scheduler = new ControllableAdapterEventScheduler();
        await using var adapter = new AlpacaExecutionAdapter(
            options,
            transport,
            new ManualTradeUpdateSource(),
            Clock(),
            scheduler,
            confirmations);
        await adapter.ConnectAsync(options.KeyId, options.SecretKey);
        var existing = Instruction(
            "alpaca-live-revoke-existing",
            CanonicalOrderType.Limit,
            new ScaledPrice(10_025, 2));
        var submitsBeforeRevocation = transport.Submits.Count;
        var cancelsBeforeRevocation = transport.CancelledOrderIds.Count;
        var replacesBeforeRevocation = transport.ReplacedOrderIds.Count;
        Assert.True(confirmations.Remove(AlpacaExecutionOptions.BrokerId, AccountId));

        var submit = adapter.Submit(new BrokerSubmitCommand(
            Instruction("alpaca-live-revoke-submit"),
            OmsTestData.Causation("alpaca-live-revoke-submit"),
            adapter.Capabilities.Version));
        var cancel = adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(existing.Identity.ClientOrderId),
            OmsTestData.Causation("alpaca-live-revoke-cancel")));
        var replace = adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByClientId(existing.Identity.ClientOrderId),
            existing.Terms with { LimitPrice = new ScaledPrice(10_050, 2) },
            OmsTestData.Causation("alpaca-live-revoke-replace"),
            adapter.Capabilities.Version));

        Assert.All(new[] { submit, cancel, replace }, result =>
        {
            Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, result.Status);
            Assert.Equal(BrokerAdapterCommandFault.ExecutionUnavailable, result.Fault);
        });
        Assert.False(adapter.Session.CanExecute);
        Assert.Equal(submitsBeforeRevocation, transport.Submits.Count);
        Assert.Equal(cancelsBeforeRevocation, transport.CancelledOrderIds.Count);
        Assert.Equal(replacesBeforeRevocation, transport.ReplacedOrderIds.Count);
    }

    [Fact]
    public async Task LiveAuthentication_RefusesAccountThatDoesNotMatchPersistedBinding()
    {
        var options = LiveOptions();
        var confirmations = LiveConfirmations(options);
        var endpoint = AlpacaExecutionEndpointGate.Resolve(options, confirmations);
        var transport = new MockTransport(endpoint)
        {
            Account = ActiveAccount() with { AccountId = "different-live-account" },
        };
        await using var adapter = new AlpacaExecutionAdapter(
            options,
            transport,
            new ManualTradeUpdateSource(),
            Clock(),
            new ControllableAdapterEventScheduler(),
            confirmations);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.ConnectAsync(options.KeyId, options.SecretKey));

        Assert.False(adapter.Session.CanExecute);
    }

    [Fact]
    public void LiveEndpoint_RefusesPaperUrlEvenWithEveryAuthorizationCondition()
    {
        var options = LiveOptions();
        options.BaseUrl = AlpacaExecutionOptions.PaperBaseUrl;

        Assert.Throws<InvalidOperationException>(() =>
            AlpacaExecutionEndpointGate.Resolve(options, LiveConfirmations(options)));
    }

    [Fact]
    public async Task DiRegistration_IsAbsentByDefaultAndMockOptInDoesNotConnect()
    {
        var defaults = new ServiceCollection();
        defaults.AddAlpacaExecution();
        Assert.DoesNotContain(defaults, item => item.ServiceType == typeof(IBrokerExecutionAdapter));
        Assert.DoesNotContain(defaults, item => item.ServiceType == typeof(IAlpacaExecutionTransport));

        var services = new ServiceCollection();
        services.AddSingleton<IClock>(Clock());
        MockTransport? mock = null;
        services.AddAlpacaExecution(
            Configure,
            (_, endpoint) => mock = new MockTransport(endpoint),
            _ => new ManualTradeUpdateSource());
        Assert.DoesNotContain(services, item => item.ServiceType == typeof(IAlpacaExecutionTransport));

        await using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<IBrokerExecutionAdapter>();

        Assert.IsType<AlpacaExecutionAdapter>(adapter);
        Assert.Null(provider.GetService<IAlpacaExecutionTransport>());
        Assert.Equal(0, mock!.ConnectCount);
        Assert.True(mock.Endpoint.IsPaper);
    }

    [Fact]
    public async Task AcceptedThenFilled_TravelsThroughCoordinatorIntoExactLedgerEvidence()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction("alpaca-fill");
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));
        Assert.True(released.IsSuccess, released.Reason);
        Assert.Equal(OrderLifecycleState.Acknowledging, released.OmsResult.Projection!.State);
        Assert.Equal(instruction.Identity.ClientOrderId.Value, Assert.Single(harness.Transport.Submits).ClientOrderId);

        Assert.True(harness.Scheduler.RunAll() > 0);
        Assert.Equal(
            OrderLifecycleState.Working,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);

        harness.Source.Publish(Order(instruction.Identity.ClientOrderId.Value, "filled", filled: 2));
        Assert.True(harness.Scheduler.RunAll() > 0);

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(OrderLifecycleState.Filled, projection.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), projection.FilledQuantity);
        Assert.Equal(ScaledMoney.Zero, projection.TotalFees);
        Assert.Equal(new BrokerOrderId("paper-order-001"), projection.BrokerOrderId);
        Assert.Single(events, item => item.Kind == OrderEventKind.PositionObserved);
        Assert.Equal(
            ScaledQuantity.FromWhole(2),
            Assert.Single(harness.Adapter.CaptureReconciliationSnapshot().Positions).Quantity);
        Assert.True(
            events.ToList().FindIndex(item => item.Kind == OrderEventKind.SubmissionRecorded) <
            events.ToList().FindIndex(item => item.Kind == OrderEventKind.VenueAcknowledged));
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
    }

    [Fact]
    public async Task ReplaceAndCancel_UseBrokerIdAndRemainQueryableByClientAndBrokerId()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "alpaca-replace-cancel",
            CanonicalOrderType.Limit,
            new ScaledPrice(10_025, 2));
        var submitted = harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-direct-submit"),
            harness.Adapter.Capabilities.Version));
        Assert.True(submitted.IsDispatched);
        Assert.True(harness.Scheduler.RunAll() > 0);

        var replacement = instruction.Terms with
        {
            Quantity = ScaledQuantity.FromWhole(3),
            LimitPrice = new ScaledPrice(10_050, 2),
        };
        var replace = harness.Adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByBrokerId(new BrokerOrderId("paper-order-001")),
            replacement,
            OmsTestData.Causation("alpaca-replace"),
            harness.Adapter.Capabilities.Version));
        Assert.True(replace.IsDispatched);
        Assert.True(harness.Scheduler.RunAll() > 0);
        Assert.Equal("paper-order-001", Assert.Single(harness.Transport.ReplacedOrderIds));

        var byClient = harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId));
        var byBroker = harness.Adapter.Query(BrokerOrderQuery.ByBrokerId(new BrokerOrderId("paper-order-002")));
        Assert.True(byClient.Found);
        Assert.True(byBroker.Found);
        Assert.Equal(replacement, byClient.Order!.CurrentTerms);

        var cancel = harness.Adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByBrokerId(new BrokerOrderId("paper-order-002")),
            OmsTestData.Causation("alpaca-cancel")));
        Assert.True(cancel.IsDispatched);
        Assert.Equal("paper-order-002", Assert.Single(harness.Transport.CancelledOrderIds));
        harness.Source.Publish(Order(instruction.Identity.ClientOrderId.Value, "canceled", orderId: "paper-order-002", quantity: 3));
        Assert.True(harness.Scheduler.RunAll() > 0);
        Assert.Equal(
            OrderLifecycleState.Cancelled,
            harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.State);
    }

    [Fact]
    public async Task CancelNotFoundAndReplaceClientError_MapToUnknownWithoutRejectingOriginalOrder()
    {
        await using (var cancelHarness = await Harness.CreateAsync())
        {
            var instruction = Instruction("alpaca-cancel-404");
            Assert.True(cancelHarness.Adapter.Submit(new BrokerSubmitCommand(
                instruction,
                OmsTestData.Causation("alpaca-cancel-404-submit"),
                cancelHarness.Adapter.Capabilities.Version)).IsDispatched);
            cancelHarness.Scheduler.RunAll();
            cancelHarness.Transport.CancelFailure = new AlpacaApiException(
                HttpStatusCode.NotFound,
                "40410000",
                "order not found");

            Assert.True(cancelHarness.Adapter.Cancel(new BrokerCancelCommand(
                BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
                OmsTestData.Causation("alpaca-cancel-404"))).IsDispatched);
            cancelHarness.Scheduler.RunAll();

            Assert.Equal(
                OrderLifecycleState.Unknown,
                cancelHarness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.State);
        }

        await using (var replaceHarness = await Harness.CreateAsync())
        {
            var instruction = Instruction(
                "alpaca-replace-422",
                CanonicalOrderType.Limit,
                new ScaledPrice(10_025, 2));
            Assert.True(replaceHarness.Adapter.Submit(new BrokerSubmitCommand(
                instruction,
                OmsTestData.Causation("alpaca-replace-422-submit"),
                replaceHarness.Adapter.Capabilities.Version)).IsDispatched);
            replaceHarness.Scheduler.RunAll();
            replaceHarness.Transport.ReplaceFailure = new AlpacaApiException(
                HttpStatusCode.UnprocessableEntity,
                "42210000",
                "replacement ambiguous");

            Assert.True(replaceHarness.Adapter.Replace(new BrokerReplaceCommand(
                BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
                instruction.Terms with { LimitPrice = new ScaledPrice(10_050, 2) },
                OmsTestData.Causation("alpaca-replace-422"),
                replaceHarness.Adapter.Capabilities.Version)).IsDispatched);
            replaceHarness.Scheduler.RunAll();

            Assert.Equal(
                OrderLifecycleState.Unknown,
                replaceHarness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.State);
        }
    }

    [Fact]
    public async Task FractionalOpeningPosition_IsPreservedExactlyWhenWholeFillArrives()
    {
        var transport = new MockTransport(Endpoint());
        transport.Positions.Add(new AlpacaPositionSnapshot(
            Symbol,
            "us_equity",
            new ScaledQuantity(5, 1),
            OmsTestData.TimestampUtc));
        var source = new ManualTradeUpdateSource();
        await using var adapter = CreateAdapter(transport, source, out var scheduler);
        await adapter.ConnectAsync("paper-key", "paper-secret");
        scheduler.RunAll();
        BrokerPositionEvent? positionEvent = null;
        adapter.EventReceived += item => positionEvent = item as BrokerPositionEvent ?? positionEvent;
        var instruction = Instruction("alpaca-fractional-opening");
        Assert.True(adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-fractional-submit"),
            adapter.Capabilities.Version)).IsDispatched);
        scheduler.RunAll();

        source.Publish(Order(instruction.Identity.ClientOrderId.Value, "filled", filled: 2));
        scheduler.RunAll();

        Assert.Equal(new ScaledQuantity(25, 1), positionEvent!.Position);
        Assert.Equal(
            new ScaledQuantity(25, 1),
            Assert.Single(adapter.CaptureReconciliationSnapshot().Positions).Quantity);
    }

    [Fact]
    public async Task Reconciliation_HydratesAuthoritativeReplacementBrokerIdForClientRecovery()
    {
        const string clientOrderId = "alpaca-restart-replacement";
        var transport = new MockTransport(Endpoint());
        transport.OpenOrders.Add(Order(
            clientOrderId,
            "new",
            orderId: "paper-order-new",
            updatedAtUtc: OmsTestData.TimestampUtc));
        transport.ClosedOrders.Add(Order(
            clientOrderId,
            "replaced",
            orderId: "paper-order-old",
            updatedAtUtc: OmsTestData.TimestampUtc.AddMinutes(1)));
        await using var adapter = CreateAdapter(transport, new ManualTradeUpdateSource(), out _);

        await adapter.ConnectAsync("paper-key", "paper-secret");

        var byClient = adapter.Query(BrokerOrderQuery.ByClientId(new ClientOrderId(clientOrderId)));
        Assert.True(byClient.Found);
        Assert.Equal(new BrokerOrderId("paper-order-new"), byClient.Order!.BrokerOrderId);
        Assert.False(adapter.Query(BrokerOrderQuery.ByBrokerId(new BrokerOrderId("paper-order-old"))).Found);
        Assert.True(adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(new ClientOrderId(clientOrderId)),
            OmsTestData.Causation("alpaca-restart-cancel"))).IsDispatched);
        Assert.Equal("paper-order-new", Assert.Single(transport.CancelledOrderIds));
    }

    [Fact]
    public async Task ExplicitAsyncCorrelationRecovery_HydratesCacheWithoutSyncQueryNetwork()
    {
        const string clientOrderId = "alpaca-explicit-recovery";
        var transport = new MockTransport(Endpoint());
        await using var adapter = CreateAdapter(transport, new ManualTradeUpdateSource(), out _);
        await adapter.ConnectAsync("paper-key", "paper-secret");
        Assert.False(adapter.Query(BrokerOrderQuery.ByClientId(new ClientOrderId(clientOrderId))).Found);
        transport.OpenOrders.Add(Order(clientOrderId, "new", orderId: "paper-order-recovered"));

        Assert.True(await adapter.RefreshOrderCorrelationAsync(
            BrokerOrderQuery.ByClientId(new ClientOrderId(clientOrderId))));

        Assert.True(adapter.Query(BrokerOrderQuery.ByBrokerId(new BrokerOrderId("paper-order-recovered"))).Found);
        Assert.True(adapter.Cancel(new BrokerCancelCommand(
            BrokerOrderQuery.ByClientId(new ClientOrderId(clientOrderId)),
            OmsTestData.Causation("alpaca-recovered-cancel"))).IsDispatched);
        Assert.Equal("paper-order-recovered", Assert.Single(transport.CancelledOrderIds));
    }

    [Fact]
    public async Task PollFirstReplacement_PublishesExactTermsAndLaterResponseOnlyAdvancesBrokerId()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction(
            "alpaca-poll-first-replace",
            CanonicalOrderType.Limit,
            new ScaledPrice(10_025, 2));
        Assert.True(harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-poll-first-submit"),
            harness.Adapter.Capabilities.Version)).IsDispatched);
        harness.Scheduler.RunAll();
        var replacement = instruction.Terms with { LimitPrice = new ScaledPrice(10_050, 2) };
        harness.Transport.ReplaceCompletion = new TaskCompletionSource<AlpacaOrderSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BrokerOrderEvent? replacedEvent = null;
        harness.Adapter.EventReceived += item =>
        {
            if (item is BrokerOrderEvent { VenueEvent.Kind: VenueEventKind.Replaced } orderEvent)
                replacedEvent = orderEvent;
        };

        Assert.True(harness.Adapter.Replace(new BrokerReplaceCommand(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId),
            replacement,
            OmsTestData.Causation("alpaca-poll-first-replace"),
            harness.Adapter.Capabilities.Version)).IsDispatched);
        harness.Source.Publish(Order(
            instruction.Identity.ClientOrderId.Value,
            "replaced",
            orderId: "paper-order-001"));
        harness.Scheduler.RunAll();

        Assert.Equal(replacement, replacedEvent!.VenueEvent.ReplacementTerms);
        harness.Transport.ReplaceCompletion.SetResult(Order(
            instruction.Identity.ClientOrderId.Value,
            "new",
            orderId: "paper-order-002"));
        await WaitUntilAsync(() => harness.Adapter.Query(
            BrokerOrderQuery.ByBrokerId(new BrokerOrderId("paper-order-002"))).Found);
        Assert.Equal(replacement, harness.Adapter.Query(
            BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.CurrentTerms);
        Assert.Equal(0, harness.Scheduler.RunAll());
    }

    [Fact]
    public async Task VenueRejection_IsMappedIntoCoordinatorLifecycle()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Transport.SubmitFailure = new AlpacaApiException(
            HttpStatusCode.UnprocessableEntity,
            "42210000",
            "insufficient buying power");
        var instruction = Instruction("alpaca-rejected");
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));
        Assert.True(released.IsSuccess);
        Assert.True(harness.Scheduler.RunAll() > 0);

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.Rejected, projection.State);
        Assert.Contains(
            harness.Store.Read(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.VenueRejected &&
                    item.Reason!.Contains("buying power", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Disconnect_CancelsPendingSubmitAndPreventsLateCallbackIntoEndedSession()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Transport.SubmitCompletion = new TaskCompletionSource<AlpacaOrderSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var instruction = Instruction("alpaca-disconnect-pending");
        var result = harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-pending-submit"),
            harness.Adapter.Capabilities.Version));
        Assert.True(result.IsDispatched);

        await harness.Adapter.DisconnectAsync();
        await WaitUntilAsync(() => Volatile.Read(ref harness.Transport.SubmitCancellationCount) == 1);
        harness.Transport.SubmitCompletion.TrySetResult(Order(instruction.Identity.ClientOrderId.Value, "new"));

        Assert.Equal(0, harness.Scheduler.RunAll());
        Assert.False(harness.Adapter.Session.CanExecute);
    }

    [Fact]
    public async Task ReconnectToDifferentPaperAccount_RejectsTrackedStateReuse()
    {
        await using var harness = await Harness.CreateAsync();
        var instruction = Instruction("alpaca-account-change");
        Assert.True(harness.Adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-account-change-submit"),
            harness.Adapter.Capabilities.Version)).IsDispatched);
        harness.Scheduler.RunAll();
        harness.Transport.Account = ActiveAccount() with { AccountId = "paper-account-002" };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Adapter.ConnectAsync("second-paper-key", "second-paper-secret"));

        Assert.Contains("tracked order state", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(harness.Adapter.Session.CanExecute);
        Assert.True(harness.Adapter.Query(BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Found);
    }

    [Fact]
    public async Task SynchronousDispose_WaitsForCanceledPendingCommandAndStopsSource()
    {
        var transport = new MockTransport(Endpoint())
        {
            SubmitCompletion = new TaskCompletionSource<AlpacaOrderSnapshot>(
                TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var source = new ManualTradeUpdateSource();
        var adapter = CreateAdapter(transport, source, out var scheduler);
        await adapter.ConnectAsync("paper-key", "paper-secret");
        scheduler.RunAll();
        var instruction = Instruction("alpaca-sync-dispose");
        Assert.True(adapter.Submit(new BrokerSubmitCommand(
            instruction,
            OmsTestData.Causation("alpaca-sync-dispose-submit"),
            adapter.Capabilities.Version)).IsDispatched);

        adapter.Dispose();

        Assert.Equal(1, Volatile.Read(ref transport.SubmitCancellationCount));
        Assert.False(source.IsRunning);
        Assert.False(transport.IsConnected);
        Assert.Equal(0, scheduler.RunAll());
    }

    [Fact]
    public async Task ReconciliationSnapshot_ContainsOpenClosedPositionAndCashEvidence()
    {
        var transport = new MockTransport(Endpoint());
        transport.OpenOrders.Add(Order("snapshot-open", "new", orderId: "snapshot-order-open"));
        transport.ClosedOrders.Add(Order("snapshot-filled", "filled", filled: 2, orderId: "snapshot-order-filled"));
        transport.Positions.Add(new AlpacaPositionSnapshot(
            Symbol,
            "us_equity",
            ScaledQuantity.FromWhole(2),
            OmsTestData.TimestampUtc));
        await using var adapter = CreateAdapter(transport, new ManualTradeUpdateSource(), out _);

        await adapter.ConnectAsync("paper-key", "paper-secret");
        var snapshot = adapter.CaptureReconciliationSnapshot();

        Assert.Single(snapshot.OpenOrders);
        Assert.Single(snapshot.CompletedOrders);
        Assert.Equal(ScaledQuantity.FromWhole(2), Assert.Single(snapshot.Positions).Quantity);
        var cash = Assert.Single(snapshot.Cash);
        Assert.Equal("USD", cash.Currency);
        Assert.Equal(new ScaledMoney(100_000, 2), cash.Total);
        Assert.Equal(new ScaledMoney(200_000, 2), cash.Available);
        Assert.Equal(1, transport.OpenSnapshotCount);
        Assert.Equal(1, transport.ClosedSnapshotCount);
    }

    [Fact]
    public async Task Reconciliation_FailsClosedWhenBoundedOrderPageMayBeTruncated()
    {
        var transport = new MockTransport(Endpoint());
        for (var index = 0; index < 500; index++)
        {
            transport.OpenOrders.Add(Order(
                $"bounded-{index}",
                "new",
                orderId: $"paper-order-{index}"));
        }
        await using var adapter = CreateAdapter(transport, new ManualTradeUpdateSource(), out _);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            adapter.ConnectAsync("paper-key", "paper-secret"));

        Assert.Contains("500-order limit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(adapter.Session.CanExecute);
    }

    [Fact]
    public async Task PollSource_IsBoundedCancelableAndStopsIssuingRequestsAfterDispose()
    {
        var transport = new MockTransport(Endpoint());
        await transport.ConnectAsync("paper-key", "paper-secret");
        var source = new AlpacaPollingTradeUpdateSource(TimeSpan.FromMilliseconds(100), 32);
        await source.StartAsync(transport);
        await WaitUntilAsync(() => Volatile.Read(ref transport.AllPollCount) >= 1);

        source.Dispose();
        var stoppedAt = Volatile.Read(ref transport.AllPollCount);
        await Task.Delay(250);

        Assert.Equal(stoppedAt, Volatile.Read(ref transport.AllPollCount));
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task IncompletePollPage_ProcessesRecentRowsThenReportsFailClosedFaultAndContinues()
    {
        var transport = new MockTransport(Endpoint());
        for (var index = 0; index < 500; index++)
        {
            transport.OpenOrders.Add(Order(
                $"poll-{index}",
                "new",
                orderId: $"poll-order-{index}"));
        }
        await transport.ConnectAsync("paper-key", "paper-secret");
        var source = new AlpacaPollingTradeUpdateSource(TimeSpan.FromMilliseconds(100), 32);
        var updates = 0;
        var incomplete = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OrderUpdated += _ => Interlocked.Increment(ref updates);
        source.Faulted += exception =>
        {
            if (exception.Message.Contains("500-order limit", StringComparison.OrdinalIgnoreCase))
                incomplete.TrySetResult(exception);
        };

        await source.StartAsync(transport);
        _ = await incomplete.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var firstPolls = Volatile.Read(ref transport.AllPollCount);
        await WaitUntilAsync(() => Volatile.Read(ref transport.AllPollCount) > firstPolls);

        Assert.Equal(32, Volatile.Read(ref updates));
        Assert.True(source.IsRunning);
        source.Dispose();
    }

    [Fact]
    public async Task IncompleteProductionPoll_RevokesAdapterCertificationAndNewOrderAdmission()
    {
        var transport = new MockTransport(Endpoint())
        {
            AllOrdersOverride = Enumerable.Range(0, 500)
                .Select(index => Order($"admission-{index}", "new", orderId: $"admission-order-{index}"))
                .ToArray(),
        };
        var source = new AlpacaPollingTradeUpdateSource(TimeSpan.FromMilliseconds(100), 32);
        await using var adapter = new AlpacaExecutionAdapter(
            Options(),
            transport,
            source,
            Clock(),
            new ControllableAdapterEventScheduler());
        await adapter.ConnectAsync("paper-key", "paper-secret");

        await WaitUntilAsync(() => !adapter.Session.IsExecutionCertified);

        Assert.Equal(ExecutionSessionHealth.Degraded, adapter.Session.Health);
        Assert.Equal(
            BrokerAdapterCommandFault.ExecutionUnavailable,
            adapter.Submit(new BrokerSubmitCommand(
                Instruction("alpaca-incomplete-admission"),
                OmsTestData.Causation("alpaca-incomplete-admission-submit"),
                adapter.Capabilities.Version)).Fault);
    }

    private static AlpacaExecutionEndpoint Endpoint() =>
        AlpacaExecutionEndpointGate.Resolve(Options());

    private static AlpacaExecutionOptions Options()
    {
        var options = new AlpacaExecutionOptions();
        Configure(options);
        return options;
    }

    private static AlpacaExecutionOptions LiveOptions()
    {
        var options = Options();
        options.Mode = ExecutionMode.Live;
        options.BaseUrl = AlpacaExecutionOptions.LiveBaseUrl;
        options.AllowLiveExecution = true;
        options.KeyId = "in-process-live-key";
        options.SecretKey = "in-process-live-secret";
        options.ExpectedAccountId = AccountId;
        return options;
    }

    private static InMemoryLiveExecutionConfirmationStore LiveConfirmations(AlpacaExecutionOptions options)
    {
        var store = new InMemoryLiveExecutionConfirmationStore();
        store.Save(new LiveExecutionConfirmation(
            AlpacaExecutionOptions.BrokerId,
            options.ExpectedAccountId,
            LiveExecutionConfirmation.RequiredAcknowledgement,
            OmsTestData.TimestampUtc,
            "test-owner"));
        return store;
    }

    private static void Copy(AlpacaExecutionOptions source, AlpacaExecutionOptions target)
    {
        target.Enabled = source.Enabled;
        target.Mode = source.Mode;
        target.AllowLiveExecution = source.AllowLiveExecution;
        target.BaseUrl = source.BaseUrl;
        target.MarketDataBaseUrl = source.MarketDataBaseUrl;
        target.KeyId = source.KeyId;
        target.SecretKey = source.SecretKey;
        target.ExpectedAccountId = source.ExpectedAccountId;
        target.Symbol = source.Symbol;
        target.CanonicalInstrumentId = source.CanonicalInstrumentId;
        target.PollIntervalMilliseconds = source.PollIntervalMilliseconds;
        target.MaximumTrackedOrders = source.MaximumTrackedOrders;
        target.MaximumCommandsPerMinute = source.MaximumCommandsPerMinute;
        target.RequestTimeoutMilliseconds = source.RequestTimeoutMilliseconds;
    }

    private static void Configure(AlpacaExecutionOptions options)
    {
        options.Enabled = true;
        options.BaseUrl = AlpacaExecutionOptions.PaperBaseUrl;
        options.MarketDataBaseUrl = AlpacaExecutionOptions.DataBaseUrl;
        options.Symbol = Symbol;
        options.CanonicalInstrumentId = OmsTestData.Instruction().TradeIntent.Instrument.Value;
        options.MaximumTrackedOrders = 32;
        options.PollIntervalMilliseconds = 100;
        options.KeyId = string.Empty;
        options.SecretKey = string.Empty;
    }

    private static AlpacaAccountSnapshot ActiveAccount() => new(
        AccountId,
        "ACTIVE",
        "USD",
        new ScaledMoney(100_000, 2),
        new ScaledMoney(200_000, 2),
        false,
        false,
        false);

    private static AlpacaExecutionAdapter CreateAdapter(
        MockTransport transport,
        IAlpacaTradeUpdateSource source,
        out ControllableAdapterEventScheduler scheduler)
    {
        scheduler = new ControllableAdapterEventScheduler();
        return new AlpacaExecutionAdapter(Options(), transport, source, Clock(), scheduler);
    }

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private static CanonicalOrderInstruction Instruction(
        string clientOrderId,
        CanonicalOrderType orderType = CanonicalOrderType.Market,
        ScaledPrice? limitPrice = null) =>
        OmsTestData.Instruction(
            clientOrderId,
            target: 2,
            orderType,
            CanonicalTimeInForce.GoodTillCancelled,
            limitPrice);

    private static AlpacaOrderSnapshot Order(
        string clientOrderId,
        string status,
        int filled = 0,
        string orderId = "paper-order-001",
        int quantity = 2,
        DateTime? updatedAtUtc = null) => new(
            orderId,
            clientOrderId,
            Symbol,
            "us_equity",
            "buy",
            "market",
            "gtc",
            status,
            ScaledQuantity.FromWhole(quantity),
            ScaledQuantity.FromWhole(filled),
            filled > 0 ? new ScaledPrice(10_025, 2) : null,
            null,
            null,
            updatedAtUtc ?? OmsTestData.TimestampUtc);

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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition())
            await Task.Delay(10, timeout.Token);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            MockTransport transport,
            ManualTradeUpdateSource source,
            ControllableAdapterEventScheduler scheduler,
            AlpacaExecutionAdapter adapter,
            InMemoryOrderEventStore store,
            OrderManagementService service,
            ExecutionCoordinator coordinator)
        {
            Transport = transport;
            Source = source;
            Scheduler = scheduler;
            Adapter = adapter;
            Store = store;
            Service = service;
            Coordinator = coordinator;
        }

        internal MockTransport Transport { get; }
        internal ManualTradeUpdateSource Source { get; }
        internal ControllableAdapterEventScheduler Scheduler { get; }
        internal AlpacaExecutionAdapter Adapter { get; }
        internal InMemoryOrderEventStore Store { get; }
        internal OrderManagementService Service { get; }
        internal ExecutionCoordinator Coordinator { get; }

        internal static async Task<Harness> CreateAsync()
        {
            var transport = new MockTransport(Endpoint());
            var source = new ManualTradeUpdateSource();
            var adapter = CreateAdapter(transport, source, out var scheduler);
            await adapter.ConnectAsync("paper-key", "paper-secret");
            scheduler.RunAll();
            var store = new InMemoryOrderEventStore();
            var clock = Clock();
            var service = new OrderManagementService(
                store,
                OmsTestData.RiskEngine(),
                new DeterministicSimulatedVenue(clock),
                clock);
            var coordinator = new ExecutionCoordinator(service, adapter);
            return new Harness(transport, source, scheduler, adapter, store, service, coordinator);
        }

        public async ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            await Adapter.DisposeAsync();
        }
    }

    private sealed class ManualTradeUpdateSource : IAlpacaTradeUpdateSource
    {
        public bool IsRunning { get; private set; }
        public event Action<AlpacaOrderSnapshot>? OrderUpdated;
        public event Action<Exception>? Faulted
        {
            add { }
            remove { }
        }

        public Task StartAsync(IAlpacaExecutionTransport transport, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsRunning = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsRunning = false;
            return ValueTask.CompletedTask;
        }

        internal void Publish(AlpacaOrderSnapshot order) => OrderUpdated?.Invoke(order);
    }

    private sealed class MockTransport(AlpacaExecutionEndpoint endpoint) : IAlpacaExecutionTransport
    {
        private bool _connected;
        internal int AllPollCount;
        internal int SubmitCancellationCount;

        public AlpacaExecutionEndpoint Endpoint { get; } = endpoint;
        public bool IsConnected => _connected;
        internal int ConnectCount { get; private set; }
        internal int OpenSnapshotCount { get; private set; }
        internal int ClosedSnapshotCount { get; private set; }
        internal AlpacaAccountSnapshot Account { get; set; } = ActiveAccount();
        internal AlpacaAssetSnapshot Asset { get; set; } = new(
            Symbol,
            "us_equity",
            true,
            true,
            new ScaledQuantity(1, 9),
            new ScaledQuantity(1, 9),
            new ScaledPrice(1, 2));
        internal Exception? LatestTradeFailure { get; set; }
        internal Exception? SubmitFailure { get; set; }
        internal Exception? CancelFailure { get; set; }
        internal Exception? ReplaceFailure { get; set; }
        internal TaskCompletionSource<AlpacaOrderSnapshot>? SubmitCompletion { get; set; }
        internal TaskCompletionSource<AlpacaOrderSnapshot>? ReplaceCompletion { get; set; }
        internal string SubmitOrderId { get; set; } = "paper-order-001";
        internal List<AlpacaSubmitRequest> Submits { get; } = [];
        internal List<string> CancelledOrderIds { get; } = [];
        internal List<string> ReplacedOrderIds { get; } = [];
        internal List<AlpacaOrderSnapshot> OpenOrders { get; } = [];
        internal List<AlpacaOrderSnapshot> ClosedOrders { get; } = [];
        internal List<AlpacaPositionSnapshot> Positions { get; } = [];
        internal IReadOnlyList<AlpacaOrderSnapshot>? AllOrdersOverride { get; set; }

        public Task ConnectAsync(string keyId, string secretKey, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(secretKey))
                throw new ArgumentException("credentials required");
            ConnectCount++;
            _connected = true;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _connected = false;
            return Task.CompletedTask;
        }

        public Task<AlpacaAccountSnapshot> GetAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task<AlpacaAssetSnapshot> GetAssetAsync(string symbol, CancellationToken cancellationToken = default) =>
            Task.FromResult(Asset);

        public Task<AlpacaLatestTrade?> GetLatestTradeAsync(string symbol, CancellationToken cancellationToken = default) =>
            LatestTradeFailure is null
                ? Task.FromResult<AlpacaLatestTrade?>(new AlpacaLatestTrade(new ScaledPrice(10_025, 2), OmsTestData.TimestampUtc))
                : Task.FromException<AlpacaLatestTrade?>(LatestTradeFailure);

        public async Task<AlpacaOrderSnapshot> SubmitOrderAsync(
            AlpacaSubmitRequest request,
            CancellationToken cancellationToken = default)
        {
            if (SubmitFailure is not null)
                throw SubmitFailure;
            Submits.Add(request);
            if (SubmitCompletion is not null)
            {
                try
                {
                    return await SubmitCompletion.Task.WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref SubmitCancellationCount);
                    throw;
                }
            }
            return Order(request.ClientOrderId, "new", orderId: SubmitOrderId);
        }

        public Task CancelOrderAsync(string orderId, CancellationToken cancellationToken = default)
        {
            if (CancelFailure is not null)
                return Task.FromException(CancelFailure);
            CancelledOrderIds.Add(orderId);
            return Task.CompletedTask;
        }

        public async Task<AlpacaOrderSnapshot> ReplaceOrderAsync(
            string orderId,
            AlpacaReplaceRequest request,
            CancellationToken cancellationToken = default)
        {
            if (ReplaceFailure is not null)
                throw ReplaceFailure;
            ReplacedOrderIds.Add(orderId);
            if (ReplaceCompletion is not null)
                return await ReplaceCompletion.Task.WaitAsync(cancellationToken);
            return Order(
                Submits.Last().ClientOrderId,
                "new",
                orderId: "paper-order-002",
                quantity: checked((int)request.Quantity.Coefficient));
        }

        public Task<AlpacaOrderSnapshot?> GetOrderByIdAsync(string orderId, CancellationToken cancellationToken = default) =>
            Task.FromResult<AlpacaOrderSnapshot?>(
                OpenOrders.Concat(ClosedOrders).FirstOrDefault(item => item.OrderId == orderId));

        public Task<AlpacaOrderSnapshot?> GetOrderByClientIdAsync(
            string clientOrderId,
            CancellationToken cancellationToken = default) => Task.FromResult<AlpacaOrderSnapshot?>(
                OpenOrders.Concat(ClosedOrders).FirstOrDefault(item => item.ClientOrderId == clientOrderId));

        public Task<IReadOnlyList<AlpacaOrderSnapshot>> GetOrdersAsync(
            AlpacaOrderStatusFilter status,
            CancellationToken cancellationToken = default)
        {
            if (status == AlpacaOrderStatusFilter.All)
            {
                Interlocked.Increment(ref AllPollCount);
                return Task.FromResult(
                    AllOrdersOverride ?? (IReadOnlyList<AlpacaOrderSnapshot>)OpenOrders.Concat(ClosedOrders).ToArray());
            }
            if (status == AlpacaOrderStatusFilter.Open)
            {
                OpenSnapshotCount++;
                return Task.FromResult<IReadOnlyList<AlpacaOrderSnapshot>>(OpenOrders.ToArray());
            }
            ClosedSnapshotCount++;
            return Task.FromResult<IReadOnlyList<AlpacaOrderSnapshot>>(ClosedOrders.ToArray());
        }

        public Task<IReadOnlyList<AlpacaPositionSnapshot>> GetPositionsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AlpacaPositionSnapshot>>(Positions.ToArray());

        public ValueTask DisposeAsync()
        {
            _connected = false;
            return ValueTask.CompletedTask;
        }
    }
}
