using System.Text;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Execution.Oms;

/// <summary>Explicit command causation and inbox identity for one OMS operation.</summary>
public readonly record struct OrderCommandContext(
    CausationId CausationId,
    DeduplicationKey DeduplicationKey)
{
    /// <summary>Gets whether both command identities are populated and bounded.</summary>
    public bool IsValid => CausationId.IsValid && DeduplicationKey.IsValid;
}

/// <summary>Fault-as-value result categories from the simulation-only OMS coordinator.</summary>
public enum OmsCommandFault : byte
{
    /// <summary>The command committed successfully.</summary>
    None = 0,

    /// <summary>The command context or aggregate identity is invalid.</summary>
    InvalidCommand = 1,

    /// <summary>The canonical economic instruction is structurally invalid.</summary>
    InvalidInstruction = 2,

    /// <summary>No event aggregate exists for the requested client-order id.</summary>
    OrderNotFound = 3,

    /// <summary>The requested lifecycle edge is not legal from current state.</summary>
    IllegalTransition = 4,

    /// <summary>The simulator cannot represent the order faithfully.</summary>
    UnsupportedCapability = 5,

    /// <summary>The risk snapshot does not describe the same exact economic instruction.</summary>
    RiskSnapshotMismatch = 6,

    /// <summary>The existing versioned risk engine rejected the complete order without clamping.</summary>
    RiskRejected = 7,

    /// <summary>The transactional event-store append was rejected.</summary>
    PersistenceRejected = 8,

    /// <summary>A source/deduplication key was reused with different content.</summary>
    DuplicateConflict = 9,

    /// <summary>Unknown or reconciling state forbids blind submission retry.</summary>
    RetryBlockedUnknown = 10,

    /// <summary>The simulator proved that submission failed before acceptance; same-id retry is safe.</summary>
    VenueFailedBeforeAcceptance = 11,

    /// <summary>The simulator could not prove submission outcome; reconciliation is required.</summary>
    VenueOutcomeUnknown = 12,

    /// <summary>The simulator observably rejected the order or command.</summary>
    VenueRejected = 13,

    /// <summary>The simulator detected conflicting reuse of a client-order id.</summary>
    VenueIdempotencyConflict = 14,

    /// <summary>A simulated callback was invalid for the aggregate or exact economics.</summary>
    InvalidVenueEvent = 15,

    /// <summary>Explicit reconciliation evidence is invalid or incomplete.</summary>
    InvalidReconciliation = 16,

    /// <summary>Changed replacement terms require a fresh versioned risk decision.</summary>
    ReplaceRequiresNewValidation = 17,

    /// <summary>The adapter is data-only, unauthenticated, uncertified, or otherwise unavailable.</summary>
    ExecutionUnavailable = 18,

    /// <summary>A local adapter dispatch receipt was invalid or could not be persisted.</summary>
    DispatchReceiptRejected = 19,

    /// <summary>Durable startup recovery must be resolved before new-order admission resumes.</summary>
    RecoveryRequired = 20,

    /// <summary>An unresolved material account discrepancy blocks new submit/replace admission.</summary>
    ReconciliationRequired = 21,

    /// <summary>The account writer lease is absent, lost, or carries a stale fencing token.</summary>
    LeaseRejected = 22,
}

/// <summary>Immutable result from one OMS command.</summary>
public readonly record struct OmsCommandResult(
    OmsCommandFault Fault,
    OrderProjection? Projection,
    RiskDecisionRecord? RiskDecision = null,
    string? Reason = null)
{
    /// <summary>Gets whether the command completed without a refusal or fault.</summary>
    public bool IsSuccess => Fault == OmsCommandFault.None && Projection is not null;

    /// <summary>Gets whether a proved pre-acceptance failure permits retry with the same client id.</summary>
    public bool CanRetrySameClientOrderId =>
        Fault == OmsCommandFault.VenueFailedBeforeAcceptance &&
        Projection?.State == OrderLifecycleState.Armed;
}

/// <summary>
/// In-process OMS coordinator for execution slice 1. It accepts only the sealed deterministic
/// simulator, so this assembly contains no injectable broker, socket, network, or live-order path.
/// State is reconstructed from <see cref="IOrderEventStore"/> after every command (ADR D7 and
/// roadmap sections 6, 11, and 13.4).
/// </summary>
public sealed class OrderManagementService
{
    private readonly IOrderEventStore _eventStore;
    private readonly RiskEngine _riskEngine;
    private readonly DeterministicSimulatedVenue _venue;
    private readonly IClock _clock;

    /// <summary>Creates one simulation-only, explicitly clocked OMS instance.</summary>
    public OrderManagementService(
        IOrderEventStore eventStore,
        RiskEngine riskEngine,
        DeterministicSimulatedVenue venue,
        IClock clock)
    {
        _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        _riskEngine = riskEngine ?? throw new ArgumentNullException(nameof(riskEngine));
        _venue = venue ?? throw new ArgumentNullException(nameof(venue));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>
    /// Gets whether the backing ledger's startup recovery gate permits risk validation and arming.
    /// In-memory stores have no restart recovery phase and are admitted by default.
    /// </summary>
    public bool CanAdmitNewOrders =>
        _eventStore is not IExecutionAdmissionGate gate || gate.CanAdmitNewOrders;

    /// <summary>
    /// Gets whether durable reconciliation facts allow a successful startup cycle to discharge the
    /// ledger's immutable restart-recovery gate.
    /// </summary>
    public bool CanAdmitAfterStartupReconciliation =>
        _eventStore is not IExecutionAdmissionGate gate || gate.CanAdmitAfterStartupReconciliation;

    /// <summary>Appends the immutable canonical instruction as a draft with no economic effect.</summary>
    public OmsCommandResult CreateDraft(
        CanonicalOrderInstruction instruction,
        in OrderCommandContext context)
    {
        if (!context.IsValid)
            return Failed(OmsCommandFault.InvalidCommand);
        if (instruction is null || instruction.Validate() != OrderDomainFault.None)
            return Failed(OmsCommandFault.InvalidInstruction);

        var draftKey = context.DeduplicationKey.Derive("draft-created");
        var priorDraft = FindEvent(
            instruction.Identity.ClientOrderId,
            OrderEventSource.Command,
            draftKey);
        if (priorDraft is not null)
        {
            if (priorDraft.Kind != OrderEventKind.DraftCreated ||
                priorDraft.Instruction != instruction ||
                priorDraft.CausationId != context.CausationId)
            {
                return Failed(OmsCommandFault.DuplicateConflict);
            }

            return GetProjection(instruction.Identity.ClientOrderId);
        }

        return Commit(
            new OrderEventDraft(
                instruction.Identity.ClientOrderId,
                OrderEventKind.DraftCreated,
                OrderLifecycleState.Draft,
                OrderEventSource.Command,
                draftKey,
                UtcNow(),
                context.CausationId,
                Instruction: instruction));
    }

    /// <summary>
    /// Validates capability and exact risk binding, then records the existing risk engine's full
    /// versioned decision. Rejection is observable and never clamps the instruction.
    /// </summary>
    public OmsCommandResult Validate(
        ClientOrderId clientOrderId,
        in RiskInputSnapshot riskInput,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (TryReplayValidation(clientOrderId, riskInput, context, out var replay))
            return replay;
        if (projection!.State != OrderLifecycleState.Draft)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        var capabilityFault = _venue.Capabilities.Validate(projection.Terms);
        if (capabilityFault != OrderDomainFault.None)
        {
            var committed = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    OrderEventKind.ValidationRejected,
                    OrderLifecycleState.Rejected,
                    OrderEventSource.Command,
                    context.DeduplicationKey.Derive("capability-rejected"),
                    UtcNow(),
                    context.CausationId,
                    Reason: capabilityFault.ToString()));
            if (!committed.IsSuccess)
                return committed;
            return committed with
            {
                Fault = capabilityFault is OrderDomainFault.UnsupportedOrderType or
                    OrderDomainFault.UnsupportedTimeInForce
                    ? OmsCommandFault.UnsupportedCapability
                    : OmsCommandFault.InvalidInstruction,
            };
        }

