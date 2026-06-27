using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace TradingTerminal.Infrastructure.Crypto;

/// <summary>
/// Shared public-WebSocket plumbing for the keyless crypto backends (Coinbase / Bybit / Kraken / OKX).
/// Unlike Binance — which encodes the stream in the URL — these exchanges connect to one endpoint and
/// then <b>send a subscribe message</b>, and several drop idle sockets unless pinged. This helper:
/// connects, sends the subscribe JSON, optionally runs a periodic ping, reads full text messages, runs
/// the supplied parser (which may yield 0..N items per message — trade/candle batches), and reconnects
/// with exponential backoff on any drop that isn't caller cancellation. Pure transport — no
/// exchange-specific knowledge lives here.
/// </summary>
internal static class CryptoStream
{
    public static async IAsyncEnumerable<T> StreamAsync<T>(
        string url,
        string? subscribeJson,
        Func<JsonElement, IEnumerable<T>> parse,
        int initialDelaySeconds,
        int maxDelaySeconds,
        ILogger logger,
        string name,
        string? pingJson = null,
        int pingIntervalSeconds = 15,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var uri = new Uri(url);
        var delay = TimeSpan.FromSeconds(Math.Max(1, initialDelaySeconds));
        var maxDelay = TimeSpan.FromSeconds(Math.Max(1, maxDelaySeconds));

        while (!ct.IsCancellationRequested)
        {
            ClientWebSocket? ws = new();
            var sendLock = new SemaphoreSlim(1, 1);
            using var pingCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? pingTask = null;

            try
            {
                await ws.ConnectAsync(uri, ct).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(subscribeJson))
                    await SendAsync(ws, sendLock, subscribeJson, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ws.Dispose();
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
                delay = TimeSpan.FromSeconds(Math.Max(1, initialDelaySeconds)); // reset backoff after a clean connect

                if (!string.IsNullOrEmpty(pingJson))
                    pingTask = PingLoopAsync(ws, sendLock, pingJson, pingIntervalSeconds, logger, name, pingCts.Token);

                try
                {
                    while (!ct.IsCancellationRequested)
                    {
                        var msg = await ReceiveMessageAsync(ws, ct).ConfigureAwait(false);
                        if (msg is null) break;

                        IEnumerable<T>? items = null;
                        try
                        {
                            using var doc = JsonDocument.Parse(msg);
                            items = parse(doc.RootElement);
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug(ex, "{Name} parse error.", name);
                        }

                        if (items is not null)
                            foreach (var item in items)
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

    /// <summary>Reads one full WebSocket text message (re-assembling continuation frames), or null on close/error/cancel.</summary>
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
