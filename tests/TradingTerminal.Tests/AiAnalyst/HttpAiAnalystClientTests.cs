using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Notifications;
using Xunit;

namespace TradingTerminal.Tests.AiAnalyst;

public sealed class HttpAiAnalystClientTests
{
    [Fact]
    public async Task Returns_unavailable_when_options_disabled()
    {
        var options = StubOptions(enabled: false);
        var client = MakeClient(options, new StubHandler(_ => throw new InvalidOperationException("should not be called")));

        var report = await client.RunAsync(Request());

        report.Decision.Should().Be(AiAnalystDecision.NoCall);
        report.Justification.Should().Contain("disabled");
    }

    [Fact]
    public async Task Returns_unavailable_on_non_2xx_response()
    {
        var options = StubOptions(enabled: true);
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        var report = await client.RunAsync(Request());

        report.Decision.Should().Be(AiAnalystDecision.NoCall);
        report.Justification.Should().Contain("500");
    }

    [Fact]
    public async Task Maps_python_response_to_analyst_report()
    {
        var payload = """
        {
            "decision": "long",
            "forecast_horizon": "next 4 bars",
            "risk_reward_ratio": 2.1,
            "confidence": 0.72,
            "justification": "Pattern confirmed by trend agreement.",
            "indicator": { "summary": "RSI rising", "values": { "rsi_14": 58.2 } },
            "pattern":   { "pattern_name": "Bull Flag", "confidence": 0.8, "reasoning": "Clear flag." },
            "trend":     { "direction": "Up", "slope": 0.12, "channel_upper": 102.0,
                           "channel_lower": 98.0, "reasoning": "Channel sloping up." },
            "pattern_chart_png_base64": "AAA=",
            "trend_chart_png_base64":   "BBB=",
            "elapsed_ms": 1234
        }
        """;

        var options = StubOptions(enabled: true);
        var client = MakeClient(options, new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            }));

        var report = await client.RunAsync(Request());

        report.Decision.Should().Be(AiAnalystDecision.Long);
        report.ForecastHorizon.Should().Be("next 4 bars");
        report.RiskRewardRatio.Should().BeApproximately(2.1, 1e-9);
        report.Confidence.Should().BeApproximately(0.72, 1e-9);
        report.Pattern.PatternName.Should().Be("Bull Flag");
        report.Trend.Direction.Should().Be("Up");
        report.PatternChartPngBase64.Should().Be("AAA=");
        report.TrendChartPngBase64.Should().Be("BBB=");
        report.ElapsedMs.Should().Be(1234);
    }

    [Fact]
    public async Task Returns_unavailable_when_endpoint_unreachable()
    {
        var options = StubOptions(enabled: true);
        var client = MakeClient(options, new StubHandler(_ =>
            throw new HttpRequestException("connection refused")));

        var report = await client.RunAsync(Request());

        report.Decision.Should().Be(AiAnalystDecision.NoCall);
        report.Justification.Should().Contain("unreachable");
    }

    private static AnalystRequest Request() => new(
        Symbol: "ES",
        Timeframe: "1h",
        BarCount: 1,
        Provider: "openai",
        Model: "gpt-4o",
        VisionModel: "gpt-4o",
        Bars: new[] { new AnalystBar(DateTime.UtcNow, 1, 2, 0.5, 1.5, 100) });

    private static IOptionsMonitor<NotificationsOptions> StubOptions(bool enabled)
    {
        var monitor = Substitute.For<IOptionsMonitor<NotificationsOptions>>();
        monitor.CurrentValue.Returns(new NotificationsOptions
        {
            AiAnalyst = new AiAnalystOptions
            {
                Enabled = enabled,
                Endpoint = "http://127.0.0.1:1",
                Provider = "openai",
                Model = "gpt-4o",
                VisionModel = "gpt-4o",
                BarCount = 50,
                TimeoutSeconds = 5,
            },
        });
        return monitor;
    }

    private static HttpAiAnalystClient MakeClient(
        IOptionsMonitor<NotificationsOptions> options, HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(HttpAiAnalystClient.HttpClientName)
               .Returns(_ => new HttpClient(handler));
        return new HttpAiAnalystClient(factory, options, NullLogger<HttpAiAnalystClient>.Instance);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory) => _factory = factory;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(_factory(request));
    }
}
