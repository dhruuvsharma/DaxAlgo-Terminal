using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Backtest.Engine.Feeds;

/// <summary>
/// A deterministic synthetic feed: a mean-reverting (Ornstein-Uhlenbeck-ish) random walk of the mid
/// with a fixed spread, one quote per second. Lets the Studio and tests run with zero data setup, and
/// mirrors the old CLI <c>synth</c> subcommand. Seeded, so a given seed reproduces the same path.
/// </summary>
public sealed class SyntheticMarketDataFeed : IMarketDataFeed
{
    private readonly InstrumentId _instrument;
    private readonly int _count;
    private readonly int _seed;
    private readonly double _startPrice;
    private readonly double _spread;

    public SyntheticMarketDataFeed(InstrumentId instrument, int count, int seed = 1, double startPrice = 100.0, double spread = 0.02)
    {
        _instrument = instrument;
        _count = Math.Max(0, count);
        _seed = seed;
        _startPrice = startPrice;
        _spread = spread;
    }

    public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var rng = new Random(_seed);
        var t0 = spec.Data.FromUtc ?? new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var mid = _startPrice;
        const double theta = 0.01;            // pull back toward the mean
        var sigma = _startPrice * 0.001;      // step volatility
        var half = _spread * 0.5;

        for (var i = 0; i < _count; i++)
        {
            ct.ThrowIfCancellationRequested();
            mid += theta * (_startPrice - mid) + sigma * (rng.NextDouble() * 2 - 1);
            var ts = t0.AddSeconds(i);
            yield return MarketEvent.OfQuote(_instrument, new Tick(ts, mid - half, mid + half, 100, 100));
        }
    }
}
