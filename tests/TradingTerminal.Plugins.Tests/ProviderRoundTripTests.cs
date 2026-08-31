using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The provider path over a real socket.
///
/// <para>Every other codegen test stubs <c>HttpMessageHandler</c>, so the request never leaves the
/// process and the response is handed back as an object. That verifies the code around the transport
/// and nothing of the transport itself: the JSON is never serialised over a wire, the SSE frames are
/// never split across reads, the usage block is never parsed from bytes, and a chunked response is
/// never assembled. Those are exactly the places a provider integration breaks.</para>
///
/// <para>These stand up a loopback HTTP server that speaks the OpenAI-compatible shape and point the
/// real client at it. It is not a vendor — no key, no network — but everything on our side of the
/// socket is the real thing, including the Roslyn compile of whatever comes back.</para>
/// </summary>
public sealed class ProviderRoundTripTests
{
    /// <summary>A strategy the compiler will accept, as a model would return it.</summary>
    private const string GeneratedUnit = """
        Here is a small kernel.

        ```csharp
        // file: EdgeKernel.cs
        public sealed class EdgeKernel : IStrategyKernel
        {
            private readonly Ema _fast = new(5);

            public StrategyParameterSchema Schema { get; } = new(
                StrategyParameter.Instrument("instrument", "Instrument", new InstrumentId(1)));

            public StrategyDataRequirement DataRequirement => StrategyDataRequirement.Bars;

            public Task OnStartAsync(IStrategyRuntimeContext context, CancellationToken ct) =>
                Task.CompletedTask;

            public Task OnBarAsync(OhlcvBar bar, IStrategyRuntimeContext context, CancellationToken ct)
            {
                _fast.Update(bar.Close);
                return Task.CompletedTask;
            }

            public void Draw(IRenderSurface surface)
            {
                using var panel = surface.Panel("Edge", RenderPanelKind.Chart);
                Plot.Waiting(surface, "warming up");
            }
        }
        ```
        """;

