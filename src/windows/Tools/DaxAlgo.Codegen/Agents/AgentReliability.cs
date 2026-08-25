namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>
/// How often each agent's work has survived the ladder.
///
/// <para>An exponential moving average, which is the paper's Derivation 2 and is a discounted maximum-
/// likelihood estimate in closed form: no gradients, no training step, useful from the first run. Recent
/// evidence outweighs old evidence, which is what you want when the thing being measured is a model the
/// user can change between turns.</para>
///
/// <para><b>The scores come from the verifier, never from an agent's opinion of itself.</b> That is the
/// entire reason the ladder was built first. A self-rated reward is a reward the model can move without
/// improving anything.</para>
/// </summary>
public sealed class AgentReliability
{
    /// <summary>
    /// Where an agent starts before it has done anything.
    ///
    /// <para>Neither 0 nor 1, and the choice matters. Zero would multiply an agent out of contention
    /// permanently, since <c>e^(η·0)</c> against established peers never recovers on the handful of
    /// trajectories a real session produces. One would have every new agent outrank proven ones.
    /// Starting neutral means the prior decides until evidence exists — which, for most users, is
    /// forever.</para>
    /// </summary>
    public const double NeutralPrior = 0.5d;

    /// <summary>How fast recent evidence displaces old. 0.3 puts roughly half the weight in the last two
    /// or three observations — fast enough to notice a provider change, slow enough that one unlucky
    /// turn does not condemn an agent.</summary>
    public const double DefaultSmoothing = 0.3d;

    private readonly Dictionary<AgentRole, double> _scores = [];
    private readonly Dictionary<AgentRole, int> _counts = [];
    private readonly double _alpha;
    private readonly Lock _gate = new();

    public AgentReliability(double smoothing = DefaultSmoothing)
    {
        if (smoothing is <= 0d or > 1d)
            throw new ArgumentOutOfRangeException(nameof(smoothing), smoothing, "Smoothing must be in (0, 1].");
        _alpha = smoothing;
    }

    /// <summary>The current estimate, or <see cref="NeutralPrior"/> for an agent with no history.</summary>
    public double Of(AgentRole role)
    {
        lock (_gate) return _scores.TryGetValue(role, out var score) ? score : NeutralPrior;
    }

    /// <summary>How many outcomes have been folded in. Exposed so a caller can tell a genuine estimate
    /// from the neutral prior wearing the same number.</summary>
    public int ObservationsFor(AgentRole role)
    {
        lock (_gate) return _counts.GetValueOrDefault(role);
    }

    /// <summary>Folds in one outcome from the verifier.</summary>
    /// <param name="succeeded">Whether the ladder passed the work this agent produced.</param>
    public void Record(AgentRole role, bool succeeded) => Record(role, succeeded ? 1d : 0d);

    /// <summary>
    /// Folds in a graded outcome, for when a partial result is worth partial credit — clearing six rungs
    /// of eight is not the same as failing to compile, and scoring both as zero throws away most of what
    /// the ladder measured.
    /// </summary>
    public void Record(AgentRole role, double reward)
    {
        if (!double.IsFinite(reward))
            throw new ArgumentOutOfRangeException(nameof(reward), reward, "Reward must be finite.");

        var clamped = Math.Clamp(reward, 0d, 1d);
        lock (_gate)
        {
            var current = _scores.TryGetValue(role, out var score) ? score : NeutralPrior;
            _scores[role] = ((1d - _alpha) * current) + (_alpha * clamped);
            _counts[role] = _counts.GetValueOrDefault(role) + 1;
        }
    }

    /// <summary>A snapshot, for logging a routing decision alongside the evidence behind it.</summary>
    public IReadOnlyDictionary<AgentRole, double> Snapshot()
    {
        lock (_gate)
            return Enum.GetValues<AgentRole>().ToDictionary(
                role => role,
                role => _scores.TryGetValue(role, out var score) ? score : NeutralPrior);
    }
}
