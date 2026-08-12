using System.Collections.ObjectModel;

namespace TradingTerminal.Execution.Oms;

/// <summary>Canonical OMS lifecycle from ADR D7 and roadmap section 6.1.</summary>
public enum OrderLifecycleState : byte
{
    /// <summary>Editable instruction with no economic effect.</summary>
    Draft = 0,

    /// <summary>Capability, structure, and versioned pre-trade risk checks passed.</summary>
    Validated = 1,

    /// <summary>Invariant terms and idempotency data are frozen.</summary>
    Prepared = 2,

    /// <summary>A fresh authorization permits release.</summary>
    Armed = 3,

    /// <summary>The deterministic venue dispatch has started.</summary>
    Releasing = 4,

    /// <summary>Submission occurred and acknowledgement is pending.</summary>
    Acknowledging = 5,

    /// <summary>The venue accepted an economically active order.</summary>
    Working = 6,

    /// <summary>The venue filled some, but not all, of the exact quantity.</summary>
    PartiallyFilled = 7,

    /// <summary>The exact requested quantity filled.</summary>
    Filled = 8,

    /// <summary>Cancellation was confirmed.</summary>
    Cancelled = 9,

    /// <summary>The instruction or venue submission was observably rejected.</summary>
    Rejected = 10,

    /// <summary>The venue time-in-force expired.</summary>
    Expired = 11,

    /// <summary>The send may be externally visible; blind retry is forbidden.</summary>
    Unknown = 12,

    /// <summary>Explicit evidence collection is resolving an unknown outcome.</summary>
    Reconciling = 13,

    /// <summary>The original order may still fill while cancellation is pending.</summary>
    PendingCancel = 14,

    /// <summary>The original order may still fill while replacement is pending.</summary>
    PendingReplace = 15,

    /// <summary>Terminal evidence records that local interpretation matches venue truth.</summary>
    Reconciled = 16,
}

/// <summary>One allowed directed lifecycle edge.</summary>
public readonly record struct OrderLifecycleTransition(
    OrderLifecycleState From,
    OrderLifecycleState To);

