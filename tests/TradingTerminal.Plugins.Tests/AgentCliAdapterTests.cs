using FluentAssertions;
using System.Text;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The third table of vendor facts: the installed-CLI providers.
///
/// <para>These matter more than their two-row size suggests, because they are the only providers that
/// need <b>no API key</b> — the user's existing Claude Code or Codex sign-in does the work. For anyone
/// who has one installed, this is the shortest path from "I want to vibe-code a strategy" to a
/// generated unit, and nothing checked the argv it builds.</para>
///
/// <para>Every expectation below was read out of the CLIs' own <c>--help</c> on 2026-08-31, so this
/// file and the adapter table are two independent statements of the same fact. A CLI that changes its
/// flags will not break this file — only a live run can catch that — but an edit to the table that
/// contradicts what the CLI documented will.</para>
/// </summary>
public sealed class AgentCliAdapterTests
{
    /// <summary>
    /// What <c>claude --effort</c> accepts, verbatim from its help: "(low, medium, high, xhigh, max)".
    /// </summary>
    private static readonly string[] ClaudeEffortLevels = ["low", "medium", "high", "xhigh", "max"];

    [Fact]
    public void Every_effort_the_app_can_send_is_one_the_cli_accepts()
    {
        // An effort the CLI rejects is not a degraded run, it is a failed one -- the process exits on
        // the argument before a token is generated. The app's ladder and the CLI's accepted set have to
        // be the same set, not merely overlapping.
        var sent = Enum.GetValues<CodegenEffort>()
            .Select(effort => effort.Wire())
            .Where(wire => wire is not null)
            .ToArray();

        sent.Should().BeEquivalentTo(
            ClaudeEffortLevels,
            "the effort ladder and `claude --effort` must agree exactly -- anything the app can send "
            + "and the CLI does not know fails the run outright");
    }

    [Fact]
    public void Codex_is_never_sent_an_effort_flag()
    {
        // It has none: reasoning is configured through its own config file. Sending one would be an
        // unrecognised argument, which is a hard failure rather than a setting quietly ignored.
        AgentCliAdapter.Codex.EffortFlag.Should().BeNull();

        var argv = AgentCliAdapter.Codex.ArgumentsFor("gpt-x", CodegenEffort.Max);

        argv.Should().NotContain("--effort");
        argv.Should().NotContain("max");
    }

    [Fact]
    public void Codex_keeps_every_flag_in_front_of_the_stdin_marker()
    {
        // `codex exec --help`: "If not provided as an argument (or if `-` is used), instructions are
        // read from stdin." So the trailing `-` is what makes the piped prompt the prompt -- and
        // anything after it would be read as arguments to it rather than as options.
        var argv = AgentCliAdapter.Codex.ArgumentsFor("gpt-x", CodegenEffort.Default, cliProfile: "work");

        argv.Should().EndWith("-", "the stdin marker has to stay last");
        argv.Count(a => a == "-").Should().Be(1, "one marker, or the prompt source is ambiguous");

        var marker = argv.ToList().IndexOf("-");
        argv.ToList().IndexOf("-m").Should().BeLessThan(marker);
        argv.ToList().IndexOf("--profile").Should().BeLessThan(marker);
    }

    [Fact]
    public void Claude_streams_only_in_the_combination_its_cli_allows()
    {
        // Three constraints from `claude --help`, and the flags are useless apart:
        //   --output-format          "only works with --print"
        //   --include-partial-messages / --verbose   "(--print and --output-format=stream-json)"
        // Sending stream-json without -p is rejected, so the print flag is not optional here.
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor("opus", CodegenEffort.High, stream: true);

        argv.Should().Contain("-p");
        argv.Should().ContainInOrder("--output-format", "stream-json");
        argv.Should().Contain("--include-partial-messages");
        argv.Should().Contain("--verbose");
    }

    [Fact]
    public void A_non_streaming_claude_run_asks_for_one_json_result_and_no_stream()
    {
        // The fallback path. stream-json changes the OUTPUT SHAPE into JSONL, which the one-shot reader
        // does not parse; `json` is one object carrying the reply and its usage, which it does. Plain
        // text reported no usage at all, so a one-shot run read as free.
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor("opus", CodegenEffort.High);

        argv.Should().Contain("-p");
        argv.Should().ContainInOrder("--output-format", "json");
        argv.Should().NotContain("stream-json");
        argv.Should().NotContain("--include-partial-messages");
    }

