namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Brings the QuestDB tick backend up on demand and re-arms the store so L1/L2 persistence engages
/// without an app restart. The implementation may start an app-owned native runtime or attach to an
/// externally managed endpoint. Exposed as a Core abstraction so store-agnostic layers (e.g. the
/// login screen) can trigger the warm-up. When QuestDB isn't the configured backend,
/// <see cref="IsApplicable"/> is false and the rest are no-ops.
/// </summary>
public interface IQuestDbLauncher
{
    /// <summary>True only when the QuestDB backend is configured — otherwise there's nothing to start.</summary>
    bool IsApplicable { get; }

    /// <summary>Whether automatic startup of an app-owned runtime is enabled.</summary>
    bool AutoStart { get; }

    /// <summary>Quick probe — is QuestDB already accepting connections?</summary>
    bool IsReachable();

    /// <summary>Start or attach to QuestDB, then re-arm the store. Returns true once QuestDB is
    /// reachable and persisting.</summary>
    Task<bool> StartAsync(CancellationToken ct = default);
}
