namespace TradingTerminal.Execution.Oms;

/// <summary>Integrity faults found while verifying one aggregate's tamper-evident event chain.</summary>
public enum OrderEventChainFault : byte
{
    /// <summary>The chain is valid.</summary>
    None = 0,

    /// <summary>No event stream was supplied.</summary>
    MissingStream = 1,

    /// <summary>The supplied stream contained no events.</summary>
    EmptyStream = 2,

    /// <summary>A stream position contained no event value.</summary>
    MissingEvent = 3,

    /// <summary>An event carried an invalid aggregate identity.</summary>
    InvalidAggregateId = 4,

    /// <summary>Events from different aggregates were mixed in one stream.</summary>
    MixedAggregate = 5,

    /// <summary>The aggregate sequence was not contiguous and one-based.</summary>
    InvalidSequence = 6,

    /// <summary>The stream did not begin with a draft-created event in Draft state.</summary>
    InvalidInitialEvent = 7,

    /// <summary>An event's recorded prior state did not match the preceding event.</summary>
    StateBeforeMismatch = 8,

    /// <summary>A state change or narrow admitted self-state fact was illegal.</summary>
    IllegalTransition = 9,

    /// <summary>The event did not point to the preceding event hash.</summary>
    PreviousHashMismatch = 10,

    /// <summary>The stored event hash did not match a fresh hash of all immutable fields.</summary>
    EventHashMismatch = 11,

    /// <summary>An event time was not UTC or recorded time preceded occurred time.</summary>
    InvalidTimestamp = 12,

    /// <summary>The event kind cannot originate from the subsystem claimed by its source.</summary>
    InvalidEventSource = 13,
}

/// <summary>Value result from verifying aggregate sequence, lifecycle, and hash-chain integrity.</summary>
/// <param name="Fault">Detected chain fault.</param>
/// <param name="EventIndex">Zero-based failing position, or -1 when no individual event failed.</param>
public readonly record struct OrderEventChainVerificationResult(
    OrderEventChainFault Fault,
    int EventIndex)
{
    /// <summary>Gets whether every supplied event passed integrity verification.</summary>
    public bool IsValid => Fault == OrderEventChainFault.None;
}

/// <summary>
/// Tamper-evident verifier for the per-aggregate SHA-256 chain from roadmap section 13.1. It detects
/// accidental or silent corruption, not an administrator able to rewrite both rows and hashes.
/// </summary>
public static class OrderEventChainVerifier
{
    /// <summary>
    /// Recomputes every hash and validates aggregate identity, one-based sequence, prior-state
    /// continuity, and legal lifecycle application. No persisted projection is trusted.
    /// </summary>
    public static OrderEventChainVerificationResult Verify(IReadOnlyList<OrderEvent>? events)
    {
        if (events is null)
            return Failed(OrderEventChainFault.MissingStream, -1);
        if (events.Count == 0)
            return Failed(OrderEventChainFault.EmptyStream, -1);

        ClientOrderId aggregateId = default;
        OrderEvent? previous = null;
        for (var index = 0; index < events.Count; index++)
        {
            var current = events[index];
            if (current is null)
                return Failed(OrderEventChainFault.MissingEvent, index);
            if (!current.AggregateId.IsValid)
                return Failed(OrderEventChainFault.InvalidAggregateId, index);
            if (current.OccurredAtUtc.Kind != DateTimeKind.Utc ||
                current.RecordedAtUtc.Kind != DateTimeKind.Utc ||
                current.RecordedAtUtc < current.OccurredAtUtc)
            {
                return Failed(OrderEventChainFault.InvalidTimestamp, index);
            }
            if (!OrderLifecycle.IsEventSourceAllowed(current.Kind, current.Source))
                return Failed(OrderEventChainFault.InvalidEventSource, index);

            if (index == 0)
            {
                aggregateId = current.AggregateId;
                if (current.AggregateSequence != 1)
                    return Failed(OrderEventChainFault.InvalidSequence, index);
                if (current.Kind != OrderEventKind.DraftCreated ||
                    current.StateBefore.HasValue ||
                    current.StateAfter != OrderLifecycleState.Draft)
                {
                    return Failed(OrderEventChainFault.InvalidInitialEvent, index);
                }
                if (!string.Equals(
                        current.PreviousEventHash,
                        OrderEventHash.EmptyPreviousHash,
                        StringComparison.Ordinal))
                {
                    return Failed(OrderEventChainFault.PreviousHashMismatch, index);
                }
            }
            else
            {
                if (current.AggregateId != aggregateId)
                    return Failed(OrderEventChainFault.MixedAggregate, index);
                if (previous is null ||
                    previous.AggregateSequence == long.MaxValue ||
                    current.AggregateSequence != previous.AggregateSequence + 1)
                {
                    return Failed(OrderEventChainFault.InvalidSequence, index);
                }
                if (current.StateBefore != previous.StateAfter)
                    return Failed(OrderEventChainFault.StateBeforeMismatch, index);
                if (!OrderEventTransitionRules.CanApply(
                        previous.StateAfter,
                        current.StateAfter,
                        current.Kind))
                {
                    return Failed(OrderEventChainFault.IllegalTransition, index);
                }
                if (!string.Equals(current.PreviousEventHash, previous.EventHash, StringComparison.Ordinal))
                    return Failed(OrderEventChainFault.PreviousHashMismatch, index);
            }

            var computedHash = OrderEventHash.Compute(current);
            if (!string.Equals(current.EventHash, computedHash, StringComparison.Ordinal))
                return Failed(OrderEventChainFault.EventHashMismatch, index);

            previous = current;
        }

        return new OrderEventChainVerificationResult(OrderEventChainFault.None, -1);
    }

