using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using TradingTerminal.Execution.Ipc;

namespace TradingTerminal.Execution.Tests;

public sealed class ExecutionPipeAuthenticationTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Mutual_handshake_succeeds_over_current_user_named_pipe()
    {
        var secret = Secret(0x41);
        var (serverTransport, clientTransport) = await ConnectAsync();
        await using var server = serverTransport;
        await using var client = clientTransport;
        using var serverAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x51));
        using var clientAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x61));
        using var timeout = new CancellationTokenSource(TestTimeout);

        var serverTask = serverAuthenticator.AuthenticateServerAsync(server, timeout.Token).AsTask();
        var clientTask = clientAuthenticator.AuthenticateClientAsync(client, timeout.Token).AsTask();
        var results = await Task.WhenAll(serverTask, clientTask);
        var serverResult = results[0];
        var clientResult = results[1];

        Assert.True(serverResult.IsAuthenticated);
        Assert.True(clientResult.IsAuthenticated);
        Assert.Equal(ExecutionIpcProtocol.Version1, serverResult.NegotiatedVersion);
        Assert.Equal(ExecutionIpcProtocol.Version1, clientResult.NegotiatedVersion);
    }

    [Fact]
    public async Task Bad_client_proof_fails_closed_and_is_logged()
    {
        var secret = Secret(0x42);
        var serverLog = new ConcurrentQueue<string>();
        var (serverTransport, rawClientTransport) = await ConnectAsync();
        await using var server = serverTransport;
        await using var client = new CorruptClientProofTransport(rawClientTransport);
        using var serverAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x52),
            log: serverLog.Enqueue);
        using var clientAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x62));
        using var timeout = new CancellationTokenSource(TestTimeout);

        var serverTask = serverAuthenticator.AuthenticateServerAsync(server, timeout.Token).AsTask();
        var clientTask = clientAuthenticator.AuthenticateClientAsync(client, timeout.Token).AsTask();
        var results = await Task.WhenAll(serverTask, clientTask);
        var serverResult = results[0];
        var clientResult = results[1];

        Assert.False(serverResult.IsAuthenticated);
        Assert.Equal(ExecutionHandshakeFailure.AuthenticationFailed, serverResult.Failure);
        Assert.False(clientResult.IsAuthenticated);
        Assert.Equal(ExecutionHandshakeFailure.AuthenticationFailed, clientResult.Failure);
        Assert.Contains(serverLog, item => item.Contains("client proof was invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Absent_client_proof_fails_closed_and_is_logged()
    {
        var secret = Secret(0x43);
        var serverLog = new ConcurrentQueue<string>();
        var (serverTransport, rawClientTransport) = await ConnectAsync();
        await using var server = serverTransport;
        await using var client = new DropClientProofTransport(rawClientTransport);
        using var serverAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x53),
            log: serverLog.Enqueue);
        using var clientAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x63));
        using var timeout = new CancellationTokenSource(TestTimeout);

        var serverTask = serverAuthenticator.AuthenticateServerAsync(server, timeout.Token).AsTask();
        var clientTask = clientAuthenticator.AuthenticateClientAsync(client, timeout.Token).AsTask();
        var results = await Task.WhenAll(serverTask, clientTask);
        var serverResult = results[0];
        var clientResult = results[1];

        Assert.False(serverResult.IsAuthenticated);
        Assert.Equal(ExecutionHandshakeFailure.AuthenticationFailed, serverResult.Failure);
        Assert.False(clientResult.IsAuthenticated);
        Assert.Contains(serverLog, item => item.Contains("client proof was absent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Bad_service_proof_fails_closed_before_client_proof()
    {
        var secret = Secret(0x44);
        var clientLog = new ConcurrentQueue<string>();
        var (serverTransport, rawClientTransport) = await ConnectAsync();
        await using var server = serverTransport;
        var client = new CorruptServerProofTransport(rawClientTransport);
        using var serverAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x54));
        using var clientAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x64),
            log: clientLog.Enqueue);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var serverTask = serverAuthenticator.AuthenticateServerAsync(server, timeout.Token).AsTask();
        var clientResult = await clientAuthenticator.AuthenticateClientAsync(client, timeout.Token);
        await client.DisposeAsync();
        var serverResult = await serverTask;

        Assert.False(clientResult.IsAuthenticated);
        Assert.Equal(ExecutionHandshakeFailure.AuthenticationFailed, clientResult.Failure);
        Assert.False(serverResult.IsAuthenticated);
        Assert.Contains(clientLog, item => item.Contains("service proof was invalid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Authenticated_protocol_version_mismatch_fails_closed_with_clear_reason()
    {
        var secret = Secret(0x45);
        var (serverTransport, clientTransport) = await ConnectAsync();
        await using var server = serverTransport;
        await using var client = clientTransport;
        using var serverAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x55),
            protocolVersion: ExecutionIpcProtocol.Version1);
        using var clientAuthenticator = new ExecutionPipeAuthenticator(
            secret,
            new FixedNonceSource(0x65),
            protocolVersion: ExecutionIpcProtocol.Version1 + 1);
        using var timeout = new CancellationTokenSource(TestTimeout);

        var serverTask = serverAuthenticator.AuthenticateServerAsync(server, timeout.Token).AsTask();
        var clientTask = clientAuthenticator.AuthenticateClientAsync(client, timeout.Token).AsTask();
        var results = await Task.WhenAll(serverTask, clientTask);
        var serverResult = results[0];
        var clientResult = results[1];

        Assert.Equal(ExecutionHandshakeFailure.ProtocolVersionMismatch, serverResult.Failure);
        Assert.Equal(ExecutionHandshakeFailure.ProtocolVersionMismatch, clientResult.Failure);
        Assert.Contains("Client requested 2; service requires 1", serverResult.Reason, StringComparison.Ordinal);
        Assert.Contains("Client requested 2; service requires 1", clientResult.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Dpapi_secret_is_stable_and_not_stored_as_plaintext()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"daxalgo-execution-secret-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "service-secret.dpapi");
        try
        {
            var store = new DpapiExecutionServiceSecretStore(path);

            var first = store.LoadOrCreate();
            var second = store.LoadOrCreate();
            var protectedBytes = File.ReadAllBytes(path);

            Assert.Equal(DpapiExecutionServiceSecretStore.SecretSize, first.Length);
            Assert.Equal(first, second);
            Assert.False(first.AsSpan().SequenceEqual(protectedBytes));
        }
        finally
        {
            File.Delete(path);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Frame_transport_rejects_an_oversized_payload_before_writing()
    {
        await using var transport = new StreamExecutionFrameTransport(
            new MemoryStream(),
            maximumFrameBytes: 32);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await transport.WriteAsync(new OversizedFrame(new string('x', 128))));
    }

    [Fact]
    public void Pipe_dacl_contains_only_the_current_user_allow_rule()
    {
        using var pipe = SecureExecutionNamedPipe.CreateServer(PipeName());
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = Assert.IsType<SecurityIdentifier>(identity.User);
        var security = pipe.GetAccessControl();
        var rules = security
            .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<PipeAccessRule>()
            .ToArray();

        var rule = Assert.Single(rules);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(currentUser, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.True(rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite));
    }

    [Fact]
    public async Task Pipe_rejects_a_remote_style_machine_name_client()
    {
        var pipeName = PipeName();
        using var server = SecureExecutionNamedPipe.CreateServer(pipeName);
        using var remoteStyleClient = new NamedPipeClientStream(
            Environment.MachineName,
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var failure = await Record.ExceptionAsync(async () =>
            await remoteStyleClient.ConnectAsync(timeout.Token));

        Assert.NotNull(failure);
        Assert.False(remoteStyleClient.IsConnected);
        Assert.False(server.IsConnected);
    }

    private static async Task<(StreamExecutionFrameTransport Server, StreamExecutionFrameTransport Client)>
        ConnectAsync()
        => await ConnectAsync(PipeName());

    private static async Task<(StreamExecutionFrameTransport Server, StreamExecutionFrameTransport Client)>
        ConnectAsync(string pipeName)
    {
        var serverPipe = SecureExecutionNamedPipe.CreateServer(pipeName);
        var clientPipe = SecureExecutionNamedPipe.CreateLocalClient(pipeName);
        using var timeout = new CancellationTokenSource(TestTimeout);
        try
        {
            var waitForConnection = serverPipe.WaitForConnectionAsync(timeout.Token);
            await clientPipe.ConnectAsync(timeout.Token);
            await waitForConnection;
            return (
                new StreamExecutionFrameTransport(serverPipe),
                new StreamExecutionFrameTransport(clientPipe));
        }
        catch
        {
            serverPipe.Dispose();
            clientPipe.Dispose();
            throw;
        }
    }

    private static string PipeName() => $"DaxAlgo.Execution.Tests.{Guid.NewGuid():N}";

    private static byte[] Secret(byte value) =>
        Enumerable.Repeat(value, DpapiExecutionServiceSecretStore.SecretSize).ToArray();

    private sealed record OversizedFrame(string Value);

    private sealed class FixedNonceSource(byte value) : IExecutionNonceSource
    {
        public byte[] CreateNonce(int length) => Enumerable.Repeat(value, length).ToArray();
    }

    private abstract class DelegatingTransport(IExecutionFrameTransport inner) : IExecutionFrameTransport
    {
        protected IExecutionFrameTransport Inner { get; } = inner;

        public virtual ValueTask WriteAsync<TFrame>(TFrame frame, CancellationToken cancellationToken = default) =>
            Inner.WriteAsync(frame, cancellationToken);

        public virtual ValueTask<TFrame> ReadAsync<TFrame>(CancellationToken cancellationToken = default) =>
            Inner.ReadAsync<TFrame>(cancellationToken);

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class CorruptClientProofTransport(IExecutionFrameTransport inner)
        : DelegatingTransport(inner)
    {
        public override ValueTask WriteAsync<TFrame>(TFrame frame, CancellationToken cancellationToken = default)
        {
            if (frame is ExecutionClientProof)
            {
                return Inner.WriteAsync(
                    (TFrame)(object)new ExecutionClientProof(new byte[ExecutionIpcProtocol.ProofSize]),
                    cancellationToken);
            }
            return Inner.WriteAsync(frame, cancellationToken);
        }
    }

    private sealed class DropClientProofTransport(IExecutionFrameTransport inner)
        : DelegatingTransport(inner)
    {
        public override async ValueTask WriteAsync<TFrame>(
            TFrame frame,
            CancellationToken cancellationToken = default)
        {
            if (frame is ExecutionClientProof)
            {
                await Inner.DisposeAsync();
                throw new IOException("The test client disconnected before sending its proof.");
            }
            await Inner.WriteAsync(frame, cancellationToken);
        }
    }

    private sealed class CorruptServerProofTransport(IExecutionFrameTransport inner)
        : DelegatingTransport(inner)
    {
        public override async ValueTask<TFrame> ReadAsync<TFrame>(CancellationToken cancellationToken = default)
        {
            var frame = await Inner.ReadAsync<TFrame>(cancellationToken);
            if (frame is ExecutionServerChallenge challenge)
            {
                return (TFrame)(object)(challenge with
                {
                    ServerProof = new byte[ExecutionIpcProtocol.ProofSize],
                });
            }
            return frame;
        }
    }
}
