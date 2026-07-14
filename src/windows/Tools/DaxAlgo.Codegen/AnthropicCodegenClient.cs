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
    private readonly CodegenEffort _effort;

    public AnthropicCodegenClient(
        HttpClient http, string baseUrl, string model, string? apiKey,
        CodegenEffort effort = CodegenEffort.Default)
    {
        _http = http;
        _baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://api.anthropic.com" : baseUrl.TrimEnd('/');
        _model = model;
        _apiKey = apiKey;
        _effort = effort;
    }

    public string ProviderId => "anthropic";
    public string DisplayName => "Anthropic (API key)";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_model) && !string.IsNullOrWhiteSpace(_apiKey);
    public string Model => _model;
    public CodegenEffort Effort => _effort;
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

    /// <summary>
    /// Streams <c>POST /v1/messages</c> with <c>stream: true</c>. Text arrives token by token and usage
    /// as the API reports it, so a multi-minute generation shows its work instead of looking hung.
    /// </summary>
    public async IAsyncEnumerable<CodegenEvent> StreamAsync(
        StrategyCodegenRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            yield return new CodegenEvent.Completed(
                StrategyCodegenResponse.Fail("Anthropic is not configured (model / API key)."));
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
                yield return new CodegenEvent.Completed(StrategyCodegenResponse.Fail(HttpFailure(resp, payload)));
                yield break;
            }

            var accumulator = new AnthropicEventAccumulator();
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            await foreach (var evt in ServerSentEvents.ReadAsync(body, ct).ConfigureAwait(false))
            {
                foreach (var streamed in accumulator.Consume(evt))
                    yield return streamed;
            }

            yield return new CodegenEvent.Completed(Assemble(accumulator.Text, accumulator.Usage));
        }
    }

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

    /// <summary>Turns the assembled reply into a response: files if it wrote code, a plain reply if it
    /// asked a question instead.</summary>
    private static StrategyCodegenResponse Assemble(string text, CodegenUsage usage)
    {
        if (string.IsNullOrWhiteSpace(text))
            return StrategyCodegenResponse.Fail("Anthropic returned no text content.");

        var files = CodegenCodeExtractor.ExtractFiles(text);
        return files.Count == 0
            ? StrategyCodegenResponse.Reply(text, usage)
            : StrategyCodegenResponse.Ok(files, text, usage);
    }

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        if (!IsAvailable)
            return StrategyCodegenResponse.Fail("Anthropic is not configured (model / API key).");

        using var httpReq = BuildRequest(request, stream: false);

        try
        {
            using var resp = await _http.SendAsync(httpReq, ct).ConfigureAwait(false);
            var payload = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return StrategyCodegenResponse.Fail(HttpFailure(resp, payload));

            var parsed = JsonSerializer.Deserialize<MessagesResponse>(payload, Json);
            var text = parsed?.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            var usage = parsed?.Usage is { } u
                ? new CodegenUsage(
                    u.InputTokens + u.CacheCreationInputTokens + u.CacheReadInputTokens,
                    u.OutputTokens,
                    CachedInputTokens: u.CacheReadInputTokens)
                : CodegenUsage.None;

            // No code is a legitimate turn — the model is asking something back. The session shows it in
            // the chat and waits for the user, rather than treating it as a provider failure.
            return Assemble(text ?? string.Empty, usage);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The user pressed Stop. That is not a provider failure — let it surface as cancellation.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return StrategyCodegenResponse.Fail(TransportFailure(ex));
        }
    }

    /// <summary>The request body, shared by both paths — the streaming one differs only by <c>stream</c>.
    /// <para>
    /// Two cache breakpoints. The SDK context pack is byte-identical on every call, so it is marked
    /// cacheable and costs ~10% on every turn after the first. The last message is marked too: that makes
    /// this turn's whole prompt the cached prefix for the NEXT turn, which is what stops an ongoing
    /// conversation from re-billing its own history at full price. (A prefix under the model's minimum —
    /// 4k tokens on Opus — silently doesn't cache; no error, just no saving.)
    /// </para>
    /// </summary>
    private HttpRequestMessage BuildRequest(StrategyCodegenRequest request, bool stream)
    {
        var messages = request.Messages
            .Select(m => new WireMessage(
                m.Role == CodegenRole.Assistant ? "assistant" : "user",
                [new WireText(m.Content)]))
            .ToList();

        if (messages.Count > 0)
        {
            // Each message is one text block (built just above), so the breakpoint goes on that block.
            var last = messages[^1];
            messages[^1] = last with
            {
                Content = [last.Content[0] with { CacheControl = WireCacheControl.Ephemeral }],
            };
        }

        // Effort + adaptive thinking are sent ONLY when the user asked for an effort level. They are
        // rejected by models that predate them (Haiku 4.5 and older), so "Default" has to mean "send
        // neither" — that is what keeps an older model usable in the picker.
        var effort = _effort.Wire();
        var body = new MessagesRequest(
            _model, MaxTokens: 16384,
            System: [new WireText(request.SystemContext) { CacheControl = WireCacheControl.Ephemeral }],
            Messages: messages,
            OutputConfig: effort is null ? null : new WireOutputConfig(effort),
            Thinking: effort is null ? null : new WireThinking("adaptive"),
            Stream: stream ? true : null);

        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/messages")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        httpReq.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        httpReq.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        return httpReq;
    }

    /// <summary>The commonest 400 here is an effort/thinking parameter on a model that doesn't take one.
    /// Say so, rather than making the user decode the raw API error.</summary>
    private string HttpFailure(HttpResponseMessage resp, string payload)
    {
        var hint = _effort != CodegenEffort.Default && payload.Contains("effort", StringComparison.OrdinalIgnoreCase)
            ? $" — '{_model}' may not support a reasoning effort; set Effort to 'Provider default' or pick a newer model."
            : string.Empty;
        return $"Anthropic returned {(int)resp.StatusCode}: {Trim(payload)}{hint}";
    }

    private string TransportFailure(Exception ex) => ex is TaskCanceledException
        ? $"Anthropic timed out after {_http.Timeout.TotalSeconds:0}s. A long brief at a high reasoning " +
          "effort can take several minutes — raise AiCodegen:TimeoutSeconds, or lower Effort."
        : $"Anthropic request failed: {ex.Message}";

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private sealed record WireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] IReadOnlyList<WireText> Content);

    /// <summary>A text content block, optionally a cache breakpoint.</summary>
    private sealed record WireText([property: JsonPropertyName("text")] string Text)
    {
        [JsonPropertyName("type")] public string Type => "text";

        [JsonPropertyName("cache_control"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public WireCacheControl? CacheControl { get; init; }
    }

    private sealed record WireCacheControl([property: JsonPropertyName("type")] string Type)
    {
        public static WireCacheControl Ephemeral { get; } = new("ephemeral");
    }

    private sealed record MessagesRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("max_tokens")] int MaxTokens,
        [property: JsonPropertyName("system")] IReadOnlyList<WireText> System,
        [property: JsonPropertyName("messages")] IReadOnlyList<WireMessage> Messages,
        [property: JsonPropertyName("output_config"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WireOutputConfig? OutputConfig = null,
        [property: JsonPropertyName("thinking"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WireThinking? Thinking = null,
        [property: JsonPropertyName("stream"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Stream = null);
    private sealed record WireOutputConfig([property: JsonPropertyName("effort")] string Effort);
    private sealed record WireThinking([property: JsonPropertyName("type")] string Type);
    private sealed record MessagesResponse(
        [property: JsonPropertyName("content")] IReadOnlyList<ContentBlock>? Content,
        [property: JsonPropertyName("usage")] WireUsage? Usage);
    private sealed record ContentBlock([property: JsonPropertyName("type")] string Type,
                                       [property: JsonPropertyName("text")] string? Text);
    private sealed record WireUsage(
        [property: JsonPropertyName("input_tokens")] int InputTokens,
        [property: JsonPropertyName("output_tokens")] int OutputTokens,
        [property: JsonPropertyName("cache_creation_input_tokens")] int CacheCreationInputTokens = 0,
        [property: JsonPropertyName("cache_read_input_tokens")] int CacheReadInputTokens = 0);
    private sealed record ModelsResponse([property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);
    private sealed record ModelEntry([property: JsonPropertyName("id")] string Id);
}
