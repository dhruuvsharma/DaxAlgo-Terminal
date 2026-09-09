namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// The one dial a user turns: how much effort this build is worth.
///
/// <para>It replaced a four-position build-effort picker (quick / standard / deep / max) whose tooltip
/// already admitted it was doing several jobs — skill budget, fix attempts, the agent committee, and
/// how hard the model thinks. Four positions asked the user to have an opinion about a trade they have
/// no way to price, and the two middle ones were never chosen for a reason anybody could state.</para>
///
/// <para><b>Two positions, and the difference between them is "how much", never "which".</b> Both plan
/// the work, build it and review it; the dial sets how hard the model thinks, how many builders may run
/// at once, and how many repair rounds are bought. WHICH tasks exist and which critics apply is the
/// plan's decision, made from the brief — and keeping that out of the dial is what stops it turning
/// into a second, worse router.</para>
/// </summary>
public enum CodegenMode
{
    /// <summary>
    /// Get it built. The model runs at <b>its own default</b> reasoning setting — no effort parameter
    /// is sent at all — with a modest skill budget and two repair rounds.
    ///
    /// <para>Sending nothing is deliberate rather than lazy: it is the only setting every model
    /// accepts, including the ones that predate the parameter and the ones that accept it and then
    /// stop answering.</para>
    ///
    /// <para><b>It is a swarm of one</b>: a plan, one builder, the verification ladder and the critics.
    /// The same code path Research takes, with the fan-out set to one — which is what stops the two
    /// modes drifting into two builders with different bugs, the way the committee and the single
    /// conversation did.</para>
    /// </summary>
    Standard,

    /// <summary>
    /// Correctness over cost. The model runs at <b>the highest reasoning setting it is known to still
    /// answer at</b>, with the full skill budget, six repair rounds, and builders fanned out in
    /// parallel — one file each, against a contract the planner fixes before any of them start.
    ///
    /// <para>"Known to still answer at" is the whole subtlety and it is not the same as "accepted".
    /// A model that takes the parameter and then reasons until its budget is gone has produced
    /// nothing, expensively. Where no usable setting has been measured, Research falls back to the
    /// model's default and says so rather than failing.</para>
    /// </summary>
    Research,
}

/// <summary>Wire values, labels, and the migration from the retired four-position dial.</summary>
public static class CodegenModes
{
    /// <summary>The persisted value: <c>standard</c> or <c>research</c>.</summary>
    public static string Wire(this CodegenMode mode) =>
        mode == CodegenMode.Research ? "research" : "standard";

    /// <summary>What the pill reads.</summary>
    public static string Label(this CodegenMode mode) =>
        mode == CodegenMode.Research ? "Research" : "Standard";

    /// <summary>
    /// Parses a persisted value, <b>including the retired build-effort words</b>, so a session or a
    /// config file written before this dial existed opens on the position its owner would have picked
    /// rather than silently on the default.
    ///
    /// <para>Quick and Standard were the cheap settings; Deep and Max were the ones that spent extra
    /// generations and fanned out. The split falls there.</para>
    /// </summary>
    public static CodegenMode Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "research" or "deep" or "max" => CodegenMode.Research,
        _ => CodegenMode.Standard,
    };

    /// <summary>
    /// The pipeline profile this mode buys, expressed in the enum the build already understands.
    ///
    /// <para>Kept rather than collapsed because the four-position enum is still the shape of
    /// <see cref="StrategyBuildProfile"/> and of the benchmark harness, which drives Quick and Deep
    /// deliberately to measure what the two user-facing positions sit between. The dial is two
    /// positions; the machine underneath still has four, and that is not a contradiction — it is a
    /// user control over an implementation range.</para>
    /// </summary>
    public static StrategyBuildEffort ToBuildEffort(this CodegenMode mode) =>
        mode == CodegenMode.Research ? StrategyBuildEffort.Max : StrategyBuildEffort.Standard;
}
