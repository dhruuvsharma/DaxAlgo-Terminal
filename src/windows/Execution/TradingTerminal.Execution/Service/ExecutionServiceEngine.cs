using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Service;

/// <summary>
/// Lease-fenced, simulation-only service surface over the existing OMS, durable ledger,
/// coordinator, adapter scheduler, and reconciliation engine. It contains no pipe or UI logic.
/// </summary>
public sealed class ExecutionServiceEngine
{
    private const int MaximumEventsPerExchange = 256;

    private readonly IOrderEventStore _ledger;
    private readonly OrderManagementService _oms;
    private readonly ExecutionCoordinator _coordinator;
    private readonly ControllableAdapterEventScheduler _scheduler;
    private readonly ExecutionLease _lease;

    /// <summary>Creates the service boundary around already-composed slice 1-3/6 components.</summary>
    public ExecutionServiceEngine(
        IOrderEventStore ledger,
        OrderManagementService oms,
        ExecutionCoordinator coordinator,
        ControllableAdapterEventScheduler scheduler,
        ExecutionLease lease)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _oms = oms ?? throw new ArgumentNullException(nameof(oms));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
        if (!_coordinator.Accounts.Contains(_lease.Grant.Account))
            throw new ArgumentException("The lease account is not registered with the coordinator.", nameof(lease));
    }

    /// <summary>Gets the one simulation account served by this engine instance.</summary>
    public BrokerExecutionAccount Account => _lease.Grant.Account;

    /// <summary>Gets the current same-machine lease generation presented to authenticated clients.</summary>
    public ExecutionLeaseGrant LeaseGrant => _lease.Grant;

    /// <summary>
    /// Executes one request and captures a bounded ledger-event batch. A UI disconnect does not
    /// dispose this engine, its working orders, or its reconciliation state.
    /// </summary>
    public ExecutionServiceExchange Handle(ExecutionServiceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        ExecutionServiceFault fault;
        OrderLifecycleState? state = null;
        string? reason = null;
        try
        {
            if (!request.HasValidEnvelope)
            {
                fault = ExecutionServiceFault.InvalidRequest;
                reason = "The request envelope is invalid.";
            }
            else if (request.ProtocolVersion != ExecutionServiceProtocol.CurrentVersion)
            {
                fault = ExecutionServiceFault.ProtocolVersionMismatch;
                reason = $"Protocol version mismatch. Service={ExecutionServiceProtocol.CurrentVersion}; client={request.ProtocolVersion}.";
            }
            else if (request.Account != Account)
            {
                fault = ExecutionServiceFault.InvalidAccount;
                reason = "The request names a different execution adapter/account.";
            }
            else if (request.Kind is ExecutionServiceRequestKind.Status or ExecutionServiceRequestKind.Resync)
            {
                fault = request.Kind == ExecutionServiceRequestKind.Status && !_lease.CanAdmitNewOrders
                    ? ExecutionServiceFault.LeaseLost
                    : ExecutionServiceFault.None;
                reason = fault == ExecutionServiceFault.LeaseLost
                    ? "The service writer lease is no longer active; ledger resync remains available."
                    : null;
            }
            else
            {
                var presented = new ExecutionLeaseGrant(
                    request.Account,
                    request.ExecutionLeaseId,
                    request.FencingToken);
                var admitted = _lease.Execute(presented, static () => true);
                if (!admitted.IsSuccess)
                {
                    fault = MapLeaseFault(admitted.Fault);
                    reason = admitted.Reason;
                }
                else
                {
                    // Do not hold the mutex-owner thread while a coordinator account worker may
                    // itself need the lease. Every mutation below is fenced at its actual commit
                    // boundary, avoiding a lease-owner/account-worker lock inversion.
                    var mutation = ExecuteMutation(request, presented);
                    fault = mutation.Fault;
                    state = mutation.State;
                    reason = mutation.Reason;
                }
            }
        }
        catch (Exception exception)
        {
            fault = ExecutionServiceFault.InternalFailure;
            reason = exception.Message;
        }

        return CreateExchange(request, fault, state, reason);
    }

    private MutationResult ExecuteMutation(
        ExecutionServiceRequest request,
        in ExecutionLeaseGrant presented) => request.Kind switch
    {
        ExecutionServiceRequestKind.Submit => Submit(request, presented),
        ExecutionServiceRequestKind.Cancel => Cancel(request),
        ExecutionServiceRequestKind.Replace => Replace(request),
        ExecutionServiceRequestKind.Reconcile => Reconcile(request),
        _ => new MutationResult(
            ExecutionServiceFault.InvalidRequest,
            null,
            "The request kind is not a state-mutating service operation."),
    };

    private MutationResult Submit(
        ExecutionServiceRequest request,
        in ExecutionLeaseGrant presented)
    {
        var payload = request.Submit;
        if (payload?.Instruction is null ||
            payload.Instruction.Identity.ExecutionLeaseId != request.ExecutionLeaseId ||
            payload.Instruction.Identity.FencingToken != request.FencingToken ||
            payload.Instruction.Validate() != OrderDomainFault.None)
        {
            return Invalid("The submit payload or its lease/fencing identity is invalid.");
        }

        var clientOrderId = payload.Instruction.Identity.ClientOrderId;
        var createdFence = _lease.Execute(
            presented,
            () => _oms.CreateDraft(payload.Instruction, Context(request.RequestId, "create")));
        if (!createdFence.IsSuccess)
            return LeaseFailure(createdFence.Fault, createdFence.Reason);
        var created = createdFence.Value!;
        if (!created.IsSuccess)
            return OmsFailure(created);

        var validated = _coordinator.Validate(
            request.Account,
            clientOrderId,
            payload.RiskInput,
            Context(request.RequestId, "validate"));
        if (!validated.IsSuccess)
            return OmsFailure(validated);

        var preparedFence = _lease.Execute(
            presented,
            () => _oms.Prepare(clientOrderId, Context(request.RequestId, "prepare")));
        if (!preparedFence.IsSuccess)
            return LeaseFailure(preparedFence.Fault, preparedFence.Reason);
        var prepared = preparedFence.Value!;
        if (!prepared.IsSuccess)
            return OmsFailure(prepared);

        var armed = _coordinator.Arm(
            request.Account,
            clientOrderId,
            Context(request.RequestId, "arm"));
        if (!armed.IsSuccess)
            return OmsFailure(armed);

        var released = _coordinator.ReleaseAsync(
            request.Account,
            clientOrderId,
            Context(request.RequestId, "release")).GetAwaiter().GetResult();
        if (!released.IsSuccess)
            return CoordinatorFailure(released);

        _scheduler.RunAll();
        var final = _oms.GetProjection(clientOrderId);
        return final.IsSuccess
            ? new MutationResult(ExecutionServiceFault.None, final.Projection!.State, null)
            : OmsFailure(final);
    }

    private MutationResult Cancel(ExecutionServiceRequest request)
    {
        if (request.Cancel is not { ClientOrderId.IsValid: true } payload)
            return Invalid("The cancel payload is invalid.");

        var result = _coordinator.CancelAsync(
            request.Account,
            payload.ClientOrderId,
            Context(request.RequestId, "cancel")).GetAwaiter().GetResult();
        if (!result.IsSuccess)
            return CoordinatorFailure(result);

        _scheduler.RunAll();
        var final = _oms.GetProjection(payload.ClientOrderId);
        return final.IsSuccess
            ? new MutationResult(ExecutionServiceFault.None, final.Projection!.State, null)
            : OmsFailure(final);
    }

    private MutationResult Replace(ExecutionServiceRequest request)
    {
        if (request.Replace is not { ClientOrderId.IsValid: true } payload ||
            payload.Terms.Validate() != OrderDomainFault.None)
        {
            return Invalid("The replacement payload is invalid.");
        }

        var result = _coordinator.ReplaceAsync(
            request.Account,
            payload.ClientOrderId,
            payload.Terms,
            payload.RiskInput,
            Context(request.RequestId, "replace")).GetAwaiter().GetResult();
        if (!result.IsSuccess)
            return CoordinatorFailure(result);

        _scheduler.RunAll();
        var final = _oms.GetProjection(payload.ClientOrderId);
        return final.IsSuccess
            ? new MutationResult(ExecutionServiceFault.None, final.Projection!.State, null)
            : OmsFailure(final);
    }

    private MutationResult Reconcile(ExecutionServiceRequest request)
    {
        if (!request.ReconciliationTrigger.HasValue ||
            !Enum.IsDefined(request.ReconciliationTrigger.Value))
        {
            return Invalid("The reconciliation trigger is invalid.");
        }

        var result = _coordinator.RunReconciliationAsync(
            request.Account,
            request.ReconciliationTrigger.Value).GetAwaiter().GetResult();
        return result.IsSuccess
            ? new MutationResult(ExecutionServiceFault.None, null, null)
            : new MutationResult(
                ExecutionServiceFault.ReconciliationFailed,
                null,
                result.Reason ?? result.Fault.ToString());
    }

    private ExecutionServiceExchange CreateExchange(
        ExecutionServiceRequest request,
        ExecutionServiceFault fault,
        OrderLifecycleState? state,
        string? reason)
    {
        var events = _ledger.ReadOutbox(request.AfterOutboxSequence)
            .Take(MaximumEventsPerExchange)
            .Select(item => new ExecutionServiceEvent(item.OutboxSequence, item.Event))
            .ToArray();
        var last = events.Length == 0 ? request.AfterOutboxSequence : events[^1].OutboxSequence;
        var response = new ExecutionServiceResponse(
            ExecutionServiceProtocol.CurrentVersion,
            request.RequestId ?? string.Empty,
            fault,
            Account,
            LeaseGrant.LeaseId,
            LeaseGrant.FencingToken,
            state,
            last,
            events.Length,
            reason);
        return new ExecutionServiceExchange(response, Array.AsReadOnly(events));
    }

    private static OrderCommandContext Context(string requestId, string operation) =>
        new(
            new CausationId($"ipc:{requestId}:{operation}"),
            new DeduplicationKey($"ipc:{requestId}:{operation}"));

    private static MutationResult Invalid(string reason) =>
        new(ExecutionServiceFault.InvalidRequest, null, reason);

    private static MutationResult LeaseFailure(ExecutionLeaseFault fault, string? reason) =>
        new(MapLeaseFault(fault), null, reason ?? fault.ToString());

    private static MutationResult OmsFailure(in OmsCommandResult result) =>
        new(
            result.Fault == OmsCommandFault.LeaseRejected
                ? ExecutionServiceFault.StaleFencingToken
                : ExecutionServiceFault.OmsRejected,
            result.Projection?.State,
            result.Reason ?? result.Fault.ToString());

    private static MutationResult CoordinatorFailure(in ExecutionCoordinatorResult result) =>
        new(
            result.Fault == ExecutionCoordinatorFault.LeaseRejected
                ? ExecutionServiceFault.StaleFencingToken
                : result.Fault == ExecutionCoordinatorFault.AdapterRejected
                    ? ExecutionServiceFault.AdapterRejected
                    : ExecutionServiceFault.OmsRejected,
            result.OmsResult.Projection?.State,
            result.Reason ?? result.OmsResult.Reason ?? result.Fault.ToString());

    private static ExecutionServiceFault MapLeaseFault(ExecutionLeaseFault fault) => fault switch
    {
        ExecutionLeaseFault.GateUnavailable => ExecutionServiceFault.LeaseUnavailable,
        ExecutionLeaseFault.LeaseLost => ExecutionServiceFault.LeaseLost,
        ExecutionLeaseFault.StaleFencingToken => ExecutionServiceFault.StaleFencingToken,
        ExecutionLeaseFault.StoreRejected => ExecutionServiceFault.LeasePersistenceFailure,
        ExecutionLeaseFault.InvalidInput => ExecutionServiceFault.InvalidRequest,
        _ => ExecutionServiceFault.InternalFailure,
    };

    private readonly record struct MutationResult(
        ExecutionServiceFault Fault,
        OrderLifecycleState? State,
        string? Reason);
}
