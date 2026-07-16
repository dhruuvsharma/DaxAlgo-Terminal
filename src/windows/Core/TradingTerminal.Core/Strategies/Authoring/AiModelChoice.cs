namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>
/// One row of the flattened "every model, every provider" picker — the unified dropdown the builder
/// offers instead of making the user pick a provider first and a model second. Providers stay in the
/// entry (<see cref="ProviderLabel"/>) as a tag, so two vendors shipping the same model id never
/// collide.
/// </summary>
/// <param name="ProviderId">The provider's stable id (<c>claude-cli</c>, <c>anthropic</c>, …) — what
/// the existing provider machinery keys on.</param>
/// <param name="ProviderLabel">The provider's display name, shown as the tag after the model id.</param>
/// <param name="ModelId">The model id this row selects. Empty means "the provider's own default" —
/// an installed agent CLI with no pinned model runs whatever the vendor tool is configured for.</param>
public sealed record AiModelChoice(string ProviderId, string ProviderLabel, string ModelId)
{
    /// <summary>False when the provider isn't usable right now (CLI not installed, no API key) — the
    /// row still shows, tagged, so the user learns what setting it up would unlock.</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>"claude-opus-4-8 · Claude Code (installed CLI)". A blank model id (vendor default)
    /// falls back to the provider label alone.</summary>
    public string Display => string.IsNullOrEmpty(ModelId) ? ProviderLabel : $"{ModelId} · {ProviderLabel}";
}
