using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure.Simulation;
using Xunit;

namespace TradingTerminal.Tests.Infrastructure;

public sealed class SimulatedBrokerClientTests
{
    private static SimulatedBrokerClient Build(SimulatedBrokerOptions opts, IMarketDataStore store, IInstrumentRegistry registry)
        => new(store, registry, Options.Create(opts), NullLogger<SimulatedBrokerClient>.Instance);

    [Fact]
    public async Task Synthetic_mode_yields_bars_then_stops_on_cancellation()
    {
        var opts = new SimulatedBrokerOptions
        {
            Mode = SimulatedFeedMode.Synthetic,
            SyntheticBarIntervalMs = 1,
            SpeedMultiplier = 1000, // collapse the cadence so the test is fast
        };
        var client = Build(opts, new NoopStore(), new EmptyRegistry());

        using var cts = new CancellationTokenSource();
        var bars = new List<Bar>();
        await foreach (var bar in client.SubscribeBarsAsync(Contract.UsStock("AAPL"), BarSize.OneMinute, cts.Token))
        {
            bars.Add(bar);
            if (bars.Count == 5) cts.Cancel();
        }

        bars.Should().HaveCount(5);
        bars.Should().OnlyContain(b => b.High >= b.Low && b.Close > 0);
    }

    [Fact]
    public async Task Replay_mode_streams_stored_bars_in_event_time_order()
    {
        var id = new InstrumentId(5);
        var t0 = new DateTime(2026, 1, 2, 14, 30, 0, DateTimeKind.Utc);
        var stored = new[]
        {
            Bar(id, t0, 100),
            Bar(id, t0.AddMinutes(1), 101),
            Bar(id, t0.AddMinutes(2), 102),
        };

        var registry = new SingleInstrumentRegistry(new Instrument(id, "AAPL", AssetClass.Equity, "NASDAQ", "USD", 0.01, 1.0));
        var store = new ReplayBarStore(stored, new StoredDataExtent(t0, t0.AddMinutes(2)));
        var opts = new SimulatedBrokerOptions
        {
            Mode = SimulatedFeedMode.Replay,
            Loop = false,
            SpeedMultiplier = 1_000_000, // ~no wall-clock wait between events
        };
        var client = Build(opts, store, registry);

        var bars = new List<Bar>();
        await foreach (var bar in client.SubscribeBarsAsync(Contract.UsStock("AAPL"), BarSize.OneMinute, CancellationToken.None))
            bars.Add(bar);

        bars.Should().HaveCount(3);
        bars.Select(b => b.TimestampUtc).Should().BeInAscendingOrder();
        bars.Select(b => b.Close).Should().Equal(100, 101, 102);
    }

    private static OhlcvBar Bar(InstrumentId id, DateTime openUtc, double close) =>
        new(id, BarSize.OneMinute, openUtc, close - 1, close + 1, close - 2, close, 1000, BrokerKind.Simulated, IsFinal: true);

    // ---- Minimal test doubles (only the members the replay/synthetic paths touch) ----------

    private sealed class SingleInstrumentRegistry(Instrument instrument) : IInstrumentRegistry
    {
        public IReadOnlyList<Instrument> All() => [instrument];
        public Instrument? Get(InstrumentId id) => id == instrument.Id ? instrument : null;
        public InstrumentId? Resolve(BrokerKind broker, string brokerSymbol) => null;
        public InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker) => instrument.Id;
        public string? ToBrokerSymbol(InstrumentId id, BrokerKind broker) => null;
        public void RegisterAlias(InstrumentAlias alias) { }
    }

    private sealed class EmptyRegistry : IInstrumentRegistry
    {
        public IReadOnlyList<Instrument> All() => [];
        public Instrument? Get(InstrumentId id) => null;
        public InstrumentId? Resolve(BrokerKind broker, string brokerSymbol) => null;
        public InstrumentId ResolveOrCreate(Contract contract, BrokerKind broker) => InstrumentId.None;
        public string? ToBrokerSymbol(InstrumentId id, BrokerKind broker) => null;
        public void RegisterAlias(InstrumentAlias alias) { }
    }

    private sealed class ReplayBarStore(IReadOnlyList<OhlcvBar> bars, StoredDataExtent extent) : StoreBase
    {
        public override Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default) => Task.FromResult(extent);

        public override async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(
            InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var b in bars)
            {
                ct.ThrowIfCancellationRequested();
                yield return b;
                await Task.Yield();
            }
        }
    }

    private sealed class NoopStore : StoreBase;

    /// <summary>Base test store: writes are swallowed and reads return empty unless a derived
    /// double overrides the specific member under test.</summary>
    private abstract class StoreBase : IMarketDataStore
    {
        public void EnqueueQuote(Quote quote) { }
        public void EnqueueTrade(TradePrint trade) { }
        public void EnqueueBar(OhlcvBar bar) { }
        public void EnqueueDepth(InstrumentId instrumentId, DepthSnapshot snapshot, BrokerKind source) { }
        public Task FlushAsync(CancellationToken ct = default) => Task.CompletedTask;
        public virtual Task<StoredDataExtent> GetDataExtentAsync(CancellationToken ct = default) => Task.FromResult(StoredDataExtent.Empty);
        public Task<IReadOnlyList<OhlcvBar>> GetRecentBarsAsync(InstrumentId instrumentId, BarSize size, int count, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OhlcvBar>>([]);
        public virtual async IAsyncEnumerable<Quote> ReadQuotesAsync(InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default) { yield break; }
        public virtual async IAsyncEnumerable<TradePrint> ReadTradesAsync(InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default) { yield break; }
        public virtual async IAsyncEnumerable<DepthSnapshot> ReadDepthAsync(InstrumentId instrumentId, DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default) { yield break; }
        public virtual async IAsyncEnumerable<OhlcvBar> ReadBarsAsync(InstrumentId instrumentId, BarSize size, DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken ct = default) { yield break; }
        public Task<long> DeleteQuotesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteTradesInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteBarsInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
        public Task<long> DeleteDepthInRangeAsync(DateTime fromUtc, DateTime toUtc, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