        if (!OrderRiskBinding.MatchesInstruction(riskInput, projection.Instruction))
        {
            var committed = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    OrderEventKind.ValidationRejected,
                    OrderLifecycleState.Rejected,
                    OrderEventSource.Command,
                    context.DeduplicationKey.Derive("risk-input-rejected"),
                    UtcNow(),
                    context.CausationId,
                    Reason: "Risk input does not describe the canonical order delta."));
            return committed.IsSuccess
                ? committed with { Fault = OmsCommandFault.RiskSnapshotMismatch }
                : committed;
        }

        var decision = _riskEngine.Evaluate(riskInput);
        var accepted = decision.IsAccepted;
        var result = Commit(
            new OrderEventDraft(
                clientOrderId,
                accepted ? OrderEventKind.RiskAccepted : OrderEventKind.RiskRejected,
                accepted ? OrderLifecycleState.Validated : OrderLifecycleState.Rejected,
                OrderEventSource.Risk,
                context.DeduplicationKey.Derive("risk-decision"),
                UtcNow(),
                context.CausationId,
                RiskDecision: decision,
                Reason: accepted ? null : decision.ReasonCodes.ToString()));
        if (!result.IsSuccess)
            return result;
        return result with
        {
            Fault = accepted ? result.Fault : OmsCommandFault.RiskRejected,
            RiskDecision = decision,
        };
    }

    /// <summary>
    /// Performs adapter session/capability negotiation before evaluating risk. A failure is persisted
    /// as ValidationRejected while the order is still Draft, so it can never be prepared or armed.
    /// </summary>
    public OmsCommandResult ValidateForExecution(
        ClientOrderId clientOrderId,
        in RiskInputSnapshot riskInput,
        BrokerExecutionSession session,
        BrokerExecutionCapabilities capabilities,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;

        var rejectedKey = context.DeduplicationKey.Derive("adapter-admission-rejected");
        var prior = FindEvent(clientOrderId, OrderEventSource.Command, rejectedKey);
        if (prior is not null)
        {
            if (prior.Kind != OrderEventKind.ValidationRejected ||
                prior.CausationId != context.CausationId)
            {
                return Failed(OmsCommandFault.DuplicateConflict, projection);
            }

            return new OmsCommandResult(
                IsExecutionUnavailableReason(prior.Reason)
                    ? OmsCommandFault.ExecutionUnavailable
                    : OmsCommandFault.UnsupportedCapability,
                projection,
                null,
                prior.Reason);
        }
        if (projection!.State != OrderLifecycleState.Draft)
            return Validate(clientOrderId, riskInput, context);

        var admission = BrokerExecutionAdmission.Evaluate(
            session,
            capabilities,
            projection.Instruction,
            UtcNow());
        if (admission.IsSuccess)
            return Validate(clientOrderId, riskInput, context);

        var reason = AdmissionReason(admission);
        var committed = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.ValidationRejected,
                OrderLifecycleState.Rejected,
                OrderEventSource.Command,
                rejectedKey,
                UtcNow(),
                context.CausationId,
                Reason: reason));
        return committed.IsSuccess
            ? committed with
            {
                Fault = IsExecutionUnavailable(admission.Fault)
                    ? OmsCommandFault.ExecutionUnavailable
                    : OmsCommandFault.UnsupportedCapability,
                Reason = reason,
            }
            : committed;
    }

    /// <summary>Freezes normalized invariant dispatch terms after successful validation.</summary>
    public OmsCommandResult Prepare(ClientOrderId clientOrderId, in OrderCommandContext context) =>
        Transition(
            clientOrderId,
            OrderLifecycleState.Validated,
            OrderLifecycleState.Prepared,
            OrderEventKind.Prepared,
            OrderEventSource.Command,
            context,
            "prepared");

    /// <summary>
    /// Records fresh authorization. A recovered Prepared order must invoke this again before release.
    /// </summary>
    public OmsCommandResult Arm(ClientOrderId clientOrderId, in OrderCommandContext context) =>
        Transition(
            clientOrderId,
            OrderLifecycleState.Prepared,
            OrderLifecycleState.Armed,
            OrderEventKind.Armed,
            OrderEventSource.Command,
            context,
            "armed");

    /// <summary>
    /// Rechecks the current adapter session and exact capabilities immediately before arming. A
    /// changed or unavailable capability snapshot rejects the still-unarmed Prepared order.
    /// </summary>
    public OmsCommandResult ArmForExecution(
        ClientOrderId clientOrderId,
        BrokerExecutionSession session,
        BrokerExecutionCapabilities capabilities,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State != OrderLifecycleState.Prepared)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        var admission = BrokerExecutionAdmission.Evaluate(
            session,
            capabilities,
            projection.Instruction,
            UtcNow());
        if (admission.IsSuccess)
            return Arm(clientOrderId, context);

        var reason = AdmissionReason(admission);
        var committed = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.ValidationRejected,
                OrderLifecycleState.Rejected,
                OrderEventSource.Command,
                context.DeduplicationKey.Derive("pre-arm-admission-rejected"),
                UtcNow(),
                context.CausationId,
                Reason: reason));
        return committed.IsSuccess
            ? committed with
            {
                Fault = IsExecutionUnavailable(admission.Fault)
                    ? OmsCommandFault.ExecutionUnavailable
                    : OmsCommandFault.UnsupportedCapability,
                Reason = reason,
            }
            : committed;
    }

    /// <summary>Persists the release barrier before a coordinator publishes to its account worker.</summary>
    public OmsCommandResult BeginRelease(
        ClientOrderId clientOrderId,
        in OrderCommandContext context) =>
        Transition(
            clientOrderId,
            OrderLifecycleState.Armed,
            OrderLifecycleState.Releasing,
            OrderEventKind.SendStarted,
            OrderEventSource.Command,
            context,
            "send-started");

    /// <summary>
    /// Persists a local adapter receipt as SubmissionRecorded before any scheduled acknowledgement
    /// callback may advance the order to Working.
    /// </summary>
    public OmsCommandResult RecordDispatchReceipt(
        ClientOrderId clientOrderId,
        BrokerDispatchReceipt receipt,
        in OrderCommandContext context)
    {
        if (receipt is null ||
            !receipt.IsValid ||
            receipt.CommandKind != BrokerAdapterCommandKind.Submit ||
            receipt.ClientOrderId != clientOrderId ||
            receipt.CausationId != context.CausationId)
        {
            return Failed(OmsCommandFault.DispatchReceiptRejected);
        }
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;

        var deduplicationKey = context.DeduplicationKey.Derive("submission-recorded");
        var prior = FindEvent(clientOrderId, OrderEventSource.Command, deduplicationKey);
        var ledgerValue = receipt.ToLedgerValue();
        if (prior is not null)
        {
            return prior.Kind == OrderEventKind.SubmissionRecorded &&
                   prior.CausationId == context.CausationId &&
                   string.Equals(prior.Reason, ledgerValue, StringComparison.Ordinal)
                ? new OmsCommandResult(OmsCommandFault.None, projection)
                : Failed(OmsCommandFault.DuplicateConflict, projection);
        }
        if (projection!.State != OrderLifecycleState.Releasing)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        var recordedAtUtc = UtcNow();
        if (receipt.DispatchedAtUtc > recordedAtUtc)
            return Failed(OmsCommandFault.DispatchReceiptRejected, projection);
        return Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.SubmissionRecorded,
                OrderLifecycleState.Acknowledging,
                OrderEventSource.Command,
                deduplicationKey,
                receipt.DispatchedAtUtc,
                context.CausationId,
                Reason: ledgerValue));
    }

    /// <summary>Records a proved local refusal before adapter acceptance and restores Armed.</summary>
    public OmsCommandResult RecordSendRejectedBeforeDispatch(
        ClientOrderId clientOrderId,
        string reason,
        in OrderCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Failed(OmsCommandFault.InvalidCommand);
        return TransitionWithReason(
            clientOrderId,
            OrderLifecycleState.Releasing,
            OrderLifecycleState.Armed,
            OrderEventKind.SendFailedBeforeAcceptance,
            OrderEventSource.SimulatedVenue,
            context,
            "adapter-send-rejected",
            reason);
    }

    /// <summary>
    /// Persists SendStarted, invokes only the deterministic simulator, and records all callbacks.
    /// Unknown outcome blocks retry; proved pre-acceptance failure returns to Armed with the same id.
    /// </summary>
    public OmsCommandResult Release(ClientOrderId clientOrderId, in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        var sendStartedKey = context.DeduplicationKey.Derive("send-started");
        var priorSend = FindEvent(clientOrderId, OrderEventSource.Command, sendStartedKey);
        if (priorSend is not null)
        {
            if (priorSend.Kind != OrderEventKind.SendStarted ||
                priorSend.CausationId != context.CausationId)
            {
                return Failed(OmsCommandFault.DuplicateConflict, projection);
            }

            if (projection!.State is OrderLifecycleState.Releasing or OrderLifecycleState.Acknowledging)
            {
                return RecoverUnacknowledgedSendAsUnknown(clientOrderId, context);
            }

            return projection.State switch
            {
                OrderLifecycleState.Unknown or OrderLifecycleState.Reconciling =>
                    new OmsCommandResult(OmsCommandFault.VenueOutcomeUnknown, projection),
                OrderLifecycleState.Armed =>
                    new OmsCommandResult(OmsCommandFault.VenueFailedBeforeAcceptance, projection),
                OrderLifecycleState.Rejected =>
                    new OmsCommandResult(OmsCommandFault.VenueRejected, projection),
                _ => new OmsCommandResult(OmsCommandFault.None, projection),
            };
        }
        if (projection!.BlocksRetry)
            return Failed(OmsCommandFault.RetryBlockedUnknown, projection);
        if (projection.State != OrderLifecycleState.Armed)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        var started = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.SendStarted,
                OrderLifecycleState.Releasing,
                OrderEventSource.Command,
                sendStartedKey,
                UtcNow(),
                context.CausationId));
        if (!started.IsSuccess)
            return started;

        var venueResult = _venue.Submit(projection.Instruction, context.CausationId);
        if (venueResult.Status == VenueCommandStatus.Conflict ||
            venueResult.Fault == VenueCommandFault.IdempotencyConflict)
        {
            var ambiguous = CommitLocalUnknown(
                clientOrderId,
                OrderLifecycleState.Releasing,
                context,
                "idempotency-conflict");
            return ambiguous.IsSuccess
                ? ambiguous with { Fault = OmsCommandFault.VenueIdempotencyConflict }
                : ambiguous;
        }

        if (venueResult.Events.Count == 0 && venueResult.EffectiveStatus == VenueCommandStatus.Rejected)
        {
            var rejected = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    OrderEventKind.VenueRejected,
                    OrderLifecycleState.Rejected,
                    OrderEventSource.SimulatedVenue,
                    context.DeduplicationKey.Derive("venue-command-rejected"),
                    UtcNow(),
                    context.CausationId,
                    Reason: venueResult.Fault.ToString()));
            return rejected.IsSuccess
                ? rejected with { Fault = OmsCommandFault.VenueRejected }
                : rejected;
        }

        if (venueResult.EffectiveStatus is VenueCommandStatus.Accepted or VenueCommandStatus.Rejected)
        {
            var submitted = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    OrderEventKind.SubmissionRecorded,
                    OrderLifecycleState.Acknowledging,
                    OrderEventSource.Command,
                    context.DeduplicationKey.Derive("submission-recorded"),
                    UtcNow(),
                    context.CausationId));
            if (!submitted.IsSuccess)
                return submitted;
        }

        OmsCommandResult callbackResult = GetProjection(clientOrderId);
        foreach (var venueEvent in venueResult.Events)
        {
            callbackResult = ApplyVenueEvent(venueEvent);
            if (callbackResult.Fault is OmsCommandFault.PersistenceRejected or
                OmsCommandFault.DuplicateConflict or
                OmsCommandFault.InvalidVenueEvent or
                OmsCommandFault.IllegalTransition)
            {
                return callbackResult;
            }
        }

        if (callbackResult.Fault != OmsCommandFault.None)
            return callbackResult;
        return venueResult.EffectiveStatus switch
        {
            VenueCommandStatus.FailedBeforeAcceptance => callbackResult with
            {
                Fault = OmsCommandFault.VenueFailedBeforeAcceptance,
            },
            VenueCommandStatus.Unknown => callbackResult with
            {
                Fault = OmsCommandFault.VenueOutcomeUnknown,
            },
            VenueCommandStatus.Rejected => callbackResult with
            {
                Fault = OmsCommandFault.VenueRejected,
            },
            _ => callbackResult,
        };
    }

    /// <summary>
    /// Records a pending cancellation without invoking a venue. The coordinator publishes the
    /// immutable cancel command through the account worker after this durable transition.
    /// </summary>
    public OmsCommandResult BeginCancel(
        ClientOrderId clientOrderId,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        var deduplicationKey = context.DeduplicationKey.Derive("cancel-requested");
        var prior = FindEvent(clientOrderId, OrderEventSource.Command, deduplicationKey);
        if (prior is not null)
        {
            return prior.Kind == OrderEventKind.CancelRequested &&
                   prior.CausationId == context.CausationId
                ? new OmsCommandResult(OmsCommandFault.None, projection)
                : Failed(OmsCommandFault.DuplicateConflict, projection);
        }
        if (projection!.State == OrderLifecycleState.Cancelled)
            return new OmsCommandResult(OmsCommandFault.None, projection);
        if (projection.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
            return projection.BlocksRetry
                ? Failed(OmsCommandFault.RetryBlockedUnknown, projection)
                : Failed(OmsCommandFault.IllegalTransition, projection);

        return Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.CancelRequested,
                OrderLifecycleState.PendingCancel,
                OrderEventSource.Command,
                deduplicationKey,
                UtcNow(),
                context.CausationId));
    }

    /// <summary>Requests cancellation while preserving the original order's fillability until confirmation.</summary>
    public OmsCommandResult Cancel(ClientOrderId clientOrderId, in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State == OrderLifecycleState.Cancelled)
            return new OmsCommandResult(OmsCommandFault.None, projection);
        if (projection.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
            return projection.BlocksRetry
                ? Failed(OmsCommandFault.RetryBlockedUnknown, projection)
                : Failed(OmsCommandFault.IllegalTransition, projection);

        var pending = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.CancelRequested,
                OrderLifecycleState.PendingCancel,
                OrderEventSource.Command,
                context.DeduplicationKey.Derive("cancel-requested"),
                UtcNow(),
                context.CausationId));
        if (!pending.IsSuccess)
            return pending;

        var venueResult = _venue.Cancel(clientOrderId, context.CausationId);
        return ApplyVenueCommandResult(
            clientOrderId,
            OrderLifecycleState.PendingCancel,
            context,
            venueResult,
            "cancel");
    }

    /// <summary>
    /// Refuses changed terms when no fresh risk snapshot is supplied. An identical retry is a no-op;
    /// callers changing economics must use the risk-validating overload.
    /// </summary>
    public OmsCommandResult Replace(
        ClientOrderId clientOrderId,
        in CanonicalOrderTerms replacementTerms,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
            return projection.BlocksRetry
                ? Failed(OmsCommandFault.RetryBlockedUnknown, projection)
                : Failed(OmsCommandFault.IllegalTransition, projection);
        if (projection.Terms == replacementTerms)
            return new OmsCommandResult(OmsCommandFault.None, projection);
        return Failed(OmsCommandFault.ReplaceRequiresNewValidation, projection);
    }

    /// <summary>
    /// Evaluates and records fresh versioned risk for changed replacement terms before the original
    /// order enters PendingReplace. A rejected replacement leaves the active order unchanged.
    /// </summary>
    public OmsCommandResult Replace(
        ClientOrderId clientOrderId,
        in CanonicalOrderTerms replacementTerms,
        in RiskInputSnapshot riskInput,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (TryReplayReplacement(
                clientOrderId,
                replacementTerms,
                riskInput,
                context,
                out var replay))
        {
            return replay;
        }
        if (projection!.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
            return projection.BlocksRetry
                ? Failed(OmsCommandFault.RetryBlockedUnknown, projection)
                : Failed(OmsCommandFault.IllegalTransition, projection);
        if (projection.Terms == replacementTerms)
            return new OmsCommandResult(OmsCommandFault.None, projection);
        if (_venue.Capabilities.Validate(replacementTerms) != OrderDomainFault.None ||
            replacementTerms.Side != projection.Terms.Side)
        {
            return Failed(OmsCommandFault.UnsupportedCapability, projection);
        }

        if (!OrderRiskBinding.MatchesTerms(
                riskInput,
                projection.Instruction,
                replacementTerms,
                projection.FilledQuantity))
        {
            return Failed(OmsCommandFault.RiskSnapshotMismatch, projection);
        }

        var decision = _riskEngine.Evaluate(riskInput);
        var riskResult = Commit(
            new OrderEventDraft(
                clientOrderId,
                decision.IsAccepted
                    ? OrderEventKind.ReplaceRiskAccepted
                    : OrderEventKind.ReplaceRiskRejected,
                projection.State,
                OrderEventSource.Risk,
                context.DeduplicationKey.Derive("replace-risk-decision"),
                UtcNow(),
                context.CausationId,
                RiskDecision: decision,
                ReplacementTerms: replacementTerms,
                Reason: decision.IsAccepted ? null : decision.ReasonCodes.ToString()));
        if (!riskResult.IsSuccess)
            return riskResult;
        if (!decision.IsAccepted)
        {
            return riskResult with
            {
                Fault = OmsCommandFault.RiskRejected,
                RiskDecision = decision,
            };
        }

        var pending = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.ReplaceRequested,
                OrderLifecycleState.PendingReplace,
                OrderEventSource.Command,
                context.DeduplicationKey.Derive("replace-requested"),
                UtcNow(),
                context.CausationId,
                ReplacementTerms: replacementTerms));
        if (!pending.IsSuccess)
            return pending;

        var venueResult = _venue.Replace(clientOrderId, replacementTerms, context.CausationId);
        return ApplyVenueCommandResult(
            clientOrderId,
            OrderLifecycleState.PendingReplace,
            context,
            venueResult,
            "replace");
    }

    /// <summary>
    /// Validates and records changed replacement economics without invoking a venue. A coordinator
    /// dispatches the immutable replacement only after this method reaches PendingReplace.
    /// </summary>
    public OmsCommandResult BeginReplace(
        ClientOrderId clientOrderId,
        in CanonicalOrderTerms replacementTerms,
        in RiskInputSnapshot riskInput,
        BrokerExecutionSession session,
        BrokerExecutionCapabilities capabilities,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
            return projection.BlocksRetry
                ? Failed(OmsCommandFault.RetryBlockedUnknown, projection)
                : Failed(OmsCommandFault.IllegalTransition, projection);
        if (projection.Terms == replacementTerms)
            return new OmsCommandResult(OmsCommandFault.None, projection);

        var replacementInstruction = projection.Instruction with { Terms = replacementTerms };
        var admission = BrokerExecutionAdmission.Evaluate(
            session,
            capabilities,
            replacementInstruction,
            UtcNow(),
            isReplace: true);
        if (replacementTerms.Side != projection.Terms.Side)
        {
            return Failed(
                OmsCommandFault.UnsupportedCapability,
                projection,
                "Replacement cannot change the order side.");
        }
        if (!admission.IsSuccess)
        {
            return Failed(
                IsExecutionUnavailable(admission.Fault)
                    ? OmsCommandFault.ExecutionUnavailable
                    : OmsCommandFault.UnsupportedCapability,
                projection,
                AdmissionReason(admission));
        }
        if (projection.FilledQuantity.Coefficient > 0 &&
            (!ScaledValueMath.TryComparePositive(
                replacementTerms.Quantity.Coefficient,
                replacementTerms.Quantity.Scale,
                projection.FilledQuantity.Coefficient,
                projection.FilledQuantity.Scale,
                out var remainingComparison) ||
             remainingComparison <= 0))
        {
            return Failed(
                OmsCommandFault.InvalidInstruction,
                projection,
                "Replacement quantity must remain greater than the already-filled quantity.");
        }
        if (!OrderRiskBinding.MatchesTerms(
                riskInput,
                projection.Instruction,
                replacementTerms,
                projection.FilledQuantity))
        {
            return Failed(OmsCommandFault.RiskSnapshotMismatch, projection);
        }

        var riskKey = context.DeduplicationKey.Derive("replace-risk-decision");
        var priorRisk = FindEvent(clientOrderId, OrderEventSource.Risk, riskKey);
        RiskDecisionRecord decision;
        if (priorRisk is not null)
        {
            if (priorRisk.CausationId != context.CausationId ||
                priorRisk.ReplacementTerms != replacementTerms ||
                !priorRisk.RiskDecision.HasValue ||
                priorRisk.RiskDecision.Value.Input != riskInput)
            {
                return Failed(OmsCommandFault.DuplicateConflict, projection);
            }
            decision = priorRisk.RiskDecision.Value;
            if (priorRisk.Kind == OrderEventKind.ReplaceRiskRejected)
            {
                return new OmsCommandResult(
                    OmsCommandFault.RiskRejected,
                    projection,
                    decision,
                    priorRisk.Reason);
            }
            if (priorRisk.Kind != OrderEventKind.ReplaceRiskAccepted)
                return Failed(OmsCommandFault.DuplicateConflict, projection);
        }
        else
        {
            decision = _riskEngine.Evaluate(riskInput);
            var riskResult = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    decision.IsAccepted
                        ? OrderEventKind.ReplaceRiskAccepted
                        : OrderEventKind.ReplaceRiskRejected,
                    projection.State,
                    OrderEventSource.Risk,
                    riskKey,
                    UtcNow(),
                    context.CausationId,
                    RiskDecision: decision,
                    ReplacementTerms: replacementTerms,
                    Reason: decision.IsAccepted ? null : decision.ReasonCodes.ToString()));
            if (!riskResult.IsSuccess)
                return riskResult;
            if (!decision.IsAccepted)
            {
                return riskResult with
                {
                    Fault = OmsCommandFault.RiskRejected,
                    RiskDecision = decision,
                };
            }
            projection = riskResult.Projection;
        }

        var requestKey = context.DeduplicationKey.Derive("replace-requested");
        var priorRequest = FindEvent(clientOrderId, OrderEventSource.Command, requestKey);
        if (priorRequest is not null)
        {
            return priorRequest.Kind == OrderEventKind.ReplaceRequested &&
                   priorRequest.CausationId == context.CausationId &&
                   priorRequest.ReplacementTerms == replacementTerms
                ? GetProjection(clientOrderId) with { RiskDecision = decision }
                : Failed(OmsCommandFault.DuplicateConflict, projection);
        }

        var pending = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.ReplaceRequested,
                OrderLifecycleState.PendingReplace,
                OrderEventSource.Command,
                requestKey,
                UtcNow(),
                context.CausationId,
                ReplacementTerms: replacementTerms));
        return pending.IsSuccess ? pending with { RiskDecision = decision } : pending;
    }

    /// <summary>
    /// Restores a pending cancel/replace after a proved local adapter refusal such as rate limiting.
    /// The working order remains fillable and no rejection is misreported as a terminal order state.
    /// </summary>
    public OmsCommandResult RecordPendingCommandRejectedBeforeDispatch(
        ClientOrderId clientOrderId,
        OrderLifecycleState pendingState,
        string reason,
        in OrderCommandContext context)
    {
        if (pendingState is not (OrderLifecycleState.PendingCancel or OrderLifecycleState.PendingReplace) ||
            string.IsNullOrWhiteSpace(reason))
        {
            return Failed(OmsCommandFault.InvalidCommand);
        }
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State != pendingState)
            return Failed(OmsCommandFault.IllegalTransition, projection);
        var restoredState = projection.FilledQuantity.Coefficient == 0
            ? OrderLifecycleState.Working
            : OrderLifecycleState.PartiallyFilled;
        return TransitionWithReason(
            clientOrderId,
            pendingState,
            restoredState,
            OrderEventKind.RecoveryObserved,
            OrderEventSource.SimulatedVenue,
            context,
            "adapter-command-rejected",
            reason);
    }

    /// <summary>
    /// Records an indeterminate adapter exception after a cancel or replace request became durable.
    /// The pending order moves to Unknown and requires reconciliation; it is never restored as though
    /// the command had been proved absent.
    /// </summary>
    public OmsCommandResult RecordPendingCommandOutcomeUnknown(
        ClientOrderId clientOrderId,
        OrderLifecycleState pendingState,
        string reason,
        in OrderCommandContext context)
    {
        if (pendingState is not (OrderLifecycleState.PendingCancel or OrderLifecycleState.PendingReplace) ||
            string.IsNullOrWhiteSpace(reason))
        {
            return Failed(OmsCommandFault.InvalidCommand);
        }
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State != pendingState)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        var unknown = TransitionWithReason(
            clientOrderId,
            pendingState,
            OrderLifecycleState.Unknown,
            OrderEventKind.OutcomeUnknown,
            OrderEventSource.Recovery,
            context,
            "adapter-command-outcome-unknown",
            reason);
        return unknown.IsSuccess
            ? unknown with { Fault = OmsCommandFault.VenueOutcomeUnknown }
            : unknown;
    }

    /// <summary>
    /// Applies one at-least-once simulated callback through transactional inbox deduplication.
    /// Duplicate fill callbacks therefore cannot double-count quantity or fees.
    /// </summary>
    public OmsCommandResult ApplyVenueEvent(VenueEvent venueEvent)
    {
        if (venueEvent is null ||
            !venueEvent.ClientOrderId.IsValid ||
            !venueEvent.CausationId.IsValid ||
            !venueEvent.DeduplicationKey.IsValid ||
            venueEvent.OccurredAtUtc.Kind != DateTimeKind.Utc)
        {
            return Failed(OmsCommandFault.InvalidVenueEvent);
        }

        var currentResult = GetProjection(venueEvent.ClientOrderId);
        if (currentResult.Projection is null)
            return currentResult;
        var projection = currentResult.Projection;

        var existing = _eventStore.Read(venueEvent.ClientOrderId)
            .FirstOrDefault(orderEvent =>
                orderEvent.Source == OrderEventSource.SimulatedVenue &&
                orderEvent.DeduplicationKey == venueEvent.DeduplicationKey);

        if (!TryMapVenueEvent(
                venueEvent,
                projection,
                existing?.StateAfter,
                out var kind,
                out var targetState))
        {
            return Failed(OmsCommandFault.InvalidVenueEvent, projection);
        }

        var committed = Commit(
            new OrderEventDraft(
                venueEvent.ClientOrderId,
                kind,
                targetState,
                OrderEventSource.SimulatedVenue,
                venueEvent.DeduplicationKey,
                venueEvent.OccurredAtUtc,
                venueEvent.CausationId,
                BrokerOrderId: venueEvent.BrokerOrderId,
                ExchangeOrderId: venueEvent.ExchangeOrderId,
                Fill: venueEvent.Fill,
                ReplacementTerms: venueEvent.ReplacementTerms,
                Reason: venueEvent.Reason));

        if (!committed.IsSuccess)
            return committed;
        return venueEvent.Kind switch
        {
            VenueEventKind.FailedBeforeAcceptance => committed with
            {
                Fault = OmsCommandFault.VenueFailedBeforeAcceptance,
            },
            VenueEventKind.OutcomeUnknown => committed with
            {
                Fault = OmsCommandFault.VenueOutcomeUnknown,
            },
            VenueEventKind.Rejected => committed with
            {
                Fault = OmsCommandFault.VenueRejected,
            },
            _ => committed,
        };
    }

    /// <summary>
    /// Appends exact commission or position callback evidence without applying a second economic
    /// fill. The existing FillExecution remains authoritative for fee and position projections.
    /// </summary>
    public OmsCommandResult ApplyAdapterEvidence(BrokerAdapterEvent adapterEvent)
    {
        if (adapterEvent is null ||
            !adapterEvent.EventId.IsValid ||
            !adapterEvent.Account.IsValid ||
            !adapterEvent.ClientOrderId.IsValid ||
            adapterEvent.OccurredAtUtc.Kind != DateTimeKind.Utc)
        {
            return Failed(OmsCommandFault.InvalidVenueEvent);
        }

        OrderEventKind kind;
        CausationId causationId;
        string reason;
        switch (adapterEvent)
        {
            case BrokerCommissionEvent commission
                when commission.CausationId.IsValid &&
                     commission.Commission.IsValid &&
                     commission.Commission.Coefficient >= 0:
                kind = OrderEventKind.CommissionObserved;
                causationId = commission.CausationId;
                reason = $"commission={commission.Commission.Coefficient}:{commission.Commission.Scale}";
                break;

            case BrokerPositionEvent position
                when position.CausationId.IsValid &&
                     !position.Instrument.IsNone &&
                     position.Position.IsValid:
                kind = OrderEventKind.PositionObserved;
                causationId = position.CausationId;
                var encodedInstrument = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(position.Instrument.ToString()));
                reason = $"instrument-base64={encodedInstrument};position={position.Position.Coefficient}:{position.Position.Scale}";
                break;

            default:
                return Failed(OmsCommandFault.InvalidVenueEvent);
        }

        var current = GetProjection(adapterEvent.ClientOrderId);
        if (current.Projection is null)
            return current;
        return Commit(
            new OrderEventDraft(
                adapterEvent.ClientOrderId,
                kind,
                current.Projection.State,
                OrderEventSource.SimulatedVenue,
                new DeduplicationKey(adapterEvent.EventId.Value),
                adapterEvent.OccurredAtUtc,
                causationId,
                Reason: reason));
    }

    /// <summary>Records the crash-window rule that SendStarted without acknowledgement is Unknown.</summary>
    public OmsCommandResult RecoverUnacknowledgedSendAsUnknown(
        ClientOrderId clientOrderId,
        in OrderCommandContext context)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State is not (OrderLifecycleState.Releasing or OrderLifecycleState.Acknowledging))
            return Failed(OmsCommandFault.IllegalTransition, projection);

        var result = Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.OutcomeUnknown,
                OrderLifecycleState.Unknown,
                OrderEventSource.Recovery,
                context.DeduplicationKey.Derive("recovered-unknown"),
                UtcNow(),
                context.CausationId,
                Reason: "SendStarted was durable but acknowledgement was not provable."));
        return result.IsSuccess
            ? result with { Fault = OmsCommandFault.VenueOutcomeUnknown }
            : result;
    }

    /// <summary>Records prepared recovery without arming or dispatching the order.</summary>
    public OmsCommandResult ObservePreparedRecovery(
        ClientOrderId clientOrderId,
        in OrderCommandContext context) =>
        Transition(
            clientOrderId,
            OrderLifecycleState.Prepared,
            OrderLifecycleState.Prepared,
            OrderEventKind.RecoveryObserved,
            OrderEventSource.Recovery,
            context,
            "prepared-recovery");

    /// <summary>Moves an Unknown order into explicit reconciliation; retry remains blocked.</summary>
    public OmsCommandResult BeginReconciliation(
        ClientOrderId clientOrderId,
        in OrderCommandContext context) =>
        Transition(
            clientOrderId,
            OrderLifecycleState.Unknown,
            OrderLifecycleState.Reconciling,
            OrderEventKind.ReconciliationStarted,
            OrderEventSource.Reconciliation,
            context,
            "reconciliation-started");

    /// <summary>Appends explicit terminal reconciliation evidence; no live broker query is performed.</summary>
    public OmsCommandResult CompleteReconciliation(
        ClientOrderId clientOrderId,
        in ReconciliationResolution resolution,
        in OrderCommandContext context,
        BrokerOrderId? brokerOrderId = null,
        ExchangeOrderId? exchangeOrderId = null)
    {
        if (!resolution.IsValid ||
            brokerOrderId is { IsValid: false } ||
            exchangeOrderId is { IsValid: false } ||
            resolution.ObservedState is not (
                OrderLifecycleState.Filled or
                OrderLifecycleState.Cancelled or
                OrderLifecycleState.Rejected or
                OrderLifecycleState.Expired))
        {
            return Failed(OmsCommandFault.InvalidReconciliation);
        }
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        if (projection!.State != OrderLifecycleState.Reconciling &&
            projection.State != resolution.ObservedState)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        return Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.Reconciled,
                OrderLifecycleState.Reconciled,
                OrderEventSource.Reconciliation,
                context.DeduplicationKey.Derive("reconciled"),
                UtcNow(),
                context.CausationId,
                BrokerOrderId: brokerOrderId,
                ExchangeOrderId: exchangeOrderId,
                Reconciliation: resolution));
    }

    /// <summary>Reads the simulator's current exact state without changing the OMS ledger.</summary>
    public VenueQueryResult QuerySimulatedVenue(ClientOrderId clientOrderId) =>
        _venue.Query(clientOrderId);

    /// <summary>Rebuilds current OMS state purely from the stored immutable event stream.</summary>
    public OmsCommandResult GetProjection(ClientOrderId clientOrderId)
    {
        if (!clientOrderId.IsValid)
            return Failed(OmsCommandFault.InvalidCommand);
        var events = _eventStore.Read(clientOrderId);
        if (events.Count == 0)
            return Failed(OmsCommandFault.OrderNotFound);
        var rebuilt = OrderProjector.Rebuild(events);
        return rebuilt.IsSuccess
            ? new OmsCommandResult(OmsCommandFault.None, rebuilt.Projection)
            : Failed(
                OmsCommandFault.PersistenceRejected,
                reason: $"Projection failed: {rebuilt.Fault} at event {rebuilt.EventIndex}.");
    }

    /// <summary>Returns an immutable point-in-time copy of the aggregate ledger.</summary>
    public IReadOnlyList<OrderEvent> ReadEvents(ClientOrderId clientOrderId) =>
        _eventStore.Read(clientOrderId);

    /// <summary>Rebuilds every current aggregate projection from the immutable global outbox.</summary>
    public IReadOnlyList<OrderProjection> ReadAllProjections()
    {
        var groups = _eventStore.ReadOutbox()
            .Select(item => item.Event)
            .GroupBy(item => item.AggregateId)
            .OrderBy(item => item.Key.Value, StringComparer.Ordinal);
        var projections = new List<OrderProjection>();
        foreach (var group in groups)
        {
            var events = group.OrderBy(item => item.AggregateSequence).ToArray();
            var rebuilt = OrderProjector.Rebuild(events);
            if (!rebuilt.IsSuccess || rebuilt.Projection is null)
            {
                throw new InvalidDataException(
                    $"Projection failed for '{group.Key}': {rebuilt.Fault} at event {rebuilt.EventIndex}.");
            }
            projections.Add(rebuilt.Projection);
        }
        return projections.Count == 0
            ? Array.Empty<OrderProjection>()
            : Array.AsReadOnly(projections.ToArray());
    }

    private OmsCommandResult ApplyVenueCommandResult(
        ClientOrderId clientOrderId,
        OrderLifecycleState pendingState,
        in OrderCommandContext context,
        VenueCommandResult venueResult,
        string operation)
    {
        if (venueResult.EffectiveStatus == VenueCommandStatus.Unknown)
        {
            var unknown = CommitLocalUnknown(clientOrderId, pendingState, context, $"{operation}-unknown");
            return unknown.IsSuccess
                ? unknown with { Fault = OmsCommandFault.VenueOutcomeUnknown }
                : unknown;
        }

        if (venueResult.EffectiveStatus == VenueCommandStatus.Rejected && venueResult.Events.Count == 0)
        {
            var pendingProjection = GetProjection(clientOrderId).Projection;
            var restoredState = pendingProjection?.FilledQuantity.Coefficient == 0
                ? OrderLifecycleState.Working
                : OrderLifecycleState.PartiallyFilled;
            var restored = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    OrderEventKind.RecoveryObserved,
                    restoredState,
                    OrderEventSource.SimulatedVenue,
                    context.DeduplicationKey.Derive($"{operation}-rejected"),
                    UtcNow(),
                    context.CausationId,
                    Reason: venueResult.Fault.ToString()));
            return restored.IsSuccess
                ? restored with { Fault = OmsCommandFault.VenueRejected }
                : restored;
        }

        OmsCommandResult result = GetProjection(clientOrderId);
        foreach (var venueEvent in venueResult.Events)
        {
            result = ApplyVenueEvent(venueEvent);
            if (!result.IsSuccess)
                return result;
        }

        return result;
    }

    private OmsCommandResult CommitLocalUnknown(
        ClientOrderId clientOrderId,
        OrderLifecycleState expectedState,
        in OrderCommandContext context,
        string suffix)
    {
        var current = GetProjection(clientOrderId);
        if (current.Projection?.State != expectedState)
            return Failed(OmsCommandFault.IllegalTransition, current.Projection);

        return Commit(
            new OrderEventDraft(
                clientOrderId,
                OrderEventKind.OutcomeUnknown,
                OrderLifecycleState.Unknown,
                OrderEventSource.SimulatedVenue,
                context.DeduplicationKey.Derive(suffix),
                UtcNow(),
                context.CausationId,
                Reason: "Simulated venue outcome is unknown; retry is blocked."));
    }

    private OmsCommandResult TransitionWithReason(
        ClientOrderId clientOrderId,
        OrderLifecycleState expectedState,
        OrderLifecycleState targetState,
        OrderEventKind kind,
        OrderEventSource source,
        in OrderCommandContext context,
        string deduplicationSuffix,
        string reason)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        var deduplicationKey = context.DeduplicationKey.Derive(deduplicationSuffix);
        var replay = FindEvent(clientOrderId, source, deduplicationKey);
        if (replay is not null)
        {
            if (replay.Kind != kind ||
                replay.StateAfter != targetState ||
                replay.CausationId != context.CausationId ||
                !string.Equals(replay.Reason, reason, StringComparison.Ordinal))
            {
                return Failed(OmsCommandFault.DuplicateConflict, projection);
            }

            return new OmsCommandResult(OmsCommandFault.None, projection);
        }
        if (projection!.State != expectedState)
            return Failed(OmsCommandFault.IllegalTransition, projection);

        return Commit(
            new OrderEventDraft(
                clientOrderId,
                kind,
                targetState,
                source,
                deduplicationKey,
                UtcNow(),
                context.CausationId,
                Reason: reason));
    }

    private OmsCommandResult Transition(
        ClientOrderId clientOrderId,
        OrderLifecycleState expectedState,
        OrderLifecycleState targetState,
        OrderEventKind kind,
        OrderEventSource source,
        in OrderCommandContext context,
        string deduplicationSuffix)
    {
        if (!TryLoadForCommand(clientOrderId, context, out var projection, out var failure))
            return failure;
        var deduplicationKey = context.DeduplicationKey.Derive(deduplicationSuffix);
        var replay = FindEvent(clientOrderId, source, deduplicationKey);
        if (replay is not null)
        {
            if (replay.Kind != kind ||
                replay.StateAfter != targetState ||
                replay.CausationId != context.CausationId)
            {
                return Failed(OmsCommandFault.DuplicateConflict, projection);
            }

            return new OmsCommandResult(OmsCommandFault.None, projection);
        }
        if (projection!.State != expectedState)
            return projection.BlocksRetry
                ? Failed(OmsCommandFault.RetryBlockedUnknown, projection)
                : Failed(OmsCommandFault.IllegalTransition, projection);

        return Commit(
            new OrderEventDraft(
                clientOrderId,
                kind,
                targetState,
                source,
                deduplicationKey,
                UtcNow(),
                context.CausationId));
    }

    private OmsCommandResult Commit(OrderEventDraft draft)
    {
        var appended = _eventStore.Append(draft, UtcNow());
        if (!appended.IsSuccess)
        {
            var fault = appended.Fault == OrderEventAppendFault.ConflictingDuplicate
                ? OmsCommandFault.DuplicateConflict
                : appended.Fault == OrderEventAppendFault.IllegalTransition
                    ? OmsCommandFault.IllegalTransition
                    : OmsCommandFault.PersistenceRejected;
            return Failed(
                fault,
                reason: $"Event append failed: {appended.Fault}/{appended.ProjectionFault}.");
        }

        var rebuilt = OrderProjector.Rebuild(_eventStore.Read(draft.AggregateId));
        return rebuilt.IsSuccess
            ? new OmsCommandResult(OmsCommandFault.None, rebuilt.Projection)
            : Failed(
                OmsCommandFault.PersistenceRejected,
                reason: $"Projection failed: {rebuilt.Fault} at event {rebuilt.EventIndex}.");
    }

    private bool TryLoadForCommand(
        ClientOrderId clientOrderId,
        in OrderCommandContext context,
        out OrderProjection? projection,
        out OmsCommandResult failure)
    {
        projection = null;
        if (!clientOrderId.IsValid || !context.IsValid)
        {
            failure = Failed(OmsCommandFault.InvalidCommand);
            return false;
        }

        var loaded = GetProjection(clientOrderId);
        if (loaded.Projection is null)
        {
            failure = loaded;
            return false;
        }

        projection = loaded.Projection;
        failure = default;
        return true;
    }

    private bool TryReplayValidation(
        ClientOrderId clientOrderId,
        in RiskInputSnapshot riskInput,
        in OrderCommandContext context,
        out OmsCommandResult result)
    {
        var candidates = new[]
        {
            (OrderEventSource.Risk, context.DeduplicationKey.Derive("risk-decision")),
            (OrderEventSource.Command, context.DeduplicationKey.Derive("capability-rejected")),
            (OrderEventSource.Command, context.DeduplicationKey.Derive("risk-input-rejected")),
        };
        foreach (var (source, key) in candidates)
        {
            var prior = FindEvent(clientOrderId, source, key);
            if (prior is null)
                continue;
            var projection = GetProjection(clientOrderId).Projection;
            if (prior.CausationId != context.CausationId)
            {
                result = Failed(OmsCommandFault.DuplicateConflict, projection);
                return true;
            }

            if (prior.Kind is OrderEventKind.RiskAccepted or OrderEventKind.RiskRejected)
            {
                if (!prior.RiskDecision.HasValue || prior.RiskDecision.Value.Input != riskInput)
                {
                    result = Failed(OmsCommandFault.DuplicateConflict, projection);
                    return true;
                }

                result = new OmsCommandResult(
                    prior.Kind == OrderEventKind.RiskAccepted
                        ? OmsCommandFault.None
                        : OmsCommandFault.RiskRejected,
                    projection,
                    prior.RiskDecision);
                return true;
            }

            result = new OmsCommandResult(
                key == context.DeduplicationKey.Derive("capability-rejected")
                    ? OmsCommandFault.UnsupportedCapability
                    : OmsCommandFault.RiskSnapshotMismatch,
                projection,
                null,
                prior.Reason);
            return true;
        }

        result = default;
        return false;
    }

    private bool TryReplayReplacement(
        ClientOrderId clientOrderId,
        in CanonicalOrderTerms replacementTerms,
        in RiskInputSnapshot riskInput,
        in OrderCommandContext context,
        out OmsCommandResult result)
    {
        var riskEvent = FindEvent(
            clientOrderId,
            OrderEventSource.Risk,
            context.DeduplicationKey.Derive("replace-risk-decision"));
        if (riskEvent is null)
        {
            result = default;
            return false;
        }

        var projection = GetProjection(clientOrderId).Projection;
        if (riskEvent.CausationId != context.CausationId ||
            riskEvent.Kind is not (
                OrderEventKind.ReplaceRiskAccepted or OrderEventKind.ReplaceRiskRejected) ||
            riskEvent.ReplacementTerms != replacementTerms ||
            !riskEvent.RiskDecision.HasValue ||
            riskEvent.RiskDecision.Value.Input != riskInput)
        {
            result = Failed(OmsCommandFault.DuplicateConflict, projection);
            return true;
        }

        var decision = riskEvent.RiskDecision.Value;
        if (riskEvent.Kind == OrderEventKind.ReplaceRiskRejected)
        {
            result = new OmsCommandResult(
                OmsCommandFault.RiskRejected,
                projection,
                decision,
                riskEvent.Reason);
            return true;
        }

        var requestKey = context.DeduplicationKey.Derive("replace-requested");
        var requestEvent = FindEvent(clientOrderId, OrderEventSource.Command, requestKey);
        if (requestEvent is null)
        {
            if (projection is null ||
                projection.State is not (
                    OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled) ||
                !OrderRiskBinding.MatchesTerms(
                    riskInput,
                    projection.Instruction,
                    replacementTerms,
                    projection.FilledQuantity))
            {
                result = Failed(OmsCommandFault.DuplicateConflict, projection);
                return true;
            }

            result = Commit(
                new OrderEventDraft(
                    clientOrderId,
                    OrderEventKind.ReplaceRequested,
                    OrderLifecycleState.PendingReplace,
                    OrderEventSource.Command,
                    requestKey,
                    UtcNow(),
                    context.CausationId,
                    ReplacementTerms: replacementTerms));
            if (!result.IsSuccess)
                return true;
            projection = result.Projection;
        }
        else if (requestEvent.Kind != OrderEventKind.ReplaceRequested ||
                 requestEvent.CausationId != context.CausationId ||
                 requestEvent.ReplacementTerms != replacementTerms)
        {
            result = Failed(OmsCommandFault.DuplicateConflict, projection);
            return true;
        }

        projection = GetProjection(clientOrderId).Projection;
        var rejected = FindEvent(
            clientOrderId,
            OrderEventSource.SimulatedVenue,
            context.DeduplicationKey.Derive("replace-rejected"));
        if (rejected is not null)
        {
            result = rejected.CausationId == context.CausationId
                ? new OmsCommandResult(OmsCommandFault.VenueRejected, projection, decision, rejected.Reason)
                : Failed(OmsCommandFault.DuplicateConflict, projection);
            return true;
        }
        if (projection?.State is OrderLifecycleState.Unknown or OrderLifecycleState.Reconciling)
        {
            result = new OmsCommandResult(OmsCommandFault.VenueOutcomeUnknown, projection, decision);
            return true;
        }
        if (projection?.State != OrderLifecycleState.PendingReplace)
        {
            result = projection is null
                ? Failed(OmsCommandFault.PersistenceRejected)
                : new OmsCommandResult(OmsCommandFault.None, projection, decision);
            return true;
        }

        var venueResult = _venue.Replace(clientOrderId, replacementTerms, context.CausationId);
        result = ApplyVenueCommandResult(
            clientOrderId,
            OrderLifecycleState.PendingReplace,
            context,
            venueResult,
            "replace");
        return true;
    }

    private OrderEvent? FindEvent(
        ClientOrderId clientOrderId,
        OrderEventSource source,
        DeduplicationKey deduplicationKey) =>
        _eventStore.Read(clientOrderId).FirstOrDefault(orderEvent =>
            orderEvent.Source == source &&
            orderEvent.DeduplicationKey == deduplicationKey);

    private static bool TryMapVenueEvent(
        VenueEvent venueEvent,
        OrderProjection projection,
        OrderLifecycleState? replayState,
        out OrderEventKind kind,
        out OrderLifecycleState targetState)
    {
        kind = venueEvent.Kind switch
        {
            VenueEventKind.Acknowledged => OrderEventKind.VenueAcknowledged,
            VenueEventKind.Rejected => OrderEventKind.VenueRejected,
            VenueEventKind.FailedBeforeAcceptance => OrderEventKind.SendFailedBeforeAcceptance,
            VenueEventKind.OutcomeUnknown => OrderEventKind.OutcomeUnknown,
            VenueEventKind.Fill => OrderEventKind.FillReceived,
            VenueEventKind.Cancelled => OrderEventKind.CancelConfirmed,
            VenueEventKind.Replaced => OrderEventKind.ReplaceConfirmed,
            VenueEventKind.Expired => OrderEventKind.Expired,
            _ => default,
        };

        if (!Enum.IsDefined(venueEvent.Kind))
        {
            targetState = default;
            return false;
        }

        if (replayState.HasValue)
        {
            targetState = replayState.Value;
            return true;
        }

        switch (venueEvent.Kind)
        {
            case VenueEventKind.Acknowledged:
                if (projection.State is OrderLifecycleState.Acknowledging or OrderLifecycleState.Reconciling)
                    targetState = OrderLifecycleState.Working;
                else if (projection.State is OrderLifecycleState.Working or
                    OrderLifecycleState.PartiallyFilled or
                    OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace or
                    OrderLifecycleState.Filled or
                    OrderLifecycleState.Cancelled or
                    OrderLifecycleState.Rejected or
                    OrderLifecycleState.Expired)
                    targetState = projection.State;
                else
                    return FailedTarget(out targetState);
                return true;

            case VenueEventKind.Fill:
                if (!venueEvent.Fill.HasValue || !venueEvent.Fill.Value.IsValid ||
                    !TryAddQuantity(
                        projection.FilledQuantity,
                        venueEvent.Fill.Value.Quantity,
                        out var cumulative) ||
                    !ScaledValueMath.TryComparePositive(
                        cumulative.Coefficient,
                        cumulative.Scale,
                        projection.Terms.Quantity.Coefficient,
                        projection.Terms.Quantity.Scale,
                        out var comparison) ||
                    comparison > 0)
                {
                    return FailedTarget(out targetState);
                }

                targetState = comparison == 0
                    ? OrderLifecycleState.Filled
                    : projection.State switch
                    {
                        OrderLifecycleState.PendingCancel => OrderLifecycleState.PendingCancel,
                        OrderLifecycleState.PendingReplace => OrderLifecycleState.PendingReplace,
                        _ => OrderLifecycleState.PartiallyFilled,
                    };
                return true;

            case VenueEventKind.Rejected:
                targetState = OrderLifecycleState.Rejected;
                return true;
            case VenueEventKind.FailedBeforeAcceptance:
                targetState = OrderLifecycleState.Armed;
                return true;
            case VenueEventKind.OutcomeUnknown:
                targetState = OrderLifecycleState.Unknown;
                return true;
            case VenueEventKind.Cancelled:
                targetState = OrderLifecycleState.Cancelled;
                return true;
            case VenueEventKind.Replaced:
                if (!venueEvent.ReplacementTerms.HasValue)
                    return FailedTarget(out targetState);
                targetState = projection.FilledQuantity.Coefficient == 0
                    ? OrderLifecycleState.Working
                    : OrderLifecycleState.PartiallyFilled;
                return true;
            case VenueEventKind.Expired:
                targetState = OrderLifecycleState.Expired;
                return true;
            default:
                return FailedTarget(out targetState);
        }
    }

    private static bool FailedTarget(out OrderLifecycleState targetState)
    {
        targetState = default;
        return false;
    }

    private static bool TryAddQuantity(
        in ScaledQuantity left,
        in ScaledQuantity right,
        out ScaledQuantity sum)
    {
        sum = default;
        if (!ScaledValueMath.TryAdd(
                left.Coefficient,
                left.Scale,
                right.Coefficient,
                right.Scale,
                out var coefficient,
                out var scale) ||
            !ScaledValueMath.TryNarrow(
                coefficient,
                scale,
                out var narrowedCoefficient,
                out var narrowedScale))
        {
            return false;
        }

        sum = new ScaledQuantity(narrowedCoefficient, narrowedScale);
        return true;
    }

    private DateTime UtcNow()
    {
        var value = _clock.UtcNow;
        return value.Kind == DateTimeKind.Utc
            ? value
            : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }

    private static bool IsExecutionUnavailable(ExecutionAdmissionFault fault) =>
        fault is ExecutionAdmissionFault.InvalidSession or
            ExecutionAdmissionFault.DataDisconnected or
            ExecutionAdmissionFault.ExecutionNotAuthenticated or
            ExecutionAdmissionFault.ExecutionNotCertified or
            ExecutionAdmissionFault.SessionUnavailable;

    private static string AdmissionReason(in ExecutionAdmissionResult admission) =>
        $"Execution admission {admission.Fault}: {admission.Reason ?? "No reason supplied."}";

    private static bool IsExecutionUnavailableReason(string? reason) =>
        reason is not null &&
        (reason.StartsWith($"Execution admission {ExecutionAdmissionFault.InvalidSession}:", StringComparison.Ordinal) ||
         reason.StartsWith($"Execution admission {ExecutionAdmissionFault.DataDisconnected}:", StringComparison.Ordinal) ||
         reason.StartsWith($"Execution admission {ExecutionAdmissionFault.ExecutionNotAuthenticated}:", StringComparison.Ordinal) ||
         reason.StartsWith($"Execution admission {ExecutionAdmissionFault.ExecutionNotCertified}:", StringComparison.Ordinal) ||
         reason.StartsWith($"Execution admission {ExecutionAdmissionFault.SessionUnavailable}:", StringComparison.Ordinal));

    private static OmsCommandResult Failed(
        OmsCommandFault fault,
        OrderProjection? projection = null,
        string? reason = null) =>
        new(fault, projection, null, reason);
}
