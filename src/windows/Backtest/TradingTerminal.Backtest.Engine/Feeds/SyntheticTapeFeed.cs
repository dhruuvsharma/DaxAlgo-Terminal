using System.Runtime.CompilerServices;
using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Backtest.Engine.Feeds;

/// <summary>
/// A synthetic feed that emits both an L1 quote <b>and</b> a trade print each step, with regime
/// bursts of one-sided aggressor flow layered over a mean-reverting random walk. Unlike the
/// quote-only <see cref="SyntheticMarketDataFeed"/>, this gives <b>tape-primary</b> strategies
/// (e.g. SigmaIcFlow) genuine aggressor-side volume to score, so they can actually trade with zero
/// data setup.
///
/// <para>Two deliberate choices make tape strategies runnable on synthetic data:</para>
/// <list type="bullet">
///   <item><b>Active session.</b> Timestamps are anchored in the London/NY overlap (≈13:30 UTC) by
///   default, because session-gated strategies refuse to trade the Asian session — the old
///   midnight-anchored quote feed sat entirely in disallowed hours, so those strategies never
///   armed.</item>
///   <item><b>Momentum bursts.</b> Random regime bursts apply a directional price drift and a
///   matching aggressor bias, so flow signals (delta / footprint / tape speed / CVD) see real
///   structure rather than white noise.</item>
/// </list>
///
/// Deterministic for a fixed seed. Quote-only kernels simply ignore the trade events.
/// </summary>
public sealed class SyntheticTapeFeed : IMarketDataFeed
{
    private readonly InstrumentId _instrument;
    private readonly int _steps;
    private readonly int _seed;
    private readonly double _startPrice;
    private readonly double _tick;

    /// <summary>Default anchor: a weekday in the London/NY overlap so session gates pass. The session
    /// gate keys off the UTC hour only, so the calendar date is otherwise irrelevant.</summary>
    private static readonly DateTime SessionAnchor = new(2024, 1, 8, 13, 30, 0, DateTimeKind.Utc);

    public SyntheticTapeFeed(InstrumentId instrument, int steps, int seed = 1, double startPrice = 5_000.0, double tickSize = 0.25)
    {
        _instrument = instrument;
        _steps = Math.Max(0, steps);
        _seed = seed;
        _startPrice = startPrice;
        _tick = tickSize > 0 ? tickSize : 0.25;
    }

    public async IAsyncEnumerable<MarketEvent> StreamAsync(RunSpec spec, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var rng = new Random(_seed);
        var t0 = spec.Data.FromUtc ?? SessionAnchor;

        var mid = _startPrice;
        const double theta = 0.005;             // mild pull toward the mean
        var sigma = _startPrice * 0.0004;       // per-step diffusion
        var burstLeft = 0;
        var burstSign = 0;
        var drift = 0.0;
        long seq = 0;

        for (var i = 0; i < _steps; i++)
        {
            ct.ThrowIfCancellationRequested();

            // Start a new momentum burst occasionally; otherwise let the drift decay.
            if (burstLeft <= 0 && rng.NextDouble() < 0.02)
            {
                burstLeft = rng.Next(60, 240);
                burstSign = rng.NextDouble() < 0.5 ? 1 : -1;
                drift = burstSign * sigma * 1.8;
            }
            if (burstLeft > 0) burstLeft--; else drift *= 0.97;

            mid += drift + theta * (_startPrice - mid) + sigma * (rng.NextDouble() * 2 - 1);
            if (mid <= 0) mid = _startPrice;

            var ts = t0.AddSeconds(i);
            var bid = Math.Round((mid - _tick) / _tick) * _tick;
            var ask = bid + _tick;
            yield return MarketEvent.OfQuote(_instrument, new Tick(ts, bid, ask, 50, 50));

            // Aggressor: biased to the burst side during a burst, otherwise leans with the last move.
            var buy = burstLeft > 0
                ? rng.NextDouble() < (burstSign > 0 ? 0.72 : 0.28)
                : rng.NextDouble() < (drift >= 0 ? 0.52 : 0.48);
            var price = buy ? ask : bid;
            var size = rng.Next(1, 25);
            var aggressor = buy ? AggressorSide.Buy : AggressorSide.Sell;
            yield return MarketEvent.OfTrade(_instrument, new TradePrint(
                _instrument, ts, ts, price, size, aggressor, BrokerKind.Simulated, seq++, EventTimeApproximate: false));
        }
    }
}
