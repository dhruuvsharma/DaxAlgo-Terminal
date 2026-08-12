using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Execution.Oms;

/// <summary>Configured result of the slice-1 simulated submit operation (ADR D7).</summary>
public enum VenueSubmitOutcome : byte
{
    /// <summary>The simulated venue accepts the order and emits its configured fills.</summary>
    Accepted = 0,

    /// <summary>The simulated venue observably rejects the order.</summary>
    Rejected = 1,

    /// <summary>No acceptance occurred, so a retry with the same client id is safe.</summary>
    FailedBeforeAcceptance = 2,

    /// <summary>Acceptance cannot be proved either way; blind retry is forbidden.</summary>
    Unknown = 3,
}

/// <summary>Semantic outcome of one command sent through the narrow venue seam.</summary>
public enum VenueCommandStatus : byte
{
    /// <summary>The command was accepted.</summary>
    Accepted = 0,

    /// <summary>The command or order was observably rejected.</summary>
    Rejected = 1,

    /// <summary>The submit failed with proof that the venue did not accept it.</summary>
    FailedBeforeAcceptance = 2,

    /// <summary>The venue outcome is not provably known.</summary>
    Unknown = 3,

    /// <summary>The same idempotent command was seen and its original callbacks were replayed.</summary>
    IdempotentReplay = 4,

    /// <summary>The client-order id was reused for a different instruction.</summary>
    Conflict = 5,
}

/// <summary>Fault-as-value diagnostics returned by the slice-1 venue seam.</summary>
public enum VenueCommandFault : byte
{
    /// <summary>No structural fault occurred.</summary>
    None = 0,

    /// <summary>The instruction or one of its strongly typed identities is invalid.</summary>
    InvalidInstruction = 1,

    /// <summary>The injected command causation identity is invalid.</summary>
    InvalidCausationId = 2,

    /// <summary>The venue cannot represent the requested capability without a downgrade.</summary>
    UnsupportedCapability = 3,

    /// <summary>The configured deterministic fill plan is internally inconsistent.</summary>
    InvalidPlan = 4,

    /// <summary>No simulated order exists for the supplied client id.</summary>
    OrderNotFound = 5,

    /// <summary>The command cannot operate on the order's terminal state.</summary>
    TerminalOrder = 6,

    /// <summary>The replacement terms are invalid or conflict with already-filled quantity.</summary>
    InvalidReplacement = 7,

    /// <summary>An unknown submit outcome must be reconciled before another economic command.</summary>
    OutcomeUnknown = 8,

    /// <summary>The client-order id is already bound to a different canonical instruction.</summary>
    IdempotencyConflict = 9,
}

/// <summary>Callback fact emitted only by the deterministic simulated venue.</summary>
public enum VenueEventKind : byte
{
    /// <summary>The venue accepted the order.</summary>
    Acknowledged = 0,

    /// <summary>The venue observably rejected the order.</summary>
    Rejected = 1,

    /// <summary>Submission failed before venue acceptance.</summary>
    FailedBeforeAcceptance = 2,

    /// <summary>The submission outcome is unknown and blocks retry.</summary>
    OutcomeUnknown = 3,

    /// <summary>An exact fill occurred.</summary>
    Fill = 4,

    /// <summary>Cancellation was confirmed.</summary>
    Cancelled = 5,

    /// <summary>Replacement terms were confirmed.</summary>
    Replaced = 6,

    /// <summary>The venue expired the order.</summary>
    Expired = 7,
}

/// <summary>
/// One immutable simulated-venue callback. It contains no broker SDK type and performs no I/O;
/// causation, deduplication, exact economics, and time are explicit (roadmap sections 6 and 13.4).
/// </summary>
public sealed record VenueEvent(
    VenueEventKind Kind,
    ClientOrderId ClientOrderId,
    BrokerOrderId? BrokerOrderId,
    ExchangeOrderId? ExchangeOrderId,
    FillExecution? Fill,
    CanonicalOrderTerms? ReplacementTerms,
    DateTime OccurredAtUtc,
    CausationId CausationId,
    DeduplicationKey DeduplicationKey,
    string? Reason = null);

