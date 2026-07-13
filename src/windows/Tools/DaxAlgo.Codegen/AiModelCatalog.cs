namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// The model shortlist a provider offers in the picker without a network call, so the builder is usable
/// offline and before a key is entered. It is deliberately NOT an exhaustive list: providers ship models
/// faster than this file can track them, so the picker is editable (free-text model id) and offers
/// "refresh from provider" (<see cref="IStrategyCodegenClient.ListModelsAsync"/>) wherever the provider
/// exposes a models endpoint. Anything configured in <c>appsettings</c> is always offered too.
/// </summary>
public static class AiModelCatalog
{
    public static IReadOnlyList<string> For(string providerId) => providerId.ToLowerInvariant() switch
    {
        "anthropic" => ["claude-opus-4-8", "claude-sonnet-5", "claude-haiku-4-5-20251001"],

        // Claude Code takes an alias or a full model id; the alias tracks the vendor's current mapping.
        "claude-cli" => ["opus", "sonnet", "haiku"],

        // Everything else (OpenAI, DeepSeek, xAI, OpenRouter, Ollama, Codex): ask the provider, or type it.
        _ => [],
    };

    /// <summary>The picker's list: what the provider suggests, plus the configured model, deduped and
    /// with the configured one first (it is the one that will be used if the user picks nothing).</summary>
    public static IReadOnlyList<string> Offer(string providerId, string? configuredModel)
    {
        var known = For(providerId);
        if (string.IsNullOrWhiteSpace(configuredModel)) return known;

        return known.Contains(configuredModel, StringComparer.OrdinalIgnoreCase)
            ? [configuredModel, .. known.Where(m => !m.Equals(configuredModel, StringComparison.OrdinalIgnoreCase))]
            : [configuredModel, .. known];
    }
}
