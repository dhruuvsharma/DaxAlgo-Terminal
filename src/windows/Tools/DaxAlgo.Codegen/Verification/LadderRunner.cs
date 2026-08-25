namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Runs rungs in order and stops at the first failure.
///
/// <para>Short-circuiting is not an optimisation, it is what makes the report readable. A unit that
/// fails to compile also fails to instantiate, fails to draw and fails to trade; reporting all four
/// describes one fault four times and leaves a repair agent to work out which came first. The earliest
/// failure is the one worth fixing, so it is the only one reported.</para>
///
/// <para>Rungs are supplied as thunks rather than results because a rung that has not run must not have
/// its cost paid. Driving a unit that will not compile is not merely wasteful — it is undefined.</para>
/// </summary>
public static class LadderRunner
{
    /// <summary>Runs each rung until one fails, and returns everything that ran.</summary>
    public static VerificationReport Run(params Func<VerificationStep>[] rungs)
    {
        ArgumentNullException.ThrowIfNull(rungs);

        var steps = new List<VerificationStep>();
        foreach (var rung in rungs)
        {
            var step = rung();
            steps.Add(step);
            if (step.Outcome == VerificationOutcome.Failed) break;
        }

        return new VerificationReport(steps);
    }

    /// <summary>
    /// Runs each rung, catching anything a rung itself throws.
    ///
    /// <para>A probe that throws is a bug in the verifier, not in the candidate. Letting it escape would
    /// take down the build that was verifying somebody's strategy and report nothing about the strategy
    /// — so it is turned into a finding that says plainly which is at fault, and the ladder stops.</para>
    /// </summary>
    public static VerificationReport RunGuarded(params (VerificationRung Rung, Func<VerificationStep> Run)[] rungs)
    {
        ArgumentNullException.ThrowIfNull(rungs);

        var steps = new List<VerificationStep>();
        foreach (var (rung, run) in rungs)
        {
            VerificationStep step;
            try
            {
                step = run();
            }
            catch (Exception ex)
            {
                step = VerificationStep.Fail(
                    rung,
                    new VerificationFinding(
                        "ladder.probe-faulted",
                        $"The {rung} probe itself threw {ex.GetType().Name}: {ex.Message}",
                        "This is a fault in the verifier, not in the candidate. The candidate has not "
                        + "been judged."));
            }

            steps.Add(step);
            if (step.Outcome == VerificationOutcome.Failed) break;
        }

        return new VerificationReport(steps);
    }
}
