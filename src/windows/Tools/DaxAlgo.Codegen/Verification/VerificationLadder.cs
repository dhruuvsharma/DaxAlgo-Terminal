namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>Where a candidate stopped. Ordered cheapest-first: a rung only runs when everything below
/// it passed, so a failure names the earliest thing that is actually wrong.</summary>
public enum VerificationRung
{
    /// <summary>Files pulled out of the model's response.</summary>
    Extract = 1,

    /// <summary>Roslyn.</summary>
    Compile = 2,

    /// <summary>Deny-list scan of the compiled IL. Also the safety gate.</summary>
    Policy = 3,

    /// <summary>Exactly one hostable type, correctly shaped.</summary>
    Shape = 4,

    /// <summary>Declared parameters versus the ones the code actually reads.</summary>
    SchemaCoherence = 5,

    /// <summary>Instantiate and drive it.</summary>
    Lifecycle = 6,

    /// <summary>Drive <c>Draw</c> and inspect what came out.</summary>
    DrawProbe = 7,

    /// <summary>Replay real market data and inspect what it did to its book.</summary>
    Replay = 8,
}

/// <summary>How a rung ended.</summary>
public enum VerificationOutcome
{
    Passed,

    /// <summary>The candidate is wrong. Carries diagnostics a repair agent can act on.</summary>
    Failed,

    /// <summary>The rung does not apply — a strategy that draws nothing skips the draw probe, and a
    /// visualizer has no book to replay against. Distinct from <see cref="Passed"/> on purpose: a
    /// skipped rung must never be counted as evidence that anything was checked.</summary>
    NotApplicable,
}

/// <summary>
/// One thing wrong with a candidate, written for a repair agent rather than for a log.
/// </summary>
/// <param name="Code">Stable, greppable, and the key a router can weight on. Never a sentence.</param>
/// <param name="Message">What is wrong, in the terms the author used.</param>
/// <param name="Remedy">What to change. A diagnostic that only describes the symptom sends a model
/// looking for the problem instead of fixing it.</param>
public sealed record VerificationFinding(string Code, string Message, string? Remedy = null)
{
    public override string ToString() =>
        Remedy is null ? $"{Code}: {Message}" : $"{Code}: {Message} — {Remedy}";
}

/// <summary>What one rung concluded.</summary>
public sealed record VerificationStep(
    VerificationRung Rung,
    VerificationOutcome Outcome,
    IReadOnlyList<VerificationFinding> Findings)
{
    public static VerificationStep Pass(VerificationRung rung) => new(rung, VerificationOutcome.Passed, []);

    public static VerificationStep Skip(VerificationRung rung) =>
        new(rung, VerificationOutcome.NotApplicable, []);

    public static VerificationStep Fail(VerificationRung rung, params VerificationFinding[] findings) =>
        new(rung, VerificationOutcome.Failed, findings);
}

/// <summary>
/// The whole verdict on one candidate.
///
/// <para><see cref="Passed"/> is deliberately strict: every rung that ran must have passed, and a rung
/// that never ran is not a rung that passed. Reward is computed from this, so an optimistic reading here
/// is a reward-hacking surface rather than a convenience.</para>
/// </summary>
public sealed record VerificationReport(IReadOnlyList<VerificationStep> Steps)
{
    public bool Passed => Steps.Count > 0 && Steps.All(s => s.Outcome != VerificationOutcome.Failed);

    /// <summary>The earliest rung that failed — the one worth repairing, since later failures are often
    /// consequences of it.</summary>
    public VerificationRung? FailedAt => Steps
        .Where(s => s.Outcome == VerificationOutcome.Failed)
        .Select(s => (VerificationRung?)s.Rung)
        .FirstOrDefault();

    public IReadOnlyList<VerificationFinding> Findings =>
        [.. Steps.SelectMany(s => s.Findings)];

    /// <summary>The rungs that actually checked something. This is what a reliability estimate should be
    /// weighted by — a candidate that skipped five rungs has not earned the same credit as one that
    /// cleared them.</summary>
    public int RungsCleared => Steps.Count(s => s.Outcome == VerificationOutcome.Passed);
}
