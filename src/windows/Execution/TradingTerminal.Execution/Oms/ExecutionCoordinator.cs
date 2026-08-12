namespace TradingTerminal.Execution.Oms;

/// <summary>Fault-as-value coordinator outcomes.</summary>
public enum ExecutionCoordinatorFault : byte
{
    /// <summary>The coordinator operation completed successfully.</summary>
    None = 0,

    /// <summary>No registered adapter/account matches the requested routing identity.</summary>
    InvalidAccount = 1,

    /// <summary>The bounded account worker refused additional work.</summary>
    WorkerQueueFull = 2,

    /// <summary>The OMS refused the requested lifecycle transition.</summary>
    OmsRejected = 3,

    /// <summary>The adapter rejected the immutable command before dispatch.</summary>
    AdapterRejected = 4,

    /// <summary>The local dispatch receipt could not be committed to the OMS ledger.</summary>
    DispatchReceiptRejected = 5,

    /// <summary>An asynchronous adapter callback was invalid or could not be committed.</summary>
    CallbackRejected = 6,

    /// <summary>The account writer lease was lost or its durable fencing generation is stale.</summary>
    LeaseRejected = 7,
}

/// <summary>Immutable result from one coordinator command.</summary>
public readonly record struct ExecutionCoordinatorResult(
    ExecutionCoordinatorFault Fault,
    OmsCommandResult OmsResult,
    BrokerAdapterCommandResult? AdapterResult = null,
    BrokerDispatchReceipt? DispatchReceipt = null,
    string? Reason = null)
{
    /// <summary>Gets whether the coordinator completed the local command path successfully.</summary>
    public bool IsSuccess => Fault == ExecutionCoordinatorFault.None && OmsResult.IsSuccess;
}

/// <summary>
/// In-process execution coordinator for slice 3. Each adapter/account owns one bounded serial worker;
/// adapter callbacks enter the same worker as commands, while different accounts share no worker or
/// lock. The type depends only on execution-domain contracts and is ready for a hosting-only slice-4
/// extraction.
/// </summary>
public sealed class ExecutionCoordinator : IDisposable
{
    private readonly object _callbackGate = new();
    private readonly OrderManagementService _oms;
    private readonly Dictionary<BrokerExecutionAccount, AdapterRegistration> _registrations;
    private readonly Dictionary<BrokerExecutionAccount, OmsCommandResult> _lastCallbackResults = [];
    private readonly Dictionary<ClientOrderId, BrokerExecutionAccount> _orderAccounts = [];
    private readonly HashSet<BrokerExecutionAccount> _startupReconciledAccounts = [];
    private readonly Dictionary<BrokerExecutionAccount, ExecutionLease> _executionLeases;
    private readonly ReconciliationEngine? _reconciliation;
    private bool _disposed;

    /// <summary>Creates a coordinator over one simulated adapter/account.</summary>
    public ExecutionCoordinator(
        OrderManagementService oms,
        IBrokerExecutionAdapter adapter,
        int workerCapacity = 64)
        : this(oms, [adapter], reconciliation: null, workerCapacity)
    {
    }

    /// <summary>Creates a coordinator with account reconciliation and dynamic admission gating.</summary>
    public ExecutionCoordinator(
        OrderManagementService oms,
        IBrokerExecutionAdapter adapter,
        ReconciliationEngine reconciliation,
        int workerCapacity = 64)
        : this(oms, [adapter], reconciliation, workerCapacity)
    {
    }

    /// <summary>Creates one bounded worker for every distinct adapter/account.</summary>
    public ExecutionCoordinator(
        OrderManagementService oms,
        IEnumerable<IBrokerExecutionAdapter> adapters,
        int workerCapacity = 64)
        : this(oms, adapters, reconciliation: null, workerCapacity)
    {
    }

    /// <summary>Creates one bounded worker per account and wires the reconciliation admission gate.</summary>
    public ExecutionCoordinator(
        OrderManagementService oms,
        IEnumerable<IBrokerExecutionAdapter> adapters,
        ReconciliationEngine? reconciliation,
        int workerCapacity = 64)
        : this(oms, adapters, reconciliation, executionLeases: null, workerCapacity)
    {
    }

    /// <summary>
    /// Creates one bounded worker per account and fences every command, callback, and reconciliation
    /// mutation through the account's same-machine execution lease.
    /// </summary>
    public ExecutionCoordinator(
        OrderManagementService oms,
        IEnumerable<IBrokerExecutionAdapter> adapters,
        ReconciliationEngine? reconciliation,
        IEnumerable<ExecutionLease>? executionLeases,
        int workerCapacity = 64)
    {
        _oms = oms ?? throw new ArgumentNullException(nameof(oms));
        _reconciliation = reconciliation;
        ArgumentNullException.ThrowIfNull(adapters);
        if (workerCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(workerCapacity));

        _registrations = [];
        foreach (var adapter in adapters)
        {
            ArgumentNullException.ThrowIfNull(adapter);
            if (!adapter.Account.IsValid)
                throw new ArgumentException("Every execution adapter requires a valid account identity.", nameof(adapters));
            var eventHandler = new Action<BrokerAdapterEvent>(
                adapterEvent => OnAdapterEvent(adapter.Account, adapterEvent));
            var registration = new AdapterRegistration(
                adapter,
                new BoundedAccountWorker(
                    workerCapacity,
                    exception => RecordCallbackFailure(adapter.Account, exception)),
                eventHandler);
            if (!_registrations.TryAdd(adapter.Account, registration))
                throw new ArgumentException("Only one execution adapter may own an adapter/account identity.", nameof(adapters));
            adapter.EventReceived += eventHandler;
        }
        if (_registrations.Count == 0)
            throw new ArgumentException("At least one execution adapter is required.", nameof(adapters));

        _executionLeases = [];
        if (executionLeases is not null)
        {
            foreach (var lease in executionLeases)
            {
                ArgumentNullException.ThrowIfNull(lease);
                if (!_registrations.ContainsKey(lease.Grant.Account) ||
                    !_executionLeases.TryAdd(lease.Grant.Account, lease))
                {
                    throw new ArgumentException(
                        "Every execution lease must uniquely match a registered adapter/account.",
                        nameof(executionLeases));
                }
            }
            if (_executionLeases.Count != _registrations.Count)
            {
                throw new ArgumentException(
                    "A fenced coordinator requires one execution lease for every registered account.",
                    nameof(executionLeases));
            }
        }

        if (_reconciliation is not null)
        {
            foreach (var registration in _registrations.Values)
            {
                try
                {
                    _ = RunReconciliationCore(registration, ReconciliationTrigger.Startup);
                }
                catch (Exception)
                {
                    _reconciliation.FailClosed(registration.Adapter.Account);
                }
            }
        }
    }

