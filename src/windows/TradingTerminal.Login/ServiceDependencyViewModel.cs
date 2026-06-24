using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;

namespace TradingTerminal.App.Login;

/// <summary>The live state of an external dependency the terminal talks to but does not launch itself.</summary>
public enum ServiceState
{
    /// <summary>Not probed yet (or this row is informational only).</summary>
    Unknown,

    /// <summary>A status check is in flight.</summary>
    Checking,

    /// <summary>Reachable / running.</summary>
    Running,

    /// <summary>Not reachable — needs to be started.</summary>
    Stopped,
}

/// <summary>
/// One row in the login screen's "Services &amp; external dependencies" panel. Describes an external
/// process the terminal relies on but never starts itself (the Python sidecar, Docker, broker desktop
/// apps, a local LLM), with a one-line purpose, how to launch it, an optional copy-paste start command,
/// and — where it's cheap and safe — a live reachability probe so the user can see at a glance what's up.
/// </summary>
public sealed partial class ServiceDependencyViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<bool>>? _probe;
    private readonly Func<CancellationToken, Task>? _startAction;

    public ServiceDependencyViewModel(
        string name,
        string purpose,
        string requirement,
        string howTo,
        string? startCommand = null,
        Func<CancellationToken, Task<bool>>? probe = null,
        Func<CancellationToken, Task>? startAction = null,
        string? startActionLabel = null)
    {
        Name = name;
        Purpose = purpose;
        Requirement = requirement;
        HowTo = howTo;
        StartCommand = startCommand;
        _probe = probe;
        _startAction = startAction;
        StartActionLabel = startActionLabel ?? "Start now";
        StatusText = probe is null ? "Manual — see below" : "Not checked";
    }

    public string Name { get; }
    public string Purpose { get; }
    public string Requirement { get; }
    public string HowTo { get; }
    public string? StartCommand { get; }

    public bool HasStartCommand => !string.IsNullOrWhiteSpace(StartCommand);
    public bool CanProbe => _probe is not null;

    public string StartActionLabel { get; }
    public bool HasStartAction => _startAction is not null;

    /// <summary>Runs the one-click start action (if any), then re-probes status. Never throws.</summary>
    public async Task RunStartAsync(CancellationToken ct = default)
    {
        if (_startAction is null) return;
        State = ServiceState.Checking;
        StatusText = "Starting…";
        try { await _startAction(ct).ConfigureAwait(true); }
        catch { /* surfaced via the re-check below */ }
        await CheckAsync(ct).ConfigureAwait(true);
    }

    [ObservableProperty] private ServiceState _state = ServiceState.Unknown;
    [ObservableProperty] private string _statusText;

    /// <summary>Runs the reachability probe (if any) and folds the result into <see cref="State"/>.
    /// Never throws — a failed probe just reports <see cref="ServiceState.Stopped"/>.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (_probe is null) return;

        State = ServiceState.Checking;
        StatusText = "Checking…";
        try
        {
            var ok = await _probe(ct).ConfigureAwait(true);
            State = ok ? ServiceState.Running : ServiceState.Stopped;
            StatusText = ok ? "Running" : "Not running";
        }
        catch
        {
            State = ServiceState.Stopped;
            StatusText = "Not running";
        }
    }

    // ── Reusable probes (defensive: short timeouts, never throw) ──────────────────────────────────

    /// <summary>True when an HTTP GET to <paramref name="url"/> returns a success status within ~2s.</summary>
    public static async Task<bool> HttpOkAsync(string url, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when a TCP connection to any of <paramref name="ports"/> opens within ~1.5s.</summary>
    public static async Task<bool> TcpOpenAsync(string host, int[] ports, CancellationToken ct)
    {
        foreach (var port in ports)
        {
            try
            {
                using var client = new TcpClient();
                var connect = client.ConnectAsync(host, port);
                var finished = await Task.WhenAny(connect, Task.Delay(1500, ct)).ConfigureAwait(false);
                if (finished == connect && client.Connected) return true;
            }
            catch
            {
                // try the next port
            }
        }
        return false;
    }

    /// <summary>True when <c>docker version</c> reports a running server engine within ~3s.</summary>
    public static Task<bool> DockerRunningAsync(CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;

            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout);
        }
        catch
        {
            return false; // docker not on PATH / not installed
        }
    }, ct);
}
