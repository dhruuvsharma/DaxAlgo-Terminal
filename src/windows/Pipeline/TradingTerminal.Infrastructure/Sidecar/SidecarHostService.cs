using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Hosting;
using TradingTerminal.Infrastructure.Notifications;

namespace TradingTerminal.Infrastructure.Sidecar;

/// <summary>
/// Manages the local Python sidecar (<c>daxalgo-ml</c>) as a child process so the user never launches it
/// by hand. On app start it auto-launches the sidecar when <see cref="SidecarOptions.AutoStart"/> is on
/// AND a feature that needs it is enabled (AI Market Analyst or Paper Lab research); it waits for the
/// sidecar's <c>/healthz</c>, drains its output to the log, and kills it on app exit (a Windows Job
/// Object guarantees it can't be orphaned). It also exposes <see cref="ISidecarController"/> so the login
/// screen / settings can start it on demand.
///
/// <para>Launch resolution is exe-first: an explicit path, then a bundled <c>daxalgo-ml.exe</c> next to
/// the app or under <c>tools/python-ml/{dist,bin}</c>, then a dev fallback of <c>python -m
/// daxalgo_ml.app</c> using the repo venv. If nothing is found it logs and no-ops — the app still runs;
/// the AI/research clients just report unavailable, exactly as before.</para>
/// </summary>
internal sealed class SidecarHostService : IHostedService, ISidecarController, IDisposable
{
    private readonly IOptionsMonitor<SidecarOptions> _sidecar;
    private readonly IOptionsMonitor<ResearchReproOptions> _research;
    private readonly IOptionsMonitor<NotificationsOptions> _notifications;
    private readonly ILogger<SidecarHostService> _logger;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JobObjectProcessGuard _guard = new();
    private Process? _process;

