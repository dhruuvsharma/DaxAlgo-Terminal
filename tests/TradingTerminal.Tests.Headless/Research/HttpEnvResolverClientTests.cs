using System.Net;
using System.Net.Http;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Research;
using TradingTerminal.Infrastructure.Research;
using Xunit;

namespace TradingTerminal.Tests.Research;

/// <summary>
/// Contract tests for the env-resolver HTTP client, mirroring <c>HttpPaperIngestClientTests</c>: the
/// loopback-only guard, the disabled/empty-input short-circuits, and the snake_case response mapping —
/// all under the never-throw seam contract (every failure folds into <c>MinimalReproPlan.Empty</c>).
/// </summary>
public sealed class HttpEnvResolverClientTests
{
    private static readonly RepoRef Repo = new("https://github.com/example/repo.git", "abc1234");

    [Fact]
    public async Task Rejects_non_loopback_sidecar_url_without_throwing()
    {
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://10.0.0.5:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new InvalidOperationException("must not issue a request to a non-loopback URL")));

        var result = await client.ResolvePlanAsync(Repo);

        result.Resolved.Should().BeFalse();
        result.Plan.Should().BeNull();
        result.Error.Should().Contain("loopback");
    }

    [Fact]
    public async Task Returns_empty_when_disabled()
    {
        var options = StubOptions(enabled: false, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new InvalidOperationException("should not be called when disabled")));

        var result = await client.ResolvePlanAsync(Repo);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("disabled");
    }

    [Fact]
    public async Task Returns_empty_when_commit_pin_missing()
    {
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new InvalidOperationException("must not call the sidecar without a commit pin")));

        var result = await client.ResolvePlanAsync(new RepoRef("https://github.com/example/repo.git", ""));

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("commit");
    }

    [Fact]
    public async Task Returns_empty_on_non_2xx_response()
    {
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var result = await client.ResolvePlanAsync(Repo);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("500");
    }

    [Fact]
    public async Task Returns_empty_when_endpoint_unreachable()
    {
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:1");
        var client = MakeClient(options, new StubHandler(_ =>
            throw new HttpRequestException("connection refused")));

        var result = await client.ResolvePlanAsync(Repo);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("unreachable");
    }

    [Fact]
    public async Task Maps_resolved_plan_from_snake_case_sidecar_response()
    {
        var payload = """
        {
            "image": "python:3.11-slim",
            "setup_commands": ["pip install --no-cache-dir -r requirements.txt"],
            "entrypoint": "python repro.py --out $RESULT_JSON",
            "declared_data_deps": ["binance"],
            "env_hash": "deadbeefcafe",
            "error": null
        }
        """;
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }));

        var result = await client.ResolvePlanAsync(Repo);

        result.Resolved.Should().BeTrue();
        result.Plan.Should().NotBeNull();
        result.Plan!.Image.Should().Be("python:3.11-slim");
        result.Plan.SetupCommands.Should().ContainSingle()
            .Which.Should().Be("pip install --no-cache-dir -r requirements.txt");
        result.Plan.Entrypoint.Should().Be("python repro.py --out $RESULT_JSON");
        result.Plan.DeclaredDataDeps.Should().ContainSingle().Which.Should().Be("binance");
        result.Plan.EnvHash.Value.Should().Be("deadbeefcafe");
    }

    [Fact]
    public async Task Maps_error_response_to_empty()
    {
        var payload = """
        { "image": "", "setup_commands": [], "entrypoint": "", "declared_data_deps": [], "env_hash": "", "error": "No runnable entrypoint." }
        """;
        var options = StubOptions(enabled: true, sidecarBaseUrl: "http://127.0.0.1:8000");
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }));

        var result = await client.ResolvePlanAsync(Repo);

        result.Resolved.Should().BeFalse();
        result.Error.Should().Contain("entrypoint");
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

    private static HttpEnvResolverClient MakeClient(
        IOptionsMonitor<ResearchReproOptions> options, HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpEnvResolverClient.HttpClientName)
               .Returns(_ => new HttpClient(handler));
        return new HttpEnvResolverClient(factory, options, NullLogger<HttpEnvResolverClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_factory(request));
    }
}
