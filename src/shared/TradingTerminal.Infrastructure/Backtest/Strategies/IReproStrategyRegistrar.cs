using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Research;

namespace TradingTerminal.Infrastructure.Backtest.Strategies;

/// <summary>
/// The Paper Lab "save as strategy" backend: bridges a succeeded reproduction into a runnable,
/// paper-tagged <see cref="BacktestStrategyOption"/> and registers it into the live
/// <c>IBacktestStrategyRegistry</c> — so it shows up in the Backtest catalog like any built-in. This is
/// the method the Paper Lab window (ai-windows, Phase-3 task 13) calls; the UI never touches the bridge,
/// factory, or registry directly.
///
/// <para>Mirrors the runtime registration done by <c>StrategyAuthoringViewModel</c> (compile → register
/// into <c>IBacktestStrategyRegistry</c>), but the strategy comes from a reproduction instead of authored
/// C#. Provenance (paper id + commit + env hash + confidence) rides on the result. Never throws — a
/// failed reproduction or empty manifest yields <see cref="ReproRegistration.Failed"/>.</para>
/// </summary>
public interface IReproStrategyRegistrar
{
    /// <summary>Bridge the result to a manifest, score confidence, build the paper-tagged option, and
    /// register it. Returns the registered option + confidence, or a failed registration on any problem.
    /// Never throws.</summary>
    Task<ReproRegistration> RegisterAsync(ReproResult result, CancellationToken ct = default);
}

/// <summary>The outcome of a "save as strategy" registration.</summary>
public sealed record ReproRegistration(
    bool Success,
    BacktestStrategyOption? Option,
    ReproSignalManifest Manifest,
    ReplicationConfidence Confidence,
    string? Error)
{
    public static ReproRegistration Failed(string reason, ReproSignalManifest? manifest = null) =>
        new(false, null, manifest ?? ReproSignalManifest.Empty(), ReplicationConfidence.None, reason);
}
