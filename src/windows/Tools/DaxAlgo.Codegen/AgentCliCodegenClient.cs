using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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
    string? EffortFlag = null,
    string? ProfileFlag = null)
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

            // One JSON object carrying the reply AND its usage. Plain text reports no usage at all, so a
            // one-shot run read as zero tokens however much it cost.
            OneShotFlags = ["--output-format", "json"],

            // WHAT A PLAIN `claude -p` COSTS BEFORE A BYTE OF OURS IS SENT, measured 2026-09-13 from an
            // empty folder: 24,882 tokens — Claude Code's own system prompt, every built-in tool's
            // definition, the user's MCP servers, skills and CLAUDE.md. With these flags and the pack as
            // the system prompt: 540. Hyperion needs none of it — every call is prompt in, fenced text
            // out — and a model holding Read and Bash spends extra turns exploring a folder that has
            // nothing to do with the brief.
            //
            // --safe-mode and not --bare. --bare is the obvious flag and it reads ONLY an API key, so on
            // the subscription sign-in — the reason anyone picks this provider — it returns nothing.
            PromptOnlyFlags =
            [
                "--tools", "",
                "--safe-mode",
                "--strict-mcp-config",
                "--disable-slash-commands",
                "--no-session-persistence",
            ],

            SystemPromptFileFlag = "--system-prompt-file",
        };

    /// <summary>OpenAI Codex CLI: <c>codex exec</c> runs a one-shot prompt from stdin, ChatGPT sign-in
    /// handled by the CLI. Hyperion is not itself a Git repository, so the repository-presence check
    /// is skipped while its prompt-only subprocess stays read-only. No effort flag — it configures
    /// reasoning through its own config.</summary>
    public static AgentCliAdapter Codex { get; } =
        new("codex-cli", "Codex (installed CLI)", "codex",
            ["exec", "--skip-git-repo-check", "--sandbox", "read-only", StdinMarker],
            ModelFlag: "-m", ProfileFlag: "--profile")
        {
            // Not --ignore-user-config: a --profile is layered on top of that file, so ignoring it would
            // silently discard the profile the user configured.
            PromptOnlyFlags = ["--ephemeral"],
        };

    public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];

    /// <summary>Flags that make the CLI emit its events as JSONL instead of plain text. Claude Code wraps
    /// the very same Anthropic stream events (<c>{"type":"stream_event","event":{…}}</c>), so the API's
    /// parser reads them unchanged. Null ⇒ this CLI cannot stream and the caller falls back to one shot.</summary>
    public IReadOnlyList<string>? StreamFlags { get; init; }

    /// <summary>Flags for a non-streaming run that make the CLI answer with one JSON result carrying
    /// usage. Null ⇒ the reply is read as plain text and no usage is reported.</summary>
    public IReadOnlyList<string>? OneShotFlags { get; init; }

    /// <summary>Flags that strip the vendor's own agent harness from a call that only needs a prompt
    /// answered — its tools, its plugins, its saved sessions. Sent on every run.</summary>
    public IReadOnlyList<string> PromptOnlyFlags { get; init; } = [];

    /// <summary>
    /// The flag that reads the system prompt from a file, or null when the CLI has none and the shared
    /// pack has to ride in the prompt itself.
    ///
    /// <para><b>This is what makes the pack cacheable.</b> Flattened into the prompt, the pack and the
    /// task are one text block that ends differently on every call, and a prompt cache matches whole
    /// blocks — so every call wrote the pack to the cache again and none ever read it back. Measured on
    /// the live runs of 2026-09-12: 52–70k tokens written on every call, the only cache read Claude
    /// Code's own 16k prompt. As the system prompt the pack is a block of its own, identical across the
    /// run, and every call after the first reads it.</para>
    ///
    /// <para>Codex has no need of one: OpenAI's cache matches a raw token prefix, and the flattened
    /// prompt already leads with the pack.</para>
    /// </summary>
    public string? SystemPromptFileFlag { get; init; }

    /// <summary>The argv for a run, with the model and effort flags inserted before the stdin marker (if
    /// any) so they parse as options and not as the prompt. Unset ⇒ the CLI uses its own defaults.
    /// <c>systemPromptFile</c> is the file holding the shared pack when the pack is sent as the system
    /// prompt; a CLI with no <see cref="SystemPromptFileFlag"/> ignores it.</summary>
    public IReadOnlyList<string> ArgumentsFor(
        string? model, CodegenEffort effort = CodegenEffort.Default, bool stream = false,
        string? cliProfile = null, string? systemPromptFile = null)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(cliProfile) && ProfileFlag is not null)
        {
            flags.Add(ProfileFlag);
            flags.Add(cliProfile.Trim());
        }
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

        flags.AddRange(PromptOnlyFlags);

        if (!string.IsNullOrWhiteSpace(systemPromptFile) && SystemPromptFileFlag is not null)
        {
            flags.Add(SystemPromptFileFlag);
            flags.Add(systemPromptFile);
        }

        if (stream && StreamFlags is { Count: > 0 } streaming)
            flags.AddRange(streaming);
        else if (!stream && OneShotFlags is { Count: > 0 } oneShot)
            flags.AddRange(oneShot);

        if (flags.Count == 0) return Arguments;

        var before = Arguments.TakeWhile(a => a != StdinMarker);
        var after = Arguments.SkipWhile(a => a != StdinMarker);
        return [.. before, .. flags, .. after];
    }
}

