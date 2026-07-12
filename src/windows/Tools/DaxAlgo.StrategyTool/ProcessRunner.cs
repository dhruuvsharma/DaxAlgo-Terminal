using System.Diagnostics;

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

    /// <summary>PowerShell for the packaging script — pwsh if present, else Windows PowerShell.</summary>
    public static string PowerShell =>
        OperatingSystem.IsWindows() ? "powershell" : "pwsh";
}
