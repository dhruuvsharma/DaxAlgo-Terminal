using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Ai.MarketAnalyst;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Notifications;
using Xunit;

namespace TradingTerminal.Tests.AiAnalyst;

public sealed class AiAnalystViewModelTests
{
    [Fact]
    public void Defaults_to_unavailable_when_client_reports_unavailable()
    {
        var vm = MakeVm(out _, out _, available: false);

        vm.IsAvailable.Should().BeFalse();
        vm.LatestReport!.Decision.Should().Be(AiAnalystDecision.NoCall);
        vm.LatestReport!.Justification.Should().Contain("unavailable");
    }

    [Fact]
    public async Task Analyze_uses_repository_bars_and_surfaces_report()
    {
        var vm = MakeVm(out var analyst, out _, available: true);
        var report = SuccessReport();
        analyst.RunAsync(Arg.Any<AnalystRequest>(), Arg.Any<CancellationToken>()).Returns(report);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        vm.LatestReport.Should().Be(report);
        vm.History.Should().HaveCount(1);
        vm.IsRunning.Should().BeFalse();
        vm.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Analyze_short_circuits_when_client_unavailable()
    {
        var vm = MakeVm(out var analyst, out _, available: false);

        await vm.AnalyzeCommand.ExecuteAsync(null);

        analyst.DidNotReceive().RunAsync(Arg.Any<AnalystRequest>(), Arg.Any<CancellationToken>());
        vm.ErrorMessage.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Analyze_records_error_when_run_throws()
    {
        var vm = MakeVm(out var analyst, out _, available: true);
        analyst.RunAsync(Arg.Any<AnalystRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<AnalystReport>>(_ => throw new InvalidOperationException("boom"));

        await vm.AnalyzeCommand.ExecuteAsync(null);

        vm.ErrorMessage.Should().Contain("boom");
        vm.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void ClearHistory_empties_collection()
    {
        var vm = MakeVm(out _, out _, available: true);
        vm.History.Add(SuccessReport());
        vm.History.Add(SuccessReport());

        vm.ClearHistoryCommand.Execute(null);

        vm.History.Should().BeEmpty();
    }

    private static AnalystReport SuccessReport() => new(
        Decision: AiAnalystDecision.Long,
        ForecastHorizon: "next 4 bars",
        RiskRewardRatio: 2.0,
        Confidence: 0.7,
        Justification: "ok",
        Indicator: new IndicatorReport("rsi rising", new Dictionary<string, double>()),
        Pattern: new PatternReport("Bull Flag", 0.8, "clear"),
        Trend: new TrendReport("Up", 0.1, 101, 99, "channel up"),
        PatternChartPngBase64: "AAA=",
        TrendChartPngBase64: "BBB=",
        ElapsedMs: 1000);

    private static AiAnalystViewModel MakeVm(
        out IAiAnalystClient analyst, out IMarketDataRepository repo, bool available)
    {
        analyst = Substitute.For<IAiAnalystClient>();
        analyst.IsAvailable.Returns(available);

        repo = Substitute.For<IMarketDataRepository>();
        repo.GetHistoricalBarsAsync(Arg.Any<Contract>(), Arg.Any<BrokerKind>(), Arg.Any<BarSize>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new[] { new Bar(DateTime.UtcNow.AddHours(-1), 1, 2, 0.5, 1.5, 100) });

        var selector = Substitute.For<IBrokerSelector>();
        selector.Connected.Returns(new[] { BrokerKind.InteractiveBrokers });

        var options = Substitute.For<IOptionsMonitor<NotificationsOptions>>();
        options.CurrentValue.Returns(new NotificationsOptions
        {
            AiAnalyst = new AiAnalystOptions
            {
                Enabled = available,
                Provider = "openai",
                Model = "gpt-4o",
                VisionModel = "gpt-4o",
                BarCount = 10,
                TimeoutSeconds = 5,
            },
        });

        return new AiAnalystViewModel(analyst, repo, selector, options, NullLogger<AiAnalystViewModel>.Instance);
    }
}
