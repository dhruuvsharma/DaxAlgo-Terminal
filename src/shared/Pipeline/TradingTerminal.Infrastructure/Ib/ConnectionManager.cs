using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Per-broker connect / reconnect loop with exponential backoff (1s → cap). Each broker the
/// user signs into has its own <see cref="ConnectionManager"/> owned by the
/// <see cref="IBrokerSelector"/> — there is no shared singleton anymore.
///
/// The lifecycle is start-stop, not connect-once: <see cref="StartAsync"/> spins up the loop
/// and returns immediately; the loop drives the underlying <see cref="IBrokerClient"/> through
/// Connecting → Connected → (drop) → Reconnecting → Connected until the user calls
/// <see cref="StopAsync"/>. State is surfaced through <see cref="ConnectionState"/> which
/// replays the latest value to new subscribers.
///
/// Lives under <c>Infrastructure/Ib/</c> for historical reasons; the implementation is fully
/// broker-neutral.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(30);

    // How many times to attempt the *initial* connect before giving up. A login that has never
    // succeeded is usually permanently broken (missing OAuth token / API key, unreachable or
    // geo-blocked host) — retrying forever just hammers the host and floods the Activity Log with
    // identical warnings. After this many failures we surface Failed and stop. A drop *after* a
    // successful connection still reconnects indefinitely (transient-network resilience).
    private const int MaxInitialConnectAttempts = 3;

    private readonly IBrokerClient _client;
    private readonly ILogger _logger;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _stateSub;
    private Task? _loop;
    private bool _userRequestedDisconnect;
    private bool _haveConnectedAtLeastOnce;
    private int _initialAttempts;
    private TimeSpan _initialBackoff = DefaultInitialBackoff;
    private TimeSpan _maxBackoff = DefaultMaxBackoff;

    public ConnectionManager(IBrokerClient client, ILogger<ConnectionManager> logger)
    {
        _client = client;
        _logger = logger;
        _stateSub = _client.ConnectionState.Subscribe(OnClientStateChanged);
    }

    /// <summary>Test/internal hook for supplying a non-generic logger (e.g. one created per broker via <c>ILoggerFactory</c>).</summary>
    internal ConnectionManager(IBrokerClient client, ILogger logger)
    {
        _client = client;
        _logger = logger;
        _stateSub = _client.ConnectionState.Subscribe(OnClientStateChanged);
    }

    public BrokerKind Broker => _client.Kind;

    /// <summary>Optional: caller-supplied backoff override. Defaults are 1s → 30s.</summary>
    public void ConfigureBackoff(TimeSpan initial, TimeSpan max)
    {
        if (initial <= TimeSpan.Zero) initial = DefaultInitialBackoff;
        if (max < initial) max = initial;
        _initialBackoff = initial;
        _maxBackoff = max;
    }

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public ConnectionState CurrentState => _state.Value;

    public async Task StartAsync(CancellationToken ct = default)
    {
        _userRequestedDisconnect = false;

        // Idempotent: if a reconnect loop is already running, leave it alone. A fresh Start (e.g.
        // the user hitting Reconnect after a give-up) resets the attempt budget so we try again.
        if (_loop is null || _loop.IsCompleted)
        {
            _initialAttempts = 0;
            _loop = Task.Run(() => RunReconnectLoopAsync(_cts.Token));
        }

        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _userRequestedDisconnect = true;
        try { await _client.DisconnectAsync(ct); } catch { /* swallow */ }
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    private void OnClientStateChanged(ConnectionState s)
    {
        _state.OnNext(s);
        if (s is Core.Domain.ConnectionState.Connected) _haveConnectedAtLeastOnce = true;
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        var initial = _initialBackoff;
        var max = _maxBackoff;
        var delay = initial;

        while (!ct.IsCancellationRequested && !_userRequestedDisconnect)
        {
            string? failureReason = null;
            try
            {
                _state.OnNext(_haveConnectedAtLeastOnce
                    ? Core.Domain.ConnectionState.Reconnecting
                    : Core.Domain.ConnectionState.Connecting);

                await _client.ConnectAsync(ct);

                // ConnectAsync returning does NOT guarantee a live connection — several clients
                // report a bad login by setting Failed and returning normally (cTrader/LSE with
                // missing credentials, a keyless feed that can't reach its host). Only treat an
                // actual Connected state as success; otherwise it's a failed attempt (don't log a
                // false "Connected" followed immediately by "dropped").
                if (_state.Value == Core.Domain.ConnectionState.Connected)
                {
                    _logger.LogInformation("Connected to {Broker}", _client.Kind);
                    delay = initial;            // reset backoff on a real success
                    _initialAttempts = 0;

                    // Wait until the client transitions away from Connected.
                    var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    using var sub = _client.ConnectionState
                        .Where(s => s is Core.Domain.ConnectionState.Disconnected or Core.Domain.ConnectionState.Failed)
                        .Subscribe(_ => dropped.TrySetResult());

                    await dropped.Task.WaitAsync(ct);
                    if (_userRequestedDisconnect) break;
                    _logger.LogWarning("{Broker} connection dropped — will retry", _client.Kind);
                }
                else
                {
                    failureReason = $"reported {_state.Value}";
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failureReason = ex.Message;
            }

            if (_userRequestedDisconnect || ct.IsCancellationRequested) break;

            // An initial connect that has never succeeded is treated as likely-permanent: retry a
            // small number of times, then give up (Failed) instead of looping forever and flooding
            // the log. A post-connection drop (failureReason == null below) reconnects indefinitely.
            if (failureReason is not null && !_haveConnectedAtLeastOnce)
            {
                _initialAttempts++;
                if (_initialAttempts >= MaxInitialConnectAttempts)
                {
                    _logger.LogError(
                        "{Broker} could not connect after {Attempts} attempts ({Reason}) — giving up. " +
                        "Check credentials/endpoint, then reconnect.",
                        _client.Kind, _initialAttempts, failureReason);
                    _state.OnNext(Core.Domain.ConnectionState.Failed);
                    break;
                }
                _logger.LogWarning("{Broker} connect attempt {Attempt}/{Max} failed ({Reason})",
                    _client.Kind, _initialAttempts, MaxInitialConnectAttempts, failureReason);
            }
            else if (failureReason is not null)
            {
                _logger.LogWarning("{Broker} connect attempt failed ({Reason})", _client.Kind, failureReason);
            }

            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { break; }

            delay = TimeSpan.FromSeconds(Math.Min(max.TotalSeconds, delay.TotalSeconds * 2));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _stateSub?.Dispose();
        try { if (_loop is not null) await _loop.ConfigureAwait(false); }
        catch { /* swallow */ }
        _cts.Dispose();
        _state.Dispose();
    }
}
