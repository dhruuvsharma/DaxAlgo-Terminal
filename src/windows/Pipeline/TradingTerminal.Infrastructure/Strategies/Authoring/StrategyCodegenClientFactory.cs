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
            clients.Add(new AgentCliCodegenClient(adapter));

        // Keyed / local providers from config.
        foreach (var (id, provider) in _options.Providers)
        {
            var key = _keyResolver(id);
            var isOllama = id.Equals("ollama", StringComparison.OrdinalIgnoreCase);
            clients.Add(provider.Kind switch
            {
                AiCodegenProviderKind.Anthropic =>
                    new AnthropicCodegenClient(_httpFactory(), provider.BaseUrl, provider.Model, key),
                _ => new OpenAiCompatibleCodegenClient(
                    _httpFactory(), id, DisplayNameFor(id), provider.BaseUrl, provider.Model, key, keyless: isOllama),
            });
        }

        return clients;
    }

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
