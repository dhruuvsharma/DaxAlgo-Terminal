using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Swarm;

/// <summary>
/// What a run reports while it is happening.
///
/// <para><b>Reported as it starts, not as it finishes</b> — which is the opposite end from where the
/// committee reported, and the reason it read as a hang. A ten-task run takes minutes; a pane that shows
/// nothing until a task completes tells the user what has already happened and nothing about the silence
/// they are sitting in.</para>
/// </summary>
public abstract record SwarmEvent
{
    /// <summary>The planner has been asked for a plan.</summary>
    public sealed record Planning : SwarmEvent;

    /// <summary>A plan exists. <paramref name="Origin"/> says whether the model produced it.</summary>
    public sealed record Planned(BuildPlan Plan, PlanOrigin Origin) : SwarmEvent;

    /// <summary>A milestone's tasks are about to run.</summary>
    public sealed record MilestoneStarted(Milestone Milestone) : SwarmEvent;

    /// <summary>One task has been handed to a builder.</summary>
    public sealed record TaskStarted(BuildTask Task, bool IsRepair) : SwarmEvent;

    /// <summary>One task is done. <paramref name="Wrote"/> is false when the builder returned no usable
    /// file, which is a turn the user paid for and must be able to see.</summary>
    public sealed record TaskFinished(BuildTask Task, bool Wrote, CodegenUsage Usage, string? Note) : SwarmEvent;

    /// <summary>The whole unit has been compiled and run up the ladder.</summary>
    public sealed record Gated(VerificationReport Report, int Round) : SwarmEvent;

    /// <summary>The critics have judged a unit that cleared the ladder.</summary>
    public sealed record Reviewed(Gauntlet.GauntletResult Result, int Round) : SwarmEvent;

    /// <summary>The run is over.</summary>
    public sealed record Finished(SwarmOutcome Outcome, string Summary) : SwarmEvent;
}

/// <summary>How a run ended.</summary>
public enum SwarmOutcome
{
    /// <summary>The unit compiled and cleared the ladder.</summary>
    Delivered,

    /// <summary>
    /// Repair stopped repairing: several rounds in a row bought no ground, so the run ended rather than
    /// spending the rest of the budget on the same wall.
    ///
    /// <para>Named separately from <see cref="BudgetExhausted"/> because the two mean opposite things to
    /// whoever is paying: a budget running out says "it needed more room", and this says "more room
    /// would have bought nothing". Reporting a wall as a budget invites another spend.</para>
    /// </summary>
    Stalled,

    /// <summary>The round budget ran out first.</summary>
    BudgetExhausted,

    /// <summary>The provider failed: auth, timeout, no CLI. Retrying will not fix a missing key.</summary>
    ProviderFailed,

    /// <summary>The user stopped it. Whatever was built is kept.</summary>
    Cancelled,

    /// <summary>
    /// The planner asked the user something instead of planning. Not a failure — the run is waiting.
    ///
    /// <para>Distinguishing this from a planner that simply mumbled is the difference between an
    /// interview and a silently discarded question. A model that replies "which instrument?" to a
    /// vague brief is doing its job; treating that as unparseable JSON and building anyway throws
    /// away the one turn where the user could have corrected it.</para>
    /// </summary>
    AwaitingUser,
}

/// <summary>
/// The limits a run may not exceed, and the one place they are written down.
/// </summary>
/// <param name="MaxParallel">Builders in flight at once. <b>One for an agent CLI</b>: each is a process
/// with a workspace, and four of them is four checkouts of the same unit.</param>
/// <param name="MaxRounds">Gate-and-repair rounds after the first build. The user's money.</param>
/// <param name="MaxTasks">What the planner may ask for. A planner rewarded for looking thorough will
/// decompose a moving average into six tasks.</param>
/// <param name="StallLimit">Consecutive rounds that may buy no ground before the run stops.</param>
public sealed record SwarmBudget(int MaxParallel, int MaxRounds, int MaxTasks, int StallLimit = 3)
{
    /// <summary>
    /// What a build profile buys, expressed as limits.
    ///
    /// <para>Standard is deliberately a swarm of one: a plan, a builder and the gate. It is still the
    /// cheap position — the plan is a small call and the builder is the call the single conversation
    /// would have made anyway — but it is the same code path, so the two modes cannot drift into two
    /// products the way the committee and the conversation did.</para>
    /// </summary>
    public static SwarmBudget For(StrategyBuildProfile profile, bool isAgentCli = false)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var parallel = isAgentCli ? 1 : profile.UseAgents ? 4 : 1;
        return new SwarmBudget(
            MaxParallel: parallel,
            MaxRounds: Math.Max(1, profile.MaxFixAttempts),
            MaxTasks: profile.UseAgents ? 8 : 1);
    }
}