    public SidecarHostService(
        IOptionsMonitor<SidecarOptions> sidecar,
        IOptionsMonitor<ResearchReproOptions> research,
        IOptionsMonitor<NotificationsOptions> notifications,
        ILogger<SidecarHostService> logger)
    {
        _sidecar = sidecar;
        _research = research;
        _notifications = notifications;
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    private int Port => _sidecar.CurrentValue.Port > 0 ? _sidecar.CurrentValue.Port : 8765;
    private string HealthzUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/healthz";

    private bool FeatureNeedsSidecar =>
        _research.CurrentValue.Enabled || _notifications.CurrentValue.AiAnalyst.Enabled;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_sidecar.CurrentValue.AutoStart && FeatureNeedsSidecar)
            // Fire-and-forget — don't block app startup on the sidecar warming up.
            _ = Task.Run(() => EnsureRunningAsync(CancellationToken.None));
        else
            _logger.LogDebug("Sidecar auto-start skipped (AutoStart={Auto}, feature enabled={Feat}).",
                _sidecar.CurrentValue.AutoStart, FeatureNeedsSidecar);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        KillProcess();
        return Task.CompletedTask;
    }

    public async Task<bool> EnsureRunningAsync(CancellationToken ct = default)
    {
        // Already up (ours or an externally-started one)? Don't spawn a second.
        if (await IsReachableAsync(ct).ConfigureAwait(false))
        {
            IsRunning = true;
            return true;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (await IsReachableAsync(ct).ConfigureAwait(false))
            {
                IsRunning = true;
                return true;
            }

            if (_process is { HasExited: false })
            {
                // A launch is already in flight from a previous call — just wait for health.
                IsRunning = await WaitForHealthzAsync(ct).ConfigureAwait(false);
                return IsRunning;
            }

            var launch = ResolveLaunch(_sidecar.CurrentValue, Port);
            if (launch is null)
            {
                _logger.LogWarning(
                    "Sidecar not launched — no daxalgo-ml.exe found and no Python/{0} dev install located. " +
                    "Set Sidecar:ExecutablePath, or build the exe (pyinstaller daxalgo_ml.spec).",
                    "tools/python-ml");
                return false;
            }

            var (fileName, args, workDir) = launch.Value;
            _logger.LogInformation("Starting sidecar: {File} {Args} (port {Port})",
                fileName, string.Join(' ', args), Port);

            if (!TryStart(fileName, args, workDir))
                return false;

            IsRunning = await WaitForHealthzAsync(ct).ConfigureAwait(false);
            if (IsRunning)
                _logger.LogInformation("Sidecar is ready on {Url}.", HealthzUrl);
            else
                _logger.LogWarning("Sidecar started but {Url} didn't answer within {Sec}s.",
                    HealthzUrl, _sidecar.CurrentValue.StartupTimeoutSeconds);
            return IsRunning;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sidecar launch failed.");
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryStart(string fileName, IReadOnlyList<string> args, string? workDir)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = workDir ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["DAXALGO_ML_PORT"] = Port.ToString(CultureInfo.InvariantCulture);

            var process = Process.Start(psi);
            if (process is null) return false;

            _process = process;
            _guard.TryAssign(process); // dies with the app even on a crash
            process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) _logger.LogDebug("[sidecar] {Line}", e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) _logger.LogDebug("[sidecar] {Line}", e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start sidecar process {File}.", fileName);
            return false;
        }
    }

    private async Task<bool> WaitForHealthzAsync(CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(2, _sidecar.CurrentValue.StartupTimeoutSeconds));
        while (DateTime.UtcNow < deadline)
        {
            if (_process is { HasExited: true })
            {
                _logger.LogWarning("Sidecar process exited early (code {Code}).", _process.ExitCode);
                return false;
            }
            if (await IsReachableAsync(ct).ConfigureAwait(false)) return true;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private async Task<bool> IsReachableAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync(HealthzUrl, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    // ── Launch resolution (exe-first, dev-python fallback) ───────────────────────────────────────

    private (string FileName, List<string> Args, string? WorkDir)? ResolveLaunch(SidecarOptions o, int port)
    {
        var portArg = port.ToString(CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(o.ExecutablePath) && File.Exists(o.ExecutablePath))
            return (o.ExecutablePath, new List<string> { "--port", portArg }, Path.GetDirectoryName(o.ExecutablePath));

        if (FindFrozenExe() is { } exe)
            return (exe, new List<string> { "--port", portArg }, Path.GetDirectoryName(exe));

        if (FindToolsPythonDir() is { } toolsDir && ResolvePython(o, toolsDir) is { } python)
            return (python, new List<string> { "-m", "daxalgo_ml.app", "--port", portArg }, toolsDir);

        return null;
    }

    private static string? FindFrozenExe()
    {
        var baseDir = AppContext.BaseDirectory;
        var direct = new[]
        {
            Path.Combine(baseDir, "daxalgo-ml.exe"),
            Path.Combine(baseDir, "sidecar", "daxalgo-ml.exe"),
        };
        foreach (var p in direct)
            if (File.Exists(p)) return p;

        // Dev: walk up for the repo's tools/python-ml/{dist,bin}/daxalgo-ml.exe.
        foreach (var rel in new[] { "tools/python-ml/dist/daxalgo-ml.exe", "tools/python-ml/bin/daxalgo-ml.exe" })
            if (FindUpwards(baseDir, rel) is { } hit) return hit;

        return null;
    }

    private static string? FindToolsPythonDir()
    {
        var marker = FindUpwards(AppContext.BaseDirectory, "tools/python-ml/daxalgo_ml/app.py");
        if (marker is null) return null;
        // marker = …/tools/python-ml/daxalgo_ml/app.py → return …/tools/python-ml
        return Path.GetDirectoryName(Path.GetDirectoryName(marker));
    }

    private static string? ResolvePython(SidecarOptions o, string toolsDir)
    {
        if (!string.IsNullOrWhiteSpace(o.PythonPath) && File.Exists(o.PythonPath)) return o.PythonPath;
        var venv = Path.Combine(toolsDir, ".venv", "Scripts", "python.exe");
        if (File.Exists(venv)) return venv;
        return "python"; // rely on PATH; if absent, Process.Start throws and we degrade gracefully
    }

    /// <summary>Walks up from <paramref name="startDir"/> looking for a relative path, returning its
    /// absolute form when found.</summary>
    private static string? FindUpwards(string startDir, string relative)
    {
        var dir = new DirectoryInfo(startDir);
        var relParts = relative.Replace('\\', '/');
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relParts.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    private void KillProcess()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _logger.LogInformation("Stopped the managed sidecar process.");
            }
        }
        catch { /* best effort — the job object also enforces teardown */ }
        finally
        {
            _process?.Dispose();
            _process = null;
            IsRunning = false;
        }
    }

    public void Dispose()
    {
        KillProcess();
        _guard.Dispose();
        _gate.Dispose();
    }
}
