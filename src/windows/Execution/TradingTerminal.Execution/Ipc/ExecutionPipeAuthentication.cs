using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace TradingTerminal.Execution.Ipc;

/// <summary>Stable local execution IPC protocol constants.</summary>
public static class ExecutionIpcProtocol
{
    /// <summary>The only protocol version accepted by this slice.</summary>
    public const int Version1 = 1;

    /// <summary>Fresh random challenge size for each side of every connection.</summary>
    public const int NonceSize = 32;

    /// <summary>HMAC-SHA256 proof size.</summary>
    public const int ProofSize = 32;
}

/// <summary>Produces a fresh cryptographic nonce for one side of a connection.</summary>
public interface IExecutionNonceSource
{
    /// <summary>Creates exactly <paramref name="length"/> fresh random bytes.</summary>
    byte[] CreateNonce(int length);
}

/// <summary>Operating-system cryptographic nonce source.</summary>
public sealed class CryptographicExecutionNonceSource : IExecutionNonceSource
{
    /// <summary>Shared stateless instance.</summary>
    public static CryptographicExecutionNonceSource Instance { get; } = new();

    private CryptographicExecutionNonceSource()
    {
    }

    /// <inheritdoc />
    public byte[] CreateNonce(int length)
    {
        if (length <= 0)
            throw new ArgumentOutOfRangeException(nameof(length));
        return RandomNumberGenerator.GetBytes(length);
    }
}

/// <summary>Stable handshake failure categories.</summary>
public enum ExecutionHandshakeFailure : byte
{
    /// <summary>The peer proved possession and protocol negotiation succeeded.</summary>
    None = 0,

    /// <summary>The authenticated peers do not support the same protocol version.</summary>
    ProtocolVersionMismatch = 1,

    /// <summary>A proof was absent, malformed, or did not match.</summary>
    AuthenticationFailed = 2,

    /// <summary>The ordered handshake transcript was malformed.</summary>
    InvalidHandshake = 3,

    /// <summary>The underlying local transport closed or failed.</summary>
    TransportFailed = 4,
}

/// <summary>Fail-closed result of one connection handshake.</summary>
public readonly record struct ExecutionHandshakeResult(
    ExecutionHandshakeFailure Failure,
    int NegotiatedVersion,
    string? Reason)
{
    /// <summary>Gets whether the connection may proceed to execution IPC messages.</summary>
    public bool IsAuthenticated => Failure == ExecutionHandshakeFailure.None;

    /// <summary>Successful version-1 result.</summary>
    public static ExecutionHandshakeResult Authenticated =>
        new(ExecutionHandshakeFailure.None, ExecutionIpcProtocol.Version1, null);
}

/// <summary>First frame sent by the desktop control plane.</summary>
public sealed record ExecutionClientHello(int ProtocolVersion, byte[] ClientNonce);

/// <summary>Authenticated service challenge binding both nonces and the version decision.</summary>
public sealed record ExecutionServerChallenge(
    int ServerProtocolVersion,
    bool VersionAccepted,
    byte[] ServerNonce,
    byte[] ServerProof,
    string? FailureReason);

/// <summary>Desktop proof of the per-service secret.</summary>
public sealed record ExecutionClientProof(byte[] Proof);

/// <summary>Final fail-closed decision sent by the service.</summary>
public sealed record ExecutionHandshakeCompletion(
    bool Accepted,
    int ProtocolVersion,
    string? FailureReason);

/// <summary>
/// Mutual per-connection nonce/HMAC authentication. Direction-separated HMAC transcripts bind both
/// fresh nonces and the exact protocol negotiation, preventing reflection and version substitution.
/// </summary>
public sealed class ExecutionPipeAuthenticator : IDisposable
{
    private static readonly byte[] ServerProofDomain =
        Encoding.UTF8.GetBytes("DaxAlgo.Execution.IPC.Handshake.v1/server-proof");
    private static readonly byte[] ClientProofDomain =
        Encoding.UTF8.GetBytes("DaxAlgo.Execution.IPC.Handshake.v1/client-proof");

    private readonly byte[] _secret;
    private readonly IExecutionNonceSource _nonceSource;
    private readonly int _protocolVersion;
    private readonly Action<string>? _log;
    private bool _disposed;

