using System.Net.Http;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Assembles the codegen providers that are actually usable on this machine, from
/// <see cref="AiCodegenOptions"/> (non-secret endpoint/model config) plus a key resolver the shell
/// supplies (API keys live in the DPAPI credential store, which Infrastructure can't reference — so the
/// shell passes a <c>providerId → key</c> delegate). Building a client never touches the network;
/// <see cref="IStrategyCodegenClient.IsAvailable"/> is a cheap PATH / key-present check, so the provider
/// picker and the CLI can list what's ready without a round-trip.
/// <para>
/// A client is immutable in its model, so switching model in the UI rebuilds the client
/// (<see cref="Build"/>) rather than mutating one — the same path the configured default takes.
/// </para>
/// </summary>
public sealed class StrategyCodegenClientFactory
{
    private readonly Func<HttpClient> _httpFactory;
    private readonly AiCodegenOptions _options;
    private readonly Func<string, string?> _keyResolver;

    /// <summary>The browser sign-in, so a user with no API key can still reach the Anthropic API.</summary>
    private readonly AnthropicOAuthCli _oauth;

    /// <summary>
    /// The signed-in Anthropic provider, listed beside the installed agent CLIs and for the same reason:
    /// its credential lives in a vendor tool rather than in this application's key store.
    ///
    /// <para>A DISTINCT id from `anthropic` on purpose. The two bill differently in the user's mind — one
    /// is a key they pasted, the other an account they signed into — and collapsing them would make
    /// "which of my organisations is this spending?" unanswerable from the picker.</para>
    /// </summary>
    public const string AnthropicOAuthId = AnthropicCodegenClient.SignInProviderId;

    /// <param name="httpFactory">Produces an HttpClient per keyed request (pass an
    /// <c>IHttpClientFactory.CreateClient</c> in the app).</param>
    /// <param name="keyResolver">Resolves a provider id to its API key (credential store), or null.</param>
    public StrategyCodegenClientFactory(
        Func<HttpClient> httpFactory,
        AiCodegenOptions options,
        Func<string, string?> keyResolver,
        AnthropicOAuthCli? oauth = null)
    {
        _httpFactory = httpFactory;
        _options = options;
        _keyResolver = keyResolver;
        _oauth = oauth ?? new AnthropicOAuthCli();
    }

