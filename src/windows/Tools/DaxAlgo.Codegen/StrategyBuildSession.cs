using System.Text;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>What one turn of the conversation produced.</summary>
public enum BuildTurnKind
{
    /// <summary>The model wrote files and they compiled (possibly after auto-fixes).</summary>
    Compiled,

    /// <summary>The model wrote files that still don't compile after the auto-fix bound.</summary>
    CompileFailed,

    /// <summary>The model replied without code — it is asking the user something, or explaining.</summary>
    Question,

    /// <summary>The provider itself failed (no key, no CLI, timeout). Retrying won't fix it.</summary>
    ProviderError,
}

/// <summary>
/// The result of one user turn: what the model said (for the chat), the files it produced, the compile
/// result, and what the turn cost.
/// </summary>
/// <param name="Kind">How the turn ended — the UI branches on this, not on booleans.</param>
/// <param name="AssistantText">The model's reply verbatim (prose + fences) — what the chat shows.</param>
/// <param name="Files">The files the model emitted this turn (empty for a question).</param>
/// <param name="Compile">The last compile result, or null when nothing was compiled.</param>
/// <param name="Error">Provider-level error text, when <see cref="BuildTurnKind.ProviderError"/>.</param>
/// <param name="Generations">How many times the model was called this turn (1 + auto-fix retries).</param>
/// <param name="Usage">Tokens billed this turn, summed over its generations.</param>
public sealed record StrategyBuildTurn(
    BuildTurnKind Kind,
    string AssistantText,
    IReadOnlyList<StrategyFile> Files,
    StrategyCompileResult? Compile,
    string? Error,
    int Generations,
    CodegenUsage Usage)
{
    public bool Success => Kind == BuildTurnKind.Compiled;
}

/// <summary>
/// A running conversation with one provider about one strategy — the object behind the builder's chat.
/// <para>
/// It owns the message list, so every turn (the first instruction, a follow-up like "make the stop
/// tighter", an answer to the model's own question, the compiler's errors) lands in the SAME thread and
/// the model keeps its context. Inside a turn it runs the build loop: generate → compile through the
/// shared <see cref="IStrategyCompiler"/> (so the policy scan + version gates apply) → feed any errors
/// back → retry, bounded. It never registers anything: the caller decides what to do with a compiled
/// result, through the same scan + consent gate as a plugin.
/// </para>
/// <para>
/// A reply with no code is not an error — it is the model asking a clarifying question. The session
/// returns it as <see cref="BuildTurnKind.Question"/> and waits for the user's next
/// <see cref="SendAsync"/>, which is what makes the builder conversational rather than one-shot.
/// </para>
/// </summary>
public sealed class StrategyBuildSession
{
    private readonly IStrategyCompiler _compiler;
    private readonly ILogger? _logger;
    private readonly List<CodegenMessage> _messages = [];

    /// <param name="history">A thread to resume — the conversation as it stood when the app last closed.
    /// Replaying it is what makes a restored chat more than a transcript: the model can still answer
    /// "now tighten the stop" because it remembers what it wrote.</param>
    /// <param name="priorUsage">Tokens already spent on this thread, so the counter continues rather than
    /// restarting at zero.</param>
    internal StrategyBuildSession(
        IStrategyCompiler compiler,
        IStrategyCodegenClient provider,
        string systemContext,
        string strategyId,
        string displayName,
        int maxFixAttempts,
        ILogger? logger = null,
        IReadOnlyList<CodegenMessage>? history = null,
        CodegenUsage? priorUsage = null)
    {
        _compiler = compiler;
        _logger = logger;
        Provider = provider;
        SystemContext = systemContext;
        StrategyId = strategyId;
        DisplayName = displayName;
        MaxFixAttempts = Math.Max(0, maxFixAttempts);

        if (history is { Count: > 0 }) _messages.AddRange(history);
        TotalUsage = priorUsage ?? CodegenUsage.None;
    }

    public IStrategyCodegenClient Provider { get; }
    public string SystemContext { get; }
    public string StrategyId { get; }
    public string DisplayName { get; }
    public int MaxFixAttempts { get; }

    /// <summary>The whole thread so far — user turns, model replies, and the auto-fix prompts.</summary>
    public IReadOnlyList<CodegenMessage> Transcript => _messages;

    /// <summary>The most recent files the model produced (across turns — a follow-up rewrites them).</summary>
    public IReadOnlyList<StrategyFile> Files { get; private set; } = [];

    /// <summary>Tokens billed across the whole session.</summary>
    public CodegenUsage TotalUsage { get; private set; } = CodegenUsage.None;

