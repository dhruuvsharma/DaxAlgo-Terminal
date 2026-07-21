using System.IO;

namespace TradingTerminal.Infrastructure.Backtest.Worker;

internal sealed record BacktestWorkerLaunch(
    string FileName,
    IReadOnlyList<string> PrefixArguments,
    string ResolvedWorkerPath);

internal static class BacktestWorkerExecutableResolver
{
    private const string WorkerBaseName = "TradingTerminal.Backtest.Worker";
    private const string EnvironmentVariable = "DAXALGO_BACKTEST_WORKER_PATH";

    public static bool TryResolve(
        BacktestWorkerOptions options,
        out BacktestWorkerLaunch? launch,
        out string? error) =>
        TryResolve(
            options,
            AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable(EnvironmentVariable),
            out launch,
            out error);

    internal static bool TryResolve(
        BacktestWorkerOptions options,
        string baseDirectory,
        string? environmentPath,
        out BacktestWorkerLaunch? launch,
        out string? error)
    {
        var candidates = new List<string>();
        AddCandidate(candidates, options.WorkerExecutablePath);
        AddCandidate(candidates, environmentPath);

        var stagedDirectory = Path.Combine(baseDirectory, "backtest-worker");
        AddDefaultCandidates(candidates, stagedDirectory);
        AddDefaultCandidates(candidates, baseDirectory);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            string full;
            try { full = Path.GetFullPath(candidate); }
            catch { continue; }
            if (!seen.Add(full) || !File.Exists(full)) continue;

            if (string.Equals(Path.GetExtension(full), ".dll", StringComparison.OrdinalIgnoreCase))
            {
                var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
                launch = new BacktestWorkerLaunch(
                    string.IsNullOrWhiteSpace(dotnetHost) ? "dotnet" : dotnetHost,
                    [full],
                    full);
            }
            else
            {
                launch = new BacktestWorkerLaunch(full, [], full);
            }

            error = null;
            return true;
        }

        launch = null;
        error =
            $"Backtest worker was not found. Set BacktestWorkerOptions.WorkerExecutablePath or {EnvironmentVariable}; " +
            $"the default staged location is '{Path.Combine(stagedDirectory, WorkerBaseName + ".exe")}'.";
        return false;
    }

    private static void AddDefaultCandidates(List<string> candidates, string directory)
    {
        if (OperatingSystem.IsWindows()) AddCandidate(candidates, Path.Combine(directory, WorkerBaseName + ".exe"));
        AddCandidate(candidates, Path.Combine(directory, WorkerBaseName));
        AddCandidate(candidates, Path.Combine(directory, WorkerBaseName + ".dll"));
    }

    private static void AddCandidate(List<string> candidates, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate)) candidates.Add(candidate);
    }
}
