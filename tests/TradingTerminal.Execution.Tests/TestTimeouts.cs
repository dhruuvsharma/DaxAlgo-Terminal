namespace TradingTerminal.Execution.Tests;

/// <summary>
/// Shared upper bounds for tests that wait on an asynchronous signal.
/// </summary>
internal static class TestTimeouts
{
    /// <summary>
    /// How long to wait for a signal that arrives in milliseconds when the code is correct.
    ///
    /// <para>This is a DEADLOCK DETECTOR, not a performance assertion. No test here is claiming the
    /// system is fast; the only question the bound answers is "did this hang?". It is therefore
    /// deliberately generous — during a full-solution run a dozen test assemblies compete for cores,
    /// and the short ad-hoc bounds this replaced (2–5 s) turned ordinary CPU contention into random
    /// red builds that passed on rerun. A wait that actually reaches this bound is a real hang.</para>
    ///
    /// <para>Tests asserting that something does NOT complete must not use this — they need a short,
    /// explicit bound of their own, and should say so at the call site.</para>
    /// </summary>
    public static readonly TimeSpan Deadlock = TimeSpan.FromSeconds(60);
}
