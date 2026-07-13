using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>Per-CLI details, isolated so one vendor's output-format drift doesn't touch the others.</summary>
public sealed record AgentCliAdapter(
    string ProviderId,
    string DisplayName,
    string Executable,
    IReadOnlyList<string> Arguments,
    string? ModelFlag = null,
    string? EffortFlag = null)
{
    /// <summary>The stdin marker some CLIs take as a positional argument ("read the prompt from stdin").
    /// Flags must precede it, so <see cref="ArgumentsFor"/> inserts the model there.</summary>
    private const string StdinMarker = "-";

    /// <summary>Claude Code in print mode: <c>claude -p</c> reads the prompt from stdin and prints the
    /// reply to stdout. The user's subscription/key lives in Claude Code itself — never seen here.
    /// <c>--model</c> takes a full model id (or a short alias); <c>--effort</c> sets the reasoning
    /// effort for the run.</summary>
    public static AgentCliAdapter ClaudeCode { get; } =
        new("claude-cli", "Claude Code (installed CLI)", "claude", ["-p"],
            ModelFlag: "--model", EffortFlag: "--effort")
        {
            // --include-partial-messages is what turns the JSONL into token-by-token deltas rather than
            // one lump at the end; --verbose is required by the CLI alongside stream-json.
            StreamFlags = ["--output-format", "stream-json", "--include-partial-messages", "--verbose"],
        };

    /// <summary>OpenAI Codex CLI: <c>codex exec</c> runs a one-shot prompt from stdin, ChatGPT sign-in
    /// handled by the CLI. No effort flag — it configures reasoning through its own config.</summary>
    public static AgentCliAdapter Codex { get; } =
        new("codex-cli", "Codex (installed CLI)", "codex", ["exec", StdinMarker], ModelFlag: "-m");

    public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];

    /// <summary>Flags that make the CLI emit its events as JSONL instead of plain text. Claude Code wraps
    /// the very same Anthropic stream events (<c>{"type":"stream_event","event":{…}}</c>), so the API's
    /// parser reads them unchanged. Null ⇒ this CLI cannot stream and the caller falls back to one shot.</summary>
    public IReadOnlyList<string>? StreamFlags { get; init; }

    /// <summary>The argv for a run, with the model and effort flags inserted before the stdin marker (if
    /// any) so they parse as options and not as the prompt. Unset ⇒ the CLI uses its own defaults.</summary>
    public IReadOnlyList<string> ArgumentsFor(
        string? model, CodegenEffort effort = CodegenEffort.Default, bool stream = false)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(model) && ModelFlag is not null)
        {
            flags.Add(ModelFlag);
            flags.Add(model);
        }
        if (effort.Wire() is { } level && EffortFlag is not null)
        {
            flags.Add(EffortFlag);
            flags.Add(level);
        }
        if (stream && StreamFlags is { Count: > 0 } streaming)
            flags.AddRange(streaming);

        if (flags.Count == 0) return Arguments;

        var before = Arguments.TakeWhile(a => a != StdinMarker);
        var after = Arguments.SkipWhile(a => a != StdinMarker);
        return [.. before, .. flags, .. after];
    }
}

/// <summary>
/// Codegen by driving an installed agent CLI (Claude Code / Codex) headless: the flattened prompt is
/// written to the child's stdin, the reply read from stdout, with a wall-clock timeout and a kill-tree
/// on overrun (the same subprocess discipline as the Python sidecar). The vendor CLI owns its own login,
/// so no credentials pass through here.
/// <para>Availability is "the executable resolves on PATH". CLI output formats drift, so the fenced-code
/// extraction is tolerant and a non-zero exit is surfaced with guidance, never a crash.</para>
/// </summary>
public sealed class AgentCliCodegenClient : IStrategyCodegenClient
{
    private readonly AgentCliAdapter _adapter;
    private readonly Func<string, string?> _resolveOnPath;
    private readonly TimeSpan _timeout;
    private readonly string? _model;
    private readonly CodegenEffort _effort;

