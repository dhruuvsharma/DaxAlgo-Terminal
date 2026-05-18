using FluentAssertions;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Infrastructure.AiAnalyst;
using Xunit;

namespace TradingTerminal.Tests.AiAnalyst;

public sealed class NullAiAnalystClientTests
{
    [Fact]
    public void Reports_not_available()
    {
        var client = new NullAiAnalystClient();
        client.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task Always_returns_no_call_unavailable_report()
    {
        var client = new NullAiAnalystClient();
        var request = new AnalystRequest(
            Symbol: "ES",
            Timeframe: "1h",
            BarCount: 1,
            Provider: "openai",
            Model: "gpt-4o",
            VisionModel: "gpt-4o",
            Bars: new[] { new AnalystBar(DateTime.UtcNow, 1, 2, 0.5, 1.5, 100) });

        var report = await client.RunAsync(request);

        report.Decision.Should().Be(AiAnalystDecision.NoCall);
        report.PatternChartPngBase64.Should().BeEmpty();
        report.TrendChartPngBase64.Should().BeEmpty();
        report.Justification.Should().Contain("unavailable");
    }
}
