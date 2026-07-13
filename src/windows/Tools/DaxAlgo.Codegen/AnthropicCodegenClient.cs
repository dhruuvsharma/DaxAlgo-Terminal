using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Codegen over Anthropic's native <c>POST {baseUrl}/v1/messages</c> — the system pack goes in the
/// top-level <c>system</c> field (which Anthropic prompt-caches), the conversation in <c>messages</c>.
/// BYO key from the credential store; only the prompt + pack leave the machine.
/// </summary>
public sealed class AnthropicCodegenClient : IStrategyCodegenClient
{
    private const string AnthropicVersion = "2023-06-01";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string? _apiKey;

    public AnthropicCodegenClient(HttpClient http, string baseUrl, string model, string? apiKey)
    {
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com" : baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
    }

    public string ProviderId => "anthropic";
    public string DisplayName => "Anthropic (API key)";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_apiKey);
    public string Model => _model;
    public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);

    /// <summary>The models this key can actually call. A failure here is not an error the user needs —
    /// the picker just falls back to the curated shortlist.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return [];

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/v1/models?limit=100");
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<ModelsResponse>(payload, Json);
            return parsed?.Data?.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return StrategyCodegenResponse.Fail("Anthropic is not configured (model / API key).");

        var messages = request.Messages
            .Select(m => new WireMessage(m.Role == CodegenRole.Assistant ? "assistant" : "user", m.Content))
            .ToList();

        var body = new MessagesRequest(_model, MaxTokens: 16384, request.SystemContext, messages);
        using var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        httpReq.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        httpReq.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);

        try
        {
            using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return StrategyCodegenResponse.Fail($"Anthropic returned {(int)resp.StatusCode}: {Trim(payload)}");

            var parsed = JsonSerializer.Deserialize<MessagesResponse>(payload, Json);
            var text = parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            if (string.IsNullOrWhiteSpace(text))
                return StrategyCodegenResponse.Fail("Anthropic returned no text content.");

            var usage = parsed?.Usage is { } u ? new CodegenUsage(u.InputTokens, u.OutputTokens) : CodegenUsage.None;
            var files = CodegenCodeExtractor.ExtractFiles(text);

            // No code is a legitimate turn — the model is asking something back. The session shows it in
            // the chat and waits for the user, rather than treating it as a provider failure.
            return files.Count == 0
                ? StrategyCodegenResponse.Reply(text, usage)
                : StrategyCodegenResponse.Ok(files, text, usage);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return StrategyCodegenResponse.Fail($"Anthropic request failed: {ex.Message}");
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record WireMessage([property: JsonPropertyName("role")] string Role,
                                      [property: JsonPropertyName("content")] string Content);
    private sealed record MessagesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] string System,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages);
    private sealed record MessagesResponse(
        [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock>? Content,
        [property: JsonPropertyName("usage")] WireUsage? Usage);
    private sealed record ContentBlock([property: JsonPropertyName("type")] string Type,
                                       [property: JsonPropertyName("text")] string? Text);
    private sealed record WireUsage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens);
    private sealed record ModelsResponse([property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);
    private sealed record ModelEntry([property: JsonPropertyName("id")] string Id);
}
