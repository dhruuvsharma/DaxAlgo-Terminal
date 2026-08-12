using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TradingTerminal.Core.Time;

namespace TradingTerminal.Execution.Oms;

/// <summary>Faults returned by the durable fencing-generation store.</summary>
public enum ExecutionLeaseStoreFault : byte
{
    /// <summary>The store operation completed successfully.</summary>
    None = 0,

    /// <summary>The account, lease identity, token, or timestamp was invalid.</summary>
    InvalidInput = 1,

    /// <summary>The signed 64-bit fencing-token space is exhausted for this account.</summary>
    TokenExhausted = 2,

    /// <summary>The requested lease identity was already used by another generation.</summary>
    LeaseIdentityConflict = 3,
}

/// <summary>Exact account, lease, and fencing identity carried by one mutating operation.</summary>
public readonly record struct ExecutionLeaseGrant(
    BrokerExecutionAccount Account,
    ExecutionLeaseId LeaseId,
    FencingToken FencingToken)
{
    /// <summary>Gets whether all strongly typed claim fields are valid.</summary>
    public bool IsValid => Account.IsValid && LeaseId.IsValid && FencingToken.IsValid;
}

/// <summary>One durable fencing generation acquired for an execution account.</summary>
public readonly record struct ExecutionLeaseGeneration(
    ExecutionLeaseGrant Grant,
    DateTime AcquiredAtUtc)
{
    /// <summary>Gets whether the generation is structurally valid and UTC timestamped.</summary>
    public bool IsValid => Grant.IsValid && AcquiredAtUtc.Kind == DateTimeKind.Utc;
}

/// <summary>Result of atomically incrementing one account's durable fencing generation.</summary>
public readonly record struct ExecutionLeaseStoreAcquireResult(
    ExecutionLeaseStoreFault Fault,
    ExecutionLeaseGeneration? Generation,
    string? Reason = null)
{
    /// <summary>Gets whether a valid, durable generation was acquired.</summary>
    public bool IsSuccess => Fault == ExecutionLeaseStoreFault.None && Generation is { IsValid: true };
}

/// <summary>Result of checking an exact claim against the latest durable generation.</summary>
public readonly record struct ExecutionLeaseStoreValidationResult(
    ExecutionLeaseStoreFault Fault,
    bool IsCurrent,
    string? Reason = null)
{
    /// <summary>Gets whether the store completed the comparison without a structural fault.</summary>
    public bool IsSuccess => Fault == ExecutionLeaseStoreFault.None;
}

/// <summary>
/// Durable monotonic fencing seam. Callers must invoke acquisition and validation while holding the
/// matching account-scoped interprocess gate.
/// </summary>
public interface IExecutionLeaseStore
{
    /// <summary>Appends and returns the next strictly-greater fencing generation for one account.</summary>
    ExecutionLeaseStoreAcquireResult Acquire(
        BrokerExecutionAccount account,
        ExecutionLeaseId leaseId,
        DateTime acquiredAtUtc);

    /// <summary>Compares an exact account/lease/token claim with the latest durable generation.</summary>
    ExecutionLeaseStoreValidationResult Validate(in ExecutionLeaseGrant grant);
}