    [Fact]
    public void An_unset_model_or_effort_leaves_the_cli_on_its_own_defaults()
    {
        // A blank model must not become an empty argument: `--model ""` is not "use the default", it is
        // a model named empty string, and the CLI rejects it.
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor(null, CodegenEffort.Default).ToList();

        argv.Should().NotContain("--model");
        argv.Should().NotContain("--effort");

        // Exactly one empty argument, and it is the tool list: `claude --help` documents `--tools ""`
        // as "disable all tools". Anywhere else an empty argument is a value somebody forgot.
        argv.Count(a => a.Length == 0).Should().Be(1);
        argv[argv.IndexOf(string.Empty) - 1].Should().Be("--tools");
    }

    // -- a prompt to answer, not an agent to run ------------------------------------------------------

    /// <summary>
    /// What every Claude call must carry, read out of <c>claude --help</c> (2.1.270) on 2026-09-13.
    /// Measured the same day from an empty folder: a plain <c>claude -p</c> carried 24,882 tokens of
    /// Claude Code's own harness before a byte of the prompt; these flags plus a system prompt, 540.
    /// </summary>
    private static readonly string[] PromptOnly =
        ["--safe-mode", "--strict-mcp-config", "--disable-slash-commands", "--no-session-persistence"];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Claude_is_sent_a_prompt_to_answer_and_none_of_its_own_harness(bool stream)
    {
        var argv = AgentCliAdapter.ClaudeCode.ArgumentsFor("opus", CodegenEffort.High, stream: stream);

        argv.Should().Contain(PromptOnly, "Claude Code's tools, plugins, MCP servers, CLAUDE.md and saved "
            + "sessions are all tokens a generation pays for and never uses");
        argv.Should().ContainInOrder("--tools", string.Empty);

        // The tempting flag, and the wrong one: --bare reads ONLY an API key, so on a subscription
        // sign-in — the reason to pick this provider at all — it answers nothing. Measured.
        argv.Should().NotContain("--bare");
    }

    [Fact]
    public void Codex_runs_without_saving_a_session_and_keeps_the_users_profile()
    {
        var argv = AgentCliAdapter.Codex.ArgumentsFor("gpt-x", cliProfile: "work").ToList();

        argv.Should().Contain("--ephemeral");
        argv.IndexOf("--ephemeral").Should().BeLessThan(argv.IndexOf("-"), "a flag after the marker is prompt");

        // A profile is layered on top of the user config, so ignoring that file discards the profile.
        argv.Should().NotContain("--ignore-user-config");
    }

    [Fact]
    public void The_pack_goes_to_claude_as_its_system_prompt_and_not_down_stdin()
    {
        // THE CACHE DEFECT, pinned. Flattened into stdin, the pack and the task were one block that ended
        // differently every call, so every call wrote the pack to the cache and none read it back: the
        // live runs of 2026-09-12 wrote 52-70k tokens on every call. As the system prompt it is a block of
        // its own, and the second call reads it — measured, 17,254 written then 16,736 read.
        using var scratch = new Scratch();
        var client = new AgentCliCodegenClient(AgentCliAdapter.ClaudeCode, scratchDirectory: scratch.Path);
        var request = Request(pack: "THE SHARED PACK — with an en dash", role: "YOU ARE THE PLANNER");

        var file = client.Prepare(request);

        file.Should().NotBeNull();
        File.ReadAllText(file!, Encoding.UTF8).Should().Be(request.SystemContext,
            "the file is the cached prefix, so it must be the pack byte for byte");

        var argv = client.ProcessFor("claude", stream: true, file).ArgumentList.ToList();
        argv.Should().ContainInOrder("--system-prompt-file", file);

        var stdin = AgentCliCodegenClient.FlattenPrompt(request, includeSystemContext: false);
        stdin.Should().NotContain("THE SHARED PACK", "sending it twice bills it twice");
        stdin.Should().Contain("YOU ARE THE PLANNER", "the role differs per call, so it stays out of the prefix");
        stdin.Should().Contain("build me a footprint");
    }

