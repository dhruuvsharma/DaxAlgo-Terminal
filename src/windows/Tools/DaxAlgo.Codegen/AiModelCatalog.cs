using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The model shortlist a provider offers in the picker without a network call, so the builder is usable
/// offline and before a key is entered. It is deliberately NOT exhaustive: providers ship models faster
/// than this file can track them, so the picker is editable (type any model id) and offers "refresh from
/// provider" (<see cref="IStrategyCodegenClient.ListModelsAsync"/>) wherever the provider exposes a
/// models endpoint. Whatever is configured in <c>appsettings</c> is always offered too.
/// <para>
/// Only providers whose model ids are known are listed. Guessing ids for the others would put dead
/// entries in the picker — they get their list from the provider itself, or from the user typing one.
/// </para>
/// </summary>
public static class AiModelCatalog
{
    /// <summary>Anthropic model ids — the same strings Claude Code's <c>--model</c> accepts, so the API
    /// provider and the installed CLI offer the same list (the CLI also takes the short aliases).</summary>
    private static readonly string[] AnthropicModels =
    [
        "claude-opus-4-8",    // most capable Opus tier — the default for hard strategy work
        "claude-sonnet-5",    // near-Opus quality on coding, cheaper
        "claude-opus-4-7",
        "claude-haiku-4-5",   // fastest / cheapest; no effort or thinking support
        "claude-fable-5",     // most capable overall; premium pricing
    ];

    public static IReadOnlyList<string> For(string providerId) => providerId.ToLowerInvariant() switch
    {
        "anthropic" or "claude-cli" => AnthropicModels,

        // OpenAI, DeepSeek, xAI, OpenRouter, Ollama, Codex: ask the provider (they all expose a models
        // endpoint), or type the id. We don't ship a guessed list that goes stale.
        _ => [],
    };

    /// <summary>The picker's list: what the provider suggests, plus the configured model, deduped and
    /// with the configured one first (it is the one used if the user picks nothing).</summary>
    public static IReadOnlyList<string> Offer(string providerId, string? configuredModel)
    {
        var known = For(providerId);
        if (string.IsNullOrWhiteSpace(configuredModel)) return known;

        return known.Contains(configuredModel, StringComparer.OrdinalIgnoreCase)
            ? [configuredModel, .. known.Where(m => !m.Equals(configuredModel, StringComparison.OrdinalIgnoreCase))]
            : [configuredModel, .. known];
    }

    /// <summary>
    /// Whether the provider takes a reasoning-effort setting at all. Agent CLIs and the Anthropic /
    /// OpenAI-compatible APIs do; a provider that doesn't simply ignores the picker (we never send a
    /// parameter it would reject).
    /// </summary>
    public static bool SupportsEffort(string providerId) => providerId.ToLowerInvariant() switch
    {
        "anthropic" or "claude-cli" or "openai" or "xai" or "openrouter" => true,
        _ => false,
    };
}
