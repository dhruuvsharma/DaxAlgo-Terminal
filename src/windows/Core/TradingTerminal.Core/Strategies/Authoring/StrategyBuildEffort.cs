namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// How hard the BUILD PIPELINE works on a strategy — a different dial from <see cref="CodegenEffort"/>,
/// which is how hard the model thinks inside one generation. Build effort spends whole extra
/// generations: more domain skill packs in the system prompt, more auto-fix retries, a self-review
/// pass, and a runtime smoke of the compiled strategy. Quick is a cheap sketch; Max is "spend what it
/// takes".
/// </summary>
public enum StrategyBuildEffort
{
    /// <summary>Sketch fast: one skill pack, one fix attempt, no review, no smoke.</summary>
    Quick,

    /// <summary>The default — today's behavior: three skill packs, two fix attempts.</summary>
    Standard,

    /// <summary>Thorough: five skill packs, four fix attempts, a self-review pass and a backtest smoke.</summary>
    Deep,

    /// <summary>Correctness over cost: eight skill packs, six fix attempts, review + smoke.</summary>
    Max,
}

/// <summary>Wire values for <see cref="StrategyBuildEffort"/> (<c>quick</c>/<c>standard</c>/<c>deep</c>/
/// <c>max</c>) — what the session snapshot and the user-config file persist.</summary>
public static class StrategyBuildEfforts
{
    /// <summary>The persisted value. Never null — unlike <see cref="CodegenEffort"/>, there is no
    /// "send nothing" state; the pipeline always runs at SOME effort.</summary>
    public static string Wire(this StrategyBuildEffort effort) => effort switch
    {
        StrategyBuildEffort.Quick => "quick",
        StrategyBuildEffort.Deep => "deep",
        StrategyBuildEffort.Max => "max",
        _ => "standard",
    };

    /// <summary>Parses a persisted value; anything unrecognized (including the absent field of an old
    /// snapshot) is <see cref="StrategyBuildEffort.Standard"/> rather than an error.</summary>
    public static StrategyBuildEffort Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "quick" => StrategyBuildEffort.Quick,
        "deep" => StrategyBuildEffort.Deep,
        "max" => StrategyBuildEffort.Max,
        _ => StrategyBuildEffort.Standard,
    };
}

/// <summary>
/// What a <see cref="StrategyBuildEffort"/> actually buys, resolved once (<see cref="For"/>) and
/// threaded through session creation so the turn loop never re-derives it.
/// </summary>
/// <param name="MaxSkills">How many domain skill packs the brief may pull into the system prompt.</param>
/// <param name="MaxFixAttempts">How many compiler-error retries a turn gets after the first generation.</param>
/// <param name="SelfReview">Run one extra generation after a clean compile asking the model to critique
/// and improve its own strategy. A review that doesn't compile is discarded, never adopted.</param>
/// <param name="BacktestSmoke">After a clean compile, run the strategy's lifecycle over a handful of
/// synthetic ticks purely to catch runtime throws. Advisory — a failure is a diagnostic, not a block.</param>
public sealed record StrategyBuildProfile(int MaxSkills, int MaxFixAttempts, bool SelfReview, bool BacktestSmoke)
{
    /// <summary>The profile an effort level buys.</summary>
    public static StrategyBuildProfile For(StrategyBuildEffort effort) => effort switch
    {
        StrategyBuildEffort.Quick => new(MaxSkills: 1, MaxFixAttempts: 1, SelfReview: false, BacktestSmoke: false),
        StrategyBuildEffort.Deep => new(MaxSkills: 5, MaxFixAttempts: 4, SelfReview: true, BacktestSmoke: true),
        StrategyBuildEffort.Max => new(MaxSkills: 8, MaxFixAttempts: 6, SelfReview: true, BacktestSmoke: true),
        _ => new(MaxSkills: 3, MaxFixAttempts: 2, SelfReview: false, BacktestSmoke: false),
    };
}
