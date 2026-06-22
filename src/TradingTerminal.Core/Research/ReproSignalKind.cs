namespace TradingTerminal.Core.Research;

/// <summary>
/// The semantics of a reproduced signal's <c>value</c> — what the replay kernel and confidence scorer
/// should make of the number. Declared in the sandbox artifact (snake_case <c>kind</c> field) so the
/// bridge knows how to interpret each signal without re-running any untrusted code.
/// </summary>
public enum ReproSignalKind
{
    /// <summary>
    /// A directional position target: the engine takes <c>sign(value)</c> as the desired side
    /// (+1 long, -1 short, 0 flat). Magnitude is ignored by the simple replay kernel. This is the
    /// default and the only kind the Phase-3 replay kernel acts on positionally.
    /// </summary>
    Position,

    /// <summary>A continuous score/prediction (e.g. a forecasted return or alpha). Carried as
    /// provenance/diagnostics; the simple replay kernel still acts on its sign.</summary>
    Score,

    /// <summary>A portfolio weight in [-1, 1]. Carried for richer future sizing; the simple replay
    /// kernel still acts on its sign.</summary>
    Weight,
}