    /// <summary>Gets the registered adapter/account identities.</summary>
    public IReadOnlyCollection<BrokerExecutionAccount> Accounts => _registrations.Keys;

    /// <summary>
    /// Performs detailed adapter negotiation before risk evaluation. A mismatch becomes a durable
    /// Draft-to-Rejected transition and cannot subsequently be prepared or armed.
    /// </summary>
    public OmsCommandResult Validate(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId,
        in RiskInputSnapshot riskInput,
        in OrderCommandContext context)
    {
        if (!TryGetRegistration(account, out var registration))
            return InvalidOmsResult("No execution adapter is registered for the account.");
        if (_reconciliation is not null &&
            (!IsStartupReconciled(account) || !_reconciliation.CanAdmitNewOrders(account)))
            return ReconciliationBlocked(clientOrderId);
        if (!_oms.CanAdmitNewOrders && !HasCompletedStartupReconciliation())
            return RecoveryBlocked(clientOrderId);
        if (!TryBindOrderAccount(account, clientOrderId))
            return InvalidOmsResult("The order is already bound to another adapter/account.");
        var copiedRiskInput = riskInput;
        var copiedContext = context;
        var fenced = ExecuteMutation(
            account,
            () => _oms.ValidateForExecution(
                clientOrderId,
                copiedRiskInput,
                registration.Adapter.Session,
                registration.Adapter.Capabilities,
                copiedContext));
        return fenced.IsSuccess
            ? fenced.Value!
            : LeaseRejected(clientOrderId, fenced.Fault, fenced.Reason);
    }

    /// <summary>Revalidates the adapter immediately before recording the Armed transition.</summary>
    public OmsCommandResult Arm(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId,
        in OrderCommandContext context)
    {
        if (!TryGetRegistration(account, out var registration))
            return InvalidOmsResult("No execution adapter is registered for the account.");
        if (_reconciliation is not null &&
            (!IsStartupReconciled(account) || !_reconciliation.CanAdmitNewOrders(account)))
            return ReconciliationBlocked(clientOrderId);
        if (!_oms.CanAdmitNewOrders && !HasCompletedStartupReconciliation())
            return RecoveryBlocked(clientOrderId);
        if (!TryBindOrderAccount(account, clientOrderId))
            return InvalidOmsResult("The order is already bound to another adapter/account.");
        var copiedContext = context;
        var fenced = ExecuteMutation(
            account,
            () => _oms.ArmForExecution(
                clientOrderId,
                registration.Adapter.Session,
                registration.Adapter.Capabilities,
                copiedContext));
        return fenced.IsSuccess
            ? fenced.Value!
            : LeaseRejected(clientOrderId, fenced.Fault, fenced.Reason);
    }

