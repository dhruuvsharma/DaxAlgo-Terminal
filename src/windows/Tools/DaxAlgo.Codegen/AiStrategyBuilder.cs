using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The one thing the AI builder UI depends on: the available providers + their models, a conversation
/// to drive (<see cref="StartSession"/>), and a one-shot "instruction in, strategy out" call for the
/// CLI. It bundles the provider factory, the pack (system prompt), and the orchestrator so the
/// view-model needs a single injected dependency. Null when AI codegen isn't wired — the pane hides
/// gracefully.
/// </summary>
public interface IAiStrategyBuilder
{
    /// <summary>Every provider the app knows how to build, available or not — the picker shows all and
    /// disables the unconfigured ones.</summary>
    IReadOnlyList<IStrategyCodegenClient> Providers { get; }

    /// <summary>The provider to select by default (configured default if available, else first available).</summary>
    IStrategyCodegenClient? DefaultProvider { get; }

    /// <summary>The same provider bound to a different model + reasoning effort — what the pickers call.
    /// Null when the provider id is unknown. A blank model means "the configured / vendor default";
    /// <see cref="CodegenEffort.Default"/> sends no effort parameter at all.</summary>
    IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort);

    /// <summary>Models to offer for a provider without a network call (curated + configured). The UI can
    /// additionally ask the provider itself via <see cref="IStrategyCodegenClient.ListModelsAsync"/>.</summary>
    IReadOnlyList<string> ModelsFor(string providerId);

    /// <summary>
    /// The flattened picker: EVERY provider the app can build (available or not — the UI tags the
    /// unavailable ones) × its known models, each row carrying its provider as a label. Providers with
    /// no curated shortlist still get one row — their configured model, or the vendor default (blank
    /// model id) — so every provider always shows in the dropdown.
    /// </summary>
    IReadOnlyList<AiModelChoice> AllModels();

    /// <summary>Opens a conversation about one strategy: the message thread persists across turns, so
    /// follow-ups ("tighten the stop"), the compiler's own errors, and the model's questions all land in
    /// the same context. Pass <paramref name="history"/> to RESUME a thread saved from a previous run —
    /// that is what makes a restored chat more than a transcript. <paramref name="profile"/> sets the
    /// build effort (skill budget, fix attempts, self-review, backtest smoke); null keeps the defaults.
    /// The session never registers anything.</summary>
    StrategyBuildSession StartSession(
        IStrategyCodegenClient provider, string strategyId, string displayName,
        IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
        StrategyBuildProfile? profile = null);

    /// <summary>One-shot: generate from <paramref name="instruction"/> and drive the compile/auto-fix
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

    public IStrategyCodegenClient? WithSettings(string providerId, string? model, CodegenEffort effort) =>
        factory.Build(providerId, model, effort);

    public IReadOnlyList<string> ModelsFor(string providerId) => factory.ModelsFor(providerId);

    public IReadOnlyList<AiModelChoice> AllModels()
    {
        var choices = new List<AiModelChoice>();
        foreach (var client in factory.BuildAll())
        {
            // The curated shortlist + whatever is configured (configured first). A provider with neither
            // still gets one row: its own model if it reports one, else the vendor default (blank id) —
            // an installed CLI with no pinned model is still a perfectly good choice.
            var models = AiModelCatalog.Offer(
                client.ProviderId, string.IsNullOrWhiteSpace(client.Model) ? null : client.Model);
            if (models.Count == 0)
                models = [string.IsNullOrWhiteSpace(client.Model) ? string.Empty : client.Model];

            foreach (var model in models)
                choices.Add(new AiModelChoice(client.ProviderId, client.DisplayName, model)
                {
                    IsAvailable = client.IsAvailable,
                });
        }
        return choices;
    }

    public StrategyBuildSession StartSession(
        IStrategyCodegenClient provider, string strategyId, string displayName,
        IReadOnlyList<CodegenMessage>? history = null, CodegenUsage? priorUsage = null,
        StrategyBuildProfile? profile = null) =>
        orchestrator.CreateSession(
            provider, pack.SystemPrompt, strategyId, displayName, options.MaxFixAttempts, history, priorUsage, profile);

    public Task<StrategyBuildLoopResult> BuildAsync(
        IStrategyCodegenClient provider, string instruction, string strategyId, string displayName,
        CancellationToken ct = default) =>
        orchestrator.BuildAsync(provider, pack.SystemPrompt, instruction, strategyId, displayName, options.MaxFixAttempts, ct);
}
