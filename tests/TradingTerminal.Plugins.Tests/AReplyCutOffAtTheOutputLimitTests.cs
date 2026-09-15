using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A reply that stops at the provider's output limit in the middle of a file says so.
///
/// <para><b>Measured on Token Harbor's DeepSeek V4.1 Flash.</b> A page builder billed exactly 32,000
/// output tokens four times in a row and ended each reply inside an unclosed <c>```html</c> fence. The
/// client never read <c>finish_reason</c>, the extractor found no closed block, and the swarm reported
/// "returned no file" — which reads as a model ignoring instructions, not as a ceiling.</para>
/// </summary>
public sealed class AReplyCutOffAtTheOutputLimitTests
{
    [Fact]
    public async Task A_reply_cut_off_inside_a_block_fails_and_names_the_limit()
    {
        var response = await Drain(
            Chunk(content: "Here is the page.\n\n```html\n<!-- file: ui/index.html -->\n<html><body>"),
            Chunk(finish: "length"),
            Usage(prompt: 4_855, completion: 32_000));

        response.Success.Should().BeFalse();
        response.Error.Should().Contain("output limit").And.Contain("32,000");
        response.Usage!.OutputTokens.Should().Be(32_000, "the call was paid for, and the run's total must say so");
    }

    [Fact]
    public async Task A_reply_cut_off_after_its_files_are_whole_is_kept()
    {
        // Cut in the closing prose: every block is closed, so every file is complete.
        var response = await Drain(
            Chunk(content: "```csharp\n// file: Unit.cs\npublic sealed class Unit { }\n```\n\nThis unit subscribes to"),
            Chunk(finish: "length"),
            Usage(prompt: 900, completion: 8_000));

        response.Success.Should().BeTrue();
        response.FileList.Should().ContainSingle(f => f.Name == "Unit.cs");
    }

    [Fact]
    public async Task A_reply_that_finished_normally_is_untouched()
    {
        var response = await Drain(
            Chunk(content: "```html\n<!-- file: ui/index.html -->\n<html></html>\n```"),
            Chunk(finish: "stop"),
            Usage(prompt: 900, completion: 400));

        response.Success.Should().BeTrue();
        response.RawText.Should().Contain("<html></html>");
    }

    [Theory]
    [InlineData("no fences at all", false)]
    [InlineData("```csharp\nclass A {}\n```", false)]
    [InlineData("```csharp\nclass A {}\n```\n```html\n<html>", true)]
    public void Only_an_unclosed_fence_counts_as_cut_mid_block(string text, bool cut)
    {
        OpenAiCompatibleCodegenClient.IsCutMidBlock(text).Should().Be(cut);
    }

    private static string Chunk(string? content = null, string? finish = null) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content }, finish_reason = finish } },
        });

    private static string Usage(int prompt, int completion) =>
        "data: " + JsonSerializer.Serialize(new
        {
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = prompt, completion_tokens = completion },
        });

    private static async Task<StrategyCodegenResponse> Drain(params string[] events)
    {
        var body = string.Join("\n\n", events) + "\n\ndata: [DONE]\n\n";
        using var http = new HttpClient(new Answers(body));
        var client = new OpenAiCompatibleCodegenClient(http, "gateway", "Gateway", "https://example.invalid/v1", "m", "k");

        StrategyCodegenResponse? completed = null;
        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest("ctx", [])))
            if (evt is CodegenEvent.Completed done) completed = done.Response;

        return completed!;
    }

    private sealed class Answers(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }
}
