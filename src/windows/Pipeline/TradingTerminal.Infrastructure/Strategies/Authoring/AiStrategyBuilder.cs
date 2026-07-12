using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The one thing the AI builder UI depends on: the available providers, and a "turn this instruction
/// into a compiling strategy" call. It bundles the provider factory, the pack (system prompt), and the
/// build-loop orchestrator so the view-model needs a single injected dependency, and so the same facade
/// backs the CLI. Null when AI codegen isn't wired — the pane hides gracefully.
/// </summary>
public interface IAiStrategyBuilder
{
    /// <summary>Every provider the app knows how to build, available or not — the picker shows all and
    /// disables the unconfigured ones.</summary>
    IReadOnlyList<IStrategyCodegenClient> Providers { get; }

    /// <summary>The provider to select by default (configured default if available, else first available).</summary>
    IStrategyCodegenClient? DefaultProvider { get; }

    /// <summary>Generate a strategy from <paramref name="instruction"/> and drive the compile/auto-fix
    /// loop. Returns the transcript + the final compile result; the caller registers a success through
    /// the same scan/consent path as any authored strategy (this never registers).</summary>
    Task<StrategyBuildLoopResult> BuildAsync(
        IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
        CancellationToken ct = default);
}

public sealed class AiStrategyBuilder(
    StrategyCodegenClientFactory factory,
    StrategyCodegenOrchestrator orchestrator,
    StrategyContextPack pack,
    AiCodegenOptions options) : IAiStrategyBuilder
{
    public IReadOnlyList<IStrategyCodegenClient> Providers => factory.BuildAll();

    public IStrategyCodegenClient? DefaultProvider => factory.SelectDefault();

    public Task<StrategyBuildLoopResult> BuildAsync(
        IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
        CancellationToken ct = default) =>
        orchestrator.BuildAsync(provider, pack.SystemPrompt, instruction, strategyId, displayName, options.MaxFixAttempts, ct);
}
