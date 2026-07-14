using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>The result of a one-shot build: whether it produced a compiling strategy, the final compile
/// result (with its diagnostics — including any policy-scan block), the full conversation transcript for
/// the UI, a provider-level error if the provider itself failed, how many generations it took, and the
/// files it produced.</summary>
public sealed record StrategyBuildLoopResult(
    bool Success,
    StrategyCompileResult? Compile,
    IReadOnlyList<CodegenMessage> Transcript,
    string? ProviderError,
    int Attempts,
    string? Code = null,
    IReadOnlyList<StrategyFile>? Files = null,
    CodegenUsage? Usage = null);

/// <summary>
/// Creates <see cref="StrategyBuildSession"/>s — a running conversation with one provider about one
/// strategy — and offers the one-shot <see cref="BuildAsync"/> for callers that just want
/// "instruction in, compiling strategy out" (the <c>daxalgo strategy ai</c> CLI, the tests).
/// <para>
/// Every session compiles through the SAME <see cref="IStrategyCompiler"/> the manual editor uses, so
/// the policy scan + version gates apply to model-written code exactly as they do to a pasted snippet.
/// Nothing here registers anything: the caller decides what to do with a compiled result, through the
/// same scan + consent gate as a plugin. Generated code that reaches for a blocked API (P/Invoke,
/// Process, the registry, …) simply never compiles, so it cannot leave a session as a success.
/// </para>
/// </summary>
public sealed class StrategyCodegenOrchestrator(IStrategyCompiler compiler, ILogger<StrategyCodegenOrchestrator>? logger = null)
{
    private readonly IStrategyCompiler _compiler = compiler;
    private readonly ILogger? _logger = logger;

    /// <summary>Opens a conversation — or resumes one, when <paramref name="history"/> carries a thread
    /// restored from disk. The caller drives it turn by turn with
    /// <see cref="StrategyBuildSession.SendAsync"/>, which is what lets the model ask questions back.</summary>
    public StrategyBuildSession CreateSession(
        IStrategyCodegenClient client,
        string systemContext,
        string strategyId,
        string displayName,
        int maxFixAttempts,
        IReadOnlyList<CodegenMessage>? history = null,
        CodegenUsage? priorUsage = null) =>
        new(_compiler, client, systemContext, strategyId, displayName, maxFixAttempts, _logger, history, priorUsage);

    /// <summary>One-shot: a single instruction taken as far as the auto-fix bound allows.</summary>
    public async Task<StrategyBuildLoopResult> BuildAsync(
        IStrategyCodegenClient client,
        string systemContext,
        string instruction,
        string strategyId,
        string displayName,
        int maxFixAttempts,
        CancellationToken ct = default)
    {
        var session = CreateSession(client, systemContext, strategyId, displayName, maxFixAttempts);
        var turn = await session.SendAsync(instruction, activity: null, ct).ConfigureAwait(false);

        return new StrategyBuildLoopResult(
            Success: turn.Success,
            Compile: turn.Compile,
            Transcript: session.Transcript,
            ProviderError: turn.Kind == BuildTurnKind.ProviderError ? turn.Error : null,
            Attempts: turn.Generations,
            Code: turn.Files.Count > 0 ? turn.Files[0].Content : null,
            Files: turn.Files,
            Usage: turn.Usage);
    }
}