    private static OrderEventChainVerificationResult Failed(OrderEventChainFault fault, int eventIndex) =>
        new(fault, eventIndex);
}

/// <summary>Fault-as-value reasons a verified event stream cannot produce an economic projection.</summary>
public enum OrderProjectionFault : byte
{
    /// <summary>The projection was rebuilt successfully.</summary>
    None = 0,

    /// <summary>Aggregate sequence, state continuity, or hash-chain verification failed.</summary>
    InvalidEventChain = 1,

    /// <summary>The draft event did not carry the canonical instruction.</summary>
    MissingInstruction = 2,

    /// <summary>The canonical instruction was structurally invalid or belonged to another aggregate.</summary>
    InvalidInstruction = 3,

    /// <summary>A later event attempted to replace the immutable canonical instruction.</summary>
    InstructionChanged = 4,

    /// <summary>A risk transition omitted, misplaced, or contradicted its immutable decision record.</summary>
    InvalidRiskDecision = 5,

    /// <summary>A later event attempted to rewrite an already-recorded risk explanation.</summary>
    RiskDecisionChanged = 6,

    /// <summary>An externally assigned identity was invalid.</summary>
    InvalidExternalOrderId = 7,

    /// <summary>Two events assigned different external identities to the same canonical order.</summary>
    ConflictingExternalOrderId = 8,

    /// <summary>A replacement event omitted terms or carried structurally invalid terms.</summary>
    InvalidReplacementTerms = 9,

    /// <summary>A non-replacement event unexpectedly carried replacement terms.</summary>
    UnexpectedReplacementTerms = 10,

    /// <summary>A fill event omitted exact valid economics, or another event carried fill economics.</summary>
    InvalidFill = 11,

    /// <summary>Exact fill quantity or fee accumulation exceeded the supported integer range.</summary>
    FillArithmeticOverflow = 12,

    /// <summary>Cumulative fill quantity exceeded the effective canonical order quantity.</summary>
    FillQuantityExceeded = 13,

    /// <summary>A fill's resulting lifecycle state disagreed with its cumulative exact quantity.</summary>
    FillStateMismatch = 14,

    /// <summary>A replacement request was not immediately authorized by matching accepted risk.</summary>
    MissingReplacementAuthorization = 15,

    /// <summary>A reconciliation event omitted or contradicted its required terminal evidence.</summary>
    InvalidReconciliation = 16,

    /// <summary>A non-reconciliation event unexpectedly carried reconciliation evidence.</summary>
    UnexpectedReconciliation = 17,
}

