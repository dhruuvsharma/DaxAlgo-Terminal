using System.Diagnostics;
using System.Text;

namespace TradingTerminal.QuantConnect;

/// <summary>Outcome of a subprocess run: exit code (null = could not start / killed), the combined
/// stdout+stderr text, and a flag for "executable not found".</summary>
internal sealed record ProcessOutcome(int? ExitCode, string Output, bool NotFound)
{
    public bool Success => ExitCode == 0;
}

/// <summary>
/// Minimal async process runner for the <c>lean</c> CLI: redirects stdout/stderr, streams each line to
/// an optional callback as it arrives, accumulates the full transcript, and kills the whole process
/// tree on cancellation or timeout. No LEAN-specific logic lives here.
/// </summary>
internal static class LeanProcessRunner
{
    public static async Task<ProcessOutcome> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        Action<string>? onLine,
        int timeoutSeconds,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? Environment.CurrentDirectory : workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

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
                return new ProcessOutcome(null, "Failed to start process.", NotFound: false);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // ERROR_FILE_NOT_FOUND (2) → the CLI isn't installed / not on PATH.
            return new ProcessOutcome(null, ex.Message, NotFound: ex.NativeErrorCode == 2);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeoutSeconds > 0)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            var killedFor = ct.IsCancellationRequested ? "cancelled" : $"timed out after {timeoutSeconds}s";
            Sink($"[run {killedFor}]");
            string outp;
            lock (sb) outp = sb.ToString();
            return new ProcessOutcome(null, outp, NotFound: false);
        }

        string output;
        lock (sb) output = sb.ToString();
        return new ProcessOutcome(process.ExitCode, output, NotFound: false);
    }
}
