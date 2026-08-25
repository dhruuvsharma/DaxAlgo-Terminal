using System.Diagnostics;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Rung 6 — start the unit, drive it, stop it, and see whether it survives.
///
/// <para>Replaces a synthetic smoke test that ran forty-eight fabricated ticks past a stub clock and a
/// stub router. This drives the real callbacks in the real order, which is where the faults actually
/// are: warm-up code that indexes an empty history on the first bar, an <c>OnStartAsync</c> that reads a
/// parameter it never declared, a callback that throws once a value goes negative.</para>
///
/// <para>It also holds a wall-clock budget. A unit that blocks — <c>Thread.Sleep</c>, a <c>.Result</c> on
/// something that never completes, a loop that does not terminate — cannot be distinguished from one
/// that is merely slow, and both are fatal in a host that calls this from a pump thread. The budget is
/// generous enough that no honest unit meets it.</para>
/// </summary>
public static class LifecycleProbe
{
    /// <summary>How long a full drive may take. Generous on purpose: this is a liveness check, not a
    /// performance one, and a false positive here would fail a correct unit on a busy machine.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    /// <summary>Runs <paramref name="drive"/> and reports what happened.</summary>
    /// <param name="drive">Start, feed, and stop the unit. Should surface exceptions rather than swallow
    /// them — a swallowed fault here is a fault that reaches a user instead.</param>
    /// <param name="phase">
    /// Which part of the lifecycle is running, so a failure names it. The caller knows; the probe cannot
    /// see inside the delegate, and a diagnostic that says only "it threw" makes a repair agent read the
    /// whole file to find out where.
    /// </param>
    public static VerificationStep Run(Action drive, string phase = "the drive")
    {
        ArgumentNullException.ThrowIfNull(drive);

        var stopwatch = Stopwatch.StartNew();
        try
        {
            drive();
        }
        catch (Exception ex)
        {
            return VerificationStep.Fail(
                VerificationRung.Lifecycle,
                new VerificationFinding(
                    "lifecycle.threw",
                    $"{ex.GetType().Name} during {phase}: {ex.Message}",
                    Remedy(ex)));
        }
        finally
        {
            stopwatch.Stop();
        }

        return stopwatch.Elapsed > Budget
            ? VerificationStep.Fail(
                VerificationRung.Lifecycle,
                new VerificationFinding(
                    "lifecycle.too-slow",
                    $"{phase} took {stopwatch.Elapsed.TotalSeconds:0.#}s, past the "
                    + $"{Budget.TotalSeconds:0}s budget.",
                    "The callbacks run on a pump thread that may fire hundreds of times a second. Do "
                    + "not block, do not sleep, and do not call .Result or .Wait()."))
            : VerificationStep.Pass(VerificationRung.Lifecycle);
    }

    /// <summary>
    /// The remedy is chosen from the exception type, because these three are almost always the same
    /// three mistakes and naming the mistake saves a round trip that the user pays for.
    /// </summary>
    private static string Remedy(Exception ex) => ex switch
    {
        ArgumentOutOfRangeException or IndexOutOfRangeException =>
            "Almost always the warm-up: indexing history before enough of it has arrived. Return early "
            + "until RecentBars gives you the count you need.",

        KeyNotFoundException =>
            "A parameter was read that the schema does not declare. Add it to Schema, or read the key "
            + "that is declared.",

        DivideByZeroException =>
            "A count or span was still zero. Guard the warm-up, and remember that a flat price series "
            + "gives a zero-width range.",

        NullReferenceException =>
            "State used before OnStartAsync initialised it, or a lookup that returned nothing. Initialise "
            + "every field in OnStartAsync rather than at the first callback.",

        _ => "The callbacks run inside a host that cannot recover from a throw. Guard the inputs "
            + "instead: market data contains zero, negative and non-finite values.",
    };
}
