using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using SHA1 = System.Security.Cryptography.SHA1;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Infrastructure.Crypto;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The shared crypto socket, driven against a WebSocket server on the loopback interface.
///
/// <para><b>The regression this file exists for.</b> Until 2026-09-25 the stream parsed each frame into a
/// <c>JsonDocument</c>, disposed it, and then enumerated the parser's result. Every venue parser is a lazy
/// iterator, so enumeration read a disposed document and threw on the first data frame — the live
/// ticks, books, trades and bars of Coinbase, Bybit, Kraken, OKX, Deribit and Hyperliquid all died on
/// arrival, and nothing in the suite noticed, because no test ever put a frame through the transport.</para>
/// </summary>
public sealed class CryptoStreamTransportTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(20);

    private static IEnumerable<int> LazyNumbers(JsonElement root)
    {
        foreach (var n in root.GetProperty("data").EnumerateArray()) yield return n.GetInt32();
    }

    private static CryptoStreamSpec Spec(LoopbackWebSocketServer server, params string[] subscribe) => new()
    {
        Name = "loopback",
        Url = CryptoStreamSpec.Fixed(server.Url),
        Subscribe = subscribe,
        InitialDelaySeconds = 1,
        MaxDelaySeconds = 1,
    };

    private static async Task<List<T>> Take<T>(IAsyncEnumerable<T> stream, int count)
    {
        using var cts = new CancellationTokenSource(Budget);
        var items = new List<T>();
        await foreach (var item in stream.WithCancellation(cts.Token))
        {
            items.Add(item);
            if (items.Count == count) break;
        }

        return items;
    }

    [Fact]
    public async Task A_lazy_parser_is_enumerated_before_its_document_is_disposed()
    {
        await using var server = new LoopbackWebSocketServer(async (ws, _) => await ws.SendTextAsync("""{"data":[1,2,3]}"""));

        var items = await Take(CryptoStream.StreamAsync(
            server.Url, subscribeJson: null, LazyNumbers, 1, 1, NullLogger.Instance, "loopback"), 3);

        items.Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task Subscribe_messages_go_out_in_order_before_anything_is_read()
    {
        var received = new ConcurrentQueue<string>();
        await using var server = new LoopbackWebSocketServer(async (ws, _) =>
        {
            received.Enqueue(await ws.ReceiveTextAsync());
            received.Enqueue(await ws.ReceiveTextAsync());
            await ws.SendTextAsync("""{"data":[7]}""");
        });

        var items = await Take(CryptoStream.StreamJsonAsync(Spec(server, "first", "second"), LazyNumbers, NullLogger.Instance), 1);

        items.Should().Equal(7);
        received.Should().Equal("first", "second");
    }

    [Fact]
    public async Task A_server_heartbeat_is_answered_on_the_same_socket()
    {
        var answer = new TaskCompletionSource<string>();
        await using var server = new LoopbackWebSocketServer(async (ws, _) =>
        {
            await ws.SendTextAsync("""{"ping":42}""");
            answer.TrySetResult(await ws.ReceiveTextAsync());
            await ws.SendTextAsync("""{"data":[1]}""");
        });

        await Take(CryptoStream.StreamJsonAsync(Spec(server), LazyNumbers, NullLogger.Instance,
            reply: root => root.TryGetProperty("ping", out var p) ? $"{{\"pong\":{p.GetRawText()}}}" : null), 1);

        (await answer.Task.WaitAsync(Budget)).Should().Be("""{"pong":42}""");
    }

    [Fact]
    public async Task A_gzipped_frame_is_decoded_before_it_is_parsed()
    {
        await using var server = new LoopbackWebSocketServer(async (ws, _) =>
        {
            using var packed = new MemoryStream();
            using (var gz = new GZipStream(packed, CompressionLevel.Fastest, leaveOpen: true))
                gz.Write("""{"data":[5,6]}"""u8);
            await ws.SendAsync(packed.ToArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        });

        var spec = new CryptoStreamSpec
        {
            Name = "loopback",
            Url = CryptoStreamSpec.Fixed(server.Url),
            Decode = TradingTerminal.Infrastructure.Htx.RealHtxClient.Gunzip,
        };

        (await Take(CryptoStream.StreamJsonAsync(spec, LazyNumbers, NullLogger.Instance), 2)).Should().Equal(5, 6);
    }

    [Fact]
    public async Task A_resync_reconnects_and_the_connect_hook_runs_again()
    {
        var hooks = 0;
        await using var server = new LoopbackWebSocketServer(async (ws, connection) =>
            await ws.SendTextAsync(connection == 1 ? """{"gap":true}""" : """{"data":[9]}"""));

        var spec = new CryptoStreamSpec
        {
            Name = "loopback",
            Url = CryptoStreamSpec.Fixed(server.Url),
            InitialDelaySeconds = 1,
            OnConnected = _ => { Interlocked.Increment(ref hooks); return Task.CompletedTask; },
        };

        var items = await Take(CryptoStream.StreamJsonAsync(spec, root =>
            root.TryGetProperty("gap", out _) ? throw new CryptoStreamResyncException("gap") : LazyNumbers(root),
            NullLogger.Instance), 1);

        items.Should().Equal(9);
        server.Connections.Should().Be(2);
        hooks.Should().Be(2, "the snapshot a delta stream sits on is rebuilt on every connection");
    }

    [Fact]
    public async Task The_url_is_resolved_on_every_connection()
    {
        var resolutions = 0;
        await using var server = new LoopbackWebSocketServer(async (ws, connection) =>
        {
            // The first connection closes at once, as a venue does when a token has expired.
            if (connection == 1)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "token expired", CancellationToken.None);
                return;
            }
            await ws.SendTextAsync("""{"data":[4]}""");
        });

        var spec = new CryptoStreamSpec
        {
            Name = "loopback",
            Url = _ => { Interlocked.Increment(ref resolutions); return Task.FromResult(server.Url); },
            InitialDelaySeconds = 1,
        };

        (await Take(CryptoStream.StreamJsonAsync(spec, LazyNumbers, NullLogger.Instance), 1)).Should().Equal(4);
        resolutions.Should().Be(2, "a per-connection token must be fetched again for the reconnect");
    }

    [Fact]
    public async Task A_frame_that_is_not_json_is_skipped_not_fatal()
    {
        await using var server = new LoopbackWebSocketServer(async (ws, _) =>
        {
            await ws.SendTextAsync("pong");
            await ws.SendTextAsync("""{"data":[8]}""");
        });

        (await Take(CryptoStream.StreamJsonAsync(Spec(server), LazyNumbers, NullLogger.Instance), 1)).Should().Equal(8);
    }
}

