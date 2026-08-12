using System.IO.Pipes;
using System.Security.Cryptography;
using TradingTerminal.Execution.Service;

namespace TradingTerminal.Execution.Ipc;

/// <summary>
/// Named-pipe-only execution-service endpoint. Authentication is repeated for every accepted local
/// connection; disconnecting a client never disposes the engine or abandons working orders.
/// </summary>
public sealed class ExecutionNamedPipeServer : IDisposable
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly ExecutionServiceEngine _engine;
    private readonly byte[] _secret;
    private readonly string _pipeName;
    private readonly IExecutionNonceSource _nonceSource;
    private readonly Action<string>? _log;
    private readonly TimeSpan _handshakeTimeout;
    private bool _disposed;

    /// <summary>Creates a local server from the DPAPI-backed per-service secret.</summary>
    public ExecutionNamedPipeServer(
        ExecutionServiceEngine engine,
        IExecutionServiceSecretStore secretStore,
        string pipeName = SecureExecutionNamedPipe.DefaultPipeName,
        IExecutionNonceSource? nonceSource = null,
        Action<string>? log = null,
        TimeSpan? handshakeTimeout = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        ArgumentNullException.ThrowIfNull(secretStore);
        _secret = secretStore.LoadOrCreate();
        if (_secret.Length != DpapiExecutionServiceSecretStore.SecretSize)
            throw new InvalidDataException("The execution service secret has an invalid length.");
        _pipeName = pipeName;
        _nonceSource = nonceSource ?? CryptographicExecutionNonceSource.Instance;
        _log = log;
        _handshakeTimeout = ValidateHandshakeTimeout(handshakeTimeout);
    }

    /// <summary>Accepts local clients until cancellation; each reconnect performs a fresh handshake.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunOneConnectionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log?.Invoke($"Execution pipe connection failed closed: {exception.Message}");
            }
        }
    }

    /// <summary>Accepts and serves exactly one local connection; primarily used by the real-pipe test.</summary>
    public async Task<ExecutionHandshakeResult> RunOneConnectionAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var pipe = SecureExecutionNamedPipe.CreateServer(_pipeName);
        await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ServeConnectedPipeAsync(pipe, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_secret);
    }

    private async Task<ExecutionHandshakeResult> ServeConnectedPipeAsync(
        NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using var transport = new StreamExecutionFrameTransport(pipe, leaveOpen: true);
        using var authenticator = new ExecutionPipeAuthenticator(
            _secret,
            _nonceSource,
            ExecutionIpcProtocol.Version1,
            _log);
        ExecutionHandshakeResult handshake;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            deadline.CancelAfter(_handshakeTimeout);
            try
            {
                handshake = await authenticator.AuthenticateServerAsync(
                    transport,
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
            {
                const string reason =
                    "Execution pipe authentication timed out because the client proof was absent.";
                TryLog(_log, reason);
                handshake = new ExecutionHandshakeResult(
                    ExecutionHandshakeFailure.AuthenticationFailed,
                    0,
                    reason);
            }
        }
        if (!handshake.IsAuthenticated)
            return handshake;

        while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
        {
            ExecutionServiceRequest request;
            try
            {
                request = await transport.ReadAsync<ExecutionServiceRequest>(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }
            catch (InvalidDataException exception)
            {
                _log?.Invoke($"Execution pipe request failed closed: {exception.Message}");
                break;
            }

            var exchange = _engine.Handle(request);
            await transport.WriteAsync(exchange.Response, cancellationToken).ConfigureAwait(false);
            foreach (var executionEvent in exchange.Events)
                await transport.WriteAsync(executionEvent, cancellationToken).ConfigureAwait(false);
        }

        return handshake;
    }

    private static TimeSpan ValidateHandshakeTimeout(TimeSpan? handshakeTimeout)
    {
        var value = handshakeTimeout ?? DefaultHandshakeTimeout;
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        return value;
    }

    private static void TryLog(Action<string>? log, string reason)
    {
        try
        {
            log?.Invoke(reason);
        }
        catch
        {
            // Diagnostic sinks must never turn authentication failure into an accepted connection.
        }
    }
}

/// <summary>Authenticated desktop-side client API for a local execution-service process.</summary>
public sealed class ExecutionNamedPipeClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamExecutionFrameTransport _transport;
    private bool _disposed;

    private ExecutionNamedPipeClient(
        NamedPipeClientStream pipe,
        StreamExecutionFrameTransport transport)
    {
        _pipe = pipe;
        _transport = transport;
    }

    /// <summary>
    /// Connects only to <c>.</c> (the local machine), proves the DPAPI-backed secret, and negotiates
    /// protocol version 1. Any authentication or version failure closes the pipe.
    /// </summary>
    public static async Task<ExecutionNamedPipeClient> ConnectAsync(
        IExecutionServiceSecretStore secretStore,
        string pipeName = SecureExecutionNamedPipe.DefaultPipeName,
        IExecutionNonceSource? nonceSource = null,
        int protocolVersion = ExecutionIpcProtocol.Version1,
        Action<string>? log = null,
        TimeSpan? handshakeTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secretStore);
        var authenticationTimeout = ValidateHandshakeTimeout(handshakeTimeout);
        NamedPipeClientStream? pipe = null;
        StreamExecutionFrameTransport? transport = null;
        var secret = secretStore.LoadOrCreate();
        try
        {
            pipe = SecureExecutionNamedPipe.CreateLocalClient(pipeName);
            ExecutionHandshakeResult handshake;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(authenticationTimeout);
                try
                {
                    await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                    transport = new StreamExecutionFrameTransport(pipe, leaveOpen: true);
                    using var authenticator = new ExecutionPipeAuthenticator(
                        secret,
                        nonceSource,
                        protocolVersion,
                        log);
                    handshake = await authenticator.AuthenticateClientAsync(
                            transport,
                            deadline.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
                {
                    const string reason =
                        "Execution pipe authentication timed out because the local service connection or proof was absent.";
                    TryLog(log, reason);
                    handshake = new ExecutionHandshakeResult(
                        ExecutionHandshakeFailure.AuthenticationFailed,
                        0,
                        reason);
                }
            }
            if (!handshake.IsAuthenticated)
            {
                throw new InvalidOperationException(
                    $"Execution pipe authentication failed ({handshake.Failure}): {handshake.Reason}");
            }

            return new ExecutionNamedPipeClient(
                pipe,
                transport ?? throw new InvalidOperationException("The authenticated pipe transport was absent."));
        }
        catch
        {
            if (transport is not null)
                await transport.DisposeAsync().ConfigureAwait(false);
            pipe?.Dispose();
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Sends one command and reads its exact request-correlated response/event batch.</summary>
    public async Task<ExecutionServiceExchange> ExchangeAsync(
        ExecutionServiceRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        await _transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        var response = await _transport.ReadAsync<ExecutionServiceResponse>(
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(response.RequestId, request.RequestId, StringComparison.Ordinal) ||
            response.ProtocolVersion != ExecutionServiceProtocol.CurrentVersion ||
            response.EventCount < 0 ||
            response.EventCount > 256)
        {
            throw new InvalidDataException("The execution service returned an invalid response envelope.");
        }

        var events = new ExecutionServiceEvent[response.EventCount];
        var previousSequence = request.AfterOutboxSequence;
        for (var index = 0; index < events.Length; index++)
        {
            var executionEvent = await _transport.ReadAsync<ExecutionServiceEvent>(
                cancellationToken).ConfigureAwait(false);
            if (executionEvent.Event is null || executionEvent.OutboxSequence <= previousSequence)
                throw new InvalidDataException("The execution service event stream is absent or out of order.");
            events[index] = executionEvent;
            previousSequence = executionEvent.OutboxSequence;
        }
        if (response.LastOutboxSequence != previousSequence)
            throw new InvalidDataException("The response outbox cursor does not match its event stream.");

        return new ExecutionServiceExchange(response, Array.AsReadOnly(events));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await _transport.DisposeAsync().ConfigureAwait(false);
        _pipe.Dispose();
    }

    private static TimeSpan ValidateHandshakeTimeout(TimeSpan? handshakeTimeout)
    {
        var value = handshakeTimeout ?? DefaultHandshakeTimeout;
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout));
        return value;
    }

    private static void TryLog(Action<string>? log, string reason)
    {
        try
        {
            log?.Invoke(reason);
        }
        catch
        {
            // Diagnostic sinks must never turn authentication failure into an accepted connection.
        }
    }
}
