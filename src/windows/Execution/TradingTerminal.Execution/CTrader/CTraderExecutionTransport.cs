using System.Buffers;
using System.Buffers.Binary;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Google.Protobuf;

namespace TradingTerminal.Execution.CTrader;

/// <summary>
/// cTrader Open API envelope transport. Implementations may use the real demo TLS
/// endpoint or a deterministic in-process protobuf peer.
/// </summary>
public interface ICTraderExecutionTransport : IAsyncDisposable
{
    /// <summary>The already-gated endpoint represented by this transport.</summary>
    CTraderExecutionEndpoint Endpoint { get; }

    /// <summary>Gets whether a transport session is currently open.</summary>
    bool IsConnected { get; }

    /// <summary>Raised for one fully parsed Open API protobuf envelope.</summary>
    event Action<ProtoMessage>? MessageReceived;

    /// <summary>Raised when the read loop or TLS session becomes unusable.</summary>
    event Action<Exception>? Faulted;

    /// <summary>Opens the transport session.</summary>
    Task ConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes one complete length-prefixed protobuf envelope.</summary>
    Task SendAsync(ProtoMessage message, CancellationToken cancellationToken = default);

    /// <summary>Closes the transport session.</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

/// <summary>One exact unrealized-PnL value from the Open API 2.0 position snapshot.</summary>
public readonly record struct CTraderPositionUnrealizedPnl(
    long PositionId,
    long GrossUnrealizedPnl,
    int NetUnrealizedPnl);

/// <summary>
/// Open API 2.0 position-unrealized-PnL response. OpenAPI.Net 1.4.4 predates this generated
/// message, so the bounded wire codec below keeps the message local and exact.
/// </summary>
public sealed record CTraderPositionUnrealizedPnlResponse(
    long CtidTraderAccountId,
    uint MoneyDigits,
    IReadOnlyList<CTraderPositionUnrealizedPnl> Positions);

/// <summary>Explicit Open API 2.0 envelope codec used identically by the real and mock transports.</summary>
public static class CTraderOpenApiProtocol
{
    /// <summary>Official Open API 2.0 payload for ProtoOAGetPositionUnrealizedPnLReq.</summary>
    public const uint PositionUnrealizedPnlRequestPayloadType = 2187;

    /// <summary>Official Open API 2.0 payload for ProtoOAGetPositionUnrealizedPnLRes.</summary>
    public const uint PositionUnrealizedPnlResponsePayloadType = 2188;