    /// <summary>Creates a versioned authenticator over one unprotected 32-byte service secret.</summary>
    public ExecutionPipeAuthenticator(
        byte[] secret,
        IExecutionNonceSource? nonceSource = null,
        int protocolVersion = ExecutionIpcProtocol.Version1,
        Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(secret);
        if (secret.Length != DpapiExecutionServiceSecretStore.SecretSize)
            throw new ArgumentException("The execution service secret must contain exactly 32 bytes.", nameof(secret));
        if (protocolVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(protocolVersion));

        _secret = (byte[])secret.Clone();
        _nonceSource = nonceSource ?? CryptographicExecutionNonceSource.Instance;
        _protocolVersion = protocolVersion;
        _log = log;
    }

    /// <summary>Authenticates a service-side accepted pipe connection.</summary>
    public async ValueTask<ExecutionHandshakeResult> AuthenticateServerAsync(
        IExecutionFrameTransport transport,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transport);

        try
        {
            var hello = await transport.ReadAsync<ExecutionClientHello>(cancellationToken).ConfigureAwait(false);
            if (!IsNonceValid(hello.ClientNonce) || hello.ProtocolVersion <= 0)
                return Fail(ExecutionHandshakeFailure.InvalidHandshake, "The client hello was invalid.");

            var serverNonce = CreateNonce();
            var versionAccepted = hello.ProtocolVersion == _protocolVersion;
            var mismatchReason = versionAccepted
                ? null
                : $"Protocol version mismatch. Client requested {hello.ProtocolVersion}; service requires {_protocolVersion}.";
            var serverProof = ComputeProof(
                ServerProofDomain,
                hello.ProtocolVersion,
                _protocolVersion,
                versionAccepted,
                hello.ClientNonce,
                serverNonce);
            await transport.WriteAsync(
                new ExecutionServerChallenge(
                    _protocolVersion,
                    versionAccepted,
                    serverNonce,
                    serverProof,
                    mismatchReason),
                cancellationToken).ConfigureAwait(false);

            ExecutionClientProof clientProof;
            try
            {
                clientProof = await transport.ReadAsync<ExecutionClientProof>(cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return Fail(ExecutionHandshakeFailure.AuthenticationFailed, "The client proof was absent.");
            }

            var expectedClientProof = ComputeProof(
                ClientProofDomain,
                hello.ProtocolVersion,
                _protocolVersion,
                versionAccepted,
                hello.ClientNonce,
                serverNonce);
            var clientProofValid = IsProofValid(clientProof.Proof) &&
                CryptographicOperations.FixedTimeEquals(clientProof.Proof, expectedClientProof);
            CryptographicOperations.ZeroMemory(expectedClientProof);
            if (!clientProofValid)
            {
                await TryWriteCompletionAsync(
                    transport,
                    accepted: false,
                    "Authentication failed.",
                    cancellationToken).ConfigureAwait(false);
                return Fail(ExecutionHandshakeFailure.AuthenticationFailed, "The client proof was invalid.");
            }

            if (!versionAccepted)
            {
                await TryWriteCompletionAsync(
                    transport,
                    accepted: false,
                    mismatchReason,
                    cancellationToken).ConfigureAwait(false);
                return Fail(ExecutionHandshakeFailure.ProtocolVersionMismatch, mismatchReason!);
            }

            await transport.WriteAsync(
                new ExecutionHandshakeCompletion(true, _protocolVersion, null),
                cancellationToken).ConfigureAwait(false);
            return new ExecutionHandshakeResult(ExecutionHandshakeFailure.None, _protocolVersion, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProtocolOrTransportFailure(exception))
        {
            return Fail(ExecutionHandshakeFailure.TransportFailed, "The server handshake transport failed.");
        }
    }

    /// <summary>Authenticates the desktop side of one local service connection.</summary>
    public async ValueTask<ExecutionHandshakeResult> AuthenticateClientAsync(
        IExecutionFrameTransport transport,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(transport);

        try
        {
            var clientNonce = CreateNonce();
            await transport.WriteAsync(
                new ExecutionClientHello(_protocolVersion, clientNonce),
                cancellationToken).ConfigureAwait(false);
            var challenge = await transport.ReadAsync<ExecutionServerChallenge>(cancellationToken).ConfigureAwait(false);
            if (!IsNonceValid(challenge.ServerNonce) ||
                !IsProofValid(challenge.ServerProof) ||
                challenge.ServerProtocolVersion <= 0 ||
                challenge.VersionAccepted != (_protocolVersion == challenge.ServerProtocolVersion))
            {
                return Fail(ExecutionHandshakeFailure.InvalidHandshake, "The service challenge was invalid.");
            }

            var expectedServerProof = ComputeProof(
                ServerProofDomain,
                _protocolVersion,
                challenge.ServerProtocolVersion,
                challenge.VersionAccepted,
                clientNonce,
                challenge.ServerNonce);
            var serverProofValid =
                CryptographicOperations.FixedTimeEquals(challenge.ServerProof, expectedServerProof);
            CryptographicOperations.ZeroMemory(expectedServerProof);
            if (!serverProofValid)
                return Fail(ExecutionHandshakeFailure.AuthenticationFailed, "The service proof was invalid.");

            var clientProof = ComputeProof(
                ClientProofDomain,
                _protocolVersion,
                challenge.ServerProtocolVersion,
                challenge.VersionAccepted,
                clientNonce,
                challenge.ServerNonce);
            await transport.WriteAsync(new ExecutionClientProof(clientProof), cancellationToken).ConfigureAwait(false);
            var completion = await transport.ReadAsync<ExecutionHandshakeCompletion>(cancellationToken).ConfigureAwait(false);
            if (completion.ProtocolVersion != challenge.ServerProtocolVersion)
                return Fail(ExecutionHandshakeFailure.InvalidHandshake, "The handshake completion changed protocol version.");

            if (!challenge.VersionAccepted)
            {
                if (completion.Accepted)
                    return Fail(ExecutionHandshakeFailure.InvalidHandshake, "The service accepted a mismatched protocol version.");
                return Fail(
                    ExecutionHandshakeFailure.ProtocolVersionMismatch,
                    challenge.FailureReason ?? completion.FailureReason ?? "Protocol version mismatch.");
            }

            if (!completion.Accepted)
                return Fail(ExecutionHandshakeFailure.AuthenticationFailed, "The service rejected client authentication.");

            return new ExecutionHandshakeResult(
                ExecutionHandshakeFailure.None,
                challenge.ServerProtocolVersion,
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsProtocolOrTransportFailure(exception))
        {
            return Fail(ExecutionHandshakeFailure.TransportFailed, "The client handshake transport failed.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_secret);
    }

    private byte[] CreateNonce()
    {
        var nonce = _nonceSource.CreateNonce(ExecutionIpcProtocol.NonceSize);
        if (!IsNonceValid(nonce))
            throw new CryptographicException("The nonce source returned an invalid nonce length.");
        return nonce;
    }

    private byte[] ComputeProof(
        byte[] domain,
        int clientVersion,
        int serverVersion,
        bool versionAccepted,
        byte[] clientNonce,
        byte[] serverNonce)
    {
        var transcript = new byte[
            sizeof(int) + domain.Length +
            sizeof(int) + sizeof(int) + sizeof(byte) +
            clientNonce.Length + serverNonce.Length];
        var offset = 0;
        BinaryPrimitives.WriteInt32BigEndian(transcript.AsSpan(offset, sizeof(int)), domain.Length);
        offset += sizeof(int);
        domain.CopyTo(transcript, offset);
        offset += domain.Length;
        BinaryPrimitives.WriteInt32BigEndian(transcript.AsSpan(offset, sizeof(int)), clientVersion);
        offset += sizeof(int);
        BinaryPrimitives.WriteInt32BigEndian(transcript.AsSpan(offset, sizeof(int)), serverVersion);
        offset += sizeof(int);
        transcript[offset++] = versionAccepted ? (byte)1 : (byte)0;
        clientNonce.CopyTo(transcript, offset);
        offset += clientNonce.Length;
        serverNonce.CopyTo(transcript, offset);
        try
        {
            return HMACSHA256.HashData(_secret, transcript);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(transcript);
        }
    }

    private async ValueTask TryWriteCompletionAsync(
        IExecutionFrameTransport transport,
        bool accepted,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            await transport.WriteAsync(
                new ExecutionHandshakeCompletion(accepted, _protocolVersion, reason),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsProtocolOrTransportFailure(exception))
        {
            // The connection is already rejected. Preserve the original authentication outcome.
        }
    }

    private ExecutionHandshakeResult Fail(ExecutionHandshakeFailure failure, string reason)
    {
        try
        {
            _log?.Invoke(reason);
        }
        catch
        {
            // Diagnostic sinks must never turn authentication failure into an accepted connection.
        }
        return new ExecutionHandshakeResult(failure, 0, reason);
    }

    private static bool IsNonceValid(byte[]? nonce) =>
        nonce is { Length: ExecutionIpcProtocol.NonceSize };

    private static bool IsProofValid(byte[]? proof) =>
        proof is { Length: ExecutionIpcProtocol.ProofSize };

    private static bool IsProtocolOrTransportFailure(Exception exception) =>
        exception is IOException or InvalidDataException or CryptographicException;
}
