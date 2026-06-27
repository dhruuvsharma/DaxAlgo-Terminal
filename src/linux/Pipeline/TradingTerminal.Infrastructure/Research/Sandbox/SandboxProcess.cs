using System.Diagnostics;
using System.Text;

namespace TradingTerminal.Infrastructure.Research.Sandbox;

/// <summary>Outcome of a sandbox subprocess run: exit code (null = could not start / killed), the
/// combined stdout+stderr transcript, and a flag for "executable not found".</summary>
internal sealed record SandboxProcessOutcome(int? ExitCode, string Output, bool NotFound)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Minimal async process runner for the container/git CLIs. Extracted from the
/// <c>LeanProcessRunner</c> pattern: redirects stdout/stderr, streams each line to an optional sink,
/// accumulates the transcript, and — critically for untrusted execution — kills the ENTIRE process
/// tree on cancellation or wall-clock timeout. It only ever spawns the trusted CLI (docker / git),
/// never the untrusted paper entrypoint directly.
///
/// <para>Arguments are passed as a token list via <see cref="ProcessStartInfo.ArgumentList"/> — one
/// token per entry, never a single manually-quoted string. This is a security control: a single
/// <c>Arguments</c> string requires hand-quoting that the Win32 arg parser mangles for Windows paths
/// (drive letters, spaces) and that opens an argument-injection surface. The list API quotes each
/// token correctly per platform and removes the injection vector entirely.</para>
/// </summary>
internal static class SandboxProcess
{
    public static async Task<SandboxProcessOutcome> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? workingDirectory,
        Action<string>? onLine,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? Environment.CurrentDirectory
                : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // One token per entry — no manual quoting, no injection surface (see class remarks).
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        var sb = new StringBuilder();
        void Sink(string? line)
        {
            if (line is null) return;
            lock (sb) sb.AppendLine(line);
            onLine?.Invoke(line);
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => Sink(e.Data);
        process.ErrorDataReceived += (_, e) => Sink(e.Data);

        try
        {
            if (!process.Start())
                return new SandboxProcessOutcome(null, "Failed to start process.", NotFound: false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ERROR_FILE_NOT_FOUND (2) → the CLI isn't installed / not on PATH.
            return new SandboxProcessOutcome(null, ex.Message, NotFound: ex.NativeErrorCode == 2);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout > TimeSpan.Zero)
            timeoutCts.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree so a forked/child process from untrusted code can't outlive the run.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            var killedFor = ct.IsCancellationRequested
                ? "cancelled"
                : $"timed out after {timeout.TotalSeconds:0}s";
            Sink($"[run {killedFor}]");
            string outp;
            lock (sb) outp = sb.ToString();
            return new SandboxProcessOutcome(null, outp, NotFound: false);
        }

        string output;
        lock (sb) output = sb.ToString();
        return new SandboxProcessOutcome(process.ExitCode, output, NotFound: false);
    }
}
