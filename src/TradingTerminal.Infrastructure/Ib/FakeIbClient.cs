using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Synthetic IB client. Used by default so the app builds and runs without the IB binary.
/// Generates a plausible random walk per symbol and "ticks" a new closed bar on the
/// configured cadence.
/// </summary>
public sealed class FakeIbClient : IIbClient
{
    private readonly ILogger<FakeIbClient> _logger;
    private readonly BehaviorSubject<ConnectionState> _state = new(Core.Domain.ConnectionState.Disconnected);
    private readonly Random _rng = new(42);
    private readonly Dictionary<string, double> _lastPrice = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public FakeIbClient(ILogger<FakeIbClient> logger)
    {
        _logger = logger;
    }

    public IObservable<ConnectionState> ConnectionState => _state.AsObservable();

    public async Task ConnectAsync(string host, int port, int clientId, CancellationToken ct = default)
    {
        _logger.LogInformation("FakeIbClient connecting (host={Host}, port={Port}, clientId={ClientId})",
            host, port, clientId);
        _state.OnNext(Core.Domain.ConnectionState.Connecting);

        // Brief simulated handshake so the UI's "Connecting..." state is visible in demo mode.
        try { await Task.Delay(400, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { _state.OnNext(Core.Domain.ConnectionState.Disconnected); throw; }

        _state.OnNext(Core.Domain.ConnectionState.Connected);
    }

    public Task DisconnectAsync(CancellationToken ct = default)
    {
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract, BarSize barSize, TimeSpan duration, CancellationToken ct = default)
    {
        var step = barSize.ToTimeSpan();
        var count = Math.Max(1, (int)(duration.TotalSeconds / step.TotalSeconds));
        var startUtc = DateTime.UtcNow - TimeSpan.FromTicks(step.Ticks * count);

        var bars = new List<Bar>(count);
        var price = SeedPriceFor(contract.Symbol);
        for (int i = 0; i < count; i++)
        {
            var ts = startUtc + TimeSpan.FromTicks(step.Ticks * i);
            var (bar, next) = NextBar(price, ts);
            bars.Add(bar);
            price = next;
        }

        lock (_gate) { _lastPrice[contract.Symbol] = price; }

        _logger.LogDebug("FakeIbClient produced {Count} historical {Size} bars for {Symbol}",
            bars.Count, barSize.ToDisplayString(), contract.Symbol);

        return Task.FromResult<IReadOnlyList<Bar>>(bars);
    }

    public async IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract, BarSize barSize,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_state.Value is not Core.Domain.ConnectionState.Connected)
            throw new InvalidOperationException("Not connected.");

        // For demo purposes synthesize bars on a sped-up cadence (1 bar per second)
        // so the chart actually moves while you're watching.
        var step = TimeSpan.FromSeconds(1);
        var ch = Channel.CreateUnbounded<Bar>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var price = SeedPriceFor(contract.Symbol);
                var ts = DateTime.UtcNow;
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(step, ct);
                    var (bar, next) = NextBar(price, ts);
                    price = next;
                    ts += barSize.ToTimeSpan();
                    if (!ch.Writer.TryWrite(bar)) break;
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            finally { ch.Writer.TryComplete(); }
        }, ct);

        await foreach (var bar in ch.Reader.ReadAllAsync(ct))
            yield return bar;
    }

    public async IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (_state.Value is not Core.Domain.ConnectionState.Connected)
            throw new InvalidOperationException("Not connected.");

        // ~5 ticks per second so the bid-tick rule has enough samples per bar to be interesting.
        var step = TimeSpan.FromMilliseconds(200);
        var ch = Channel.CreateUnbounded<Tick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        _ = Task.Run(async () =>
        {
            try
            {
                var bid = SeedPriceFor(contract.Symbol);
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(step, ct);

                    // Random walk on the bid; ask = bid + small spread. Mostly small moves with
                    // occasional bigger jumps so the delta accumulates a meaningful signed sum.
                    var jump = (_rng.NextDouble() - 0.5) * 0.04;
                    if (_rng.NextDouble() < 0.05)
                        jump *= 4; // occasional outlier
                    bid = Math.Max(0.01, bid + jump);
                    var spread = 0.01 + _rng.NextDouble() * 0.02;
                    var ask = bid + spread;
                    var bidSize = (long)(1 + _rng.NextDouble() * 50);
                    var askSize = (long)(1 + _rng.NextDouble() * 50);

                    var tick = new Tick(DateTime.UtcNow, bid, ask, bidSize, askSize);
                    if (!ch.Writer.TryWrite(tick)) break;
                }
            }
            catch (OperationCanceledException) { /* expected */ }
            finally { ch.Writer.TryComplete(); }
        }, ct);

        await foreach (var tick in ch.Reader.ReadAllAsync(ct))
            yield return tick;
    }

    private double SeedPriceFor(string symbol)
    {
        lock (_gate)
        {
            if (_lastPrice.TryGetValue(symbol, out var p)) return p;
            // Stable but symbol-dependent base.
            var hash = Math.Abs(symbol.GetHashCode(StringComparison.Ordinal)) % 400;
            var seed = 50 + hash;
            _lastPrice[symbol] = seed;
            return seed;
        }
    }

    private (Bar bar, double next) NextBar(double prevClose, DateTime ts)
    {
        // Random walk with small drift. Bar's open == previous close.
        var open = prevClose;
        var drift = (_rng.NextDouble() - 0.5) * 0.6;
        var close = Math.Max(0.01, open + drift);
        var high = Math.Max(open, close) + _rng.NextDouble() * 0.2;
        var low  = Math.Max(0.01, Math.Min(open, close) - _rng.NextDouble() * 0.2);
        var vol  = (long)(50_000 + _rng.NextDouble() * 200_000);
        return (new Bar(ts, open, high, low, close, vol), close);
    }

    public ValueTask DisposeAsync()
    {
        _state.OnNext(Core.Domain.ConnectionState.Disconnected);
        _state.Dispose();
        return ValueTask.CompletedTask;
    }
}
