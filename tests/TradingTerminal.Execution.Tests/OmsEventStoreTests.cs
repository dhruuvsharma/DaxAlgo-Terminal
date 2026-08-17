using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class OmsEventStoreTests
{
    [Fact]
    public void ExactInboxReplay_ReturnsOriginalEvent_AndCreatesNoSecondOutboxEntry()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();

        var first = store.Append(draft, OmsTestData.TimestampUtc);
        var replay = store.Append(draft, OmsTestData.TimestampUtc);

        Assert.True(first.WasAppended);
        Assert.True(replay.IsExactReplay);
        Assert.Same(first.Event, replay.Event);
        Assert.Single(store.Read(draft.AggregateId));
        Assert.Single(store.ReadOutbox());
    }

    [Fact]
    public void ConflictingInboxReplay_IsRejectedWithoutMutation()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);

        var conflict = store.Append(
            draft with { Reason = "different content under the same inbox key" },
            OmsTestData.TimestampUtc);

        Assert.Equal(OrderEventAppendStatus.Rejected, conflict.Status);
        Assert.Equal(OrderEventAppendFault.ConflictingDuplicate, conflict.Fault);
        Assert.Single(store.Read(draft.AggregateId));
        Assert.Single(store.ReadOutbox());
    }

    [Fact]
    public void IllegalTransition_IsRejectedRatherThanApplied()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);

        var illegal = store.Append(
            new OrderEventDraft(
                draft.AggregateId,
                OrderEventKind.Armed,
                OrderLifecycleState.Armed,
                OrderEventSource.Command,
                OmsTestData.Dedup("skip-validation"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("skip-validation")),
            OmsTestData.TimestampUtc);

        Assert.Equal(OrderEventAppendFault.IllegalTransition, illegal.Fault);
        Assert.Equal(OrderLifecycleState.Draft, OrderProjection.Rebuild(store.Read(draft.AggregateId)).Projection!.State);
    }

    [Fact]
    public void LegalStateEdge_WithWrongEventKind_IsRejected()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);

        var mislabeled = store.Append(
            new OrderEventDraft(
                draft.AggregateId,
                OrderEventKind.Prepared,
                OrderLifecycleState.Validated,
                OrderEventSource.Command,
                OmsTestData.Dedup("mislabeled"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("mislabeled")),
            OmsTestData.TimestampUtc);

        Assert.True(OrderLifecycle.CanTransition(OrderLifecycleState.Draft, OrderLifecycleState.Validated));
        Assert.False(OrderLifecycle.CanApplyEvent(
            OrderEventKind.Prepared,
            OrderLifecycleState.Draft,
            OrderLifecycleState.Validated));
        Assert.Equal(OrderEventAppendFault.IllegalTransition, mislabeled.Fault);
        Assert.Single(store.Read(draft.AggregateId));
    }

    [Fact]
    public void LegalRiskEdge_WithCommandSource_IsRejected()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);

        var impersonatedRisk = store.Append(
            new OrderEventDraft(
                draft.AggregateId,
                OrderEventKind.RiskAccepted,
                OrderLifecycleState.Validated,
                OrderEventSource.Command,
                OmsTestData.Dedup("wrong-risk-source"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("wrong-risk-source")),
            OmsTestData.TimestampUtc);

        Assert.True(OrderLifecycle.CanApplyEvent(
            OrderEventKind.RiskAccepted,
            OrderLifecycleState.Draft,
            OrderLifecycleState.Validated));
        Assert.Equal(OrderEventAppendFault.InvalidEventSource, impersonatedRisk.Fault);
        Assert.Single(store.Read(draft.AggregateId));
    }

    [Fact]
    public void RecoveryCannotProjectPartialFillWithoutFillEconomics()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        var store = new InMemoryOrderEventStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            new DeterministicSimulatedVenue(clock),
            clock);
        var instruction = OmsTestData.Instruction();
        Assert.True(service.CreateDraft(instruction, Context("draft")).IsSuccess);
        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context("validate")).IsSuccess);
        Assert.True(service.Prepare(instruction.Identity.ClientOrderId, Context("prepare")).IsSuccess);
        Assert.True(service.Arm(instruction.Identity.ClientOrderId, Context("arm")).IsSuccess);
        Assert.True(service.Release(instruction.Identity.ClientOrderId, Context("release")).IsSuccess);
        Assert.True(store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.CancelRequested,
                OrderLifecycleState.PendingCancel,
                OrderEventSource.Command,
                OmsTestData.Dedup("pending-cancel"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("pending-cancel")),
            OmsTestData.TimestampUtc).IsSuccess);

        var malformedRecovery = store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.RecoveryObserved,
                OrderLifecycleState.PartiallyFilled,
                OrderEventSource.Recovery,
                OmsTestData.Dedup("fabricated-partial"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("fabricated-partial")),
            OmsTestData.TimestampUtc);

        Assert.Equal(OrderEventAppendFault.ProjectionRejected, malformedRecovery.Fault);
        Assert.Equal(OrderProjectionFault.FillStateMismatch, malformedRecovery.ProjectionFault);
        Assert.Equal(
            OrderLifecycleState.PendingCancel,
            OrderProjection.Rebuild(store.Read(instruction.Identity.ClientOrderId)).Projection!.State);
    }

    [Fact]
    public void ReplacementRequestWithoutImmediatelyAcceptedMatchingRisk_IsRejected()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        var store = new InMemoryOrderEventStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(),
            new DeterministicSimulatedVenue(clock),
            clock);
        var instruction = OmsTestData.Instruction();
        Assert.True(service.CreateDraft(instruction, Context("draft")).IsSuccess);
        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context("validate")).IsSuccess);
        Assert.True(service.Prepare(instruction.Identity.ClientOrderId, Context("prepare")).IsSuccess);
        Assert.True(service.Arm(instruction.Identity.ClientOrderId, Context("arm")).IsSuccess);
        Assert.True(service.Release(instruction.Identity.ClientOrderId, Context("release")).IsSuccess);
        var replacement = new CanonicalOrderTerms(
            TradingTerminal.Core.Trading.OrderSide.Buy,
            CanonicalOrderType.Limit,
            CanonicalTimeInForce.Day,
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(99, 0),
            null);

        var bypass = store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.ReplaceRequested,
                OrderLifecycleState.PendingReplace,
                OrderEventSource.Command,
                OmsTestData.Dedup("replace-without-risk"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("replace-without-risk"),
                ReplacementTerms: replacement),
            OmsTestData.TimestampUtc);

        Assert.Equal(OrderEventAppendFault.ProjectionRejected, bypass.Fault);
        Assert.Equal(OrderProjectionFault.MissingReplacementAuthorization, bypass.ProjectionFault);
        Assert.Equal(
            OrderLifecycleState.Working,
            OrderProjection.Rebuild(store.Read(instruction.Identity.ClientOrderId)).Projection!.State);
    }

    [Fact]
    public void ReconciliationEvidence_IsRequiredOnlyOnReconciledEvents()
    {
        var unexpectedStore = new InMemoryOrderEventStore();
        var unexpected = unexpectedStore.Append(
            DraftCreated() with
            {
                Reconciliation = new ReconciliationResolution(
                    new ReconciliationCaseId("unexpected-case"),
                    OrderLifecycleState.Rejected,
                    "Evidence on the wrong event kind."),
            },
            OmsTestData.TimestampUtc);
        Assert.Equal(OrderEventAppendFault.ProjectionRejected, unexpected.Fault);
        Assert.Equal(OrderProjectionFault.UnexpectedReconciliation, unexpected.ProjectionFault);

        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        var store = new InMemoryOrderEventStore();
        var service = new OrderManagementService(
            store,
            OmsTestData.RiskEngine(maximumOrderQuantity: 1),
            new DeterministicSimulatedVenue(clock),
            clock);
        var instruction = OmsTestData.Instruction();
        Assert.True(service.CreateDraft(instruction, Context("rejected-draft")).IsSuccess);
        Assert.Equal(
            OmsCommandFault.RiskRejected,
            service.Validate(
                instruction.Identity.ClientOrderId,
                OmsTestData.RiskSnapshot(),
                Context("rejected-validation")).Fault);

        var missing = store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.Reconciled,
                OrderLifecycleState.Reconciled,
                OrderEventSource.Reconciliation,
                OmsTestData.Dedup("missing-resolution"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("missing-resolution")),
            OmsTestData.TimestampUtc);

        Assert.Equal(OrderEventAppendFault.ProjectionRejected, missing.Fault);
        Assert.Equal(OrderProjectionFault.InvalidReconciliation, missing.ProjectionFault);
        Assert.Equal(
            OrderLifecycleState.Rejected,
            OrderProjection.Rebuild(store.Read(instruction.Identity.ClientOrderId)).Projection!.State);

        var unknownInstruction = OmsTestData.Instruction("reconciliation-invalid-observed");
        var unknownPlan = new VenueSubmitPlan(
            unknownInstruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Unknown);
        var reconciliationStore = new InMemoryOrderEventStore();
        var reconciliationService = new OrderManagementService(
            reconciliationStore,
            OmsTestData.RiskEngine(),
            new DeterministicSimulatedVenue(clock, [unknownPlan]),
            clock);
        Assert.True(reconciliationService.CreateDraft(
            unknownInstruction,
            Context("unknown-draft")).IsSuccess);
        Assert.True(reconciliationService.Validate(
            unknownInstruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context("unknown-validation")).IsSuccess);
        Assert.True(reconciliationService.Prepare(
            unknownInstruction.Identity.ClientOrderId,
            Context("unknown-prepare")).IsSuccess);
        Assert.True(reconciliationService.Arm(
            unknownInstruction.Identity.ClientOrderId,
            Context("unknown-arm")).IsSuccess);
        Assert.Equal(
            OmsCommandFault.VenueOutcomeUnknown,
            reconciliationService.Release(
                unknownInstruction.Identity.ClientOrderId,
                Context("unknown-release")).Fault);
        Assert.True(reconciliationService.BeginReconciliation(
            unknownInstruction.Identity.ClientOrderId,
            Context("unknown-reconciliation")).IsSuccess);
        var invalidResolution = new ReconciliationResolution(
            new ReconciliationCaseId("invalid-observed-case"),
            OrderLifecycleState.Working,
            "Working is not terminal reconciliation evidence.");
        Assert.False(invalidResolution.IsValid);

        var invalidObservedState = reconciliationStore.Append(
            new OrderEventDraft(
                unknownInstruction.Identity.ClientOrderId,
                OrderEventKind.Reconciled,
                OrderLifecycleState.Reconciled,
                OrderEventSource.Reconciliation,
                OmsTestData.Dedup("invalid-observed-state"),
                OmsTestData.TimestampUtc,
                OmsTestData.Causation("invalid-observed-state"),
                Reconciliation: invalidResolution),
            OmsTestData.TimestampUtc);

        Assert.Equal(OrderEventAppendFault.ProjectionRejected, invalidObservedState.Fault);
        Assert.Equal(
            OrderProjectionFault.InvalidReconciliation,
            invalidObservedState.ProjectionFault);
    }

    [Fact]
    public void HashVerifier_DetectsTamperedImmutableEventCopy()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);
        var original = Assert.Single(store.Read(draft.AggregateId));
        var tampered = original with { Reason = "tampered after append" };

        var verification = OrderEventChainVerifier.Verify([tampered]);

        Assert.False(verification.IsValid);
        Assert.Equal(OrderEventChainFault.EventHashMismatch, verification.Fault);
        Assert.Equal(0, verification.EventIndex);
    }

    [Fact]
    public void HashVerifier_RejectsTamperedTimestampKind()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);
        var original = Assert.Single(store.Read(draft.AggregateId));
        var tampered = original with
        {
            OccurredAtUtc = DateTime.SpecifyKind(original.OccurredAtUtc, DateTimeKind.Unspecified),
        };

        var verification = OrderEventChainVerifier.Verify([tampered]);

        Assert.False(verification.IsValid);
        Assert.Equal(OrderEventChainFault.InvalidTimestamp, verification.Fault);
    }

    [Fact]
    public void Projection_RebuildsFromDetachedEventSnapshotOnly()
    {
        var store = new InMemoryOrderEventStore();
        var draft = DraftCreated();
        Assert.True(store.Append(draft, OmsTestData.TimestampUtc).IsSuccess);
        var detached = store.Read(draft.AggregateId).ToArray();

        var rebuilt = OrderProjection.Rebuild(detached);

        Assert.True(rebuilt.IsSuccess);
        Assert.Equal(OrderLifecycleState.Draft, rebuilt.Projection!.State);
        Assert.Equal(draft.Instruction, rebuilt.Projection.Instruction);
        Assert.Equal(ScaledQuantity.Zero, rebuilt.Projection.FilledQuantity);
        Assert.Equal(ScaledMoney.Zero, rebuilt.Projection.TotalFees);
    }

    private static OrderEventDraft DraftCreated()
    {
        var instruction = OmsTestData.Instruction();
        return new OrderEventDraft(
            instruction.Identity.ClientOrderId,
            OrderEventKind.DraftCreated,
            OrderLifecycleState.Draft,
            OrderEventSource.Command,
            OmsTestData.Dedup("draft"),
            OmsTestData.TimestampUtc,
            OmsTestData.Causation("draft"),
            Instruction: instruction);
    }

    private static OrderCommandContext Context(string suffix) =>
        new(OmsTestData.Causation(suffix), OmsTestData.Dedup(suffix));
}
