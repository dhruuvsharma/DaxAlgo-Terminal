using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// Reading the OpenCode CLI's <c>--format json</c> events.
///
/// <para>It is the only way to reach that account's free models — Zen's HTTP gateway answers
/// <c>403 FreeTierError</c> ("can only be used from within OpenCode") to every other client — so the
/// parser has to be right rather than approximately right.</para>
///
/// <para>The lines below are shapes captured from the real CLI (v1.18.31) on 2026-09-17.</para>
/// </summary>
public sealed class TheOpenCodeCliStreamTests
{
    private static (string Text, CodegenUsage Usage, string? Error, List<CodegenEvent> Events) Read(params string[] lines)
    {
        var accumulator = new OpenCodeEventAccumulator();
        var events = new List<CodegenEvent>();

        foreach (var line in lines)
            events.AddRange(accumulator.Consume(JsonDocument.Parse(line).RootElement.Clone()));

        return (accumulator.Text, accumulator.Usage, accumulator.Error, events);
    }

    private static string Text(string id, string text) =>
        """{"type":"text","sessionID":"ses_1","part":{"id":"ID","messageID":"msg_1","type":"text","text":TEXT}}"""
            .Replace("ID", id, StringComparison.Ordinal)
            .Replace("TEXT", JsonSerializer.Serialize(text), StringComparison.Ordinal);

    private static string StepFinish(int input, int output) =>
        """{"type":"step_finish","part":{"id":"prt_f","reason":"stop","type":"step-finish","tokens":{"total":TOTAL,"input":IN,"output":OUT,"reasoning":0,"cache":{"write":0,"read":0}},"cost":0}}"""
            .Replace("TOTAL", (input + output).ToString(), StringComparison.Ordinal)
            .Replace("IN", input.ToString(), StringComparison.Ordinal)
            .Replace("OUT", output.ToString(), StringComparison.Ordinal);

    [Fact]
    public void A_part_that_grows_is_read_as_the_text_it_added()
    {
        // OpenCode rewrites a part as it streams: the same id arrives again carrying everything it has so
        // far. Treated as deltas, the answer would be repeated once per update.
        var (text, _, _, events) = Read(
            Text("prt_a", "```json"),
            Text("prt_a", "```json\n{\"contract\""),
            Text("prt_a", "```json\n{\"contract\": {}}\n```"));

        text.Should().Be("```json\n{\"contract\": {}}\n```");
        events.OfType<CodegenEvent.TextDelta>().Select(d => d.Text)
            .Should().Equal("```json", "\n{\"contract\"", ": {}}\n```");
    }

    [Fact]
    public void Several_parts_are_the_answer_in_the_order_they_appeared()
    {
        var (text, _, _, _) = Read(Text("prt_a", "first "), Text("prt_b", "second"), Text("prt_a", "first "));

        text.Should().Be("first second");
    }

    [Fact]
    public void The_step_that_finishes_carries_what_the_run_was_billed()
    {
        // Measured on the real CLI: input 9,126 / output 14 for a two-word answer, because the CLI sends
        // its own harness with every call. A run that reported zero made the token counter useless.
        var (_, usage, _, events) = Read(Text("prt_a", "ok"), StepFinish(9_126, 14));

        usage.InputTokens.Should().Be(9_126);
        usage.OutputTokens.Should().Be(14);
        events.OfType<CodegenEvent.UsageUpdate>().Should().ContainSingle();
    }

    [Fact]
    public void Steps_add_up_across_a_multi_step_run()
    {
        var (_, usage, _, _) = Read(Text("prt_a", "a"), StepFinish(100, 10), Text("prt_b", "b"), StepFinish(200, 20));

        usage.InputTokens.Should().Be(300);
        usage.OutputTokens.Should().Be(30);
    }

    [Fact]
    public void Thinking_is_kept_apart_from_the_answer()
    {
        var (text, _, _, events) = Read(
            """{"type":"reasoning","part":{"id":"prt_r","type":"reasoning","text":"weighing the contract"}}""",
            Text("prt_a", "the answer"));

        text.Should().Be("the answer", "thinking is not the reply");
        events.OfType<CodegenEvent.ReasoningDelta>().Should().ContainSingle()
            .Which.Text.Should().Be("weighing the contract");
    }

    [Fact]
    public void An_error_event_is_what_the_run_failed_with()
    {
        var (_, _, error, _) = Read(
            """{"type":"error","error":{"message":"model not available on this account"}}""");

        error.Should().Contain("model not available");
    }
}
