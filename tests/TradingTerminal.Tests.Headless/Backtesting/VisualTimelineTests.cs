using FluentAssertions;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>Verifies the engine captures a visual timeline only when asked, with OHLC bars and
/// entry/exit markers consistent with the trade ledger.</summary>
public sealed class VisualTimelineTests
{
    private static readonly InstrumentId Id = new(1);

    private static IEnumerable<MarketEvent> Quotes(int count)
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < count; i++)
        {
            var mid = 100.0 + 5.0 * Math.Sin(i * 2 * Math.PI / 50.0);
            yield return MarketEvent.OfQuote(Id, new Tick(start.AddSeconds(i), mid - 0.01, mid + 0.01, 10, 10));
        }
    }

    private static RunSpec Spec(VisualRecording visual) => new(
        Universe: Universe.Single(new InstrumentSpec(Id, Contract.UsStock("TEST"), 0.01, 1.0)),
        Data: new DataSpec(),
        Parameters: new StrategyParameters(new Dictionary<string, double>
        {
            ["lookback"] = 20, ["entryZ"] = 1.0, ["exitZ"] = 0.2, ["qty"] = 5,
        }),
        Visual: visual);

    [Fact]
    public async Task Visual_off_records_no_timeline()
    {
        var report = await new BacktestEngine(new InMemoryMarketDataFeed(Quotes(600)))
            .RunAsync(Spec(VisualRecording.Off), new MeanReversionKernel());

        report.Visual.Should().BeNull();
    }

    [Fact]
    public async Task Visual_on_records_bars_and_markers_matching_trades()
    {
        var report = await new BacktestEngine(new InMemoryMarketDataFeed(Quotes(600)))
            .RunAsync(Spec(VisualRecording.On), new MeanReversionKernel());

        report.Visual.Should().NotBeNull();
        report.Visual!.Instrument.Should().Be(Id);
        report.Visual.Bars.Should().NotBeEmpty();

        // 600 one-second quotes at 1-minute bars → 10 bars.
        report.Visual.Bars.Should().HaveCount(10);
        report.Visual.Bars.Should().OnlyContain(b => b.High >= b.Low && b.High >= b.Open && b.High >= b.Close);

        // One entry + one exit marker per closed round-trip.
        report.Visual.Markers.Count(m => m.IsEntry).Should().Be(report.Trades.Count);
        report.Visual.Markers.Count(m => !m.IsEntry).Should().Be(report.Trades.Count);
    }
}
