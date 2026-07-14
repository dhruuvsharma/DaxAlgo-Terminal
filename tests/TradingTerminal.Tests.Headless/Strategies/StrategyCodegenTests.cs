using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Plugins;
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
    [InlineData("Here you go:\n```cs\nclass Code {}\n```\nHope it helps!", "class Code {}")]
    [InlineData("public class Bare {}", "public class Bare {}")]
    [InlineData("", "")]
    public void Extractor_pulls_the_fenced_block_or_the_bare_text(string reply, string expected) =>
        CodegenCodeExtractor.Extract(reply).Should().Be(expected);

    [Fact]
    public void Extractor_splits_a_multi_file_reply_and_reads_its_file_headers()
    {
        const string reply = """
            Here's the strategy, split in two.

            ```csharp
            // file: MomentumKernel.cs
            public sealed class MomentumKernel { }
            ```

            ```csharp
            // file: Indicators.cs
            public static class Indicators { }
            ```

            ```json
            { "not": "csharp" }
            ```
            """;

        var files = CodegenCodeExtractor.ExtractFiles(reply);

        files.Should().HaveCount(2, "the json block is not C# and must not be compiled");
        files[0].Name.Should().Be("MomentumKernel.cs");
        files[0].Content.Should().Be("public sealed class MomentumKernel { }",
            "the `// file:` marker is stripped so compiler line numbers match the editor");
        files[1].Name.Should().Be("Indicators.cs");
    }

    [Fact]
    public void Extractor_names_unlabelled_blocks_positionally_and_never_collides()
    {
        var files = CodegenCodeExtractor.ExtractFiles(
            "```csharp\nclass A {}\n```\n```csharp\n// file: A.cs\nclass B {}\n```");

        files.Should().HaveCount(2);
        files[0].Name.Should().Be(StrategyFile.DefaultName);
        files[1].Name.Should().NotBe(files[0].Name, "two files must never overwrite each other in the editor");
    }

    [Fact]
    public void Prose_with_no_code_is_a_question_not_a_file()
    {
        // The model asking "which instrument? what timeframe?" must not be compiled as C#.
        CodegenCodeExtractor.ExtractFiles(
            "Before I write this: which instrument, and what timeframe are you trading?")
            .Should().BeEmpty();
    }

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

    // ── the compiled assembly is a plugin, not just a kernel ───────────────────────────────────────

    [Fact]
    public void A_kernel_plus_a_descriptor_is_recognized_as_a_catalog_strategy()
    {
        // The kernel alone is backtest-only. Adding an ITradingStrategy descriptor is what starts to make
        // it a catalog entry — the compiler must reflect it out of the emitted assembly.
        var script = new StrategyScript("gen.desc", "Gen", [
            new StrategyFile("Kernel.cs", MinimalKernel("DescStrategy")),
            new StrategyFile("Descriptor.cs", """
                public sealed class DescStrategyDescriptor : ITradingStrategy
                {
                    public string Id => "gen.desc";
                    public string DisplayName => "Gen";
                    public string Description => "A generated strategy.";
                }
                """),
        ]);

        var result = new RoslynStrategyCompiler().Compile(script);

        result.Success.Should().BeTrue();
        result.Authored.Should().NotBeNull();
        result.Authored!.KernelType.Name.Should().Be("DescStrategy");
        result.Authored.DescriptorType!.Name.Should().Be("DescStrategyDescriptor");

        // No view-model and no view ⇒ no live window, and the host must say precisely what's missing
        // rather than putting a card on the pane that throws when clicked.
        result.Authored.HasLiveWindow.Should().BeFalse();
        result.Authored.MissingForCatalog.Should().HaveCount(2)
            .And.Contain(m => m.Contains("view-model"))
            .And.Contain(m => m.Contains("view"));
    }

    [Fact]
    public void The_authored_assembly_is_a_real_plugin_the_loader_can_register()
    {
        // THE restart bug: an authored DLL with no IStrategyPlugin is found by the loader on the next
        // start, rejected, and reported as "failed to load". The compiler must generate the entry point.
        var script = new StrategyScript("gen.plugin", "Gen plugin", [
            new StrategyFile("Kernel.cs", MinimalKernel("PluginKernel")),
            new StrategyFile("Descriptor.cs", """
                public sealed class PluginDescriptor : ITradingStrategy
                {
                    public string Id => "gen.plugin";
                    public string DisplayName => "Gen plugin";
                    public string Description => "A generated strategy.";
                }
                """),
        ]);

        var result = new RoslynStrategyCompiler().Compile(script);
        result.Success.Should().BeTrue();

        var services = new ServiceCollection();
        var loaded = PluginLoader.RegisterFromAssembly(
            result.Authored!.Assembly, services, DaxAlgo.Sdk.SdkInfo.Version);

        loaded.Should().NotBeNull("the generated IStrategyPlugin is what makes this a plugin at all");
        loaded!.Name.Should().Be("Gen plugin");

        // And it contributed what a hand-written plugin's AddXxxStrategy() would.
        var provider = services.BuildServiceProvider();
        provider.GetServices<ITradingStrategy>().Should().ContainSingle().Which.Id.Should().Be("gen.plugin");
        provider.GetServices<BacktestStrategyOption>().Should().ContainSingle().Which.Id.Should().Be("gen.plugin");

        // Attribution: the loader reads ImplementationType to badge an unsigned plugin's strategies DEV,
        // so the descriptor must be registered by type, not as an instance.
        loaded.StrategyImplementationTypes.Should().ContainSingle()
            .Which.Should().Contain("PluginDescriptor");
    }

    [Fact]
    public void The_emitted_image_is_persistable_and_the_dll_is_never_locked()
    {
        // The installer writes this image to the plugins folder so the strategy survives a restart. It
        // must be loaded from the byte[], never the file — otherwise regenerating would hit a locked DLL.
        var result = new RoslynStrategyCompiler().Compile(
            new StrategyScript("gen.img", "Gen", MinimalKernel("ImgStrategy")));

        result.Authored!.Image.Should().NotBeEmpty();
        result.Authored.Assembly.Location.Should().BeEmpty("loaded from memory — so nothing holds the file open");

        var path = Path.Combine(Path.GetTempPath(), $"dax-authored-{Guid.NewGuid():N}.dll");
        try
        {
            File.WriteAllBytes(path, result.Authored.Image);
            File.WriteAllBytes(path, result.Authored.Image); // overwrite: a regenerate must not fail here
            new FileInfo(path).Length.Should().BeGreaterThan(0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A kernel that compiles and does nothing — the fixture for the plugin-shape tests.</summary>
    private static string MinimalKernel(string name) => $$"""
        public sealed class {{name}}(Contract contract) : IBacktestStrategy
        {
            private readonly Contract _contract = contract;
            public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
            public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        }
        """;

    // ── the conversation (multi-turn session) ──────────────────────────────────────────────────────

    [Fact]
    public async Task A_reply_with_no_code_is_a_question_the_user_answers_in_the_same_thread()
    {
        // Turn 1: the model asks what to trade. Turn 2 (after the user answers): it writes the kernel.
        var client = new FakeCodegenClient(
            "Which instrument, and what timeframe?",
            FakeCodegenClient.DefaultKernel);
        var session = Orchestrator().CreateSession(client, Pack, "gen.strat", "Gen", maxFixAttempts: 2);

        var asked = await session.SendAsync("make me a scalper");

        asked.Kind.Should().Be(BuildTurnKind.Question, "prose with no code is a question, not a failure");
        asked.Files.Should().BeEmpty();
        asked.Compile.Should().BeNull("nothing was compiled — there was no code");

        var answered = await session.SendAsync("ES futures, 1-minute bars");

        answered.Success.Should().BeTrue();
        session.Transcript.Should().HaveCount(4, "user, question, user, code — one thread");
        session.Transcript[0].Content.Should().Be("make me a scalper");
        session.Transcript[2].Content.Should().Contain("ES futures",
            "the answer lands in the SAME conversation, so the model keeps its context");
    }

    [Fact]
    public async Task A_multi_file_answer_compiles_as_one_strategy()
    {
        const string twoFiles = """
            ```csharp
            // file: Kernel.cs
            public sealed class TwoFileStrategy(Contract contract) : IBacktestStrategy
            {
                private readonly Contract _contract = contract;
                private int _ticks;
                public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
                {
                    _ticks++;
                    Indicators.Ema(ref _ema, (tick.Bid + tick.Ask) / 2.0, 20);
                    return Task.CompletedTask;
                }
                private double _ema;
                public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            }
            ```

            ```csharp
            // file: Indicators.cs
            public static class Indicators
            {
                public static void Ema(ref double ema, double x, int period)
                {
                    if (ema == 0) { ema = x; return; }
                    ema += 2.0 / (period + 1) * (x - ema);
                }
            }
            ```
            """;
        var session = Orchestrator().CreateSession(
            new FakeCodegenClient(twoFiles), Pack, "gen.two", "Two", maxFixAttempts: 0);

        var turn = await session.SendAsync("split the indicator out");

        turn.Success.Should().BeTrue("a strategy is a small project — helpers may live in their own file");
        turn.Files.Should().HaveCount(2);
        turn.Compile!.Option!.Id.Should().Be("gen.two");
    }

    [Fact]
    public async Task A_compile_error_names_the_file_it_is_in()
    {
        // Two files, the second broken: the diagnostic (and so the fix prompt) must say WHICH file.
        const string broken = """
            ```csharp
            // file: Kernel.cs
            public sealed class OkStrategy(Contract contract) : IBacktestStrategy
            {
                public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
                public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
                public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
            }
            ```

            ```csharp
            // file: Helpers.cs
            public static class Helpers { public static int Broken() { return "not an int"; } }
            ```
            """;
        var session = Orchestrator().CreateSession(
            new FakeCodegenClient(broken), Pack, "gen.broken", "Broken", maxFixAttempts: 0);

        var turn = await session.SendAsync("add a helper");

        turn.Kind.Should().Be(BuildTurnKind.CompileFailed);
        turn.Compile!.Errors.Should().Contain(e => e.File == "Helpers.cs",
            "the model can only fix an error it can locate");
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

    // ── models + reasoning effort ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_model_shortlist_offers_real_model_ids_not_cli_aliases()
    {
        // The picker used to show "opus"/"sonnet"/"haiku" — the CLI's shorthand, not model ids. Both the
        // API provider and the installed CLI must offer the same, real ids.
        foreach (var provider in new[] { "anthropic", "claude-cli" })
        {
            var models = AiModelCatalog.For(provider);
            models.Should().Contain("claude-opus-4-8").And.Contain("claude-sonnet-5");
            models.Should().NotContain("opus").And.NotContain("sonnet").And.NotContain("haiku");
        }
    }

    [Fact]
    public async Task Effort_is_sent_only_when_the_user_picks_one()
    {
        // Default ⇒ no effort/thinking fields at all: a model that predates them (Haiku 4.5 and older)
        // 400s on either, so "Provider default" has to be wire-silent.
        var (body, _) = await CaptureAnthropicRequest(CodegenEffort.Default);
        body.Should().NotContain("effort").And.NotContain("thinking");

        var (withEffort, _) = await CaptureAnthropicRequest(CodegenEffort.XHigh);
        withEffort.Should().Contain("\"effort\":\"xhigh\"")
            .And.Contain("\"thinking\":{\"type\":\"adaptive\"}",
                "effort controls thinking depth — the two go together on current models");
    }

    [Fact]
    public void The_claude_cli_gets_model_and_effort_flags_before_the_prompt()
    {
        var args = AgentCliAdapter.ClaudeCode.ArgumentsFor("claude-opus-4-8", CodegenEffort.Max);

        args.Should().ContainInOrder("--model", "claude-opus-4-8", "--effort", "max");

        AgentCliAdapter.ClaudeCode.ArgumentsFor(model: null, CodegenEffort.Default)
            .Should().NotContain("--effort", "no pick ⇒ the vendor CLI keeps its own default");
    }

    [Fact]
    public void Codex_has_no_effort_flag_so_none_is_passed()
    {
        // Guessing a flag the CLI doesn't have would make every generation fail on an arg parse error.
        AgentCliAdapter.Codex.ArgumentsFor("gpt-x", CodegenEffort.High)
            .Should().Contain("-m").And.NotContain("--effort");
    }

    private static async Task<(string Body, StrategyCodegenResponse Response)> CaptureAnthropicRequest(CodegenEffort effort)
    {
        string? body = null;
        var handler = new StubHandler(async req =>
        {
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"content":[{"type":"text","text":"```csharp\npublic class Ok {}\n```"}],"usage":{"input_tokens":10,"output_tokens":5}}"""),
            };
        });
        var client = new AnthropicCodegenClient(
            new HttpClient(handler), "https://api.example.com", "claude-opus-4-8", apiKey: "sk-test", effort);

        var response = await client.GenerateAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")]));
        return (body!, response);
    }

    // ── what a turn actually costs ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Superseded_code_is_stripped_from_the_thread_and_the_current_files_ride_along_once()
    {
        // The naive thread keeps every version of every file the model ever wrote and re-sends all of
        // them on every turn — cost grows with the square of the work. The files are STATE: history keeps
        // the prose, and exactly one copy of the code (the editor's) travels with the newest turn.
        var client = new FakeCodegenClient(
            "Here you go.\n```csharp\n// file: Kernel.cs\npublic sealed class V1 { }\n```",
            "Tightened it.\n```csharp\n// file: Kernel.cs\npublic sealed class V2 { }\n```");
        var session = Orchestrator().CreateSession(client, Pack, "gen.cost", "Cost", maxFixAttempts: 0);

        await session.SendAsync("write it");            // model writes V1
        await session.SendAsync("now tighten the stop"); // model replaces it with V2
        await session.SendAsync("explain the exit");     // third turn: V1 is now truly superseded

        var sent = client.LastRequest!.Messages;
        var prompt = string.Join("\n", sent.Select(m => m.Content));

        // V1 was replaced two turns ago. The naive thread would still be carrying it — and paying for it.
        prompt.Should().NotContain("public sealed class V1",
            "a superseded file must not be re-sent on every later turn");
        prompt.Should().Contain("[code omitted", "but what the model SAID is kept — a follow-up depends on it");
        prompt.Should().Contain("Tightened it.");

        // The current code appears exactly once, attached to the newest turn.
        Regex.Matches(prompt, "public sealed class V2").Should().HaveCount(1);
        sent[^1].Role.Should().Be(CodegenRole.User);
        sent[^1].Content.Should().Contain("as they stand right now").And.Contain("public sealed class V2");
    }

    [Fact]
    public async Task A_hand_edit_in_the_editor_beats_whatever_the_model_last_wrote()
    {
        var client = new FakeCodegenClient(FakeCodegenClient.DefaultKernel);
        var session = Orchestrator().CreateSession(client, Pack, "gen.edit", "Edit", maxFixAttempts: 0);
        await session.SendAsync("write it");

        session.SyncEditedFiles([new StrategyFile("Kernel.cs", "public sealed class EditedByHand { }")]);
        await session.SendAsync("explain what I changed");

        client.LastRequest!.Messages[^1].Content.Should().Contain("EditedByHand",
            "the editor is the truth — the model must work from the code that is actually there");
    }

    [Fact]
    public async Task Anthropic_marks_the_pack_and_the_last_turn_as_cacheable()
    {
        // The pack is byte-identical on every call and the conversation prefix repeats — without cache
        // breakpoints, every turn re-bills all of it at full price.
        var (body, _) = await CaptureAnthropicRequest(CodegenEffort.Default);

        body.Should().Contain("\"cache_control\":{\"type\":\"ephemeral\"}");
        Regex.Matches(body, "cache_control").Should().HaveCount(2,
            "one breakpoint on the system pack, one on the last message (so it is the cached prefix next turn)");
    }

    [Fact]
    public async Task The_cached_share_of_the_prompt_is_reported_separately()
    {
        const string json = """
            {"content":[{"type":"text","text":"```csharp\npublic class Ok {}\n```"}],
             "usage":{"input_tokens":200,"cache_creation_input_tokens":0,"cache_read_input_tokens":9000,"output_tokens":50}}
            """;
        var client = new AnthropicCodegenClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            }))),
            "https://api.example.com", "claude-opus-4-8", apiKey: "sk-test");

        var usage = (await client.GenerateAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")]))).Usage!;

        usage.InputTokens.Should().Be(9_200, "the whole prompt, cached portion included");
        usage.CachedInputTokens.Should().Be(9_000,
            "surfaced separately — a session where this stays at zero is paying full price every turn");
    }

    // ── resuming a saved conversation ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_resumed_session_replays_the_thread_so_the_model_still_remembers_what_it_wrote()
    {
        // Restoring a chat has to restore the MODEL's memory too, not just the bubbles the user reads —
        // otherwise "now tighten the stop" arrives with no idea what the stop is.
        var yesterday = new List<CodegenMessage>
        {
            new(CodegenRole.User, "build me an EMA cross"),
            new(CodegenRole.Assistant, FakeCodegenClient.DefaultKernel),
        };

        var client = new FakeCodegenClient();
        var session = Orchestrator().CreateSession(
            client, Pack, "gen.resume", "Resumed", maxFixAttempts: 0,
            history: yesterday,
            priorUsage: new CodegenUsage(1_000, 500));

        session.Transcript.Should().HaveCount(2, "the thread came back with the session");
        session.TotalUsage.TotalTokens.Should().Be(1_500, "and so did what it had already cost");

        await session.SendAsync("now tighten the stop");

        // The provider sees the whole thread — yesterday's turns, then today's follow-up.
        session.Transcript.Should().HaveCount(4);
        session.Transcript[0].Content.Should().Be("build me an EMA cross");
        session.Transcript[2].Content.Should().Be("now tighten the stop");
        session.TotalUsage.TotalTokens.Should().BeGreaterThan(1_500, "the counter continues, it doesn't restart");
    }

    // ── streaming ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Anthropic_streams_text_deltas_and_usage_and_ends_with_the_assembled_files()
    {
        // The real SSE shape: usage up front (prompt + cache), text in fragments, output tokens at the end.
        const string sse = """
            event: message_start
            data: {"type":"message_start","message":{"usage":{"input_tokens":10,"cache_creation_input_tokens":1000,"cache_read_input_tokens":5000}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"```csharp\n"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"public class Streamed {}\n```"}}

            event: message_delta
            data: {"type":"message_delta","usage":{"output_tokens":42}}

            event: message_stop
            data: {"type":"message_stop"}

            """;
        var handler = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse),
        }));
        var client = new AnthropicCodegenClient(
            new HttpClient(handler), "https://api.example.com", "claude-opus-4-8", apiKey: "sk-test");

        var events = new List<CodegenEvent>();
        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")])))
            events.Add(evt);

        events.OfType<CodegenEvent.TextDelta>().Select(d => d.Text)
            .Should().HaveCount(2, "the reply arrives in fragments — that is the point of streaming");

        var usage = events.OfType<CodegenEvent.UsageUpdate>().Last().Usage;
        usage.InputTokens.Should().Be(6010,
            "prompt tokens include what the cache served — reporting 10 for a 6k-token prompt would be a lie");
        usage.OutputTokens.Should().Be(42);

        var completed = events.OfType<CodegenEvent.Completed>().Should().ContainSingle().Subject.Response;
        completed.Success.Should().BeTrue();
        completed.FileList.Should().ContainSingle()
            .Which.Content.Should().Be("public class Streamed {}");
    }

    [Fact]
    public async Task A_streamed_reply_with_no_code_is_still_a_question()
    {
        const string sse = """
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Which instrument, and what tick size?"}}

            data: {"type":"message_stop"}

            """;
        var client = new AnthropicCodegenClient(
            new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse),
            }))),
            "https://api.example.com", "claude-opus-4-8", apiKey: "sk-test");

        var completed = await LastCompleted(client);

        completed.Success.Should().BeTrue();
        completed.HasFiles.Should().BeFalse();
        completed.RawText.Should().Contain("tick size", "an under-specified brief should be asked about, not guessed at");
    }

    [Fact]
    public async Task A_provider_that_cannot_stream_still_yields_one_completed()
    {
        // FakeCodegenClient overrides nothing — it gets the interface's default StreamAsync (which is why
        // it is reached through the interface), so callers never branch on whether a provider streams.
        IStrategyCodegenClient client = new FakeCodegenClient();

        var events = new List<CodegenEvent>();
        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")])))
            events.Add(evt);

        events.OfType<CodegenEvent.TextDelta>().Should().BeEmpty();
        events.OfType<CodegenEvent.Completed>().Should().ContainSingle()
            .Which.Response.Success.Should().BeTrue();
    }

    [Fact]
    public async Task The_session_reports_deltas_as_they_arrive()
    {
        var session = Orchestrator().CreateSession(
            new FakeCodegenClient(), Pack, "gen.stream", "Gen", maxFixAttempts: 0);

        var streamed = new List<CodegenEvent>();
        var turn = await session.SendAsync(
            "make a strategy", activity: null, ct: default,
            events: new Progress<CodegenEvent>(streamed.Add));

        turn.Success.Should().BeTrue();
        // The fake reports usage but no deltas; the session must forward whatever the provider gives it.
        streamed.OfType<CodegenEvent.UsageUpdate>().Should().NotBeEmpty();
        session.TotalUsage.TotalTokens.Should().BeGreaterThan(0, "the chat header shows what the session cost");
    }

    [Fact]
    public void The_claude_cli_only_asks_for_stream_json_when_streaming()
    {
        AgentCliAdapter.ClaudeCode.ArgumentsFor("claude-opus-4-8", CodegenEffort.High, stream: true)
            .Should().ContainInOrder("--output-format", "stream-json")
            .And.Contain("--include-partial-messages");

        AgentCliAdapter.ClaudeCode.ArgumentsFor("claude-opus-4-8", CodegenEffort.High, stream: false)
            .Should().NotContain("--output-format", "the one-shot path stays plain text");

        AgentCliAdapter.Codex.ArgumentsFor("gpt-x", CodegenEffort.High, stream: true)
            .Should().NotContain("--output-format", "Codex has no stream-json mode — never invent a flag");
    }

    private static async Task<StrategyCodegenResponse> LastCompleted(IStrategyCodegenClient client)
    {
        StrategyCodegenResponse? last = null;
        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest(Pack, [new(CodegenRole.User, "hi")])))
            if (evt is CodegenEvent.Completed completed) last = completed.Response;
        return last!;
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