/// <summary>
/// Immutable per-client deterministic submit plan. Fill chunks are defensively copied so later
/// caller mutation cannot alter replayed economics.
/// </summary>
public sealed class VenueSubmitPlan
{
    /// <summary>Creates one immutable plan for a specific idempotency key.</summary>
    public VenueSubmitPlan(
        ClientOrderId clientOrderId,
        VenueSubmitOutcome outcome,
        IEnumerable<FillExecution>? fills = null,
        bool fillBeforeAcknowledgement = false,
        string? reason = null,
        BrokerOrderId? brokerOrderId = null,
        ExchangeOrderId? exchangeOrderId = null)
    {
        ClientOrderId = clientOrderId;
        Outcome = outcome;
        Fills = Array.AsReadOnly(fills?.ToArray() ?? []);
        FillBeforeAcknowledgement = fillBeforeAcknowledgement;
        Reason = reason;
        BrokerOrderId = brokerOrderId;
        ExchangeOrderId = exchangeOrderId;
    }

    /// <summary>Gets the client-order id this plan controls.</summary>
    public ClientOrderId ClientOrderId { get; }

    /// <summary>Gets the deterministic submit outcome.</summary>
    public VenueSubmitOutcome Outcome { get; }

    /// <summary>Gets immutable exact fill chunks, in callback order.</summary>
    public IReadOnlyList<FillExecution> Fills { get; }

    /// <summary>Gets whether fills are emitted before the acknowledgement callback.</summary>
    public bool FillBeforeAcknowledgement { get; }

    /// <summary>Gets an optional deterministic rejection or failure reason.</summary>
    public string? Reason { get; }

    /// <summary>Gets an optional explicitly configured simulated broker-order id.</summary>
    public BrokerOrderId? BrokerOrderId { get; }

    /// <summary>Gets an optional explicitly configured simulated exchange-order id.</summary>
    public ExchangeOrderId? ExchangeOrderId { get; }
}

/// <summary>Current exact state returned by the in-process simulated venue query seam.</summary>
public sealed record VenueOrderSnapshot(
    CanonicalOrderInstruction Instruction,
    CanonicalOrderTerms CurrentTerms,
    OrderLifecycleState State,
    BrokerOrderId? BrokerOrderId,
    ExchangeOrderId? ExchangeOrderId,
    ScaledQuantity FilledQuantity);

/// <summary>Immutable command result with value faults and zero or more venue callbacks.</summary>
public sealed class VenueCommandResult
{
    internal VenueCommandResult(
        VenueCommandStatus status,
        VenueCommandStatus originalStatus,
        VenueCommandFault fault,
        IEnumerable<VenueEvent>? events,
        VenueOrderSnapshot? order)
    {
        Status = status;
        OriginalStatus = originalStatus;
        Fault = fault;
        Events = Array.AsReadOnly(events?.ToArray() ?? []);
        Order = order;
    }

    /// <summary>Gets this invocation's status, including idempotent replay.</summary>
    public VenueCommandStatus Status { get; }

    /// <summary>Gets the original status when <see cref="Status"/> is an idempotent replay.</summary>
    public VenueCommandStatus OriginalStatus { get; }

    /// <summary>Gets the structural fault, if any.</summary>
    public VenueCommandFault Fault { get; }

    /// <summary>Gets immutable callbacks in the exact order in which the simulator emitted them.</summary>
    public IReadOnlyList<VenueEvent> Events { get; }

    /// <summary>Gets the current simulated venue state when an order is known.</summary>
    public VenueOrderSnapshot? Order { get; }

    /// <summary>Gets the economic status after removing the replay wrapper.</summary>
    public VenueCommandStatus EffectiveStatus =>
        Status == VenueCommandStatus.IdempotentReplay ? OriginalStatus : Status;
}

/// <summary>Read-only query result for the narrow venue seam.</summary>
public readonly record struct VenueQueryResult(
    bool Found,
    VenueCommandFault Fault,
    VenueOrderSnapshot? Order);

/// <summary>
/// Deterministic exact-arithmetic venue core promoted into the slice-3 adapter by composition. This
/// sealed type is in-memory only: it has no broker client, socket, network, filesystem, or other
/// live-order dependency (ADR D7).
/// </summary>
public sealed class DeterministicSimulatedVenue
{
    private readonly object _gate = new();
    private readonly IClock _clock;
    private readonly Dictionary<ClientOrderId, VenueSubmitPlan> _plans;
    private readonly Dictionary<ClientOrderId, StoredAttempt> _attempts = [];

