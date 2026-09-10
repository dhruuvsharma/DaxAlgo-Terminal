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

        // Retried on two different kinds of "not your fault", which need two different responses.
        //
        // A gateway drops an idle connection with a 502/503/504 while a reasoning model is thinking —
        // measured at 278 seconds before the first byte on a 67 KB prompt — and the answer is to send
        // again AT ONCE, because nothing is wrong.
        //
        // A 429 is the opposite: the provider is saying WAIT, and sending again at once is what it
        // just refused. It was not retried at all, and the cost was measured — a batch of six briefs
        // on a free tier spent thirty-four minutes on the first and then failed the remaining five in
        // UNDER HALF A SECOND EACH, producing nothing, because the first run had used the quota. A
        // rate limit is the most retryable failure there is.
        HttpResponseMessage? resp = null;
        string? failure = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            resp?.Dispose();

            using (var httpReq = BuildRequest(request, stream: true))
            {
                (resp, failure) = await TrySendAsync(httpReq, ct).ConfigureAwait(false);
            }

            if (failure is not null || resp is null) break;

            var status = (int)resp.StatusCode;
            if (!IsTransientGatewayFailure(status) && !IsRateLimited(status)) break;
            if (attempt == MaxAttempts - 1) break;

            yield return new CodegenEvent.TextDelta(string.Empty);   // keeps the turn visibly alive

            // Cancellable, because the Stop button has to work during the wait as much as during the
            // generation — a minute of un-cancellable sleep is a hung application.
            var wait = RetryAfter(resp, attempt);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
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
                var (moved, stalled, broken) = await TryMoveAsync(chunks, DisplayName, ct).ConfigureAwait(false);

                if (stalled is not null)
                {
                    yield return new CodegenEvent.Completed(StrategyCodegenResponse.Fail(
                        $"{DisplayName} opened a stream and then stopped sending. {stalled} "
                        + "Raise AiCodegen:TimeoutSeconds if the model needs longer to think, or try "
                        + "another provider."));
                    yield break;
                }

                if (broken is not null)
                {
                    yield return new CodegenEvent.Completed(StrategyCodegenResponse.Fail(
                        broken + " Nothing partial is kept: half a source file cannot compile. "
                        + "The turn is lost, but the rest of the run is not."));
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

                // A reasoning model on this wire format streams its thinking as `reasoning_content` (or
                // `reasoning` — the gateways disagree), a SEPARATE field, and emits no `content` at all
                // until it has finished. On a hard brief that is minutes of apparent silence.
                //
                // It used to be counted and thrown away, on the grounds that a raw chain of thought is
                // noise in a builder chat. Half right: it is noise in the TRANSCRIPT, which is why it
                // goes to its own event and the UI shows it collapsed behind a disclosure. Discarding
                // it outright meant the one honest answer to "is this thing working or has it hung?"
                // was reduced to a counter nobody sees, and a five-minute wait looked like a crash.
                if (hasDelta && ReasoningIn(delta) is { Length: > 0 } thought)
                {
                    reasoningCharacters += thought.Length;
                    yield return new CodegenEvent.ReasoningDelta(thought);
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

    /// <summary>
    /// The thinking fragment in a streamed delta, whichever name this gateway gives it.
    ///
    /// <para>There is no standard. DeepSeek and the Chinese gateways (TokenRouter's GLM route among
    /// them) send <c>reasoning_content</c>; OpenRouter and several proxies send <c>reasoning</c>.
    /// Reading only one of them means the model appears to sit in silence on every provider that chose
    /// the other spelling — so both are read, and an endpoint that sends neither simply yields
    /// nothing.</para>
    /// </summary>
    private static string? ReasoningIn(JsonElement delta)
    {
        foreach (var name in (ReadOnlySpan<string>)["reasoning_content", "reasoning"])
        {
            if (delta.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String &&
                value.GetString() is { Length: > 0 } thought)
            {
                return thought;
            }
        }

        return null;
    }

    /// <summary>
    /// Advances the stream and classifies what went wrong, for the same reason as
    /// <see cref="TrySendAsync"/>: an iterator may not yield from a catch.
    ///
    /// <para><b>A CONNECTION CAN DIE AFTER IT OPENED, and this is the third place in this file to learn
    /// it.</b> Sending was guarded and stalling was guarded; reading the body was not. So a network drop
    /// mid-generation threw an IOException straight out of the iterator, past the drain, past the
    /// builder, and out of the whole run. Measured: two runs, an hour and eighteen minutes each, four
    /// files already written between them, all discarded because DNS failed while two builders were
    /// mid-stream.</para>
    ///
    /// <para>The partial text is deliberately NOT returned. Half a C# file is not a smaller answer, it
    /// is an answer that cannot compile, and handing it back as the task's file would put a truncated
    /// source into the build and spend the repair budget on a diagnostic nobody can act on.</para>
    /// </summary>
    internal static async Task<(bool Moved, string? Stalled, string? Broken)> TryMoveAsync(
        IAsyncEnumerator<JsonElement> chunks, string displayName, CancellationToken ct = default)
    {
        try
        {
            return (await chunks.MoveNextAsync().ConfigureAwait(false), null, null);
        }
        catch (TimeoutException stalled)
        {
            return (false, stalled.Message, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // the user pressed Stop
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            return (false, null, $"{displayName} lost the connection part-way through the answer: {ex.Message}");
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
            messages.Add(WireMessage.From(m));

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

    /// <summary>The provider is asking for a pause rather than reporting a fault.</summary>
    internal static bool IsRateLimited(int status) => status is 429;

    /// <summary>Attempts before giving up. Three rather than two because two of them can now be spent
    /// waiting out a rate limit, and a limit that clears in a minute should not cost the generation.</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// How long to wait before trying again.
    ///
    /// <para><c>Retry-After</c> when the provider sends one — it knows and we do not — accepting both
    /// forms the header allows, a seconds count and an HTTP date. Otherwise an exponential back-off,
    /// and nothing at all for a dropped gateway connection, where the point is to reconnect at once.</para>
    ///
    /// <para>Capped, because a provider asking for an hour is a provider to report to the user rather
    /// than to wait for silently.</para>
    /// </summary>
    internal static TimeSpan RetryAfter(HttpResponseMessage response, int attempt)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (!IsRateLimited((int)response.StatusCode)) return TimeSpan.Zero;

        var after = response.Headers.RetryAfter;
        var asked =
            after?.Delta
            ?? (after?.Date is { } date ? (TimeSpan?)(date - DateTimeOffset.UtcNow) : null)
            ?? TimeSpan.FromSeconds(Math.Pow(2d, attempt + 1));   // 2s, then 4s

        return asked <= TimeSpan.Zero ? TimeSpan.Zero
            : asked > MaxRetryWait ? MaxRetryWait
            : asked;
    }

    /// <summary>The longest this will wait on one attempt.</summary>
    internal static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(30d);

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
    /// <summary>
    /// One message. <c>content</c> is a bare string for text and an ARRAY OF PARTS when there are
    /// pictures, which is the shape OpenAI-compatible endpoints expect.
    ///
    /// <para>The string form is kept for text rather than always sending the array, and that is not
    /// tidiness: several endpoints calling themselves OpenAI-compatible implement only the string form,
    /// and sending them a one-element array for an ordinary turn would break every text conversation to
    /// buy a feature that turn is not using.</para>
    /// </summary>
    private sealed record WireMessage(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] object Content)
    {
        public static WireMessage From(CodegenMessage message)
        {
            var role = message.Role == CodegenRole.Assistant ? "assistant" : "user";
            if (!message.HasImages) return new WireMessage(role, message.Content);

            var parts = new List<object>(message.Images!.Count + 1);
            foreach (var image in message.Images)
            {
                if (image.Caption is { Length: > 0 } caption) parts.Add(new WireTextPart(caption));
                parts.Add(new WireImagePart(new WireImageUrl(
                    $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}")));
            }

            parts.Add(new WireTextPart(message.Content));
            return new WireMessage(role, parts);
        }
    }

    private sealed record WireTextPart([property: JsonPropertyName("text")] string Text)
    {
        [JsonPropertyName("type")] public string Type => "text";
    }

    private sealed record WireImagePart([property: JsonPropertyName("image_url")] WireImageUrl ImageUrl)
    {
        [JsonPropertyName("type")] public string Type => "image_url";
    }

    private sealed record WireImageUrl([property: JsonPropertyName("url")] string Url);
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
    private sealed record Choice([property: JsonPropertyName("message")] ReplyMessage? Message);

    /// <summary>
    /// A message coming BACK, whose content is always a plain string.
    ///
    /// <para>Separate from the request shape, whose content became polymorphic when messages gained
    /// pictures. Reusing one record for both directions is what tied the reply parser to a change that
    /// had nothing to do with it — the model does not send us images.</para>
    /// </summary>
    private sealed record ReplyMessage([property: JsonPropertyName("content")] string? Content);
    private sealed record WireUsage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens);
    private sealed record ModelsResponse([property: JsonPropertyName("data")] IReadOnlyList<ModelEntry>? Data);
    private sealed record ModelEntry([property: JsonPropertyName("id")] string Id);
}
