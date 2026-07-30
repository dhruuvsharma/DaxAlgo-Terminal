using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
/// One external dependency the terminal relies on (Docker for the Paper Lab sandbox, a broker's desktop
/// app), with a one-line purpose, how to launch it, optional copy-paste and one-click start actions, and —
/// where it's cheap and safe — a live reachability probe so the user can see at a glance what's up.
///
/// <para>Used in two places on the login screen: the shared "Services &amp; dependencies" panel
/// (<see cref="LoginViewModel.Services"/>) and, for broker-specific prerequisites like TWS or
/// NinjaTrader 8, inside that broker's own expander via
/// <see cref="BrokerLoginFormBase.Prerequisite"/> — which is why a row can re-probe itself through
/// <see cref="RecheckCommand"/> without going through the panel's sweep.</para>
/// </summary>
public sealed partial class ServiceDependencyViewModel : ObservableObject
{
    public const string QuestDbDockerRunCommand =
        "docker run -d --name questdb -p 9000:9000 -p 8812:8812 -p 9009:9009 questdb/questdb";

    private static readonly string[] QuestDbDockerRunArguments =
    {
        "run", "-d", "--name", "questdb",
        "-p", "9000:9000",
        "-p", "8812:8812",
        "-p", "9009:9009",
        "questdb/questdb",
    };

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

    /// <summary>Self-service re-probe, for rows rendered outside the panel's "Re-check" sweep
    /// (a broker expander's prerequisite block). No-op when this row has no probe.</summary>
    [RelayCommand]
    private Task Recheck() => CheckAsync();

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
            if (ct.IsCancellationRequested) return false;

            try
            {
                using var client = new TcpClient();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
                await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
                if (client.Connected) return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                // Per-port timeout: try the next candidate.
            }
            catch (SocketException)
            {
                // Connection refused/unreachable: try the next candidate.
            }
            catch
            {
                // Defensive probe contract: an unexpected transport failure still means unavailable.
            }
        }
        return false;
    }

    /// <summary>True when at least one process named <paramref name="processName"/> (no extension) is
    /// running. Cheap local check for broker desktop apps that expose no socket to probe.</summary>
    public static Task<bool> ProcessRunningAsync(string processName, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var found = Process.GetProcessesByName(processName);
            foreach (var p in found) p.Dispose();
            return found.Length > 0;
        }
        catch
        {
            return false; // access denied / process list unavailable — report unavailable, never throw
        }
    }, ct);

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

    /// <summary>True when QuestDB's HTTP console or PostgreSQL wire endpoint is reachable locally.</summary>
    public static async Task<bool> QuestDbRunningAsync(CancellationToken ct)
    {
        if (await HttpOkAsync("http://127.0.0.1:9000", ct).ConfigureAwait(false))
            return true;

        return await TcpOpenAsync("127.0.0.1", new[] { 8812 }, ct).ConfigureAwait(false);
    }

    /// <summary>Starts the existing QuestDB container, creating it with the standard ports when absent.</summary>
    public static async Task StartQuestDbAsync(CancellationToken ct)
    {
        var start = await RunDockerCommandAsync(new[] { "start", "questdb" }, ct).ConfigureAwait(false);
        if (start.ExitCode == 0)
            return;

        var startOutput = start.StandardOutput + "\n" + start.StandardError;
        if (!startOutput.Contains("No such container", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Docker could not start the QuestDB container.");

        var run = await RunDockerCommandAsync(QuestDbDockerRunArguments, ct).ConfigureAwait(false);
        if (run.ExitCode != 0)
            throw new InvalidOperationException("Docker could not create the QuestDB container.");
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> RunDockerCommandAsync(
        string[] arguments,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Docker could not be started.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        return (
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false));
    }
}
