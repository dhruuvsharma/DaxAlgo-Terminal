using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.ExecutionUi;

public static class ExecutionConsoleServiceCollectionExtensions
{
    public static IServiceCollection AddExecutionConsole(this IServiceCollection services)
    {
        services.AddSingleton<IExecutionConfirmationService, WpfExecutionConfirmationService>();
        services.TryAddSingleton<IExecutionLeaseStore, ExecutionConsoleLeaseStore>();
        services.TryAddSingleton<ExecutionModeStatusProjection>();
        // App lifetime, NOT per window. The console used to resolve a transient client inside the
        // window's DI scope, so closing the window disposed the engine and every book with it — and
        // the engine only ran while the window was open. Books outlive their window now, and the
        // engine keeps running in the background for as long as the application does.
        services.AddSingleton<IExecutionClient, InProcessExecutionClient>();
        // App lifetime, like the engine it watches: the header chip lives as long as the shell does.
        services.AddSingleton<ExecutionBooksChipViewModel>();
        services.AddTransient<ExecutionConsoleViewModel>();
        services.AddTransient<ExecutionConsoleView>();
        return services;
    }
}

/// <summary>
/// Process-shared, fail-closed fencing generations for the in-process console. The fixed capacities
/// prevent repeated console lifetimes from turning lease history into an unbounded collection.
/// </summary>
internal sealed class ExecutionConsoleLeaseStore : IExecutionLeaseStore
{
    internal const int MaximumAccounts = 256;
    internal const int MaximumLeaseIdentities = 4_096;

    private readonly object _gate = new();
    private readonly Dictionary<BrokerExecutionAccount, ExecutionLeaseGeneration> _latest = [];
    private readonly HashSet<ExecutionLeaseId> _leaseIds = [];

    public ExecutionLeaseStoreAcquireResult Acquire(
        BrokerExecutionAccount account,
        ExecutionLeaseId leaseId,
        DateTime acquiredAtUtc)
    {
        if (!account.IsValid || !leaseId.IsValid || acquiredAtUtc.Kind != DateTimeKind.Utc)
            return Failed("The account, lease identity, or UTC timestamp is invalid.");

        lock (_gate)
        {
            if (_leaseIds.Contains(leaseId))
            {
                return new ExecutionLeaseStoreAcquireResult(
                    ExecutionLeaseStoreFault.LeaseIdentityConflict,
                    null,
                    "The execution lease identity was already used.");
            }
            if (!_latest.ContainsKey(account) && _latest.Count >= MaximumAccounts)
                return Failed($"The console lease store reached its {MaximumAccounts}-account bound.");
            if (_leaseIds.Count >= MaximumLeaseIdentities)
                return Failed($"The console lease store reached its {MaximumLeaseIdentities}-generation bound.");

            var prior = _latest.TryGetValue(account, out var generation)
                ? generation.Grant.FencingToken.Value
                : 0L;
            if (prior == long.MaxValue)
            {
                return new ExecutionLeaseStoreAcquireResult(
                    ExecutionLeaseStoreFault.TokenExhausted,
                    null,
                    "The fencing-token space is exhausted.");
            }

            var next = new ExecutionLeaseGeneration(
                new ExecutionLeaseGrant(account, leaseId, new FencingToken(checked(prior + 1))),
                acquiredAtUtc);
            _latest[account] = next;
            _leaseIds.Add(leaseId);
            return new ExecutionLeaseStoreAcquireResult(ExecutionLeaseStoreFault.None, next);
        }
    }

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
            var current = _latest.TryGetValue(grant.Account, out var latest) && latest.Grant == grant;
            return new ExecutionLeaseStoreValidationResult(ExecutionLeaseStoreFault.None, current);
        }
    }

    private static ExecutionLeaseStoreAcquireResult Failed(string reason) =>
        new(ExecutionLeaseStoreFault.InvalidInput, null, reason);
}