/// <summary>Thread-safe in-memory fencing store for deterministic engine tests.</summary>
public sealed class InMemoryExecutionLeaseStore : IExecutionLeaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<BrokerExecutionAccount, ExecutionLeaseGeneration> _latest = [];
    private readonly HashSet<ExecutionLeaseId> _leaseIds = [];

    /// <inheritdoc />
    public ExecutionLeaseStoreAcquireResult Acquire(
        BrokerExecutionAccount account,
        ExecutionLeaseId leaseId,
        DateTime acquiredAtUtc)
    {
        if (!account.IsValid || !leaseId.IsValid || acquiredAtUtc.Kind != DateTimeKind.Utc)
            return Failed(ExecutionLeaseStoreFault.InvalidInput, "The account, lease identity, or UTC timestamp is invalid.");

        lock (_gate)
        {
            if (_leaseIds.Contains(leaseId))
                return Failed(ExecutionLeaseStoreFault.LeaseIdentityConflict, "The execution lease identity was already used.");

            var prior = _latest.TryGetValue(account, out var generation)
                ? generation.Grant.FencingToken.Value
                : 0L;
            if (prior == long.MaxValue)
                return Failed(ExecutionLeaseStoreFault.TokenExhausted, "The fencing-token space is exhausted.");

            var next = new ExecutionLeaseGeneration(
                new ExecutionLeaseGrant(account, leaseId, new FencingToken(checked(prior + 1))),
                acquiredAtUtc);
            _latest[account] = next;
            _leaseIds.Add(leaseId);
            return new ExecutionLeaseStoreAcquireResult(ExecutionLeaseStoreFault.None, next);
        }
    }

    /// <inheritdoc />
    public ExecutionLeaseStoreValidationResult Validate(in ExecutionLeaseGrant grant)
    {
        if (!grant.IsValid)
        {
            return new ExecutionLeaseStoreValidationResult(
                ExecutionLeaseStoreFault.InvalidInput,
                false,
                "The execution lease claim is invalid.");
        }

        lock (_gate)
        {
            var isCurrent = _latest.TryGetValue(grant.Account, out var latest) && latest.Grant == grant;
            return new ExecutionLeaseStoreValidationResult(ExecutionLeaseStoreFault.None, isCurrent);
        }
    }

    private static ExecutionLeaseStoreAcquireResult Failed(
        ExecutionLeaseStoreFault fault,
        string reason) =>
        new(fault, null, reason);
}

/// <summary>Fail-closed outcomes from acquiring or using an account execution lease.</summary>
public enum ExecutionLeaseFault : byte
{
    /// <summary>The requested operation completed while the lease was current.</summary>
    None = 0,

    /// <summary>The account, lease identity, clock, or operation claim was invalid.</summary>
    InvalidInput = 1,

    /// <summary>Another same-machine process or thread owns the account gate.</summary>
    GateUnavailable = 2,

    /// <summary>The durable fencing store refused acquisition or validation.</summary>
    StoreRejected = 3,

    /// <summary>The lease was explicitly lost or disposed and no new mutation may start.</summary>
    LeaseLost = 4,

    /// <summary>The supplied operation claim is not the current durable fencing generation.</summary>
    StaleFencingToken = 5,

    /// <summary>The fenced operation threw; the failure was contained as a value.</summary>
    OperationFailed = 6,
}

/// <summary>Result of acquiring the same-machine gate and durable fencing generation.</summary>
public readonly record struct ExecutionLeaseAcquireResult(
    ExecutionLeaseFault Fault,
    ExecutionLease? Lease,
    string? Reason = null)
{
    /// <summary>Gets whether one active lease owns the account gate.</summary>
    public bool IsSuccess => Fault == ExecutionLeaseFault.None && Lease is { CanAdmitNewOrders: true };
}

/// <summary>Result of one operation executed under an exact, revalidated lease claim.</summary>
public readonly record struct ExecutionLeaseOperationResult<T>(
    ExecutionLeaseFault Fault,
    T? Value,
    string? Reason = null)
{
    /// <summary>Gets whether the operation ran under the current durable fence.</summary>
    public bool IsSuccess => Fault == ExecutionLeaseFault.None;
}

/// <summary>
/// Same-machine execution lease for one broker account. A dedicated owner thread holds the named
/// system mutex for the complete lease lifetime. Every mutation is marshalled to that thread,
/// revalidates the exact durable account/lease/token claim while the mutex remains held, and only then
/// invokes the supplied operation. No mutex ownership crosses a thread boundary.
/// </summary>
public sealed class ExecutionLease : IDisposable
{
    private readonly BrokerExecutionAccount _account;
    private readonly ExecutionLeaseId _leaseId;
    private readonly IExecutionLeaseStore _store;
    private readonly IClock _clock;
    private readonly BlockingCollection<ILeaseWorkItem> _work = new();
    private readonly TaskCompletionSource<OwnerStartResult> _ownerStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _ownerThread;
    private ExecutionLeaseGeneration _generation;
    private int _ownerThreadId;
    private int _lost;
    private int _lossNotified;
    private int _disposed;

