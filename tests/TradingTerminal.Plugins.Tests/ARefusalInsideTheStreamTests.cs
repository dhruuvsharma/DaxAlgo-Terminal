using System.Net;
using System.Net.Http;
using System.Text.Json;
using FluentAssertions;
using TradingTerminal.Core.Strategies.Authoring;
using TradingTerminal.Infrastructure.Strategies.Authoring;
using Xunit;

namespace TradingTerminal.Plugins.Tests;

/// <summary>
/// A provider can refuse INSIDE a stream that opened with 200. NVIDIA NIM does exactly that for an
/// overloaded model — one event, <c>data: {"error":{"message":"Service temporarily overloaded",
/// "type":"service_unavailable","code":503}}</c> — and on 2026-09-24 it ended a Battlefield run in its first
/// second as "returned no message content", unretried. It is read as the refusal it is: retried when it
/// passes and nothing had been written yet, and otherwise reported in the provider's own words.
/// </summary>
public sealed class ARefusalInsideTheStreamTests
{
    private const string Overloaded = """data: {"error":{"message":"Service temporarily overloaded","type":"service_unavailable","code":503}}""";

    [Fact]
    public void The_refusal_is_read_with_its_code_and_message_and_an_overload_named_only_in_words_is_one()
    {
        OpenAiCompatibleCodegenClient.StreamedError(Parse(Overloaded)).Should().Be((503, "Service temporarily overloaded"));
        OpenAiCompatibleCodegenClient.StreamedError(Parse("""data: {"error":{"message":"Model is overloaded, try later"}}""")).Should().Be((503, "Model is overloaded, try later"));
        OpenAiCompatibleCodegenClient.StreamedError(Parse("""data: {"error":{"code":"429","message":"slow down"}}""")).Should().Be((429, "slow down"));
        OpenAiCompatibleCodegenClient.StreamedError(Parse("""data: {"choices":[{"delta":{"content":"x"}}]}""")).Should().BeNull();
    }

    [Theory]
    [InlineData(503, "Service temporarily overloaded", true)]
    [InlineData(502, "bad gateway", true)]
    [InlineData(429, "slow down", true)]
    [InlineData(400, "max_tokens is too large", false)]
    [InlineData(402, "Insufficient account funds", false)]
    public void Only_what_passes_is_sent_again(int code, string message, bool retry)
    {
        OpenAiCompatibleCodegenClient.IsRetryableStreamedError(code, message).Should().Be(retry);
    }

    [Fact]
    public async Task An_overloaded_model_is_asked_again_and_its_answer_arrives()
    {
        var handler = new Scripted(
            Overloaded + "\n\ndata: [DONE]\n\n",
            Chunk("```csharp\n// file: Unit.cs\npublic sealed class Unit { }\n```") + "\n\ndata: [DONE]\n\n");

        var response = await Drain(handler);

        handler.Calls.Should().Be(2, "the refusal came before anything was written, so the request was sent again");
        response.Success.Should().BeTrue(response.Error);
        response.RawText.Should().Contain("class Unit");
    }

    [Fact]
    public async Task A_refusal_that_will_not_pass_is_reported_in_the_provider_s_words_at_once()
    {
        var handler = new Scripted("""data: {"error":{"message":"max_tokens is too large for this model","code":400}}""" + "\n\ndata: [DONE]\n\n");

        var response = await Drain(handler);

        handler.Calls.Should().Be(1);
        response.Success.Should().BeFalse();
        response.Error.Should().Contain("400").And.Contain("max_tokens is too large for this model").And.NotContain("no message content");
    }

    [Fact]
    public async Task A_request_that_never_reached_the_provider_is_sent_again()
    {
        // 2026-09-24: DNS stopped resolving integrate.api.nvidia.com for a while, and every call failed at once.
        var handler = new DropsFirst(Chunk("```csharp\n// file: Unit.cs\npublic sealed class Unit { }\n```") + "\n\ndata: [DONE]\n\n");

        var response = await Drain(handler);

        handler.Calls.Should().Be(2);
        response.Success.Should().BeTrue(response.Error);
    }

    /// <summary>Fails the first request the way a DNS outage does, then answers.</summary>
    private sealed class DropsFirst(string body) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Interlocked.Increment(ref _calls) == 1
                ? throw new HttpRequestException("No such host is known. (example.invalid:443)")
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line["data: ".Length..]).RootElement.Clone();

    private static string Chunk(string content) =>
        "data: " + JsonSerializer.Serialize(new { choices = new[] { new { delta = new { content }, finish_reason = (string?)null } } });

    private static async Task<StrategyCodegenResponse> Drain(HttpMessageHandler handler)
    {
        using var http = new HttpClient(handler);
        var client = new OpenAiCompatibleCodegenClient(http, "gateway", "Gateway", "https://example.invalid/v1", "m", "k");

        StrategyCodegenResponse? completed = null;
        await foreach (var evt in client.StreamAsync(new StrategyCodegenRequest("ctx", [])))
            if (evt is CodegenEvent.Completed done) completed = done.Response;

        return completed!;
    }

    /// <summary>Answers each request with the next body, all as a 200 event stream.</summary>
    private sealed class Scripted(params string[] bodies) : HttpMessageHandler
    {
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var at = Interlocked.Increment(ref _calls) - 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(bodies[Math.Min(at, bodies.Length - 1)]) });
        }
    }
}
