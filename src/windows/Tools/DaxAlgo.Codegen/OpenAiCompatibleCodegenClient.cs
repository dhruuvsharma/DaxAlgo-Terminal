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
        string? apiKey, bool keyless = false, CodegenEffort effort = CodegenEffort.Default)
    {
        _http = http;
        ProviderId = providerId;
        DisplayName = displayName;
        _baseUrl = baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _keyless = keyless;
        _effort = effort;
    }

    private readonly bool _keyless;
    private readonly CodegenEffort _effort;

    public string ProviderId { get; }
    public string DisplayName { get; }

    public bool IsAvailable =>
        !string.IsNullOrWhiteSpace(_baseUrl) && !string.IsNullOrWhiteSpace(_model) &&
        (_keyless || !string.IsNullOrWhiteSpace(_apiKey));

    public string Model => _model;
    public CodegenEffort Effort => _effort;
    public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);

    /// <summary>Every OpenAI-compatible endpoint (including Ollama) exposes <c>GET /models</c>, so the
    /// picker can list what this key/server actually has. A failure is an empty list, never an error.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl)) return [];

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/models");
        if (!string.IsNullOrWhiteSpace(_apiKey))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

        try
        {
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return [];

            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<ModelsResponse>(payload, Json);
            return parsed?.Data?.Select(m => m.Id).Where(id => !string.IsNullOrWhiteSpace(id)).Order(StringComparer.Ordinal).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Streams <c>POST /chat/completions</c> with <c>stream: true</c>. <c>stream_options.include_usage</c>
    /// asks for a final usage chunk — servers that don't know the option ignore it, so a token counter is
    /// a bonus, never a requirement.
    /// </summary>
    public async IAsyncEnumerable<CodegenEvent> StreamAsync(
        StrategyCodegenRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            yield return new CodegenEvent.Completed(
                StrategyCodegenResponse.Fail($"{DisplayName} is not configured (base URL / model / API key)."));
            yield break;
        }

        using var httpReq = BuildRequest(request, stream: true);

        var (resp, failure) = await TrySendAsync(httpReq, ct).ConfigureAwait(false);
        if (failure is not null)
        {
            yield return new CodegenEvent.Completed(StrategyCodegenResponse.Fail(failure));
            yield break;
        }

        using (resp!)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                yield return new CodegenEvent.Completed(
                    StrategyCodegenResponse.Fail($"{DisplayName} returned {(int)resp.StatusCode}: {Trim(payload)}"));
                yield break;
            }

            var text = new System.Text.StringBuilder();
            var usage = CodegenUsage.None;
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            await foreach (var chunk in ServerSentEvents.ReadAsync(body, ct).ConfigureAwait(false))
            {
                if (chunk.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is { Length: > 0 } fragment)
                {
                    text.Append(fragment);
                    yield return new CodegenEvent.TextDelta(fragment);
                }

                if (chunk.TryGetProperty("usage", out var reported) && reported.ValueKind == JsonValueKind.Object)
                {
                    usage = new CodegenUsage(
                        Int(reported, "prompt_tokens"),
                        Int(reported, "completion_tokens"));
                    yield return new CodegenEvent.UsageUpdate(usage);
                }
            }

            yield return new CodegenEvent.Completed(Assemble(text.ToString(), usage));
        }
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>Sends and classifies the failure, because an iterator may not yield from a catch.</summary>
    private async Task<(HttpResponseMessage? Response, string? Failure)> TrySendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            return (await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the user pressed Stop
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, TransportFailure(ex));
        }
    }

    /// <summary>Prose with no code is the model asking a clarifying question — a normal turn.</summary>
    private StrategyCodegenResponse Assemble(string text, CodegenUsage usage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return StrategyCodegenResponse.Fail($"{DisplayName} returned no message content.");

        var files = CodegenCodeExtractor.ExtractFiles(text);
        return files.Count == 0
            ? StrategyCodegenResponse.Reply(text, usage)
            : StrategyCodegenResponse.Ok(files, text, usage);
    }

    private HttpRequestMessage BuildRequest(StrategyCodegenRequest request, bool stream)
    {
        var messages = new List<WireMessage> { new("system", request.SystemContext) };
        foreach (var m in request.Messages)
            messages.Add(new(m.Role == CodegenRole.Assistant ? "assistant" : "user", m.Content));

        var body = new ChatRequest(
            _model, messages, Temperature: 0.2, ReasoningEffort: ReasoningEffort(),
            Stream: stream ? true : null,
            StreamOptions: stream ? new WireStreamOptions(true) : null);

        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/chat/completions")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        if (!string.IsNullOrWhiteSpace(_apiKey))
            httpReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
        return httpReq;
    }

    private string TransportFailure(Exception ex) => ex is TaskCanceledException
        ? $"{DisplayName} timed out after {_http.Timeout.TotalSeconds:0}s. A long brief at a high reasoning " +
          "effort can take several minutes — raise AiCodegen:TimeoutSeconds, or lower Effort."
        : $"{DisplayName} request failed: {ex.Message}";

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return StrategyCodegenResponse.Fail($"{DisplayName} is not configured (base URL / model / API key).");

        using var httpReq = BuildRequest(request, stream: false);

        try
        {
            using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return StrategyCodegenResponse.Fail($"{DisplayName} returned {(int)resp.StatusCode}: {Trim(payload)}");

            var parsed = JsonSerializer.Deserialize<ChatResponse>(payload, Json);
            var text = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
            var usage = parsed?.Usage is { } u ? new CodegenUsage(u.PromptTokens, u.CompletionTokens) : CodegenUsage.None;

            return Assemble(text ?? string.Empty, usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user pressed Stop — cancellation, not a provider failure.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return StrategyCodegenResponse.Fail(TransportFailure(ex));
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>OpenAI's <c>reasoning_effort</c> takes low/medium/high only, so the two Anthropic-only
    /// levels clamp to high. Null (the "Default" pick, or a provider with no effort knob) omits the field
    /// entirely — a server that doesn't know it would reject the request.</summary>
    private string? ReasoningEffort()
    {
        if (!AiModelCatalog.SupportsEffort(ProviderId)) return null;

        return _effort switch
        {
            CodegenEffort.Low => "low",
            CodegenEffort.Medium => "medium",
            CodegenEffort.High or CodegenEffort.XHigh or CodegenEffort.Max => "high",
            _ => null,
        };
    }

    // ── wire shapes ───────────────────────────────────────────────────────────────────────────────
    private sealed record WireMessage([property: JsonPropertyName("role")] string Role,
                                      [property: JsonPropertyName("content")] string Content);
    private sealed record ChatRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
        [property: JsonPropertyName("temperature")] double Temperature,
        [property: JsonPropertyName("reasoning_effort"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ReasoningEffort = null,
        [property: JsonPropertyName("stream"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Stream = null,
        [property: JsonPropertyName("stream_options"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WireStreamOptions? StreamOptions = null);
    private sealed record WireStreamOptions([property: JsonPropertyName("include_usage")] bool IncludeUsage);
    private sealed record ChatResponse(
        [property: JsonPropertyName("choices")] IReadOnlyList<Choice>? Choices,
        [property: JsonPropertyName("usage")] WireUsage? Usage);
    private sealed record Choice([property: JsonPropertyName("message")] WireMessage? Message);
    private sealed record WireUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
    private sealed record ModelsResponse([property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);
    private sealed record ModelEntry([property: JsonPropertyName("id")] string Id);
}