    /// <summary>Wraps one generated Open API message in its transport envelope.</summary>
    public static ProtoMessage Encode(IMessage message, string? clientMessageId = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payloadType = message switch
        {
            ProtoHeartbeatEvent => 51u,
            ProtoOAApplicationAuthReq => (uint)ProtoOAPayloadType.ProtoOaApplicationAuthReq,
            ProtoOAApplicationAuthRes => (uint)ProtoOAPayloadType.ProtoOaApplicationAuthRes,
            ProtoOAAccountAuthReq => (uint)ProtoOAPayloadType.ProtoOaAccountAuthReq,
            ProtoOAAccountAuthRes => (uint)ProtoOAPayloadType.ProtoOaAccountAuthRes,
            ProtoOAVersionReq => (uint)ProtoOAPayloadType.ProtoOaVersionReq,
            ProtoOAVersionRes => (uint)ProtoOAPayloadType.ProtoOaVersionRes,
            ProtoOANewOrderReq => (uint)ProtoOAPayloadType.ProtoOaNewOrderReq,
            ProtoOACancelOrderReq => (uint)ProtoOAPayloadType.ProtoOaCancelOrderReq,
            ProtoOAAmendOrderReq => (uint)ProtoOAPayloadType.ProtoOaAmendOrderReq,
            ProtoOAAssetListReq => (uint)ProtoOAPayloadType.ProtoOaAssetListReq,
            ProtoOAAssetListRes => (uint)ProtoOAPayloadType.ProtoOaAssetListRes,
            ProtoOASymbolByIdReq => (uint)ProtoOAPayloadType.ProtoOaSymbolByIdReq,
            ProtoOASymbolByIdRes => (uint)ProtoOAPayloadType.ProtoOaSymbolByIdRes,
            ProtoOATraderReq => (uint)ProtoOAPayloadType.ProtoOaTraderReq,
            ProtoOATraderRes => (uint)ProtoOAPayloadType.ProtoOaTraderRes,
            ProtoOATraderUpdatedEvent => (uint)ProtoOAPayloadType.ProtoOaTraderUpdateEvent,
            ProtoOASymbolChangedEvent => (uint)ProtoOAPayloadType.ProtoOaSymbolChangedEvent,
            ProtoOAReconcileReq => (uint)ProtoOAPayloadType.ProtoOaReconcileReq,
            ProtoOAReconcileRes => (uint)ProtoOAPayloadType.ProtoOaReconcileRes,
            ProtoOAExecutionEvent => (uint)ProtoOAPayloadType.ProtoOaExecutionEvent,
            ProtoOAOrderErrorEvent => (uint)ProtoOAPayloadType.ProtoOaOrderErrorEvent,
            ProtoOAErrorRes => (uint)ProtoOAPayloadType.ProtoOaErrorRes,
            ProtoOAGetAccountListByAccessTokenReq => (uint)ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenReq,
            ProtoOAGetAccountListByAccessTokenRes => (uint)ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenRes,
            ProtoOAAccountsTokenInvalidatedEvent => (uint)ProtoOAPayloadType.ProtoOaAccountsTokenInvalidatedEvent,
            ProtoOAAccountDisconnectEvent => (uint)ProtoOAPayloadType.ProtoOaAccountDisconnectEvent,
            ProtoOAMarginChangedEvent => (uint)ProtoOAPayloadType.ProtoOaMarginChangedEvent,
            ProtoOAOrderListReq => (uint)ProtoOAPayloadType.ProtoOaOrderListReq,
            ProtoOAOrderListRes => (uint)ProtoOAPayloadType.ProtoOaOrderListRes,
            _ => throw new ArgumentException($"Unsupported cTrader Open API message type '{message.GetType().Name}'.", nameof(message)),
        };
        var envelope = new ProtoMessage
        {
            PayloadType = payloadType,
            Payload = message.ToByteString(),
        };
        if (!string.IsNullOrWhiteSpace(clientMessageId))
            envelope.ClientMsgId = clientMessageId;
        return envelope;
    }

    /// <summary>Parses one supported Open API envelope into its generated protobuf type.</summary>
    public static IMessage? Decode(ProtoMessage envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return envelope.PayloadType switch
        {
            51u => ProtoHeartbeatEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaApplicationAuthReq => ProtoOAApplicationAuthReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaApplicationAuthRes => ProtoOAApplicationAuthRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAccountAuthReq => ProtoOAAccountAuthReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAccountAuthRes => ProtoOAAccountAuthRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaVersionReq => ProtoOAVersionReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaVersionRes => ProtoOAVersionRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaNewOrderReq => ProtoOANewOrderReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaCancelOrderReq => ProtoOACancelOrderReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAmendOrderReq => ProtoOAAmendOrderReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAssetListReq => ProtoOAAssetListReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAssetListRes => ProtoOAAssetListRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaSymbolByIdReq => ProtoOASymbolByIdReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaSymbolByIdRes => ProtoOASymbolByIdRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaTraderReq => ProtoOATraderReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaTraderRes => ProtoOATraderRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaTraderUpdateEvent => ProtoOATraderUpdatedEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaSymbolChangedEvent => ProtoOASymbolChangedEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaReconcileReq => ProtoOAReconcileReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaReconcileRes => ProtoOAReconcileRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaExecutionEvent => ProtoOAExecutionEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaOrderErrorEvent => ProtoOAOrderErrorEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaErrorRes => ProtoOAErrorRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenReq => ProtoOAGetAccountListByAccessTokenReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaGetAccountsByAccessTokenRes => ProtoOAGetAccountListByAccessTokenRes.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAccountsTokenInvalidatedEvent => ProtoOAAccountsTokenInvalidatedEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaAccountDisconnectEvent => ProtoOAAccountDisconnectEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaMarginChangedEvent => ProtoOAMarginChangedEvent.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaOrderListReq => ProtoOAOrderListReq.Parser.ParseFrom(envelope.Payload),
            (uint)ProtoOAPayloadType.ProtoOaOrderListRes => ProtoOAOrderListRes.Parser.ParseFrom(envelope.Payload),
            _ => null,
        };
    }

