using System.Reactive.Subjects;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.AiAnalyst;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Notifications;
using TradingTerminal.Infrastructure.AiAnalyst;
using TradingTerminal.Infrastructure.Notifications;
using TradingTerminal.Infrastructure.Notifications.AiAnalyst;
using Xunit;

namespace TradingTerminal.Tests.AiAnalyst;

public sealed class AiAnalystEnricherTests
{
    [Fact]
    public void ShouldRun_only_for_signal_and_trade_kinds_when_enabled()
    {
        var enricher = MakeEnricher(includeInEnricher: true, available: true, out _, out _);

        enricher.ShouldRun(Notification(NotificationKind.Signal)).Should().BeTrue();
        enricher.ShouldRun(Notification(NotificationKind.Trade)).Should().BeTrue();
        enricher.ShouldRun(Notification(NotificationKind.IdleSignal)).Should().BeFalse();
        enricher.ShouldRun(Notification(NotificationKind.Test)).Should().BeFalse();
        enricher.ShouldRun(Notification(NotificationKind.AlgoArmed)).Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_false_when_include_in_enricher_off()
    {
        var enricher = MakeEnricher(includeInEnricher: false, available: true, out _, out _);
        enricher.ShouldRun(Notification(NotificationKind.Signal)).Should().BeFalse();
    }

    [Fact]
    public void ShouldRun_false_when_analyst_unavailable()
    {
        var enricher = MakeEnricher(includeInEnricher: true, available: false, out _, out _);
        enricher.ShouldRun(Notification(NotificationKind.Signal)).Should().BeFalse();
    }

    [Fact]
    public async Task Returns_original_when_analyst_throws()
    {
        var enricher = MakeEnricher(includeInEnricher: true, available: true, out var analyst, out _);
        analyst.RunAsync(Arg.Any<AnalystRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<AnalystReport>>(_ => throw new InvalidOperationException("boom"));

        var original = Notification(NotificationKind.Signal);
        var enriched = await enricher.EnrichAsync(original, CancellationToken.None);

        enriched.Should().Be(original);
    }

    [Fact]
    public async Task Returns_original_on_timeout()
    {
        var enricher = MakeEnricher(includeInEnricher: true, available: true, out var analyst, out _);
        analyst.RunAsync(Arg.Any<AnalystRequest>(), Arg.Any<CancellationToken>())
               .Returns<Task<AnalystReport>>(async ci =>
               {
                   await Task.Delay(TimeSpan.FromSeconds(30), ci.Arg<CancellationToken>());
                   throw new TimeoutException();
               });

        var original = Notification(NotificationKind.Signal);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var enriched = await enricher.EnrichAsync(original, cts.Token);

        enriched.Should().Be(original);
    }

    [Fact]
    public async Task Appends_verdict_line_on_successful_report()
    {
        var enricher = MakeEnricher(includeInEnricher: true, available: true, out var analyst, out _);
        var report = new AnalystReport(
            Decision: AiAnalystDecision.Long,
            ForecastHorizon: "next 4 bars",
            RiskRewardRatio: 2.1,
            Confidence: 0.72,
            Justification: "ok",
            Indicator: new IndicatorReport("rsi rising", new Dictionary<string, double> { ["rsi_14"] = 60 }),
            Pattern: new PatternReport("Bull Flag", 0.8, "clear"),
            Trend: new TrendReport("Up", 0.1, 101, 99, "channel up"),
            PatternChartPngBase64: "AAA=",
            TrendChartPngBase64: "BBB=",
            ElapsedMs: 1000);
        analyst.RunAsync(Arg.Any<AnalystRequest>(), Arg.Any<CancellationToken>()).Returns(report);

        var enriched = await enricher.EnrichAsync(Notification(NotificationKind.Signal), CancellationToken.None);

        enriched.Message.Should().Contain("🤖 AI Analyst");
        enriched.Message.Should().Contain("Long");
        enriched.Message.Should().Contain("Bull Flag");
    }

    private static StrategyNotification Notification(NotificationKind kind) => new(
        Kind: kind,
        StrategyId: "test.rsi",
        StrategyName: "RSI",
        Symbol: "NVDA",
        Direction: "Long",
        Message: "Signal fired",
        TimestampUtc: DateTime.UtcNow);

    private static AiAnalystEnricher MakeEnricher(
        bool includeInEnricher, bool available,
        out IAiAnalystClient analyst, out IMarketDataRepository repo)
    {
        analyst = Substitute.For<IAiAnalystClient>();
        analyst.IsAvailable.Returns(available);

        repo = Substitute.For<IMarketDataRepository>();
        repo.GetHistoricalBarsAsync(Arg.Any<Contract>(), Arg.Any<BarSize>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Bar(DateTime.UtcNow.AddHours(-2), 1, 2, 0.5, 1.5, 100),
                new Bar(DateTime.UtcNow.AddHours(-1), 1.5, 2.5, 1, 2, 120),
            });
        repo.ConnectionState.Returns(new BehaviorSubject<ConnectionState>(ConnectionState.Connected));

        var options = Substitute.For<IOptionsMonitor<NotificationsOptions>>();
        options.CurrentValue.Returns(new NotificationsOptions
        {
            AiAnalyst = new AiAnalystOptions
            {
                Enabled = true,
                IncludeInEnricher = includeInEnricher,
                Provider = "openai",
                Model = "gpt-4o",
                VisionModel = "gpt-4o",
                BarCount = 50,
                TimeoutSeconds = 5,
            },
        });

        return new AiAnalystEnricher(analyst, repo, options, NullLogger<AiAnalystEnricher>.Instance);
    }
}
