using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.Infrastructure.Strategies.Authoring.Agents;

/// <summary>
/// Compiles what an agent wrote and runs it up the ladder — the real judge the loop takes as a
/// delegate, and the first place all eight rungs are assembled in one pass.
///
/// <para>Rungs 1 to 4 come from the compiler: extraction happened upstream, and compile, policy scan and
/// shape are what <see cref="IStrategyCompiler"/> already does. Rungs 5 to 8 come from
/// <see cref="AuthoredUnitVerifier"/>, which needs a resolved type and so cannot run any earlier. Both
/// halves existed; nothing had joined them.</para>
///
/// <para>Stateful across turns on purpose: a verdict alone cannot say whether this unit owes a picture
/// or whether a human has reviewed it, so the judge carries what the report cannot and hands the loop a
/// state rather than making it guess.</para>
/// </summary>
public sealed class AuthoringJudge(
    IStrategyCompiler compiler,
    string strategyId,
    string displayName,
    RoutingState initial)
{
    private readonly IStrategyCompiler _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
    private RoutingState _state = initial ?? throw new ArgumentNullException(nameof(initial));

    /// <summary>The compile result behind the latest verdict, so a caller can register what was built
    /// without compiling it a second time.</summary>
    public StrategyCompileResult? Latest { get; private set; }

    /// <summary>Where the session stands after the last verdict.</summary>
    public RoutingState State => _state;

    /// <summary>The delegate <see cref="AgentLoop"/> takes.</summary>
    public VerdictAndState Judge(IReadOnlyList<StrategyFile> files)
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
                    d.Message,
                    "Fix the diagnostic. The line and column are in the message."))
                .DefaultIfEmpty(new VerificationFinding(
                    "compile.failed", "The code did not compile.", "Read the diagnostics."))
                .ToArray();

            var failed = new VerificationReport(
                [new VerificationStep(VerificationRung.Compile, VerificationOutcome.Failed, findings)]);

            _state = LadderFeedback.Advance(_state, failed);
            return new VerdictAndState(failed, _state);
        }

        // A visualizer owes a picture; a strategy does not. Taken from the resolved type rather than
        // from the brief, because what the author actually wrote is the only reliable answer.
        var mustDraw = result.Unit.Kind == AuthoringKind.Visualizer;

        var verified = AuthoredUnitVerifier.Verify(result.Unit);

        // Compile, policy and shape all passed to get here, so they are recorded as cleared rather than
        // left out — a report that omits them would understate how much was actually checked, and the
        // reward is computed from exactly that.
        var full = new VerificationReport(
        [
            VerificationStep.Pass(VerificationRung.Compile),
            VerificationStep.Pass(VerificationRung.Policy),
            VerificationStep.Pass(VerificationRung.Shape),
            .. verified.Steps,
        ]);

        _state = LadderFeedback.Advance(_state with { MustDraw = mustDraw }, full) with
        {
            // Reviewed is a human's word, never the ladder's. It stays false until someone says
            // otherwise, which is what keeps the Reviewer in the loop rather than routed past.
            Reviewed = _state.Reviewed,
        };

        return new VerdictAndState(full, _state);
    }

    /// <summary>Marks the unit reviewed, which is the only thing that lets a run finish.</summary>
    public void MarkReviewed() => _state = _state with { Reviewed = true };
}