    public AgentCliCodegenClient(
        AgentCliAdapter adapter, Func<string, string?>? resolveOnPath = null, TimeSpan? timeout = null,
        string? model = null, CodegenEffort effort = CodegenEffort.Default)
    {
        _adapter = adapter;
        _resolveOnPath = resolveOnPath ?? ResolveOnPath;
        // A long brief at a high effort is a multi-minute run; the old 3-minute wall killed exactly the
        // generations worth waiting for. Configurable via AiCodegen:TimeoutSeconds.
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _model = model;
        _effort = effort;
    }

    public string ProviderId => _adapter.ProviderId;
    public string DisplayName => _adapter.DisplayName;
    public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;

    /// <summary>Empty ⇒ the vendor CLI uses whatever model it is configured for.</summary>
    public string Model => _model ?? string.Empty;
    public CodegenEffort Effort => _effort;
    public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);

    /// <summary>
    /// Streams the CLI's <c>--output-format stream-json</c>: one JSON object per line, most of them
    /// wrapping the very same Anthropic events the API's SSE stream carries — so the API parser reads them
    /// unchanged. The CLI's own <c>result</c> line carries the authoritative final text.
    /// <para>A CLI with no streaming mode (Codex) falls back to the one-shot path; the caller can't tell.</para>
    /// </summary>
    public async IAsyncEnumerable<CodegenEvent> StreamAsync(
        StrategyCodegenRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var exe = _resolveOnPath(_adapter.Executable);
        if (exe is null || _adapter.StreamFlags is null)
        {
            yield return new CodegenEvent.Completed(await GenerateAsync(request, ct).ConfigureAwait(false));
            yield break;
        }

        var psi = ProcessFor(exe, stream: true);
        using var process = new Process { StartInfo = psi };

        if (!process.Start())
        {
            yield return new CodegenEvent.Completed(
                StrategyCodegenResponse.Fail($"Could not start {_adapter.Executable}."));
            yield break;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        await process.StandardInput.WriteAsync(FlattenPrompt(request).AsMemory(), timeoutCts.Token).ConfigureAwait(false);
        process.StandardInput.Close();

        var accumulator = new AnthropicEventAccumulator();
        string? finalText = null;
        string? cliError = null;

        while (true)
        {
            var (line, failure) = await ReadLineAsync(process, timeoutCts.Token, ct).ConfigureAwait(false);
            if (failure is not null)
            {
                yield return new CodegenEvent.Completed(StrategyCodegenResponse.Fail(failure));
                yield break;
            }
            if (line is null) break;
            if (line.Length == 0 || line[0] != '{') continue;

            JsonElement message;
            try
            {
                message = JsonDocument.Parse(line).RootElement.Clone();
            }
            catch (JsonException)
            {
                continue; // the CLI also prints non-JSON chatter; it is not worth failing a run over
            }

            if (!message.TryGetProperty("type", out var type)) continue;

            switch (type.GetString())
            {
                case "stream_event" when message.TryGetProperty("event", out var evt):
                    foreach (var streamed in accumulator.Consume(evt))
                        yield return streamed;
                    break;

                case "result":
                    // The CLI's own summary line — authoritative for the final text and for failure.
                    if (message.TryGetProperty("is_error", out var isError) &&
                        isError.ValueKind == JsonValueKind.True)
                    {
                        cliError = message.TryGetProperty("result", out var errText) ? errText.GetString() : null;
                    }
                    else if (message.TryGetProperty("result", out var result))
                    {
                        finalText = result.GetString();
                    }
                    break;
            }
        }

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        if (cliError is not null)
        {
            yield return new CodegenEvent.Completed(
                StrategyCodegenResponse.Fail($"{_adapter.DisplayName} failed: {Trim(cliError)}"));
            yield break;
        }

        yield return new CodegenEvent.Completed(Assemble(finalText ?? accumulator.Text, accumulator.Usage));
    }

    /// <summary>Reads one line, turning a timeout into a message rather than an exception — an iterator
    /// cannot yield from inside a try/catch, so the catching lives here.</summary>
    private async Task<(string? Line, string? Failure)> ReadLineAsync(
        Process process, CancellationToken timeoutToken, CancellationToken userToken)
    {
        try
        {
            return (await process.StandardOutput.ReadLineAsync(timeoutToken).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (userToken.IsCancellationRequested)
        {
            KillTree(process);
            throw; // the user pressed Stop
        }
        catch (OperationCanceledException)
        {
            KillTree(process);
            return (null, $"{_adapter.DisplayName} timed out after {_timeout.TotalSeconds:0}s. A long brief at a " +
                          "high reasoning effort can take several minutes — raise AiCodegen:TimeoutSeconds, or lower Effort.");
        }
    }

    /// <summary>No code is a legitimate turn — the agent is asking something back.</summary>
    private StrategyCodegenResponse Assemble(string text, CodegenUsage usage)
    {
        var files = CodegenCodeExtractor.ExtractFiles(text);
        return files.Count == 0
            ? StrategyCodegenResponse.Reply(text, usage)
            : StrategyCodegenResponse.Ok(files, text, usage);
    }

    private ProcessStartInfo ProcessFor(string exe, bool stream)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in _adapter.ArgumentsFor(_model, _effort, stream)) psi.ArgumentList.Add(arg);
        return psi;
    }

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        var exe = _resolveOnPath(_adapter.Executable);
        if (exe is null)
            return StrategyCodegenResponse.Fail($"{_adapter.Executable} is not on PATH — install it, or pick a keyed provider.");

        var prompt = FlattenPrompt(request);

        using var process = new Process { StartInfo = ProcessFor(exe, stream: false) };
        try
        {
            if (!process.Start())
                return StrategyCodegenResponse.Fail($"Could not start {_adapter.Executable}.");

            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                KillTree(process);
                return StrategyCodegenResponse.Fail(
                    $"{_adapter.DisplayName} timed out after {_timeout.TotalSeconds:0}s. A long brief at a high " +
                    "reasoning effort can take several minutes — raise AiCodegen:TimeoutSeconds, or lower Effort.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                return StrategyCodegenResponse.Fail($"{_adapter.DisplayName} exited {process.ExitCode}: {Trim(stderr)}");
            }

            // No code is a legitimate turn — the agent is asking something back; the session shows it in
            // the chat and waits. (Plain print mode reports no token usage; the streaming path does.)
            return Assemble(stdout, CodegenUsage.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            KillTree(process);
            return StrategyCodegenResponse.Fail($"{_adapter.DisplayName} failed: {ex.Message}");
        }
    }

    /// <summary>Flattens the pack + conversation into one prompt (the CLIs take a single string, not a
    /// role array). The pack leads; each turn is labelled so the model keeps the thread.</summary>
    internal static string FlattenPrompt(StrategyCodegenRequest request)
    {
        var sb = new StringBuilder(request.SystemContext).AppendLine().AppendLine();
        foreach (var m in request.Messages)
            sb.Append(m.Role == CodegenRole.Assistant ? "ASSISTANT: " : "USER: ").AppendLine(m.Content).AppendLine();
        sb.AppendLine("Answer per the OUTPUT CONTRACT in the context above: one ```csharp fenced block per file, " +
                      "each starting with a `// file: <Name>.cs` line. Ask a question instead if the request is ambiguous.");
        return sb.ToString();
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    /// <summary>Resolves an executable against PATH (with the platform's executable extensions), so
    /// <see cref="IsAvailable"/> doesn't pay a process launch.</summary>
    private static string? ResolveOnPath(string executable)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : [string.Empty];

        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, executable + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
