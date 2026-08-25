using TradingTerminal.Core.Strategies.Parameters;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>
/// Rung 5 — what the unit declared against what it actually read.
///
/// <para>This catches the commonest silent failure a language model produces: declaring a parameter and
/// then hard-coding the value. Nothing else can see it. The unit compiles, instantiates, runs, draws and
/// trades; the user drags the slider and <b>nothing happens</b>, which reads as a broken application
/// rather than a wrong strategy — so it does not even get reported as a bug in the unit.</para>
///
/// <para>It is also the cheapest rung after compilation, which is why it sits this low on the ladder.</para>
/// </summary>
public static class SchemaCoherenceProbe
{
    /// <param name="schema">What the unit declared.</param>
    /// <param name="keysRead">What it actually asked for, from <see cref="RecordingParameters"/> after a
    /// drive that reached every callback the unit implements.</param>
    /// <param name="drivenToCompletion">
    /// False when the drive stopped early — a compile error, a throw, a cancelled run. An unread
    /// parameter is then evidence of nothing, because the code that would have read it may never have
    /// run, and reporting it would send a repair agent to rewrite correct code.
    /// </param>
    public static VerificationStep Run(
        StrategyParameterSchema schema,
        IReadOnlyCollection<string> keysRead,
        bool drivenToCompletion = true)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(keysRead);

        var declared = schema.Parameters.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

        // Read-but-not-declared first: it is the harder fault. The real IParameters throws on an unknown
        // key, so this unit fails the moment it starts — a crash, not a subtle wrongness.
        var undeclared = keysRead.Where(key => !declared.Contains(key)).OrderBy(k => k, StringComparer.Ordinal).ToArray();
        if (undeclared.Length > 0)
        {
            return VerificationStep.Fail(
                VerificationRung.SchemaCoherence,
                new VerificationFinding(
                    "schema.undeclared",
                    $"Read {Quote(undeclared)}, which {(undeclared.Length == 1 ? "is" : "are")} not in the schema.",
                    "Either add the parameter to Schema or read the key that is declared. Reading an "
                    + "undeclared key throws at start-up."));
        }

        if (declared.Count == 0)
            return VerificationStep.Skip(VerificationRung.SchemaCoherence);

        if (!drivenToCompletion)
            return VerificationStep.Skip(VerificationRung.SchemaCoherence);

        var unread = declared.Except(keysRead, StringComparer.Ordinal)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToArray();
        if (unread.Length == 0) return VerificationStep.Pass(VerificationRung.SchemaCoherence);

        return VerificationStep.Fail(
            VerificationRung.SchemaCoherence,
            new VerificationFinding(
                "schema.declared-not-read",
                $"Declared {Quote(unread)} but never read {(unread.Length == 1 ? "it" : "them")}.",
                "The editor will show the control and changing it will do nothing, which looks like a "
                + "broken application rather than a wrong strategy. Read the value through "
                + "context.Parameters, or drop it from the schema."));
    }

    private static string Quote(IReadOnlyList<string> keys) =>
        string.Join(", ", keys.Select(key => $"'{key}'"));
}