    /// <summary>
    /// Opens the durable release barrier, publishes through the bounded account worker, and records
    /// the local receipt before queued adapter acknowledgement/fill events are allowed to run.
    /// </summary>
    public ValueTask<ExecutionCoordinatorResult> ReleaseAsync(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId,
        OrderCommandContext context)
    {
        if (!TryGetRegistration(account, out var registration))
            return ValueTask.FromResult(InvalidCoordinatorResult("No execution adapter is registered for the account."));
        if (!TryBindOrderAccount(account, clientOrderId))
            return ValueTask.FromResult(InvalidCoordinatorResult("The order is already bound to another adapter/account."));

        return Enqueue(
            registration,
            () =>
            {
                if (_reconciliation is not null &&
                    (!IsStartupReconciled(account) || !_reconciliation.CanAdmitNewOrders(account)))
                    return OmsRejected(ReconciliationBlocked(clientOrderId));
                if (!_oms.CanAdmitNewOrders && !HasCompletedStartupReconciliation())
                    return OmsRejected(RecoveryBlocked(clientOrderId));
                if (!TryAuthorizeLiveDispatch(
                        registration,
                        clientOrderId,
                        requiresNewOrderAdmission: true,
                        out var liveBlocked))
                    return liveBlocked;
                var before = _oms.GetProjection(clientOrderId);
                if (!before.IsSuccess || before.Projection!.State != OrderLifecycleState.Armed)
                    return OmsRejected(before);

                var started = _oms.BeginRelease(clientOrderId, context);
                if (!started.IsSuccess)
                    return OmsRejected(started);

                BrokerAdapterCommandResult adapterResult;
                try
                {
                    using var liveAdmission = CreateLiveAdmission(
                        registration,
                        BrokerAdapterCommandKind.Submit,
                        started.Projection!.Instruction,
                        default,
                        default,
                        context.CausationId,
                        registration.Adapter.Capabilities.Version);
                    adapterResult = registration.Adapter.Submit(
                        new BrokerSubmitCommand(
                            started.Projection!.Instruction,
                            context.CausationId,
                            registration.Adapter.Capabilities.Version)
                        {
                            LiveGuardrailAdmission = liveAdmission,
                        });
                }
                catch (Exception exception)
                {
                    var unknown = RecoverReleaseAsUnknown(clientOrderId, context, exception.Message);
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.AdapterRejected,
                        unknown,
                        Reason: $"The adapter submit outcome is unknown: {exception.Message}");
                }
                if (!adapterResult.IsDispatched || adapterResult.DispatchReceipt is null)
                {
                    var restored = started;
                    if (adapterResult.Status == BrokerAdapterCommandStatus.Conflict)
                    {
                        restored = RecoverReleaseAsUnknown(
                            clientOrderId,
                            context,
                            "The adapter reported an idempotency conflict.");
                    }
                    else if (adapterResult.ScheduledEventCount == 0 &&
                             adapterResult.Status == BrokerAdapterCommandStatus.RejectedBeforeDispatch)
                    {
                        restored = _oms.RecordSendRejectedBeforeDispatch(
                            clientOrderId,
                            adapterResult.Reason ?? adapterResult.Fault.ToString(),
                            context);
                    }
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.AdapterRejected,
                        restored,
                        adapterResult,
                        null,
                        adapterResult.Reason);
                }

                var receipt = adapterResult.DispatchReceipt;
                if (receipt.Account != account)
                {
                    var unknown = RecoverReleaseAsUnknown(
                        clientOrderId,
                        context,
                        "The dispatch receipt account did not match the selected worker.");
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.DispatchReceiptRejected,
                        unknown,
                        adapterResult,
                        receipt,
                        "The adapter receipt account does not match the selected account worker.");
                }
                OmsCommandResult recorded;
                try
                {
                    recorded = _oms.RecordDispatchReceipt(clientOrderId, receipt, context);
                }
                catch (Exception exception)
                {
                    var unknown = RecoverReleaseAsUnknown(clientOrderId, context, exception.Message);
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.DispatchReceiptRejected,
                        unknown,
                        adapterResult,
                        receipt,
                        $"The local dispatch receipt outcome is unknown: {exception.Message}");
                }
                if (!recorded.IsSuccess)
                {
                    var unknown = RecoverReleaseAsUnknown(
                        clientOrderId,
                        context,
                        recorded.Reason ?? recorded.Fault.ToString());
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.DispatchReceiptRejected,
                        unknown.Projection?.State == OrderLifecycleState.Unknown ? unknown : recorded,
                        adapterResult,
                        receipt,
                        recorded.Reason);
                }

                return new ExecutionCoordinatorResult(
                    ExecutionCoordinatorFault.None,
                    recorded,
                    adapterResult,
                    receipt);
            });
    }

    /// <summary>Publishes a durable pending cancellation through the matching account worker.</summary>
    public ValueTask<ExecutionCoordinatorResult> CancelAsync(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId,
        OrderCommandContext context)
    {
        if (!TryGetRegistration(account, out var registration))
            return ValueTask.FromResult(InvalidCoordinatorResult("No execution adapter is registered for the account."));
        if (!TryBindOrderAccount(account, clientOrderId))
            return ValueTask.FromResult(InvalidCoordinatorResult("The order is already bound to another adapter/account."));

        return Enqueue(
            registration,
            () =>
            {
                if (!TryAuthorizeLiveDispatch(
                        registration,
                        clientOrderId,
                        requiresNewOrderAdmission: false,
                        out var liveBlocked))
                    return liveBlocked;
                var pending = _oms.BeginCancel(clientOrderId, context);
                if (!pending.IsSuccess || pending.Projection!.State != OrderLifecycleState.PendingCancel)
                    return OmsRejected(pending);

                BrokerAdapterCommandResult adapterResult;
                try
                {
                    var order = BrokerOrderQuery.ByClientId(clientOrderId);
                    using var liveAdmission = CreateLiveAdmission(
                        registration,
                        BrokerAdapterCommandKind.Cancel,
                        null,
                        order,
                        default,
                        context.CausationId,
                        null);
                    adapterResult = registration.Adapter.Cancel(
                        new BrokerCancelCommand(
                            order,
                            context.CausationId)
                        {
                            LiveGuardrailAdmission = liveAdmission,
                        });
                }
                catch (Exception exception)
                {
                    var unknown = RecoverPendingCommandAsUnknown(
                        clientOrderId,
                        OrderLifecycleState.PendingCancel,
                        context,
                        exception.Message);
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.AdapterRejected,
                        unknown,
                        Reason: $"The adapter cancel outcome is unknown: {exception.Message}");
                }
                if (!adapterResult.IsDispatched)
                {
                    var restored = pending;
                    if (adapterResult.ScheduledEventCount == 0)
                    {
                        restored = _oms.RecordPendingCommandRejectedBeforeDispatch(
                            clientOrderId,
                            OrderLifecycleState.PendingCancel,
                            adapterResult.Reason ?? adapterResult.Fault.ToString(),
                            context);
                    }
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.AdapterRejected,
                        restored,
                        adapterResult,
                        null,
                        adapterResult.Reason);
                }

                return new ExecutionCoordinatorResult(
                    ExecutionCoordinatorFault.None,
                    pending,
                    adapterResult,
                    adapterResult.DispatchReceipt);
            });
    }

    /// <summary>Publishes a freshly risk-validated replacement through the matching account worker.</summary>
    public ValueTask<ExecutionCoordinatorResult> ReplaceAsync(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId,
        CanonicalOrderTerms replacementTerms,
        RiskInputSnapshot riskInput,
        OrderCommandContext context)
    {
        if (!TryGetRegistration(account, out var registration))
            return ValueTask.FromResult(InvalidCoordinatorResult("No execution adapter is registered for the account."));
        if (!TryBindOrderAccount(account, clientOrderId))
            return ValueTask.FromResult(InvalidCoordinatorResult("The order is already bound to another adapter/account."));

        return Enqueue(
            registration,
            () =>
            {
                if (_reconciliation is not null &&
                    (!IsStartupReconciled(account) || !_reconciliation.CanAdmitNewOrders(account)))
                    return OmsRejected(ReconciliationBlocked(clientOrderId));
                if (!_oms.CanAdmitNewOrders && !HasCompletedStartupReconciliation())
                    return OmsRejected(RecoveryBlocked(clientOrderId));
                if (!TryAuthorizeLiveDispatch(
                        registration,
                        clientOrderId,
                        requiresNewOrderAdmission: true,
                        out var liveBlocked))
                    return liveBlocked;
                var pending = _oms.BeginReplace(
                    clientOrderId,
                    replacementTerms,
                    riskInput,
                    registration.Adapter.Session,
                    registration.Adapter.Capabilities,
                    context);
                if (!pending.IsSuccess || pending.Projection!.State != OrderLifecycleState.PendingReplace)
                    return OmsRejected(pending);

                BrokerAdapterCommandResult adapterResult;
                try
                {
                    var order = BrokerOrderQuery.ByClientId(clientOrderId);
                    using var liveAdmission = CreateLiveAdmission(
                        registration,
                        BrokerAdapterCommandKind.Replace,
                        null,
                        order,
                        replacementTerms,
                        context.CausationId,
                        registration.Adapter.Capabilities.Version);
                    adapterResult = registration.Adapter.Replace(
                        new BrokerReplaceCommand(
                            order,
                            replacementTerms,
                            context.CausationId,
                            registration.Adapter.Capabilities.Version)
                        {
                            LiveGuardrailAdmission = liveAdmission,
                        });
                }
                catch (Exception exception)
                {
                    var unknown = RecoverPendingCommandAsUnknown(
                        clientOrderId,
                        OrderLifecycleState.PendingReplace,
                        context,
                        exception.Message);
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.AdapterRejected,
                        unknown,
                        Reason: $"The adapter replace outcome is unknown: {exception.Message}");
                }
                if (!adapterResult.IsDispatched)
                {
                    var restored = pending;
                    if (adapterResult.ScheduledEventCount == 0)
                    {
                        restored = _oms.RecordPendingCommandRejectedBeforeDispatch(
                            clientOrderId,
                            OrderLifecycleState.PendingReplace,
                            adapterResult.Reason ?? adapterResult.Fault.ToString(),
                            context);
                    }
                    return new ExecutionCoordinatorResult(
                        ExecutionCoordinatorFault.AdapterRejected,
                        restored,
                        adapterResult,
                        null,
                        adapterResult.Reason);
                }

                return new ExecutionCoordinatorResult(
                    ExecutionCoordinatorFault.None,
                    pending,
                    adapterResult,
                    adapterResult.DispatchReceipt);
            });
    }

    /// <summary>Queries the adapter by client or broker id without changing OMS state.</summary>
    public BrokerOrderQueryResult Query(BrokerExecutionAccount account, BrokerOrderQuery query) =>
        TryGetRegistration(account, out var registration)
            ? registration.Adapter.Query(query)
            : new BrokerOrderQueryResult(
                false,
                BrokerAdapterCommandFault.InvalidCommand,
                null,
                "No execution adapter is registered for the account.");

    /// <summary>Captures one adapter's reconciliation snapshots; no reconcile loop runs in slice 3.</summary>
    public BrokerReconciliationSnapshot? CaptureReconciliationSnapshot(BrokerExecutionAccount account) =>
        TryGetRegistration(account, out var registration)
            ? registration.Adapter.CaptureReconciliationSnapshot()
            : null;

    /// <summary>
    /// Runs one caller-driven startup, reconnect, periodic, Unknown, or operator reconciliation on
    /// the same serial account worker used by commands and callbacks.
    /// </summary>
    public ValueTask<ReconciliationCycleResult> RunReconciliationAsync(
        BrokerExecutionAccount account,
        ReconciliationTrigger trigger)
    {
        if (_reconciliation is null)
        {
            return ValueTask.FromResult(new ReconciliationCycleResult(
                ReconciliationCycleFault.InvalidInput,
                trigger,
                account,
                DateTime.UnixEpoch,
                Array.Empty<ReconciliationCase>(),
                1,
                "No reconciliation engine is attached to this coordinator."));
        }
        if (!TryGetRegistration(account, out var registration))
        {
            return ValueTask.FromResult(new ReconciliationCycleResult(
                ReconciliationCycleFault.InvalidInput,
                trigger,
                account,
                DateTime.UnixEpoch,
                Array.Empty<ReconciliationCase>(),
                1,
                "No execution adapter is registered for the account."));
        }

        return EnqueueReconciliation(
            registration,
            trigger,
            () => RunReconciliationCore(registration, trigger));
    }

    /// <summary>Returns the most recent OMS result produced by an asynchronous adapter callback.</summary>
    public OmsCommandResult? GetLastCallbackResult(BrokerExecutionAccount account)
    {
        lock (_callbackGate)
            return _lastCallbackResults.TryGetValue(account, out var result) ? result : null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var registration in _registrations.Values)
            registration.Adapter.EventReceived -= registration.EventHandler;
    }

    private ValueTask<ExecutionCoordinatorResult> Enqueue(
        AdapterRegistration registration,
        Func<ExecutionCoordinatorResult> action)
    {
        var completion = new TaskCompletionSource<ExecutionCoordinatorResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!registration.Worker.TryPost(
                () =>
                {
                    try
                    {
                        var fenced = ExecuteMutation(registration.Adapter.Account, action);
                        if (!fenced.IsSuccess)
                        {
                            completion.TrySetResult(LeaseRejectedCoordinator(
                                registration.Adapter.Account,
                                fenced.Fault,
                                fenced.Reason));
                            return;
                        }
                        var result = fenced.Value!;
                        if (_reconciliation is not null &&
                            result.OmsResult.Projection is { State: OrderLifecycleState.Unknown } unknown)
                        {
                            var cycle = RunReconciliationCore(registration, ReconciliationTrigger.UnknownOutcome);
                            if (cycle.IsSuccess)
                            {
                                result = result with { OmsResult = _oms.GetProjection(unknown.ClientOrderId) };
                            }
                            else
                            {
                                result = result with
                                {
                                    Reason = $"{result.Reason} Reconciliation failed: {cycle.Reason}".Trim(),
                                };
                            }
                        }
                        completion.TrySetResult(result);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetResult(new ExecutionCoordinatorResult(
                            ExecutionCoordinatorFault.OmsRejected,
                            InvalidOmsResult(exception.Message),
                            Reason: exception.Message));
                    }
                }))
        {
            return ValueTask.FromResult(new ExecutionCoordinatorResult(
                ExecutionCoordinatorFault.WorkerQueueFull,
                InvalidOmsResult("The bounded adapter/account worker queue is full."),
                Reason: "The bounded adapter/account worker queue is full."));
        }

        return completion.Task.IsCompletedSuccessfully
            ? ValueTask.FromResult(completion.Task.Result)
            : new ValueTask<ExecutionCoordinatorResult>(completion.Task);
    }

    private ValueTask<ReconciliationCycleResult> EnqueueReconciliation(
        AdapterRegistration registration,
        ReconciliationTrigger trigger,
        Func<ReconciliationCycleResult> action)
    {
        var completion = new TaskCompletionSource<ReconciliationCycleResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!registration.Worker.TryPost(
                () =>
                {
                    try
                    {
                        completion.TrySetResult(action());
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetResult(new ReconciliationCycleResult(
                            ReconciliationCycleFault.InvalidInput,
                            trigger,
                            registration.Adapter.Account,
                            registration.Adapter.Session.ObservedAtUtc,
                            Array.Empty<ReconciliationCase>(),
                            1,
                            exception.Message));
                    }
                }))
        {
            _reconciliation!.FailClosed(registration.Adapter.Account);
            return ValueTask.FromResult(new ReconciliationCycleResult(
                ReconciliationCycleFault.InvalidInput,
                trigger,
                registration.Adapter.Account,
                registration.Adapter.Session.ObservedAtUtc,
                Array.Empty<ReconciliationCase>(),
                1,
                "The bounded adapter/account worker queue is full."));
        }

        return completion.Task.IsCompletedSuccessfully
            ? ValueTask.FromResult(completion.Task.Result)
            : new ValueTask<ReconciliationCycleResult>(completion.Task);
    }

    private ReconciliationCycleResult RunReconciliationCore(
        AdapterRegistration registration,
        ReconciliationTrigger trigger)
    {
        var fenced = ExecuteMutation(
            registration.Adapter.Account,
            () => RunReconciliationUnfenced(registration, trigger));
        if (fenced.IsSuccess)
            return fenced.Value!;

        _reconciliation!.FailClosed(registration.Adapter.Account);
        return new ReconciliationCycleResult(
            ReconciliationCycleFault.InvalidInput,
            trigger,
            registration.Adapter.Account,
            registration.Adapter.Session.ObservedAtUtc,
            Array.Empty<ReconciliationCase>(),
            1,
            FencingReason(fenced.Fault, fenced.Reason));
    }

    private ReconciliationCycleResult RunReconciliationUnfenced(
        AdapterRegistration registration,
        ReconciliationTrigger trigger)
    {
        try
        {
            if (trigger == ReconciliationTrigger.Startup)
                NormalizeStartupUnknowns();
            var snapshot = registration.Adapter.CaptureReconciliationSnapshot();
            var localOrders = CollectLocalOrders(registration.Adapter.Account, snapshot);
            var result = _reconciliation!.RunCycle(trigger, registration.Adapter.Account, localOrders, snapshot);
            if (trigger == ReconciliationTrigger.Startup && result.IsSuccess)
            {
                lock (_callbackGate)
                    _startupReconciledAccounts.Add(registration.Adapter.Account);
            }
            return result;
        }
        catch
        {
            _reconciliation!.FailClosed(registration.Adapter.Account);
            throw;
        }
    }

    private IReadOnlyList<OrderProjection> CollectLocalOrders(
        BrokerExecutionAccount account,
        BrokerReconciliationSnapshot snapshot)
    {
        var snapshotIds = (snapshot.OpenOrders ?? Array.Empty<VenueOrderSnapshot>())
            .Concat(snapshot.CompletedOrders ?? Array.Empty<VenueOrderSnapshot>())
            .Where(item => item?.Instruction is not null)
            .Select(item => item.Instruction.Identity.ClientOrderId)
            .ToHashSet();
        var soleRegisteredAccount = _registrations.Count == 1;
        var result = new List<OrderProjection>();
        foreach (var projection in _oms.ReadAllProjections())
        {
            // Predispatch Draft/Validated/Prepared/Armed orders are not adapter-visible and their
            // absence from a broker snapshot is expected, not a broker-missing divergence.
            if (!HasExternalVisibility(projection.ClientOrderId))
                continue;

            var include = false;
            lock (_callbackGate)
            {
                if (_orderAccounts.TryGetValue(projection.ClientOrderId, out var bound))
                {
                    include = bound == account;
                }
            }
            if (!include && TryReadLedgerAccount(projection.ClientOrderId, out var durableAccount))
            {
                include = durableAccount == account;
                if (include)
                    TryBindOrderAccount(account, projection.ClientOrderId);
            }
            if (!include && snapshotIds.Contains(projection.ClientOrderId))
            {
                include = TryBindOrderAccount(account, projection.ClientOrderId);
            }
            if (!include && soleRegisteredAccount)
            {
                include = TryBindOrderAccount(account, projection.ClientOrderId);
            }
            if (include)
                result.Add(projection);
        }
        result.Sort(static (left, right) => string.CompareOrdinal(
            left.ClientOrderId.Value,
            right.ClientOrderId.Value));
        return result.Count == 0 ? Array.Empty<OrderProjection>() : Array.AsReadOnly(result.ToArray());
    }

    private void NormalizeStartupUnknowns()
    {
        foreach (var projection in _oms.ReadAllProjections())
        {
            if (projection.State is not (OrderLifecycleState.Releasing or OrderLifecycleState.Acknowledging))
                continue;
            var context = new OrderCommandContext(
                new CausationId($"startup-reconciliation:{projection.ClientOrderId.Value}"),
                new DeduplicationKey($"startup-reconciliation:{projection.ClientOrderId.Value}:{projection.LastSequence}"));
            _oms.RecoverUnacknowledgedSendAsUnknown(projection.ClientOrderId, context);
        }
    }

    private bool HasCompletedStartupReconciliation()
    {
        if (_reconciliation is null || !_oms.CanAdmitAfterStartupReconciliation)
            return false;
        bool allAccountsReconciled;
        lock (_callbackGate)
            allAccountsReconciled = _registrations.Keys.All(_startupReconciledAccounts.Contains);
        return allAccountsReconciled && !HasAmbiguousUnattributedOrders();
    }

    private bool IsStartupReconciled(BrokerExecutionAccount account)
    {
        if (!_oms.CanAdmitAfterStartupReconciliation)
            return false;
        bool reconciled;
        lock (_callbackGate)
            reconciled = _startupReconciledAccounts.Contains(account);
        return reconciled && !HasAmbiguousUnattributedOrders();
    }

    private bool HasAmbiguousUnattributedOrders()
    {
        foreach (var projection in _oms.ReadAllProjections())
        {
            if (!projection.BlocksRetry && !HasExternalVisibility(projection.ClientOrderId))
                continue;

            lock (_callbackGate)
            {
                if (_orderAccounts.ContainsKey(projection.ClientOrderId))
                    continue;
            }

            if (TryReadLedgerAccount(projection.ClientOrderId, out var durableAccount) &&
                _registrations.ContainsKey(durableAccount))
            {
                TryBindOrderAccount(durableAccount, projection.ClientOrderId);
                continue;
            }

            return true;
        }

        return false;
    }

    private bool TryReadLedgerAccount(ClientOrderId clientOrderId, out BrokerExecutionAccount account)
    {
        foreach (var orderEvent in _oms.ReadEvents(clientOrderId).Reverse())
        {
            if (orderEvent.Kind == OrderEventKind.SubmissionRecorded &&
                BrokerDispatchReceipt.TryReadAccountLedgerValue(orderEvent.Reason, out account))
            {
                return true;
            }
        }
        account = default;
        return false;
    }

    private bool HasExternalVisibility(ClientOrderId clientOrderId) =>
        _oms.ReadEvents(clientOrderId).Any(item => item.Kind is
            OrderEventKind.SendStarted or
            OrderEventKind.SubmissionRecorded or
            OrderEventKind.VenueAcknowledged or
            OrderEventKind.FillReceived or
            OrderEventKind.CancelConfirmed or
            OrderEventKind.ReplaceConfirmed or
            OrderEventKind.VenueRejected or
            OrderEventKind.Expired or
            OrderEventKind.OutcomeUnknown);

    private void OnAdapterEvent(
        BrokerExecutionAccount expectedAccount,
        BrokerAdapterEvent adapterEvent)
    {
        if (_disposed || !_registrations.TryGetValue(expectedAccount, out var registration))
            return;
        if (!IsValidAdapterEventEnvelope(expectedAccount, adapterEvent, out var reason) ||
            !IsOrderBoundToAccount(expectedAccount, adapterEvent.ClientOrderId))
        {
            RecordCallbackResult(
                expectedAccount,
                new OmsCommandResult(
                    OmsCommandFault.InvalidVenueEvent,
                    null,
                    null,
                    reason ?? "The callback order is not bound to the publishing adapter/account."));
            return;
        }

        // Economic callbacks are never dropped. A compliant adapter raises them asynchronously, so
        // bounded backpressure may block only that adapter/account producer until capacity returns.
        registration.Worker.PostWithBackpressure(() => ApplyAdapterEvent(expectedAccount, adapterEvent));
    }

    private void ApplyAdapterEvent(
        BrokerExecutionAccount expectedAccount,
        BrokerAdapterEvent adapterEvent)
    {
        var fenced = ExecuteMutation(
            expectedAccount,
            () => ApplyAdapterEventUnfenced(expectedAccount, adapterEvent));
        if (!fenced.IsSuccess)
        {
            RecordCallbackResult(
                expectedAccount,
                LeaseRejected(adapterEvent.ClientOrderId, fenced.Fault, fenced.Reason));
        }
    }

    private bool ApplyAdapterEventUnfenced(
        BrokerExecutionAccount expectedAccount,
        BrokerAdapterEvent adapterEvent)
    {
        try
        {
            var result = adapterEvent switch
            {
                BrokerOrderEvent order => _oms.ApplyVenueEvent(order.VenueEvent),
                BrokerExecutionEvent execution => _oms.ApplyVenueEvent(execution.VenueEvent),
                // Commission is already embedded in FillExecution and position is fill-derived.
                // These callbacks are ledgered without applying either economic value twice.
                BrokerCommissionEvent or BrokerPositionEvent => _oms.ApplyAdapterEvidence(adapterEvent),
                _ => new OmsCommandResult(
                    OmsCommandFault.InvalidVenueEvent,
                    null,
                    null,
                    "The adapter event category is unsupported."),
            };
            if (_reconciliation is not null &&
                result.Projection is { State: OrderLifecycleState.Unknown } &&
                _registrations.TryGetValue(expectedAccount, out var registration))
            {
                var cycle = RunReconciliationCore(registration, ReconciliationTrigger.UnknownOutcome);
                if (cycle.IsSuccess)
                    result = _oms.GetProjection(adapterEvent.ClientOrderId);
            }
            RecordCallbackResult(expectedAccount, result);
            return true;
        }
        catch (Exception exception)
        {
            RecordCallbackFailure(expectedAccount, exception);
            return false;
        }
    }

    private ExecutionLeaseOperationResult<T> ExecuteMutation<T>(
        BrokerExecutionAccount account,
        Func<T> mutation)
    {
        if (!_executionLeases.TryGetValue(account, out var lease))
        {
            try
            {
                return new ExecutionLeaseOperationResult<T>(
                    ExecutionLeaseFault.None,
                    mutation());
            }
            catch (Exception exception)
            {
                return new ExecutionLeaseOperationResult<T>(
                    ExecutionLeaseFault.OperationFailed,
                    default,
                    exception.Message);
            }
        }

        return lease.Execute(lease.Grant, mutation);
    }

    private OmsCommandResult LeaseRejected(
        ClientOrderId clientOrderId,
        ExecutionLeaseFault fault,
        string? reason)
    {
        var current = _oms.GetProjection(clientOrderId);
        return new OmsCommandResult(
            fault == ExecutionLeaseFault.OperationFailed
                ? OmsCommandFault.PersistenceRejected
                : OmsCommandFault.LeaseRejected,
            current.Projection,
            null,
            FencingReason(fault, reason));
    }

    private ExecutionCoordinatorResult LeaseRejectedCoordinator(
        BrokerExecutionAccount account,
        ExecutionLeaseFault fault,
        string? reason)
    {
        var message = FencingReason(fault, reason);
        return new ExecutionCoordinatorResult(
            fault == ExecutionLeaseFault.OperationFailed
                ? ExecutionCoordinatorFault.OmsRejected
                : ExecutionCoordinatorFault.LeaseRejected,
            new OmsCommandResult(
                fault == ExecutionLeaseFault.OperationFailed
                    ? OmsCommandFault.PersistenceRejected
                    : OmsCommandFault.LeaseRejected,
                null,
                null,
                $"{account.AdapterId.Value}/{account.AccountId.Value}: {message}"),
            Reason: message);
    }

    private static string FencingReason(ExecutionLeaseFault fault, string? reason) =>
        $"Execution lease validation failed ({fault}): {reason ?? "no additional reason"}";

    private void RecordCallbackResult(
        BrokerExecutionAccount account,
        in OmsCommandResult result)
    {
        lock (_callbackGate)
            _lastCallbackResults[account] = result;
    }

    private void RecordCallbackFailure(
        BrokerExecutionAccount account,
        Exception exception) =>
        RecordCallbackResult(
            account,
            new OmsCommandResult(
                OmsCommandFault.PersistenceRejected,
                null,
                null,
                $"The adapter callback could not be committed: {exception.Message}"));

    private OmsCommandResult RecoverReleaseAsUnknown(
        ClientOrderId clientOrderId,
        in OrderCommandContext context,
        string failureReason)
    {
        try
        {
            return _oms.RecoverUnacknowledgedSendAsUnknown(clientOrderId, context);
        }
        catch (Exception exception)
        {
            return new OmsCommandResult(
                OmsCommandFault.PersistenceRejected,
                null,
                null,
                $"{failureReason} Recovery to Unknown also failed: {exception.Message}");
        }
    }

    private OmsCommandResult RecoverPendingCommandAsUnknown(
        ClientOrderId clientOrderId,
        OrderLifecycleState pendingState,
        in OrderCommandContext context,
        string failureReason)
    {
        try
        {
            return _oms.RecordPendingCommandOutcomeUnknown(
                clientOrderId,
                pendingState,
                failureReason,
                context);
        }
        catch (Exception exception)
        {
            return new OmsCommandResult(
                OmsCommandFault.PersistenceRejected,
                null,
                null,
                $"{failureReason} Recovery to Unknown also failed: {exception.Message}");
        }
    }

    private OmsCommandResult RecoveryBlocked(ClientOrderId clientOrderId)
    {
        var current = _oms.GetProjection(clientOrderId);
        return new OmsCommandResult(
            OmsCommandFault.RecoveryRequired,
            current.Projection,
            null,
            "Durable startup recovery must be resolved before new orders can be validated or armed.");
    }

    private OmsCommandResult ReconciliationBlocked(ClientOrderId clientOrderId)
    {
        var current = _oms.GetProjection(clientOrderId);
        return new OmsCommandResult(
            OmsCommandFault.ReconciliationRequired,
            current.Projection,
            null,
            "An unresolved material reconciliation case blocks new submit/replace admission for this account; cancel remains allowed.");
    }

    private bool TryAuthorizeLiveDispatch(
        AdapterRegistration registration,
        ClientOrderId clientOrderId,
        bool requiresNewOrderAdmission,
        out ExecutionCoordinatorResult blocked)
    {
        blocked = default;
        if (registration.Adapter.Mode != ExecutionMode.Live)
            return true;

        var account = registration.Adapter.Account;
        if (!_executionLeases.TryGetValue(account, out var lease) || !lease.CanAdmitNewOrders)
        {
            blocked = LeaseRejectedCoordinator(
                account,
                ExecutionLeaseFault.LeaseLost,
                "LIVE dispatch requires one current account-scoped execution lease.");
            return false;
        }

        if (_reconciliation is null)
        {
            blocked = OmsRejected(LiveGuardrailBlocked(
                clientOrderId,
                "LIVE dispatch requires an attached reconciliation admission gate."));
            return false;
        }

        if (requiresNewOrderAdmission &&
            (!IsStartupReconciled(account) || !_reconciliation.CanAdmitNewOrders(account)))
        {
            blocked = OmsRejected(ReconciliationBlocked(clientOrderId));
            return false;
        }

        return true;
    }

    private OmsCommandResult LiveGuardrailBlocked(ClientOrderId clientOrderId, string reason)
    {
        var current = _oms.GetProjection(clientOrderId);
        return new OmsCommandResult(
            OmsCommandFault.ReconciliationRequired,
            current.Projection,
            null,
            reason);
    }

    private static LiveAdapterAdmission? CreateLiveAdmission(
        AdapterRegistration registration,
        BrokerAdapterCommandKind kind,
        CanonicalOrderInstruction? instruction,
        BrokerOrderQuery order,
        CanonicalOrderTerms? replacementTerms,
        CausationId causationId,
        string? capabilityVersion) =>
        registration.Adapter.Mode == ExecutionMode.Live
            ? new LiveAdapterAdmission(
                registration.Adapter.Account,
                kind,
                instruction,
                order,
                replacementTerms,
                causationId,
                capabilityVersion)
            : null;

    internal static bool TryConsumeLiveGuardrailAdmission(
        BrokerExecutionAccount account,
        BrokerSubmitCommand command) =>
        command.LiveGuardrailAdmission is LiveAdapterAdmission admission &&
        admission.TryConsume(
            account,
            BrokerAdapterCommandKind.Submit,
            command.Instruction,
            default,
            default,
            command.CausationId,
            command.CapabilityVersion);

    internal static bool TryConsumeLiveGuardrailAdmission(
        BrokerExecutionAccount account,
        BrokerCancelCommand command) =>
        command.LiveGuardrailAdmission is LiveAdapterAdmission admission &&
        admission.TryConsume(
            account,
            BrokerAdapterCommandKind.Cancel,
            null,
            command.Order,
            default,
            command.CausationId,
            null);

    internal static bool TryConsumeLiveGuardrailAdmission(
        BrokerExecutionAccount account,
        BrokerReplaceCommand command) =>
        command.LiveGuardrailAdmission is LiveAdapterAdmission admission &&
        admission.TryConsume(
            account,
            BrokerAdapterCommandKind.Replace,
            null,
            command.Order,
            command.ReplacementTerms,
            command.CausationId,
            command.CapabilityVersion);

    private bool TryBindOrderAccount(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId)
    {
        if (!account.IsValid || !clientOrderId.IsValid)
            return false;
        lock (_callbackGate)
        {
            if (_orderAccounts.TryGetValue(clientOrderId, out var existing))
                return existing == account;
            _orderAccounts.Add(clientOrderId, account);
            return true;
        }
    }

    private bool IsOrderBoundToAccount(
        BrokerExecutionAccount account,
        ClientOrderId clientOrderId)
    {
        lock (_callbackGate)
            return _orderAccounts.TryGetValue(clientOrderId, out var existing) && existing == account;
    }

    private static bool IsValidAdapterEventEnvelope(
        BrokerExecutionAccount expectedAccount,
        BrokerAdapterEvent adapterEvent,
        out string? reason)
    {
        reason = null;
        if (adapterEvent is null ||
            !adapterEvent.EventId.IsValid ||
            adapterEvent.Account != expectedAccount ||
            !adapterEvent.ClientOrderId.IsValid ||
            adapterEvent.OccurredAtUtc.Kind != DateTimeKind.Utc)
        {
            reason = "The adapter callback envelope is invalid or belongs to another account.";
            return false;
        }

        VenueEvent? venueEvent = adapterEvent switch
        {
            BrokerOrderEvent { VenueEvent: { Kind: not VenueEventKind.Fill } orderEvent } => orderEvent,
            BrokerExecutionEvent { VenueEvent: { Kind: VenueEventKind.Fill } executionEvent } => executionEvent,
            BrokerCommissionEvent or BrokerPositionEvent => null,
            _ => null,
        };
        if (adapterEvent is BrokerCommissionEvent or BrokerPositionEvent)
            return true;
        if (venueEvent is null ||
            venueEvent.ClientOrderId != adapterEvent.ClientOrderId ||
            venueEvent.OccurredAtUtc != adapterEvent.OccurredAtUtc)
        {
            reason = "The adapter callback category or inner venue-event identity is inconsistent.";
            return false;
        }
        return true;
    }

    private bool TryGetRegistration(
        BrokerExecutionAccount account,
        out AdapterRegistration registration)
    {
        if (_disposed || !account.IsValid)
        {
            registration = null!;
            return false;
        }
        return _registrations.TryGetValue(account, out registration!);
    }

    private static ExecutionCoordinatorResult OmsRejected(in OmsCommandResult result) =>
        new(ExecutionCoordinatorFault.OmsRejected, result, Reason: result.Reason ?? result.Fault.ToString());

    private static ExecutionCoordinatorResult InvalidCoordinatorResult(string reason) =>
        new(ExecutionCoordinatorFault.InvalidAccount, InvalidOmsResult(reason), Reason: reason);

    private static OmsCommandResult InvalidOmsResult(string reason) =>
        new(OmsCommandFault.InvalidCommand, null, null, reason);

    private sealed record AdapterRegistration(
        IBrokerExecutionAdapter Adapter,
        BoundedAccountWorker Worker,
        Action<BrokerAdapterEvent> EventHandler);

    private sealed class LiveAdapterAdmission(
        BrokerExecutionAccount account,
        BrokerAdapterCommandKind kind,
        CanonicalOrderInstruction? instruction,
        BrokerOrderQuery order,
        CanonicalOrderTerms? replacementTerms,
        CausationId causationId,
        string? capabilityVersion) : IDisposable
    {
        private int _state;

        internal bool TryConsume(
            BrokerExecutionAccount presentedAccount,
            BrokerAdapterCommandKind presentedKind,
            CanonicalOrderInstruction? presentedInstruction,
            BrokerOrderQuery presentedOrder,
            CanonicalOrderTerms? presentedReplacementTerms,
            CausationId presentedCausationId,
            string? presentedCapabilityVersion)
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return false;

            return presentedAccount == account &&
                   presentedKind == kind &&
                   Equals(presentedInstruction, instruction) &&
                   presentedOrder == order &&
                   presentedReplacementTerms == replacementTerms &&
                   presentedCausationId == causationId &&
                   string.Equals(presentedCapabilityVersion, capabilityVersion, StringComparison.Ordinal);
        }

        public void Dispose() => Interlocked.Exchange(ref _state, 2);
    }

    /// <summary>
    /// One bounded serial queue. Its drainer runs on the caller that acquired the idle worker; a slow
    /// adapter holds only its own account worker, never a coordinator-global lock or another account.
    /// </summary>
    private sealed class BoundedAccountWorker(
        int capacity,
        Action<Exception> onUnhandledException)
    {
        private readonly object _gate = new();
        private readonly Queue<Action> _queue = [];
        private bool _draining;
        private int _drainingThreadId;

        internal bool TryPost(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_gate)
            {
                if (_queue.Count >= capacity)
                    return false;
                _queue.Enqueue(action);
                if (_draining)
                    return true;
                _draining = true;
                _drainingThreadId = Environment.CurrentManagedThreadId;
            }

            Drain();
            return true;
        }

        internal void PostWithBackpressure(Action action)
        {
            ArgumentNullException.ThrowIfNull(action);
            lock (_gate)
            {
                while (_queue.Count >= capacity)
                {
                    if (_drainingThreadId == Environment.CurrentManagedThreadId)
                    {
                        throw new InvalidOperationException(
                            "An adapter raised too many callbacks inline; adapter callbacks must be asynchronous.");
                    }
                    Monitor.Wait(_gate);
                }
                _queue.Enqueue(action);
                if (_draining)
                    return;
                _draining = true;
                _drainingThreadId = Environment.CurrentManagedThreadId;
            }

            Drain();
        }

        private void Drain()
        {
            while (true)
            {
                Action action;
                lock (_gate)
                {
                    if (_queue.Count == 0)
                    {
                        _draining = false;
                        _drainingThreadId = 0;
                        Monitor.PulseAll(_gate);
                        return;
                    }
                    action = _queue.Dequeue();
                    Monitor.PulseAll(_gate);
                }
                try
                {
                    action();
                }
                catch (Exception exception)
                {
                    try
                    {
                        onUnhandledException(exception);
                    }
                    catch (Exception)
                    {
                        // The worker remains drainable even if its diagnostic sink fails.
                    }
                }
            }
        }
    }
}
