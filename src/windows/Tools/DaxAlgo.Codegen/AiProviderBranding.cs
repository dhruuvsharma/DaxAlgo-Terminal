namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>How a provider is recognised in a list, before its name is read.</summary>
/// <param name="Mark">One or two characters for the badge. A monogram, not a logo.</param>
/// <param name="Accent">The badge colour, as <c>#RRGGBB</c>.</param>
/// <param name="Blurb">One line saying what this provider is and what it needs.</param>
public readonly record struct AiProviderBrand(string Mark, string Accent, string Blurb);

/// <summary>
/// A badge and a line of description per provider, so a list of them can be scanned rather than read.
///
/// <para><b>Monograms rather than the vendors' actual logos.</b> Shipping somebody else's wordmark means
/// shipping their asset under their trademark guidelines, and getting that wrong is worse than not
/// having it. A coloured badge in the vendor's own accent is recognisable at list scale, has no
/// licensing question attached, and needs no files — and if real logo assets are ever licensed, this is
/// the one place that has to change.</para>
///
/// <para>Colours are the vendors' published brand accents, used to identify a row rather than to claim
/// endorsement. Anything unknown — including every provider a user adds themselves — falls back to a
/// neutral badge carrying its own initial.</para>
/// </summary>
public static class AiProviderBranding
{
    private static readonly Dictionary<string, AiProviderBrand> Known =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic"] = new("A", "#D97757",
                "Claude, straight from Anthropic. Paste an API key, or sign in with your account."),

            [StrategyCodegenClientFactory.AnthropicOAuthId] = new("A", "#D97757",
                "Claude, signed in with your Anthropic account — no key to paste. Billed per token to "
                + "the organisation you pick, which is API access rather than a Pro or Max subscription."),

            ["openai"] = new("O", "#10A37F", "GPT models. Key from platform.openai.com."),
            ["azure"] = new("Az", "#0078D4",
                "Azure OpenAI. Set Model to the DEPLOYMENT name, which need not match the model id."),
            ["google"] = new("G", "#4285F4",
                "Gemini through Google's OpenAI-compatible endpoint. Key from AI Studio."),
            ["groq"] = new("Gq", "#F55036", "Very fast inference for open-weight models."),
            ["mistral"] = new("M", "#FA520F", "Mistral's own API. Key from console.mistral.ai."),
            ["together"] = new("T", "#0F6FFF", "A wide range of open-weight models, hosted."),
            ["fireworks"] = new("F", "#5A2EE5", "Model ids here are paths, like accounts/…/models/…"),
            ["cerebras"] = new("C", "#F04B23", "Very high tokens per second; a short model list."),
            ["moonshot"] = new("K", "#16162B", "The Kimi family, from Moonshot directly."),
            ["openrouter"] = new("OR", "#6467F2", "One key in front of many vendors' models."),
            ["tokenrouter"] = new("TR", "#7C5CFF", "A gateway in front of many models."),
            ["deepseek"] = new("DS", "#4D6BFE", "DeepSeek's own API."),
            ["xai"] = new("X", "#111111", "Grok, from xAI."),

            ["ollama"] = new("Ol", "#4B4B4B",
                "Models running on this machine. No key, and nothing leaves the computer."),
            ["lmstudio"] = new("LM", "#4B4B4B",
                "LM Studio on this machine. Start its local server first. No key."),
            ["vllm"] = new("vL", "#4B4B4B", "A vLLM server you are running. No key."),
            ["litellm"] = new("Li", "#4B4B4B",
                "A proxy in front of anything else — the way to reach a provider not listed here."),
        };

    /// <summary>The neutral badge, for a provider nobody has branded — every custom one included.</summary>
    public static AiProviderBrand Fallback { get; } =
        new("?", "#6B7280", "A custom endpoint. Give it a base URL, a model and a key.");

    /// <summary>The badge and blurb for a provider id, never null.</summary>
    public static AiProviderBrand For(string? providerId, string? displayName = null)
    {
        if (!string.IsNullOrWhiteSpace(providerId) && Known.TryGetValue(providerId, out var brand))
            return brand;

        var source = !string.IsNullOrWhiteSpace(displayName) ? displayName! : providerId ?? string.Empty;
        var initial = source.FirstOrDefault(char.IsLetterOrDigit);

        return initial == default
            ? Fallback
            : Fallback with { Mark = char.ToUpperInvariant(initial).ToString() };
    }
}
