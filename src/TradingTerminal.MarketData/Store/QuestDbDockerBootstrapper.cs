using System.Diagnostics;
using System.IO;
using System.Threading;
using Microsoft.Extensions.Logging;
using Npgsql;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.Infrastructure.MarketData.Store;

/// <summary>
/// Best-effort helpers for bringing the QuestDB Docker container up. Unlike the Postgres path, the
/// QuestDB backend has <b>no SQLite fallback for ticks</b> — so rather than silently disabling L1/L2
/// persistence, we try to start the container the repo's <c>docker-compose.yml</c> defines (single
/// source of truth for ports/env/volume).
///
/// <para>These helpers back the asynchronous QuestDB warm-up — the login-screen auto-start and the
/// manual <c>File → Start QuestDB</c> command, both in <c>QuestDbDockerService</c> (which can also
/// launch Docker Desktop itself). They're never called on the UI thread synchronously; the store
/// factory only probes reachability. Everything is defensive: anything missing is logged and skipped
/// so the app always launches.</para>
/// </summary>
internal static class QuestDbDockerBootstrapper
{
    public static TimeSpan StartupTimeout(MarketDataStoreOptions opts) =>
        TimeSpan.FromSeconds(Math.Max(5, opts.DockerStartupTimeoutSeconds));

    /// <summary>True when the <c>docker</c> CLI is on PATH.</summary>
    public static bool DockerCliPresent() => TryRunDocker("--version", TimeSpan.FromSeconds(15), out _, log: null);

    /// <summary>True when the Docker daemon answers — <c>docker info</c> fails fast if it's down.</summary>
    public static bool DockerDaemonReady() => TryRunDocker("info", TimeSpan.FromSeconds(20), out _, log: null);

    /// <summary>Best-effort PG-wire probe — opens and closes a connection to decide availability.</summary>
    public static bool IsReachable(string conn)
    {
        try
        {
            using var cn = new NpgsqlConnection(conn);
            cn.Open();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Brings up the container: prefer <c>docker compose up -d</c> against the repo's compose
    /// file (source of truth), else <c>docker start</c> the named container. Logs the docker error on
    /// failure so the user can see why (e.g. daemon not running).</summary>
    public static bool TryStartContainer(MarketDataStoreOptions opts, ILogger log)
    {
        var composeFile = FindComposeFile();
        if (composeFile is not null)
        {
            if (TryRunDocker($"compose -f \"{composeFile}\" up -d {opts.DockerComposeService}",
                    TimeSpan.FromSeconds(180), out var composeOut, log: null))
                return true;
            if (!string.IsNullOrWhiteSpace(composeOut))
                log.LogWarning("docker compose up failed: {Error}", composeOut.Trim());
        }

        // Fallback for published builds shipped without the compose file: start an existing container.
        return TryRunDocker($"start {opts.DockerContainerName}", TimeSpan.FromSeconds(60), out _, log: null);
    }

    /// <summary>Launches Docker Desktop (configured path or a known install location). Returns false if
    /// no executable could be found/started.</summary>
    public static bool TryLaunchDockerDesktop(MarketDataStoreOptions opts, ILogger log)
    {
        foreach (var path in DockerDesktopCandidates(opts))
        {
            if (!File.Exists(path)) continue;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
                log.LogInformation("Launched Docker Desktop ({Path}). Waiting for the engine to come up…", path);
                return true;
            }
            catch (Exception ex)
            {
                log.LogDebug(ex, "Failed to launch Docker Desktop at {Path}", path);
            }
        }
        return false;
    }

    /// <summary>Polls <c>docker info</c> until the daemon answers or the timeout elapses.</summary>
    public static bool WaitForDaemon(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (DockerDaemonReady()) return true;
            Thread.Sleep(2000);
        }
        return false;
    }

    /// <summary>Polls the QuestDB PG-wire port until it accepts a connection or the timeout elapses.</summary>
    public static bool WaitUntilReachable(string conn, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            if (IsReachable(conn)) return true;
            Thread.Sleep(1000);
        }
        return false;
    }

    private static IEnumerable<string> DockerDesktopCandidates(MarketDataStoreOptions opts)
    {
        if (!string.IsNullOrWhiteSpace(opts.DockerDesktopPath))
            yield return opts.DockerDesktopPath;

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(programFiles, "Docker", "Docker", "Docker Desktop.exe");
        yield return Path.Combine(programFilesX86, "Docker", "Docker", "Docker Desktop.exe");
        yield return Path.Combine(localAppData, "Docker", "Docker Desktop.exe");
    }

    /// <summary>Walk up from the app's base directory to locate the repo's docker-compose.yml.</summary>
    private static string? FindComposeFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docker-compose.yml");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Runs <c>docker &lt;arguments&gt;</c>, returns true on exit code 0. <paramref name="output"/>
    /// gets stdout (or stderr when stdout is empty). Never throws.</summary>
    private static bool TryRunDocker(string arguments, TimeSpan timeout, out string output, ILogger? log)
    {
        output = string.Empty;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Drain both pipes asynchronously so a full stderr buffer can't deadlock a blocked stdout read.
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)timeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* best-effort */ }
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            if (proc.ExitCode != 0)
            {
                log?.LogDebug("docker {Args} exited {Code}: {Err}", arguments, proc.ExitCode, stderr.Trim());
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            log?.LogDebug(ex, "docker {Args} failed to launch", arguments);
            return false;
        }
    }
}