/// <summary>
/// A WebSocket server on 127.0.0.1 and an ephemeral port, speaking just enough HTTP to complete the
/// upgrade. No <c>HttpListener</c> — that needs a URL reservation on Windows — and no ASP.NET.
/// Each accepted connection runs the script, then stays open until the client goes away.
/// </summary>
internal sealed class LoopbackWebSocketServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accepting;
    private int _connections;

    public LoopbackWebSocketServer(Func<WebSocket, int, Task> script)
    {
        _listener.Start();
        _accepting = AcceptAsync(script);
    }

    public string Url => $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    public int Connections => Volatile.Read(ref _connections);

    private async Task AcceptAsync(Func<WebSocket, int, Task> script)
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { return; }

            var number = Interlocked.Increment(ref _connections);
            _ = Task.Run(async () =>
            {
                using (client)
                {
                    var stream = client.GetStream();
                    await HandshakeAsync(stream);
                    using var ws = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true });
                    try
                    {
                        await script(ws, number);
                        if (ws.State == WebSocketState.Open) await DrainAsync(ws);
                    }
                    catch { /* the client went away */ }
                }
            });
        }
    }

    private async Task DrainAsync(WebSocket ws)
    {
        var buffer = new byte[4096];
        try
        {
            while (ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                await ws.ReceiveAsync(buffer, _stop.Token);
        }
        catch { /* closed */ }
    }

    private static async Task HandshakeAsync(NetworkStream stream)
    {
        var request = new StringBuilder();
        var buffer = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(buffer) == 0) throw new IOException("closed during handshake");
            request.Append((char)buffer[0]);
        }

        var key = request.ToString().Split("\r\n")
            .First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))
            .Split(':', 2)[1].Trim();
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response));
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _accepting; } catch { }
        _stop.Dispose();
    }
}

internal static class WebSocketTestExtensions
{
    public static Task SendTextAsync(this WebSocket ws, string text) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    public static async Task<string> ReceiveTextAsync(this WebSocket ws)
    {
        var buffer = new byte[4096];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, CancellationToken.None);
            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
