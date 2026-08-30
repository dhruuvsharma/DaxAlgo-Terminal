using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// One provider the user can add in a click, with its endpoint already filled in.
/// </summary>
/// <param name="Id">The stable key the provider is stored and keyed under.</param>
/// <param name="DisplayName">What the picker calls it.</param>
/// <param name="BaseUrl">The endpoint. Empty for the blank "something else" row, where the user
/// supplies it.</param>
/// <param name="Kind">Which wire shape it speaks.</param>
/// <param name="Note">One line on what the user needs to know before choosing it — where the key
/// comes from, or that it is a server they must be running themselves.</param>
public sealed record AiProviderPreset(
    string Id,
    string DisplayName,
    string BaseUrl,
    AiCodegenProviderKind Kind,
    string Note)
{
    /// <summary>True for a server on the user's own machine — no key, nothing leaves the network.</summary>
    public bool IsLocal =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri) && uri.IsLoopback;

    /// <summary>True for the blank row, which the user fills in themselves.</summary>
    public bool IsBlank => string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>A provider record ready to save.</summary>
    public AiCodegenProvider ToProvider() => new()
    {
        BaseUrl = BaseUrl,
        Kind = Kind,
        DisplayName = DisplayName,
        IsUserDefined = true,
        ApiVersion = Kind == AiCodegenProviderKind.AzureOpenAi
            ? AiCodegenProvider.DefaultAzureApiVersion
            : string.Empty,
    };
}

/// <summary>
/// The providers offered when adding one — endpoints, not models.
///
/// <para>This exists because "bring your own provider" was true of the configuration file and false
/// of the product: the settings pane could edit the eight providers <c>appsettings.json</c> shipped
/// and could not add a ninth, so reaching Groq or a local LM Studio meant hand-editing
/// <c>%LocalAppData%\DaxAlgo Terminal\ai-codegen.json</c>, which nobody does.</para>
///
/// <para><b>Endpoints only, deliberately no model ids.</b> A base URL is stable for years; a model id
/// is stable for weeks. <c>appsettings.json</c> already records what happens otherwise — a free model
/// there was withdrawn one week after it was named, and the comment now warns that the ids it lists
/// are "a snapshot, not a menu". Every endpoint below serves <c>GET /models</c>, so the picker's
/// refresh knows what a key can actually reach today, which a list compiled here never will.</para>
///
/// <para>Nor is this a gate. It is a shortcut past typing a URL: the blank row takes any endpoint
/// that speaks one of the three wire shapes, which is what makes the answer to "can I use provider
/// X" yes rather than a list.</para>
/// </summary>
public static class AiProviderCatalog
{
    /// <summary>The id given to a provider added from the blank row when the user names it nothing
    /// useful.</summary>
    public const string CustomId = "custom";

    /// <summary>Presets in the order they are offered: hosted gateways, then the machine's own
    /// servers, then the blank row.</summary>
    public static IReadOnlyList<AiProviderPreset> Presets { get; } =
    [
        new("google", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai",
            AiCodegenProviderKind.OpenAiCompatible,
            "Google's OpenAI-compatible endpoint, so no separate client is needed. Key from AI Studio."),

        new("groq", "Groq", "https://api.groq.com/openai/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "Fast inference for open-weight models."),

        new("mistral", "Mistral", "https://api.mistral.ai/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "Key from console.mistral.ai."),

        new("together", "Together AI", "https://api.together.xyz/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "Hosts a wide range of open-weight models."),

        new("fireworks", "Fireworks AI", "https://api.fireworks.ai/inference/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "Model ids here are paths, like accounts/fireworks/models/<name>."),

        new("cerebras", "Cerebras", "https://api.cerebras.ai/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "Very high tokens per second; a small model list."),

        new("moonshot", "Moonshot", "https://api.moonshot.ai/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "The Kimi family, from Moonshot directly."),

        new("azure", "Azure OpenAI", "https://YOUR-RESOURCE.openai.azure.com",
            AiCodegenProviderKind.AzureOpenAi,
            "Replace YOUR-RESOURCE, and set Model to the DEPLOYMENT name, which need not match the model."),

        new("lmstudio", "LM Studio (this machine)", "http://localhost:1234/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "Start the local server in LM Studio first. No key, and nothing leaves the machine."),

        new("vllm", "vLLM (this machine)", "http://localhost:8000/v1",
            AiCodegenProviderKind.OpenAiCompatible,
            "A vLLM server you are running. No key."),

        new("litellm", "LiteLLM proxy", "http://localhost:4000",
            AiCodegenProviderKind.OpenAiCompatible,
            "A proxy in front of anything else — the way to reach a provider not listed here."),

        new(CustomId, "Something else…", string.Empty,
            AiCodegenProviderKind.OpenAiCompatible,
            "Any endpoint that speaks one of the three wire shapes. Give it a name and a base URL."),
    ];

    /// <summary>The preset with this id, or null.</summary>
    public static AiProviderPreset? Find(string? id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Presets.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A stable, file-safe id from whatever the user typed as a name.
    ///
    /// <para>The id is a configuration key and a credential-store key, so it has to be predictable and
    /// free of anything that would need escaping. Lower-cased, non-alphanumerics collapsed to single
    /// hyphens, trimmed — "My Company's Gateway" becomes "my-company-s-gateway".</para>
    /// </summary>
    public static string IdFrom(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return CustomId;

        var builder = new System.Text.StringBuilder(name.Length);
        var pendingHyphen = false;
        foreach (var character in name.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(character))
            {
                if (pendingHyphen && builder.Length > 0) builder.Append('-');
                pendingHyphen = false;
                builder.Append(character);
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var id = builder.ToString();
        return string.IsNullOrEmpty(id) ? CustomId : id;
    }

    /// <summary>
    /// <paramref name="candidate"/>, or the first free id derived from it.
    ///
    /// <para>Adding a second provider under an existing id would silently overwrite the first — same
    /// configuration key, same credential-store entry — so the collision is resolved rather than
    /// discovered later as a key that stopped working.</para>
    /// </summary>
    public static string UniqueId(string candidate, IEnumerable<string> existing)
    {
        var taken = new HashSet<string>(existing ?? [], StringComparer.OrdinalIgnoreCase);
        if (!taken.Contains(candidate)) return candidate;

        for (var suffix = 2; suffix < 100; suffix++)
        {
            var next = $"{candidate}-{suffix}";
            if (!taken.Contains(next)) return next;
        }

        return $"{candidate}-{Guid.NewGuid().ToString("N")[..6]}";
    }
}