    /// <summary>
    /// Send one user turn and run the build loop over the reply.
    /// </summary>
    /// <param name="userMessage">What the user typed — an instruction, a follow-up, or an answer to the
    /// model's question.</param>
    /// <param name="activity">Progress for the UI's activity strip ("Asking Claude…", "Compiling 3
    /// file(s)…", "Fixing 2 error(s)…"). Reported on the calling context.</param>
    /// <param name="events">Streamed events — text deltas as the model writes, usage as the provider
    /// reports it. A provider that can't stream reports nothing here and the turn still returns normally,
    /// so the caller never branches on it.</param>
    public async Task<StrategyBuildTurn> SendAsync(
        string userMessage,
        IProgress<string>? activity = null,
        CancellationToken ct = default,
        IProgress<CodegenEvent>? events = null)
    {
        _messages.Add(new CodegenMessage(CodegenRole.User, userMessage));

        // One generation, plus MaxFixAttempts more that each get the compiler's errors fed back.
        var totalGenerations = MaxFixAttempts + 1;
        var usage = CodegenUsage.None;
        StrategyCompileResult? lastCompile = null;
        IReadOnlyList<StrategyFile> lastFiles = [];
        var lastText = string.Empty;

        for (var generation = 1; generation <= totalGenerations; generation++)
        {
            ct.ThrowIfCancellationRequested();

            activity?.Report(generation == 1
                ? $"Asking {Provider.DisplayName}…"
                : $"Asking {Provider.DisplayName} to fix {Count(lastCompile)} error(s)…");

            // Stream it. A provider that can't yields one Completed and nothing else, so this is the only
            // path — there is no non-streaming branch to keep in step.
            StrategyCodegenResponse? response = null;
            var generationUsage = CodegenUsage.None;

            await foreach (var evt in Provider
                .StreamAsync(new StrategyCodegenRequest(SystemContext, _messages), ct)
                .ConfigureAwait(false))
            {
                switch (evt)
                {
                    case CodegenEvent.TextDelta:
                        events?.Report(evt);
                        break;

                    case CodegenEvent.UsageUpdate update:
                        // Absolute for THIS generation — replace it, then re-derive the running totals, so
                        // an auto-fix retry doesn't double-count the generations before it.
                        generationUsage = update.Usage;
                        events?.Report(evt);
                        break;

                    case CodegenEvent.Completed completed:
                        response = completed.Response;
                        break;
                }
            }

            response ??= StrategyCodegenResponse.Fail($"{Provider.DisplayName} returned nothing.");
            var reported = response.Usage ?? generationUsage;
            usage = usage.Add(reported);
            TotalUsage = TotalUsage.Add(reported);

            if (!response.Success)
            {
                // A provider-level failure (auth, timeout, no CLI) — not a compile error. Stop; retrying
                // won't fix a missing key.
                _logger?.LogWarning("Codegen provider {Provider} failed: {Error}", Provider.ProviderId, response.Error);
                activity?.Report($"{Provider.DisplayName} failed.");
                return new StrategyBuildTurn(
                    BuildTurnKind.ProviderError, string.Empty, lastFiles, lastCompile,
                    response.Error ?? "The provider returned nothing.", generation - 1, usage);
            }

            // Record the model's turn verbatim so the transcript reads naturally and the next call has context.
            lastText = response.RawText ?? string.Empty;
            _messages.Add(new CodegenMessage(CodegenRole.Assistant, lastText));

            if (!response.HasFiles)
            {
                // Prose, no code: the model is asking something back. Hand it to the user and stop —
                // auto-fixing a question would be nonsense.
                activity?.Report($"{Provider.DisplayName} has a question.");
                return new StrategyBuildTurn(
                    BuildTurnKind.Question, lastText, lastFiles, lastCompile, null, generation, usage);
            }

            lastFiles = response.FileList;
            Files = lastFiles;

            activity?.Report($"Compiling {lastFiles.Count} file(s)…");
            var compile = _compiler.Compile(new StrategyScript(StrategyId, DisplayName, lastFiles));
            lastCompile = compile;

            if (compile.Success)
            {
                _logger?.LogInformation(
                    "AI-authored strategy {Id} compiled on generation {Generation}/{Total} ({Files} file(s))",
                    StrategyId, generation, totalGenerations, lastFiles.Count);
                activity?.Report($"Compiled {lastFiles.Count} file(s) cleanly.");
                return new StrategyBuildTurn(
                    BuildTurnKind.Compiled, lastText, lastFiles, compile, null, generation, usage);
            }

            if (generation < totalGenerations)
                _messages.Add(new CodegenMessage(CodegenRole.User, FixPrompt(compile)));
        }

        _logger?.LogWarning("AI-authored strategy {Id} did not compile after {Total} generation(s)",
            StrategyId, totalGenerations);
        activity?.Report($"Still {Count(lastCompile)} error(s) after {totalGenerations} attempt(s).");
        return new StrategyBuildTurn(
            BuildTurnKind.CompileFailed, lastText, lastFiles, lastCompile, null, totalGenerations, usage);
    }

    /// <summary>Push the user's hand-edits back into the thread, so a follow-up doesn't ask the model to
    /// patch a version of the code that no longer exists.</summary>
    public void SyncEditedFiles(IReadOnlyList<StrategyFile> files) => Files = files;

    private static int Count(StrategyCompileResult? compile) => compile?.Errors.Count() ?? 0;

    /// <summary>The auto-fix message: the compiler's own errors, verbatim, and a request for the whole
    /// corrected file set (partial diffs confuse the file-per-fence contract).</summary>
    private static string FixPrompt(StrategyCompileResult compile)
    {
        var sb = new StringBuilder(
            "The code did not compile. Fix these errors and return the COMPLETE corrected file set " +
            "(every file, each in its own fenced block with its `// file:` header):\n");
        foreach (var error in compile.Errors)
            sb.Append("- ").Append(error.Id).Append(' ').Append(error.Location).Append(": ").AppendLine(error.Message);
        return sb.ToString();
    }
}
