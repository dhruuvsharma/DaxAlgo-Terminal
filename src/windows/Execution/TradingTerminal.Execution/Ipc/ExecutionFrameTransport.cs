using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TradingTerminal.Execution.Ipc;

/// <summary>Bounded, typed frame transport used by the local execution IPC protocol.</summary>
public interface IExecutionFrameTransport : IAsyncDisposable
{
    /// <summary>Writes one complete JSON frame.</summary>
    ValueTask WriteAsync<TFrame>(TFrame frame, CancellationToken cancellationToken = default);

    /// <summary>Reads and deserializes one complete JSON frame.</summary>
    ValueTask<TFrame> ReadAsync<TFrame>(CancellationToken cancellationToken = default);
}

/// <summary>
/// Four-byte big-endian length-prefixed JSON framing over an injected duplex stream. Both reads and
/// writes are serialized independently so concurrent response/event writers cannot interleave bytes.
/// </summary>
public sealed class StreamExecutionFrameTransport : IExecutionFrameTransport
{
    /// <summary>Default maximum serialized frame size.</summary>
    public const int DefaultMaximumFrameBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly Stream _stream;
    private readonly int _maximumFrameBytes;
    private readonly bool _leaveOpen;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _disposed;

    /// <summary>Creates bounded framing over a readable and writable stream.</summary>
    public StreamExecutionFrameTransport(
        Stream stream,
        int maximumFrameBytes = DefaultMaximumFrameBytes,
        bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        if (!stream.CanRead || !stream.CanWrite)
            throw new ArgumentException("The execution frame stream must be readable and writable.", nameof(stream));
        if (maximumFrameBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumFrameBytes));

        _maximumFrameBytes = maximumFrameBytes;
        _leaveOpen = leaveOpen;
    }

    /// <inheritdoc />
    public async ValueTask WriteAsync<TFrame>(
        TFrame frame,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(frame);

        byte[] payload;
        try
        {
            payload = JsonSerializer.SerializeToUtf8Bytes(frame, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The execution IPC frame could not be serialized.", exception);
        }

        if (payload.Length == 0 || payload.Length > _maximumFrameBytes)
        {
            throw new InvalidDataException(
                $"The execution IPC frame length {payload.Length} is outside the allowed range 1..{_maximumFrameBytes}.");
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var lengthPrefix = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32BigEndian(lengthPrefix, payload.Length);
            await _stream.WriteAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask<TFrame> ReadAsync<TFrame>(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var lengthPrefix = new byte[sizeof(int)];
            await _stream.ReadExactlyAsync(lengthPrefix, cancellationToken).ConfigureAwait(false);
            var payloadLength = BinaryPrimitives.ReadInt32BigEndian(lengthPrefix);
            if (payloadLength <= 0 || payloadLength > _maximumFrameBytes)
            {
                throw new InvalidDataException(
                    $"The execution IPC frame length {payloadLength} is outside the allowed range 1..{_maximumFrameBytes}.");
            }

            var payload = new byte[payloadLength];
            await _stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            try
            {
                return JsonSerializer.Deserialize<TFrame>(payload, JsonOptions) ??
                    throw new InvalidDataException("The execution IPC frame contained JSON null.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The execution IPC frame contained invalid JSON.", exception);
            }
        }
        finally
        {
            _readGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (!_leaveOpen)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _readGate.Dispose();
        _writeGate.Dispose();
    }
}
