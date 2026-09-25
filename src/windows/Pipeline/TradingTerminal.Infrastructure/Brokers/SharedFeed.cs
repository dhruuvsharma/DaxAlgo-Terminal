using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>Where and how to open a feed socket — resolved on every connection, because the URL or
/// headers carry a session that may have been renewed since the last one.</summary>
internal sealed record FeedEndpoint(Uri Url, IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    public static FeedEndpoint At(string url) => new(new Uri(url), []);
}

/// <summary>What a shared feed needs to know about one broker's socket protocol.</summary>
internal sealed class FeedProtocol<TPacket>
{
    public required string Name { get; init; }

    public required Func<CancellationToken, Task<FeedEndpoint>> Endpoint { get; init; }

    /// <summary>Messages to send right after connecting, for every key currently wanted — which on a
    /// reconnect re-establishes every subscription at once.</summary>
    public required Func<IReadOnlyCollection<string>, IEnumerable<string>> OnConnect { get; init; }

    /// <summary>Messages to send when a key is first wanted while already connected.</summary>
    public required Func<string, IEnumerable<string>> OnAdd { get; init; }

    /// <summary>Turns a frame into (key, packet) pairs. Frames nobody wants — heartbeats, acks — yield none.</summary>
    public required Func<byte[], IEnumerable<KeyValuePair<string, TPacket>>> Decode { get; init; }

    /// <summary>Messages to send when the last subscriber of a key leaves, where the broker should be told
    /// (a feed that keeps streaming an instrument nobody reads spends the user's bandwidth and, on some
    /// brokers, a subscription slot). Null sends nothing.</summary>
    public Func<string, IEnumerable<string>>? OnRemove { get; init; }

    /// <summary>
    /// Messages to send in answer to a received frame, given the keys wanted now — for handshakes that must
    /// wait on the broker: subscribe only once the login is acknowledged (Schwab), open a channel once
    /// authorised (DXLink), authorise once the socket says it is open (Tradovate). Null answers nothing.
    /// </summary>
    public Func<byte[], IReadOnlyCollection<string>, IEnumerable<string>>? Reply { get; init; }

    /// <summary>A text frame the client sends on a timer, where the broker wants one.</summary>
    public string? Ping { get; init; }

    /// <summary>Seconds between pings; fractional, because Tradovate wants one every 2.5.</summary>
    public double PingSeconds { get; init; } = 10;

    public int InitialDelaySeconds { get; init; } = 1;

    public int MaxDelaySeconds { get; init; } = 30;
}

/// <summary>
/// One socket per broker, shared by every subscription on it.
///
/// <para><b>Why not a socket per subscription</b>, as the public crypto venues use: the Indian brokers cap
/// live connections per user — Kite at three, Angel One at three, Dhan at five — and a chart, a book and
/// a tape on two instruments is already six. Here the first subscriber starts the socket, each new
/// instrument is added to it in place, and every packet is handed to the subscribers of its key.</para>
///
/// <para><b>Bounded fan-out.</b> Each subscriber reads from a channel of 256 that drops the oldest packet
/// when full: a stalled consumer loses stale quotes rather than growing the heap without limit (see the
/// memory-safety notes on the footprint window).</para>
/// </summary>
internal sealed class SharedFeed<TPacket> : IAsyncDisposable
{
    private readonly FeedProtocol<TPacket> _protocol;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Channel<TPacket>, byte>> _subscribers = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _startGate = new();
    private Task? _loop;
    private ClientWebSocket? _socket;

    public SharedFeed(FeedProtocol<TPacket> protocol, ILogger logger)
    {
        _protocol = protocol;
        _logger = logger;
    }

    /// <summary>The keys currently wanted.</summary>
    public IReadOnlyCollection<string> Keys => [.. _subscribers.Where(k => !k.Value.IsEmpty).Select(k => k.Key)];

