using TradingTerminal.Backtest.Engine;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

[Collection(SqliteOrderEventStoreCollection.Name)]
public sealed class ReconciliationEngineTests
{
    [Fact]
    public void RealAccountCashBasis_MatchesOpeningTotal_WithoutTreatingBuyingPowerAsLedgerCash()
    {
        using var harness = Harness.Create();
        var caseStore = new InMemoryReconciliationCaseStore();
        var openingCash = new ScaledMoney(100_000, 2);
        var openingBuyingPower = new ScaledMoney(200_000, 2);
        var engine = new ReconciliationEngine(
            harness.Service,
            caseStore,
            harness.Clock,
            new ReconciliationCashBasis(
                "USD",
                openingCash,
                openingBuyingPower,
                CompareAvailable: false));
        var snapshot = new BrokerReconciliationSnapshot(
            harness.Adapter.Account,
            harness.Clock.UtcNow,
            [],
            [],
            [],
            [new BrokerCashSnapshot(
                "USD",
                openingCash,
                new ScaledMoney(175_000, 2),
                harness.Clock.UtcNow)]);

        var result = engine.RunCycle(
            ReconciliationTrigger.Startup,
            harness.Adapter.Account,
            [],
            snapshot);

        Assert.True(result.IsSuccess, result.Reason);
        var cashCase = Assert.Single(result.Cases, item =>
            item.SubjectKind == ReconciliationSubjectKind.Cash);
        Assert.Equal(ReconciliationCaseKind.Matched, cashCase.Kind);
        Assert.Equal(ReconciliationCaseStatus.Resolved, cashCase.Status);
        Assert.False(result.IsAdmissionBlocked);
    }

    [Fact]
    public async Task StartupCycle_CleanSnapshot_ProducesOnlyMatchedCases()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-clean");
        await MakeWorkingAsync(harness, instruction);

