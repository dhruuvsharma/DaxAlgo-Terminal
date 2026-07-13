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

    /// <param name="httpFactory">Produces an HttpClient per keyed request (pass an
    /// <c>IHttpClientFactory.CreateClient</c> in the app).</param>
    /// <param name="keyResolver">Resolves a provider id to its API key (credential store), or null.</param>
    public StrategyCodegenClientFactory(Func<HttpClient> httpFactory, AiCodegenOptions options, Func<string, string?> keyResolver)
    {
        _httpFactory = httpFactory;
        _options = options;
        _keyResolver = keyResolver;
    }

    /// <summary>Every provider the app knows how to build — installed agent CLIs, the configured keyed
    /// providers, and Anthropic. Includes UNavailable ones so the settings UI can show "install / add a
    /// key"; filter on <see cref="IStrategyCodegenClient.IsAvailable"/> for the picker.</summary>
    public IReadOnlyList<IStrategyCodegenClient> BuildAll()
    {
        var clients = new List<IStrategyCodegenClient>();

        // Installed agent CLIs — availability is "on PATH"; the vendor tool owns the login.
        foreach (var adapter in AgentCliAdapter.All)
            clients.Add(new AgentCliCodegenClient(
                adapter,
                timeout: Timeout,
                model: ConfiguredModel(adapter.ProviderId),
                effort: ConfiguredEffort(adapter.ProviderId)));

        // Keyed / local providers from config. An agent CLI may ALSO appear here (to pin its model), and
        // must not be built a second time as an HTTP provider — it has no BaseUrl or key.
        foreach (var (id, provider) in _options.Providers)
        {
            if (AgentCliAdapter.All.Any(a => a.ProviderId.Equals(id, StringComparison.OrdinalIgnoreCase))) continue;
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
        var adapter = AgentCliAdapter.All.FirstOrDefault(a =>
            a.ProviderId.Equals(providerId, StringComparison.OrdinalIgnoreCase));
        if (adapter is not null)
            return new AgentCliCodegenClient(
                adapter,
                timeout: Timeout,
                model: Blank(model) ? ConfiguredModel(providerId) : model,
                effort: effort);

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
    private TimeSpan Timeout => TimeSpan.FromSeconds(Math.Max(30, _options.TimeoutSeconds));

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
            _ => new OpenAiCompatibleCodegenClient(
                http, id, DisplayNameFor(id), provider.BaseUrl, effectiveModel, key,
                keyless: isOllama, effort: effectiveEffort),
        };
    }

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

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);

    private static string DisplayNameFor(string id) => id switch
    {
        "openai" => "OpenAI (API key)",
        "deepseek" => "DeepSeek (API key)",
        "xai" => "xAI / Grok (API key)",
        "openrouter" => "OpenRouter (API key)",
        "ollama" => "Ollama (local)",
        _ => $"{id} (API key)",
    };
}
