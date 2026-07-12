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

    // ── provider factory ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Factory_builds_the_agent_clis_plus_configured_keyed_providers()
    {
        var options = new TradingTerminal.Core.Configuration.AiCodegenOptions
        {
            Providers =
            {
                ["deepseek"] = new() { BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat" },
                ["anthropic"] = new() { BaseUrl = "https://api.anthropic.com", Model = "claude-x", Kind = TradingTerminal.Core.Configuration.AiCodegenProviderKind.Anthropic },
                ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1", Model = "llama3.1" },
            },
        };
        // Only DeepSeek has a key; Ollama is keyless-local; Anthropic has no key.
        var factory = new StrategyCodegenClientFactory(
            () => new HttpClient(), options,
            keyResolver: id => id == "deepseek" ? "sk-deepseek" : null);

        var all = factory.BuildAll();

        all.Should().Contain(c => c.ProviderId == "claude-cli");
        all.Should().Contain(c => c.ProviderId == "codex-cli");
        all.Single(c => c.ProviderId == "deepseek").IsAvailable.Should().BeTrue("it has a key");
        all.Single(c => c.ProviderId == "ollama").IsAvailable.Should().BeTrue("local endpoint is keyless");
        all.Single(c => c.ProviderId == "anthropic").IsAvailable.Should().BeFalse("no key configured");
    }

    [Fact]
    public void Factory_selects_the_configured_default_when_available_else_the_first_available()
    {
        var options = new TradingTerminal.Core.Configuration.AiCodegenOptions
        {
            DefaultProvider = "deepseek",
            Providers = { ["deepseek"] = new() { BaseUrl = "https://api.deepseek.com/v1", Model = "deepseek-chat" } },
        };
        var withKey = new StrategyCodegenClientFactory(() => new HttpClient(), options, _ => "sk-x");
        withKey.SelectDefault()!.ProviderId.Should().Be("deepseek");

        // Default configured but unavailable (no key) ⇒ fall through to the first available (an agent
        // CLI if installed, else nothing). We only assert it's not the unavailable default.
        var noKey = new StrategyCodegenClientFactory(() => new HttpClient(), options, _ => null);
        var selected = noKey.SelectDefault();
        (selected is null || selected.ProviderId != "deepseek").Should().BeTrue();
    }

    // ── context pack + builder facade ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_context_pack_is_embedded_and_states_the_contract()
    {
        var pack = StrategyContextPack.Load().SystemPrompt;

        pack.Should().NotBeNullOrWhiteSpace();
        pack.Should().Contain("IBacktestStrategy").And.Contain("IClock").And.Contain("OUTPUT CONTRACT (a)");
    }

    [Fact]
    public async Task The_builder_facade_takes_an_instruction_to_a_compiling_strategy()
    {
        // End-to-end through the real facade: factory (for the provider list) + orchestrator + embedded
        // pack. The generation itself is driven by an explicit fake provider (no network in CI).
        var options = new TradingTerminal.Core.Configuration.AiCodegenOptions
        {
            MaxFixAttempts = 2,
            Providers = { ["ollama"] = new() { BaseUrl = "http://localhost:11434/v1", Model = "llama3.1" } },
        };
        var factory = new StrategyCodegenClientFactory(() => new HttpClient(), options, _ => null);
        var builder = new AiStrategyBuilder(factory, new StrategyCodegenOrchestrator(new RoslynStrategyCompiler()),
            StrategyContextPack.Load(), options);

        builder.Providers.Should().Contain(p => p.ProviderId == "ollama" && p.IsAvailable,
            "the local keyless provider is available and offered in the picker");

        var result = await builder.BuildAsync(new FakeCodegenClient(), "an EMA strategy", "gen.s", "Gen");
        result.Success.Should().BeTrue();
        result.Code.Should().NotBeNullOrWhiteSpace("the facade surfaces the final code for the editor");
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
