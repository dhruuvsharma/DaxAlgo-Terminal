using System.IO;
using FluentAssertions;
using TradingTerminal.Core.Domain;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using Xunit;

namespace TradingTerminal.Tests.Backtest;

public sealed class ParquetTickRoundtripTests : IDisposable
{
    private readonly string _tempPath = Path.Combine(
        Path.GetTempPath(),
        $"daxalgo-ticks-{Guid.NewGuid():N}.parquet");

    public void Dispose()
    {
        if (File.Exists(_tempPath)) File.Delete(_tempPath);
    }

    [Fact]
    public async Task Roundtrip_PreservesTicks_InOrder()
    {
        var origin = new DateTime(2026, 5, 12, 13, 30, 0, DateTimeKind.Utc);
        var written = Enumerable.Range(0, 1_000)
            .Select(i => new Tick(
                origin.AddMilliseconds(i * 250),
                Bid: 4250.25 + i * 0.01,
                Ask: 4250.50 + i * 0.01,
                BidSize: 10 + i,
                AskSize: 20 + i))
            .ToList();

        await using (var writer = new ParquetTickWriter(_tempPath, rowGroupSize: 256))
        {
            foreach (var t in written) await writer.WriteAsync(t);
        }

        var read = new List<Tick>();
        await foreach (var t in ParquetTickReader.ReadAsync(_tempPath))
            read.Add(t);

        read.Should().BeEquivalentTo(written, opt => opt.WithStrictOrdering());
    }

    [Fact]
    public async Task ReadAsync_RespectsDateRangeFilter()
    {
        var origin = new DateTime(2026, 5, 12, 13, 0, 0, DateTimeKind.Utc);
        await using (var writer = new ParquetTickWriter(_tempPath, rowGroupSize: 16))
        {
            for (var i = 0; i < 100; i++)
                await writer.WriteAsync(new Tick(origin.AddMinutes(i), 100, 100.5, 1, 1));
        }

        var from = origin.AddMinutes(30);
        var to = origin.AddMinutes(50);
        var window = new List<Tick>();
        await foreach (var t in ParquetTickReader.ReadAsync(_tempPath, from, to))
            window.Add(t);

        window.Should().HaveCount(20);
        window.First().TimestampUtc.Should().Be(from);
        window.Last().TimestampUtc.Should().Be(origin.AddMinutes(49));
    }
}