    /// <summary>Every provider the app knows how to build — installed agent CLIs, the configured keyed
    /// providers, and Anthropic. Includes UNavailable ones so the settings UI can show "install / add a
    /// key"; filter on <see cref="IStrategyCodegenClient.IsAvailable"/> for the picker.</summary>
    public IReadOnlyList<IStrategyCodegenClient> BuildAll()
    {
        var clients = new List<IStrategyCodegenClient>();

        // NO AGENT CLIs. Authenticating is now one of two things a person can understand — paste a key,
        // or sign in — and a third kind whose credential lives inside somebody else's program was the
        // single biggest source of confusion in this pane: keyless, unlistable, its spend invisible
        // (agent CLIs report no usage, so the token counter read zero on every run), and billed against
        // a subscription rather than the key the rest of the pane is about.
        //
        // AgentCliCodegenClient still exists and still works — the benchmark drives it directly for a
        // keyless live run, and the Vibe Code menu still opens a CLI workspace. It is simply no longer
        // one of the answers to "which provider should I use?".

        // The signed-in Anthropic provider. Always listed, never available unless `ant` is installed, so
        // the settings pane can offer the sign-in rather than hiding a provider the user could have.
        clients.Add(BuildAnthropicOAuth(model: null));

        // Keyed / local providers from config. An agent CLI may ALSO appear here (to pin its model), and
        // must not be built a second time as an HTTP provider — it has no BaseUrl or key.
        foreach (var (id, provider) in _options.Providers)
        {
            // A stale agent-CLI section in somebody's config must not resurrect the provider as an HTTP
            // one: it has no BaseUrl and no key, so it would list as permanently broken.
            if (AgentCliAdapter.All.Any(a => a.ProviderId.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
            if (id.Equals(AnthropicOAuthId, StringComparison.OrdinalIgnoreCase)) continue;
            clients.Add(BuildKeyed(id, provider, model: null));
        }

        return clients;
    }

    /// <summary>
    /// The same provider, bound to a different model and reasoning effort — what the builder's pickers
    /// call when the user switches either (a client is immutable in both). An unknown provider id
    /// returns null; a blank model means "the configured / vendor default".
    /// </summary>
    public IStrategyCodegenClient? Build(string providerId, string? model, CodegenEffort effort = CodegenEffort.Default)
    {
        if (providerId.Equals(AnthropicOAuthId, StringComparison.OrdinalIgnoreCase))
            return BuildAnthropicOAuth(model, effort);

        var configured = _options.Providers.FirstOrDefault(p =>
            p.Key.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        return configured.Value is null ? null : BuildKeyed(configured.Key, configured.Value, model, effort);
    }

    /// <summary>The models to offer for a provider without a network call (curated shortlist + whatever
    /// is configured). The UI adds a "refresh from provider" that calls
    /// <see cref="IStrategyCodegenClient.ListModelsAsync"/> on top of this.</summary>
    public IReadOnlyList<string> ModelsFor(string providerId) =>
        AiModelCatalog.Offer(providerId, ConfiguredModel(providerId));

    /// <summary>The provider the app should use: the configured default if it's available, else the
    /// first available one (agent CLIs first — they need no key), else null (nothing set up).</summary>
    public IStrategyCodegenClient? SelectDefault()
    {
        var all = BuildAll();
        if (!string.IsNullOrWhiteSpace(_options.DefaultProvider))
        {
            var chosen = all.FirstOrDefault(c =>
                c.ProviderId.Equals(_options.DefaultProvider, StringComparison.OrdinalIgnoreCase) && c.IsAvailable);
            if (chosen is not null) return chosen;
        }
        return all.FirstOrDefault(c => c.IsAvailable);
    }

    /// <summary>One generation's wall clock, from config. Applied to BOTH transports — a keyed provider
    /// would otherwise inherit <see cref="HttpClient"/>'s 100-second default and abandon exactly the long,
    /// high-effort generations worth waiting for.</summary>
    /// <summary>Zero or less means no limit — the user's Stop button is the control, not a guess about
    /// how long another company's model takes to think. Anything positive is honoured, floored at thirty
    /// seconds so a typo cannot make every request fail instantly.</summary>
    private TimeSpan Timeout => _options.TimeoutSeconds <= 0
        ? System.Threading.Timeout.InfiniteTimeSpan
        : TimeSpan.FromSeconds(Math.Max(30, _options.TimeoutSeconds));

    /// <summary>
    /// The Anthropic client authenticated by the CLI's browser sign-in rather than by a stored key.
    ///
    /// <para>The token is fetched per request, not here: it is short-lived, and one read at construction
    /// would authenticate for a while and then start failing on a session nobody had touched.</para>
    /// </summary>
    private IStrategyCodegenClient BuildAnthropicOAuth(string? model, CodegenEffort effort = CodegenEffort.Default)
    {
        var configured = _options.Providers
            .FirstOrDefault(p => p.Key.Equals(AnthropicOAuthId, StringComparison.OrdinalIgnoreCase)).Value;

        var effectiveModel = Blank(model)
            ? (Blank(configured?.Model) ? AiModelCatalog.For("anthropic").FirstOrDefault() ?? string.Empty : configured!.Model)
            : model!;

        var effectiveEffort = effort == CodegenEffort.Default
            ? CodegenEfforts.Parse(configured?.Effort)
            : effort;

        var http = _httpFactory();
        http.Timeout = Timeout;

        return new AnthropicCodegenClient(
            http,
            configured?.BaseUrl ?? string.Empty,
            effectiveModel,
            AnthropicCredential.OAuth(_oauth.AccessTokenAsync, () => _oauth.IsInstalled),
            effectiveEffort);
    }

    private IStrategyCodegenClient BuildKeyed(
        string id, AiCodegenProvider provider, string? model, CodegenEffort effort = CodegenEffort.Default)
    {
        var key = _keyResolver(id);
        var isOllama = id.Equals("ollama", StringComparison.OrdinalIgnoreCase);
        var effectiveModel = Blank(model) ? provider.Model : model!;
        var effectiveEffort = effort == CodegenEffort.Default ? CodegenEfforts.Parse(provider.Effort) : effort;

        // A fresh HttpClient per build (IHttpClientFactory pools the handler), so setting Timeout here is
        // safe — it is only illegal to change it after the client has sent a request.
        var http = _httpFactory();
        http.Timeout = Timeout;

        return provider.Kind switch
        {
            AiCodegenProviderKind.Anthropic =>
                new AnthropicCodegenClient(http, provider.BaseUrl, effectiveModel, key, effectiveEffort),
            AiCodegenProviderKind.AzureOpenAi => new OpenAiCompatibleCodegenClient(
                http, id, DisplayNameFor(id, provider), provider.BaseUrl, effectiveModel, key,
                effort: effectiveEffort,
                azureApiVersion: Blank(provider.ApiVersion)
                    ? AiCodegenProvider.DefaultAzureApiVersion
                    : provider.ApiVersion),
            _ => new OpenAiCompatibleCodegenClient(
                http, id, DisplayNameFor(id, provider), provider.BaseUrl, effectiveModel, key,
                keyless: isOllama || IsLoopback(provider.BaseUrl), effort: effectiveEffort),
        };
    }

    /// <summary>
    /// True for an endpoint on this machine.
    ///
    /// <para>A local server — Ollama under another name, LM Studio, vLLM, a LiteLLM proxy — needs no
    /// key, and a keyed client with no key reports itself unavailable. Before this, only the provider
    /// literally named <c>ollama</c> was exempt, so a user pointing a custom provider at
    /// <c>http://localhost:1234/v1</c> got a row that said "no key yet" about a server that does not
    /// want one.</para>
    /// </summary>
    private static bool IsLoopback(string? baseUrl) =>
        // Normalised first, because this is asked of what the USER TYPED. "localhost:1234/v1" -- the
        // form every local-runtime readme prints -- parses as an absolute URI whose SCHEME is
        // "localhost", and that is not loopback. The client repairs the same string before sending, so
        // without this the request would go to the right place while the row insisted on a key the
        // server does not want.
        CodegenBaseUrl.TryAbsolute(CodegenBaseUrl.Normalise(baseUrl)) is { IsLoopback: true };

    /// <summary>An agent CLI can also be pinned to a model/effort in config
    /// (<c>AiCodegen:Providers:claude-cli:Model</c>) even though it needs no BaseUrl/key — that is the
    /// only reason it appears in the provider map.</summary>
    private string? ConfiguredModel(string providerId) =>
        _options.Providers.TryGetValue(providerId, out var provider) && !string.IsNullOrWhiteSpace(provider.Model)
            ? provider.Model
            : null;

    private CodegenEffort ConfiguredEffort(string providerId) =>
        _options.Providers.TryGetValue(providerId, out var provider)
            ? CodegenEfforts.Parse(provider.Effort)
            : CodegenEffort.Default;

    private string? ConfiguredCliProfile(string providerId) =>
        _options.Providers.TryGetValue(providerId, out var provider) && !string.IsNullOrWhiteSpace(provider.CliProfile)
            ? provider.CliProfile
            : null;

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    /// <summary>What the picker calls a provider. A name the user gave when adding it wins over
    /// anything derived here — they named it, and an id is what they were trying to avoid seeing.</summary>
    private static string DisplayNameFor(string id, AiCodegenProvider provider) =>
        string.IsNullOrWhiteSpace(provider.DisplayName) ? DisplayNameFor(id) : provider.DisplayName;

    private static string DisplayNameFor(string id) => id switch
    {
        "nvidia" => "NVIDIA NIM (API key)",
        "opencode" => "OpenCode Zen (API key)",
        "openai" => "OpenAI (API key)",
        "deepseek" => "DeepSeek (API key)",
        "xai" => "xAI / Grok (API key)",
        "openrouter" => "OpenRouter (API key)",
        "tokenrouter" => "TokenRouter (API key)",
        "ollama" => "Ollama (local)",
        _ => $"{id} (API key)",
    };
}