    [Fact]
    public void Every_call_carrying_the_same_pack_names_the_same_file()
    {
        // The cache keys on bytes. The planner, each builder, each repair and each critic in a run carry
        // one pack and must land on one file; a different pack must never be served a stale one.
        using var scratch = new Scratch();
        var client = new AgentCliCodegenClient(AgentCliAdapter.ClaudeCode, scratchDirectory: scratch.Path);

        var planner = client.Prepare(Request(pack: "PACK A", role: "planner"));
        var builder = client.Prepare(Request(pack: "PACK A", role: "builder"));
        var other = client.Prepare(Request(pack: "PACK B", role: "planner"));

        builder.Should().Be(planner);
        other.Should().NotBe(planner);
    }

    [Fact]
    public void Codex_keeps_the_pack_at_the_front_of_its_prompt()
    {
        // No system-prompt file for Codex — and none needed: OpenAI's cache matches a raw token prefix,
        // which the flattened prompt already leads with.
        using var scratch = new Scratch();
        var client = new AgentCliCodegenClient(AgentCliAdapter.Codex, scratchDirectory: scratch.Path);
        var request = Request(pack: "THE SHARED PACK", role: "YOU ARE THE PLANNER");

        client.Prepare(request).Should().BeNull();

        var stdin = AgentCliCodegenClient.FlattenPrompt(request);
        stdin.Should().StartWith("THE SHARED PACK");
        stdin.IndexOf("YOU ARE THE PLANNER", StringComparison.Ordinal)
            .Should().BeGreaterThan(stdin.IndexOf("THE SHARED PACK", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cli_runs_in_an_empty_folder_of_its_own()
    {
        // It inherited the app's working directory: a developer's build output inside a checkout whose
        // CLAUDE.md and hooks an agent CLI would load, or wherever a shortcut started the terminal.
        using var scratch = new Scratch();
        var client = new AgentCliCodegenClient(AgentCliAdapter.ClaudeCode, scratchDirectory: scratch.Path);

        client.Prepare(Request(pack: "PACK", role: null));
        var psi = client.ProcessFor("claude", stream: true);

        psi.WorkingDirectory.Should().Be(client.WorkingDirectory);
        psi.WorkingDirectory.Should().NotBe(Environment.CurrentDirectory);
        Directory.EnumerateFileSystemEntries(psi.WorkingDirectory).Should().BeEmpty(
            "nothing in it may be read as instructions — the pack lives beside it, not inside it");
    }

    [Fact]
    public void A_json_result_is_read_with_the_whole_prompt_and_its_cached_share()
    {
        // Shape captured from `claude -p --output-format json` on 2026-09-13.
        const string stdout = """
            {"type":"result","subtype":"success","is_error":false,"num_turns":1,"result":"OK",
             "usage":{"input_tokens":9,"cache_creation_input_tokens":506,"cache_read_input_tokens":16736,"output_tokens":42}}
            """;

        var result = AgentCliCodegenClient.ReadResult(stdout);

        result.Should().NotBeNull();
        result!.IsError.Should().BeFalse();
        result.Text.Should().Be("OK");

        // The stream parser's convention: input is the whole prompt, cache included, with the cached part
        // beside it — otherwise a cached call reads as a nine-token prompt.
        result.Usage.InputTokens.Should().Be(9 + 506 + 16736);
        result.Usage.CachedInputTokens.Should().Be(16736);
        result.Usage.OutputTokens.Should().Be(42);
    }

    [Fact]
    public void A_refusal_in_a_json_result_is_a_failure_with_its_own_words()
    {
        // A spent usage window exits non-zero AND explains itself on stdout. The words are what a caller
        // waiting out the window reads the reset time from.
        const string stdout = """{"type":"result","is_error":true,"result":"Claude usage limit reached. Your limit resets 3pm"}""";

        var result = AgentCliCodegenClient.ReadResult(stdout);

        result.Should().NotBeNull();
        result!.IsError.Should().BeTrue();
        result.Text.Should().Contain("resets 3pm");

        AgentCliCodegenClient.ReadResult("not json at all").Should().BeNull(
            "output that is not the result object goes to the plain-text path, not to an exception");
    }

    private static StrategyCodegenRequest Request(string pack, string? role) =>
        new(pack, [new CodegenMessage(CodegenRole.User, "build me a footprint")], role);

    /// <summary>A scratch directory of the test's own, so no test writes into the real temp folder.</summary>
    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "daxalgo-cli-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    // -- the fourth table: the curated model lists ------------------------------------------------

    [Fact]
    public void The_configured_model_is_always_offered_and_offered_first()
    {
        // The contract the picker depends on. A configured model missing from its own dropdown reads as
        // "that setting is invalid" -- and the ids most at risk are exactly the ones not on any curated
        // list, because those are the new ones somebody typed in deliberately.
        var offered = AiModelCatalog.Offer("anthropic", "claude-something-unreleased");

        offered.Should().HaveElementAt(0, "claude-something-unreleased");
        offered.Should().Contain(AiModelCatalog.For("anthropic"),
            "the curated list is added to, not replaced");

        // Already-listed ids are promoted rather than duplicated.
        var promoted = AiModelCatalog.Offer("anthropic", "claude-sonnet-5");
        promoted.Should().HaveElementAt(0, "claude-sonnet-5");
        promoted.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void The_curated_anthropic_list_still_names_the_current_flagship()
    {
        // It went stale once. The list called claude-opus-4-8 the "most capable Opus tier" while
        // appsettings.json was already shipping a provider configured for claude-opus-5, so the picker
        // omitted the very model the app itself pointed at. Free text meant nobody was blocked, which
        // is precisely why it could sit there unnoticed.
        var models = AiModelCatalog.For("anthropic");

        models.Should().Contain("claude-opus-5-5");
        models.Should().OnlyHaveUniqueItems();
        models.Should().NotContain(string.Empty);

        // The CLI provider shares the list, so the API and the installed CLI never offer different
        // menus for the same vendor.
        AiModelCatalog.For("claude-cli").Should().BeEquivalentTo(models);
    }

    [Fact]
    public void Providers_without_a_curated_list_offer_nothing_rather_than_a_guess()
    {
        // Deliberate: these all expose a models endpoint, and the picker has a "refresh from provider"
        // that asks. A shipped guess would be stale within weeks -- the appsettings notes record one
        // stealth id that was free for exactly a week.
        foreach (var id in (string[])["openai", "deepseek", "xai", "openrouter", "ollama", "codex-cli"])
        {
            AiModelCatalog.For(id).Should().BeEmpty(
                "{0} should be asked rather than guessed at", id);
        }
    }

    [Fact]
    public void Every_adapter_names_a_bare_executable_rather_than_a_path()
    {
        // Resolved off PATH, so the app finds whatever the user installed rather than a location we
        // guessed. A path here would work on the machine it was written on and nowhere else.
        foreach (var adapter in AgentCliAdapter.All)
        {
            adapter.Executable.Should().NotBeNullOrWhiteSpace();
            adapter.Executable.Should().NotContain("/");
            adapter.Executable.Should().NotContain("\\");
            adapter.Executable.Should().NotEndWith(".exe", "the extension is the platform's business");

            adapter.ProviderId.Should().NotBeNullOrWhiteSpace();
            adapter.Arguments.Should().NotBeEmpty("a CLI invoked bare would open its interactive mode "
                + "and hang the turn until the timeout");
        }

        AgentCliAdapter.All.Select(a => a.ProviderId).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void EveryRedirectedStreamIsUtf8()
    {
        // All three, and the OUTPUT half is the one that was missing. A redirected pipe inherits the
        // Windows console code page, so a model writing an en dash sends back E2 80 93 and the client
        // decoded it as CP437: the generated source then held the literal string "ΓÇô".
        //
        // It compiled. It cleared every rung. It reached a window and showed
        // "77671.75 ΓÇô 77671.75" to a user, which is where it was finally seen -- in a
        // screenshot of a unit that had passed everything. The input half was fixed long ago because
        // Codex REJECTS invalid UTF-8 and fails loudly; nothing rejects a corrupted reply.
        var client = new AgentCliCodegenClient(AgentCliAdapter.ClaudeCode);
        var psi = client.ProcessFor("claude", stream: false);

        psi.StandardInputEncoding.Should().BeOfType<UTF8Encoding>();
        psi.StandardOutputEncoding.Should().BeOfType<UTF8Encoding>(
            "a reply decoded as the console code page reaches the user as mojibake and nothing rejects it");
        psi.StandardErrorEncoding.Should().BeOfType<UTF8Encoding>();
    }
}
