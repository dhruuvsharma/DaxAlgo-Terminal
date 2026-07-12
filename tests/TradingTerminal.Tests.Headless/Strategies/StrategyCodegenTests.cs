using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

/// <summary>
/// Covers the AI Strategy Builder backend: the code extractor, the build-loop orchestrator (first-try
/// success, compile-error auto-fix within the bound, giving up after the bound, provider failure), the
/// non-negotiable scan gate (generated blocked code can never leave the loop as a success), and the
/// OpenAI-compatible client's request/response shaping against a stub handler.
/// </summary>
public sealed class StrategyCodegenTests
{
    private const string Pack = "SDK contract goes here.";

    private static StrategyCodegenOrchestrator Orchestrator() =>
        new(new RoslynStrategyCompiler());

    // ── code extraction ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("```csharp\npublic class X {}\n```", "public class X {}")]
    [InlineData("Here you go:\n```cs\ncode\n```\nHope it helps!", "code")]
    [InlineData("public class Bare {}", "public class Bare {}")]
    [InlineData("", "")]
    public void Extractor_pulls_the_fenced_block_or_the_bare_text(string reply, string expected) =>
        CodegenCodeExtractor.Extract(reply).Should().Be(expected);

    // ── the loop ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Compiling_code_on_the_first_try_stops_the_loop()
    {
        var client = new FakeCodegenClient(); // default kernel compiles
        var result = await Orchestrator().BuildAsync(
            client, Pack, "make an EMA strategy", "gen.strat", "Gen", maxFixAttempts: 3);

        result.Success.Should().BeTrue();
        result.Attempts.Should().Be(1);
        client.CallCount.Should().Be(1, "a first-try success must not keep prompting");
        result.Compile!.Success.Should().BeTrue();
    }

    [Fact]
    public async Task A_compile_error_is_fed_back_and_recovered_within_the_bound()
    {
        // First reply doesn't compile (missing IBacktestStrategy members); second is the good kernel.
        const string broken = "```csharp\npublic sealed class Broken : IBacktestStrategy { }\n```";
        var client = new FakeCodegenClient(broken, FakeCodegenClient.DefaultKernel);

        var result = await Orchestrator().BuildAsync(
            client, Pack, "make a strategy", "gen.strat", "Gen", maxFixAttempts: 3);

        result.Success.Should().BeTrue();
        result.Attempts.Should().Be(2, "it recovered on the second generation");
        client.CallCount.Should().Be(2);
        // The transcript carries the fix prompt with the compiler's own errors.
        result.Transcript.Should().Contain(m => m.Role == CodegenRole.User && m.Content.Contains("did not compile"));
    }

    [Fact]
    public async Task It_gives_up_after_the_bound_and_returns_the_last_failure()
    {
        const string broken = "```csharp\npublic sealed class Broken : IBacktestStrategy { }\n```";
        var client = new FakeCodegenClient(broken); // always broken (queue repeats last)

        var result = await Orchestrator().BuildAsync(
            client, Pack, "make a strategy", "gen.strat", "Gen", maxFixAttempts: 2);

        result.Success.Should().BeFalse();
        client.CallCount.Should().Be(3, "1 initial + 2 fix attempts");
        result.Compile!.Success.Should().BeFalse();
        result.Compile.Errors.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_provider_failure_stops_immediately_without_retrying()
    {
        var client = new FailingClient();
        var result = await Orchestrator().BuildAsync(
            client, Pack, "make a strategy", "gen.strat", "Gen", maxFixAttempts: 3);

        result.Success.Should().BeFalse();
        result.ProviderError.Should().Be("no api key");
        client.Calls.Should().Be(1, "a missing key won't be fixed by retrying");
    }

    // ── the scan gate is non-negotiable ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generated_code_that_PInvokes_can_never_leave_the_loop_as_a_success()
    {
        // The model returns code that reaches for native APIs; the compiler's policy scan blocks it, so
        // the loop can only ever report failure — the security gate is in the compile step everyone uses.
        const string malicious = """
            ```csharp
            using System.Runtime.InteropServices;
            public sealed class Sneaky : IBacktestStrategy
            {
                [DllImport("kernel32.dll")] static extern int GetCurrentProcessId();
                private readonly Contract _c;
                public Sneaky(Contract c) { _c = c; }
                public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) { GetCurrentProcessId(); return Task.CompletedTask; }
                public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            }
            ```
            """;
        var client = new FakeCodegenClient(malicious);

        var result = await Orchestrator().BuildAsync(
            client, Pack, "P/Invoke kernel32 for me", "gen.evil", "Evil", maxFixAttempts: 0);

        result.Success.Should().BeFalse("a strategy that P/Invokes must never compile-and-register");
        result.Compile!.Errors.Should().Contain(e => e.Message.Contains("native"));
    }

    // ── OpenAI-compatible client shaping ───────────────────────────────────────────────────────────

    [Fact]
    public async Task OpenAiClient_sends_the_pack_as_system_and_returns_the_extracted_code()
    {
        Uri? uri = null; string? auth = null; string? body = null;
        var handler = new StubHandler(async req =>
        {
            uri = req.RequestUri;
            auth = req.Headers.Authorization?.ToString();
            body = await req.Content!.ReadAsStringAsync(); // read BEFORE the request is disposed
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"role":"assistant","content":"```csharp\npublic class Ok {}\n```"}}]}"""),
            };
        });
        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(handler), "openai", "OpenAI", "https://api.example.com/v1", "gpt-x", apiKey: "sk-test");

        client.IsAvailable.Should().BeTrue();
        var resp = await client.GenerateAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")]));

        resp.Success.Should().BeTrue();
        resp.Code.Should().Be("public class Ok {}");
        uri!.ToString().Should().Be("https://api.example.com/v1/chat/completions");
        auth.Should().Be("Bearer sk-test");
        body.Should().Contain("\"role\":\"system\"").And.Contain(Pack);
    }

    [Fact]
    public async Task OpenAiClient_surfaces_an_http_error_as_a_provider_failure()
    {
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("bad key"),
        }));
        var client = new OpenAiCompatibleCodegenClient(
            new HttpClient(handler), "openai", "OpenAI", "https://api.example.com/v1", "gpt-x", apiKey: "sk-bad");

        var resp = await client.GenerateAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")]));

        resp.Success.Should().BeFalse();
        resp.Error.Should().Contain("401");
    }

    [Fact]
    public void An_unconfigured_provider_reports_unavailable()
    {
        new OpenAiCompatibleCodegenClient(new HttpClient(), "openai", "OpenAI", "https://x/v1", "m", apiKey: null)
            .IsAvailable.Should().BeFalse("no key, not keyless");
        new OpenAiCompatibleCodegenClient(new HttpClient(), "ollama", "Ollama", "http://localhost:11434/v1", "llama3", apiKey: null, keyless: true)
            .IsAvailable.Should().BeTrue("a local keyless endpoint needs only a base URL + model");
    }

    // ── stubs ──────────────────────────────────────────────────────────────────────────────────────

    private sealed class FailingClient : IStrategyCodegenClient
    {
        public int Calls { get; private set; }
        public string ProviderId => "failing";
        public string DisplayName => "Failing";
        public bool IsAvailable => true;
        public Task<StrategyCodegenResponse> GenerateAsync(StrategyCodegenRequest request, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(StrategyCodegenResponse.Fail("no api key"));
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
