using System.Collections.ObjectModel;

namespace TradingTerminal.Execution.Oms;

/// <summary>Outcome category for one transactional inbox/event/outbox append.</summary>
public enum OrderEventAppendStatus : byte
{
    /// <summary>A new immutable event and its outbox entry were appended atomically.</summary>
    Appended = 0,

    /// <summary>The exact inbox message was already committed and its original event was returned.</summary>
    ExactReplay = 1,

    /// <summary>No event, inbox record, or outbox entry was written.</summary>
    Rejected = 2,
}

/// <summary>Fault-as-value reasons an order-event append can be rejected.</summary>
public enum OrderEventAppendFault : byte
{
    /// <summary>No fault occurred.</summary>
    None = 0,

    /// <summary>The proposed event value was absent.</summary>
    MissingDraft = 1,

    /// <summary>The aggregate identity was invalid.</summary>
    InvalidAggregateId = 2,

    /// <summary>The source, event kind, or target state was not a defined slice-1 value.</summary>
    InvalidClassification = 3,

    /// <summary>The inbox key was invalid.</summary>
    InvalidDeduplicationKey = 4,

    /// <summary>The transition had no valid causation identity.</summary>
    InvalidCausationId = 5,

    /// <summary>Occurred or recorded time was not UTC, or recorded time preceded occurred time.</summary>
    InvalidTimestamp = 6,

    /// <summary>The same source and inbox key were reused for different event content.</summary>
    ConflictingDuplicate = 7,

    /// <summary>An aggregate did not begin with a canonical draft-created fact.</summary>
    InvalidInitialEvent = 8,

    /// <summary>The requested state change is not an admitted lifecycle transition.</summary>
    IllegalTransition = 9,

    /// <summary>The aggregate or global outbox sequence could not advance.</summary>
    SequenceExhausted = 10,

    /// <summary>The candidate stream could not produce a valid current-state projection.</summary>
    ProjectionRejected = 11,

    /// <summary>The event kind cannot originate from the claimed subsystem.</summary>
    InvalidEventSource = 12,
}

/// <summary>
/// Result of one roadmap section 13.1 transactional append. Exact replay is successful but does
/// not append a second event or outbox entry.
/// </summary>
/// <param name="Status">Whether a new event was appended, replayed, or rejected.</param>
/// <param name="Fault">Stable append fault; <see cref="OrderEventAppendFault.None"/> on success.</param>
/// <param name="Event">The appended or previously committed event for a successful result.</param>
/// <param name="ProjectionFault">Projection reason when <paramref name="Fault"/> is projection rejection.</param>
public readonly record struct OrderEventAppendResult(
    OrderEventAppendStatus Status,
    OrderEventAppendFault Fault,
    OrderEvent? Event,
    OrderProjectionFault ProjectionFault = OrderProjectionFault.None)
{
    /// <summary>Gets whether the inbox operation resolved to one committed immutable event.</summary>
    public bool IsSuccess => Fault == OrderEventAppendFault.None && Event is not null;

    /// <summary>Gets whether this call performed the event and outbox append.</summary>
    public bool WasAppended => Status == OrderEventAppendStatus.Appended && IsSuccess;

    /// <summary>Gets whether this call returned the prior result of an exact inbox replay.</summary>
    public bool IsExactReplay => Status == OrderEventAppendStatus.ExactReplay && IsSuccess;
}

/// <summary>
/// Immutable local outbox entry created in the same transaction or critical section as its order
/// event. External notification dispatch remains outside this persistence boundary.
/// </summary>
/// <param name="OutboxSequence">Monotonic process-local outbox position.</param>
/// <param name="Event">The immutable order fact to publish to in-process consumers.</param>
public sealed record OrderEventOutboxEntry(long OutboxSequence, OrderEvent Event);

/// <summary>
/// Persistence boundary for the append-only OMS ledger from roadmap sections 13.1 and 13.4.
/// The in-memory implementation remains the fast test double; the durable implementation owns a
/// separate SQLite WAL database behind this unchanged contract.
/// </summary>
public interface IOrderEventStore
{
    /// <summary>
    /// Atomically deduplicates the inbox fact, appends its event, and creates one outbox entry.
    /// The caller supplies recorded time so implementations never consult an ambient clock.
    /// </summary>
    OrderEventAppendResult Append(OrderEventDraft draft, DateTime recordedAtUtc);

