namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>One routing decision, with the arithmetic that produced it.</summary>
/// <param name="Role">Who takes the turn.</param>
/// <param name="Weights">The full posterior, so a decision can be logged with its reasoning rather than
/// as a bare name — which is the difference between a trajectory log worth distilling later and one
/// that only records what happened.</param>
public sealed record RoutingDecision(AgentRole Role, IReadOnlyDictionary<AgentRole, double> Weights);

/// <summary>
/// Combines the hand-written prior with measured reliability.
///
/// <para><c>q*ₐ ∝ pₐ · e^(η·r̄ₐ)</c> — the paper's Derivation 1, proven the unique optimum of a reward
/// objective under a KL trust region around the prior. Read plainly: <b>start from what the state
/// machine says, and tilt towards agents that have been working.</b> η sets how far the tilt may go.</para>
///
/// <para>Everything here is a pure function of state, reliability and η. No provider, no key, no token
/// budget — which is what makes the routing testable on its own, and why it was built before any agent
/// existed to be routed to.</para>
/// </summary>
public static class AgentRouter
{
    /// <summary>
    /// How hard reliability may pull against the prior.
    ///
    /// <para>At 1.0 the gap between a perfect and a hopeless agent is a factor of <c>e</c> — enough to
    /// reorder two agents the prior thought comparable, never enough to overturn a prior that strongly
    /// favours one. That asymmetry is deliberate: the prior encodes what is <i>possible</i> at this
    /// point in the session, and reliability only expresses a preference among possibilities.</para>
    /// </summary>
    public const double DefaultEta = 1.0d;

    /// <summary>The posterior over eligible agents. Empty when the session is complete.</summary>
    public static IReadOnlyDictionary<AgentRole, double> Weigh(
        RoutingState state,
        AgentReliability reliability,
        double eta = DefaultEta)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(reliability);
        if (!double.IsFinite(eta) || eta < 0d)
            throw new ArgumentOutOfRangeException(nameof(eta), eta, "Eta must be finite and non-negative.");

        var prior = RoutingPrior.For(state);
        if (prior.Count == 0) return prior;

        // A zero in the prior stays zero: exp() is strictly positive, so multiplying preserves it.
        // That is what keeps an ineligible agent unreachable however reliable it looks.
        var tilted = prior.ToDictionary(
            pair => pair.Key,
            pair => pair.Value * Math.Exp(eta * reliability.Of(pair.Key)));

        var total = tilted.Values.Sum();
        return total <= 0d
            ? prior
            : tilted.ToDictionary(pair => pair.Key, pair => pair.Value / total);
    }

    /// <summary>
    /// The highest-weighted agent, or null when there is nothing left to do.
    ///
    /// <para>Deterministic on purpose. A user watching Hyperion build their strategy should be able to
    /// re-run a brief and get the same route; a session that wanders differently each time cannot be
    /// debugged, and the exploration a bandit wants is worth far less than that here — the action space
    /// is six, and the prior already knows most of the answer.</para>
    /// </summary>
    public static RoutingDecision? Choose(
        RoutingState state,
        AgentReliability reliability,
        double eta = DefaultEta)
    {
        var weights = Weigh(state, reliability, eta);
        if (weights.Count == 0) return null;

        // Ties break by declaration order rather than dictionary order, so the choice does not depend on
        // hashing — a test that passes on one runtime and fails on another is worse than no test.
        var best = weights
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => (int)pair.Key)
            .First();

        return new RoutingDecision(best.Key, weights);
    }

    /// <summary>
    /// Samples from the posterior instead of taking the maximum, for deliberately exploring.
    ///
    /// <para>The RNG is a parameter rather than ambient so a session can be replayed exactly. An
    /// exploration policy that cannot be reproduced cannot be evaluated, which would leave the
    /// trajectory log recording decisions nobody can account for.</para>
    /// </summary>
    public static RoutingDecision? Sample(
        RoutingState state,
        AgentReliability reliability,
        Random random,
        double eta = DefaultEta)
    {
        ArgumentNullException.ThrowIfNull(random);

        var weights = Weigh(state, reliability, eta);
        if (weights.Count == 0) return null;

        var ordered = weights.OrderBy(pair => (int)pair.Key).ToArray();
        var roll = random.NextDouble();
        var cumulative = 0d;
        foreach (var pair in ordered)
        {
            cumulative += pair.Value;
            if (roll <= cumulative) return new RoutingDecision(pair.Key, weights);
        }

        // Floating-point remainder: the cumulative sum can fall a hair short of 1.
        return new RoutingDecision(ordered[^1].Key, weights);
    }
}