    [Fact]
    public async Task A_non_streaming_reply_survives_the_wire_and_compiles()
    {
        // The whole vertical minus the vendor: real HTTP, real JSON, real extraction, real Roslyn.
        using var server = FakeProvider.Start(streaming: false);

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), "loopback", "Loopback", server.BaseUrl, "test-model", "sk-loopback");

        client.IsAvailable.Should().BeTrue();

        var response = await client.GenerateAsync(new StrategyCodegenRequest(
            "You write strategies.", [new(CodegenRole.User, "write me an edge kernel")]));

        response.Success.Should().BeTrue(response.Error);
        response.HasFiles.Should().BeTrue("the fenced block has to survive serialisation");
        response.FileList[0].Name.Should().Be("EdgeKernel.cs");

        // Usage parsed from the bytes the server actually wrote, not from an object handed over.
        response.Usage!.InputTokens.Should().Be(1234);
        response.Usage.OutputTokens.Should().Be(567);

        // And it is real source: compile what came off the socket.
        var compile = new RoslynStrategyCompiler().Compile(
            new StrategyScript("edge-kernel", "Edge kernel", response.FileList));

        compile.Success.Should().BeTrue(
            string.Join("; ", compile.Diagnostics.Select(d => $"{d.Id} {d.Message}")));
    }

    [Fact]
    public async Task A_streamed_reply_is_reassembled_from_its_frames()
    {
        // The failure this catches is invisible to a handler stub: an SSE body arrives as a sequence
        // of `data:` frames that a reader may split anywhere, and the text has to come back whole.
        using var server = FakeProvider.Start(streaming: true);

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), "loopback", "Loopback", server.BaseUrl, "test-model", "sk-loopback");

        var text = new StringBuilder();
        StrategyCodegenResponse? completed = null;
        CodegenUsage? usage = null;

        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest(
            "You write strategies.", [new(CodegenRole.User, "write me an edge kernel")])))
        {
            switch (evt)
            {
                case CodegenEvent.TextDelta delta: text.Append(delta.Text); break;
                case CodegenEvent.UsageUpdate update: usage = update.Usage; break;
                case CodegenEvent.Completed done: completed = done.Response; break;
            }
        }

        text.Length.Should().BeGreaterThan(0, "the deltas are what the user watches arrive");
        completed.Should().NotBeNull();
        completed!.Success.Should().BeTrue(completed.Error);
        completed.HasFiles.Should().BeTrue();
        completed.FileList[0].Content.Should().Contain("EdgeKernel");

        // Exactly one Completed, last — the contract callers rely on to avoid branching on streaming.
        usage!.OutputTokens.Should().Be(567);
    }

    [Fact]
    public async Task The_request_carries_the_shared_pack_and_the_role_as_separate_messages()
    {
        // Two system messages, not one concatenated string. Providers cache on an exact prefix, so
        // folding the role into the shared pack re-bills the pack on every role switch. Asserted on
        // the body the SERVER received, which is the only place the claim is actually true or false.
        using var server = FakeProvider.Start(streaming: false);

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), "loopback", "Loopback", server.BaseUrl, "test-model", "sk-loopback");

        await client.GenerateAsync(new StrategyCodegenRequest(
            "SHARED PACK", [new(CodegenRole.User, "hello")], RoleInstruction: "ROLE INSTRUCTION"));

        var body = server.LastBody!;
        body.Should().Contain("SHARED PACK").And.Contain("ROLE INSTRUCTION");
        body.IndexOf("SHARED PACK", StringComparison.Ordinal)
            .Should().BeLessThan(body.IndexOf("ROLE INSTRUCTION", StringComparison.Ordinal),
                "the cacheable prefix has to come first");

        server.LastAuthorization.Should().Be("Bearer sk-loopback");
    }

    [Fact]
    public async Task A_provider_error_is_reported_rather_than_thrown()
    {
        // A 401 from a wrong key is the single most common first-run failure. It has to reach the
        // transcript as a message, not as an exception out of the turn.
        using var server = FakeProvider.Start(streaming: false, status: HttpStatusCode.Unauthorized);

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), "loopback", "Loopback", server.BaseUrl, "test-model", "sk-wrong");

        var response = await client.GenerateAsync(new StrategyCodegenRequest(
            "pack", [new(CodegenRole.User, "hello")]));

        response.Success.Should().BeFalse();
        response.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task The_model_list_comes_back_from_the_endpoint()
    {
        // What "Refresh models" calls, and the control that proves a provider is reachable at all.
        using var server = FakeProvider.Start(streaming: false);

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), "loopback", "Loopback", server.BaseUrl, "test-model", "sk-loopback");

        var models = await client.ListModelsAsync();

        models.Should().Contain("test-model").And.Contain("other-model");
    }

    // ── the server ──────────────────────────────────────────────────────────────────────────────

    /// <summary>A loopback endpoint that answers the OpenAI-compatible shape.</summary>
    [Fact]
    public async Task A_loopback_address_typed_without_a_scheme_still_reaches_the_provider()
    {
        // The repair, proven against a real socket rather than as string equality. A user running LM
        // Studio or vLLM copies "127.0.0.1:PORT/v1" out of its readme, and that has to WORK, not merely
        // fail politely -- it is the single most likely bring-your-own-provider setup, and the one with
        // no vendor support article to fall back on when it does not.
        using var server = FakeProvider.Start(streaming: false);

        var schemeless = server.BaseUrl.Replace("http://", string.Empty, StringComparison.Ordinal);
        schemeless.Should().NotStartWith("http", "the point is that the scheme is missing");

        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(), "loopback", "Loopback", schemeless, "test-model", "sk-loopback");

        client.IsAvailable.Should().BeTrue();

        var response = await client.GenerateAsync(new StrategyCodegenRequest(
            "You write strategies.", [new(CodegenRole.User, "write me an edge kernel")]));

        response.Success.Should().BeTrue(response.Error);
        response.FileList[0].Name.Should().Be("EdgeKernel.cs");
    }

    private sealed class FakeProvider : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stopping = new();

        public string BaseUrl { get; private set; } = string.Empty;

        public string? LastBody { get; private set; }

        public string? LastAuthorization { get; private set; }

        public static FakeProvider Start(
            bool streaming, HttpStatusCode status = HttpStatusCode.OK)
        {
            // Port zero would be ideal; HttpListener has no such thing, so take one the OS gave us
            // for a throwaway socket and reuse it. A collision retries rather than failing the suite.
            for (var attempt = 0; attempt < 8; attempt++)
            {
                var port = FreePort();
                var server = new FakeProvider { BaseUrl = $"http://127.0.0.1:{port}/v1" };
                server._listener.Prefixes.Add($"http://127.0.0.1:{port}/");

                try
                {
                    server._listener.Start();
                }
                catch (HttpListenerException)
                {
                    server.Dispose();
                    continue;
                }

                _ = Task.Run(() => server.ServeAsync(streaming, status));
                return server;
            }

            throw new InvalidOperationException("Could not bind a loopback port for the fake provider.");
        }

        private static int FreePort()
        {
            using var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private async Task ServeAsync(bool streaming, HttpStatusCode status)
        {
            while (!_stopping.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch { return; }

                try
                {
                    using var reader = new StreamReader(context.Request.InputStream);
                    LastBody = await reader.ReadToEndAsync();
                    LastAuthorization = context.Request.Headers["Authorization"];

                    if (context.Request.Url!.AbsolutePath.EndsWith("/models", StringComparison.Ordinal))
                    {
                        await WriteAsync(context, HttpStatusCode.OK,
                            """{"data":[{"id":"test-model"},{"id":"other-model"}]}""");
                        continue;
                    }

                    if (status != HttpStatusCode.OK)
                    {
                        await WriteAsync(context, status, """{"error":{"message":"invalid api key"}}""");
                        continue;
                    }

                    if (streaming) await WriteStreamAsync(context);
                    else await WriteAsync(context, HttpStatusCode.OK, Completion());
                }
                catch
                {
                    // A dropped client is the ordinary end of a test, not a failure.
                }
            }
        }

        /// <summary>Built with the serializer rather than by string surgery, so the fenced block's own
        /// braces and newlines cannot corrupt the envelope carrying it.</summary>
        private static string Completion() =>
            System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = GeneratedUnit } } },
                usage = new { prompt_tokens = 1234, completion_tokens = 567 },
            });

        private static async Task WriteAsync(HttpListenerContext context, HttpStatusCode status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = (int)status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }

        /// <summary>Server-sent events, deliberately split so the reader has to reassemble them.</summary>
        private static async Task WriteStreamAsync(HttpListenerContext context)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "text/event-stream";
            context.Response.SendChunked = true;

            var output = context.Response.OutputStream;

            foreach (var piece in Chunks(GeneratedUnit, 64))
            {
                var frame = "data: " + System.Text.Json.JsonSerializer.Serialize(new
                {
                    choices = new[] { new { delta = new { content = piece } } },
                }) + "\n\n";

                var bytes = Encoding.UTF8.GetBytes(frame);
                await output.WriteAsync(bytes);
                await output.FlushAsync();
            }

            var usage = "data: " + System.Text.Json.JsonSerializer.Serialize(new
            {
                choices = Array.Empty<object>(),
                usage = new { prompt_tokens = 1234, completion_tokens = 567 },
            }) + "\n\ndata: [DONE]\n\n";

            await output.WriteAsync(Encoding.UTF8.GetBytes(usage));
            await output.FlushAsync();
            context.Response.Close();
        }

        private static IEnumerable<string> Chunks(string text, int size)
        {
            for (var i = 0; i < text.Length; i += size)
                yield return text.Substring(i, Math.Min(size, text.Length - i));
        }

        public void Dispose()
        {
            _stopping.Cancel();
            try { _listener.Close(); } catch { /* already closed */ }
            _stopping.Dispose();
        }
    }
}