/// <summary>Pure transition policy; illegal state changes are rejected rather than applied.</summary>
public static class OrderLifecycle
{
    private static readonly OrderLifecycleTransition[] TransitionArray =
    [
        new(OrderLifecycleState.Draft, OrderLifecycleState.Validated),
        new(OrderLifecycleState.Draft, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Validated, OrderLifecycleState.Prepared),
        new(OrderLifecycleState.Validated, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Prepared, OrderLifecycleState.Armed),
        new(OrderLifecycleState.Prepared, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Prepared, OrderLifecycleState.Expired),
        new(OrderLifecycleState.Armed, OrderLifecycleState.Releasing),
        new(OrderLifecycleState.Armed, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.Armed, OrderLifecycleState.Expired),
        new(OrderLifecycleState.Releasing, OrderLifecycleState.Acknowledging),
        new(OrderLifecycleState.Releasing, OrderLifecycleState.Armed),
        new(OrderLifecycleState.Releasing, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.Releasing, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.Working),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.PartiallyFilled),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.Filled),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.Expired),
        new(OrderLifecycleState.Acknowledging, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.Working, OrderLifecycleState.PartiallyFilled),
        new(OrderLifecycleState.Working, OrderLifecycleState.Filled),
        new(OrderLifecycleState.Working, OrderLifecycleState.PendingCancel),
        new(OrderLifecycleState.Working, OrderLifecycleState.PendingReplace),
        new(OrderLifecycleState.Working, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.Working, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Working, OrderLifecycleState.Expired),
        new(OrderLifecycleState.Working, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.PartiallyFilled, OrderLifecycleState.Filled),
        new(OrderLifecycleState.PartiallyFilled, OrderLifecycleState.PendingCancel),
        new(OrderLifecycleState.PartiallyFilled, OrderLifecycleState.PendingReplace),
        new(OrderLifecycleState.PartiallyFilled, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.PartiallyFilled, OrderLifecycleState.Expired),
        new(OrderLifecycleState.PartiallyFilled, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.Working),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.PartiallyFilled),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.Filled),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.Expired),
        new(OrderLifecycleState.PendingCancel, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.Working),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.PartiallyFilled),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.Filled),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.Expired),
        new(OrderLifecycleState.PendingReplace, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.Unknown, OrderLifecycleState.Reconciling),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Working),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.PartiallyFilled),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Filled),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Cancelled),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Rejected),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Expired),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Unknown),
        new(OrderLifecycleState.Reconciling, OrderLifecycleState.Reconciled),
        new(OrderLifecycleState.Filled, OrderLifecycleState.Reconciled),
        new(OrderLifecycleState.Cancelled, OrderLifecycleState.Reconciled),
        new(OrderLifecycleState.Rejected, OrderLifecycleState.Reconciled),
        new(OrderLifecycleState.Expired, OrderLifecycleState.Reconciled),
    ];

    private static readonly ReadOnlyCollection<OrderLifecycleTransition> TransitionView =
        Array.AsReadOnly(TransitionArray);

    /// <summary>Gets every directed edge admitted by the slice-1 state machine.</summary>
    public static IReadOnlyList<OrderLifecycleTransition> LegalTransitions => TransitionView;

    /// <summary>Gets whether a transition is explicitly allowed.</summary>
    public static bool CanTransition(OrderLifecycleState from, OrderLifecycleState to)
    {
        foreach (var transition in TransitionArray)
        {
            if (transition.From == from && transition.To == to)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets whether a semantic event kind is permitted to apply one declared state edge. This binds
    /// lifecycle shape to causation so a generically legal edge cannot be mislabeled in the ledger.
    /// </summary>
    public static bool CanApplyEvent(
        OrderEventKind kind,
        OrderLifecycleState from,
        OrderLifecycleState to)
    {
        if (from == to)
        {
            return kind switch
            {
                OrderEventKind.FillReceived =>
                    to is OrderLifecycleState.PartiallyFilled or
                        OrderLifecycleState.PendingCancel or
                        OrderLifecycleState.PendingReplace,
                OrderEventKind.VenueAcknowledged =>
                    to is OrderLifecycleState.Working or
                        OrderLifecycleState.PartiallyFilled or
                        OrderLifecycleState.PendingCancel or
                        OrderLifecycleState.PendingReplace or
                        OrderLifecycleState.Filled or
                        OrderLifecycleState.Cancelled or
                        OrderLifecycleState.Rejected or
                        OrderLifecycleState.Expired,
                OrderEventKind.RecoveryObserved => to == OrderLifecycleState.Prepared,
                OrderEventKind.ReplaceRiskAccepted or OrderEventKind.ReplaceRiskRejected =>
                    to is OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled,
                OrderEventKind.CommissionObserved or OrderEventKind.PositionObserved =>
                    to is OrderLifecycleState.Acknowledging or
                        OrderLifecycleState.Working or
                        OrderLifecycleState.PartiallyFilled or
                        OrderLifecycleState.PendingCancel or
                        OrderLifecycleState.PendingReplace or
                        OrderLifecycleState.Filled or
                        OrderLifecycleState.Cancelled or
                        OrderLifecycleState.Rejected or
                        OrderLifecycleState.Expired or
                        OrderLifecycleState.Unknown or
                        OrderLifecycleState.Reconciling or
                        OrderLifecycleState.Reconciled,
                _ => false,
            };
        }

        if (!CanTransition(from, to))
            return false;

        return kind switch
        {
            OrderEventKind.RiskAccepted =>
                from == OrderLifecycleState.Draft && to == OrderLifecycleState.Validated,
            OrderEventKind.RiskRejected =>
                from == OrderLifecycleState.Draft && to == OrderLifecycleState.Rejected,
            OrderEventKind.ValidationRejected =>
                from is OrderLifecycleState.Draft or
                    OrderLifecycleState.Validated or
                    OrderLifecycleState.Prepared &&
                to == OrderLifecycleState.Rejected,
            OrderEventKind.Prepared =>
                from == OrderLifecycleState.Validated && to == OrderLifecycleState.Prepared,
            OrderEventKind.Armed =>
                from == OrderLifecycleState.Prepared && to == OrderLifecycleState.Armed,
            OrderEventKind.SendStarted =>
                from == OrderLifecycleState.Armed && to == OrderLifecycleState.Releasing,
            OrderEventKind.SendFailedBeforeAcceptance =>
                from == OrderLifecycleState.Releasing && to == OrderLifecycleState.Armed,
            OrderEventKind.SubmissionRecorded =>
                from == OrderLifecycleState.Releasing && to == OrderLifecycleState.Acknowledging,
            OrderEventKind.VenueAcknowledged =>
                from is OrderLifecycleState.Acknowledging or OrderLifecycleState.Reconciling &&
                to == OrderLifecycleState.Working,
            OrderEventKind.FillReceived =>
                from is OrderLifecycleState.Acknowledging or
                    OrderLifecycleState.Working or
                    OrderLifecycleState.PartiallyFilled or
                    OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace or
                    OrderLifecycleState.Reconciling &&
                to is OrderLifecycleState.PartiallyFilled or OrderLifecycleState.Filled,
            OrderEventKind.CancelRequested =>
                from is OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled &&
                to == OrderLifecycleState.PendingCancel,
            OrderEventKind.CancelConfirmed =>
                from is OrderLifecycleState.Armed or
                    OrderLifecycleState.Acknowledging or
                    OrderLifecycleState.Working or
                    OrderLifecycleState.PartiallyFilled or
                    OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace or
                    OrderLifecycleState.Reconciling &&
                to == OrderLifecycleState.Cancelled,
            OrderEventKind.ReplaceRequested =>
                from is OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled &&
                to == OrderLifecycleState.PendingReplace,
            OrderEventKind.ReplaceConfirmed =>
                from == OrderLifecycleState.PendingReplace &&
                to is OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled,
            OrderEventKind.VenueRejected =>
                from is OrderLifecycleState.Releasing or
                    OrderLifecycleState.Acknowledging or
                    OrderLifecycleState.Working or
                    OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace or
                    OrderLifecycleState.Reconciling &&
                to == OrderLifecycleState.Rejected,
            OrderEventKind.Expired =>
                from is OrderLifecycleState.Prepared or
                    OrderLifecycleState.Armed or
                    OrderLifecycleState.Acknowledging or
                    OrderLifecycleState.Working or
                    OrderLifecycleState.PartiallyFilled or
                    OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace or
                    OrderLifecycleState.Reconciling &&
                to == OrderLifecycleState.Expired,
            OrderEventKind.OutcomeUnknown =>
                from is OrderLifecycleState.Releasing or
                    OrderLifecycleState.Acknowledging or
                    OrderLifecycleState.Working or
                    OrderLifecycleState.PartiallyFilled or
                    OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace or
                    OrderLifecycleState.Reconciling &&
                to == OrderLifecycleState.Unknown,
            OrderEventKind.ReconciliationStarted =>
                from == OrderLifecycleState.Unknown && to == OrderLifecycleState.Reconciling,
            OrderEventKind.Reconciled =>
                from is OrderLifecycleState.Reconciling or
                    OrderLifecycleState.Filled or
                    OrderLifecycleState.Cancelled or
                    OrderLifecycleState.Rejected or
                    OrderLifecycleState.Expired &&
                to == OrderLifecycleState.Reconciled,
            OrderEventKind.RecoveryObserved =>
                from is OrderLifecycleState.PendingCancel or
                    OrderLifecycleState.PendingReplace &&
                to is OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled,
            _ => false,
        };
    }

    /// <summary>
    /// Gets whether an event kind may originate from the supplied subsystem. This prevents a
    /// syntactically legal lifecycle edge from impersonating risk, venue, recovery, or reconciliation.
    /// </summary>
    public static bool IsEventSourceAllowed(OrderEventKind kind, OrderEventSource source) =>
        kind switch
        {
            OrderEventKind.DraftCreated or
            OrderEventKind.ValidationRejected or
            OrderEventKind.Prepared or
            OrderEventKind.Armed or
            OrderEventKind.SendStarted or
            OrderEventKind.SubmissionRecorded or
            OrderEventKind.CancelRequested or
            OrderEventKind.ReplaceRequested => source == OrderEventSource.Command,
            OrderEventKind.RiskAccepted or
            OrderEventKind.RiskRejected or
            OrderEventKind.ReplaceRiskAccepted or
            OrderEventKind.ReplaceRiskRejected => source == OrderEventSource.Risk,
            OrderEventKind.SendFailedBeforeAcceptance or
            OrderEventKind.VenueAcknowledged or
            OrderEventKind.FillReceived or
            OrderEventKind.CancelConfirmed or
            OrderEventKind.ReplaceConfirmed or
            OrderEventKind.VenueRejected or
            OrderEventKind.Expired => source == OrderEventSource.SimulatedVenue,
            OrderEventKind.OutcomeUnknown =>
                source is OrderEventSource.SimulatedVenue or OrderEventSource.Recovery,
            OrderEventKind.ReconciliationStarted or OrderEventKind.Reconciled =>
                source == OrderEventSource.Reconciliation,
            OrderEventKind.RecoveryObserved =>
                source is OrderEventSource.Recovery or OrderEventSource.SimulatedVenue,
            OrderEventKind.CommissionObserved or OrderEventKind.PositionObserved =>
                source == OrderEventSource.SimulatedVenue,
            _ => false,
        };

    /// <summary>Gets whether a state forbids submission retry until reconciliation resolves it.</summary>
    public static bool BlocksRetry(OrderLifecycleState state) =>
        state is OrderLifecycleState.Unknown or OrderLifecycleState.Reconciling;

    /// <summary>
    /// Gets whether no later economic transition is allowed. Economic final states may still add
    /// the administrative evidence-only transition to Reconciled.
    /// </summary>
    public static bool IsTerminal(OrderLifecycleState state) =>
        state is OrderLifecycleState.Filled or
            OrderLifecycleState.Cancelled or
            OrderLifecycleState.Rejected or
            OrderLifecycleState.Expired or
            OrderLifecycleState.Reconciled;
}
