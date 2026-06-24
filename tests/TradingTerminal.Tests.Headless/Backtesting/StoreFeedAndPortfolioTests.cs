using FluentAssertions;
using NSubstitute;
using TradingTerminal.Backtest.Engine;
using TradingTerminal.Backtest.Engine.Feeds;
using TradingTerminal.Backtest.Engine.Kernels;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using Xunit;

namespace TradingTerminal.Tests.Backtesting;

/// <summary>
/// Exercises the store-backed feed's k-way merge and the engine's multi-instrument (portfolio)
/// accounting, using a substituted <see cref="IMarketDataStore"/> so no real database is needed.
/// </summary>
public sealed class StoreFeedAndPortfolioTests
{
    private static readonly InstrumentId A = new(1);
    private static readonly InstrumentId B = new(2);
    private static readonly DateTime Start = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Quote Q(InstrumentId id, int second, double mid) =>
        new(id, Start.AddSeconds(second), Start.AddSeconds(second), mid - 0.01, mid + 0.01, 10, 10, BrokerKind.Simulated, second, false);

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items)
    {
        await Task.CompletedTask;
        foreach (var i in items) yield return i;
    }

    private static IMarketDataStore StoreWith(IReadOnlyDictionary<InstrumentId, List<Quote>> quotes)
    {
        var store = Substitute.For<IMarketDataStore>();
        store.ReadQuotesAsync(Arg.Any<InstrumentId>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ToAsync(quotes.GetValueOrDefault((InstrumentId)ci[0], new List<Quote>())));
        store.ReadTradesAsync(Arg.Any<InstrumentId>(), Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<BrokerKind?>(), Arg.Any<CancellationToken>())
            .Returns(_ => ToAsync(Array.Empty<TradePrint>()));
        return store;
    }

    private static RunSpec PortfolioSpec() => new(
        Universe: Universe.Of(
            new InstrumentSpec(A, Contract.UsStock("AAA"), 0.01, 1.0),
            new InstrumentSpec(B, Contract.UsStock("BBB"), 0.01, 1.0)),
        Data: new DataSpec(BacktestDataSource.LocalStore, Start, Start.AddHours(1)),
        Parameters: new StrategyParameters(new Dictionary<string, double>
        {
            ["lookback"] = 20, ["entryZ"] = 1.0, ["exitZ"] = 0.2, ["qty"] = 5,
        }));

    [Fact]
    public async Task Store_feed_merges_instruments_in_event_time_order()
    {
        // A on even seconds, B on odd seconds — a correct merge strictly interleaves them by time.
        var aQuotes = Enumerable.Range(0, 50).Select(k => Q(A, k * 2, 100)).ToList();
        var bQuotes = Enumerable.Range(0, 50).Select(k => Q(B, k * 2 + 1, 200)).ToList();
        var store = StoreWith(new Dictionary<InstrumentId, List<Quote>> { [A] = aQuotes, [B] = bQuotes });

        var recorder = new RecordingKernel();
        await new BacktestEngine(new StoreMarketDataFeed(store)).RunAsync(PortfolioSpec(), recorder);

        recorder.Seen.Should().HaveCount(100);
        recorder.Seen.Select(s => s.ts).Should().BeInAscendingOrder();
        recorder.Seen.Select(s => s.id).Distinct().Should().BeEquivalentTo(new[] { A, B });
    }

    [Fact]
    public async Task Portfolio_run_trades_each_instrument_independently()
    {
        // Both instruments oscillate (different phase) so each trips the z-score thresholds on its own.
        var aQuotes = Enumerable.Range(0, 400).Select(k => Q(A, k, 100 + 5 * Math.Sin(k * 2 * Math.PI / 50.0))).ToList();
        var bQuotes = Enumerable.Range(0, 400).Select(k => Q(B, k, 200 + 8 * Math.Cos(k * 2 * Math.PI / 40.0))).ToList();
        var store = StoreWith(new Dictionary<InstrumentId, List<Quote>> { [A] = aQuotes, [B] = bQuotes });

        var report = await new BacktestEngine(new StoreMarketDataFeed(store))
            .RunAsync(PortfolioSpec(), new MeanReversionKernel());

        report.Summary.EventsProcessed.Should().Be(800);
        report.PerInstrument.Select(p => p.Instrument).Should().BeEquivalentTo(new[] { A, B });
        report.Trades.Select(t => t.Instrument).Distinct().Should().BeEquivalentTo(new[] { A, B });
    }

    private sealed class RecordingKernel : IStrategyKernel
    {
        public List<(InstrumentId id, DateTime ts)> Seen { get; } = new();
        public Task OnStartAsync(IStrategyContext ctx, CancellationToken ct) => Task.CompletedTask;
        public Task OnQuoteAsync(InstrumentId instrument, Tick quote, IStrategyContext ctx, CancellationToken ct)
        {
            Seen.Add((instrument, quote.TimestampUtc));
            return Task.CompletedTask;
        }
    }
}
