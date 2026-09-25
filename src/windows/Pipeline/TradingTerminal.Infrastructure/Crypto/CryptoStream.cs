using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// How one public socket is reached and kept alive — everything about a venue's stream that is not
/// parsing.
///
/// <para>The original five venues needed only a fixed URL, one subscribe message and an optional
/// ping. The next twelve did not fit that: KuCoin issues a fresh socket URL per connection, Crypto.com
/// and HTX send heartbeats that must be <i>answered</i> or the socket is closed, HTX gzips every frame,
/// MEXC speaks protocol buffers, and Bitvavo's book is deltas that mean nothing until a REST snapshot
/// has been laid under them. Each of those is one optional member here rather than a fork of the
/// transport.</para>
/// </summary>
internal sealed class CryptoStreamSpec
{
    /// <summary>Venue name for logs.</summary>
    public required string Name { get; init; }

    /// <summary>Resolved on every connection, not once — KuCoin's token-bearing URL is per connection.</summary>
    public required Func<CancellationToken, Task<string>> Url { get; init; }

    /// <summary>Sent in order after each connect.</summary>
    public IReadOnlyList<string> Subscribe { get; init; } = [];

    /// <summary>Sent every <see cref="PingIntervalSeconds"/> while connected, as text. Need not be JSON —
    /// Bitget and Upbit want a bare word.</summary>
    public string? Ping { get; init; }

    public int PingIntervalSeconds { get; init; } = 15;

    public int InitialDelaySeconds { get; init; } = 1;

    public int MaxDelaySeconds { get; init; } = 30;

    /// <summary>Applied to each frame before it is handled — HTX's gzip.</summary>
    public Func<byte[], byte[]>? Decode { get; init; }

    /// <summary>Runs after the subscribe messages are sent and before the first frame is read, on every
    /// connection. Anything sent in the meantime waits in the socket, which is what makes it the right
    /// place to reset a book or fetch the snapshot a delta stream is laid on.</summary>
    public Func<CancellationToken, Task>? OnConnected { get; init; }

    /// <summary>A spec with a fixed URL.</summary>
    public static Func<CancellationToken, Task<string>> Fixed(string url) => _ => Task.FromResult(url);
}

/// <summary>
/// Thrown by a parser that can no longer trust its own state — a sequence gap in a delta book. The
/// stream closes the socket and reconnects, and the reconnect's <see cref="CryptoStreamSpec.OnConnected"/>
/// rebuilds the state from a fresh snapshot. Showing a book with a hole in it would be worse than a
/// one-second gap.
/// </summary>
internal sealed class CryptoStreamResyncException(string reason) : Exception(reason);

/// <summary>What handling one frame produced: items to yield, and optionally text to send back.</summary>
internal readonly record struct CryptoFrame<T>(IEnumerable<T>? Items, string? Reply = null)
{
    public static CryptoFrame<T> None { get; } = new(null);
}

/// <summary>
/// Shared public-WebSocket plumbing for the keyless crypto backends. Unlike Binance — which encodes the
/// stream in the URL — these exchanges connect to one endpoint and then <b>send a subscribe message</b>,
/// and several drop idle sockets unless pinged. This helper connects, sends the subscribe messages,
/// optionally runs a periodic ping, reads full messages, hands each to the venue's handler (which may
/// yield 0..N items per message — trade/candle batches — and may ask for a reply), and reconnects with
/// exponential backoff on any drop that isn't caller cancellation. Pure transport — no exchange-specific
/// knowledge lives here.
/// </summary>
internal static class CryptoStream
{
    /// <summary>The original shape: fixed URL, one subscribe message, JSON frames.</summary>
    public static IAsyncEnumerable<T> StreamAsync<T>(
        string url,
        string? subscribeJson,
        Func<JsonElement, IEnumerable<T>> parse,
        int initialDelaySeconds,
        int maxDelaySeconds,
        ILogger logger,
        string name,
        string? pingJson = null,
        int pingIntervalSeconds = 15,
        CancellationToken ct = default) =>
        StreamJsonAsync(
            new CryptoStreamSpec
            {
                Name = name,
                Url = CryptoStreamSpec.Fixed(url),
                Subscribe = string.IsNullOrEmpty(subscribeJson) ? [] : [subscribeJson],
                Ping = pingJson,
                PingIntervalSeconds = pingIntervalSeconds,
                InitialDelaySeconds = initialDelaySeconds,
                MaxDelaySeconds = maxDelaySeconds,
            },
            parse, logger, reply: null, ct);