    /// <summary>Encodes the official unrealized-PnL request omitted by OpenAPI.Net 1.4.4.</summary>
    public static ProtoMessage EncodePositionUnrealizedPnlRequest(
        long ctidTraderAccountId,
        string clientMessageId)
    {
        if (ctidTraderAccountId <= 0)
            throw new ArgumentOutOfRangeException(nameof(ctidTraderAccountId));
        if (string.IsNullOrWhiteSpace(clientMessageId))
            throw new ArgumentException("A request correlation ID is required.", nameof(clientMessageId));
        using var payload = new MemoryStream();
        using (var output = new CodedOutputStream(payload, leaveOpen: true))
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt64(ctidTraderAccountId);
            output.Flush();
        }
        return new ProtoMessage
        {
            PayloadType = PositionUnrealizedPnlRequestPayloadType,
            Payload = ByteString.CopyFrom(payload.ToArray()),
            ClientMsgId = clientMessageId,
        };
    }

    /// <summary>Decodes an unrealized-PnL request for deterministic in-process peers.</summary>
    public static bool TryDecodePositionUnrealizedPnlRequest(
        ProtoMessage envelope,
        out long ctidTraderAccountId)
    {
        ctidTraderAccountId = 0;
        if (envelope is null || envelope.PayloadType != PositionUnrealizedPnlRequestPayloadType)
            return false;
        var seenAccount = false;
        try
        {
            var input = new CodedInputStream(envelope.Payload.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                if (tag == WireFormat.MakeTag(2, WireFormat.WireType.Varint) && !seenAccount)
                {
                    ctidTraderAccountId = input.ReadInt64();
                    seenAccount = true;
                }
                else
                {
                    input.SkipLastField();
                }
            }
            return seenAccount && ctidTraderAccountId > 0;
        }
        catch (InvalidProtocolBufferException)
        {
            ctidTraderAccountId = 0;
            return false;
        }
    }

