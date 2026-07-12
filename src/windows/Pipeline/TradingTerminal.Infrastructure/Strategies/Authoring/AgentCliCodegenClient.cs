using System.Diagnostics;
using System.IO;
using System.Text;
using TradingTerminal.Core.Strategies.Authoring;

namespace TradingTerminal.Infrastructure.Strategies.Authoring;

/// <summary>Per-CLI details, isolated so one vendor's output-format drift doesn't touch the others.</summary>
public sealed record AgentCliAdapter(
    string ProviderId,
    string DisplayName,
    string Executable,
    IReadOnlyList<string> Arguments)
{
    /// <summary>Claude Code in print mode: <c>claude -p</c> reads the prompt from stdin and prints the
    /// reply to stdout. The user's subscription/key lives in Claude Code itself — never seen here.</summary>
    public static AgentCliAdapter ClaudeCode { get; } =
        new("claude-cli", "Claude Code (installed CLI)", "claude", ["-p"]);

    /// <summary>OpenAI Codex CLI: <c>codex exec</c> runs a one-shot prompt from stdin, ChatGPT sign-in
    /// handled by the CLI.</summary>
    public static AgentCliAdapter Codex { get; } =
        new("codex-cli", "Codex (installed CLI)", "codex", ["exec", "-"]);

    public static IReadOnlyList<AgentCliAdapter> All { get; } = [ClaudeCode, Codex];
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

    public AgentCliCodegenClient(AgentCliAdapter adapter, Func<string, string?>? resolveOnPath = null, TimeSpan? timeout = null)
    {
        _adapter = adapter;
        _resolveOnPath = resolveOnPath ?? ResolveOnPath;
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public string ProviderId => _adapter.ProviderId;
    public string DisplayName => _adapter.DisplayName;
    public bool IsAvailable => _resolveOnPath(_adapter.Executable) is not null;

    public async Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
    {
        var exe = _resolveOnPath(_adapter.Executable);
        if (exe is null)
            return StrategyCodegenResponse.Fail($"{_adapter.Executable} is not on PATH — install it, or pick a keyed provider.");

        var prompt = FlattenPrompt(request);

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in _adapter.Arguments) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
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
                return StrategyCodegenResponse.Fail($"{_adapter.DisplayName} timed out after {_timeout.TotalSeconds:0}s.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var stderr = await stderrTask.ConfigureAwait(false);
                return StrategyCodegenResponse.Fail($"{_adapter.DisplayName} exited {process.ExitCode}: {Trim(stderr)}");
            }

            var code = CodegenCodeExtractor.Extract(stdout);
            return string.IsNullOrWhiteSpace(code)
                ? StrategyCodegenResponse.Fail($"{_adapter.DisplayName} returned no code.")
                : StrategyCodegenResponse.Ok(code, stdout);
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
        sb.AppendLine("Return only the single-file C# kernel, in a ```csharp fenced block.");
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
