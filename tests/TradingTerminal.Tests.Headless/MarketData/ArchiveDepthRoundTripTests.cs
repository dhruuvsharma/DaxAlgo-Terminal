using System.IO;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.MarketData.Archive;
using TradingTerminal.Infrastructure.MarketData.Archive;
using Xunit;

namespace TradingTerminal.Tests.MarketData;

/// <summary>
/// End-to-end test of the depth (L2) archive path added for the QuestDB backend: seed depth into a
/// store, run the real <see cref="MarketDataArchiver"/> over an in-memory transport (build bundle →
/// upload → sha256-verify → prune), then restore and confirm the snapshots reconstruct level-for-
/// level. Deterministic — no QuestDB or Telegram needed; it exercises the new flatten/regroup +
/// parquet round-trip, which is the actual risk surface.
/// </summary>
public sealed class ArchiveDepthRoundTripTests
{
    [Fact]
    public async Task Depth_archives_prunes_and_restores_level_for_level()
    {
        var id = new InstrumentId(4242);
        var t0 = new DateTime(2026, 3, 2, 9, 30, 0, DateTimeKind.Utc);
        var seeded = new[]
        {
            new DepthSnapshot(t0,
                new[] { new DepthLevel(100.0, 5), new DepthLevel(99.5, 8) },
                new[] { new DepthLevel(100.5, 3), new DepthLevel(101.0, 12) }),
            new DepthSnapshot(t0.AddSeconds(1),
                new[] { new DepthLevel(100.1, 7) },
                new[] { new DepthLevel(100.6, 4), new DepthLevel(101.1, 9) }),
        };

        var store = new FakeDepthStore(id, seeded);
        var registry = Substitute.For<IInstrumentRegistry>();
        registry.All().Returns(new[] { new Instrument(id, "TEST", (AssetClass)0, "", "USD", 0.01, 1) });

        var transport = new InMemoryTransport();
        var manifestPath = Path.Combine(Path.GetTempPath(), $"depth-manifest-{Guid.NewGuid():N}.db");
        using var manifest = new ArchiveManifestStore(manifestPath);

        var opts = new ArchiveOptions
        {
            Tables = ArchiveTables.Depth,
            VerifyAfterUpload = true,
            DeleteLocalAfterArchive = true,
            StagingDirectory = Path.Combine(Path.GetTempPath(), $"depth-staging-{Guid.NewGuid():N}"),
        };
        var archiver = new MarketDataArchiver(
            store, registry, transport, manifest,
            new StaticOptionsMonitor<ArchiveOptions>(opts),
            NullLogger<MarketDataArchiver>.Instance);

        try
        {
            // Archive [t0, t0+1min): builds the depth bundle, uploads, verifies, prunes the source.
            var result = await archiver.ArchiveRangeAsync(
                t0, t0.AddMinutes(1), ArchiveTarget.SavedMessages, null, CancellationToken.None);

            result.VerifiedRoundTrip.Should().BeTrue();
            result.LocalDataDeleted.Should().BeTrue();
            result.Entry.RowsDepth.Should().Be(7); // (2 bids + 2 asks) + (1 bid + 2 asks)
            store.Pruned.Should().BeTrue();
            store.Captured.Should().BeEmpty(); // nothing restored yet

            // Restore re-imports the depth rows back into the store.
            await archiver.RestoreAsync(result.Entry, null, CancellationToken.None);

            store.Captured.Should().HaveCount(2);
            var byTime = store.Captured.OrderBy(s => s.TimestampUtc).ToArray();

            byTime[0].TimestampUtc.Should().Be(t0);
            byTime[0].Bids.Select(b => (b.Price, b.Size)).Should().Equal((100.0, 5), (99.5, 8));
            byTime[0].Asks.Select(a => (a.Price, a.Size)).Should().Equal((100.5, 3), (101.0, 12));

            byTime[1].TimestampUtc.Should().Be(t0.AddSeconds(1));
            byTime[1].Bids.Should().ContainSingle().Which.Price.Should().Be(100.1);
            byTime[1].Asks.Select(a => a.Price).Should().Equal(100.6, 101.1);
        }
        finally
        {
            try { File.Delete(manifestPath); } catch { /* best effort */ }
        }
    }

    /// <summary>In-memory <see cref="IMarketDataStore"/>: serves seeded depth on read, captures
    /// re-enqueued depth on restore, and tracks the prune call. All other streams are inert.</summary>
    private sealed class FakeDepthStore : IMarketDataStore
    {
        private readonly InstrumentId _id;
        private List<DepthSnapshot> _seeded;
        public List<DepthSnapshot> Captured { get; } = new();
        public bool Pruned { get; private set; }

        public FakeDepthStore(InstrumentId id, IEnumerable<DepthSnapshot> seeded)
        {
            _id = id;
            _seeded = seeded.ToList();
        }

        public void EnqueueQuote(Quote q) { }
        public void EnqueueTrade(TradePrint t) { }
        public void EnqueueBar(OhlcvBar b) { }
        public void EnqueueDepth(InstrumentId id, DepthSnapshot snapshot, BrokerKind source)
        {
            if (id == _id) Captured.Add(snapshot);
        }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(InstrumentId id, BarSize size, int count, BrokerKind? source = null, CancellationToken ct = default) =>
            Task.FromResult((IReadOnlyList<OhlcvBar>)Array.Empty<OhlcvBar>());

        public async IAsyncEnumerable<Quote> ReadQuotesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<TradePrint> ReadTradesAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }
        public async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(InstrumentId id, BarSize size, DateTime fromUtc, DateTime toUtc, BrokerKind? source = null, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.CompletedTask; yield break; }

        public async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(InstrumentId id, DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            if (id != _id) yield break;
            foreach (var s in _seeded)
                if (s.TimestampUtc >= fromUtc && s.TimestampUtc < toUtc) yield return s;
        }

        public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
        {
            Pruned = true;
            var n = _seeded.Count(s => s.TimestampUtc >= fromUtc && s.TimestampUtc < toUtc);
            _seeded = _seeded.Where(s => !(s.TimestampUtc >= fromUtc && s.TimestampUtc < toUtc)).ToList();
            return Task.FromResult((long)n);
        }
    }

    /// <summary>Round-trips bytes in memory so verify (re-download + sha256) passes without a network.</summary>
    private sealed class InMemoryTransport : IArchiveTransport
    {
        private readonly Dictionary<string, byte[]> _blobs = new();
        public string Name => "in-memory";
        public bool IsReady => true;

        public async Task<ArchiveBlobRef> UploadAsync(Stream content, string displayName, long contentLength, ArchiveTarget target, IProgress<long>? progress, CancellationToken ct)
        {
            using var ms = new MemoryStream();
            await content.CopyToAsync(ms, ct);
            var bytes = ms.ToArray();
            var key = $"{displayName}:{Guid.NewGuid():N}";
            _blobs[key] = bytes;
            return new ArchiveBlobRef(Name, displayName, bytes.LongLength, "",
                new Dictionary<string, string> { ["key"] = key });
        }

        public async Task DownloadAsync(ArchiveBlobRef blob, Stream destination, IProgress<long>? progress, CancellationToken ct)
        {
            var bytes = _blobs[blob.Metadata["key"]];
            await destination.WriteAsync(bytes, ct);
        }
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;
        public T CurrentValue { get; }
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