    /// <summary>Encodes an unrealized-PnL response for deterministic in-process peers.</summary>
    public static ProtoMessage EncodePositionUnrealizedPnlResponse(
        CTraderPositionUnrealizedPnlResponse response,
        string clientMessageId)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.CtidTraderAccountId <= 0 || response.Positions is null)
            throw new ArgumentException("The unrealized-PnL response is invalid.", nameof(response));
        if (string.IsNullOrWhiteSpace(clientMessageId))
            throw new ArgumentException("A request correlation ID is required.", nameof(clientMessageId));
        using var payload = new MemoryStream();
        using (var output = new CodedOutputStream(payload, leaveOpen: true))
        {
            output.WriteTag(2, WireFormat.WireType.Varint);
            output.WriteInt64(response.CtidTraderAccountId);
            foreach (var position in response.Positions)
            {
                using var nestedPayload = new MemoryStream();
                using (var nested = new CodedOutputStream(nestedPayload, leaveOpen: true))
                {
                    nested.WriteTag(1, WireFormat.WireType.Varint);
                    nested.WriteInt64(position.PositionId);
                    nested.WriteTag(2, WireFormat.WireType.Varint);
                    nested.WriteInt64(position.GrossUnrealizedPnl);
                    nested.WriteTag(3, WireFormat.WireType.Varint);
                    nested.WriteInt32(position.NetUnrealizedPnl);
                    nested.Flush();
                }
                output.WriteTag(3, WireFormat.WireType.LengthDelimited);
                output.WriteBytes(ByteString.CopyFrom(nestedPayload.ToArray()));
            }
            output.WriteTag(4, WireFormat.WireType.Varint);
            output.WriteUInt32(response.MoneyDigits);
            output.Flush();
        }
        return new ProtoMessage
        {
            PayloadType = PositionUnrealizedPnlResponsePayloadType,
            Payload = ByteString.CopyFrom(payload.ToArray()),
            ClientMsgId = clientMessageId,
        };
    }

    /// <summary>Decodes the exact official unrealized-PnL response.</summary>
    public static bool TryDecodePositionUnrealizedPnlResponse(
        ProtoMessage envelope,
        out CTraderPositionUnrealizedPnlResponse? response)
    {
        response = null;
        if (envelope is null || envelope.PayloadType != PositionUnrealizedPnlResponsePayloadType)
            return false;
        var accountId = 0L;
        var moneyDigits = 0u;
        var seenAccount = false;
        var seenMoneyDigits = false;
        var positions = new List<CTraderPositionUnrealizedPnl>();
        try
        {
            var input = new CodedInputStream(envelope.Payload.ToByteArray());
            uint tag;
            while ((tag = input.ReadTag()) != 0)
            {
                switch (tag)
                {
                    case 16 when !seenAccount:
                        accountId = input.ReadInt64();
                        seenAccount = true;
                        break;
                    case 26:
                        if (!TryDecodePositionUnrealizedPnl(input.ReadBytes(), out var position))
                            return false;
                        positions.Add(position);
                        break;
                    case 32 when !seenMoneyDigits:
                        moneyDigits = input.ReadUInt32();
                        seenMoneyDigits = true;
                        break;
                    default:
                        input.SkipLastField();
                        break;
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return false;
        }
        if (!seenAccount || accountId <= 0 || !seenMoneyDigits ||
            positions.Any(static item => item.PositionId <= 0) ||
            positions.Select(static item => item.PositionId).Distinct().Count() != positions.Count)
        {
            return false;
        }
        response = new CTraderPositionUnrealizedPnlResponse(
            accountId,
            moneyDigits,
            Array.AsReadOnly(positions.ToArray()));
        return true;
    }

    private static bool TryDecodePositionUnrealizedPnl(
        ByteString payload,
        out CTraderPositionUnrealizedPnl position)
    {
        position = default;
        var positionId = 0L;
        var gross = 0L;
        var net = 0;
        var seenPosition = false;
        var seenGross = false;
        var seenNet = false;
        var input = new CodedInputStream(payload.ToByteArray());
        uint tag;
        while ((tag = input.ReadTag()) != 0)
        {
            switch (tag)
            {
                case 8 when !seenPosition:
                    positionId = input.ReadInt64();
                    seenPosition = true;
                    break;
                case 16 when !seenGross:
                    gross = input.ReadInt64();
                    seenGross = true;
                    break;
                case 24 when !seenNet:
                    net = input.ReadInt32();
                    seenNet = true;
                    break;
                default:
                    input.SkipLastField();
                    break;
            }
        }
        if (!seenPosition || !seenGross || !seenNet || positionId <= 0)
            return false;
        position = new CTraderPositionUnrealizedPnl(positionId, gross, net);
        return true;
    }
}

/// <summary>
/// Real cTrader transport for an already-authorized paper/live endpoint. TLS certificate validation uses the operating-system trust chain;
/// no callback, override, or plaintext fallback exists.
/// </summary>
public sealed class CTraderTlsExecutionTransport : ICTraderExecutionTransport
{
    private const int MaximumFrameLength = 8 * 1024 * 1024;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly object _stateGate = new();
    private TcpClient? _client;
    private SslStream? _stream;
    private CancellationTokenSource? _readCancellation;
    private Task? _readTask;
    private Task? _heartbeatTask;

    /// <summary>Creates a transport only for an exact endpoint token produced by the central gate.</summary>
    public CTraderTlsExecutionTransport(CTraderExecutionEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAuthorized)
            throw new InvalidOperationException("The cTrader transport requires an exact endpoint produced by the authorization gate.");
        Endpoint = endpoint;
    }

    /// <inheritdoc />
    public CTraderExecutionEndpoint Endpoint { get; }

    /// <inheritdoc />
    public bool IsConnected
    {
        get
        {
            lock (_stateGate)
                return _stream is not null && _client?.Connected == true;
        }
    }

    /// <inheritdoc />
    public event Action<ProtoMessage>? MessageReceived;

    /// <inheritdoc />
    public event Action<Exception>? Faulted;

    /// <inheritdoc />
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_stream is not null)
                throw new InvalidOperationException("The cTrader transport is already connected.");
        }

        var client = new TcpClient();
        SslStream? stream = null;
        try
        {
            await client.ConnectAsync(Endpoint.Host, Endpoint.Port, cancellationToken).ConfigureAwait(false);
            stream = new SslStream(client.GetStream(), leaveInnerStreamOpen: false);
            await stream.AuthenticateAsClientAsync(
                new SslClientAuthenticationOptions
                {
                    TargetHost = Endpoint.Host,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateRevocationCheckMode = X509RevocationMode.Online,
                },
                cancellationToken).ConfigureAwait(false);

            var readCancellation = new CancellationTokenSource();
            lock (_stateGate)
            {
                _client = client;
                _stream = stream;
                _readCancellation = readCancellation;
                _readTask = ReadLoopAsync(stream, readCancellation.Token);
                _heartbeatTask = HeartbeatLoopAsync(readCancellation.Token);
            }
        }
        catch
        {
            stream?.Dispose();
            client.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(ProtoMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var payload = message.ToByteArray();
        if (payload.Length is <= 0 or > MaximumFrameLength)
            throw new InvalidDataException("The cTrader protobuf frame length is outside the bounded transport limit.");

        SslStream stream;
        lock (_stateGate)
            stream = _stream ?? throw new InvalidOperationException("The cTrader transport is not connected.");

        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var header = new byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource? readCancellation;
        Task? readTask;
        Task? heartbeatTask;
        SslStream? stream;
        TcpClient? client;
        lock (_stateGate)
        {
            readCancellation = _readCancellation;
            readTask = _readTask;
            heartbeatTask = _heartbeatTask;
            stream = _stream;
            client = _client;
            _readCancellation = null;
            _readTask = null;
            _heartbeatTask = null;
            _stream = null;
            _client = null;
        }

        readCancellation?.Cancel();
        stream?.Dispose();
        client?.Dispose();
        if (readTask is not null)
        {
            try
            {
                await readTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The explicit disconnect cancellation is the expected read-loop exit.
            }
            catch (IOException)
            {
                // Disposing the TLS stream is the expected read-loop exit.
            }
        }
        if (heartbeatTask is not null)
        {
            try
            {
                await heartbeatTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The explicit disconnect cancellation is the expected heartbeat-loop exit.
            }
        }
        readCancellation?.Dispose();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendGate.Dispose();
    }

    private async Task ReadLoopAsync(SslStream stream, CancellationToken cancellationToken)
    {
        var header = new byte[sizeof(uint)];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, allowCleanEof: true, cancellationToken).ConfigureAwait(false))
                    throw new EndOfStreamException("The cTrader TLS peer closed the protobuf stream.");
                var length = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (length is 0 or > MaximumFrameLength)
                    throw new InvalidDataException("The cTrader peer sent an invalid protobuf frame length.");

                var rented = ArrayPool<byte>.Shared.Rent((int)length);
                try
                {
                    await ReadExactAsync(
                        stream,
                        rented.AsMemory(0, (int)length),
                        allowCleanEof: false,
                        cancellationToken).ConfigureAwait(false);
                    var envelope = ProtoMessage.Parser.ParseFrom(rented, 0, (int)length);
                    if (envelope.PayloadType == 51u)
                    {
                        await SendAsync(
                            CTraderOpenApiProtocol.Encode(new ProtoHeartbeatEvent()),
                            cancellationToken).ConfigureAwait(false);
                    }
                    MessageReceived?.Invoke(envelope);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rented, clearArray: true);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(exception);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAsync(
                    CTraderOpenApiProtocol.Encode(new ProtoHeartbeatEvent()),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Faulted?.Invoke(exception);
        }
    }

    private static async Task<bool> ReadExactAsync(
        Stream stream,
        Memory<byte> buffer,
        bool allowCleanEof,
        CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[read..], cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                if (allowCleanEof && read == 0)
                    return false;
                throw new EndOfStreamException("The cTrader peer closed a partial protobuf frame.");
            }
            read += count;
        }
        return true;
    }
}
