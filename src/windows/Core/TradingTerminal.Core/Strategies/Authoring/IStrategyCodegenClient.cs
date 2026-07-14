namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>Who is speaking in a codegen conversation.</summary>
public enum CodegenRole
{
    User,
    Assistant,
}

/// <summary>
/// How hard the model should think before answering — the reasoning/output effort dial. It trades
/// latency and tokens for quality: a scalper with a subtle exit rule is worth <c>XHigh</c>; a rename is
/// not. <see cref="Default"/> sends nothing and lets the provider (or the vendor CLI) choose, which is
/// the only safe setting for a model that predates the parameter.
/// </summary>
public enum CodegenEffort
{
    /// <summary>Send no effort/thinking parameters — the provider's own default.</summary>
    Default,
    Low,
    Medium,
    High,

    /// <summary>Between High and Max. The best setting for most coding work on current models.</summary>
    XHigh,

    /// <summary>Correctness over cost.</summary>
    Max,
}

/// <summary>Wire values for <see cref="CodegenEffort"/> (<c>low</c>/<c>medium</c>/<c>high</c>/<c>xhigh</c>/<c>max</c>).</summary>
public static class CodegenEfforts
{
    /// <summary>The wire value, or null for <see cref="CodegenEffort.Default"/> (send nothing).</summary>
    public static string? Wire(this CodegenEffort effort) => effort switch
    {
        CodegenEffort.Low => "low",
        CodegenEffort.Medium => "medium",
        CodegenEffort.High => "high",
        CodegenEffort.XHigh => "xhigh",
        CodegenEffort.Max => "max",
        _ => null,
    };

    /// <summary>Parses a configured/persisted value; anything unrecognized is
    /// <see cref="CodegenEffort.Default"/> rather than an error.</summary>
    public static CodegenEffort Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "low" => CodegenEffort.Low,
        "medium" => CodegenEffort.Medium,
        "high" => CodegenEffort.High,
        "xhigh" or "x-high" => CodegenEffort.XHigh,
        "max" => CodegenEffort.Max,
        _ => CodegenEffort.Default,
    };
}

/// <summary>One turn of the codegen conversation.</summary>
public sealed record CodegenMessage(CodegenRole Role, string Content);

