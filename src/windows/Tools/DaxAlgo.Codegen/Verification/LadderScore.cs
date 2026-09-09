namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Turns a ladder verdict into a number, for the two callers that need one: the trajectory log, which
/// records what a turn bought, and the swarm's stall detector, which stops a run that is buying
/// nothing.
///
/// <para>It lives beside the ladder rather than beside the agents on purpose. It was previously half of
/// <c>LadderFeedback</c>, whose other half advanced a routing state — so scoring a report required
/// knowing what an agent was, and the router and the ladder could not be changed independently. The
/// router is gone; this half was always the part worth keeping.</para>
/// </summary>
public static class LadderScore
{
    /// <summary>Rungs the ladder can run. Fixed rather than derived, and the reason is in
    /// <see cref="RewardFor"/>.</summary>
    public const int TotalRungs = 8;

    /// <summary>
    /// What a candidate earned, in [0, 1].
    ///
    /// <para><b>Graded, not binary.</b> Clearing six rungs and failing the seventh is not the same
    /// contribution as failing to compile, and scoring both zero throws away nearly everything the
    /// ladder measured.</para>
    ///
    /// <para>Skipped rungs earn nothing. A unit that arranged to be checked by very little must not come
    /// out looking like one that cleared a lot, or that is what a builder will learn to produce.</para>
    /// </summary>
    /// <param name="report">The verdict to score.</param>
    /// <param name="totalRungs">Rungs the ladder could have run. Fixed rather than taken from the
    /// report, because a report that stopped early would otherwise flatter itself by shrinking its own
    /// denominator.</param>
    public static double RewardFor(VerificationReport report, int totalRungs = TotalRungs)
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
    /// How far up the ladder a verdict got, as a single comparable number — the swarm's definition of
    /// progress.
    ///
    /// <para>Rungs dominate findings by a factor no realistic finding count can close, so clearing one
    /// more rung always beats removing diagnostics at the same height. Within a height, fewer findings
    /// IS progress: a repair that fixes one of four errors has bought ground and should keep its budget,
    /// while one that returns the same verdict has not, whatever it says about itself.</para>
    ///
    /// <para>Seeded against <see cref="int.MinValue"/> by callers rather than -1. A verdict that clears
    /// no rung has NEGATIVE height — it is scored down by its own findings — so seeding at -1 made
    /// genuine progress read as none and stalled converging runs at the third round.</para>
    /// </summary>
    public static int HeightOf(VerificationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return (report.RungsCleared * 1000) - report.Findings.Count;
    }
}
