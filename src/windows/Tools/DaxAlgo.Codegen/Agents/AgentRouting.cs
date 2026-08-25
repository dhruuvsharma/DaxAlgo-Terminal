using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>
/// Who does the next turn. Six roles, derived from the failure modes a single conversation kept
/// producing rather than from a generic agent taxonomy.
/// </summary>
public enum AgentRole
{
    /// <summary>Vague brief into a written spec. Asks clarifying questions; writes no code.</summary>
    Interviewer,

    /// <summary>Indicators, features, the maths.</summary>
    Quant,

    /// <summary>Spec and maths into an <c>IStrategyKernel</c> or <c>IVisualizer</c>.</summary>
    Coder,

    /// <summary>The picture. Separate from <see cref="Coder"/> on purpose — composition and scale are a
    /// different skill from signal logic, and splitting them lets the draw path be scored on its
    /// own.</summary>
    Painter,

    /// <summary>Diagnostics into a repair.</summary>
    Fixer,

    /// <summary>What verification cannot catch: look-ahead bias, unbounded buffers, wall-clock time,
    /// a parameter declared and then ignored in spirit if not in letter.</summary>
    Reviewer,
}

/// <summary>
/// What the session knows right now — the input the routing prior reads.
///
/// <para>Deliberately a set of facts rather than a phase enum. A phase would have to be assigned by
/// something, and that something would be a second routing decision competing with this one.</para>
/// </summary>
/// <param name="HasSpec">The brief has been turned into something concrete enough to build from.</param>
/// <param name="NeedsMaths">The spec calls for indicators or features that do not exist yet.</param>
/// <param name="HasCode">Something has been written.</param>
/// <param name="Compiles">That code compiled.</param>
/// <param name="MustDraw">This unit is obliged to produce a picture — true for every visualizer.</param>
/// <param name="Draws">It currently produces one.</param>
/// <param name="FailedAt">The earliest rung that failed, or null when nothing has.</param>
/// <param name="Reviewed">A reviewer has already passed over the finished unit.</param>
public sealed record RoutingState(
    bool HasSpec = false,
    bool NeedsMaths = false,
    bool HasCode = false,
    bool Compiles = false,
    bool MustDraw = false,
    bool Draws = false,
    VerificationRung? FailedAt = null,
    bool Reviewed = false)
{
    /// <summary>Nothing left to do: it builds, it draws if it must, nothing failed, and it has been
    /// reviewed.</summary>
    public bool IsComplete => Compiles && FailedAt is null && (!MustDraw || Draws) && Reviewed;
}

/// <summary>
/// The hand-written state machine that decides which agents are even eligible, and how strongly each is
/// preferred.
///
/// <para>This is the <c>p</c> in <c>q*ₐ ∝ pₐ · e^(η·r̄ₐ)</c>. The paper it comes from supplies that prior
/// from a learned controller; <b>nothing in the derivation requires one</b>. Supplying it by hand is what
/// lets the same update rule work from trajectory zero, with no training data and no model to ship —
/// and it inherits the regret bound the paper's own remark notes.</para>
///
/// <para>A zero here is absolute. Reliability can only redistribute weight among agents the machine
/// already considers eligible; it can never make an ineligible one reachable. Painter before the code
/// compiles is not a worse choice, it is a meaningless one, and a bandit allowed to reach it would
/// eventually try.</para>
/// </summary>
public static class RoutingPrior
{
    /// <summary>The eligible agents and their weights. Always sums to 1 over a non-empty set; empty only
    /// when the session is finished.</summary>
    public static IReadOnlyDictionary<AgentRole, double> For(RoutingState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // Repair outranks everything. A failed rung means the current artifact is wrong, and any other
        // agent would be building on it.
        if (state.FailedAt is { } rung) return Normalise(ForFailure(rung));

        if (!state.HasSpec) return Only(AgentRole.Interviewer);
        if (state.NeedsMaths && !state.HasCode) return Only(AgentRole.Quant);
        if (!state.HasCode) return Only(AgentRole.Coder);

        // Compiled but not yet drawing what it owes: the Painter's whole job.
        if (state.Compiles && state.MustDraw && !state.Draws) return Only(AgentRole.Painter);

        if (state.Compiles && !state.Reviewed) return Only(AgentRole.Reviewer);

        // Complete. An empty prior is how the loop is told to stop, rather than a role meaning "done"
        // that could be selected by mistake.
        return new Dictionary<AgentRole, double>();
    }

    /// <summary>
    /// Which agent repairs which rung.
    ///
    /// <para>Fixer takes most of it, but two rungs point elsewhere: a draw-probe failure is the
    /// Painter's own work coming back, and a schema mismatch is nearly always the Coder having declared
    /// a parameter it then hard-coded. Sending those to a general repair agent loses the context that
    /// makes them cheap to fix.</para>
    /// </summary>
    private static Dictionary<AgentRole, double> ForFailure(VerificationRung rung) => rung switch
    {
        VerificationRung.DrawProbe => new() { [AgentRole.Painter] = 0.75, [AgentRole.Fixer] = 0.25 },
        VerificationRung.SchemaCoherence => new() { [AgentRole.Coder] = 0.7, [AgentRole.Fixer] = 0.3 },
        VerificationRung.Replay => new() { [AgentRole.Quant] = 0.5, [AgentRole.Fixer] = 0.5 },
        _ => new() { [AgentRole.Fixer] = 1.0 },
    };

    private static IReadOnlyDictionary<AgentRole, double> Only(AgentRole role) =>
        new Dictionary<AgentRole, double> { [role] = 1.0 };

    private static IReadOnlyDictionary<AgentRole, double> Normalise(Dictionary<AgentRole, double> weights)
    {
        var total = weights.Values.Sum();
        if (total <= 0d) return weights;
        return weights.ToDictionary(pair => pair.Key, pair => pair.Value / total);
    }
}
