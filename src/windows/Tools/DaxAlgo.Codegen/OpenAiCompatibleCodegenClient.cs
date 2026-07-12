using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Codegen over the OpenAI <c>POST {baseUrl}/chat/completions</c> shape — which OpenAI, DeepSeek, xAI
/// (Grok), OpenRouter, and a local Ollama server all speak. One client, chosen by base URL + key + model.
/// The context pack goes as the system message; the conversation follows. Only the prompt + pack leave
/// the machine, to the endpoint the user configured.
/// </summary>
public sealed class OpenAiCompatibleCodegenClient : IStrategyCodegenClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;

    /// <param name="apiKey">Bearer key, or null for a keyless local endpoint (Ollama). When null the
    /// provider reports unavailable unless <paramref name="keyless"/> is set.</param>
    /// <param name="keyless">True for a local endpoint that needs no key (Ollama) — then availability
    /// depends only on a configured base URL.</param>
    public OpenAiCompatibleCodegenClient(
        HttpClient http, string providerId, string displayName, string baseUrl, string model,
        string? apiKey, bool keyless = false)
    {
        _http = http;
        ProviderId = providerId;
        DisplayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _keyless = keyless;
    }

    private readonly bool _keyless;

    public string ProviderId { get; }
    public string DisplayName { get; }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_model) &&
        (_keyless || !string.IsNullOrWhiteSpace(_apiKey));

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return StrategyCodegenResponse.Fail($"{DisplayName} is not configured (base URL / model / API key).");

        var messages = new List<WireMessage> { new("system", request.SystemContext) };
        foreach (var m in request.Messages)
            messages.Add(new(m.Role == CodegenRole.Assistant ? "assistant" : "user", m.Content));

        var body = new ChatRequest(_model, messages, Temperature: 0.2);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        try
        {
            using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return StrategyCodegenResponse.Fail($"{DisplayName} returned {(int)resp.StatusCode}: {Trim(payload)}");

            var parsed = JsonSerializer.Deserialize<ChatResponse>(payload, Json);
            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            if (string.IsNullOrWhiteSpace(text))
                return StrategyCodegenResponse.Fail($"{DisplayName} returned no message content.");

            var code = CodegenCodeExtractor.Extract(text);
            return string.IsNullOrWhiteSpace(code)
                ? StrategyCodegenResponse.Fail($"{DisplayName} returned no code.")
                : StrategyCodegenResponse.Ok(code, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return StrategyCodegenResponse.Fail($"{DisplayName} request failed: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    // ── wire shapes ───────────────────────────────────────────────────────────────────────────────
    private sealed record WireMessage([property: JsonPropertyName("role")] string Role,
                                      [property: JsonPropertyName("content")] string Content);
    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature);
    private sealed record ChatResponse([property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices);
    private sealed record Choice([property: JsonPropertyName("message")] WireMessage? Message);
}
