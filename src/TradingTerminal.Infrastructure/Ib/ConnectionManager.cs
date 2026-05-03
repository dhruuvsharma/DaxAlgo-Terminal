using System.Reactive.Linq;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Owns the connect / reconnect loop with exponential backoff (1s → cap).
/// Surfaces a <see cref="ConnectionState"/> stream that consumers can subscribe to.
/// Reconnect is triggered automatically when the underlying client's state drops to
/// <see cref="ConnectionState.Disconnected"/> after at least one successful connection.
/// </summary>
public sealed class ConnectionManager : IAsyncDisposable
{
    private readonly IIbClient _client;
    private readonly InteractiveBrokersOptions _options;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _stateSub;
    private Task? _loop;
    private bool _userRequestedDisconnect;
    private bool _haveConnectedAtLeastOnce;

    public ConnectionManager(IIbClient client, IOptions<InteractiveBrokersOptions> options,
        ILogger<ConnectionManager> logger)
    {
        _client = client;
        _options = options.Value;
        _logger = logger;
    }

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task StartAsync(CancellationToken ct = default)
    {
        _userRequestedDisconnect = false;
        _stateSub ??= _client.ConnectionState.Subscribe(OnClientStateChanged);

        // Kick off the initial connect; the reconnect loop will take over on drops.
        _loop = Task.Run(() => RunReconnectLoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        _userRequestedDisconnect = true;
        await _client.DisconnectAsync(ct);
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
    }

    public async Task RequestReconnectAsync(CancellationToken ct = default)
    {
        _logger.LogInformation("User-requested reconnect");
        _userRequestedDisconnect = false;
        try { await _client.DisconnectAsync(ct); } catch { /* swallow */ }
        // Restart the loop if it has exited.
        if (_loop is null || _loop.IsCompleted)
            _loop = Task.Run(() => RunReconnectLoopAsync(_cts.Token));
    }

    private void OnClientStateChanged(ConnectionState s)
    {
        _state.OnNext(s);
        if (s is Core.Domain.ConnectionState.Connected) _haveConnectedAtLeastOnce = true;
    }

    private async Task RunReconnectLoopAsync(CancellationToken ct)
    {
        var initial = TimeSpan.FromSeconds(Math.Max(1, _options.ReconnectInitialDelaySeconds));
        var max = TimeSpan.FromSeconds(Math.Max(initial.TotalSeconds, _options.ReconnectMaxDelaySeconds));
        var delay = initial;

        while (!ct.IsCancellationRequested && !_userRequestedDisconnect)
        {
            try
            {
                _state.OnNext(_haveConnectedAtLeastOnce
                    ? Core.Domain.ConnectionState.Reconnecting
                    : Core.Domain.ConnectionState.Connecting);

                await _client.ConnectAsync(_options.Host, _options.Port, _options.ClientId, ct);
                _logger.LogInformation("Connected to IB at {Host}:{Port} (clientId={ClientId})",
                    _options.Host, _options.Port, _options.ClientId);

                delay = initial; // reset backoff on success

                // Wait until the client transitions away from Connected.
                using var droppedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var sub = _client.ConnectionState
                    .Where(s => s is Core.Domain.ConnectionState.Disconnected or Core.Domain.ConnectionState.Failed)
                    .Subscribe(_ => dropped.TrySetResult());

                await dropped.Task.WaitAsync(ct);
                if (_userRequestedDisconnect) break;
                _logger.LogWarning("IB connection dropped — will retry");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IB connect attempt failed");
            }

            if (_userRequestedDisconnect || ct.IsCancellationRequested) break;

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