/// <summary>
/// Tokens billed for one generation, as reported by the provider (both the OpenAI and Anthropic wire
/// shapes return this on every reply; agent CLIs and local models may not, hence <see cref="None"/>).
/// The builder sums it across a session so the user can see what a strategy cost to write.
/// </summary>
/// <param name="InputTokens">The whole prompt, cached portion included.</param>
/// <param name="OutputTokens">What the model wrote.</param>
/// <param name="CachedInputTokens">How much of the prompt the provider served from its cache — billed at
/// roughly a tenth. Surfacing it separately is the only way to see whether caching is actually working;
/// a long session where this stays at zero is a session paying full price for the same context every
/// turn.</param>
public sealed record CodegenUsage(int InputTokens, int OutputTokens, int CachedInputTokens = 0)
{
    public static CodegenUsage None { get; } = new(0, 0);

    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>True when the provider actually reported usage (a CLI that doesn't is not "0 tokens").</summary>
    public bool IsReported => InputTokens > 0 || OutputTokens > 0;

    public CodegenUsage Add(CodegenUsage? other) => other is null
        ? this
        : new(InputTokens + other.InputTokens,
              OutputTokens + other.OutputTokens,
              CachedInputTokens + other.CachedInputTokens);
}

/// <summary>
/// A request to generate strategy source. <paramref name="SystemContext"/> is the AI context pack (the
/// SDK contract + rules + output contract); <paramref name="Messages"/> is the running conversation —
/// the first is the user's instruction, and the auto-fix loop appends the model's last answer plus the
/// compiler errors so the model can correct itself.
/// </summary>
public sealed record StrategyCodegenRequest(string SystemContext, IReadOnlyList<CodegenMessage> Messages);

/// <summary>
/// The outcome of one generation. <paramref name="Files"/> is the extracted C# — one entry per file the
/// model emitted (a strategy is usually several: kernel, helpers, plugin descriptor, view-model, view).
/// <paramref name="Code"/> is the first file's source, kept for single-file callers.
/// <paramref name="RawText"/> is the full model reply (the transcript the user reads), and
/// <paramref name="Error"/> a provider-level failure (auth, timeout, no CLI) — distinct from a compile
/// failure, which the caller discovers by compiling the files.
/// <para>
/// A successful response with NO files is a conversational reply — the model asking a clarifying
/// question rather than writing code. That is a normal turn, not an error.
/// </para>
/// </summary>
public sealed record StrategyCodegenResponse(
    bool Success,
    string? Code,
    string? RawText,
    string? Error,
    IReadOnlyList<StrategyFile>? Files = null,
    CodegenUsage? Usage = null)
{
    /// <summary>The generated files, never null — falls back to <see cref="Code"/> as a single file.</summary>
    public IReadOnlyList<StrategyFile> FileList => Files ?? (string.IsNullOrWhiteSpace(Code)
        ? []
        : [new StrategyFile(StrategyFile.DefaultName, Code)]);

    /// <summary>The model wrote code this turn.</summary>
    public bool HasFiles => FileList.Count > 0;

    public static StrategyCodegenResponse Ok(string code, string rawText) => new(true, code, rawText, null);

    public static StrategyCodegenResponse Ok(IReadOnlyList<StrategyFile> files, string rawText, CodegenUsage? usage = null) =>
        new(true, files.Count > 0 ? files[0].Content : null, rawText, null, files, usage);

    /// <summary>A prose-only turn — the model answered or asked something instead of emitting code.</summary>
    public static StrategyCodegenResponse Reply(string rawText, CodegenUsage? usage = null) =>
        new(true, null, rawText, null, [], usage);

    public static StrategyCodegenResponse Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// One thing that happened while the model was answering. A generation is minutes long on a hard brief,
/// so the builder streams: text arrives token by token, usage lands as the provider reports it, and the
/// turn ends with the assembled response. A provider that cannot stream still yields a single
/// <see cref="Completed"/> — the caller never has to branch on whether streaming was available.
/// </summary>
public abstract record CodegenEvent
{
    private CodegenEvent() { }

    /// <summary>A fragment of the model's reply, in order. Append it to what you already have.</summary>
    public sealed record TextDelta(string Text) : CodegenEvent;

    /// <summary>Tokens billed so far in this generation, as the provider reports them (input up front,
    /// output as it writes). Absolute, not incremental — replace the running value, don't add to it.</summary>
    public sealed record UsageUpdate(CodegenUsage Usage) : CodegenEvent;

    /// <summary>The generation is over; carries the assembled reply, its files, and the final usage.</summary>
    public sealed record Completed(StrategyCodegenResponse Response) : CodegenEvent;
}

/// <summary>
/// Generates strategy source from a natural-language instruction — the seam behind the AI Strategy
/// Builder. Implementations wrap a provider: an installed agent CLI (Claude Code, Codex — the user's
/// own login lives in the vendor tool, never seen here), an OpenAI-compatible / Anthropic HTTP API with
/// a BYO key, or a local Ollama model. A <c>Fake</c> implementation drives the tests and the CLI's
/// <c>--provider fake</c>.
/// <para>
/// This produces UNTRUSTED code. Nothing it returns reaches the strategy catalog without going through
/// the same policy scan + consent gate as any plugin (a generated strategy that P/Invokes fails to
/// compile). The client never registers anything itself — it only returns text.
/// </para>
/// </summary>
public interface IStrategyCodegenClient
{
    /// <summary>Stable id (<c>fake</c>, <c>openai</c>, <c>anthropic</c>, <c>claude-cli</c>,
    /// <c>codex-cli</c>, <c>ollama</c>, …) — the settings key and the CLI's <c>--provider</c> value.</summary>
    string ProviderId { get; }

    /// <summary>Human-readable name for the provider picker.</summary>
    string DisplayName { get; }

    /// <summary>True when this provider can actually be used right now — its CLI is on PATH, or its API
    /// key is configured. A pane/CLI shows setup guidance instead of calling an unavailable provider.</summary>
    bool IsAvailable { get; }

    /// <summary>The model this client will call (empty when the provider picks its own — an agent CLI
    /// with no explicit model uses whatever the vendor tool is configured for).</summary>
    string Model => string.Empty;

    /// <summary>How hard this client asks the model to think. <see cref="CodegenEffort.Default"/> sends
    /// no effort/thinking parameters at all — required for models that predate them.</summary>
    CodegenEffort Effort => CodegenEffort.Default;

    /// <summary>The models offered in the picker without asking the provider — the configured one plus a
    /// curated shortlist. The UI also allows a free-text model id, so this need not be exhaustive.</summary>
    IReadOnlyList<string> KnownModels => [];

    /// <summary>Asks the provider what models this key can actually use (OpenAI/Anthropic both expose a
    /// models endpoint). Returns empty when the provider has no such endpoint — the caller falls back to
    /// <see cref="KnownModels"/>. Never throws: a failed lookup is an empty list.</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default);

    /// <summary>
    /// The same generation, streamed. Yields <see cref="CodegenEvent.TextDelta"/> as the model writes,
    /// <see cref="CodegenEvent.UsageUpdate"/> as the provider reports tokens, and exactly one
    /// <see cref="CodegenEvent.Completed"/> last.
    /// <para>
    /// The default implementation just runs <see cref="GenerateAsync"/> and yields its result — so a
    /// provider that cannot stream (or a fake) needs no code, and callers never branch on whether
    /// streaming was available. Providers that can stream override this; their
    /// <see cref="GenerateAsync"/> stays the non-streaming path.
    /// </para>
    /// </summary>
    async IAsyncEnumerable<CodegenEvent> StreamAsync(
        StrategyCodegenRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GenerateAsync(request, ct).ConfigureAwait(false);
        if (response.Usage is { IsReported: true } usage) yield return new CodegenEvent.UsageUpdate(usage);
        yield return new CodegenEvent.Completed(response);
    }
}