    private ExecutionLease(
        BrokerExecutionAccount account,
        ExecutionLeaseId leaseId,
        IExecutionLeaseStore store,
        IClock clock)
    {
        _account = account;
        _leaseId = leaseId;
        _store = store;
        _clock = clock;
        _ownerThread = new Thread(OwnGateAndRun)
        {
            IsBackground = true,
            Name = $"DaxAlgo execution lease {ShortAccountHash(account)}",
        };
    }

    /// <summary>Gets the durable generation owned by this lease.</summary>
    public ExecutionLeaseGeneration Generation => _generation;

    /// <summary>Gets the exact account, lease identity, and fencing token required by mutations.</summary>
    public ExecutionLeaseGrant Grant => _generation.Grant;

    /// <summary>
    /// Raised exactly once when this instance stops owning its account lease. Handlers must not
    /// attempt another fenced operation; the service runtime uses this to demote its ledger handle
    /// to read-only before a replacement writer takes over.
    /// </summary>
    public event Action? LeaseLost;

    /// <summary>
    /// Gets whether new state-mutating admissions are allowed. This flips to false synchronously when
    /// <see cref="MarkLost"/> is called or durable revalidation proves the local generation stale.
    /// </summary>
    public bool CanAdmitNewOrders =>
        _generation.IsValid &&
        Volatile.Read(ref _lost) == 0 &&
        Volatile.Read(ref _disposed) == 0;

    /// <summary>
    /// Attempts to own the account's machine-wide named mutex and append the next durable generation.
    /// A supplied lease id makes deterministic tests possible; production callers normally omit it.
    /// </summary>
    public static ExecutionLeaseAcquireResult Acquire(
        BrokerExecutionAccount account,
        IExecutionLeaseStore store,
        IClock clock,
        ExecutionLeaseId? leaseId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);
        var selectedLeaseId = leaseId ?? new ExecutionLeaseId($"lease-{Guid.NewGuid():N}");
        if (!account.IsValid || !selectedLeaseId.IsValid)
        {
            return new ExecutionLeaseAcquireResult(
                ExecutionLeaseFault.InvalidInput,
                null,
                "The execution account or lease identity is invalid.");
        }

