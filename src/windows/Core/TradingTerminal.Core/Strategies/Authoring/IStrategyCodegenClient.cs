namespace TradingTerminal.Core.Strategies.Authoring;

/// <summary>Who is speaking in a codegen conversation.</summary>
public enum CodegenRole
{
    User,
    Assistant,
}

/// <summary>One turn of the codegen conversation.</summary>
public sealed record CodegenMessage(CodegenRole Role, string Content);

/// <summary>
/// A request to generate strategy source. <paramref name="SystemContext"/> is the AI context pack (the
/// SDK contract + rules + output contract); <paramref name="Messages"/> is the running conversation —
/// the first is the user's instruction, and the auto-fix loop appends the model's last answer plus the
/// compiler errors so the model can correct itself.
/// </summary>
public sealed record StrategyCodegenRequest(string SystemContext, IReadOnlyList<CodegenMessage> Messages);

/// <summary>
/// The outcome of one generation. <paramref name="Code"/> is the extracted C# (the single-file kernel),
/// <paramref name="RawText"/> the full model reply (kept for the transcript), <paramref name="Error"/>
/// a provider-level failure (auth, timeout, no CLI) — distinct from a compile failure, which the caller
/// discovers by compiling <paramref name="Code"/>.
/// </summary>
public sealed record StrategyCodegenResponse(bool Success, string? Code, string? RawText, string? Error)
{
    public static StrategyCodegenResponse Ok(string code, string rawText) => new(true, code, rawText, null);
    public static StrategyCodegenResponse Fail(string error) => new(false, null, null, error);
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

    Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default);
}
