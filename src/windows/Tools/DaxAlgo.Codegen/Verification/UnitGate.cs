using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

/// <summary>What one pass over the gate concluded.</summary>
/// <param name="Report">The ladder's verdict — the only thing a repair turn is given.</param>
/// <param name="Compile">The compile behind it, so a caller can register what was built without
/// compiling it a second time. Null only when compilation itself threw.</param>
public sealed record GateResult(VerificationReport Report, StrategyCompileResult? Compile)
{
    /// <summary>The unit is deliverable: it compiled and nothing the ladder ran came back failed.</summary>
    public bool Passed => Report.Passed;

    /// <summary>The resolved unit, when there is one.</summary>
    public AuthoredUnit? Unit => Compile?.Unit;
}

/// <summary>
/// Compiles a candidate and runs it up all eight rungs in one pass — the objective half of the build,
/// and the only thing allowed to say a unit is wrong for free.
///
/// <para>Rungs 1 to 4 come from the compiler: extraction happened upstream, and compile, policy scan and
/// shape are what <see cref="IStrategyCompiler"/> already does. Rungs 5 to 8 come from
/// <see cref="AuthoredUnitVerifier"/>, which needs a resolved type and so cannot run any earlier. Both
/// halves existed; this is what joins them.</para>
///
/// <para><b>Stateless, and that is the change from what it replaced.</b> It used to carry a
/// <c>RoutingState</c> — a private copy of what the session knew, advanced on every verdict — so a judge
/// and its caller each held a half-truth about the same session and the two drifted. It now answers
/// exactly one question, "is this candidate wrong, and where", and leaves knowing anything else to the
/// thing that owns the session.</para>
///
/// <para>It is also the gate in the Prime Agent sense: cheap, deterministic, and run before anything
/// expensive. Nothing that costs a model call ever sees a candidate that has not cleared this.</para>
/// </summary>
public sealed class UnitGate(IStrategyCompiler compiler, string strategyId, string displayName)
{
    private readonly IStrategyCompiler _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));

    /// <summary>The compile behind the latest verdict.</summary>
    public StrategyCompileResult? Latest { get; private set; }

    /// <summary>Compiles the files and runs the ladder over what came out.</summary>
    public GateResult Run(IReadOnlyList<StrategyFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var result = _compiler.Compile(new StrategyScript(strategyId, displayName, files));
        Latest = result;

        if (!result.Success || result.Unit is null)
        {
            // The compiler folds the policy scan into its diagnostics, so a unit reaching for P/Invoke
            // or the registry arrives here as a compile failure — which is the right severity: it is
            // refused, not warned about.
            var findings = result.Diagnostics
                .Where(d => d.Severity == StrategyDiagnosticSeverity.Error)
                .Select(d => new VerificationFinding(
                    d.Id,
                    $"{d.Location}: {d.Message}",
                    "Fix the diagnostic. The line and column are in the message.",
                    File: string.IsNullOrEmpty(d.File) ? null : d.File))
                .DefaultIfEmpty(new VerificationFinding(
                    "compile.failed", "The code did not compile.", "Read the diagnostics."))
                .ToArray();

            return new GateResult(
                new VerificationReport(
                    [new VerificationStep(VerificationRung.Compile, VerificationOutcome.Failed, findings)]),
                result);
        }

        var verified = AuthoredUnitVerifier.Verify(result.Unit);

        // Compile, policy and shape all passed to get here, so they are recorded as cleared rather than
        // left out — a report that omits them would understate how much was actually checked, and the
        // score is computed from exactly that.
        var full = new VerificationReport(
        [
            VerificationStep.Pass(VerificationRung.Compile),
            VerificationStep.Pass(VerificationRung.Policy),
            VerificationStep.Pass(VerificationRung.Shape),
            .. verified.Steps,
        ]);

        return new GateResult(full, result);
    }
}
