using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Infrastructure.Research;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// Contract tests for the paper-ingest HTTP client, mirroring <c>HttpAiAnalystClientTests</c>: the
/// loopback-only guard, the disabled/empty-URL short-circuits, and the snake_case response mapping —
/// all under the never-throw seam contract (every failure folds into <c>PaperIngestResult.Empty</c>).
/// </summary>
public sealed class HttpPaperIngestClientTests
{
    private const string PaperUrl = "https://arxiv.org/abs/2507.22712";

    [Fact]
    public async Task Rejects_non_loopback_sidecar_url_without_throwing()
    {
        // A non-loopback base URL must never reach the network — the sidecar is loopback-only.
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://10.0.0.5:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new InvalidOperationException("must not issue a request to a non-loopback URL")));

        var result = await client.ResolveAsync(PaperUrl);

        result.Resolved.Should().BeFalse();
        result.Paper.Should().BeNull();
        result.Repos.Should().BeEmpty();
        result.Error.Should().Contain("loopback");
    }

    [Fact]
    public async Task Returns_empty_when_disabled()
    {
        var options = StubOptions(enabled: false, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new InvalidOperationException("should not be called when disabled")));

        var result = await client.ResolveAsync(PaperUrl);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("disabled");
    }

    [Fact]
    public async Task Returns_empty_on_non_2xx_response()
    {
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.ResolveAsync(PaperUrl);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("500");
    }

    [Fact]
    public async Task Returns_empty_when_endpoint_unreachable()
    {
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:1");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new HttpRequestException("connection refused")));

        var result = await client.ResolveAsync(PaperUrl);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("unreachable");
    }

    [Fact]
    public async Task Maps_resolved_paper_and_repos_from_sidecar_response()
    {
        var payload = """
        {
            "resolved": true,
            "paper": { "arxiv_id": "2507.22712", "title": "Order-Flow Imbalance", "url": "https://arxiv.org/abs/2507.22712" },
            "repos": [
                { "git_url": "https://github.com/example/repo.git", "commit": "abc123" },
                { "git_url": "", "commit": "skipme" }
            ],
            "error": null
        }
        """;
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }));

        var result = await client.ResolveAsync(PaperUrl);

        result.Resolved.Should().BeTrue();
        result.Paper!.ArxivId.Should().Be("2507.22712");
        result.Paper.Title.Should().Be("Order-Flow Imbalance");
        result.Repos.Should().ContainSingle("repos with empty git url/commit are filtered out");
        result.Repos[0].GitUrl.Should().Be("https://github.com/example/repo.git");
        result.Repos[0].Commit.Should().Be("abc123");
    }

    private static IOptionsMonitor<ResearchReproOptions> StubOptions(bool enabled, string sidecarBaseUrl)
    {
        var monitor = Substitute.For<IOptionsMonitor<ResearchReproOptions>>();
        monitor.CurrentValue.Returns(new ResearchReproOptions
        {
            Enabled = enabled,
            SidecarBaseUrl = sidecarBaseUrl,
            SidecarTimeoutSeconds = 5,
        });
        return monitor;
    }

    private static HttpPaperIngestClient MakeClient(
        IOptionsMonitor<ResearchReproOptions> options, HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpPaperIngestClient.HttpClientName)
               .Returns(_ => new HttpClient(handler));
        return new HttpPaperIngestClient(factory, options, NullLogger<HttpPaperIngestClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_factory(request));
    }
}
