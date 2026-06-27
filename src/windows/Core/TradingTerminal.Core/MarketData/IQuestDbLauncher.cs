namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Brings the QuestDB tick backend up on demand — starts its Docker container (launching Docker
/// Desktop first if the daemon is down) and re-arms the store so L1/L2 persistence engages without an
/// app restart. Exposed as a Core abstraction so layers that must stay store-agnostic (e.g. the login
/// screen) can still trigger the warm-up. When QuestDB isn't the configured backend,
/// <see cref="IsApplicable"/> is false and the rest are no-ops.
/// </summary>
public interface IQuestDbLauncher
{
    /// <summary>True only when the QuestDB backend is configured — otherwise there's nothing to start.</summary>
    bool IsApplicable { get; }

    /// <summary>Whether automatic startup is enabled (<c>MarketDataStore:AutoStartDocker</c>).</summary>
    bool AutoStart { get; }

    /// <summary>Quick probe — is QuestDB already accepting connections?</summary>
    bool IsReachable();

    /// <summary>Start QuestDB (and Docker Desktop if needed), then re-arm the store. Returns true once
    /// QuestDB is reachable and persisting. Runs off the calling thread.</summary>
    Task<bool> StartAsync(CancellationToken ct = default);
}
