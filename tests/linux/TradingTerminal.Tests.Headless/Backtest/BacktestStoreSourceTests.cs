using FluentAssertions;
using NSubstitute;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Backtest;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

/// <summary>
/// Exercises the LocalStore tick source: the engine should pull canonical Quotes from the
/// store, project them to legacy Ticks, and run the strategy against them. Mirrors the
/// existing parquet-source tests but skips disk I/O entirely.
/// </summary>
public sealed class BacktestStoreSourceTests
{
    [Fact]
    public async Task Replays_quotes_from_store_when_source_is_LocalStore()
    {
        var id = new InstrumentId(101);
        var t0 = new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc);
        var store = new FakeStore(id, new[]
        {
            new Quote(id, t0,                 t0, 100.00, 100.02, 1, 1, BrokerKind.Alpaca, 1, false),
            new Quote(id, t0.AddSeconds(10),  t0, 100.05, 100.07, 1, 1, BrokerKind.Alpaca, 2, false),
            new Quote(id, t0.AddSeconds(20),  t0, 100.10, 100.12, 1, 1, BrokerKind.Alpaca, 3, false),
        });

        var strategy = new CountingStrategy();
        var config = new BacktestConfig(
            Contract: Contract.UsStock("FAKE"),
            TickDataPath: string.Empty,
            FromUtc: t0.AddMinutes(-1),
            ToUtc: t0.AddMinutes(1),
            TickSize: 0.01,
            Source: BacktestDataSource.LocalStore,
            InstrumentId: id);

        var session = new BacktestSession(store);
        var result = await session.RunAsync(config, strategy);

        strategy.TicksSeen.Should().Be(3);
        strategy.LastBid.Should().Be(100.10);
        result.StartingCash.Should().Be(100_000d);
        store.ReadCount.Should().Be(1);
    }

    [Fact]
    public async Task Threads_config_broker_into_the_store_read()
    {
        var id = new InstrumentId(102);
        var t0 = new DateTime(2026, 5, 25, 14, 30, 0, DateTimeKind.Utc);
        var store = new FakeStore(id, new[]
        {
            new Quote(id, t0, t0, 100.00, 100.02, 1, 1, BrokerKind.InteractiveBrokers, 1, false),
        });

        var config = new BacktestConfig(
            Contract: Contract.UsStock("FAKE"),
            TickDataPath: string.Empty,
            FromUtc: t0.AddMinutes(-1),
            ToUtc: t0.AddMinutes(1),
            Source: BacktestDataSource.LocalStore,
            InstrumentId: id,
            Broker: BrokerKind.InteractiveBrokers);

        await new BacktestSession(store).RunAsync(config, new CountingStrategy());

        store.LastReadSource.Should().Be(BrokerKind.InteractiveBrokers); // scoped to the configured broker
    }

    [Fact]
    public async Task Throws_when_LocalStore_source_lacks_InstrumentId()
    {
        var store = new FakeStore(new InstrumentId(1), Array.Empty<Quote>());
        var config = new BacktestConfig(
            Contract: Contract.UsStock("FAKE"),
            TickDataPath: string.Empty,
            FromUtc: DateTime.UtcNow.AddHours(-1),
            ToUtc: DateTime.UtcNow,
            Source: BacktestDataSource.LocalStore,
            InstrumentId: InstrumentId.None);

        var session = new BacktestSession(store);
        var act = async () => await session.RunAsync(config, new CountingStrategy());

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*InstrumentId*");
    }

    [Fact]
    public async Task Throws_when_LocalStore_source_lacks_date_range()
    {
        var store = new FakeStore(new InstrumentId(1), Array.Empty<Quote>());
        var config = new BacktestConfig(
            Contract: Contract.UsStock("FAKE"),
            TickDataPath: string.Empty,
            Source: BacktestDataSource.LocalStore,
            InstrumentId: new InstrumentId(42));

        var session = new BacktestSession(store);
        var act = async () => await session.RunAsync(config, new CountingStrategy());

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*FromUtc*ToUtc*");
    }

    [Fact]
    public async Task Throws_when_LocalStore_source_used_without_store()
    {
        var config = new BacktestConfig(
            Contract: Contract.UsStock("FAKE"),
            TickDataPath: string.Empty,
            FromUtc: DateTime.UtcNow.AddHours(-1),
            ToUtc: DateTime.UtcNow,
            Source: BacktestDataSource.LocalStore,
            InstrumentId: new InstrumentId(42));

        var session = new BacktestSession();   // parameterless ctor: no store
        var act = async () => await session.RunAsync(config, new CountingStrategy());

        await act.Should().ThrowAsync<InvalidOperationException>()
                 .WithMessage("*LocalStore*IMarketDataStore*");
    }

    /// <summary>Minimal hand-rolled <see cref="IMarketDataStore"/> — NSubstitute's <c>Returns(...)</c>
    /// chain doesn't compose cleanly with <see cref="IAsyncEnumerable{T}"/> return types.</summary>
    private sealed class FakeStore : IMarketDataStore
    {
        private readonly InstrumentId _id;
        private readonly Quote[] _quotes;
        public int ReadCount;

        public FakeStore(InstrumentId id, Quote[] quotes) { _id = id; _quotes = quotes; }

        public void EnqueueQuote(Quote q) { }
        public void EnqueueTrade(TradePrint t) { }
        public void EnqueueBar(OhlcvBar b) { }
        public void EnqueueDepth(InstrumentId id, DepthSnapshot snapshot, TradingTerminal.Core.Brokers.BrokerKind source) { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        /// <summary>The source passed into the most recent quote/trade read — lets a test assert that
        /// BacktestConfig.Broker is threaded through to the store.</summary>
        public TradingTerminal.Core.Brokers.BrokerKind? LastReadSource;

        public Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(InstrumentId id, BarSize size, int count, TradingTerminal.Core.Brokers.BrokerKind? source = null, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<OhlcvBar>)Array.Empty<OhlcvBar>());
        public async IAsyncEnumerable<TradePrint> ReadTradesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, TradingTerminal.Core.Brokers.BrokerKind? source = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            LastReadSource = source;
            await Task.CompletedTask;
            yield break;
        }
        public async IAsyncEnumerable<Quote> ReadQuotesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, TradingTerminal.Core.Brokers.BrokerKind? source = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            ReadCount++;
            LastReadSource = source;
            foreach (var q in _quotes)
                if (q.InstrumentId == id) yield return q;
            await Task.CompletedTask;
        }
        public async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(InstrumentId id, BarSize size, DateTime fromUtc, DateTime toUtc, TradingTerminal.Core.Brokers.BrokerKind? source = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
    }

    private sealed class CountingStrategy : IBacktestStrategy
    {
        public int TicksSeen;
        public double LastBid;

        public Task OnStartAsync(TradingTerminal.Core.Time.IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public Task OnEndAsync(TradingTerminal.Core.Time.IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;
        public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;
        public Task OnDepthAsync(DepthSnapshot snapshot, TradingTerminal.Core.Time.IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

        public Task OnTickAsync(Tick tick, TradingTerminal.Core.Time.IClock clock, IOrderRouter router, CancellationToken ct)
        {
            TicksSeen++;
            LastBid = tick.Bid;
            return Task.CompletedTask;
        }
    }
}