/// <summary>
/// Current OMS state rebuilt only from immutable events. It contains exact economics and the
/// versioned risk explanation; no database row or mutable in-memory aggregate is authoritative.
/// </summary>
/// <param name="ClientOrderId">Canonical aggregate and durable idempotency identity.</param>
/// <param name="State">Current rich OMS lifecycle state.</param>
/// <param name="Instruction">Original immutable economic instruction.</param>
/// <param name="Terms">Effective terms, including the last confirmed replacement.</param>
/// <param name="ReplacementTerms">Last requested or confirmed replacement terms, when any.</param>
/// <param name="RiskDecision">Immutable versioned pre-trade explanation, when evaluated.</param>
/// <param name="BrokerOrderId">Optional externally assigned broker identity.</param>
/// <param name="ExchangeOrderId">Optional externally assigned exchange identity.</param>
/// <param name="FilledQuantity">Cumulative exact fill quantity.</param>
/// <param name="TotalFees">Cumulative exact non-negative fees.</param>
/// <param name="LastSequence">Last applied aggregate sequence.</param>
/// <param name="LastEventHash">Hash of the last applied event.</param>
/// <param name="LastCausationId">Causation identity of the last applied fact.</param>
public sealed record OrderProjection(
    ClientOrderId ClientOrderId,
    OrderLifecycleState State,
    CanonicalOrderInstruction Instruction,
    CanonicalOrderTerms Terms,
    CanonicalOrderTerms? ReplacementTerms,
    RiskDecisionRecord? RiskDecision,
    BrokerOrderId? BrokerOrderId,
    ExchangeOrderId? ExchangeOrderId,
    ScaledQuantity FilledQuantity,
    ScaledMoney TotalFees,
    long LastSequence,
    string LastEventHash,
    CausationId LastCausationId)
{
    /// <summary>Gets whether blind submission retry is forbidden pending explicit reconciliation.</summary>
    public bool BlocksRetry => OrderLifecycle.BlocksRetry(State);

    /// <summary>Rebuilds a projection from the complete event stream without ambient state.</summary>
    public static OrderProjectionResult Rebuild(IReadOnlyList<OrderEvent>? events) =>
        OrderProjector.Rebuild(events);
}

/// <summary>Value result from a pure current-state projection rebuild.</summary>
/// <param name="Fault">Detected projection fault.</param>
/// <param name="Projection">Rebuilt state when successful.</param>
/// <param name="EventIndex">Zero-based failing event position, or -1 when not applicable.</param>
/// <param name="ChainFault">Detailed integrity fault when chain verification failed.</param>
public readonly record struct OrderProjectionResult(
    OrderProjectionFault Fault,
    OrderProjection? Projection,
    int EventIndex,
    OrderEventChainFault ChainFault = OrderEventChainFault.None)
{
    /// <summary>Gets whether a complete current-state projection was produced.</summary>
    public bool IsSuccess => Fault == OrderProjectionFault.None && Projection is not null;
}

