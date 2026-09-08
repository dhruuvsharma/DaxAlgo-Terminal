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
        "claude-opus-5",      // current Opus flagship — the default for hard strategy work
        "claude-sonnet-5",    // near-Opus quality on coding, cheaper
        "claude-opus-4-8",    // the previous Opus tier, kept for anyone pinned to it
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

        // TOKENROUTER IS DELIBERATELY NOT HERE, and the reason corrects my own earlier claim.
        //
        // I added it after measuring that reasoning_effort=low and =high were both ACCEPTED -- no
        // error, valid reply. That is the wrong test. Accepted is not usable: at "high",
        // z-ai/glm-5.3-free reasons until the budget is gone and never begins an answer. Measured on
        // a real brief at Max effort -- 1,088 seconds, 95,763 output tokens, no code -- against the
        // same brief at the provider's own default, which compiled in 374 seconds on one generation.
        //
        // So the model is left on its default, which works. The lesson generalises to every row here:
        // the question is not whether the parameter is refused, it is whether the model still answers.

        // OpenCode Zen fronts models from several vendors behind one OpenAI-compatible endpoint, and
        // which of them read a reasoning-effort parameter depends on the model chosen rather than on
        // the gateway. Left out deliberately: sending an effort a model does not accept is refused, and
        // a refusal here reads as a bad key.
        // NVIDIA NIM and OpenCode Zen both front models from several vendors behind one
        // OpenAI-compatible endpoint, so whether reasoning_effort is read depends on the model rather
        // than the gateway. Left off: a parameter a model rejects fails the request, and that failure
        // reads as a bad key. kimi-k3 does accept it — turn this on per-provider if you pin a model
        // that takes it.
        "opencode" or "nvidia" => false,
        _ => false,
    };

    /// <summary>
    /// What <see cref="CodegenMode.Research"/> actually sends for a given model: the highest reasoning
    /// setting that model is known to <b>still answer at</b>, or <see cref="CodegenEffort.Default"/>
    /// when there is none.
    ///
    /// <para><b>Answering is the test, not acceptance.</b> That distinction is the whole reason this
    /// method exists rather than a boolean. <c>z-ai/glm-5.3-free</c> accepts <c>reasoning_effort=high</c>
    /// without complaint and then reasons until its budget is gone: measured 2026-09-01 on a real brief
    /// at 1,088 seconds and 95,763 output tokens for no code, against the same brief on the provider's
    /// own default, which compiled in 374 seconds. A dial that offered Research there would offer a
    /// setting that has never produced anything.</para>
    ///
    /// <para>Ceilings are conservative on purpose. Sending an effort a model does not take fails the
    /// request, and a failed request reads to a user as a bad key — so an unmeasured model gets the
    /// value its provider documents, not the highest this enum can express.</para>
    /// </summary>
    /// <param name="providerId">The provider the request goes to.</param>
    /// <param name="model">The model id, which is what actually decides this on a gateway fronting
    /// several vendors.</param>
    public static CodegenEffort ResearchEffort(string providerId, string? model)
    {
        // Model first: a gateway's ceiling is a property of what is behind it, not of the gateway.
        if (Measured(model) is { } measured) return measured;

        return providerId.ToLowerInvariant() switch
        {
            // Extended thinking, and the CLI carries the user's own subscription rather than a budget
            // this account has to afford.
            "anthropic" or "claude-cli" => CodegenEffort.Max,

            // Documented ceiling for the reasoning series. XHigh and Max are not measured here and a
            // rejected parameter reads as a bad key, so this stops at the value the API documents.
            "openai" or "xai" or "openrouter" => CodegenEffort.High,

            // No usable research setting. tokenrouter is the measured case above; the rest either take
            // no effort parameter at all or front several vendors behind one endpoint, where the answer
            // depends on the model and none has been measured.
            _ => CodegenEffort.Default,
        };
    }

    /// <summary>True when Research would send something other than the model's own default.</summary>
    public static bool ResearchAvailable(string providerId, string? model) =>
        ResearchEffort(providerId, model) != CodegenEffort.Default;

    /// <summary>
    /// The sentence to show when Research is selected on a model that has none, or null when it has
    /// one.
    ///
    /// <para>It says what happened and what is being done about it, because the alternative — a dial
    /// that silently behaves like the position next to it — is how a user concludes the setting does
    /// nothing and stops trusting the rest of them.</para>
    /// </summary>
    public static string? ResearchUnavailable(string providerId, string? model) =>
        ResearchAvailable(providerId, model)
            ? null
            : $"Research mode is not available on {model ?? providerId} — it has no reasoning setting "
              + "that is known to still return an answer. Running at the model's own default instead; "
              + "the rest of Research (the full skill budget, the extra fix attempts, the review pass "
              + "and the agents) still applies.";

    /// <summary>
    /// Per-model ceilings that have actually been measured, which override the provider's.
    ///
    /// <para>Matched on a substring because a model id travels with a vendor prefix on one gateway and
    /// without it on another — <c>z-ai/glm-5.3-free</c> and <c>glm-5.3</c> are the same model and the
    /// same measurement.</para>
    /// </summary>
    private static CodegenEffort? Measured(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;

        var id = model.ToLowerInvariant();

        // Accepted, then silent. See the remarks above.
        if (id.Contains("glm-5.3", StringComparison.Ordinal)) return CodegenEffort.Default;

        return null;
    }
}
