using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>
/// Turns a verification verdict into the two things routing needs: where the session now stands, and
/// what the last agent earned.
///
/// <para>This is the join between the ladder and the router, and it is deliberately the only one. The
/// router never reads a report and the ladder never knows an agent exists — so the reward can be
/// changed without touching either, and neither can quietly start scoring itself.</para>
/// </summary>
public static class LadderFeedback
{
    /// <summary>
    /// What the last turn earned, in [0, 1].
    ///
    /// <para><b>Graded, not binary.</b> Clearing six rungs and failing the seventh is not the same
    /// contribution as failing to compile, and scoring both zero throws away nearly everything the
    /// ladder measured — which would leave the reliability estimate learning only from the coarsest
    /// signal available.</para>
    ///
    /// <para>Skipped rungs earn nothing. A unit that arranged to be checked by very little must not
    /// come out looking like one that cleared a lot, or that is what agents will learn to produce.</para>
    /// </summary>
    /// <param name="totalRungs">Rungs the ladder could have run. Fixed rather than taken from the
    /// report, because a report that stopped early would otherwise flatter itself by shrinking its own
    /// denominator.</param>
    public static double RewardFor(VerificationReport report, int totalRungs = 8)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (totalRungs <= 0) throw new ArgumentOutOfRangeException(nameof(totalRungs));

        if (report.Steps.Count == 0) return 0d;

        var cleared = Math.Min(report.RungsCleared, totalRungs);
        var progress = (double)cleared / totalRungs;

        // Passing dominates failing, because a passing artifact is deliverable and a failing one costs
        // the user another turn whatever it cleared. But it dominates by BAND, not by a floor: the first
        // version returned max(progress, 0.9) for a pass, so a run that skipped almost every rung
        // collected 0.9 — exactly the same as one that cleared them all. That is the reward hack this
        // whole design exists to refuse, sitting in the reward function itself.
        //
        //   passed → 0.5 .. 1.0, rising with how much was actually checked
        //   failed → 0.0 .. 0.5, rising with how far it got
        return report.Passed ? 0.5d + (0.5d * progress) : 0.5d * progress;
    }

    /// <summary>
    /// Folds a verdict into the estimate for whoever produced it.
    /// </summary>
    public static void Record(AgentReliability reliability, AgentRole role, VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(reliability);
        reliability.Record(role, RewardFor(report));
    }

    /// <summary>
    /// Updates the routing state from a verdict, keeping whatever the report cannot know.
    ///
    /// <para>A report says nothing about whether a brief was ever turned into a spec or whether a human
    /// reviewed the result, so those are carried through rather than reset — inferring them would make
    /// a fresh verification look like a fresh session and send the loop back to the Interviewer.</para>
    /// </summary>
    public static RoutingState Advance(RoutingState state, VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(report);

        var compiled = report.Steps.Any(step =>
            step.Rung >= VerificationRung.Compile && step.Outcome == VerificationOutcome.Passed);

        var drew = report.Steps.Any(step =>
            step.Rung == VerificationRung.DrawProbe && step.Outcome == VerificationOutcome.Passed);

        return state with
        {
            HasCode = true,
            Compiles = compiled && report.FailedAt is not VerificationRung.Compile,
            Draws = drew || state.Draws,
            FailedAt = report.FailedAt,
        };
    }
}