/// <summary>
/// Codegen by driving an installed agent CLI (Claude Code / Codex) headless: the prompt is written to the
/// child's stdin, the reply read from stdout, with a wall-clock timeout and a kill-tree on overrun (the
/// same subprocess discipline as the Python sidecar). The vendor CLI owns its own login, so no
/// credentials pass through here.
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
    private readonly string? _cliProfile;
    private readonly string _scratch;

    /// <summary><c>scratchDirectory</c> is where the empty working folder and the pack files live —
    /// <c>%TEMP%\DaxAlgo\hyperion-cli</c> unless a test passes its own.</summary>
    public AgentCliCodegenClient(
        AgentCliAdapter adapter, Func<string, string?>? resolveOnPath = null, TimeSpan? timeout = null,
        string? model = null, CodegenEffort effort = CodegenEffort.Default, string? cliProfile = null,
        string? scratchDirectory = null)
    {
        _adapter = adapter;
        _resolveOnPath = resolveOnPath ?? ResolveOnPath;
        // A long brief at a high effort is a multi-minute run; the old 3-minute wall killed exactly the
        // generations worth waiting for. Configurable via AiCodegen:TimeoutSeconds.
        _timeout = timeout ?? TimeSpan.FromMinutes(10);
        _model = model;
        _effort = effort;
        _cliProfile = cliProfile;
        _scratch = scratchDirectory ?? Path.Combine(Path.GetTempPath(), "DaxAlgo", "hyperion-cli");
    }

    public string ProviderId => _adapter.ProviderId;
    public string DisplayName => _adapter.DisplayName;
    public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;

    /// <summary>Empty ⇒ the vendor CLI uses whatever model it is configured for.</summary>
    public string Model => _model ?? string.Empty;
    public CodegenEffort Effort => _effort;
    public IReadOnlyList<string> KnownModels => AiModelCatalog.Offer(ProviderId, _model);

    /// <summary>
    /// The folder the CLI runs in, and it is empty on purpose.
    ///
    /// <para>The child used to inherit the app's own working directory — the build output under a
    /// developer's checkout, or wherever a shortcut started the terminal. An agent CLI reads instruction
    /// files from the folder it starts in and its parents, so every call could quietly carry somebody's
    /// repository guide, and in a checkout with Claude Code hooks, run them. A generation needs no
    /// folder at all.</para>
    /// </summary>
    internal string WorkingDirectory => Path.Combine(_scratch, "workspace");

    private string PackDirectory => Path.Combine(_scratch, "packs");

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

        var packFile = Prepare(request);
        var psi = ProcessFor(exe, stream: true, packFile);
        using var process = new Process { StartInfo = psi };

        if (!process.Start())
        {
            yield return new CodegenEvent.Completed(
                StrategyCodegenResponse.Fail($"Could not start {_adapter.Executable}."));
            yield break;
        }

        // Drained from the start. A redirected stream nobody reads fills its pipe and then blocks the
        // child, and stderr is also the only place a rejected argument is explained.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        await process.StandardInput.WriteAsync(
            FlattenPrompt(request, includeSystemContext: packFile is null).AsMemory(), timeoutCts.Token).ConfigureAwait(false);
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

        // A process that exited non-zero without ever writing a result line did not answer. It used to
        // arrive as an empty reply — a turn with no code, which the pane reads as the model asking a
        // question — when what actually happened was the CLI refusing its arguments.
        if (finalText is null && accumulator.Text.Length == 0 && process.ExitCode != 0)
        {
            yield return new CodegenEvent.Completed(
                StrategyCodegenResponse.Fail(ExitFailure(process.ExitCode, await DrainAsync(stderr).ConfigureAwait(false))));
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

    internal ProcessStartInfo ProcessFor(string exe, bool stream, string? systemPromptFile = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // ALL THREE streams, because a redirected pipe otherwise inherits the Windows console code
            // page. The input half was fixed first and alone: an em dash became CP1252 byte 0x97, which
            // Codex correctly rejects as invalid UTF-8 — a loud failure, so it got fixed.
            //
            // The output half fails SILENTLY, which is why it survived. A model writes an en dash in a
            // label, the UTF-8 bytes E2 80 93 come back decoded as CP437, and the generated source
            // contains the literal string "ΓÇô". It compiles. It clears every rung. It reaches a window
            // and shows "77671.75 ΓÇô 77671.75" to a user — which is where this was finally seen, in a
            // screenshot of a unit that had passed everything.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
            CreateNoWindow = true,
            // Only once it exists: Process.Start refuses a working directory that does not, and a folder
            // that could not be created is not a reason to fail the generation.
            WorkingDirectory = Directory.Exists(WorkingDirectory) ? WorkingDirectory : string.Empty,
        };
        foreach (var arg in _adapter.ArgumentsFor(_model, _effort, stream, _cliProfile, systemPromptFile))
            psi.ArgumentList.Add(arg);
        return psi;
    }

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        var exe = _resolveOnPath(_adapter.Executable);
        if (exe is null)
            return StrategyCodegenResponse.Fail($"{_adapter.Executable} is not on PATH — install it, or pick a keyed provider.");

        var packFile = Prepare(request);
        var prompt = FlattenPrompt(request, includeSystemContext: packFile is null);

        using var process = new Process { StartInfo = ProcessFor(exe, stream: false, packFile) };
        try
        {
            if (!process.Start())
                return StrategyCodegenResponse.Fail($"Could not start {_adapter.Executable}.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            await process.StandardInput.WriteAsync(prompt.AsMemory(), ct).ConfigureAwait(false);
            process.StandardInput.Close();

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

            // The JSON result first, and whatever the exit code: a refusal such as a spent usage window
            // exits non-zero AND explains itself on stdout, and the explanation is the useful half.
            if (_adapter.OneShotFlags is { Count: > 0 } && ReadResult(stdout) is { } result)
            {
                return result.IsError
                    ? StrategyCodegenResponse.Fail($"{_adapter.DisplayName} failed: {Trim(result.Text)}")
                    : Assemble(result.Text, result.Usage);
            }

            if (process.ExitCode != 0)
                return StrategyCodegenResponse.Fail(ExitFailure(process.ExitCode, await stderrTask.ConfigureAwait(false)));

            // No code is a legitimate turn — the agent is asking something back; the session shows it in
            // the chat and waits. (A CLI with no JSON mode reports no token usage.)
            return Assemble(stdout, CodegenUsage.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            KillTree(process);
            return StrategyCodegenResponse.Fail($"{_adapter.DisplayName} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Creates the empty working folder and, for a CLI that can take one, writes the shared pack to its
    /// file. Returns that file, or null when the pack must ride in the prompt instead.
    ///
    /// <para><b>A file that cannot be written is not a failed generation.</b> The pack goes back into the
    /// prompt, where it worked before — uncached, which costs money, but a build that stops because the
    /// temp folder is full costs the whole build.</para>
    /// </summary>
    internal string? Prepare(StrategyCodegenRequest request)
    {
        try
        {
            Directory.CreateDirectory(WorkingDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // ProcessFor then names no working directory; see there.
        }

        if (_adapter.SystemPromptFileFlag is null || string.IsNullOrWhiteSpace(request.SystemContext))
            return null;

        try
        {
            return PackFileFor(request.SystemContext);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The file holding <paramref name="systemContext"/>, named by its content hash.
    ///
    /// <para><b>Named by content, not by run</b>, because what the cache keys on is the bytes. Every call
    /// in a run carries the same pack and so lands on the same file; a different pack can never be served
    /// a stale one; and builders running in parallel that race to write it are writing identical bytes,
    /// so whichever move wins is correct.</para>
    /// </summary>
    internal string PackFileFor(string systemContext)
    {
        Directory.CreateDirectory(PackDirectory);

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(systemContext);
        var path = Path.Combine(PackDirectory, $"{Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant()}.md");

        if (File.Exists(path))
        {
            // Touched, so a run that is still using it is never pruned out from under itself.
            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch (IOException) { }
            return path;
        }

        PruneStalePacks();

        var staging = $"{path}.{Guid.NewGuid():N}.tmp";
        File.WriteAllBytes(staging, bytes);
        try
        {
            File.Move(staging, path);
        }
        catch (IOException) when (File.Exists(path))
        {
            File.Delete(staging); // another call wrote the same bytes first
        }

        return path;
    }

    /// <summary>Packs untouched for a few days belong to runs long finished. A pack is ~150 KB and every
    /// edit to the SDK surface makes a new one, so without this the folder only grows.</summary>
    private void PruneStalePacks()
    {
        try
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(3);
            foreach (var stale in new DirectoryInfo(PackDirectory).EnumerateFiles()
                         .Where(f => f.LastWriteTimeUtc < cutoff))
            {
                try { stale.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Housekeeping. Never worth a generation.
        }
    }

    /// <summary>What a JSON result said: the reply, whether it is a refusal, and what it cost.</summary>
    internal sealed record CliResult(string Text, bool IsError, CodegenUsage Usage);

    /// <summary>
    /// Reads <c>--output-format json</c>: one object with <c>result</c>, <c>is_error</c> and
    /// <c>usage</c>. Null when the output is not that object, so a CLI that printed something else
    /// still reaches the plain-text path rather than an exception.
    /// </summary>
    internal static CliResult? ReadResult(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        // The whole output first; otherwise the last line that is an object, in case anything was
        // printed before it.
        foreach (var candidate in new[] { stdout.Trim() }.Concat(
                     stdout.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith('{')).Reverse()))
        {
            try
            {
                using var document = JsonDocument.Parse(candidate);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("result", out var result))
                    continue;

                var usage = CodegenUsage.None;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                {
                    // The same convention as the stream parser: input is the whole prompt, cache included,
                    // and the cached part is reported beside it.
                    var cached = Int(u, "cache_read_input_tokens");
                    usage = new CodegenUsage(
                        Int(u, "input_tokens") + Int(u, "cache_creation_input_tokens") + cached,
                        Int(u, "output_tokens"),
                        cached);
                }

                return new CliResult(
                    result.ValueKind == JsonValueKind.String ? result.GetString() ?? string.Empty : result.GetRawText(),
                    root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True,
                    usage);
            }
            catch (JsonException)
            {
                // Try the next candidate.
            }
        }

        return null;
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    /// <summary>
    /// The prompt as written to stdin (the CLIs take a single string, not a role array). Each turn is
    /// labelled so the model keeps the thread.
    /// <para><c>includeSystemContext</c> is whether the shared pack leads the prompt: false when it went
    /// to the CLI as its system prompt instead, because sending it twice would bill it twice.</para>
    /// </summary>
    internal static string FlattenPrompt(StrategyCodegenRequest request, bool includeSystemContext = true)
    {
        var sb = new StringBuilder();
        if (includeSystemContext && request.SystemContext is { Length: > 0 } pack)
            sb.Append(pack).AppendLine().AppendLine();

        // The role follows the pack and never joins it: the pack is the part that must be byte-identical
        // on every call for the cache to hold, and the role is what differs between them.
        if (request.RoleInstruction is { Length: > 0 } role)
            sb.AppendLine(role).AppendLine();
        foreach (var m in request.Messages)
            sb.Append(m.Role == CodegenRole.Assistant ? "ASSISTANT: " : "USER: ").AppendLine(m.Content).AppendLine();
        // This paragraph demanded "kernel + ITradingStrategy descriptor + live view-model + code-built
        // view" until 2026-08-25 — the four-file model the visualizer rework retired. It was telling a
        // CLI agent to write three files the host would never load.
        sb.AppendLine(
            "Answer per the output contract above: one ```csharp fenced block per file, each starting with " +
            "a `// file: <Name>.cs` line. Write ONE public class implementing IStrategyKernel (a strategy) " +
            "or IVisualizer (a visualizer), with a public parameterless constructor. No view, no " +
            "view-model, no descriptor, no XAML — the host builds the window chrome, and a body of several "
            + "panels is declared with UnitLayout rather than built as controls. Ask a question instead of " +
            "guessing if the brief is ambiguous about the instrument, timeframe, sizing or risk.");
        return sb.ToString();
    }

    /// <summary>A non-zero exit, explained. An unknown option means the installed CLI predates a flag
    /// this client sends, and "update it" is the whole of the fix.</summary>
    private string ExitFailure(int exitCode, string stderr) =>
        $"{_adapter.DisplayName} exited {exitCode}: {Trim(stderr)}"
        + (stderr.Contains("unknown option", StringComparison.OrdinalIgnoreCase)
            ? $" — the installed {_adapter.Executable} is older than this app expects; update it."
            : string.Empty);

    private static async Task<string> DrainAsync(Task<string> stderr)
    {
        try
        {
            return await stderr.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";

    private static void KillTree(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
    }

    /// <summary>Resolves an executable against PATH (with the platform's executable extensions), so
    /// <see cref="IsAvailable"/> doesn't pay a process launch. Internal: the CLI workspace launcher
    /// answers "which CLIs are installed?" with the same logic, so the two can never disagree.</summary>
    internal static string? ResolveOnPath(string executable)
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
