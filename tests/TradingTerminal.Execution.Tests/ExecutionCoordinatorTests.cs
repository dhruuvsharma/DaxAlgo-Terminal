using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class ExecutionCoordinatorTests
{
    [Fact]
    public async Task ArmedOrder_RecordsDispatchReceiptBeforeAcknowledgement_ThenFillsFromScheduledCallbacks()
    {
        var instruction = OmsTestData.Instruction("coordinator-fill");
        var fill = new FillExecution(
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(100, 0),
            new ScaledMoney(3, 0),
            LiquidityFlag.Taker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [fill]));
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));

        Assert.True(released.IsSuccess);
        Assert.NotNull(released.DispatchReceipt);
        Assert.Equal(OrderLifecycleState.Acknowledging, released.OmsResult.Projection!.State);
        var beforeCallbacks = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Contains(beforeCallbacks, item => item.Kind == OrderEventKind.SubmissionRecorded);
        Assert.DoesNotContain(beforeCallbacks, item => item.Kind == OrderEventKind.VenueAcknowledged);

        Assert.Equal(4, harness.Scheduler.RunAll());

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        var receiptIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.SubmissionRecorded);
        var acknowledgementIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.VenueAcknowledged);
        Assert.Equal(OrderLifecycleState.Filled, projection.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), projection.FilledQuantity);
        Assert.True(receiptIndex >= 0 && acknowledgementIndex > receiptIndex);
        Assert.Single(events, item => item.Kind == OrderEventKind.CommissionObserved);
        Assert.Single(events, item => item.Kind == OrderEventKind.PositionObserved);
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
    }

    [Fact]
    public async Task FillBeforeAcknowledgement_PreservesReceiptOrderEconomicsAndExplicitVenueIds()
    {
        var instruction = OmsTestData.Instruction("fill-before-ack");
        var brokerOrderId = new BrokerOrderId("broker-fill-before-ack");
        var exchangeOrderId = new ExchangeOrderId("exchange-fill-before-ack");
        var fill = new FillExecution(
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(102, 0),
            new ScaledMoney(5, 0),
            LiquidityFlag.Taker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [fill],
                fillBeforeAcknowledgement: true,
                brokerOrderId: brokerOrderId,
                exchangeOrderId: exchangeOrderId));
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));
        Assert.True(released.IsSuccess);
        Assert.Equal(4, harness.Scheduler.RunAll());

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        var receiptIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.SubmissionRecorded);
        var fillIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.FillReceived);
        var acknowledgementIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.VenueAcknowledged);
        Assert.True(receiptIndex >= 0 && fillIndex > receiptIndex && acknowledgementIndex > fillIndex);
        Assert.Equal(OrderLifecycleState.Filled, projection.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), projection.FilledQuantity);
        Assert.Equal(new ScaledMoney(5, 0), projection.TotalFees);
        Assert.Equal(brokerOrderId, projection.BrokerOrderId);
        Assert.Equal(exchangeOrderId, projection.ExchangeOrderId);
        Assert.Single(events, item => item.Kind == OrderEventKind.FillReceived);
        var lateAcknowledgement = events[acknowledgementIndex];
        Assert.Equal(OrderLifecycleState.Filled, lateAcknowledgement.StateBefore);
        Assert.Equal(OrderLifecycleState.Filled, lateAcknowledgement.StateAfter);
        Assert.Equal(brokerOrderId, lateAcknowledgement.BrokerOrderId);
        Assert.Equal(exchangeOrderId, lateAcknowledgement.ExchangeOrderId);
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
    }

    [Fact]
    public async Task CoordinatorEvidence_RoundTripsThroughDurableSqliteLedger()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("coordinator.db");
        var instruction = OmsTestData.Instruction("coordinator-sqlite");
        var fill = new FillExecution(
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(100, 0),
            new ScaledMoney(3, 0),
            LiquidityFlag.Taker);
        var clock = Clock();

        {
            using var store = new SqliteOrderEventStore(databasePath, clock);
            var venue = new DeterministicSimulatedVenue(
                clock,
                [new VenueSubmitPlan(
                    instruction.Identity.ClientOrderId,
                    VenueSubmitOutcome.Accepted,
                    [fill])]);
            var scheduler = new ControllableAdapterEventScheduler();
            var adapter = new SimulatedExecutionAdapter(venue, clock, scheduler);
            var service = new OrderManagementService(
                store,
                OmsTestData.RiskEngine(),
                venue,
                clock);
            using var coordinator = new ExecutionCoordinator(service, adapter);

            Assert.True(service.CreateDraft(
                instruction,
                Context(instruction, "draft")).IsSuccess);
            Assert.True(coordinator.Validate(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                OmsTestData.RiskSnapshot(),
                Context(instruction, "validate")).IsSuccess);
            Assert.True(service.Prepare(
                instruction.Identity.ClientOrderId,
                Context(instruction, "prepare")).IsSuccess);
            Assert.True(coordinator.Arm(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                Context(instruction, "arm")).IsSuccess);
            Assert.True((await coordinator.ReleaseAsync(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                Context(instruction, "release"))).IsSuccess);
            Assert.Equal(4, scheduler.RunAll());

            Assert.Equal(
                OrderLifecycleState.Filled,
                store.ReadProjection(instruction.Identity.ClientOrderId)!.State);
        }

        using var reopened = new SqliteOrderEventStore(databasePath, clock);
        var persisted = reopened.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(
            OrderLifecycleState.Filled,
            reopened.ReadProjection(instruction.Identity.ClientOrderId)!.State);
        Assert.Contains(persisted, item => item.Kind == OrderEventKind.SubmissionRecorded);
        Assert.Contains(persisted, item => item.Kind == OrderEventKind.VenueAcknowledged);
        Assert.Contains(persisted, item => item.Kind == OrderEventKind.FillReceived);
        Assert.Single(persisted, item => item.Kind == OrderEventKind.CommissionObserved);
        Assert.Single(persisted, item => item.Kind == OrderEventKind.PositionObserved);
        Assert.True(OrderEventChainVerifier.Verify(persisted).IsValid);
    }

    [Fact]
    public void ExactCapabilityMismatch_IsRejectedBeforeRiskAndCannotBeArmed()
    {
        var instruction = OmsTestData.Instruction("unsupported-lot");
        var clock = Clock();
        var venue = new DeterministicSimulatedVenue(clock);
        var capabilities = SimulatedExecutionAdapter.CreateDefaultCapabilities(venue.Capabilities) with
        {
            LotSize = ScaledQuantity.FromWhole(3),
        };
        using var harness = Harness.Create(
            instruction,
            clock: clock,
            venue: venue,
            capabilities: capabilities,
            riskEngine: OmsTestData.RiskEngine(maximumOrderQuantity: 1));
        Assert.True(harness.Service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);

        var rejected = harness.Coordinator.Validate(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate"));

        Assert.Equal(OmsCommandFault.UnsupportedCapability, rejected.Fault);
        Assert.Equal(OrderLifecycleState.Rejected, rejected.Projection!.State);
        Assert.Null(rejected.Projection.RiskDecision);
        Assert.Contains(nameof(ExecutionAdmissionFault.QuantityNotRepresentable), rejected.Reason, StringComparison.Ordinal);
        Assert.Equal(ScaledQuantity.FromWhole(2), rejected.Projection.Terms.Quantity);
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Single(events, item => item.Kind == OrderEventKind.ValidationRejected);
        Assert.DoesNotContain(events, item => item.Kind is OrderEventKind.RiskAccepted or OrderEventKind.RiskRejected);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.Armed);
    }

    [Fact]
    public async Task DuplicateAdapterCallbacks_DoNotDoubleCountFillOrFee()
    {
        var instruction = OmsTestData.Instruction("duplicate-callback");
        var fill = new FillExecution(
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(101, 0),
            new ScaledMoney(7, 0),
            LiquidityFlag.Maker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [fill]),
            duplicateCallbacks: true);
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);

        Assert.Equal(6, harness.Scheduler.RunAll());

        var projection = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(OrderLifecycleState.Filled, projection.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), projection.FilledQuantity);
        Assert.Equal(new ScaledMoney(7, 0), projection.TotalFees);
        Assert.Single(events, item => item.Kind == OrderEventKind.VenueAcknowledged);
        Assert.Single(events, item => item.Kind == OrderEventKind.FillReceived);
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
    }

    [Fact]
    public async Task PartialFillDeliveredWhileCancelIsPending_IsCountedBeforeCancelConfirmation()
    {
        var instruction = OmsTestData.Instruction("pending-cancel-fill");
        var partialFill = new FillExecution(
            ScaledQuantity.FromWhole(1),
            new ScaledPrice(99, 0),
            new ScaledMoney(2, 0),
            LiquidityFlag.Taker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [partialFill]));
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        Assert.True(harness.Scheduler.RunNext());
        Assert.Equal(
            OrderLifecycleState.Working,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);

        var cancel = await harness.Coordinator.CancelAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "cancel"));
        Assert.True(cancel.IsSuccess);
        Assert.Equal(OrderLifecycleState.PendingCancel, cancel.OmsResult.Projection!.State);

        Assert.True(harness.Scheduler.RunNext());

        var duringCancel = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.PendingCancel, duringCancel.State);
        Assert.Equal(ScaledQuantity.FromWhole(1), duringCancel.FilledQuantity);
        var fillEvent = Assert.Single(
            harness.Store.Read(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.FillReceived);
        Assert.Equal(OrderLifecycleState.PendingCancel, fillEvent.StateBefore);
        Assert.Equal(OrderLifecycleState.PendingCancel, fillEvent.StateAfter);

        Assert.Equal(3, harness.Scheduler.RunAll());
        var cancelled = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.Cancelled, cancelled.State);
        Assert.Equal(ScaledQuantity.FromWhole(1), cancelled.FilledQuantity);
    }

    [Fact]
    public async Task PartialFillDeliveredWhileReplaceIsPending_PreservesAuthorizationUntilConfirmation()
    {
        var instruction = OmsTestData.Instruction("pending-replace-fill");
        var partialFill = new FillExecution(
            ScaledQuantity.FromWhole(1),
            new ScaledPrice(100, 0),
            ScaledMoney.Zero,
            LiquidityFlag.Taker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [partialFill]));
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        Assert.True(harness.Scheduler.RunNext());

        var replacement = instruction.Terms with
        {
            OrderType = CanonicalOrderType.Limit,
            LimitPrice = new ScaledPrice(99, 0),
        };
        var replaced = await harness.Coordinator.ReplaceAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            replacement,
            OmsTestData.RiskSnapshot(referencePrice: 99),
            Context(instruction, "replace"));
        Assert.True(replaced.IsSuccess);
        Assert.Equal(OrderLifecycleState.PendingReplace, replaced.OmsResult.Projection!.State);

        Assert.True(harness.Scheduler.RunNext());
        var duringReplace = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.PendingReplace, duringReplace.State);
        Assert.Equal(ScaledQuantity.FromWhole(1), duringReplace.FilledQuantity);
        var fillEvent = Assert.Single(
            harness.Store.Read(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.FillReceived);
        Assert.Equal(OrderLifecycleState.PendingReplace, fillEvent.StateBefore);
        Assert.Equal(OrderLifecycleState.PendingReplace, fillEvent.StateAfter);

        Assert.Equal(3, harness.Scheduler.RunAll());
        var confirmed = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.PartiallyFilled, confirmed.State);
        Assert.Equal(replacement, confirmed.Terms);
        Assert.Equal(ScaledQuantity.FromWhole(1), confirmed.FilledQuantity);
    }

    [Fact]
    public async Task ReplaceConfirmation_CannotSubstituteDifferentRiskApprovedTerms()
    {
        var instruction = OmsTestData.Instruction("replace-term-substitution");
        using var harness = Harness.Create(instruction);
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        Assert.Equal(1, harness.Scheduler.RunAll());

        var replacement = instruction.Terms with
        {
            OrderType = CanonicalOrderType.Limit,
            LimitPrice = new ScaledPrice(99, 0),
        };
        var replaceContext = Context(instruction, "replace");
        var pending = await harness.Coordinator.ReplaceAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            replacement,
            OmsTestData.RiskSnapshot(referencePrice: 99),
            replaceContext);
        Assert.True(pending.IsSuccess);

        var substituted = replacement with { LimitPrice = new ScaledPrice(98, 0) };
        var callback = new VenueEvent(
            VenueEventKind.Replaced,
            instruction.Identity.ClientOrderId,
            pending.OmsResult.Projection!.BrokerOrderId,
            pending.OmsResult.Projection.ExchangeOrderId,
            null,
            substituted,
            OmsTestData.TimestampUtc,
            replaceContext.CausationId,
            OmsTestData.Dedup("replace-term-substitution-callback"));
        var rejected = harness.Service.ApplyVenueEvent(callback);

        Assert.Equal(OmsCommandFault.PersistenceRejected, rejected.Fault);
        var stillPending = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.PendingReplace, stillPending.State);
        Assert.Equal(instruction.Terms, stillPending.Terms);
        Assert.DoesNotContain(
            harness.Store.Read(instruction.Identity.ClientOrderId),
            item => item.DeduplicationKey == callback.DeduplicationKey);

        Assert.Equal(1, harness.Scheduler.RunAll());
        var confirmed = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;
        Assert.Equal(OrderLifecycleState.Working, confirmed.State);
        Assert.Equal(replacement, confirmed.Terms);
    }

    [Fact]
    public void DataOnlySession_RejectsBeforeRiskAndArming()
    {
        var instruction = OmsTestData.Instruction("data-only");
        var account = new BrokerExecutionAccount(
            new ExecutionAdapterId("simulated-data-only"),
            new BrokerAccountId("market-data-account"));
        var session = new BrokerExecutionSession(
            account,
            ExecutionSessionHealth.Healthy,
            IsDataConnected: true,
            IsExecutionAuthenticated: false,
            IsExecutionCertified: false,
            OmsTestData.TimestampUtc);
        using var harness = Harness.Create(instruction, session: session);
        Assert.True(harness.Service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);

        var rejected = harness.Coordinator.Validate(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate"));

        Assert.Equal(OmsCommandFault.ExecutionUnavailable, rejected.Fault);
        Assert.Equal(OrderLifecycleState.Rejected, rejected.Projection!.State);
        Assert.Null(rejected.Projection.RiskDecision);
        Assert.Contains(nameof(ExecutionAdmissionFault.ExecutionNotAuthenticated), rejected.Reason, StringComparison.Ordinal);
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Single(events, item => item.Kind == OrderEventKind.ValidationRejected);
        Assert.DoesNotContain(events, item => item.Kind is OrderEventKind.RiskAccepted or OrderEventKind.Armed);
        Assert.Equal(0, harness.Scheduler.PendingCount);
    }

    [Fact]
    public async Task SubmitRateLimitRejection_IsAValueAndRestoresArmedWithLedgerReason()
    {
        var instruction = OmsTestData.Instruction("rate-limited-order");
        var clock = Clock();
        var venue = new DeterministicSimulatedVenue(clock);
        var capabilities = SimulatedExecutionAdapter.CreateDefaultCapabilities(venue.Capabilities) with
        {
            RateLimit = new BrokerRateLimit(1, TimeSpan.FromMinutes(1)),
        };
        var scheduler = new ControllableAdapterEventScheduler();
        var adapter = new SimulatedExecutionAdapter(
            venue,
            clock,
            scheduler,
            capabilities: capabilities);
        var primingInstruction = OmsTestData.Instruction("rate-limit-primer");
        var primed = adapter.Submit(new BrokerSubmitCommand(
            primingInstruction,
            OmsTestData.Causation("rate-limit-primer"),
            capabilities.Version));
        Assert.True(primed.IsDispatched);
        using var harness = Harness.Create(
            instruction,
            clock: clock,
            venue: venue,
            scheduler: scheduler,
            adapter: adapter);
        DraftValidatePrepareAndArm(harness, instruction);

        var rejected = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));

        Assert.Equal(ExecutionCoordinatorFault.AdapterRejected, rejected.Fault);
        Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, rejected.AdapterResult!.Status);
        Assert.Equal(BrokerAdapterCommandFault.RateLimited, rejected.AdapterResult.Fault);
        Assert.Equal(0, rejected.AdapterResult.ScheduledEventCount);
        Assert.Contains("rate limit", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderLifecycleState.Armed, rejected.OmsResult.Projection!.State);
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(OrderEventKind.SendStarted, events[^2].Kind);
        Assert.Equal(OrderEventKind.SendFailedBeforeAcceptance, events[^1].Kind);
        Assert.Contains("rate limit", events[^1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.SubmissionRecorded);
    }

    [Fact]
    public void SqliteStartupRecoverySet_BlocksCoordinatorValidationBeforeRiskOrArming()
    {
        using var directory = new TestDirectory();
        var databasePath = directory.File("startup-recovery-gate.db");
        var clock = Clock();
        var recoveryInstruction = OmsTestData.Instruction("startup-recovery-existing");

        using (var initialStore = new SqliteOrderEventStore(databasePath, clock))
        {
            var initialVenue = new DeterministicSimulatedVenue(clock);
            var initialService = new OrderManagementService(
                initialStore,
                OmsTestData.RiskEngine(),
                initialVenue,
                clock);
            Assert.True(initialService.CreateDraft(
                recoveryInstruction,
                Context(recoveryInstruction, "draft")).IsSuccess);
            Assert.True(initialService.Validate(
                recoveryInstruction.Identity.ClientOrderId,
                OmsTestData.RiskSnapshot(),
                Context(recoveryInstruction, "validate")).IsSuccess);
            Assert.True(initialService.Prepare(
                recoveryInstruction.Identity.ClientOrderId,
                Context(recoveryInstruction, "prepare")).IsSuccess);
        }

        using var reopened = new SqliteOrderEventStore(databasePath, clock);
        Assert.False(reopened.CanAdmitNewOrders);
        Assert.Contains(
            reopened.RecoverySet,
            item => item.ClientOrderId == recoveryInstruction.Identity.ClientOrderId &&
                    item.Requirement == OrderRecoveryRequirement.FreshAuthorizationRequired);

        var candidate = OmsTestData.Instruction("startup-recovery-candidate");
        var venue = new DeterministicSimulatedVenue(clock);
        var scheduler = new ControllableAdapterEventScheduler();
        var adapter = new SimulatedExecutionAdapter(venue, clock, scheduler);
        var service = new OrderManagementService(
            reopened,
            OmsTestData.RiskEngine(maximumOrderQuantity: 1),
            venue,
            clock);
        using var coordinator = new ExecutionCoordinator(service, adapter);
        Assert.True(service.CreateDraft(candidate, Context(candidate, "draft")).IsSuccess);

        var validation = coordinator.Validate(
            adapter.Account,
            candidate.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(candidate, "validate"));
        var arming = coordinator.Arm(
            adapter.Account,
            candidate.Identity.ClientOrderId,
            Context(candidate, "arm"));

        Assert.False(validation.IsSuccess);
        Assert.Equal(OmsCommandFault.RecoveryRequired, validation.Fault);
        Assert.Equal(OrderLifecycleState.Draft, validation.Projection!.State);
        Assert.Contains("recovery", validation.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.False(arming.IsSuccess);
        Assert.Equal(OmsCommandFault.RecoveryRequired, arming.Fault);
        Assert.Equal(OrderLifecycleState.Draft, arming.Projection!.State);
        Assert.Contains("recovery", arming.Reason, StringComparison.OrdinalIgnoreCase);
        var candidateEvents = reopened.Read(candidate.Identity.ClientOrderId);
        Assert.Single(candidateEvents, item => item.Kind == OrderEventKind.DraftCreated);
        Assert.DoesNotContain(
            candidateEvents,
            item => item.Kind is OrderEventKind.RiskAccepted or
                OrderEventKind.RiskRejected or
                OrderEventKind.Prepared or
                OrderEventKind.Armed);
        Assert.Equal(0, scheduler.PendingCount);
    }

    [Fact]
    public async Task ReplacementQuantityEqualToFilledQuantity_IsRejectedBeforeAdapterDispatch()
    {
        var instruction = OmsTestData.Instruction("replace-equals-filled");
        var partialFill = new FillExecution(
            ScaledQuantity.FromWhole(1),
            new ScaledPrice(100, 0),
            ScaledMoney.Zero,
            LiquidityFlag.Taker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [partialFill]));
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True((await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        harness.Scheduler.RunAll();
        Assert.Equal(
            OrderLifecycleState.PartiallyFilled,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);
        Assert.Equal(0, harness.Scheduler.PendingCount);
        var eventsBeforeReplace = harness.Store.Read(instruction.Identity.ClientOrderId).Count;
        var replacement = instruction.Terms with
        {
            Quantity = ScaledQuantity.FromWhole(1),
        };

        var rejected = await harness.Coordinator.ReplaceAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            replacement,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "replace"));

        Assert.Equal(ExecutionCoordinatorFault.OmsRejected, rejected.Fault);
        Assert.Equal(OmsCommandFault.InvalidInstruction, rejected.OmsResult.Fault);
        Assert.Null(rejected.AdapterResult);
        Assert.Contains("greater", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OrderLifecycleState.PartiallyFilled, rejected.OmsResult.Projection!.State);
        Assert.Equal(ScaledQuantity.FromWhole(1), rejected.OmsResult.Projection.FilledQuantity);
        Assert.Equal(eventsBeforeReplace, harness.Store.Read(instruction.Identity.ClientOrderId).Count);
        Assert.Equal(0, harness.Scheduler.PendingCount);
        Assert.Equal(
            instruction.Terms,
            harness.Coordinator.Query(
                harness.Adapter.Account,
                BrokerOrderQuery.ByClientId(instruction.Identity.ClientOrderId)).Order!.CurrentTerms);
    }

    [Fact]
    public async Task FailedBeforeAcceptance_RestoresArmedWithoutSchedulingCallbacks()
    {
        var instruction = OmsTestData.Instruction("failed-before-acceptance");
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.FailedBeforeAcceptance,
                reason: "deterministic pre-acceptance failure"));
        DraftValidatePrepareAndArm(harness, instruction);

        var failed = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));

        Assert.Equal(ExecutionCoordinatorFault.AdapterRejected, failed.Fault);
        Assert.Equal(BrokerAdapterCommandStatus.RejectedBeforeDispatch, failed.AdapterResult!.Status);
        Assert.Equal(BrokerAdapterCommandFault.VenueRejected, failed.AdapterResult.Fault);
        Assert.Equal(0, failed.AdapterResult.ScheduledEventCount);
        Assert.Equal(OrderLifecycleState.Armed, failed.OmsResult.Projection!.State);
        Assert.Equal(0, harness.Scheduler.PendingCount);
        var events = harness.Store.Read(instruction.Identity.ClientOrderId);
        Assert.Equal(OrderEventKind.SendStarted, events[^2].Kind);
        Assert.Equal(OrderEventKind.SendFailedBeforeAcceptance, events[^1].Kind);
        Assert.Contains("failure before acceptance", events[^1].Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.SubmissionRecorded);
    }

    [Fact]
    public async Task CancelExceptionAfterInnerDispatch_RecordsUnknownWithoutBlindRestore()
    {
        var instruction = OmsTestData.Instruction("cancel-throws-after-dispatch");
        var clock = Clock();
        var venue = new DeterministicSimulatedVenue(clock);
        var scheduler = new ControllableAdapterEventScheduler();
        var adapter = new ThrowAfterDispatchAdapter(
            new SimulatedExecutionAdapter(venue, clock, scheduler),
            throwAfterCancel: true);
        var store = new InMemoryOrderEventStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            venue,
            clock);
        using var coordinator = new ExecutionCoordinator(service, adapter);
        Assert.True(service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);
        Assert.True(coordinator.Validate(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate")).IsSuccess);
        Assert.True(service.Prepare(
            instruction.Identity.ClientOrderId,
            Context(instruction, "prepare")).IsSuccess);
        Assert.True(coordinator.Arm(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "arm")).IsSuccess);
        Assert.True((await coordinator.ReleaseAsync(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        Assert.Equal(1, scheduler.RunAll());

        var result = await coordinator.CancelAsync(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "cancel"));

        Assert.Equal(ExecutionCoordinatorFault.AdapterRejected, result.Fault);
        Assert.Equal(OmsCommandFault.VenueOutcomeUnknown, result.OmsResult.Fault);
        Assert.Null(result.AdapterResult);
        Assert.Equal(OrderLifecycleState.Unknown, result.OmsResult.Projection!.State);
        Assert.Contains("cancel outcome is unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, scheduler.PendingCount);
        var events = store.Read(instruction.Identity.ClientOrderId);
        var unknown = Assert.Single(events, item => item.Kind == OrderEventKind.OutcomeUnknown);
        Assert.Equal(OrderLifecycleState.PendingCancel, unknown.StateBefore);
        Assert.Equal(OrderLifecycleState.Unknown, unknown.StateAfter);
        Assert.Contains("cancel threw after simulated dispatch", unknown.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.RecoveryObserved);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.CancelConfirmed);
    }

    [Fact]
    public async Task ReplaceExceptionAfterInnerDispatch_RecordsUnknownWithoutBlindRestore()
    {
        var instruction = OmsTestData.Instruction("replace-throws-after-dispatch");
        var clock = Clock();
        var venue = new DeterministicSimulatedVenue(clock);
        var scheduler = new ControllableAdapterEventScheduler();
        var adapter = new ThrowAfterDispatchAdapter(
            new SimulatedExecutionAdapter(venue, clock, scheduler),
            throwAfterReplace: true);
        var store = new InMemoryOrderEventStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            venue,
            clock);
        using var coordinator = new ExecutionCoordinator(service, adapter);
        Assert.True(service.CreateDraft(instruction, Context(instruction, "draft")).IsSuccess);
        Assert.True(coordinator.Validate(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate")).IsSuccess);
        Assert.True(service.Prepare(
            instruction.Identity.ClientOrderId,
            Context(instruction, "prepare")).IsSuccess);
        Assert.True(coordinator.Arm(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "arm")).IsSuccess);
        Assert.True((await coordinator.ReleaseAsync(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"))).IsSuccess);
        Assert.Equal(1, scheduler.RunAll());
        var replacement = instruction.Terms with
        {
            OrderType = CanonicalOrderType.Limit,
            LimitPrice = new ScaledPrice(99, 0),
        };

        var result = await coordinator.ReplaceAsync(
            adapter.Account,
            instruction.Identity.ClientOrderId,
            replacement,
            OmsTestData.RiskSnapshot(referencePrice: 99),
            Context(instruction, "replace"));

        Assert.Equal(ExecutionCoordinatorFault.AdapterRejected, result.Fault);
        Assert.Equal(OmsCommandFault.VenueOutcomeUnknown, result.OmsResult.Fault);
        Assert.Null(result.AdapterResult);
        Assert.Equal(OrderLifecycleState.Unknown, result.OmsResult.Projection!.State);
        Assert.Contains("replace outcome is unknown", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, scheduler.PendingCount);
        var events = store.Read(instruction.Identity.ClientOrderId);
        var unknown = Assert.Single(events, item => item.Kind == OrderEventKind.OutcomeUnknown);
        Assert.Equal(OrderLifecycleState.PendingReplace, unknown.StateBefore);
        Assert.Equal(OrderLifecycleState.Unknown, unknown.StateAfter);
        Assert.Contains("replace threw after simulated dispatch", unknown.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.RecoveryObserved);
        Assert.DoesNotContain(events, item => item.Kind == OrderEventKind.ReplaceConfirmed);
    }

    [Fact]
    public async Task SnapshotImmediatelyAfterDispatch_HasCompletedFillAndMatchingPosition()
    {
        var instruction = OmsTestData.Instruction("snapshot-before-callbacks");
        var fill = new FillExecution(
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(100, 0),
            new ScaledMoney(1, 0),
            LiquidityFlag.Taker);
        using var harness = Harness.Create(
            instruction,
            new VenueSubmitPlan(
                instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [fill]));
        DraftValidatePrepareAndArm(harness, instruction);

        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));
        var snapshot = harness.Coordinator.CaptureReconciliationSnapshot(harness.Adapter.Account)!;

        Assert.True(released.IsSuccess);
        Assert.Equal(OrderLifecycleState.Acknowledging, released.OmsResult.Projection!.State);
        Assert.Equal(4, harness.Scheduler.PendingCount);
        Assert.Empty(snapshot.OpenOrders);
        var completed = Assert.Single(snapshot.CompletedOrders);
        Assert.Equal(instruction, completed.Instruction);
        Assert.Equal(OrderLifecycleState.Filled, completed.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), completed.FilledQuantity);
        var position = Assert.Single(snapshot.Positions);
        Assert.Equal(instruction.TradeIntent.Instrument, position.Instrument);
        Assert.Equal(ScaledQuantity.FromWhole(2), position.Quantity);
        Assert.DoesNotContain(
            harness.Store.Read(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.FillReceived);
    }

    [Fact]
    public async Task MalformedCallbackEnvelope_IsRejectedAgainstPublishingAccountWithoutLedgerMutation()
    {
        var first = OmsTestData.Instruction("malformed-callback-first");
        var second = OmsTestData.Instruction("malformed-callback-second");
        var clock = Clock();
        var venue = new DeterministicSimulatedVenue(clock);
        var scheduler = new ControllableAdapterEventScheduler();
        var adapter = new PublishingTestAdapter(
            new SimulatedExecutionAdapter(venue, clock, scheduler));
        var store = new InMemoryOrderEventStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            venue,
            clock);
        using var coordinator = new ExecutionCoordinator(service, adapter);
        foreach (var instruction in new[] { first, second })
        {
            Assert.True(service.CreateDraft(
                instruction,
                Context(instruction, "draft")).IsSuccess);
            Assert.True(coordinator.Validate(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                OmsTestData.RiskSnapshot(),
                Context(instruction, "validate")).IsSuccess);
            Assert.True(service.Prepare(
                instruction.Identity.ClientOrderId,
                Context(instruction, "prepare")).IsSuccess);
            Assert.True(coordinator.Arm(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                Context(instruction, "arm")).IsSuccess);
            Assert.True((await coordinator.ReleaseAsync(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                Context(instruction, "release"))).IsSuccess);
        }
        Assert.Equal(2, scheduler.PendingCount);
        var firstCount = store.Read(first.Identity.ClientOrderId).Count;
        var secondCount = store.Read(second.Identity.ClientOrderId).Count;
        var otherAccount = new BrokerExecutionAccount(
            new ExecutionAdapterId("other-adapter"),
            new BrokerAccountId("other-account"));

        adapter.Publish(new BrokerOrderEvent(
            new BrokerAdapterEventId("wrong-wrapper-account"),
            otherAccount,
            first.Identity.ClientOrderId,
            OmsTestData.TimestampUtc,
            Acknowledgement(first.Identity.ClientOrderId, "wrong-wrapper-account")));

        var wrongAccount = coordinator.GetLastCallbackResult(adapter.Account);
        Assert.True(wrongAccount.HasValue);
        Assert.Equal(OmsCommandFault.InvalidVenueEvent, wrongAccount.Value.Fault);
        Assert.Contains("another account", wrongAccount.Value.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstCount, store.Read(first.Identity.ClientOrderId).Count);
        Assert.Equal(secondCount, store.Read(second.Identity.ClientOrderId).Count);

        adapter.Publish(new BrokerOrderEvent(
            new BrokerAdapterEventId("mismatched-inner-client"),
            adapter.Account,
            first.Identity.ClientOrderId,
            OmsTestData.TimestampUtc,
            Acknowledgement(second.Identity.ClientOrderId, "mismatched-inner-client")));

        var mismatchedInner = coordinator.GetLastCallbackResult(adapter.Account);
        Assert.True(mismatchedInner.HasValue);
        Assert.Equal(OmsCommandFault.InvalidVenueEvent, mismatchedInner.Value.Fault);
        Assert.Contains("inconsistent", mismatchedInner.Value.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(firstCount, store.Read(first.Identity.ClientOrderId).Count);
        Assert.Equal(secondCount, store.Read(second.Identity.ClientOrderId).Count);
        Assert.Equal(
            OrderLifecycleState.Acknowledging,
            service.GetProjection(first.Identity.ClientOrderId).Projection!.State);
        Assert.Equal(
            OrderLifecycleState.Acknowledging,
            service.GetProjection(second.Identity.ClientOrderId).Projection!.State);
    }

    [Fact]
    public async Task CallbackStoreException_DoesNotWedgeSameAccountWorker()
    {
        var first = OmsTestData.Instruction("callback-store-failure-first");
        var second = OmsTestData.Instruction("callback-store-failure-second");
        var clock = Clock();
        var venue = new DeterministicSimulatedVenue(clock);
        var scheduler = new ControllableAdapterEventScheduler();
        var adapter = new SimulatedExecutionAdapter(venue, clock, scheduler);
        var store = new ThrowOnceOnAcknowledgementStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            venue,
            clock);
        using var coordinator = new ExecutionCoordinator(service, adapter);
        foreach (var instruction in new[] { first, second })
        {
            Assert.True(service.CreateDraft(
                instruction,
                Context(instruction, "draft")).IsSuccess);
            Assert.True(coordinator.Validate(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                OmsTestData.RiskSnapshot(),
                Context(instruction, "validate")).IsSuccess);
            Assert.True(service.Prepare(
                instruction.Identity.ClientOrderId,
                Context(instruction, "prepare")).IsSuccess);
            Assert.True(coordinator.Arm(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                Context(instruction, "arm")).IsSuccess);
            Assert.True((await coordinator.ReleaseAsync(
                adapter.Account,
                instruction.Identity.ClientOrderId,
                Context(instruction, "release"))).IsSuccess);
        }
        Assert.Equal(2, scheduler.PendingCount);

        Assert.True(scheduler.RunNext());

        var failedCallback = coordinator.GetLastCallbackResult(adapter.Account);
        Assert.True(failedCallback.HasValue);
        Assert.Equal(OmsCommandFault.PersistenceRejected, failedCallback.Value.Fault);
        Assert.Contains("injected acknowledgement failure", failedCallback.Value.Reason, StringComparison.Ordinal);
        Assert.Equal(
            OrderLifecycleState.Acknowledging,
            service.GetProjection(first.Identity.ClientOrderId).Projection!.State);

        Assert.True(scheduler.RunNext());

        var nextCallback = coordinator.GetLastCallbackResult(adapter.Account);
        Assert.True(nextCallback.HasValue);
        Assert.True(nextCallback.Value.IsSuccess);
        Assert.Equal(OrderLifecycleState.Working, nextCallback.Value.Projection!.State);
        Assert.Equal(
            OrderLifecycleState.Working,
            service.GetProjection(second.Identity.ClientOrderId).Projection!.State);
        Assert.Equal(
            OrderLifecycleState.Acknowledging,
            service.GetProjection(first.Identity.ClientOrderId).Projection!.State);
        Assert.Equal(0, scheduler.PendingCount);
    }

    private static VenueEvent Acknowledgement(ClientOrderId clientOrderId, string suffix) =>
        new(
            VenueEventKind.Acknowledged,
            clientOrderId,
            null,
            null,
            null,
            null,
            OmsTestData.TimestampUtc,
            OmsTestData.Causation(suffix),
            OmsTestData.Dedup(suffix));

    private static void DraftValidatePrepareAndArm(
        Harness harness,
        CanonicalOrderInstruction instruction)
    {
        var clientOrderId = instruction.Identity.ClientOrderId;
        Assert.True(harness.Service.CreateDraft(
            instruction,
            Context(instruction, "draft")).IsSuccess);
        Assert.True(harness.Coordinator.Validate(
            harness.Adapter.Account,
            clientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate")).IsSuccess);
        Assert.True(harness.Service.Prepare(
            clientOrderId,
            Context(instruction, "prepare")).IsSuccess);
        Assert.True(harness.Coordinator.Arm(
            harness.Adapter.Account,
            clientOrderId,
            Context(instruction, "arm")).IsSuccess);
    }

    private static OrderCommandContext Context(
        CanonicalOrderInstruction instruction,
        string suffix) =>
        new(
            OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-{suffix}"),
            OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-{suffix}"));

    private static SimClock Clock()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return clock;
    }

    private sealed class Harness : IDisposable
    {
        private Harness(
            InMemoryOrderEventStore store,
            OrderManagementService service,
            SimulatedExecutionAdapter adapter,
            ControllableAdapterEventScheduler scheduler,
            ExecutionCoordinator coordinator)
        {
            Store = store;
            Service = service;
            Adapter = adapter;
            Scheduler = scheduler;
            Coordinator = coordinator;
        }

        internal InMemoryOrderEventStore Store { get; }

        internal OrderManagementService Service { get; }

        internal SimulatedExecutionAdapter Adapter { get; }

        internal ControllableAdapterEventScheduler Scheduler { get; }

        internal ExecutionCoordinator Coordinator { get; }

        internal static Harness Create(
            CanonicalOrderInstruction instruction,
            VenueSubmitPlan? plan = null,
            SimClock? clock = null,
            DeterministicSimulatedVenue? venue = null,
            ControllableAdapterEventScheduler? scheduler = null,
            SimulatedExecutionAdapter? adapter = null,
            BrokerExecutionSession? session = null,
            BrokerExecutionCapabilities? capabilities = null,
            RiskEngine? riskEngine = null,
            bool duplicateCallbacks = false)
        {
            clock ??= Clock();
            venue ??= new DeterministicSimulatedVenue(
                clock,
                plan is null ? null : [plan]);
            scheduler ??= new ControllableAdapterEventScheduler();
            adapter ??= new SimulatedExecutionAdapter(
                venue,
                clock,
                scheduler,
                session,
                capabilities,
                duplicateCallbacks);
            var store = new InMemoryOrderEventStore();
            var service = new OrderManagementService(
                store,
                riskEngine ?? OmsTestData.RiskEngine(),
                venue,
                clock);
            return new Harness(
                store,
                service,
                adapter,
                scheduler,
                new ExecutionCoordinator(service, adapter));
        }

        public void Dispose() => Coordinator.Dispose();
    }

    private sealed class PublishingTestAdapter(SimulatedExecutionAdapter inner)
        : IBrokerExecutionAdapter
    {
        public string BrokerId => inner.BrokerId;

        public ExecutionMode Mode => inner.Mode;

        public BrokerExecutionAccount Account => inner.Account;

        public BrokerExecutionSession Session => inner.Session;

        public BrokerExecutionCapabilities Capabilities => inner.Capabilities;

        public event Action<BrokerAdapterEvent>? EventReceived;

        public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command) => inner.Submit(command);

        public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command) => inner.Cancel(command);

        public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command) => inner.Replace(command);

        public BrokerOrderQueryResult Query(BrokerOrderQuery query) => inner.Query(query);

        public BrokerReconciliationSnapshot CaptureReconciliationSnapshot() =>
            inner.CaptureReconciliationSnapshot();

        internal void Publish(BrokerAdapterEvent adapterEvent) => EventReceived?.Invoke(adapterEvent);
    }

    private sealed class ThrowOnceOnAcknowledgementStore : IOrderEventStore
    {
        private readonly InMemoryOrderEventStore _inner = new();
        private int _throwNextAcknowledgement = 1;

        public OrderEventAppendResult Append(OrderEventDraft draft, DateTime recordedAtUtc)
        {
            if (draft.Kind == OrderEventKind.VenueAcknowledged &&
                Interlocked.Exchange(ref _throwNextAcknowledgement, 0) == 1)
            {
                throw new InvalidOperationException("injected acknowledgement failure");
            }

            return _inner.Append(draft, recordedAtUtc);
        }

        public IReadOnlyList<TradingTerminal.Execution.Oms.OrderEvent> Read(ClientOrderId aggregateId) =>
            _inner.Read(aggregateId);

        public IReadOnlyList<OrderEventOutboxEntry> ReadOutbox(long afterExclusiveSequence = 0) =>
            _inner.ReadOutbox(afterExclusiveSequence);
    }

    private sealed class ThrowAfterDispatchAdapter(
        SimulatedExecutionAdapter inner,
        bool throwAfterCancel = false,
        bool throwAfterReplace = false)
        : IBrokerExecutionAdapter
    {
        public string BrokerId => inner.BrokerId;

        public ExecutionMode Mode => inner.Mode;

        public BrokerExecutionAccount Account => inner.Account;

        public BrokerExecutionSession Session => inner.Session;

        public BrokerExecutionCapabilities Capabilities => inner.Capabilities;

        public event Action<BrokerAdapterEvent>? EventReceived
        {
            add => inner.EventReceived += value;
            remove => inner.EventReceived -= value;
        }

        public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command) => inner.Submit(command);

        public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command)
        {
            var result = inner.Cancel(command);
            if (throwAfterCancel)
                throw new InvalidOperationException("cancel threw after simulated dispatch");
            return result;
        }

        public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command)
        {
            var result = inner.Replace(command);
            if (throwAfterReplace)
                throw new InvalidOperationException("replace threw after simulated dispatch");
            return result;
        }

        public BrokerOrderQueryResult Query(BrokerOrderQuery query) => inner.Query(query);

        public BrokerReconciliationSnapshot CaptureReconciliationSnapshot() =>
            inner.CaptureReconciliationSnapshot();
    }

    private sealed class TestDirectory : IDisposable
    {
        private static readonly string AllowedRoot = Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "DaxAlgo.Execution.Tests"));

        internal TestDirectory()
        {
            Root = Path.GetFullPath(Path.Combine(AllowedRoot, Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(Root);
        }

        private string Root { get; }

        internal string File(string name) => Path.Combine(Root, name);

        public void Dispose()
        {
            var expectedPrefix = AllowedRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Root.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a test directory outside the allowed root.");
            if (!Directory.Exists(Root))
                return;

            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.TopDirectoryOnly))
                System.IO.File.Delete(file);
            Directory.Delete(Root, recursive: false);
        }
    }
}
