using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class OrderManagementServiceTests
{
    [Fact]
    public void RiskBreach_IsVersionedObservableRejection_AndNeverClamps()
    {
        var instruction = OmsTestData.Instruction(target: 2);
        var risk = OmsTestData.RiskEngine(maximumOrderQuantity: 1, policyVersion: "cap-v1");
        var (service, _, venue) = CreateService(risk);
        Assert.True(service.CreateDraft(instruction, Context("draft")).IsSuccess);

        var result = service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(target: 2),
            Context("validate"));

        Assert.Equal(OmsCommandFault.RiskRejected, result.Fault);
        Assert.Equal(OrderLifecycleState.Rejected, result.Projection!.State);
        var decision = Assert.IsType<RiskDecisionRecord>(result.RiskDecision);
        Assert.Equal("cap-v1", decision.PolicyVersion);
        Assert.Equal(64, decision.PolicyHash.Length);
        Assert.Equal(RiskReasonCode.MaximumOrderQuantityExceeded, decision.ReasonCodes);
        Assert.Equal(ScaledQuantity.FromWhole(2), decision.SignedOrderQuantity);
        Assert.Equal(ScaledQuantity.FromWhole(2), decision.Input.Intent.SignedUnits);
        Assert.Equal(RiskDecisionOutcome.Rejected, result.Projection.RiskDecision!.Value.Outcome);
        Assert.False(venue.Query(instruction.Identity.ClientOrderId).Found);

        var prior = result.Projection.RiskDecision;
        risk.ReplacePolicy(OmsTestData.RiskEngine(policyVersion: "cap-v2").CurrentPolicy);
        Assert.Equal(prior, service.GetProjection(instruction.Identity.ClientOrderId).Projection!.RiskDecision);
    }

    [Fact]
    public void UnsupportedVenueCapability_IsRejectedBeforeRiskOrArming()
    {
        var instruction = OmsTestData.Instruction(
            orderType: CanonicalOrderType.Limit,
            limitPrice: new ScaledPrice(100, 0));
        var risk = OmsTestData.RiskEngine();
        var capabilities = new VenueCapabilities(
            SupportedOrderTypes.Market,
            SupportedTimeInForce.All);
        var (service, _, _) = CreateService(risk, capabilities: capabilities);
        Assert.True(service.CreateDraft(instruction, Context("draft")).IsSuccess);

        var result = service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context("validate"));

        Assert.Equal(OmsCommandFault.UnsupportedCapability, result.Fault);
        Assert.Equal(OrderLifecycleState.Rejected, result.Projection!.State);
        Assert.Empty(risk.Decisions);
        Assert.DoesNotContain(
            service.ReadEvents(instruction.Identity.ClientOrderId),
            orderEvent => orderEvent.StateAfter == OrderLifecycleState.Armed);
    }

    [Fact]
    public void TradeIntent_ToSimulatedFills_ToTerminalLedger_RoundTripsExactly()
    {
        var instruction = OmsTestData.Instruction();
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [
                new FillExecution(
                    ScaledQuantity.FromWhole(1),
                    new ScaledPrice(10_000, 2),
                    new ScaledMoney(25, 2),
                    LiquidityFlag.Maker),
                new FillExecution(
                    ScaledQuantity.FromWhole(1),
                    new ScaledPrice(10_100, 2),
                    new ScaledMoney(50, 2),
                    LiquidityFlag.Taker),
            ]);
        var (service, store, venue) = CreateService(OmsTestData.RiskEngine(), [plan]);
        Arm(service, instruction);

        var result = service.Release(instruction.Identity.ClientOrderId, Context("release"));

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderLifecycleState.Filled, result.Projection!.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), result.Projection.FilledQuantity);
        Assert.Equal(new ScaledMoney(75, 2), result.Projection.TotalFees);
        Assert.NotNull(result.Projection.RiskDecision);
        Assert.True(result.Projection.BrokerOrderId!.Value.IsValid);
        Assert.True(result.Projection.ExchangeOrderId!.Value.IsValid);
        Assert.True(OrderLifecycle.IsTerminal(result.Projection.State));

        var events = service.ReadEvents(instruction.Identity.ClientOrderId);
        Assert.Equal(events.Count, store.ReadOutbox().Count);
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
        var rebuilt = OrderProjection.Rebuild(events.ToArray());
        Assert.True(rebuilt.IsSuccess);
        Assert.Equal(result.Projection, rebuilt.Projection);

        var simulated = venue.Query(instruction.Identity.ClientOrderId);
        Assert.True(simulated.Found);
        Assert.Equal(OrderLifecycleState.Filled, simulated.Order!.State);
        Assert.Equal(ScaledQuantity.FromWhole(2), simulated.Order.FilledQuantity);

        var reconciled = service.CompleteReconciliation(
            instruction.Identity.ClientOrderId,
            new ReconciliationResolution(
                new ReconciliationCaseId("case-filled"),
                OrderLifecycleState.Filled,
                "Terminal simulated fill ledger matches venue state."),
            Context("reconcile-filled"));
        Assert.True(reconciled.IsSuccess);
        Assert.Equal(OrderLifecycleState.Reconciled, reconciled.Projection!.State);
    }

    [Fact]
    public void DuplicateVenueFillCallback_DoesNotDoubleCountEconomics()
    {
        var instruction = OmsTestData.Instruction();
        var fill = new FillExecution(
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(100, 0),
            new ScaledMoney(3, 0),
            LiquidityFlag.Taker);
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [fill]);
        var (service, _, venue) = CreateService(OmsTestData.RiskEngine(), [plan]);
        Arm(service, instruction);
        var filled = service.Release(instruction.Identity.ClientOrderId, Context("release"));
        Assert.True(filled.IsSuccess);
        var eventCount = service.ReadEvents(instruction.Identity.ClientOrderId).Count;

        var replay = venue.Submit(instruction, OmsTestData.Causation("venue-replay"));
        Assert.Equal(VenueCommandStatus.IdempotentReplay, replay.Status);
        var duplicateFill = Assert.Single(replay.Events, item => item.Kind == VenueEventKind.Fill);
        var duplicateResult = service.ApplyVenueEvent(duplicateFill);

        Assert.True(duplicateResult.IsSuccess);
        Assert.Equal(eventCount, service.ReadEvents(instruction.Identity.ClientOrderId).Count);
        Assert.Equal(ScaledQuantity.FromWhole(2), duplicateResult.Projection!.FilledQuantity);
        Assert.Equal(new ScaledMoney(3, 0), duplicateResult.Projection.TotalFees);
    }

    [Fact]
    public void Unknown_BlocksBlindRetry_UntilExplicitReconciliation()
    {
        var instruction = OmsTestData.Instruction();
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Unknown,
            reason: "deterministic crash window");
        var (service, _, _) = CreateService(OmsTestData.RiskEngine(), [plan]);
        Arm(service, instruction);

        var unknown = service.Release(instruction.Identity.ClientOrderId, Context("release"));
        var countBeforeRetry = service.ReadEvents(instruction.Identity.ClientOrderId).Count;
        var retry = service.Release(instruction.Identity.ClientOrderId, Context("blind-retry"));

        Assert.Equal(OmsCommandFault.VenueOutcomeUnknown, unknown.Fault);
        Assert.Equal(OrderLifecycleState.Unknown, unknown.Projection!.State);
        Assert.True(unknown.Projection.BlocksRetry);
        Assert.Equal(OmsCommandFault.RetryBlockedUnknown, retry.Fault);
        Assert.Equal(countBeforeRetry, service.ReadEvents(instruction.Identity.ClientOrderId).Count);
        Assert.NotEqual(OrderLifecycleState.Rejected, retry.Projection!.State);

        var reconciling = service.BeginReconciliation(
            instruction.Identity.ClientOrderId,
            Context("reconcile-start"));
        Assert.True(reconciling.IsSuccess);
        Assert.Equal(OrderLifecycleState.Reconciling, reconciling.Projection!.State);
        Assert.True(reconciling.Projection.BlocksRetry);

        var reconciled = service.CompleteReconciliation(
            instruction.Identity.ClientOrderId,
            new ReconciliationResolution(
                new ReconciliationCaseId("case-1"),
                OrderLifecycleState.Rejected,
                "Simulator proved no accepted order remained."),
            Context("reconcile-complete"));
        Assert.True(reconciled.IsSuccess);
        Assert.Equal(OrderLifecycleState.Reconciled, reconciled.Projection!.State);
    }

    [Fact]
    public void ProvedPreAcceptanceFailure_RetriesWithSameClientOrderId()
    {
        var instruction = OmsTestData.Instruction();
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.FailedBeforeAcceptance);
        var (service, _, _) = CreateService(OmsTestData.RiskEngine(), [plan]);
        Arm(service, instruction);

        var first = service.Release(instruction.Identity.ClientOrderId, Context("release-1"));
        var second = service.Release(instruction.Identity.ClientOrderId, Context("release-2"));

        Assert.Equal(OmsCommandFault.VenueFailedBeforeAcceptance, first.Fault);
        Assert.True(first.CanRetrySameClientOrderId);
        Assert.Equal(OmsCommandFault.VenueFailedBeforeAcceptance, second.Fault);
        Assert.True(second.CanRetrySameClientOrderId);
        Assert.Equal(OrderLifecycleState.Armed, second.Projection!.State);
        var events = service.ReadEvents(instruction.Identity.ClientOrderId);
        Assert.Equal(2, events.Count(item => item.Kind == OrderEventKind.SendStarted));
        var failures = events.Where(item => item.Kind == OrderEventKind.SendFailedBeforeAcceptance).ToArray();
        Assert.Equal(2, failures.Length);
        Assert.All(failures, item => Assert.Equal(instruction.Identity.ClientOrderId, item.AggregateId));
        Assert.Equal(2, failures.Select(item => item.DeduplicationKey).Distinct().Count());
    }

    [Fact]
    public void FillBeforeAcknowledgement_IsAcceptedAndLateAckCannotRollStateBackward()
    {
        var instruction = OmsTestData.Instruction();
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [
                new FillExecution(
                    ScaledQuantity.FromWhole(2),
                    new ScaledPrice(100, 0),
                    ScaledMoney.Zero,
                    LiquidityFlag.Taker),
            ],
            fillBeforeAcknowledgement: true);
        var (service, _, _) = CreateService(OmsTestData.RiskEngine(), [plan]);
        Arm(service, instruction);

        var result = service.Release(instruction.Identity.ClientOrderId, Context("release"));

        Assert.True(result.IsSuccess);
        Assert.Equal(OrderLifecycleState.Filled, result.Projection!.State);
        var events = service.ReadEvents(instruction.Identity.ClientOrderId);
        var fillIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.FillReceived);
        var ackIndex = events.ToList().FindIndex(item => item.Kind == OrderEventKind.VenueAcknowledged);
        Assert.True(fillIndex >= 0 && ackIndex > fillIndex);
        Assert.Equal(OrderLifecycleState.Filled, events[ackIndex].StateBefore);
        Assert.Equal(OrderLifecycleState.Filled, events[ackIndex].StateAfter);
        Assert.True(OrderEventChainVerifier.Verify(events).IsValid);
    }

    [Fact]
    public void PreparedRecovery_RemainsDisarmedUntilFreshAuthorization()
    {
        var instruction = OmsTestData.Instruction();
        var (service, _, _) = CreateService(OmsTestData.RiskEngine());
        Assert.True(service.CreateDraft(instruction, Context("draft")).IsSuccess);
        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context("validate")).IsSuccess);
        Assert.True(service.Prepare(instruction.Identity.ClientOrderId, Context("prepare")).IsSuccess);

        var recovered = service.ObservePreparedRecovery(
            instruction.Identity.ClientOrderId,
            Context("recover"));
        var prematureRelease = service.Release(
            instruction.Identity.ClientOrderId,
            Context("premature-release"));

        Assert.True(recovered.IsSuccess);
        Assert.Equal(OrderLifecycleState.Prepared, recovered.Projection!.State);
        Assert.Equal(OmsCommandFault.IllegalTransition, prematureRelease.Fault);
        Assert.Equal(OrderLifecycleState.Prepared, prematureRelease.Projection!.State);

        Assert.True(service.Arm(instruction.Identity.ClientOrderId, Context("fresh-arm")).IsSuccess);
        var released = service.Release(instruction.Identity.ClientOrderId, Context("release"));
        Assert.True(released.IsSuccess);
        Assert.Equal(OrderLifecycleState.Working, released.Projection!.State);
    }

    [Fact]
    public void SendStartedWithoutAcknowledgement_RecoversAsUnknownAndBlocksRetry()
    {
        var instruction = OmsTestData.Instruction();
        var (service, store, _) = CreateService(OmsTestData.RiskEngine());
        Arm(service, instruction);
        var crashContext = Context("crash-send");
        var sendStarted = store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.SendStarted,
                OrderLifecycleState.Releasing,
                OrderEventSource.Command,
                crashContext.DeduplicationKey.Derive("send-started"),
                OmsTestData.TimestampUtc,
                crashContext.CausationId),
            OmsTestData.TimestampUtc);
        Assert.True(sendStarted.IsSuccess);

        var recovered = service.Release(
            instruction.Identity.ClientOrderId,
            crashContext);
        var retry = service.Release(
            instruction.Identity.ClientOrderId,
            Context("unsafe-retry"));

        Assert.Equal(OmsCommandFault.VenueOutcomeUnknown, recovered.Fault);
        Assert.Equal(OrderLifecycleState.Unknown, recovered.Projection!.State);
        Assert.True(recovered.Projection.BlocksRetry);
        Assert.Equal(OmsCommandFault.RetryBlockedUnknown, retry.Fault);
    }

    [Fact]
    public void AtLeastOnceCommandReplay_ReturnsCommittedStateWithoutDuplicateEvents()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        var store = new InMemoryOrderEventStore();
        var venue = new DeterministicSimulatedVenue(clock);
        var service = new OrderManagementService(store, OmsTestData.RiskEngine(), venue, clock);
        var instruction = OmsTestData.Instruction();
        var draftContext = Context("draft-replay");
        var validateContext = Context("validate-replay");
        var prepareContext = Context("prepare-replay");
        var armContext = Context("arm-replay");
        var releaseContext = Context("release-replay");

        Assert.True(service.CreateDraft(instruction, draftContext).IsSuccess);
        clock.SetTo(OmsTestData.TimestampUtc.AddSeconds(1));
        Assert.True(service.CreateDraft(instruction, draftContext).IsSuccess);
        Assert.Single(service.ReadEvents(instruction.Identity.ClientOrderId));

        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            validateContext).IsSuccess);
        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            validateContext).IsSuccess);
        Assert.True(service.Prepare(instruction.Identity.ClientOrderId, prepareContext).IsSuccess);
        Assert.True(service.Prepare(instruction.Identity.ClientOrderId, prepareContext).IsSuccess);
        Assert.True(service.Arm(instruction.Identity.ClientOrderId, armContext).IsSuccess);
        Assert.True(service.Arm(instruction.Identity.ClientOrderId, armContext).IsSuccess);
        Assert.Equal(4, service.ReadEvents(instruction.Identity.ClientOrderId).Count);

        Assert.True(service.Release(instruction.Identity.ClientOrderId, releaseContext).IsSuccess);
        var releasedEventCount = service.ReadEvents(instruction.Identity.ClientOrderId).Count;
        var replayedRelease = service.Release(instruction.Identity.ClientOrderId, releaseContext);
        Assert.True(replayedRelease.IsSuccess);
        Assert.Equal(OrderLifecycleState.Working, replayedRelease.Projection!.State);
        Assert.Equal(releasedEventCount, service.ReadEvents(instruction.Identity.ClientOrderId).Count);
    }

    [Fact]
    public void SimulatedReplaceQueryAndCancel_UseExactTermsAndPendingStates()
    {
        var instruction = OmsTestData.Instruction();
        var (service, _, _) = CreateService(
            OmsTestData.RiskEngine(maximumOrderNotional: 250));
        Arm(service, instruction);
        Assert.True(service.Release(instruction.Identity.ClientOrderId, Context("release")).IsSuccess);
        var riskIncreasingReplacement = new CanonicalOrderTerms(
            OrderSide.Buy,
            CanonicalOrderType.Limit,
            CanonicalTimeInForce.GoodTillCancelled,
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(1_000, 0),
            null);
        var countBeforeUnvalidatedReplace = service.ReadEvents(instruction.Identity.ClientOrderId).Count;
        var unvalidatedReplace = service.Replace(
            instruction.Identity.ClientOrderId,
            riskIncreasingReplacement,
            Context("unvalidated-replace"));
        Assert.Equal(OmsCommandFault.ReplaceRequiresNewValidation, unvalidatedReplace.Fault);
        Assert.Equal(
            countBeforeUnvalidatedReplace,
            service.ReadEvents(instruction.Identity.ClientOrderId).Count);

        var rejectedContext = Context("risk-increasing-replace");
        var rejectedRisk = OmsTestData.RiskSnapshot(referencePrice: 1_000);
        var countBeforeRejectedReplace = service.ReadEvents(instruction.Identity.ClientOrderId).Count;
        var rejectedReplace = service.Replace(
            instruction.Identity.ClientOrderId,
            riskIncreasingReplacement,
            rejectedRisk,
            rejectedContext);
        var countAfterRejectedReplace = service.ReadEvents(instruction.Identity.ClientOrderId).Count;
        var rejectedReplay = service.Replace(
            instruction.Identity.ClientOrderId,
            riskIncreasingReplacement,
            rejectedRisk,
            rejectedContext);

        Assert.Equal(OmsCommandFault.RiskRejected, rejectedReplace.Fault);
        Assert.Equal(OrderLifecycleState.Working, rejectedReplace.Projection!.State);
        Assert.Equal(instruction.Terms, rejectedReplace.Projection.Terms);
        Assert.Equal(countBeforeRejectedReplace + 1, countAfterRejectedReplace);
        Assert.Equal(countAfterRejectedReplace, service.ReadEvents(instruction.Identity.ClientOrderId).Count);
        Assert.Equal(OmsCommandFault.RiskRejected, rejectedReplay.Fault);
        Assert.Contains(
            service.ReadEvents(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.ReplaceRiskRejected &&
                    item.ReplacementTerms == riskIncreasingReplacement);
        Assert.DoesNotContain(
            service.ReadEvents(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.ReplaceRequested &&
                    item.ReplacementTerms == riskIncreasingReplacement);

        var replacement = new CanonicalOrderTerms(
            OrderSide.Buy,
            CanonicalOrderType.Limit,
            CanonicalTimeInForce.GoodTillCancelled,
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(9_950, 2),
            null);
        var replacementRisk = OmsTestData.RiskSnapshot(referencePrice: 100);
        var replacementContext = Context("replace");
        var replaced = service.Replace(
            instruction.Identity.ClientOrderId,
            replacement,
            replacementRisk,
            replacementContext);
        var countAfterReplace = service.ReadEvents(instruction.Identity.ClientOrderId).Count;
        var replacementReplay = service.Replace(
            instruction.Identity.ClientOrderId,
            replacement,
            replacementRisk,
            replacementContext);

        Assert.True(replaced.IsSuccess);
        Assert.Equal(OrderLifecycleState.Working, replaced.Projection!.State);
        Assert.Equal(replacement, replaced.Projection.Terms);
        Assert.True(replacementReplay.IsSuccess);
        Assert.Equal(OrderLifecycleState.Working, replacementReplay.Projection!.State);
        Assert.Equal(countAfterReplace, service.ReadEvents(instruction.Identity.ClientOrderId).Count);

        var queried = service.QuerySimulatedVenue(instruction.Identity.ClientOrderId);
        var cancelled = service.Cancel(instruction.Identity.ClientOrderId, Context("cancel"));

        Assert.True(queried.Found);
        Assert.Equal(replacement, queried.Order!.CurrentTerms);
        Assert.True(cancelled.IsSuccess);
        Assert.Equal(OrderLifecycleState.Cancelled, cancelled.Projection!.State);
        var events = service.ReadEvents(instruction.Identity.ClientOrderId);
        Assert.Contains(events, item => item.StateAfter == OrderLifecycleState.PendingReplace);
        Assert.Contains(events, item => item.StateAfter == OrderLifecycleState.PendingCancel);
    }

    [Fact]
    public void ReplaceAfterPartialFill_PreservesPartiallyFilledState()
    {
        var instruction = OmsTestData.Instruction();
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [
                new FillExecution(
                    ScaledQuantity.FromWhole(1),
                    new ScaledPrice(100, 0),
                    ScaledMoney.Zero,
                    LiquidityFlag.Taker),
            ]);
        var (service, _, _) = CreateService(OmsTestData.RiskEngine(), [plan]);
        Arm(service, instruction);
        var released = service.Release(instruction.Identity.ClientOrderId, Context("release"));
        Assert.Equal(OrderLifecycleState.PartiallyFilled, released.Projection!.State);
        var replacement = instruction.Terms with
        {
            OrderType = CanonicalOrderType.Limit,
            LimitPrice = new ScaledPrice(9_900, 2),
        };

        var replaced = service.Replace(
            instruction.Identity.ClientOrderId,
            replacement,
            OmsTestData.RiskSnapshot(target: 2, current: 1),
            Context("replace-partial"));

        Assert.True(replaced.IsSuccess);
        Assert.Equal(OrderLifecycleState.PartiallyFilled, replaced.Projection!.State);
        Assert.Equal(ScaledQuantity.FromWhole(1), replaced.Projection.FilledQuantity);
        Assert.Equal(replacement, replaced.Projection.Terms);
    }

    [Fact]
    public void AcceptedReplacementRiskEvent_CanResumeAfterCrashWithoutReevaluation()
    {
        var riskEngine = OmsTestData.RiskEngine();
        var instruction = OmsTestData.Instruction();
        var (service, store, _) = CreateService(riskEngine);
        Arm(service, instruction);
        Assert.True(service.Release(instruction.Identity.ClientOrderId, Context("release")).IsSuccess);
        var replacement = new CanonicalOrderTerms(
            OrderSide.Buy,
            CanonicalOrderType.Limit,
            CanonicalTimeInForce.Day,
            ScaledQuantity.FromWhole(2),
            new ScaledPrice(99, 0),
            null);
        var riskInput = OmsTestData.RiskSnapshot(referencePrice: 99);
        var context = Context("replace-crash-window");
        var decision = riskEngine.Evaluate(riskInput);
        Assert.True(decision.IsAccepted);
        Assert.True(store.Append(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.ReplaceRiskAccepted,
                OrderLifecycleState.Working,
                OrderEventSource.Risk,
                context.DeduplicationKey.Derive("replace-risk-decision"),
                OmsTestData.TimestampUtc,
                context.CausationId,
                RiskDecision: decision,
                ReplacementTerms: replacement),
            OmsTestData.TimestampUtc).IsSuccess);
        var decisionCountBeforeResume = riskEngine.Decisions.Count;

        var resumed = service.Replace(
            instruction.Identity.ClientOrderId,
            replacement,
            riskInput,
            context);

        Assert.True(resumed.IsSuccess);
        Assert.Equal(replacement, resumed.Projection!.Terms);
        Assert.Equal(decisionCountBeforeResume, riskEngine.Decisions.Count);
        Assert.Contains(
            service.ReadEvents(instruction.Identity.ClientOrderId),
            item => item.Kind == OrderEventKind.ReplaceRequested &&
                    item.CausationId == context.CausationId);
    }

    private static (OrderManagementService Service, InMemoryOrderEventStore Store, DeterministicSimulatedVenue Venue)
        CreateService(
            RiskEngine riskEngine,
            IEnumerable<VenueSubmitPlan>? plans = null,
            VenueCapabilities? capabilities = null)
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        var store = new InMemoryOrderEventStore();
        var venue = new DeterministicSimulatedVenue(
            clock,
            capabilities ?? VenueCapabilities.All,
            plans);
        return (new OrderManagementService(store, riskEngine, venue, clock), store, venue);
    }

    private static void Arm(OrderManagementService service, CanonicalOrderInstruction instruction)
    {
        Assert.True(service.CreateDraft(instruction, Context("draft")).IsSuccess);
        Assert.True(service.Validate(
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context("validate")).IsSuccess);
        Assert.True(service.Prepare(instruction.Identity.ClientOrderId, Context("prepare")).IsSuccess);
        Assert.True(service.Arm(instruction.Identity.ClientOrderId, Context("arm")).IsSuccess);
    }

    private static OrderCommandContext Context(string suffix) =>
        new(OmsTestData.Causation(suffix), OmsTestData.Dedup(suffix));
}