/// <summary>Pure event fold for the slice-1 OMS current-state projection.</summary>
public static class OrderProjector
{
    /// <summary>
    /// Verifies and folds a complete aggregate stream. Exact quantity and fee accumulation uses
    /// <see cref="ScaledValueMath"/> and rejects overflow or excess rather than clamping.
    /// </summary>
    public static OrderProjectionResult Rebuild(IReadOnlyList<OrderEvent>? events)
    {
        var chain = OrderEventChainVerifier.Verify(events);
        if (!chain.IsValid)
        {
            return Failed(
                OrderProjectionFault.InvalidEventChain,
                chain.EventIndex,
                chain.Fault);
        }

        CanonicalOrderInstruction? instruction = null;
        var terms = default(CanonicalOrderTerms);
        CanonicalOrderTerms? replacementTerms = null;
        RiskDecisionRecord? riskDecision = null;
        BrokerOrderId? brokerOrderId = null;
        ExchangeOrderId? exchangeOrderId = null;
        var filledQuantity = ScaledQuantity.Zero;
        var totalFees = ScaledMoney.Zero;

        for (var index = 0; index < events!.Count; index++)
        {
            var orderEvent = events[index];
            if (orderEvent.Instruction is not null)
            {
                if (orderEvent.Instruction.Validate() != OrderDomainFault.None ||
                    orderEvent.Instruction.Identity.ClientOrderId != orderEvent.AggregateId)
                {
                    return Failed(OrderProjectionFault.InvalidInstruction, index);
                }

                if (instruction is null)
                {
                    instruction = orderEvent.Instruction;
                    terms = instruction.Terms;
                    brokerOrderId = instruction.Identity.BrokerOrderId;
                    exchangeOrderId = instruction.Identity.ExchangeOrderId;
                }
                else if (instruction != orderEvent.Instruction)
                {
                    return Failed(OrderProjectionFault.InstructionChanged, index);
                }
            }

            if (index == 0 && instruction is null)
                return Failed(OrderProjectionFault.MissingInstruction, index);

            var riskFault = ApplyRiskDecision(
                orderEvent,
                instruction!,
                filledQuantity,
                ref riskDecision);
            if (riskFault != OrderProjectionFault.None)
                return Failed(riskFault, index);

            var externalIdFault = ApplyExternalIds(
                orderEvent,
                ref brokerOrderId,
                ref exchangeOrderId);
            if (externalIdFault != OrderProjectionFault.None)
                return Failed(externalIdFault, index);

            var replacementFault = ApplyReplacement(
                orderEvent,
                index > 0 ? events[index - 1] : null,
                filledQuantity,
                ref terms,
                ref replacementTerms);
            if (replacementFault != OrderProjectionFault.None)
                return Failed(replacementFault, index);

            var reconciliationFault = ApplyReconciliation(orderEvent);
            if (reconciliationFault != OrderProjectionFault.None)
                return Failed(reconciliationFault, index);

            var fillFault = ApplyFill(
                orderEvent,
                terms,
                ref filledQuantity,
                ref totalFees);
            if (fillFault != OrderProjectionFault.None)
                return Failed(fillFault, index);
        }

        var last = events[^1];
        if (!StateMatchesFillEconomics(last.StateAfter, terms.Quantity, filledQuantity))
            return Failed(OrderProjectionFault.FillStateMismatch, events.Count - 1);
        return new OrderProjectionResult(
            OrderProjectionFault.None,
            new OrderProjection(
                last.AggregateId,
                last.StateAfter,
                instruction!,
                terms,
                replacementTerms,
                riskDecision,
                brokerOrderId,
                exchangeOrderId,
                filledQuantity,
                totalFees,
                last.AggregateSequence,
                last.EventHash,
                last.CausationId),
            -1);
    }

    private static OrderProjectionFault ApplyRiskDecision(
        OrderEvent orderEvent,
        CanonicalOrderInstruction instruction,
        in ScaledQuantity filledQuantity,
        ref RiskDecisionRecord? retainedDecision)
    {
        var isInitialRiskEvent = orderEvent.Kind is OrderEventKind.RiskAccepted or OrderEventKind.RiskRejected;
        var isReplacementRiskEvent = orderEvent.Kind is
            OrderEventKind.ReplaceRiskAccepted or OrderEventKind.ReplaceRiskRejected;
        var isRiskEvent = isInitialRiskEvent || isReplacementRiskEvent;
        if (isRiskEvent != orderEvent.RiskDecision.HasValue)
            return OrderProjectionFault.InvalidRiskDecision;
        if (!orderEvent.RiskDecision.HasValue)
            return OrderProjectionFault.None;

        var decision = orderEvent.RiskDecision.Value;
        if (string.IsNullOrWhiteSpace(decision.PolicyId) ||
            string.IsNullOrWhiteSpace(decision.PolicyVersion) ||
            string.IsNullOrWhiteSpace(decision.PolicyHash) ||
            isInitialRiskEvent && !OrderRiskBinding.MatchesInstruction(decision.Input, instruction) ||
            isReplacementRiskEvent &&
                (!orderEvent.ReplacementTerms.HasValue ||
                 !OrderRiskBinding.MatchesTerms(
                     decision.Input,
                     instruction,
                     orderEvent.ReplacementTerms.Value,
                     filledQuantity)) ||
            orderEvent.Kind is OrderEventKind.RiskAccepted or OrderEventKind.ReplaceRiskAccepted &&
                !decision.IsAccepted ||
            orderEvent.Kind is OrderEventKind.RiskRejected or OrderEventKind.ReplaceRiskRejected &&
                decision.Outcome != RiskDecisionOutcome.Rejected)
        {
            return OrderProjectionFault.InvalidRiskDecision;
        }

        if (isInitialRiskEvent && retainedDecision.HasValue && retainedDecision.Value != decision)
            return OrderProjectionFault.RiskDecisionChanged;

        if (decision.IsAccepted || !retainedDecision.HasValue)
            retainedDecision = decision;
        return OrderProjectionFault.None;
    }

