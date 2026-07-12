using System.Diagnostics;
using System.IO;

namespace DaxAlgo.StrategyTool;

/// <summary>Thin subprocess helper — runs a command, streams its output to the console, returns the
/// exit code. All the CLI's non-AI verbs are wrappers over <c>dotnet</c> / the packaging script.</summary>
internal static class ProcessRunner
{
    public static int Run(string fileName, string arguments, string? workingDir = null)
    {
        Console.WriteLine($"> {fileName} {arguments}");
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        using var process = Process.Start(psi);
        if (process is null) { Console.Error.WriteLine($"Could not start {fileName}."); return 1; }
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>Runs a command and captures stdout+stderr (for the AI loop's error parsing).</summary>
    public static (int ExitCode, string Output) Capture(string fileName, string arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDir ?? Environment.CurrentDirectory,
        };
        using var process = Process.Start(psi);
        if (process is null) return (1, $"Could not start {fileName}.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    /// <summary>PowerShell for the packaging script. Prefers <c>pwsh</c> (PowerShell 7+) — it's on PATH
    /// on CI runners and cross-platform — and only falls back to Windows PowerShell 5.1 when pwsh isn't
    /// found. (A minimal 5.1 host on some runners can't auto-load <c>Get-FileHash</c>, which the packaging
    /// script needs.)</summary>
    public static string PowerShell =>
        ResolveOnPath("pwsh") ?? (OperatingSystem.IsWindows() ? "powershell" : "pwsh");

    private static string? ResolveOnPath(string exe)
    {
        var paths = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", string.Empty } : [string.Empty];
        foreach (var dir in paths)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir, exe + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return null;
    }
}
