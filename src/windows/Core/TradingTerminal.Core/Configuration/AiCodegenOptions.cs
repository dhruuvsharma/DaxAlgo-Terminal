namespace TradingTerminal.Core.Configuration;

/// <summary>Config for one BYO-key / local codegen provider.</summary>
public sealed class AiCodegenProvider
{
    /// <summary>OpenAI-compatible base URL (OpenAI, DeepSeek, xAI, OpenRouter, Ollama) or the Anthropic
    /// endpoint — the client type is chosen by <see cref="Kind"/>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Default model id (e.g. <c>claude-opus-4-8</c>, <c>deepseek-chat</c>, <c>llama3.1</c>).</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>Reasoning effort for this provider — <c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>/
    /// <c>max</c>, or empty for the provider's own default. Empty is the only safe value for a model that
    /// predates the parameter (it is then never sent).</summary>
    public string Effort { get; set; } = string.Empty;

    /// <summary>Wire protocol. Keys are never stored here — they live in the DPAPI credential store,
    /// looked up by <see cref="AiCodegenOptions.SectionName"/> + provider id.</summary>
    public AiCodegenProviderKind Kind { get; set; } = AiCodegenProviderKind.OpenAiCompatible;
}

/// <summary>Which wire protocol a keyed provider speaks.</summary>
public enum AiCodegenProviderKind
{
    /// <summary><c>POST /v1/chat/completions</c> (OpenAI, DeepSeek, xAI, OpenRouter, Ollama).</summary>
    OpenAiCompatible,

    /// <summary>Anthropic <c>POST /v1/messages</c>.</summary>
    Anthropic,
}

/// <summary>
/// Binds the <c>AiCodegen</c> section — the AI Strategy Builder's provider setup. The provider the user
/// selects, per-provider endpoint/model, and the auto-fix retry bound. API keys are NOT here (credential
/// store); this only holds non-secret configuration, so it can live in appsettings.
/// </summary>
public sealed class AiCodegenOptions
{
    public const string SectionName = "AiCodegen";

    /// <summary>The provider id to use by default (see <see cref="IStrategyCodegenClient.ProviderId"/>).
    /// Empty ⇒ the app picks the first available (an installed agent CLI, then a keyed provider).</summary>
    public string DefaultProvider { get; set; } = string.Empty;

    /// <summary>How many times the builder loop re-prompts with compiler errors before giving up. Kept
    /// small — a provider that can't fix its own output in a few tries won't in ten.</summary>
    public int MaxFixAttempts { get; set; } = 3;

    /// <summary>
    /// How long ONE generation may take before it is abandoned — the agent-CLI subprocess wall clock and
    /// the HTTP timeout for keyed providers alike.
    /// <para>
    /// Generous by design. A detailed strategy brief at a high reasoning effort is a multi-minute request:
    /// the model reads the SDK context pack, thinks, and writes several files. The old defaults (an
    /// HttpClient's 100s, the CLI adapter's 180s) killed exactly the requests worth waiting for. The user
    /// can always press Stop; a wall clock is only there so a wedged provider can't hang the pane forever.
    /// </para>
    /// </summary>
    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Per-provider endpoint/model config, keyed by provider id.</summary>
    public IDictionary<string, AiCodegenProvider> Providers { get; set; } =
        new Dictionary<string, AiCodegenProvider>(StringComparer.OrdinalIgnoreCase);
}