    private static OrderProjectionFault ApplyExternalIds(
        OrderEvent orderEvent,
        ref BrokerOrderId? brokerOrderId,
        ref ExchangeOrderId? exchangeOrderId)
    {
        if (orderEvent.BrokerOrderId.HasValue)
        {
            var proposed = orderEvent.BrokerOrderId.Value;
            if (!proposed.IsValid)
                return OrderProjectionFault.InvalidExternalOrderId;
            if (brokerOrderId.HasValue && brokerOrderId.Value != proposed)
                return OrderProjectionFault.ConflictingExternalOrderId;
            brokerOrderId = proposed;
        }

        if (orderEvent.ExchangeOrderId.HasValue)
        {
            var proposed = orderEvent.ExchangeOrderId.Value;
            if (!proposed.IsValid)
                return OrderProjectionFault.InvalidExternalOrderId;
            if (exchangeOrderId.HasValue && exchangeOrderId.Value != proposed)
                return OrderProjectionFault.ConflictingExternalOrderId;
            exchangeOrderId = proposed;
        }

        return OrderProjectionFault.None;
    }

    private static OrderProjectionFault ApplyReplacement(
        OrderEvent orderEvent,
        OrderEvent? previousEvent,
        ScaledQuantity filledQuantity,
        ref CanonicalOrderTerms terms,
        ref CanonicalOrderTerms? replacementTerms)
    {
        var isReplacementRiskEvent = orderEvent.Kind is
            OrderEventKind.ReplaceRiskAccepted or OrderEventKind.ReplaceRiskRejected;
        if (isReplacementRiskEvent)
        {
            if (!orderEvent.ReplacementTerms.HasValue ||
                orderEvent.ReplacementTerms.Value.Validate() != OrderDomainFault.None ||
                orderEvent.ReplacementTerms.Value.Side != terms.Side ||
                !CanContainFilledQuantity(orderEvent.ReplacementTerms.Value.Quantity, filledQuantity))
            {
                return OrderProjectionFault.InvalidReplacementTerms;
            }

            return OrderProjectionFault.None;
        }

        var isReplacementEvent =
            orderEvent.Kind is OrderEventKind.ReplaceRequested or OrderEventKind.ReplaceConfirmed;
        if (!isReplacementEvent && orderEvent.ReplacementTerms.HasValue)
            return OrderProjectionFault.UnexpectedReplacementTerms;
        if (!isReplacementEvent)
            return OrderProjectionFault.None;

        if (orderEvent.Kind == OrderEventKind.ReplaceConfirmed &&
            (!orderEvent.ReplacementTerms.HasValue ||
             !replacementTerms.HasValue ||
             orderEvent.ReplacementTerms.Value != replacementTerms.Value))
        {
            return OrderProjectionFault.InvalidReplacementTerms;
        }

        var proposed = orderEvent.ReplacementTerms ?? replacementTerms;
        if (!proposed.HasValue ||
            proposed.Value.Validate() != OrderDomainFault.None ||
            proposed.Value.Side != terms.Side)
            return OrderProjectionFault.InvalidReplacementTerms;
        if (orderEvent.Kind == OrderEventKind.ReplaceRequested &&
            (previousEvent?.Kind != OrderEventKind.ReplaceRiskAccepted ||
             previousEvent.CausationId != orderEvent.CausationId ||
             previousEvent.ReplacementTerms != proposed))
        {
            return OrderProjectionFault.MissingReplacementAuthorization;
        }
        if (!CanContainFilledQuantity(proposed.Value.Quantity, filledQuantity))
            return OrderProjectionFault.InvalidReplacementTerms;

        replacementTerms = proposed.Value;
        if (orderEvent.Kind == OrderEventKind.ReplaceConfirmed)
            terms = proposed.Value;
        return OrderProjectionFault.None;
    }