    /// <summary>
    /// JSON frames. A frame that is not JSON — Bitget's <c>pong</c>, Upbit's status line — is skipped,
    /// not treated as a failure. The reply hook is for the venues whose heartbeat is a question the
    /// client must answer: given each parsed frame, it returns text to send back, or null.
    /// </summary>
    public static IAsyncEnumerable<T> StreamJsonAsync<T>(
        CryptoStreamSpec spec,
        Func<JsonElement, IEnumerable<T>> parse,
        ILogger logger,
        Func<JsonElement, string?>? reply = null,
        CancellationToken ct = default) =>
        StreamFramesAsync(spec, frame =>
        {
            using var doc = JsonDocument.Parse(frame);
            var answer = reply?.Invoke(doc.RootElement);

            // Materialised before the document is disposed: a lazy parser would otherwise read a
            // JsonElement whose backing memory is already gone.
            try
            {
                return new CryptoFrame<T>(parse(doc.RootElement).ToList(), answer);
            }
            catch (Exception ex) when (answer is not null && ex is not CryptoStreamResyncException)
            {
                // A heartbeat is answered even when the data parser chokes on it — an unanswered one
                // closes the socket, which is a far bigger loss than one unparsed frame.
                return new CryptoFrame<T>(null, answer);
            }
        }, logger, ct);

    /// <summary>Raw frames, after <see cref="CryptoStreamSpec.Decode"/>. For MEXC's protocol buffers.</summary>
    public static async IAsyncEnumerable<T> StreamFramesAsync<T>(
        CryptoStreamSpec spec,
        Func<byte[], CryptoFrame<T>> handle,
        ILogger logger,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var name = spec.Name;
        var delay = TimeSpan.FromSeconds(Math.Max(1, spec.InitialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, spec.MaxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? ws = new();
            var sendLock = new SemaphoreSlim(1, 1);
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? pingTask = null;

            try
            {
                var url = await spec.Url(ct).ConfigureAwait(false);
                await ws.ConnectAsync(new Uri(url), ct).ConfigureAwait(false);
                foreach (var message in spec.Subscribe)
                    await SendAsync(ws, sendLock, message, ct).ConfigureAwait(false);
                if (spec.OnConnected is not null)
                    await spec.OnConnected(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ws.Dispose();
                sendLock.Dispose();
                yield break;
            }
            catch (Exception ex)
            {
                ws.Dispose();
                ws = null;
                logger.LogWarning(ex, "{Name} WS connect failed; retrying in {Delay}s.", name, delay.TotalSeconds);
            }

            if (ws is not null)
            {
                delay = TimeSpan.FromSeconds(Math.Max(1, spec.InitialDelaySeconds)); // reset backoff after a clean connect

                if (!string.IsNullOrEmpty(spec.Ping))
                    pingTask = PingLoopAsync(ws, sendLock, spec.Ping, spec.PingIntervalSeconds, logger, name, pingCts.Token);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                        if (msg is null) break;

                        CryptoFrame<T> frame;
                        try
                        {
                            if (spec.Decode is not null) msg = spec.Decode(msg);
                            frame = handle(msg);
                        }
                        catch (CryptoStreamResyncException ex)
                        {
                            logger.LogInformation("{Name} resynchronising: {Reason}", name, ex.Message);
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "{Name} parse error.", name);
                            continue;
                        }

                        if (frame.Reply is not null)
                        {
                            try { await SendAsync(ws, sendLock, frame.Reply, ct).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                logger.LogDebug(ex, "{Name} reply failed.", name);
                            }
                        }

                        if (frame.Items is not null)
                            foreach (var item in frame.Items)
                                if (item is not null)
                                    yield return item;
                    }
                }
                finally
                {
                    pingCts.Cancel();
                    if (pingTask is not null) { try { await pingTask.ConfigureAwait(false); } catch { /* ignore */ } }
                    try
                    {
                        if (ws.State == WebSocketState.Open)
                            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None).ConfigureAwait(false);
                    }
                    catch { /* swallow */ }
                    ws.Dispose();
                    sendLock.Dispose();
                }
            }
            else
            {
                sendLock.Dispose();
            }

            if (ct.IsCancellationRequested) yield break;
            try { await Task.Delay(delay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { yield break; }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, maxDelay.TotalSeconds));
        }
    }

    private static async Task PingLoopAsync(
        ClientWebSocket ws, SemaphoreSlim sendLock, string pingJson, int intervalSeconds,
        ILogger logger, string name, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, intervalSeconds)), ct).ConfigureAwait(false);
                await SendAsync(ws, sendLock, pingJson, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (Exception ex) { logger.LogDebug(ex, "{Name} ping loop ended.", name); }
    }

    private static async Task SendAsync(ClientWebSocket ws, SemaphoreSlim sendLock, string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, ct)
                .ConfigureAwait(false);
        }
        finally { sendLock.Release(); }
    }

    /// <summary>Reads one full WebSocket message, text or binary (re-assembling continuation frames), or
    /// null on close/error/cancel. Binary is not an error: Upbit sends its JSON in binary frames, HTX
    /// gzips, and MEXC sends protocol buffers.</summary>
    private static async Task<byte[]?> ReceiveMessageAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return null; }
            catch (WebSocketException) { return null; }

            if (result.MessageType == WebSocketMessageType.Close) return null;

            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }
        return ms.ToArray();
    }
}
