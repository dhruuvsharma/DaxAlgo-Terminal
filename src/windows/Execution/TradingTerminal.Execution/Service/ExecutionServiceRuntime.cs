using TradingTerminal.Core.Time;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Service;

/// <summary>UTC wall clock used only by the separate execution-service process.</summary>
public sealed class ExecutionSystemClock : IClock
{
    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;
}

/// <summary>
/// Owns the existing slice 1-3/6 components for the lifetime of the out-of-process service. The
/// composition is simulation-only and introduces no broker SDK, credential, socket, or network path.
/// </summary>
public sealed class ExecutionServiceRuntime : IDisposable
{
    private bool _disposed;

    private ExecutionServiceRuntime(
        SqliteOrderEventStore ledger,
        ExecutionLease lease,
        OrderManagementService oms,
        ExecutionCoordinator coordinator,
        ControllableAdapterEventScheduler scheduler,
        SimulatedExecutionAdapter adapter)
    {
        Ledger = ledger;
        Lease = lease;
        Oms = oms;
        Coordinator = coordinator;
        Scheduler = scheduler;
        Adapter = adapter;
        Engine = new ExecutionServiceEngine(ledger, oms, coordinator, scheduler, lease);
    }

    /// <summary>Gets the durable SQLite OMS ledger and fencing-generation store.</summary>
    public SqliteOrderEventStore Ledger { get; }

    /// <summary>Gets the account-scoped named-mutex lease held by the service.</summary>
    public ExecutionLease Lease { get; }

    /// <summary>Gets the reused OMS core.</summary>
    public OrderManagementService Oms { get; }

    /// <summary>Gets the reused coordinator and reconciliation host.</summary>
    public ExecutionCoordinator Coordinator { get; }

    /// <summary>Gets the deterministic callback scheduler owned by the service.</summary>
    public ControllableAdapterEventScheduler Scheduler { get; }

    /// <summary>Gets the only adapter implementation hosted by this slice.</summary>
    public SimulatedExecutionAdapter Adapter { get; }

    /// <summary>Gets the pipe-independent, lease-fenced service API.</summary>
    public ExecutionServiceEngine Engine { get; }

    /// <summary>
    /// Creates a runtime over the dedicated durable ledger. Optional plans exist only for deterministic
    /// simulation tests; production service startup supplies none and still cannot route live orders.
    /// </summary>
    public static ExecutionServiceRuntime Create(
        string? databasePath = null,
        IClock? clock = null,
        IEnumerable<VenueSubmitPlan>? simulatedPlans = null,
        ExecutionLeaseId? leaseId = null)
    {
        clock ??= new ExecutionSystemClock();
        var ledger = databasePath is null
            ? new SqliteOrderEventStore(clock)
            : new SqliteOrderEventStore(databasePath, clock);
        ExecutionLease? lease = null;
        ExecutionCoordinator? coordinator = null;
        try
        {
            var venue = new DeterministicSimulatedVenue(clock, simulatedPlans);
            var scheduler = new ControllableAdapterEventScheduler();
            var adapter = new SimulatedExecutionAdapter(venue, clock, scheduler);
            var acquired = ExecutionLease.Acquire(adapter.Account, ledger, clock, leaseId);
            if (!acquired.IsSuccess)
            {
                throw new InvalidOperationException(
                    $"The execution account lease could not be acquired ({acquired.Fault}): {acquired.Reason}");
            }
            lease = acquired.Lease!;
            lease.LeaseLost += ledger.RelinquishWriterAccess;

            var oms = new OrderManagementService(ledger, CreateSimulationRiskEngine(), venue, clock);
            var reconciliation = new ReconciliationEngine(oms, ledger, clock);
            var composed = lease.Execute(
                lease.Grant,
                () => new ExecutionCoordinator(
                    oms,
                    [adapter],
                    reconciliation,
                    [lease]));
            if (!composed.IsSuccess || composed.Value is null)
            {
                throw new InvalidOperationException(
                    $"The fenced execution coordinator could not start ({composed.Fault}): {composed.Reason}");
            }
            coordinator = composed.Value;
            return new ExecutionServiceRuntime(ledger, lease, oms, coordinator, scheduler, adapter);
        }
        catch
        {
            coordinator?.Dispose();
            lease?.Dispose();
            ledger.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Coordinator.Dispose();
        Lease.LeaseLost -= Ledger.RelinquishWriterAccess;
        Lease.Dispose();
        Ledger.Dispose();
    }

    private static RiskEngine CreateSimulationRiskEngine()
    {
        var limits = new RiskLimits(
            ScaledQuantity.FromWhole(1_000_000),
            new ScaledMoney(1_000_000_000_000, 0),
            ScaledQuantity.FromWhole(1_000_000),
            new ScaledMoney(1_000_000_000_000, 0),
            new ScaledMoney(1_000_000_000_000, 0));
        var fault = RiskPolicy.TryCreate(
            "execution-service-simulation",
            "1",
            limits,
            out var policy);
        if (fault != RiskPolicyFault.None || policy is null)
            throw new InvalidOperationException($"The simulation risk policy is invalid: {fault}.");
        return new RiskEngine(policy);
    }
}
