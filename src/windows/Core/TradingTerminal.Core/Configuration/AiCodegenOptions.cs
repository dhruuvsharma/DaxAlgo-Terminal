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

    /// <summary>Optional named configuration profile for an installed agent CLI (for example Codex
    /// <c>--profile</c>). Ignored by keyed HTTP providers and CLIs without profile support.</summary>
    public string CliProfile { get; set; } = string.Empty;

    /// <summary>Wire protocol. Keys are never stored here — they live in the DPAPI credential store,
    /// looked up by <see cref="AiCodegenOptions.SectionName"/> + provider id.</summary>
    public AiCodegenProviderKind Kind { get; set; } = AiCodegenProviderKind.OpenAiCompatible;

    /// <summary>
    /// What to call this provider in the picker. Empty for a shipped provider, whose name the app
    /// already knows; set for one the user added, which otherwise has nothing to show but its id.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Azure OpenAI's <c>api-version</c> query value — the only field Azure needs that no other
    /// provider has. Ignored by every other <see cref="Kind"/>.
    ///
    /// <para>Azure versions its API by date rather than by path, so this is not optional there and
    /// there is no safe default to guess: an old value silently disables newer parameters, and a
    /// value the resource does not recognise is a hard 404. Empty falls back to
    /// <see cref="DefaultAzureApiVersion"/>.</para>
    /// </summary>
    public string ApiVersion { get; set; } = string.Empty;

    /// <summary>The api-version used when a provider of kind
    /// <see cref="AiCodegenProviderKind.AzureOpenAi"/> does not name one.</summary>
    public const string DefaultAzureApiVersion = "2024-10-21";

    /// <summary>
    /// True when the user added this provider rather than the app shipping it.
    ///
    /// <para>The distinction is only about what may be deleted. A shipped row can have its key
    /// cleared and its endpoint edited but must survive, because <c>appsettings.json</c> would put it
    /// straight back and a user who "removed" it would meet it again on the next start. A row the
    /// user created exists only in their own config and can genuinely go.</para>
    /// </summary>
    public bool IsUserDefined { get; set; }
}

/// <summary>Which wire protocol a keyed provider speaks.</summary>
public enum AiCodegenProviderKind
{
    /// <summary><c>POST /v1/chat/completions</c> — OpenAI, DeepSeek, xAI, OpenRouter, Groq, Mistral,
    /// Together, Fireworks, Cerebras, Gemini's compatibility endpoint, a local Ollama or vLLM, or any
    /// private gateway that speaks the same shape. This is what "any provider" mostly means.</summary>
    OpenAiCompatible,

    /// <summary>Anthropic <c>POST /v1/messages</c>.</summary>
    Anthropic,

    /// <summary>
    /// Azure OpenAI. The same request and response bodies as
    /// <see cref="OpenAiCompatible"/>, and three differences that make the plain client 404: the
    /// deployment name sits in the path instead of the body, the API is versioned by an
    /// <c>api-version</c> query parameter, and the key goes in an <c>api-key</c> header rather than
    /// as a bearer token.
    /// </summary>
    AzureOpenAi,
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
    /// small — a provider that can't fix its own output in a few tries won't in ten. A
    /// <c>StrategyBuildProfile</c> supplied at session creation overrides this per-session.</summary>
    public int MaxFixAttempts { get; set; } = 3;

    /// <summary>The BUILD-PIPELINE effort the user last picked (<c>quick</c>/<c>standard</c>/<c>deep</c>/
    /// <c>max</c>) — how many skill packs, fix attempts, and whether the self-review / backtest-smoke
    /// passes run. Distinct from a provider's reasoning <see cref="AiCodegenProvider.Effort"/>. Empty ⇒
    /// <c>standard</c>. Persisted per user by the builder alongside the provider/model choice.</summary>
    public string BuildEffort { get; set; } = string.Empty;

    /// <summary>
    /// How long ONE generation may take — the agent-CLI subprocess wall clock and the HTTP timeout
    /// for keyed providers alike. <b>0, the default, means no limit.</b>
    ///
    /// <para>It was ten minutes, which is a guess about how long somebody else's model takes to think —
    /// and the guess loses either way. Too low and a hard brief at high effort is killed halfway,
    /// billing the user for tokens they never receive. Too high and it is not a safety net anyway.</para>
    ///
    /// <para>Cancellation is the control that actually belongs here: the pane's Stop button already
    /// cancels the request, and that is a decision by the person paying for it rather than by a number
    /// in a config file.</para>
    /// </summary>
    public int TimeoutSeconds { get; set; }

    /// <summary>Per-provider endpoint/model config, keyed by provider id.</summary>
    public IDictionary<string, AiCodegenProvider> Providers { get; set; } =
        new Dictionary<string, AiCodegenProvider>(StringComparer.OrdinalIgnoreCase);
}
