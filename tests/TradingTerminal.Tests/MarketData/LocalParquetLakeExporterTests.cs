using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.Infrastructure.MarketData.Archive.Lake;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// Verifies the local Parquet lake exporter writes the expected per-instrument tree, is
/// append-only/idempotent on re-run, and produces files that the DuckDB query layer can read
/// back — tying the two new storage features together.
/// </summary>
public sealed class LocalParquetLakeExporterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"daxalgo-lake-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Export_writes_tree_then_is_idempotent_and_duckdb_readable()
    {
        var id = new InstrumentId(7);
        var t0 = new DateTime(2026, 5, 4, 14, 30, 0, DateTimeKind.Utc);   // inside 2026-05
        var store = new FakeLakeStore(
            quotes: new[]
            {
                new Quote(id, t0,                t0, 100.00, 100.02, 1, 1, BrokerKind.Alpaca, 1, false),
                new Quote(id, t0.AddSeconds(1),  t0, 100.01, 100.03, 1, 1, BrokerKind.Alpaca, 2, false),
            },
            trades: new[]
            {
                new TradePrint(id, t0, t0, 100.01, 5, AggressorSide.Buy, BrokerKind.Alpaca, 1, false),
            },
            bars: new[]
            {
                new OhlcvBar(id, BarSize.OneMinute, t0, 100, 101, 99, 100.5, 1000, BrokerKind.Alpaca, true),
            });

        var registry = Substitute.For<IInstrumentRegistry>();
        registry.All().Returns(new[] { new Instrument(id, "FAKE", AssetClass.Equity, "SMART", "USD", 0.01, 1) });

        var exporter = new LocalParquetLakeExporter(store, registry, NullLogger<LocalParquetLakeExporter>.Instance);
        const ArchiveTables all = ArchiveTables.Quotes | ArchiveTables.Trades | ArchiveTables.Bars;
        var from = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        var first = await exporter.ExportRangeAsync(from, to, _root, "2026-05", all, null, default);

        first.FilesWritten.Should().Be(3);    // quotes + trades + 1m bars
        first.FilesSkipped.Should().Be(0);
        first.Rows.Should().Be(4);            // 2 quotes + 1 trade + 1 bar
        File.Exists(Path.Combine(_root, "quotes", "instrument=7", "2026-05.parquet")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "trades", "instrument=7", "2026-05.parquet")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "bars", "instrument=7", "size=0", "2026-05.parquet")).Should().BeTrue();

        // The DuckDB query layer reads the lake's parquet back.
        var duck = new DuckDbParquetQueryService(NullLogger<DuckDbParquetQueryService>.Instance);
        var quotesGlob = Path.Combine(_root, "quotes", "instrument=7", "2026-05.parquet").Replace('\\', '/');
        var count = await duck.QueryAsync($"SELECT count(*) AS n FROM read_parquet('{quotesGlob}')");
        Convert.ToInt64(count.Rows[0][0]).Should().Be(2);

        // Re-run the same period: append-only — nothing rewritten, existing files skipped.
        var second = await exporter.ExportRangeAsync(from, to, _root, "2026-05", all, null, default);
        second.FilesWritten.Should().Be(0);
        second.FilesSkipped.Should().Be(3);
    }

    /// <summary>Minimal hand-rolled <see cref="IMarketDataStore"/> — NSubstitute doesn't compose
    /// cleanly with <see cref="IAsyncEnumerable{T}"/> returns (see BacktestStoreSourceTests).</summary>
    private sealed class FakeLakeStore : IMarketDataStore
    {
        private readonly Quote[] _quotes;
        private readonly TradePrint[] _trades;
        private readonly OhlcvBar[] _bars;

        public FakeLakeStore(Quote[] quotes, TradePrint[] trades, OhlcvBar[] bars)
        {
            _quotes = quotes; _trades = trades; _bars = bars;
        }

        public void EnqueueQuote(Quote q) { }
        public void EnqueueTrade(TradePrint t) { }
        public void EnqueueBar(OhlcvBar b) { }
        public void EnqueueDepth(InstrumentId id, DepthSnapshot snapshot, TradingTerminal.Core.Brokers.BrokerKind source) { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(InstrumentId id, BarSize size, int count, TradingTerminal.Core.Brokers.BrokerKind? source = null, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<OhlcvBar>)Array.Empty<OhlcvBar>());

        public async IAsyncEnumerable<Quote> ReadQuotesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, TradingTerminal.Core.Brokers.BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var q in _quotes)
                if (q.InstrumentId == id && q.EventTimeUtc >= fromUtc && q.EventTimeUtc < toUtc) yield return q;
        }

        public async IAsyncEnumerable<TradePrint> ReadTradesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, TradingTerminal.Core.Brokers.BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var t in _trades)
                if (t.InstrumentId == id && t.EventTimeUtc >= fromUtc && t.EventTimeUtc < toUtc) yield return t;
        }

        public async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(InstrumentId id, BarSize size, DateTime fromUtc, DateTime toUtc, TradingTerminal.Core.Brokers.BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            foreach (var b in _bars)
                if (b.InstrumentId == id && b.Size == size && b.OpenTimeUtc >= fromUtc && b.OpenTimeUtc < toUtc) yield return b;
        }

        public async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