    /// <summary>Creates a simulator with all canonical capabilities and optional per-client plans.</summary>
    public DeterministicSimulatedVenue(IClock clock, IEnumerable<VenueSubmitPlan>? plans = null)
        : this(clock, VenueCapabilities.All, plans)
    {
    }

    /// <summary>Creates a simulator with immutable capabilities and per-client plans.</summary>
    public DeterministicSimulatedVenue(
        IClock clock,
        VenueCapabilities capabilities,
        IEnumerable<VenueSubmitPlan>? plans = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        Capabilities = capabilities;
        _plans = [];

        if (plans is null)
            return;

        foreach (var plan in plans)
        {
            ArgumentNullException.ThrowIfNull(plan);
            if (!plan.ClientOrderId.IsValid)
                throw new ArgumentException("Every simulated venue plan requires a valid client-order id.", nameof(plans));
            if (!_plans.TryAdd(plan.ClientOrderId, plan))
                throw new ArgumentException("Only one simulated venue plan is allowed per client-order id.", nameof(plans));
        }
    }

    /// <summary>Gets the canonical capabilities enforced by the simulated venue core.</summary>
    public VenueCapabilities Capabilities { get; }

    /// <summary>Adds one bounded host plan before that client-order id is first submitted.</summary>
    public bool TryAddSubmitPlan(VenueSubmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!plan.ClientOrderId.IsValid)
            return false;