    private static OrderProjectionFault ApplyReconciliation(OrderEvent orderEvent)
    {
        if (orderEvent.Kind != OrderEventKind.Reconciled)
        {
            return orderEvent.Reconciliation.HasValue
                ? OrderProjectionFault.UnexpectedReconciliation
                : OrderProjectionFault.None;
        }
        if (!orderEvent.Reconciliation.HasValue || !orderEvent.Reconciliation.Value.IsValid)
            return OrderProjectionFault.InvalidReconciliation;

        var observedState = orderEvent.Reconciliation.Value.ObservedState;
        if ((orderEvent.StateBefore is OrderLifecycleState.Filled or
                OrderLifecycleState.Cancelled or
                OrderLifecycleState.Rejected or
                OrderLifecycleState.Expired) &&
            orderEvent.StateBefore != observedState)
        {
            return OrderProjectionFault.InvalidReconciliation;
        }

        return OrderProjectionFault.None;
    }

    private static bool StateMatchesFillEconomics(
        OrderLifecycleState state,
        in ScaledQuantity orderQuantity,
        in ScaledQuantity filledQuantity)
    {
        if (state is not (OrderLifecycleState.PartiallyFilled or OrderLifecycleState.Filled))
            return true;
        if (!ScaledValueMath.TryComparePositive(
                filledQuantity.Coefficient,
                filledQuantity.Scale,
                orderQuantity.Coefficient,
                orderQuantity.Scale,
                out var comparison))
        {
            return false;
        }

        return state == OrderLifecycleState.Filled
            ? comparison == 0
            : filledQuantity.Coefficient > 0 && comparison < 0;
    }

    private static OrderProjectionFault ApplyFill(
        OrderEvent orderEvent,
        in CanonicalOrderTerms terms,
        ref ScaledQuantity filledQuantity,
        ref ScaledMoney totalFees)
    {
        if (orderEvent.Kind != OrderEventKind.FillReceived)
            return orderEvent.Fill.HasValue ? OrderProjectionFault.InvalidFill : OrderProjectionFault.None;
        if (!orderEvent.Fill.HasValue || !orderEvent.Fill.Value.IsValid)
            return OrderProjectionFault.InvalidFill;

        var fill = orderEvent.Fill.Value;
        if (!TryAddQuantity(filledQuantity, fill.Quantity, out var nextQuantity) ||
            !TryAddMoney(totalFees, fill.Fee, out var nextFees))
        {
            return OrderProjectionFault.FillArithmeticOverflow;
        }
        if (!ScaledValueMath.TryComparePositive(
                nextQuantity.Coefficient,
                nextQuantity.Scale,
                terms.Quantity.Coefficient,
                terms.Quantity.Scale,
                out var quantityComparison))
        {
            return OrderProjectionFault.FillArithmeticOverflow;
        }
        if (quantityComparison > 0)
            return OrderProjectionFault.FillQuantityExceeded;

        var expectedState = quantityComparison == 0
            ? OrderLifecycleState.Filled
            : orderEvent.StateBefore switch
            {
                OrderLifecycleState.PendingCancel => OrderLifecycleState.PendingCancel,
                OrderLifecycleState.PendingReplace => OrderLifecycleState.PendingReplace,
                _ => OrderLifecycleState.PartiallyFilled,
            };
        if (orderEvent.StateAfter != expectedState)
            return OrderProjectionFault.FillStateMismatch;

        filledQuantity = nextQuantity;
        totalFees = nextFees;
        return OrderProjectionFault.None;
    }

    private static bool CanContainFilledQuantity(
        in ScaledQuantity orderQuantity,
        in ScaledQuantity filledQuantity)
    {
        if (filledQuantity.Coefficient == 0)
            return true;
        return ScaledValueMath.TryComparePositive(
                   orderQuantity.Coefficient,
                   orderQuantity.Scale,
                   filledQuantity.Coefficient,
                   filledQuantity.Scale,
                   out var comparison) &&
               comparison >= 0;
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

    private static bool TryAddMoney(
        in ScaledMoney left,
        in ScaledMoney right,
        out ScaledMoney sum)
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

        sum = new ScaledMoney(narrowedCoefficient, narrowedScale);
        return true;
    }

    private static OrderProjectionResult Failed(
        OrderProjectionFault fault,
        int eventIndex,
        OrderEventChainFault chainFault = OrderEventChainFault.None) =>
        new(fault, null, eventIndex, chainFault);
}