    /// <summary>Every packet for <paramref name="key"/> until <paramref name="ct"/> is cancelled.</summary>
    public async IAsyncEnumerable<TPacket> SubscribeAsync(string key, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var channel = Channel.CreateBounded<TPacket>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });

        var set = _subscribers.GetOrAdd(key, _ => new ConcurrentDictionary<Channel<TPacket>, byte>());
        var isNew = set.IsEmpty;
        set[channel] = 0;

        EnsureRunning();
        if (isNew && _socket is { State: WebSocketState.Open } socket)
            foreach (var message in _protocol.OnAdd(key))
                await SendAsync(socket, message, ct).ConfigureAwait(false);

        try
        {
            await foreach (var packet in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return packet;
        }
        finally
        {
            set.TryRemove(channel, out _);
            if (set.IsEmpty && _protocol.OnRemove is not null && _socket is { State: WebSocketState.Open } open)
            {
                try
                {
                    foreach (var message in _protocol.OnRemove(key))
                        await SendAsync(open, message, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // The socket may be closing; the broker drops the subscription with it.
                    _logger.LogDebug(ex, "{Name} could not unsubscribe {Key}.", _protocol.Name, key);
                }
            }
        }
    }

    private void EnsureRunning()
    {
        lock (_startGate) _loop ??= Task.Run(() => RunAsync(_stop.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(Math.Max(1, _protocol.InitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, _protocol.MaxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            try
            {
                var endpoint = await _protocol.Endpoint(ct).ConfigureAwait(false);
                foreach (var (name, value) in endpoint.Headers) socket.Options.SetRequestHeader(name, value);
                await socket.ConnectAsync(endpoint.Url, ct).ConfigureAwait(false);
                _socket = socket;

                foreach (var message in _protocol.OnConnect(Keys))
                    await SendAsync(socket, message, ct).ConfigureAwait(false);

                delay = TimeSpan.FromSeconds(Math.Max(1, _protocol.InitialDelaySeconds));
                var ping = _protocol.Ping is null ? Task.CompletedTask : PingAsync(socket, _protocol.Ping, pingCts.Token);

                while (!ct.IsCancellationRequested)
                {
                    var frame = await ReceiveAsync(socket, ct).ConfigureAwait(false);
                    if (frame is null) break;
                    Dispatch(frame);
                    if (_protocol.Reply is not null)
                        foreach (var message in ReplyTo(frame))
                            await SendAsync(socket, message, ct).ConfigureAwait(false);
                }

                pingCts.Cancel();
                try { await ping.ConfigureAwait(false); } catch { /* ended */ }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} feed dropped; reconnecting in {Delay}s.", _protocol.Name, delay.TotalSeconds);
            }
            finally
            {
                _socket = null;
            }

            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }

    private void Dispatch(byte[] frame)
    {
        IEnumerable<KeyValuePair<string, TPacket>> packets;
        try
        {
            packets = _protocol.Decode(frame).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Name} frame could not be decoded.", _protocol.Name);
            return;
        }

        foreach (var (key, packet) in packets)
            if (_subscribers.TryGetValue(key, out var set))
                foreach (var channel in set.Keys)
                    channel.Writer.TryWrite(packet);
    }

    private IReadOnlyList<string> ReplyTo(byte[] frame)
    {
        try
        {
            return [.. _protocol.Reply!(frame, Keys)];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{Name} frame could not be answered.", _protocol.Name);
            return [];
        }
    }

    private async Task PingAsync(ClientWebSocket socket, string ping, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _protocol.PingSeconds)), ct).ConfigureAwait(false);
            await SendAsync(socket, ping, ct).ConfigureAwait(false);
        }
    }

    private async Task SendAsync(ClientWebSocket socket, string message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<byte[]?> ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false); }
            catch (WebSocketException) { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return ms.ToArray();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); } catch { /* stopping */ }
        }

        foreach (var set in _subscribers.Values)
            foreach (var channel in set.Keys)
                channel.Writer.TryComplete();

        _stop.Dispose();
        _sendLock.Dispose();
    }
}