        lock (_gate)
        {
            if (_attempts.ContainsKey(plan.ClientOrderId))
                return false;
            return _plans.TryAdd(plan.ClientOrderId, plan);
        }
    }

    /// <summary>Submits one exact canonical instruction under its stable client-order id.</summary>
    public VenueCommandResult Submit(CanonicalOrderInstruction instruction, CausationId causationId)
    {
        if (instruction is null || instruction.Validate() != OrderDomainFault.None)
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidInstruction);
        if (!causationId.IsValid)
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidCausationId);

        var capabilityFault = Capabilities.Validate(instruction.Terms);
        if (capabilityFault != OrderDomainFault.None)
        {
            var fault = capabilityFault is OrderDomainFault.UnsupportedOrderType or OrderDomainFault.UnsupportedTimeInForce
                ? VenueCommandFault.UnsupportedCapability
                : VenueCommandFault.InvalidInstruction;
            return Fault(VenueCommandStatus.Rejected, fault);
        }

        lock (_gate)
        {
            var clientOrderId = instruction.Identity.ClientOrderId;
            if (_attempts.TryGetValue(clientOrderId, out var prior))
            {
                if (!SameIdempotentRequest(prior.Instruction, instruction))
                {
                    return new VenueCommandResult(
                        VenueCommandStatus.Conflict,
                        VenueCommandStatus.Conflict,
                        VenueCommandFault.IdempotencyConflict,
                        null,
                        prior.Order?.Snapshot);
                }

                if (prior.Result.Status == VenueCommandStatus.FailedBeforeAcceptance)
                {
                    var retryPlan = _plans.TryGetValue(clientOrderId, out var configuredRetry)
                        ? configuredRetry
                        : new VenueSubmitPlan(clientOrderId, VenueSubmitOutcome.Accepted);
                    var retry = ExecuteSubmit(
                        instruction,
                        causationId,
                        retryPlan,
                        prior.AttemptNumber + 1,
                        out var retryOrder);
                    _attempts[clientOrderId] = new StoredAttempt(
                        instruction,
                        retry,
                        retryOrder,
                        prior.AttemptNumber + 1);
                    return retry;
                }

                return new VenueCommandResult(
                    VenueCommandStatus.IdempotentReplay,
                    prior.Result.EffectiveStatus,
                    prior.Result.Fault,
                    prior.Result.Events,
                    prior.Order?.Snapshot);
            }

            var plan = _plans.TryGetValue(clientOrderId, out var configured)
                ? configured
                : new VenueSubmitPlan(clientOrderId, VenueSubmitOutcome.Accepted);
            var result = ExecuteSubmit(instruction, causationId, plan, 1, out var order);
            _attempts.Add(clientOrderId, new StoredAttempt(instruction, result, order, 1));
            return result;
        }
    }

    /// <summary>Cancels a known simulated order.</summary>
    public VenueCommandResult Cancel(ClientOrderId clientOrderId, CausationId causationId)
    {
        if (!clientOrderId.IsValid)
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidInstruction);
        if (!causationId.IsValid)
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidCausationId);

        lock (_gate)
        {
            if (!_attempts.TryGetValue(clientOrderId, out var attempt) || attempt.Order is null)
                return Fault(VenueCommandStatus.Rejected, VenueCommandFault.OrderNotFound);

            var order = attempt.Order;
            if (order.Snapshot.State == OrderLifecycleState.Unknown)
            {
                return new VenueCommandResult(
                    VenueCommandStatus.Unknown,
                    VenueCommandStatus.Unknown,
                    VenueCommandFault.OutcomeUnknown,
                    null,
                    order.Snapshot);
            }

            if (order.CancelResult is not null)
            {
                return new VenueCommandResult(
                    VenueCommandStatus.IdempotentReplay,
                    order.CancelResult.EffectiveStatus,
                    order.CancelResult.Fault,
                    order.CancelResult.Events,
                    order.Snapshot);
            }

            if (order.Snapshot.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
                return Fault(VenueCommandStatus.Rejected, VenueCommandFault.TerminalOrder, order.Snapshot);

            var venueEvent = new VenueEvent(
                VenueEventKind.Cancelled,
                clientOrderId,
                order.Snapshot.BrokerOrderId,
                order.Snapshot.ExchangeOrderId,
                null,
                null,
                _clock.UtcNow,
                causationId,
                BuildDeduplicationKey(clientOrderId, "cancel"));
            order.Snapshot = order.Snapshot with { State = OrderLifecycleState.Cancelled };
            order.CancelResult = new VenueCommandResult(
                VenueCommandStatus.Accepted,
                VenueCommandStatus.Accepted,
                VenueCommandFault.None,
                [venueEvent],
                order.Snapshot);
            return order.CancelResult;
        }
    }

    /// <summary>Replaces exact terms on a known simulated order.</summary>
    public VenueCommandResult Replace(
        ClientOrderId clientOrderId,
        CanonicalOrderTerms replacementTerms,
        CausationId causationId)
    {
        if (!clientOrderId.IsValid)
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidInstruction);
        if (!causationId.IsValid)
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidCausationId);

        var capabilityFault = Capabilities.Validate(replacementTerms);
        if (capabilityFault != OrderDomainFault.None)
        {
            var fault = capabilityFault is OrderDomainFault.UnsupportedOrderType or OrderDomainFault.UnsupportedTimeInForce
                ? VenueCommandFault.UnsupportedCapability
                : VenueCommandFault.InvalidReplacement;
            return Fault(VenueCommandStatus.Rejected, fault);
        }

        lock (_gate)
        {
            if (!_attempts.TryGetValue(clientOrderId, out var attempt) || attempt.Order is null)
                return Fault(VenueCommandStatus.Rejected, VenueCommandFault.OrderNotFound);

            var order = attempt.Order;
            if (order.Snapshot.State == OrderLifecycleState.Unknown)
            {
                return new VenueCommandResult(
                    VenueCommandStatus.Unknown,
                    VenueCommandStatus.Unknown,
                    VenueCommandFault.OutcomeUnknown,
                    null,
                    order.Snapshot);
            }

            if (order.LastReplacement == replacementTerms && order.ReplaceResult is not null)
            {
                return new VenueCommandResult(
                    VenueCommandStatus.IdempotentReplay,
                    order.ReplaceResult.EffectiveStatus,
                    order.ReplaceResult.Fault,
                    order.ReplaceResult.Events,
                    order.Snapshot);
            }

            if (order.Snapshot.State is not (OrderLifecycleState.Working or OrderLifecycleState.PartiallyFilled))
                return Fault(VenueCommandStatus.Rejected, VenueCommandFault.TerminalOrder, order.Snapshot);
            if (replacementTerms.Side != order.Snapshot.CurrentTerms.Side ||
                !replacementTerms.Quantity.TryGetWholeUnits(out var replacementQuantity) ||
                !order.Snapshot.FilledQuantity.TryGetWholeUnits(out var filledQuantity) ||
                replacementQuantity <= filledQuantity)
            {
                return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidReplacement, order.Snapshot);
            }

            order.ReplaceSequence++;
            var venueEvent = new VenueEvent(
                VenueEventKind.Replaced,
                clientOrderId,
                order.Snapshot.BrokerOrderId,
                order.Snapshot.ExchangeOrderId,
                null,
                replacementTerms,
                _clock.UtcNow,
                causationId,
                BuildDeduplicationKey(clientOrderId, $"replace:{order.ReplaceSequence}"));
            order.Snapshot = order.Snapshot with { CurrentTerms = replacementTerms };
            order.LastReplacement = replacementTerms;
            order.ReplaceResult = new VenueCommandResult(
                VenueCommandStatus.Accepted,
                VenueCommandStatus.Accepted,
                VenueCommandFault.None,
                [venueEvent],
                order.Snapshot);
            return order.ReplaceResult;
        }
    }

    /// <summary>Queries current simulated state without changing it.</summary>
    public VenueQueryResult Query(ClientOrderId clientOrderId)
    {
        if (!clientOrderId.IsValid)
            return new VenueQueryResult(false, VenueCommandFault.InvalidInstruction, null);

        lock (_gate)
        {
            if (!_attempts.TryGetValue(clientOrderId, out var attempt) || attempt.Order is null)
                return new VenueQueryResult(false, VenueCommandFault.OrderNotFound, null);
            return new VenueQueryResult(true, VenueCommandFault.None, attempt.Order.Snapshot);
        }
    }

    private VenueCommandResult ExecuteSubmit(
        CanonicalOrderInstruction instruction,
        CausationId causationId,
        VenueSubmitPlan plan,
        int attemptNumber,
        out StoredOrder? order)
    {
        order = null;
        if (!TryValidatePlan(instruction, plan, out var plannedFilled, out var planReason))
            return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidPlan, reason: planReason);

        var clientOrderId = instruction.Identity.ClientOrderId;
        var occurredAtUtc = _clock.UtcNow;
        switch (plan.Outcome)
        {
            case VenueSubmitOutcome.FailedBeforeAcceptance:
            {
                var failed = new VenueEvent(
                    VenueEventKind.FailedBeforeAcceptance,
                    clientOrderId,
                    null,
                    null,
                    null,
                    null,
                    occurredAtUtc,
                    causationId,
                    BuildDeduplicationKey(
                        clientOrderId,
                        $"submit:attempt:{attemptNumber}:failed-before-acceptance"),
                    plan.Reason ?? "Simulated send failed before acceptance.");
                return new VenueCommandResult(
                    VenueCommandStatus.FailedBeforeAcceptance,
                    VenueCommandStatus.FailedBeforeAcceptance,
                    VenueCommandFault.None,
                    [failed],
                    null);
            }

            case VenueSubmitOutcome.Unknown:
            {
                var unknown = new VenueEvent(
                    VenueEventKind.OutcomeUnknown,
                    clientOrderId,
                    plan.BrokerOrderId,
                    plan.ExchangeOrderId,
                    null,
                    null,
                    occurredAtUtc,
                    causationId,
                    BuildDeduplicationKey(clientOrderId, "submit:unknown"),
                    plan.Reason ?? "Simulated submission outcome is unknown; retry is blocked.");
                var snapshot = new VenueOrderSnapshot(
                    instruction,
                    instruction.Terms,
                    OrderLifecycleState.Unknown,
                    plan.BrokerOrderId,
                    plan.ExchangeOrderId,
                    ScaledQuantity.Zero);
                order = new StoredOrder(snapshot);
                return new VenueCommandResult(
                    VenueCommandStatus.Unknown,
                    VenueCommandStatus.Unknown,
                    VenueCommandFault.None,
                    [unknown],
                    snapshot);
            }

            case VenueSubmitOutcome.Rejected:
                return CreateRejected(instruction, causationId, plan, occurredAtUtc, out order);

            case VenueSubmitOutcome.Accepted:
                break;

            default:
                return Fault(VenueCommandStatus.Rejected, VenueCommandFault.InvalidPlan);
        }

        if (instruction.Terms.TimeInForce == CanonicalTimeInForce.FillOrKill &&
            instruction.Terms.Quantity.TryGetWholeUnits(out var requestedFok) &&
            plannedFilled != requestedFok)
        {
            return CreateRejected(
                instruction,
                causationId,
                plan,
                occurredAtUtc,
                out order,
                "Simulated FOK liquidity could not fill the entire exact quantity.");
        }

        var brokerOrderId = plan.BrokerOrderId ?? CreateSimulatedId<BrokerOrderId>(clientOrderId, "broker");
        var exchangeOrderId = plan.ExchangeOrderId ?? CreateSimulatedId<ExchangeOrderId>(clientOrderId, "exchange");
        var acknowledgement = new VenueEvent(
            VenueEventKind.Acknowledged,
            clientOrderId,
            brokerOrderId,
            exchangeOrderId,
            null,
            null,
            occurredAtUtc,
            causationId,
            BuildDeduplicationKey(clientOrderId, "submit:ack"));
        var fillEvents = new VenueEvent[plan.Fills.Count];
        for (var index = 0; index < plan.Fills.Count; index++)
        {
            fillEvents[index] = new VenueEvent(
                VenueEventKind.Fill,
                clientOrderId,
                brokerOrderId,
                exchangeOrderId,
                plan.Fills[index],
                null,
                occurredAtUtc,
                causationId,
                BuildDeduplicationKey(clientOrderId, $"submit:fill:{index + 1}"));
        }

        var events = new List<VenueEvent>(fillEvents.Length + 2);
        if (plan.FillBeforeAcknowledgement)
        {
            events.AddRange(fillEvents);
            events.Add(acknowledgement);
        }
        else
        {
            events.Add(acknowledgement);
            events.AddRange(fillEvents);
        }

        instruction.Terms.Quantity.TryGetWholeUnits(out var requestedQuantity);
        var state = plannedFilled == requestedQuantity
            ? OrderLifecycleState.Filled
            : plannedFilled > 0
                ? OrderLifecycleState.PartiallyFilled
                : OrderLifecycleState.Working;
        if (instruction.Terms.TimeInForce == CanonicalTimeInForce.ImmediateOrCancel &&
            plannedFilled < requestedQuantity)
        {
            events.Add(new VenueEvent(
                VenueEventKind.Cancelled,
                clientOrderId,
                brokerOrderId,
                exchangeOrderId,
                null,
                null,
                occurredAtUtc,
                causationId,
                BuildDeduplicationKey(clientOrderId, "submit:ioc-cancel"),
                "Simulated IOC remainder cancelled."));
            state = OrderLifecycleState.Cancelled;
        }

        var acceptedSnapshot = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            state,
            brokerOrderId,
            exchangeOrderId,
            ScaledQuantity.FromWhole(plannedFilled));
        order = new StoredOrder(acceptedSnapshot);
        return new VenueCommandResult(
            VenueCommandStatus.Accepted,
            VenueCommandStatus.Accepted,
            VenueCommandFault.None,
            events,
            acceptedSnapshot);
    }

    private static VenueCommandResult CreateRejected(
        CanonicalOrderInstruction instruction,
        CausationId causationId,
        VenueSubmitPlan plan,
        DateTime occurredAtUtc,
        out StoredOrder order,
        string? overrideReason = null)
    {
        var clientOrderId = instruction.Identity.ClientOrderId;
        var rejected = new VenueEvent(
            VenueEventKind.Rejected,
            clientOrderId,
            plan.BrokerOrderId,
            plan.ExchangeOrderId,
            null,
            null,
            occurredAtUtc,
            causationId,
            BuildDeduplicationKey(clientOrderId, "submit:rejected"),
            overrideReason ?? plan.Reason ?? "Simulated venue rejection.");
        var snapshot = new VenueOrderSnapshot(
            instruction,
            instruction.Terms,
            OrderLifecycleState.Rejected,
            plan.BrokerOrderId,
            plan.ExchangeOrderId,
            ScaledQuantity.Zero);
        order = new StoredOrder(snapshot);
        return new VenueCommandResult(
            VenueCommandStatus.Rejected,
            VenueCommandStatus.Rejected,
            VenueCommandFault.None,
            [rejected],
            snapshot);
    }

    private static bool TryValidatePlan(
        CanonicalOrderInstruction instruction,
        VenueSubmitPlan plan,
        out long totalFilled,
        out string? reason)
    {
        totalFilled = 0;
        reason = null;
        if (plan.ClientOrderId != instruction.Identity.ClientOrderId ||
            plan.BrokerOrderId is { IsValid: false } ||
            plan.ExchangeOrderId is { IsValid: false })
        {
            reason = "The simulated venue plan has invalid or mismatched identities.";
            return false;
        }

        if (plan.Outcome != VenueSubmitOutcome.Accepted && plan.Fills.Count != 0)
        {
            reason = "Only an accepted simulated submission can contain fills.";
            return false;
        }

        if (!instruction.Terms.Quantity.TryGetWholeUnits(out var requestedQuantity))
        {
            reason = "The exact requested quantity is not a positive whole quantity.";
            return false;
        }

        Int128 running = 0;
        foreach (var fill in plan.Fills)
        {
            if (!fill.IsValid || !fill.Quantity.TryGetWholeUnits(out var fillQuantity))
            {
                reason = "A simulated fill is not an exact valid whole quantity, price, and fee.";
                return false;
            }

            if (instruction.Terms.LimitPrice.HasValue &&
                (!ScaledValueMath.TryComparePositive(
                    fill.Price.Coefficient,
                    fill.Price.Scale,
                    instruction.Terms.LimitPrice.Value.Coefficient,
                    instruction.Terms.LimitPrice.Value.Scale,
                    out var priceComparison) ||
                 instruction.Terms.Side == OrderSide.Buy && priceComparison > 0 ||
                 instruction.Terms.Side == OrderSide.Sell && priceComparison < 0))
            {
                reason = "A simulated fill violates the order's exact limit price.";
                return false;
            }

            running += fillQuantity;
            if (running > requestedQuantity)
            {
                reason = "Simulated fills exceed the exact requested quantity.";
                return false;
            }
        }

        totalFilled = (long)running;
        return true;
    }

    private static bool SameIdempotentRequest(
        CanonicalOrderInstruction left,
        CanonicalOrderInstruction right)
    {
        var leftIdentity = left.Identity;
        var rightIdentity = right.Identity;
        return leftIdentity.IntentId == rightIdentity.IntentId &&
               leftIdentity.BucketId == rightIdentity.BucketId &&
               leftIdentity.LegId == rightIdentity.LegId &&
               leftIdentity.ClientOrderId == rightIdentity.ClientOrderId &&
               leftIdentity.CorrelationId == rightIdentity.CorrelationId &&
               leftIdentity.ExecutionLeaseId == rightIdentity.ExecutionLeaseId &&
               leftIdentity.FencingToken == rightIdentity.FencingToken &&
               left.TradeIntent == right.TradeIntent &&
               left.Terms == right.Terms;
    }

    private static DeduplicationKey BuildDeduplicationKey(ClientOrderId clientOrderId, string suffix)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(clientOrderId.Value));
        return new DeduplicationKey($"sim-venue:{Convert.ToHexString(hash).ToLowerInvariant()}:{suffix}");
    }

    private static TId CreateSimulatedId<TId>(ClientOrderId clientOrderId, string kind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{kind}:{clientOrderId.Value}"));
        var value = $"sim-{kind}-{Convert.ToHexString(hash).ToLowerInvariant()}";
        return typeof(TId) == typeof(BrokerOrderId)
            ? (TId)(object)new BrokerOrderId(value)
            : (TId)(object)new ExchangeOrderId(value);
    }

    private static VenueCommandResult Fault(
        VenueCommandStatus status,
        VenueCommandFault fault,
        VenueOrderSnapshot? order = null,
        string? reason = null)
    {
        _ = reason;
        return new VenueCommandResult(status, status, fault, null, order);
    }

    private sealed record StoredAttempt(
        CanonicalOrderInstruction Instruction,
        VenueCommandResult Result,
        StoredOrder? Order,
        int AttemptNumber);

    private sealed class StoredOrder(VenueOrderSnapshot snapshot)
    {
        internal VenueOrderSnapshot Snapshot { get; set; } = snapshot;

        internal VenueCommandResult? CancelResult { get; set; }

        internal CanonicalOrderTerms? LastReplacement { get; set; }

        internal VenueCommandResult? ReplaceResult { get; set; }

        internal int ReplaceSequence { get; set; }
    }
}