        var result = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Startup);

        Assert.True(result.IsSuccess, result.Reason);
        Assert.Equal(ReconciliationTrigger.Startup, result.Trigger);
        Assert.NotEmpty(result.Cases);
        Assert.All(result.Cases, reconciliationCase =>
        {
            Assert.Equal(ReconciliationCaseKind.Matched, reconciliationCase.Kind);
            Assert.Equal(ReconciliationCaseStatus.Resolved, reconciliationCase.Status);
            Assert.True(reconciliationCase.IsValid);
        });
        Assert.Equal(0, result.UnresolvedMaterialCaseCount);
        Assert.False(result.IsAdmissionBlocked);
        Assert.True(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Fact]
    public async Task ReconnectCycle_InjectedBrokerOnlyOrder_ClassifiesLocallyMissingWithEvidence()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-locally-missing");
        var brokerOrder = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            OrderLifecycleState.Working,
            new BrokerOrderId("broker-locally-missing"),
            new ExchangeOrderId("exchange-locally-missing"),
            ScaledQuantity.Zero);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness, openOrders: [brokerOrder]));

        var result = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Reconnect);

        Assert.True(result.IsSuccess, result.Reason);
        var reconciliationCase = Assert.Single(result.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.LocallyMissing);
        Assert.Equal("v1:absent", reconciliationCase.LocalEvidence);
        Assert.Contains("\"clientOrderId\":\"reconciliation-locally-missing\"", reconciliationCase.BrokerEvidence);
        Assert.Contains("\"quantity\":\"2e-0\"", reconciliationCase.BrokerEvidence);
        Assert.Equal(ReconciliationCaseStatus.Open, reconciliationCase.Status);
        Assert.True(reconciliationCase.IsValid);
    }

    [Fact]
    public async Task PeriodicCycle_InjectedMissingBrokerOrder_ClassifiesBrokerMissingWithEvidence()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-broker-missing");
        await MakeWorkingAsync(harness, instruction);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness));

        var result = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);

        Assert.True(result.IsSuccess, result.Reason);
        var reconciliationCase = Assert.Single(result.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.BrokerMissing);
        Assert.Contains("\"clientOrderId\":\"reconciliation-broker-missing\"", reconciliationCase.LocalEvidence);
        Assert.Contains("\"state\":\"Working\"", reconciliationCase.LocalEvidence);
        Assert.Equal("v1:absent", reconciliationCase.BrokerEvidence);
        Assert.Equal(ReconciliationCaseStatus.Open, reconciliationCase.Status);
        Assert.True(reconciliationCase.IsValid);
    }

    [Fact]
    public async Task OperatorCycle_InjectedQuantityDifference_ClassifiesExactMismatchWithEvidence()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-quantity-mismatch");
        await MakeWorkingAsync(harness, instruction);
        var actual = harness.Adapter.CaptureReconciliationSnapshot();
        var actualOrder = Assert.Single(actual.OpenOrders);
        var divergentOrder = actualOrder with
        {
            CurrentTerms = actualOrder.CurrentTerms with
            {
                Quantity = ScaledQuantity.FromWhole(3),
            },
        };
        harness.Adapter.InjectReconciliationSnapshot(actual with { OpenOrders = [divergentOrder] });

        var result = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);

        Assert.True(result.IsSuccess, result.Reason);
        var reconciliationCase = Assert.Single(result.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.QuantityMismatch);
        Assert.Contains("\"quantity\":\"2e-0\"", reconciliationCase.LocalEvidence);
        Assert.Contains("\"quantity\":\"3e-0\"", reconciliationCase.BrokerEvidence);
        Assert.NotEqual(reconciliationCase.LocalEvidence, reconciliationCase.BrokerEvidence);
        Assert.True(reconciliationCase.IsValid);
    }

    [Fact]
    public async Task UnresolvedMaterialCase_BlocksAdmissionAndReducingReplace_ButAllowsCancel()
    {
        using var harness = Harness.Create();
        var working = OmsTestData.Instruction("reconciliation-gate-working");
        var armed = OmsTestData.Instruction("reconciliation-gate-armed");
        var prepared = OmsTestData.Instruction("reconciliation-gate-prepared");
        var draft = OmsTestData.Instruction("reconciliation-gate-draft");
        await MakeWorkingAsync(harness, working);
        DraftValidatePrepareAndArm(harness, armed);
        DraftValidateAndPrepare(harness, prepared);
        Assert.True(harness.Service.CreateDraft(draft, Context(draft, "draft")).IsSuccess);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);

        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.True(cycle.IsAdmissionBlocked);
        Assert.True(cycle.UnresolvedMaterialCaseCount > 0);
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));

        var validation = harness.Coordinator.Validate(
            harness.Adapter.Account,
            draft.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(draft, "blocked-validate"));
        var arming = harness.Coordinator.Arm(
            harness.Adapter.Account,
            prepared.Identity.ClientOrderId,
            Context(prepared, "blocked-arm"));
        var release = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            armed.Identity.ClientOrderId,
            Context(armed, "blocked-release"));
        var reducingTerms = working.Terms with { Quantity = ScaledQuantity.FromWhole(1) };
        var reducingReplace = await harness.Coordinator.ReplaceAsync(
            harness.Adapter.Account,
            working.Identity.ClientOrderId,
            reducingTerms,
            OmsTestData.RiskSnapshot(target: 1),
            Context(working, "blocked-reducing-replace"));

        Assert.Equal(OmsCommandFault.ReconciliationRequired, validation.Fault);
        Assert.Equal(OmsCommandFault.ReconciliationRequired, arming.Fault);
        Assert.Equal(OmsCommandFault.ReconciliationRequired, release.OmsResult.Fault);
        Assert.Equal(OmsCommandFault.ReconciliationRequired, reducingReplace.OmsResult.Fault);
        Assert.Equal(0, harness.Scheduler.PendingCount);

        var cancel = await harness.Coordinator.CancelAsync(
            harness.Adapter.Account,
            working.Identity.ClientOrderId,
            Context(working, "allowed-cancel"));

        Assert.True(cancel.IsSuccess, cancel.Reason);
        Assert.Equal(OrderLifecycleState.PendingCancel, cancel.OmsResult.Projection!.State);
        Assert.Equal(1, harness.Scheduler.PendingCount);
    }

    [Fact]
    public async Task UnknownOutcomeCycle_UsesInjectedTerminalSnapshotBeforeLeavingUnknown()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-unknown");
        DraftValidatePrepareAndArm(harness, instruction);
        var releaseContext = Context(instruction, "local-release");
        Assert.True(harness.Service.BeginRelease(
            instruction.Identity.ClientOrderId,
            releaseContext).IsSuccess);
        var unknown = harness.Service.RecoverUnacknowledgedSendAsUnknown(
            instruction.Identity.ClientOrderId,
            Context(instruction, "unknown"));
        Assert.Equal(OrderLifecycleState.Unknown, unknown.Projection!.State);
        Assert.True(unknown.Projection.BlocksRetry);
        Assert.Equal(0, harness.Scheduler.PendingCount);
        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(1));

        var terminalOrder = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            OrderLifecycleState.Cancelled,
            new BrokerOrderId("broker-reconciled-cancelled"),
            new ExchangeOrderId("exchange-reconciled-cancelled"),
            ScaledQuantity.Zero);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(
            harness,
            completedOrders: [terminalOrder]));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.UnknownOutcome);
        var reconciled = harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!;

        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.Equal(OrderLifecycleState.Reconciled, reconciled.State);
        Assert.False(reconciled.BlocksRetry);
        var resolvedCase = Assert.Single(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.TerminalStateMismatch);
        Assert.Equal(ReconciliationCaseStatus.Resolved, resolvedCase.Status);
        Assert.Contains("Cancelled", resolvedCase.ResolutionEvidence);
        var events = harness.Store.Read(instruction.Identity.ClientOrderId).ToList();
        var unknownIndex = events.FindIndex(item => item.Kind == OrderEventKind.OutcomeUnknown);
        var startedIndex = events.FindIndex(item => item.Kind == OrderEventKind.ReconciliationStarted);
        var reconciledIndex = events.FindIndex(item => item.Kind == OrderEventKind.Reconciled);
        Assert.True(unknownIndex >= 0 && unknownIndex < startedIndex);
        Assert.True(startedIndex < reconciledIndex);
        Assert.Equal(0, harness.Scheduler.PendingCount);
    }

    [Fact]
    public async Task OperatorResolution_AppendsIdentityAndEvidence_WithoutRewritingOriginalObservation()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-operator-resolution");
        await MakeWorkingAsync(harness, instruction);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness));
        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);
        var opened = Assert.Single(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.BrokerMissing);
        var originalObservation = opened with { };
        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(1));

        var resolved = harness.Engine.ResolveCase(
            opened.CaseId,
            " operator-17 ",
            "Reviewed the simulation injection and accepted the local ledger as authoritative.");
        var facts = harness.CaseStore.Read(opened.CaseId);

        Assert.True(resolved);
        Assert.Equal(2, facts.Count);
        Assert.Equal(originalObservation, facts[0]);
        Assert.Equal(ReconciliationCaseStatus.Open, facts[0].Status);
        Assert.Null(facts[0].ResolvedBy);
        Assert.Null(facts[0].ResolutionEvidence);
        Assert.Equal(ReconciliationCaseStatus.Resolved, facts[1].Status);
        Assert.Equal("operator-17", facts[1].ResolvedBy);
        Assert.Equal(
            "Reviewed the simulation injection and accepted the local ledger as authoritative.",
            facts[1].ResolutionEvidence);
        Assert.Equal(facts[0].LocalEvidence, facts[1].LocalEvidence);
        Assert.Equal(facts[0].BrokerEvidence, facts[1].BrokerEvidence);
        Assert.Equal(facts[0].OpenedAtUtc, facts[1].OpenedAtUtc);
        Assert.True(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Fact]
    public async Task PeriodicCycle_PredispatchOrderIsNotClassifiedBrokerMissing()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-predispatch");
        DraftValidatePrepareAndArm(harness, instruction);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);

        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.DoesNotContain(cycle.Cases, item => item.ClientOrderId == instruction.Identity.ClientOrderId);
        Assert.True(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Fact]
    public async Task UnknownOutcomeCycle_StaleSnapshotCannotMoveOrderOutOfUnknown()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-unknown-stale");
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True(harness.Service.BeginRelease(
            instruction.Identity.ClientOrderId,
            Context(instruction, "release")).IsSuccess);
        Assert.Equal(OrderLifecycleState.Unknown, harness.Service.RecoverUnacknowledgedSendAsUnknown(
            instruction.Identity.ClientOrderId,
            Context(instruction, "unknown")).Projection!.State);
        var terminal = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            OrderLifecycleState.Cancelled,
            null,
            null,
            ScaledQuantity.Zero);
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness, completedOrders: [terminal]));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.UnknownOutcome);

        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.Equal(
            OrderLifecycleState.Unknown,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);
        Assert.Contains(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.TerminalStateMismatch &&
            item.Status == ReconciliationCaseStatus.Open);
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Theory]
    [InlineData(OrderLifecycleState.Filled, 0, true)]
    [InlineData(OrderLifecycleState.PartiallyFilled, 0, false)]
    [InlineData(OrderLifecycleState.Working, 2, false)]
    public async Task UnknownOutcomeCycle_IncoherentSnapshotIsManualExceptionAndCannotResolveUnknown(
        OrderLifecycleState brokerState,
        long filledUnits,
        bool completed)
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction($"reconciliation-incoherent-{brokerState}");
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True(harness.Service.BeginRelease(
            instruction.Identity.ClientOrderId,
            Context(instruction, "release")).IsSuccess);
        Assert.Equal(OrderLifecycleState.Unknown, harness.Service.RecoverUnacknowledgedSendAsUnknown(
            instruction.Identity.ClientOrderId,
            Context(instruction, "unknown")).Projection!.State);
        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(1));
        var malformed = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            brokerState,
            null,
            null,
            ScaledQuantity.FromWhole(filledUnits));
        harness.Adapter.InjectReconciliationSnapshot(completed
            ? Snapshot(harness, completedOrders: [malformed])
            : Snapshot(harness, openOrders: [malformed]));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.UnknownOutcome);

        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.Equal(
            OrderLifecycleState.Unknown,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);
        Assert.Contains(cycle.Cases, item =>
            item.SubjectKind == ReconciliationSubjectKind.Account &&
            item.Kind == ReconciliationCaseKind.ManualException &&
            item.Status == ReconciliationCaseStatus.Open);
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOutcomeCycle_IndependentTermsOrCollectionDifferenceCannotResolveUnknown(
        bool wrongCollection)
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction(
            wrongCollection ? "reconciliation-unknown-collection" : "reconciliation-unknown-terms");
        DraftValidatePrepareAndArm(harness, instruction);
        Assert.True(harness.Service.BeginRelease(
            instruction.Identity.ClientOrderId,
            Context(instruction, "release")).IsSuccess);
        Assert.Equal(OrderLifecycleState.Unknown, harness.Service.RecoverUnacknowledgedSendAsUnknown(
            instruction.Identity.ClientOrderId,
            Context(instruction, "unknown")).Projection!.State);
        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(1));
        var working = new VenueOrderSnapshot(
            instruction,
            wrongCollection
                ? instruction.Terms
                : instruction.Terms with
                {
                    Side = instruction.Terms.Side == OrderSide.Buy ? OrderSide.Sell : OrderSide.Buy,
                },
            OrderLifecycleState.Working,
            null,
            null,
            ScaledQuantity.Zero);
        harness.Adapter.InjectReconciliationSnapshot(wrongCollection
            ? Snapshot(harness, completedOrders: [working])
            : Snapshot(harness, openOrders: [working]));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.UnknownOutcome);

        Assert.True(cycle.IsSuccess, cycle.Reason);
        Assert.Equal(
            OrderLifecycleState.Unknown,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);
        Assert.Contains(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.IsMaterial &&
            item.Status == ReconciliationCaseStatus.Open);
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Fact]
    public async Task Cycle_ClassifiesWrongSnapshotCollectionAsTerminalStateMismatch()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-wrong-collection");
        await MakeWorkingAsync(harness, instruction);
        var actual = harness.Adapter.CaptureReconciliationSnapshot();
        var working = Assert.Single(actual.OpenOrders);
        harness.Adapter.InjectReconciliationSnapshot(actual with
        {
            OpenOrders = Array.Empty<VenueOrderSnapshot>(),
            CompletedOrders = [working],
        });

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);

        var mismatch = Assert.Single(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.TerminalStateMismatch);
        Assert.Contains("collection=completed", mismatch.BrokerEvidence, StringComparison.Ordinal);
        Assert.DoesNotContain(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.Matched);
    }

    [Fact]
    public async Task Cycle_ClassifiesEconomicOrExternalIdentityDifferenceAsManualException()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-identity-mismatch");
        await MakeWorkingAsync(harness, instruction);
        var actual = harness.Adapter.CaptureReconciliationSnapshot();
        var working = Assert.Single(actual.OpenOrders);
        var divergent = working with
        {
            CurrentTerms = working.CurrentTerms with
            {
                TimeInForce = CanonicalTimeInForce.GoodTillCancelled,
            },
            BrokerOrderId = new BrokerOrderId("different-broker-id"),
        };
        harness.Adapter.InjectReconciliationSnapshot(actual with { OpenOrders = [divergent] });

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);

        var exception = Assert.Single(cycle.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.ManualException);
        Assert.Contains("GoodTillCancelled", exception.BrokerEvidence, StringComparison.Ordinal);
        Assert.Contains("different-broker-id", exception.BrokerEvidence, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cycle_ClassifiesDuplicateExchangeIdentityAcrossLocalOrBrokerOrders(
        bool duplicateInBrokerSnapshot)
    {
        using var harness = Harness.Create();
        var firstInstruction = OmsTestData.Instruction("reconciliation-duplicate-exchange-a");
        var secondInstruction = OmsTestData.Instruction("reconciliation-duplicate-exchange-b");
        await MakeWorkingAsync(harness, firstInstruction);
        await MakeWorkingAsync(harness, secondInstruction);
        var actual = harness.Adapter.CaptureReconciliationSnapshot();
        var sharedExchangeId = new ExchangeOrderId("shared-exchange-id");

        ReconciliationCycleResult cycle;
        if (duplicateInBrokerSnapshot)
        {
            harness.Adapter.InjectReconciliationSnapshot(actual with
            {
                OpenOrders = actual.OpenOrders
                    .Select(item => item with { ExchangeOrderId = sharedExchangeId })
                    .ToArray(),
            });
            cycle = await harness.Coordinator.RunReconciliationAsync(
                harness.Adapter.Account,
                ReconciliationTrigger.OperatorRequest);
        }
        else
        {
            var local = new[]
            {
                harness.Service.GetProjection(firstInstruction.Identity.ClientOrderId).Projection! with
                {
                    ExchangeOrderId = sharedExchangeId,
                },
                harness.Service.GetProjection(secondInstruction.Identity.ClientOrderId).Projection! with
                {
                    ExchangeOrderId = sharedExchangeId,
                },
            };
            cycle = harness.Engine.RunCycle(
                ReconciliationTrigger.OperatorRequest,
                harness.Adapter.Account,
                local,
                actual);
        }

        var duplicates = cycle.Cases
            .Where(item => item.Kind == ReconciliationCaseKind.DuplicateCandidate)
            .ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, item =>
            Assert.Contains("exchange-order-id", $"{item.LocalEvidence}{item.BrokerEvidence}", StringComparison.Ordinal));
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Fact]
    public async Task ChangedEvidenceForSameClassification_AppendsANewOpeningObservation()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-evidence-change");
        await MakeWorkingAsync(harness, instruction);
        var actual = harness.Adapter.CaptureReconciliationSnapshot();
        var working = Assert.Single(actual.OpenOrders);
        var firstSnapshot = actual with
        {
            OpenOrders = [working with
            {
                CurrentTerms = working.CurrentTerms with { Quantity = ScaledQuantity.FromWhole(3) },
            }],
        };
        harness.Adapter.InjectReconciliationSnapshot(firstSnapshot);
        var first = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);
        var firstCase = Assert.Single(first.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.QuantityMismatch);

        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(1));
        harness.Adapter.InjectReconciliationSnapshot(firstSnapshot with
        {
            CapturedAtUtc = harness.Clock.UtcNow,
            OpenOrders = [working with
            {
                CurrentTerms = working.CurrentTerms with { Quantity = ScaledQuantity.FromWhole(4) },
            }],
            Positions = firstSnapshot.Positions
                .Select(item => item with { ObservedAtUtc = harness.Clock.UtcNow })
                .ToArray(),
            Cash = firstSnapshot.Cash
                .Select(item => item with { ObservedAtUtc = harness.Clock.UtcNow })
                .ToArray(),
        });
        var second = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);
        var secondCase = Assert.Single(second.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.QuantityMismatch);

        Assert.NotEqual(firstCase.CaseId, secondCase.CaseId);
        var openQuantityCases = harness.CaseStore.Read(harness.Adapter.Account)
            .GroupBy(item => item.CaseId)
            .Select(item => item.Last())
            .Where(item => item.ClientOrderId == instruction.Identity.ClientOrderId &&
                           item.Kind == ReconciliationCaseKind.QuantityMismatch &&
                           item.Status == ReconciliationCaseStatus.Open)
            .ToArray();
        Assert.Equal(2, openQuantityCases.Length);
    }

    [Fact]
    public void CaseStore_RejectsOrphanInvestigationAndResolutionFacts()
    {
        var store = new InMemoryReconciliationCaseStore();
        var account = new BrokerExecutionAccount(
            new ExecutionAdapterId("simulated"),
            new BrokerAccountId("orphan-case-account"));
        var opening = new ReconciliationCase(
            new ReconciliationCaseId("orphan-case"),
            account,
            ReconciliationSubjectKind.Cash,
            "SIM",
            null,
            ReconciliationCaseKind.ManualException,
            ReconciliationCaseStatus.Open,
            "v1:local",
            "v1:broker",
            OmsTestData.TimestampUtc,
            null,
            null,
            null);
        var investigating = opening with { Status = ReconciliationCaseStatus.Investigating };
        var resolved = opening with
        {
            Status = ReconciliationCaseStatus.Resolved,
            ResolvedAtUtc = OmsTestData.TimestampUtc.AddMinutes(1),
            ResolvedBy = "operator-1",
            ResolutionEvidence = "reviewed",
        };

        Assert.True(investigating.IsValid);
        Assert.True(resolved.IsValid);
        Assert.False(store.TryAppend(investigating));
        Assert.False(store.TryAppend(resolved));
        Assert.Empty(store.Read(opening.CaseId));
    }

    [Fact]
    public async Task StaleSnapshot_CannotResolveCasesOrReopenAdmission()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-stale-cycle");
        await MakeWorkingAsync(harness, instruction);
        var staleMatchingSnapshot = harness.Adapter.CaptureReconciliationSnapshot();
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(harness));
        var divergent = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);
        var missing = Assert.Single(divergent.Cases, item =>
            item.ClientOrderId == instruction.Identity.ClientOrderId &&
            item.Kind == ReconciliationCaseKind.BrokerMissing);

        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(1));
        harness.Adapter.InjectReconciliationSnapshot(staleMatchingSnapshot);
        var stale = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);

        Assert.Contains(stale.Cases, item =>
            item.SubjectKind == ReconciliationSubjectKind.Account &&
            item.Kind == ReconciliationCaseKind.ManualException);
        Assert.Equal(ReconciliationCaseStatus.Open, harness.CaseStore.Read(missing.CaseId)[^1].Status);
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));

        harness.Clock.SetTo(OmsTestData.TimestampUtc.AddMinutes(2));
        harness.Adapter.ClearReconciliationSnapshotInjection();
        var fresh = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.Periodic);

        Assert.True(fresh.IsSuccess, fresh.Reason);
        Assert.Equal(ReconciliationCaseStatus.Resolved, harness.CaseStore.Read(missing.CaseId)[^1].Status);
        Assert.True(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    [Fact]
    public void RestartedEngine_ReusesClockWithoutCaseIdentityCollision()
    {
        using var harness = Harness.Create();
        var instruction = OmsTestData.Instruction("reconciliation-case-id-restart");
        var brokerOrder = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            OrderLifecycleState.Working,
            null,
            null,
            ScaledQuantity.Zero);
        var snapshot = Snapshot(harness, openOrders: [brokerOrder]);
        var first = harness.Engine.RunCycle(
            ReconciliationTrigger.OperatorRequest,
            harness.Adapter.Account,
            Array.Empty<OrderProjection>(),
            snapshot);
        var firstCase = Assert.Single(first.Cases, item => item.Kind == ReconciliationCaseKind.LocallyMissing);
        Assert.True(harness.Engine.ResolveCase(firstCase.CaseId, "operator-1", "resolved for replay test"));
        var restarted = new ReconciliationEngine(harness.Service, harness.CaseStore, harness.Clock);

        var replay = restarted.RunCycle(
            ReconciliationTrigger.OperatorRequest,
            harness.Adapter.Account,
            Array.Empty<OrderProjection>(),
            snapshot);
        var replayCase = Assert.Single(replay.Cases, item => item.Kind == ReconciliationCaseKind.LocallyMissing);

        Assert.True(replay.IsSuccess, replay.Reason);
        Assert.NotEqual(firstCase.CaseId, replayCase.CaseId);
        Assert.Equal(ReconciliationCaseStatus.Open, replayCase.Status);
    }

    [Fact]
    public async Task MalformedNestedSnapshot_AppendsDurableManualExceptionWithoutThrowing()
    {
        using var harness = Harness.Create();
        harness.Adapter.InjectReconciliationSnapshot(Snapshot(
            harness,
            openOrders: new VenueOrderSnapshot[] { null! }));

        var cycle = await harness.Coordinator.RunReconciliationAsync(
            harness.Adapter.Account,
            ReconciliationTrigger.OperatorRequest);

        Assert.True(cycle.IsSuccess, cycle.Reason);
        var manual = Assert.Single(cycle.Cases, item =>
            item.SubjectKind == ReconciliationSubjectKind.Account &&
            item.Kind == ReconciliationCaseKind.ManualException);
        Assert.Equal(ReconciliationCaseStatus.Open, manual.Status);
        Assert.Contains(manual, harness.CaseStore.Read(harness.Adapter.Account));
        Assert.False(harness.Engine.CanAdmitNewOrders(harness.Adapter.Account));
    }

    private static async Task MakeWorkingAsync(
        Harness harness,
        CanonicalOrderInstruction instruction)
    {
        DraftValidatePrepareAndArm(harness, instruction);
        var released = await harness.Coordinator.ReleaseAsync(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "release"));
        Assert.True(released.IsSuccess, released.Reason);
        Assert.Equal(1, harness.Scheduler.RunAll());
        Assert.Equal(
            OrderLifecycleState.Working,
            harness.Service.GetProjection(instruction.Identity.ClientOrderId).Projection!.State);
    }

    private static void DraftValidatePrepareAndArm(
        Harness harness,
        CanonicalOrderInstruction instruction)
    {
        DraftValidateAndPrepare(harness, instruction);
        Assert.True(harness.Coordinator.Arm(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            Context(instruction, "arm")).IsSuccess);
    }

    private static void DraftValidateAndPrepare(
        Harness harness,
        CanonicalOrderInstruction instruction)
    {
        Assert.True(harness.Service.CreateDraft(
            instruction,
            Context(instruction, "draft")).IsSuccess);
        Assert.True(harness.Coordinator.Validate(
            harness.Adapter.Account,
            instruction.Identity.ClientOrderId,
            OmsTestData.RiskSnapshot(),
            Context(instruction, "validate")).IsSuccess);
        Assert.True(harness.Service.Prepare(
            instruction.Identity.ClientOrderId,
            Context(instruction, "prepare")).IsSuccess);
    }

    private static BrokerReconciliationSnapshot Snapshot(
        Harness harness,
        IReadOnlyList<VenueOrderSnapshot>? openOrders = null,
        IReadOnlyList<VenueOrderSnapshot>? completedOrders = null) =>
        new(
            harness.Adapter.Account,
            harness.Clock.UtcNow,
            openOrders ?? Array.Empty<VenueOrderSnapshot>(),
            completedOrders ?? Array.Empty<VenueOrderSnapshot>(),
            Array.Empty<BrokerPositionSnapshot>(),
            [new BrokerCashSnapshot(
                "SIM",
                ScaledMoney.Zero,
                ScaledMoney.Zero,
                harness.Clock.UtcNow)]);

    private static OrderCommandContext Context(
        CanonicalOrderInstruction instruction,
        string suffix) =>
        new(
            OmsTestData.Causation($"{instruction.Identity.ClientOrderId.Value}-{suffix}"),
            OmsTestData.Dedup($"{instruction.Identity.ClientOrderId.Value}-{suffix}"));

    private sealed class Harness : IDisposable
    {
        private Harness(
            SimClock clock,
            InMemoryOrderEventStore store,
            InMemoryReconciliationCaseStore caseStore,
            OrderManagementService service,
            SimulatedExecutionAdapter adapter,
            ControllableAdapterEventScheduler scheduler,
            ReconciliationEngine engine,
            ExecutionCoordinator coordinator)
        {
            Clock = clock;
            Store = store;
            CaseStore = caseStore;
            Service = service;
            Adapter = adapter;
            Scheduler = scheduler;
            Engine = engine;
            Coordinator = coordinator;
        }

        internal SimClock Clock { get; }

        internal InMemoryOrderEventStore Store { get; }

        internal InMemoryReconciliationCaseStore CaseStore { get; }

        internal OrderManagementService Service { get; }

        internal SimulatedExecutionAdapter Adapter { get; }

        internal ControllableAdapterEventScheduler Scheduler { get; }

        internal ReconciliationEngine Engine { get; }

        internal ExecutionCoordinator Coordinator { get; }

        internal static Harness Create()
        {
            var clock = new SimClock();
            clock.SetTo(OmsTestData.TimestampUtc);
            var venue = new DeterministicSimulatedVenue(clock);
            var scheduler = new ControllableAdapterEventScheduler();
            var adapter = new SimulatedExecutionAdapter(venue, clock, scheduler);
            var store = new InMemoryOrderEventStore();
            var service = new OrderManagementService(
                store,
                OmsTestData.RiskEngine(),
                venue,
                clock);
            var caseStore = new InMemoryReconciliationCaseStore();
            var engine = new ReconciliationEngine(service, caseStore, clock);
            var coordinator = new ExecutionCoordinator(service, adapter, engine);
            return new Harness(
                clock,
                store,
                caseStore,
                service,
                adapter,
                scheduler,
                engine,
                coordinator);
        }

        public void Dispose() => Coordinator.Dispose();
    }
}
