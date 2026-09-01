using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>
/// Codegen over the OpenAI <c>POST {baseUrl}/chat/completions</c> shape — which OpenAI, DeepSeek, xAI
/// (Grok), OpenRouter, Groq, Mistral, Together, Fireworks, Cerebras, Gemini's compatibility endpoint,
/// a local Ollama or vLLM server, and most private gateways all speak. One client, chosen by base URL
/// + key + model. The context pack goes as the system message; the conversation follows. Only the
/// prompt + pack leave the machine, to the endpoint the user configured.
///
/// <para><b>Azure OpenAI rides the same client.</b> Its request and response bodies are identical;
/// what differs is only the envelope — the deployment name in the path rather than the body, an
/// <c>api-version</c> query parameter, and an <c>api-key</c> header instead of a bearer token. That
/// is three lines of addressing, not a protocol, so forking a second client for it would duplicate
/// the streaming parser, the retry and the usage handling to change a URL.</para>
/// </summary>
public sealed class OpenAiCompatibleCodegenClient : IStrategyCodegenClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Uri? _baseUri;
    private readonly string _model;
    private readonly string? _apiKey;

    /// <param name="apiKey">Bearer key, or null for a keyless local endpoint (Ollama). When null the
    /// provider reports unavailable unless <paramref name="keyless"/> is set.</param>
    /// <param name="keyless">True for a local endpoint that needs no key (Ollama) — then availability
    /// depends only on a configured base URL.</param>
    /// <param name="azureApiVersion">Non-empty switches this client to the Azure OpenAI envelope. Null
    /// or empty is the ordinary OpenAI-compatible shape.</param>
    public OpenAiCompatibleCodegenClient(
        HttpClient http, string providerId, string displayName, string baseUrl, string model,
        string? apiKey, bool keyless = false, CodegenEffort effort = CodegenEffort.Default,
        string? azureApiVersion = null)
    {
        _http = http;
        ProviderId = providerId;
        DisplayName = displayName;
        _baseUrl = NormaliseBaseUrl(baseUrl);
        _baseUri = TryAbsolute(_baseUrl);
        _model = model;
        _apiKey = NormaliseApiKey(apiKey);
        _keyless = keyless;
        _effort = effort;
        _azureApiVersion = string.IsNullOrWhiteSpace(azureApiVersion) ? null : azureApiVersion.Trim();
    }

    private readonly bool _keyless;
    private readonly CodegenEffort _effort;
    private readonly string? _azureApiVersion;

    /// <summary>True when this client is addressing Azure OpenAI.</summary>
    private bool IsAzure => _azureApiVersion is not null;

    /// <summary>Where a completion is posted. On Azure the model is the DEPLOYMENT name and it lives
    /// in the path, so a deployment named differently from the model is addressed correctly.</summary>
    private string CompletionsUrl => IsAzure
        ? $"{_baseUrl}/openai/deployments/{Uri.EscapeDataString(_model)}/chat/completions?api-version={Uri.EscapeDataString(_azureApiVersion!)}"
        : $"{_baseUrl}/chat/completions";

    /// <summary>Where the model list is read from.</summary>
    private string ModelsUrl => IsAzure
        ? $"{_baseUrl}/openai/models?api-version={Uri.EscapeDataString(_azureApiVersion!)}"
        : $"{_baseUrl}/models";

    /// <summary>Attaches the key the way this endpoint expects it. Azure reads <c>api-key</c> and
    /// ignores <c>Authorization</c> entirely, which presents as a 401 with a correct key.</summary>
    private void Authorize(HttpRequestMessage request)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) return;

        if (IsAzure) request.Headers.TryAddWithoutValidation("api-key", _apiKey);
        else request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");
    }

    public string ProviderId { get; }
    public string DisplayName { get; }

    public bool IsAvailable =>
        _baseUri is not null && !CodegenBaseUrl.IsUnedited(_baseUrl) &&
        !string.IsNullOrWhiteSpace(_model) &&
        (_keyless || !string.IsNullOrWhiteSpace(_apiKey));

    public string Model => _model;
    public CodegenEffort Effort => _effort;
    public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);

    /// <summary>Every OpenAI-compatible endpoint (including Ollama) exposes <c>GET /models</c>, so the
    /// picker can list what this key/server actually has. A failure is an empty list, never an error.</summary>
    public async Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default)
    {
        // Not IsAvailable: listing models deliberately works before a model is chosen, which is the
        // whole point of the button. It does need a usable URL.
        if (_baseUri is null) return [];

        using var req = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
        Authorize(req);

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

        // Sent up to twice. A reasoning model on a big prompt emits NOTHING while it reasons —
        // measured at 278 seconds before the first byte on a 67 KB prompt — and a gateway in front of
        // it drops an idle connection with a 502/503/504. That is transient and worth one more go;
        // losing a nine-minute generation to a proxy is not a failure the user can act on.
        HttpResponseMessage? resp = null;
        string? failure = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            resp?.Dispose();

            using var httpReq = BuildRequest(request, stream: true);
            (resp, failure) = await TrySendAsync(httpReq, ct).ConfigureAwait(false);

            if (failure is not null || resp is null) break;
            if (!IsTransientGatewayFailure((int)resp.StatusCode)) break;
            if (attempt == 1) break;

            yield return new CodegenEvent.TextDelta(string.Empty);   // keeps the turn visibly alive
        }

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
                    StrategyCodegenResponse.Fail(
                        $"{DisplayName} returned {(int)resp.StatusCode}: {Trim(payload)}"
                        + Hint((int)resp.StatusCode, payload, _model)));
                yield break;
            }

            var text = new System.Text.StringBuilder();
            var usage = CodegenUsage.None;
            var reasoningCharacters = 0;
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            // Driven by hand rather than with `await foreach`, for the reason TrySendAsync exists: an
            // iterator may not yield from a catch, and a stalled provider has to be REPORTED rather
            // than thrown past the caller.
            await using var chunks = ServerSentEvents
                .ReadAsync(body, _http.Timeout, ct)
                .GetAsyncEnumerator(ct);

            while (true)
            {
                var (moved, stalled) = await TryMoveAsync(chunks).ConfigureAwait(false);

                if (stalled is not null)
                {
                    yield return new CodegenEvent.Completed(StrategyCodegenResponse.Fail(
                        $"{DisplayName} opened a stream and then stopped sending. {stalled} "
                        + "Raise AiCodegen:TimeoutSeconds if the model needs longer to think, or try "
                        + "another provider."));
                    yield break;
                }

                if (!moved) break;
                var chunk = chunks.Current;

                var delta = default(JsonElement);
                var hasDelta =
                    chunk.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out delta);

                if (hasDelta &&
                    delta.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String &&
                    content.GetString() is { Length: > 0 } fragment)
                {
                    text.Append(fragment);
                    yield return new CodegenEvent.TextDelta(fragment);
                }

                // A reasoning model on this wire format streams its thinking as `reasoning_content`,
                // a SEPARATE field, and emits no `content` at all until it has finished. Counted
                // rather than shown: the raw chain of thought is noise in a builder chat, and some
                // providers ask that it not be displayed. But it is the difference between "the
                // provider is working" and "the provider has gone quiet", and if a generation ends
                // with nothing but this it is the only honest explanation of where the money went.
                if (hasDelta &&
                    delta.TryGetProperty("reasoning_content", out var reasoning) &&
                    reasoning.ValueKind == JsonValueKind.String &&
                    reasoning.GetString() is { Length: > 0 } thought)
                {
                    reasoningCharacters += thought.Length;

                    // Empty, so nothing of the model's thinking reaches the transcript — it exists to
                    // keep the turn visibly alive, exactly as the gateway retry above does.
                    yield return new CodegenEvent.TextDelta(string.Empty);
                }

                if (chunk.TryGetProperty("usage", out var reported) && reported.ValueKind == JsonValueKind.Object)
                {
                    usage = new CodegenUsage(
                        Int(reported, "prompt_tokens"),
                        Int(reported, "completion_tokens"));
                    yield return new CodegenEvent.UsageUpdate(usage);
                }
            }

            yield return new CodegenEvent.Completed(
                Assemble(text.ToString(), usage, reasoningCharacters));
        }
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : 0;

    /// <summary>Advances the stream and classifies the stall, for the same reason as
    /// <see cref="TrySendAsync"/>: an iterator may not yield from a catch.</summary>
    internal static async Task<(bool Moved, string? Stalled)> TryMoveAsync(
        IAsyncEnumerator<JsonElement> chunks)
    {
        try
        {
            return (await chunks.MoveNextAsync().ConfigureAwait(false), null);
        }
        catch (TimeoutException stalled)
        {
            return (false, stalled.Message);
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

    /// <summary>Prose with no code is the model asking a clarifying question — a normal turn.</summary>
    /// <param name="reasoningCharacters">How much the model streamed as <c>reasoning_content</c>. Only
    /// read when nothing else arrived, and then it is the whole explanation.</param>
    private StrategyCodegenResponse Assemble(string text, CodegenUsage usage, int reasoningCharacters = 0)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            // "Returned no message content" is true and tells the user nothing about a generation that
            // may have taken twenty minutes and billed every output token. Measured: a vague brief on a
            // reasoning model streamed 23.5 minutes of `reasoning_content` and never began an answer.
            // Naming that is the difference between a bug they will report and a setting they can change.
            var spent = usage.OutputTokens > 0
                ? $" It billed {usage.OutputTokens:N0} output token(s)."
                : string.Empty;

            return StrategyCodegenResponse.Fail(reasoningCharacters > 0
                ? $"{DisplayName} spent the whole generation reasoning and never started an answer."
                  + spent
                  + " Lower the reasoning effort, or give it a more specific brief — an open-ended one"
                  + " can consume the entire budget before any code is written."
                : $"{DisplayName} returned no message content.{spent}");
        }

        var files = CodegenCodeExtractor.ExtractFiles(text);
        return files.Count == 0
            ? StrategyCodegenResponse.Reply(text, usage)
            : StrategyCodegenResponse.Ok(files, text, usage);
    }

    private HttpRequestMessage BuildRequest(StrategyCodegenRequest request, bool stream)
    {
        // Two system messages rather than one concatenated string: providers that cache do it on a
        // prefix, so keeping the shared pack its own message lets it stay cached across role switches.
        // Providers that do not cache see the same instructions either way.
        var messages = new List<WireMessage> { new("system", request.SystemContext) };
        if (request.RoleInstruction is { Length: > 0 } role)
            messages.Add(new WireMessage("system", role));
        foreach (var m in request.Messages)
            messages.Add(new(m.Role == CodegenRole.Assistant ? "assistant" : "user", m.Content));

        var body = new ChatRequest(
            _model, messages, Temperature: 0.2, ReasoningEffort: ReasoningEffort(),
            Stream: stream ? true : null,
            StreamOptions: stream ? new WireStreamOptions(true) : null);

        var httpReq = new HttpRequestMessage(HttpMethod.Post, CompletionsUrl)
        {
            Content = JsonContent.Create(body, options: Json),
        };
        Authorize(httpReq);
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
                return StrategyCodegenResponse.Fail(
                    $"{DisplayName} returned {(int)resp.StatusCode}: {Trim(payload)}"
                    + Hint((int)resp.StatusCode, payload, _model));

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

    /// <summary>Kept as the client's own entry points because this is where the wire is built; the
    /// logic moved to <see cref="CodegenBaseUrl"/> when it turned out Anthropic takes the same
    /// user-typed base URL and had the same hole.</summary>
    internal static string NormaliseBaseUrl(string? baseUrl) => CodegenBaseUrl.Normalise(baseUrl);

    /// <inheritdoc cref="CodegenBaseUrl.TryAbsolute"/>
    internal static Uri? TryAbsolute(string? baseUrl) => CodegenBaseUrl.TryAbsolute(baseUrl);

    /// <summary>
    /// Takes what a user pasted into an API-key field and returns the token.
    ///
    /// <para><b>The same class of mistake as a pasted endpoint, and it costs more.</b> A provider's
    /// quickstart shows the whole header — <c>"Authorization": "Bearer nvapi-..."</c> — so what lands
    /// in a field labelled "API key" is sometimes <c>Bearer nvapi-...</c>. This client then sends
    /// <c>Authorization: Bearer Bearer nvapi-...</c> and every request 401s. Nothing about that says
    /// "your key has a word in front of it": it reads as an invalid or expired key, and the natural
    /// next step is to go and generate another one, which fails identically.</para>
    ///
    /// <para>It has already happened once, on the setup this method was written for.</para>
    /// </summary>
    internal static string? NormaliseApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return apiKey;

        var text = apiKey.Trim();
        return text.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? text["Bearer ".Length..].Trim()
            : text;
    }

    /// <summary>
    /// Whether a status is a gateway giving up rather than the provider refusing.
    ///
    /// <para>502, 503 and 504 come from the proxy in front of the model, not the model. On a reasoning
    /// model they mean "this took longer than the hop allows" — which says nothing about the request
    /// and everything about how long the model thought. Distinguished from 4xx, which is the provider
    /// telling you something you must fix and which must never be retried.</para>
    /// </summary>
    internal static bool IsTransientGatewayFailure(int status) => status is 502 or 503 or 504;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>
    /// Turns a rejection into something a user can act on.
    ///
    /// <para>An unknown model and a bad key both come back as a 4xx with a terse body, and the two have
    /// completely different fixes. The one that costs the most time is a model id in the wrong shape:
    /// gateways that front several vendors publish ids for their own config format — OpenCode Zen
    /// documents <c>opencode/&lt;id&gt;</c> — while the OpenAI-compatible <c>model</c> field wants the
    /// bare id. Both look like a name, so the wrong one produces a refusal that reads as "your key does
    /// not have access to this model".</para>
    /// </summary>
    internal static string Hint(int status, string payload, string model)
    {
        var body = payload ?? string.Empty;

        var unknownModel =
            body.Contains("model", StringComparison.OrdinalIgnoreCase)
            && (body.Contains("not found", StringComparison.OrdinalIgnoreCase)
                || body.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
                || body.Contains("unknown", StringComparison.OrdinalIgnoreCase)
                || body.Contains("invalid_model", StringComparison.OrdinalIgnoreCase));

        if (unknownModel)
        {
            var bare = model.Contains('/') ? model[(model.LastIndexOf('/') + 1)..] : null;
            return bare is null
                ? $" — the server does not know the model \"{model}\". Check the id on the provider's model list."
                : $" — the server does not know the model \"{model}\". Some gateways publish ids for their"
                  + $" own config format; over this API the bare id is usually wanted, so try \"{bare}\".";
        }

        if (IsTransientGatewayFailure(status))
        {
            return " — the provider's gateway timed out, which on a reasoning model usually means it "
                 + "thought for longer than the hop allows rather than that anything is wrong with the "
                 + "request. Already retried once. If it keeps happening, the prompt is too large for "
                 + "this model at this speed: pick a faster one in Provider settings.";
        }

        return status is 401 or 403
            ? " — check the API key for this provider in Provider settings."
            : string.Empty;
    }

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