    /// <summary>Returns an immutable point-in-time copy of one aggregate's complete event stream.</summary>
    IReadOnlyList<OrderEvent> Read(ClientOrderId aggregateId);

    /// <summary>Returns an immutable point-in-time copy after the exclusive global outbox position.</summary>
    IReadOnlyList<OrderEventOutboxEntry> ReadOutbox(long afterExclusiveSequence = 0);
}

/// <summary>
/// Read-only startup admission gate exposed by a durable ledger. Stores without a recovery phase do
/// not implement this contract and are considered ready to admit orders.
/// </summary>
public interface IExecutionAdmissionGate
{
    /// <summary>Gets whether startup recovery permits new orders to proceed to risk and arming.</summary>
    bool CanAdmitNewOrders { get; }

    /// <summary>
    /// Gets whether durable reconciliation cases permit admission after a successful startup cycle
    /// has discharged the immutable restart-recovery set.
    /// </summary>
    bool CanAdmitAfterStartupReconciliation { get; }
}

/// <summary>
/// Deterministic in-process event ledger. A single lock makes inbox deduplication, aggregate append,
/// and outbox creation atomic; it performs no I/O and has no clock, broker, socket, or network path.
/// </summary>
public sealed class InMemoryOrderEventStore : IOrderEventStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ClientOrderId, List<OrderEvent>> _streams = [];
    private readonly Dictionary<InboxIdentity, InboxRecord> _inbox = [];
    private readonly List<OrderEventOutboxEntry> _outbox = [];
    private long _lastOutboxSequence;

    /// <inheritdoc />
    public OrderEventAppendResult Append(OrderEventDraft draft, DateTime recordedAtUtc)
    {
        var basicFault = ValidateDraft(draft, recordedAtUtc);
        if (basicFault != OrderEventAppendFault.None)
            return Rejected(basicFault);

        lock (_gate)
        {
            var inboxIdentity = new InboxIdentity(draft.Source, draft.DeduplicationKey);
            if (_inbox.TryGetValue(inboxIdentity, out var prior))
            {
                return prior.Draft == draft
                    ? new OrderEventAppendResult(
                        OrderEventAppendStatus.ExactReplay,
                        OrderEventAppendFault.None,
                        prior.Event)
                    : Rejected(OrderEventAppendFault.ConflictingDuplicate);
            }

            _streams.TryGetValue(draft.AggregateId, out var stream);
            var previous = stream is { Count: > 0 } ? stream[^1] : null;
            if (!OrderLifecycle.IsEventSourceAllowed(draft.Kind, draft.Source))
                return Rejected(OrderEventAppendFault.InvalidEventSource);
            if (previous is null)
            {
                if (draft.Kind != OrderEventKind.DraftCreated ||
                    draft.StateAfter != OrderLifecycleState.Draft ||
                    draft.Instruction is null ||
                    draft.Instruction.Identity.ClientOrderId != draft.AggregateId)
                {
                    return Rejected(OrderEventAppendFault.InvalidInitialEvent);
                }
            }
            else if (!OrderEventTransitionRules.CanApply(
                         previous.StateAfter,
                         draft.StateAfter,
                         draft.Kind))
            {
                return Rejected(OrderEventAppendFault.IllegalTransition);
            }

            if (previous?.AggregateSequence == long.MaxValue || _lastOutboxSequence == long.MaxValue)
                return Rejected(OrderEventAppendFault.SequenceExhausted);

            var aggregateSequence = previous is null ? 1 : previous.AggregateSequence + 1;
            var previousHash = previous?.EventHash ?? OrderEventHash.EmptyPreviousHash;
            var eventHash = OrderEventHash.Compute(
                draft.AggregateId,
                aggregateSequence,
                draft.Kind,
                previous?.StateAfter,
                draft.StateAfter,
                draft.Source,
                draft.DeduplicationKey,
                draft.OccurredAtUtc,
                recordedAtUtc,
                draft.CausationId,
                previousHash,
                draft.Instruction,
                draft.RiskDecision,
                draft.BrokerOrderId,
                draft.ExchangeOrderId,
                draft.Fill,
                draft.ReplacementTerms,
                draft.Reconciliation,
                draft.Reason);
            var committed = new OrderEvent(
                draft.AggregateId,
                aggregateSequence,
                draft.Kind,
                previous?.StateAfter,
                draft.StateAfter,
                draft.Source,
                draft.DeduplicationKey,
                draft.OccurredAtUtc,
                recordedAtUtc,
                draft.CausationId,
                previousHash,
                eventHash,
                draft.Instruction,
                draft.RiskDecision,
                draft.BrokerOrderId,
                draft.ExchangeOrderId,
                draft.Fill,
                draft.ReplacementTerms,
                draft.Reconciliation,
                draft.Reason);

            var candidate = new OrderEvent[(stream?.Count ?? 0) + 1];
            if (stream is not null)
                stream.CopyTo(candidate, 0);
            candidate[^1] = committed;
            var projection = OrderProjector.Rebuild(candidate);
            if (!projection.IsSuccess)
            {
                return new OrderEventAppendResult(
                    OrderEventAppendStatus.Rejected,
                    OrderEventAppendFault.ProjectionRejected,
                    null,
                    projection.Fault);
            }

            if (stream is null)
            {
                stream = [];
                _streams.Add(draft.AggregateId, stream);
            }

            var outboxSequence = _lastOutboxSequence + 1;
            stream.Add(committed);
            _inbox.Add(inboxIdentity, new InboxRecord(draft, committed));
            _outbox.Add(new OrderEventOutboxEntry(outboxSequence, committed));
            _lastOutboxSequence = outboxSequence;
            return new OrderEventAppendResult(
                OrderEventAppendStatus.Appended,
                OrderEventAppendFault.None,
                committed);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OrderEvent> Read(ClientOrderId aggregateId)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(aggregateId, out var stream))
                return Array.Empty<OrderEvent>();

            return Array.AsReadOnly(stream.ToArray());
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<OrderEventOutboxEntry> ReadOutbox(long afterExclusiveSequence = 0)
    {
        lock (_gate)
        {
            if (_outbox.Count == 0 || afterExclusiveSequence >= _lastOutboxSequence)
                return Array.Empty<OrderEventOutboxEntry>();

            var copy = new List<OrderEventOutboxEntry>(_outbox.Count);
            foreach (var entry in _outbox)
            {
                if (entry.OutboxSequence > afterExclusiveSequence)
                    copy.Add(entry);
            }

            return new ReadOnlyCollection<OrderEventOutboxEntry>(copy);
        }
    }

    private static OrderEventAppendFault ValidateDraft(OrderEventDraft? draft, DateTime recordedAtUtc)
    {
        if (draft is null)
            return OrderEventAppendFault.MissingDraft;
        if (!draft.AggregateId.IsValid)
            return OrderEventAppendFault.InvalidAggregateId;
        if (!Enum.IsDefined(draft.Kind) ||
            !Enum.IsDefined(draft.StateAfter) ||
            !Enum.IsDefined(draft.Source))
        {
            return OrderEventAppendFault.InvalidClassification;
        }
        if (!draft.DeduplicationKey.IsValid)
            return OrderEventAppendFault.InvalidDeduplicationKey;
        if (!draft.CausationId.IsValid)
            return OrderEventAppendFault.InvalidCausationId;
        if (draft.OccurredAtUtc.Kind != DateTimeKind.Utc ||
            recordedAtUtc.Kind != DateTimeKind.Utc ||
            recordedAtUtc < draft.OccurredAtUtc)
        {
            return OrderEventAppendFault.InvalidTimestamp;
        }

        return OrderEventAppendFault.None;
    }

    private static OrderEventAppendResult Rejected(OrderEventAppendFault fault) =>
        new(OrderEventAppendStatus.Rejected, fault, null);

    private readonly record struct InboxIdentity(
        OrderEventSource Source,
        DeduplicationKey DeduplicationKey);

    private sealed record InboxRecord(OrderEventDraft Draft, OrderEvent Event);
}

internal static class OrderEventTransitionRules
{
    internal static bool CanApply(
        OrderLifecycleState stateBefore,
        OrderLifecycleState stateAfter,
        OrderEventKind kind)
    {
        return OrderLifecycle.CanApplyEvent(kind, stateBefore, stateAfter);
    }
}