        var lease = new ExecutionLease(account, selectedLeaseId, store, clock);
        lease._ownerThread.Start();
        var started = lease._ownerStarted.Task.GetAwaiter().GetResult();
        if (started.Fault != ExecutionLeaseFault.None)
        {
            lease.WaitForOwnerExit();
            return new ExecutionLeaseAcquireResult(started.Fault, null, started.Reason);
        }
        return new ExecutionLeaseAcquireResult(ExecutionLeaseFault.None, lease);
    }

    /// <summary>
    /// Revalidates an exact operation claim and executes the mutation on the dedicated mutex-owning
    /// thread. A stale or absent proof fails closed and the delegate is not invoked.
    /// </summary>
    public ExecutionLeaseOperationResult<T> Execute<T>(
        in ExecutionLeaseGrant presented,
        Func<T> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (Volatile.Read(ref _lost) != 0 || Volatile.Read(ref _disposed) != 0)
            return Lost<T>();

        var copiedGrant = presented;
        if (Environment.CurrentManagedThreadId == Volatile.Read(ref _ownerThreadId))
            return ExecuteCore(copiedGrant, operation);

        var item = new LeaseWorkItem<T>(copiedGrant, operation);
        try
        {
            _work.Add(item);
        }
        catch (InvalidOperationException)
        {
            return Lost<T>();
        }

        return item.Completion.Task.GetAwaiter().GetResult();
    }

    /// <summary>Executes a mutation with no return value under the exact current claim.</summary>
    public ExecutionLeaseOperationResult<bool> Execute(
        in ExecutionLeaseGrant presented,
        Action operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Execute(
            presented,
            () =>
            {
                operation();
                return true;
            });
    }

    /// <summary>
    /// Immediately closes admission and asks the owner thread to release the interprocess gate. This
    /// method waits for an already-running fenced operation to finish, but queued operations fail closed.
    /// </summary>
    public void MarkLost()
    {
        MarkLostCore();
        WaitForOwnerExit();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        MarkLostCore();
        WaitForOwnerExit();
    }

    private void OwnGateAndRun()
    {
        _ownerThreadId = Environment.CurrentManagedThreadId;
        Mutex? gate = null;
        var ownsGate = false;
        var acquisitionReported = false;
        try
        {
            gate = new Mutex(initiallyOwned: false, BuildMutexName(_account));
            try
            {
                ownsGate = gate.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                ownsGate = true;
            }

            if (!ownsGate)
            {
                acquisitionReported = true;
                _ownerStarted.TrySetResult(new OwnerStartResult(
                    ExecutionLeaseFault.GateUnavailable,
                    "Another same-machine writer owns the execution account lease."));
                return;
            }

            ExecutionLeaseStoreAcquireResult acquired;
            try
            {
                var acquiredAtUtc = _clock.UtcNow;
                acquired = _store.Acquire(_account, _leaseId, acquiredAtUtc);
            }
            catch (Exception exception)
            {
                acquisitionReported = true;
                _ownerStarted.TrySetResult(new OwnerStartResult(
                    ExecutionLeaseFault.StoreRejected,
                    $"The durable fencing generation could not be acquired: {exception.Message}"));
                return;
            }

            if (!acquired.IsSuccess)
            {
                acquisitionReported = true;
                _ownerStarted.TrySetResult(new OwnerStartResult(
                    acquired.Fault == ExecutionLeaseStoreFault.InvalidInput
                        ? ExecutionLeaseFault.InvalidInput
                        : ExecutionLeaseFault.StoreRejected,
                    acquired.Reason ?? acquired.Fault.ToString()));
                return;
            }

            _generation = acquired.Generation!.Value;
            acquisitionReported = true;
            _ownerStarted.TrySetResult(new OwnerStartResult(ExecutionLeaseFault.None, null));

            foreach (var item in _work.GetConsumingEnumerable())
            {
                if (Volatile.Read(ref _lost) != 0)
                    item.Reject(ExecutionLeaseFault.LeaseLost, "The execution lease was lost.");
                else
                    item.Run(this);

                if (Volatile.Read(ref _lost) != 0)
                    break;
            }
        }
        catch (Exception exception)
        {
            MarkLostCore();
            if (!acquisitionReported)
            {
                _ownerStarted.TrySetResult(new OwnerStartResult(
                    ExecutionLeaseFault.GateUnavailable,
                    $"The account-scoped interprocess gate could not be acquired: {exception.Message}"));
            }
        }
        finally
        {
            MarkLostCore();
            while (_work.TryTake(out var pending))
                pending.Reject(ExecutionLeaseFault.LeaseLost, "The execution lease was lost.");
            if (!acquisitionReported)
            {
                _ownerStarted.TrySetResult(new OwnerStartResult(
                    ExecutionLeaseFault.GateUnavailable,
                    "The account-scoped interprocess gate stopped before acquisition completed."));
            }
            NotifyLeaseLost();
            if (ownsGate)
                gate!.ReleaseMutex();
            gate?.Dispose();
        }
    }

    private ExecutionLeaseOperationResult<T> ExecuteCore<T>(
        in ExecutionLeaseGrant presented,
        Func<T> operation)
    {
        if (Volatile.Read(ref _lost) != 0 || Volatile.Read(ref _disposed) != 0)
            return Lost<T>();
        if (!presented.IsValid)
        {
            return new ExecutionLeaseOperationResult<T>(
                ExecutionLeaseFault.InvalidInput,
                default,
                "The execution lease claim is invalid.");
        }
        if (presented != _generation.Grant)
        {
            return new ExecutionLeaseOperationResult<T>(
                ExecutionLeaseFault.StaleFencingToken,
                default,
                "The operation does not carry this account's active lease and fencing token.");
        }

        ExecutionLeaseStoreValidationResult validation;
        try
        {
            validation = _store.Validate(presented);
        }
        catch (Exception exception)
        {
            MarkLostCore();
            return new ExecutionLeaseOperationResult<T>(
                ExecutionLeaseFault.StoreRejected,
                default,
                $"The durable fencing claim could not be revalidated: {exception.Message}");
        }

        if (!validation.IsSuccess)
        {
            MarkLostCore();
            return new ExecutionLeaseOperationResult<T>(
                ExecutionLeaseFault.StoreRejected,
                default,
                validation.Reason ?? validation.Fault.ToString());
        }
        if (!validation.IsCurrent)
        {
            MarkLostCore();
            return new ExecutionLeaseOperationResult<T>(
                ExecutionLeaseFault.StaleFencingToken,
                default,
                "A newer durable fencing generation owns the execution account.");
        }

        try
        {
            return new ExecutionLeaseOperationResult<T>(ExecutionLeaseFault.None, operation());
        }
        catch (Exception exception)
        {
            return new ExecutionLeaseOperationResult<T>(
                ExecutionLeaseFault.OperationFailed,
                default,
                exception.Message);
        }
    }

    private void MarkLostCore()
    {
        Interlocked.Exchange(ref _lost, 1);
        TryCompleteWork();
    }

    private void NotifyLeaseLost()
    {
        if (Interlocked.Exchange(ref _lossNotified, 1) != 0)
            return;

        foreach (Action handler in LeaseLost?.GetInvocationList() ?? [])
        {
            try
            {
                handler();
            }
            catch
            {
                // Loss must remain fail-closed even if a cleanup observer cannot complete.
            }
        }
    }

    private void TryCompleteWork()
    {
        if (!_work.IsAddingCompleted)
        {
            try
            {
                _work.CompleteAdding();
            }
            catch (InvalidOperationException)
            {
                // Another loss/disposal path completed the queue concurrently.
            }
        }
    }

    private void WaitForOwnerExit()
    {
        if (_ownerThread.IsAlive && Environment.CurrentManagedThreadId != Volatile.Read(ref _ownerThreadId))
            _ownerThread.Join();
    }

    private static ExecutionLeaseOperationResult<T> Lost<T>() =>
        new(ExecutionLeaseFault.LeaseLost, default, "The execution lease was lost.");

    private static string BuildMutexName(BrokerExecutionAccount account)
    {
        var material = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{account.AdapterId.Value.Length}:{account.AdapterId.Value}|{account.AccountId.Value.Length}:{account.AccountId.Value}");
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return $@"Global\DaxAlgoTerminal.Execution.Account.{Convert.ToHexString(digest)}";
    }

    private static string ShortAccountHash(BrokerExecutionAccount account)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{account.AdapterId.Value}\0{account.AccountId.Value}"));
        return Convert.ToHexString(digest.AsSpan(0, 4));
    }

    private interface ILeaseWorkItem
    {
        void Run(ExecutionLease lease);

        void Reject(ExecutionLeaseFault fault, string reason);
    }

    private sealed class LeaseWorkItem<T>(ExecutionLeaseGrant presented, Func<T> operation) : ILeaseWorkItem
    {
        internal TaskCompletionSource<ExecutionLeaseOperationResult<T>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Run(ExecutionLease lease) =>
            Completion.TrySetResult(lease.ExecuteCore(presented, operation));

        public void Reject(ExecutionLeaseFault fault, string reason) =>
            Completion.TrySetResult(new ExecutionLeaseOperationResult<T>(fault, default, reason));
    }

    private readonly record struct OwnerStartResult(ExecutionLeaseFault Fault, string? Reason);
}
