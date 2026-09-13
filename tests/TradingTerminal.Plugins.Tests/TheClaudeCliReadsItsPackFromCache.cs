using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;
using Xunit.Abstractions;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// The claim the agent-CLI client now rests on, checked against the installed Claude Code rather than
/// against its <c>--help</c>: the pack is cached on the first call and READ on the next.
///
/// <para><b>Env-gated, because it spends the user's own quota.</b> Set <c>HYPERION_CLI_LIVE=1</c> to run
/// it. Three calls against the real context pack on Haiku by default (<c>HYPERION_CLI_MODEL</c> to
/// change it) — a few cents at list price. Nothing else in the suite can catch a CLI that stops accepting
/// one of the flags, or a flag that is accepted and quietly stops the cache holding.</para>
///
/// <para>What it replaces, measured on the live runs of 2026-09-12: every call wrote 52–70k tokens to the
/// cache and the only read was Claude Code's own 16k prompt, because the pack went down stdin as part of
/// one block that ended differently every time.</para>
/// </summary>
public sealed class TheClaudeCliReadsItsPackFromCache(ITestOutputHelper output)
{
    [Fact]
    public async Task The_second_call_reads_the_pack_the_first_one_wrote()
    {
        if (Environment.GetEnvironmentVariable("HYPERION_CLI_LIVE") != "1") return;

        var client = new AgentCliCodegenClient(
            AgentCliAdapter.ClaudeCode,
            timeout: TimeSpan.FromMinutes(5),
            model: Environment.GetEnvironmentVariable("HYPERION_CLI_MODEL") is { Length: > 0 } model ? model : "haiku");

        client.IsAvailable.Should().BeTrue("claude must be on PATH for a live run");

        // The real pack, so what is measured is what a run actually sends.
        var pack = StrategyContextPack.Load().SystemPrompt;

        StrategyCodegenRequest Ask(string role, string word) => new(
            pack,
            [new CodegenMessage(CodegenRole.User, $"Do not write code. Reply with the single word {word}.")],
            role);

        // Streamed, as every swarm call is. Different roles, as the planner and a builder differ.
        var (first, firstUsage) = await CodegenStream.DrainAsync(client, Ask("You are the planner.", "ALPHA"));
        Report("stream #1", first, firstUsage);

        var (second, secondUsage) = await CodegenStream.DrainAsync(client, Ask("You are a builder.", "BRAVO"));
        Report("stream #2", second, secondUsage);

        // And the one-shot path, which now reports usage at all.
        var third = await client.GenerateAsync(Ask("You are a critic.", "CHARLIE"));
        Report("one-shot", third, third.Usage ?? CodegenUsage.None);

        first.Success.Should().BeTrue(first.Error);
        second.Success.Should().BeTrue(second.Error);
        third.Success.Should().BeTrue(third.Error);

        second.RawText.Should().Contain("BRAVO", "the reply is still read off the stream");

        secondUsage.InputTokens.Should().BeGreaterThan(pack.Length / 8,
            "the pack must actually have reached the model as its system prompt");
        secondUsage.CachedInputTokens.Should().BeGreaterThan(secondUsage.InputTokens / 2,
            "the pack is most of the prompt, and the second call must read it from the cache the first wrote");

        third.Usage.Should().NotBeNull();
        third.Usage!.IsReported.Should().BeTrue("a one-shot run that reports nothing reads as free");
        third.Usage.CachedInputTokens.Should().BeGreaterThan(third.Usage.InputTokens / 2);
    }

    private void Report(string label, StrategyCodegenResponse response, CodegenUsage usage) =>
        output.WriteLine(
            $"{label,-10} ok={response.Success} in={usage.InputTokens:N0} cached={usage.CachedInputTokens:N0} "
            + $"out={usage.OutputTokens:N0} reply={Short(response.RawText ?? response.Error)}");

    private static string Short(string? text) =>
        string.IsNullOrWhiteSpace(text) ? "(none)" : text.Length <= 60 ? text.Trim() : text.Trim()[..60] + "…";
}
