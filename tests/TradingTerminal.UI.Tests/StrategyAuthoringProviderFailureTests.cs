using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Authoring;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Infrastructure.Strategies;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The composer against a provider that fails AFTER the session is built.
///
/// <para><b>Why this exists alongside <see cref="StrategyAuthoringSendTests"/>.</b> Those drive a
/// builder that throws at <c>StartSession</c>, which is the shape the first-prompt bug took. It is not
/// the shape most failures take. A user with a working key hits a rate limit, a 500, a gateway that
/// returns HTML, or a connection that dies mid-answer -- all of them AFTER the session exists, in the
/// part of the turn a stub builder never reaches. The reported complaint was that the send button
/// blocked "after first prompt or any error occurs", and "any error" is this half.</para>
///
/// <para>So these use the real builder, the real client and a real socket. The only thing missing
/// versus a vendor is the vendor.</para>
/// </summary>
public sealed class StrategyAuthoringProviderFailureTests : IDisposable
{
    private readonly string _sessionDir = Path.Combine(
        Path.GetTempPath(), "daxalgo-authoring-fail-" + Guid.NewGuid().ToString("N"));

    public StrategyAuthoringProviderFailureTests() => AuthoringSessionStore.Directory = _sessionDir;

    public void Dispose()
    {
        AuthoringSessionStore.Directory = AuthoringSessionStore.DefaultDirectory;
        try { Directory.Delete(_sessionDir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData(Failure.ServerError)]
    [InlineData(Failure.Unauthorized)]
    [InlineData(Failure.RateLimited)]
    [InlineData(Failure.HtmlFromAGateway)]
    [InlineData(Failure.TruncatedBody)]
    [InlineData(Failure.ConnectionDropped)]
    public async Task A_provider_failing_mid_turn_leaves_the_composer_usable(Failure failure)
    {
        using var server = FailingProvider.Start(failure);
        var pane = Pane(server.BaseUrl);

        pane.Composer = "fade order-flow imbalance at the touch";
        await pane.SendCommand.ExecuteAsync(null);

        // The flag is the whole complaint: raised for the turn, lowered whatever the turn did.
        Assert.False(pane.IsGenerating, $"{failure} left the composer busy forever");

        pane.Composer = "try again";
        Assert.True(pane.SendCommand.CanExecute(null), $"{failure} left Send disabled");
    }

    [Theory]
    [InlineData(Failure.ServerError)]
    [InlineData(Failure.ConnectionDropped)]
    public async Task The_user_is_told_something_rather_than_nothing(Failure failure)
    {
        // A composer that silently does nothing is worse than one that says what went wrong: the user
        // retries the same prompt, pays for it again, and learns nothing.
        using var server = FailingProvider.Start(failure);
        var pane = Pane(server.BaseUrl);

        pane.Composer = "a mean-reversion strategy on the ES";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.True(
            pane.Messages.Count > 0 || !string.IsNullOrWhiteSpace(pane.AiStatus),
            $"{failure} produced no transcript entry and no status");
    }

    [Fact]
    public async Task A_healthy_provider_produces_the_file_it_wrote()
    {
        // The control for every other test here. Without it, "no file after a failure" is ambiguous
        // between a poisoned session and a fixture whose fake answer was never parseable -- and the two
        // call for opposite responses.
        using var server = FailingProvider.Start(Failure.None);
        var pane = Pane(server.BaseUrl);

        pane.Composer = "an edge kernel";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.False(pane.IsGenerating);
        Assert.Contains(pane.Files, f => f.Name == "EdgeKernel.cs");
    }

    [Fact]
    public async Task A_turn_that_fails_does_not_stop_the_next_one_from_succeeding()
    {
        // The recovery that matters. The first turn dies on a 500; the SAME pane then gets a real
        // answer back. A session poisoned by the failure would fail here too.
        using var server = FailingProvider.Start(Failure.ServerError);
        var pane = Pane(server.BaseUrl);

        pane.Composer = "first";
        await pane.SendCommand.ExecuteAsync(null);
        Assert.False(pane.IsGenerating);

        server.Behaviour = Failure.None;

        pane.Composer = "second";
        await pane.SendCommand.ExecuteAsync(null);

        Assert.False(pane.IsGenerating);

        // The code lands in Files, not in the transcript -- the message carries the prose and the
        // fenced block is extracted out of it. Asserting on Messages passed for the wrong reason on the
        // failing turns and failed for the wrong reason here.
        Assert.Contains(pane.Files, f => f.Name == "EdgeKernel.cs");
    }

    // ---- the real stack, pointed at the fake ----------------------------------------------------

    private static StrategyAuthoringViewModel Pane(string baseUrl)
    {
        var options = new AiCodegenOptions { DefaultProvider = "loopback" };
        options.Providers["loopback"] = new AiCodegenProvider
        {
            DisplayName = "Loopback",
            Kind = AiCodegenProviderKind.OpenAiCompatible,
            BaseUrl = baseUrl,
            Model = "test-model",
        };

        var compiler = new RoslynStrategyCompiler();
        var builder = new AiStrategyBuilder(
            new StrategyCodegenClientFactory(() => new HttpClient(), options, _ => "sk-loopback"),
            new StrategyCodegenOrchestrator(compiler),
            StrategyContextPack.Load(),
            options);

        return new StrategyAuthoringViewModel(
            compiler, new NullRegistry(), NullLogger<StrategyAuthoringViewModel>.Instance, builder)
        {
            StrategyId = "test-strategy",
            DisplayName = "Test strategy",
        };
    }

    public enum Failure
    {
        None,
        ServerError,
        Unauthorized,
        RateLimited,
        HtmlFromAGateway,
        TruncatedBody,
        ConnectionDropped,
    }

    /// <summary>
    /// A provider that answers badly, over real HTTP, in the ways real ones do.
    ///
    /// <para>On <see cref="HttpListener"/> rather than a raw socket. The first version of this drained
    /// one 16 KB read and replied, which works for a toy request and not for this one: the composer
    /// posts the whole system prompt, so the client was still writing when the fake closed, and every
    /// case failed with a connection reset that looked like a poisoned session.</para>
    /// </summary>
    private sealed class FailingProvider : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();

        public string BaseUrl { get; private set; } = string.Empty;

        public Failure Behaviour { get; set; }

        public static FailingProvider Start(Failure behaviour)
        {
            // Port zero would be ideal; HttpListener has no such thing, so take one the OS gave a
            // throwaway socket and reuse it. A collision retries rather than failing the suite.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = FreePort();
                var server = new FailingProvider { BaseUrl = $"http://127.0.0.1:{port}/v1", Behaviour = behaviour };
                server._listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                try { server._listener.Start(); }
                catch (HttpListenerException) { server.Dispose(); continue; }

                _ = Task.Run(server.ServeAsync);
                return server;
            }

            throw new InvalidOperationException("Could not bind a loopback port for the failing provider.");
        }

        private static int FreePort()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                try { await RespondAsync(context); }
                catch { /* a dropped client is several of these cases */ }
            }
        }

        private async Task RespondAsync(HttpListenerContext context)
        {
            using var reader = new StreamReader(context.Request.InputStream);
            var body = await reader.ReadToEndAsync();

            // The model list is asked for separately and is not what these tests are about.
            if (context.Request.Url!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
            {
                await WriteAsync(context, HttpStatusCode.OK, "application/json",
                    """{"data":[{"id":"test-model"}]}""");
                return;
            }

            switch (Behaviour)
            {
                case Failure.ConnectionDropped:
                    // A real mid-answer death: the socket goes away with no status at all.
                    context.Response.Abort();
                    return;

                case Failure.ServerError:
                    await WriteAsync(context, HttpStatusCode.InternalServerError, "application/json",
                        """{"error":{"message":"upstream exploded"}}""");
                    return;

                case Failure.Unauthorized:
                    await WriteAsync(context, HttpStatusCode.Unauthorized, "application/json",
                        """{"error":{"message":"invalid api key"}}""");
                    return;

                case Failure.RateLimited:
                    await WriteAsync(context, HttpStatusCode.TooManyRequests, "application/json",
                        """{"error":{"message":"rate limit reached"}}""");
                    return;

                case Failure.HtmlFromAGateway:
                    // What a proxy in front of a provider returns. It is not JSON at all, and the
                    // status is the only hint.
                    await WriteAsync(context, HttpStatusCode.BadGateway, "text/html",
                        "<html><body><h1>502 Bad Gateway</h1></body></html>");
                    return;

                case Failure.TruncatedBody:
                    // A 200 whose JSON stops mid-object. Nothing about the status says so, which is
                    // what earns it a case of its own.
                    await WriteAsync(context, HttpStatusCode.OK, "application/json",
                        """{"choices":[{"message":{"content":"```csharp""");
                    return;

                default:
                    // Answer in the shape actually requested. The composer streams, and a fake that
                    // only knows one shape returns 200 with nothing usable in it.
                    var streaming = body.Contains("\"stream\":true", StringComparison.Ordinal)
                        || body.Contains("\"stream\": true", StringComparison.Ordinal);

                    if (streaming)
                        await WriteAsync(context, HttpStatusCode.OK, "text/event-stream", StreamedAnswer);
                    else
                        await WriteAsync(context, HttpStatusCode.OK, "application/json", Answer);

                    return;
            }
        }

        private static async Task WriteAsync(
            HttpListenerContext context, HttpStatusCode status, string contentType, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = contentType;
            context.Response.ContentLength64 = bytes.Length;

            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        /// <summary>A minimal but genuinely compilable kernel, so a healthy turn proves the session
        /// works end to end rather than merely not throwing.</summary>
        private const string Reply = """
            Here you go.

            ```csharp
            // file: EdgeKernel.cs
            public sealed class EdgeKernel : IStrategyKernel
            {
                public StrategyParameterSchema Schema { get; } = new();

                public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

                public Task OnStartAsync(IStrategyContext context, CancellationToken ct) =>
                    Task.CompletedTask;
            }
            ```
            """;

        private static readonly string Answer = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = Reply } } },
            usage = new { prompt_tokens = 10, completion_tokens = 20 },
        });

        /// <summary>The same answer as one SSE frame, then the sentinel the client stops on.</summary>
        private static readonly string StreamedAnswer =
            "data: " + JsonSerializer.Serialize(new
            {
                choices = new[] { new { delta = new { content = Reply } } },
            }) + "\n\ndata: [DONE]\n\n";

        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Close(); } catch { /* best effort */ }
            _stopping.Dispose();
        }
    }

    private sealed class NullRegistry : IStrategyRegistry
    {
        public IReadOnlyList<StrategyCatalogEntry> All => [];

        public event EventHandler? Changed;

        public StrategyCatalogEntry? Find(string id) => null;

        public void Register(StrategyCatalogEntry entry) => Changed?.Invoke(this, EventArgs.Empty);

        public bool Remove(string id) => false;
    }
}
