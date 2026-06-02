using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

/// <summary>
/// Exercises <see cref="DuckDbParquetQueryService"/> against real Parquet files produced by
/// <see cref="ParquetTickWriter"/> — confirming the DuckDB engine reads the recorder's schema,
/// pushes the timestamp predicate down, and resamples ticks to bars correctly.
/// </summary>
public sealed class DuckDbParquetQueryServiceTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"daxalgo-duckdb-{Guid.NewGuid():N}.parquet");

    private readonly DuckDbParquetQueryService _sut =
        new(NullLogger<DuckDbParquetQueryService>.Instance);

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    private async Task WriteAsync(IEnumerable<Tick> ticks)
    {
        await using var writer = new ParquetTickWriter(_tempPath, rowGroupSize: 256);
        foreach (var t in ticks) await writer.WriteAsync(t);
    }

    [Fact]
    public async Task ReadTicksAsync_RoundTripsInOrder()
    {
        var origin = new DateTime(2026, 5, 12, 13, 30, 0, DateTimeKind.Utc);
        var written = Enumerable.Range(0, 500)
            .Select(i => new Tick(origin.AddMilliseconds(i * 250), 4250.25 + i * 0.01, 4250.50 + i * 0.01, 10 + i, 20 + i))
            .ToList();
        await WriteAsync(written);

        var read = new List<Tick>();
        await foreach (var t in _sut.ReadTicksAsync(_tempPath))
            read.Add(t);

        read.Should().BeEquivalentTo(written, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadTicksAsync_PushesDownTimeRange()
    {
        var origin = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        await WriteAsync(Enumerable.Range(0, 100)
            .Select(i => new Tick(origin.AddMinutes(i), 100, 100.5, 1, 1)));

        var from = origin.AddMinutes(30);
        var to = origin.AddMinutes(50);
        var window = new List<Tick>();
        await foreach (var t in _sut.ReadTicksAsync(_tempPath, from, to))
            window.Add(t);

        window.Should().HaveCount(20);                                  // half-open [30, 50)
        window.First().TimestampUtc.Should().Be(from);
        window.Last().TimestampUtc.Should().Be(origin.AddMinutes(49));
    }

    [Fact]
    public async Task AggregateBarsAsync_ResamplesMidToBars()
    {
        var origin = new DateTime(2026, 5, 12, 13, 30, 0, DateTimeKind.Utc);
        // 8 ticks at 250ms spacing → two 1-second buckets of 4 ticks each. mid == 100 + i.
        await WriteAsync(Enumerable.Range(0, 8)
            .Select(i => new Tick(origin.AddMilliseconds(i * 250), 100 + i, 100 + i, 1, 1)));

        var bars = await _sut.AggregateBarsAsync(_tempPath, TimeSpan.FromSeconds(1));

        bars.Should().HaveCount(2);
        bars[0].Should().BeEquivalentTo(new OhlcvAggregate(origin, Open: 100, High: 103, Low: 100, Close: 103, TickCount: 4));
        bars[1].Should().BeEquivalentTo(new OhlcvAggregate(origin.AddSeconds(1), Open: 104, High: 107, Low: 104, Close: 107, TickCount: 4));
    }

    [Fact]
    public async Task QueryAsync_RunsAdHocSql()
    {
        var origin = new DateTime(2026, 5, 12, 13, 30, 0, DateTimeKind.Utc);
        await WriteAsync(Enumerable.Range(0, 42).Select(i => new Tick(origin.AddSeconds(i), 1, 2, 1, 1)));

        var sql = $"SELECT count(*) AS n FROM read_parquet('{_tempPath.Replace('\\', '/')}')";
        var result = await _sut.QueryAsync(sql);

        result.Columns.Should().ContainSingle().Which.Should().Be("n");
        result.Rows.Should().ContainSingle();
        Convert.ToInt64(result.Rows[0][0]).Should().Be(42);
    }
}
