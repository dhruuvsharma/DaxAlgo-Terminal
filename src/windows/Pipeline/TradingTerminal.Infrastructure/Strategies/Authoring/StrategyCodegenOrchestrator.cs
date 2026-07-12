using System.Text;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>The result of a build-loop run: whether it produced a compiling strategy, the final compile
/// result (with its diagnostics — including any policy-scan block), the full conversation transcript for
/// the UI, a provider-level error if the provider itself failed, and how many generations it took.</summary>
public sealed record StrategyBuildLoopResult(
    bool Success,
    StrategyCompileResult? Compile,
    IReadOnlyList<CodegenMessage> Transcript,
    string? ProviderError,
    int Attempts);

/// <summary>
/// Drives the AI builder's core loop: generate source from the instruction, compile it through the SAME
/// <see cref="IStrategyCompiler"/> the manual pane uses (so the policy scan + version gates apply), and
/// on a compile failure feed the diagnostics back to the model and retry — up to a bounded number of
/// attempts. Both the in-app pane and the <c>daxalgo strategy ai</c> CLI drive this; it is provider- and
/// UI-agnostic, so it tests end-to-end against a fake client + the real Roslyn compiler.
/// <para>
/// It never registers anything: it returns the <see cref="StrategyCompileResult"/> and the caller
/// decides whether to save it to the catalog — through the same scan + consent gate as a plugin. A
/// generated strategy that uses blocked APIs (P/Invoke, Process, …) simply never compiles, so it can't
/// leave this loop as a success.
/// </para>
/// </summary>
public sealed class StrategyCodegenOrchestrator(IStrategyCompiler compiler, ILogger<StrategyCodegenOrchestrator>? logger = null)
{
    private readonly IStrategyCompiler _compiler = compiler;
    private readonly ILogger? _logger = logger;

    public async Task<StrategyBuildLoopResult> BuildAsync(
        IStrategyCodegenClient client,
        string systemContext,
        string instruction,
        string strategyId,
        string displayName,
        int maxFixAttempts,
        CancellationToken ct = default)
    {
        // At least one generation; maxFixAttempts is the number of ADDITIONAL error-feedback retries.
        var totalAttempts = Math.Max(1, maxFixAttempts + 1);
        var messages = new List<CodegenMessage> { new(CodegenRole.User, instruction) };
        StrategyCompileResult? lastCompile = null;

        for (var attempt = 1; attempt <= totalAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var response = await client.GenerateAsync(new StrategyCodegenRequest(systemContext, messages), ct)
                .ConfigureAwait(false);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Code))
            {
                // A provider-level failure (auth, timeout, no CLI) — not a compile error. Stop; retrying
                // won't fix a missing key.
                _logger?.LogWarning("Codegen provider {Provider} failed: {Error}", client.ProviderId, response.Error);
                return new StrategyBuildLoopResult(false, lastCompile, messages, response.Error ?? "The provider returned no code.", attempt - 1);
            }

            // Record the model's turn verbatim so the transcript reads naturally and the next call has context.
            messages.Add(new CodegenMessage(CodegenRole.Assistant, response.RawText ?? response.Code));

            var compile = _compiler.Compile(new StrategyScript(strategyId, displayName, response.Code));
            lastCompile = compile;
            if (compile.Success)
            {
                _logger?.LogInformation("AI-authored strategy {Id} compiled on attempt {Attempt}/{Total}",
                    strategyId, attempt, totalAttempts);
                return new StrategyBuildLoopResult(true, compile, messages, null, attempt);
            }

            if (attempt < totalAttempts)
                messages.Add(new CodegenMessage(CodegenRole.User, FixPrompt(compile)));
        }

        _logger?.LogWarning("AI-authored strategy {Id} did not compile after {Total} attempts", strategyId, totalAttempts);
        return new StrategyBuildLoopResult(false, lastCompile, messages, null, totalAttempts);
    }

    /// <summary>The auto-fix message: the compiler's own errors, verbatim, and a request for the whole
    /// corrected file (partial diffs confuse the single-file contract).</summary>
    private static string FixPrompt(StrategyCompileResult compile)
    {
        var sb = new StringBuilder("The code did not compile. Fix these errors and return the COMPLETE corrected file:\n");
        foreach (var error in compile.Errors)
            sb.Append("- ").Append(error.Id).Append(" (line ").Append(error.Line).Append("): ").AppendLine(error.Message);
        return sb.ToString();
    }
}
