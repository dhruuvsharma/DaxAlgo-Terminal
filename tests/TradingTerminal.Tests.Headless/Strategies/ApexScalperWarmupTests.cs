using System.Reactive.Linq;
using FluentAssertions;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;
using TradingTerminal.Strategies.SigmaIcFlow.Engine;
using Xunit;

namespace TradingTerminal.Tests.Strategies;

/// <summary>
/// Covers the warm-up seed in <see cref="ApexScalperStrategy.SeedFromBars"/>: historical OHLCV
/// arms the price-structure (line-fit) signals immediately so the composite leaves zero within one
/// live bar, while the flow signals (delta / Kyle / CVD) ignore the seeded bars and warm strictly
/// from live tape. Regression for "composite shows 0 all the time" on a fresh start.
/// </summary>
public sealed class ApexScalperWarmupTests
{
    private static readonly DateTime Origin = new(2026, 5, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Candle = TimeSpan.FromSeconds(15);

    private sealed class FakeClock : IClock
    {
        public DateTime UtcNow { get; set; } = Origin;
    }

    private sealed class NullRouter : IOrderRouter
    {
        public Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
            => Task.FromResult(new OrderResult(request.ClientOrderId, null, OrderState.PendingNew));
        public Task CancelOrderAsync(string clientOrderId, CancellationToken ct = default) => Task.CompletedTask;
        public IObservable<OrderEvent> OrderEvents => Observable.Empty<OrderEvent>();
    }

    /// <summary>A noisy upward trend so the line fits have non-zero residuals (else the
    /// residual-based signals — initiative / value — would read a degenerate perfect fit).</summary>
    private static List<Bar> WarmupBars(int count)
    {
        var bars = new List<Bar>(count);
        var price = 100.0;
        for (var i = 0; i < count; i++)
        {
            var open = price;
            var noise = ((i * 37) % 11 - 5) * 0.02;   // deterministic pseudo-noise
            var close = open + 0.05 + noise;
            var high = Math.Max(open, close) + 0.10;
            var low = Math.Min(open, close) - 0.10;
            bars.Add(new Bar(Origin.AddMinutes(i), open, high, low, close, 1_000));
            price = close;
        }
        return bars;
    }

    /// <summary>Feeds <paramref name="tradeCount"/> trades, one per distinct candle bucket, starting
    /// after the warm-up window. Completes <paramref name="tradeCount"/> − 1 live bars.</summary>
    private static async Task FeedLiveTrades(ApexScalperStrategy engine, int tradeCount)
    {
        var clock = new FakeClock();
        var router = new NullRouter();
        var start = Origin.AddMinutes(120);   // safely past the warm-up timestamps
        var price = 100.0 + 0.05 * 90;          // continue near the warm-up trend
        for (var i = 0; i < tradeCount; i++)
        {
            var t = start.AddSeconds(i * 20);   // 20s apart > 15s candle ⇒ each in a new bucket
            clock.UtcNow = t;
            price += 0.03;
            var aggressor = i % 2 == 0 ? AggressorSide.Buy : AggressorSide.Sell;
            await engine.OnTradeAsync(
                new TradePrint(InstrumentId.None, t, t, price, 5, aggressor, BrokerKind.Binance, i, false),
                clock, router, default);
        }
    }

    private static bool IsValid(ApexScalperStrategy engine, string signal) =>
        engine.Latest!.Signals.Single(s => s.Name == signal).IsValid;

    [Fact]
    public async Task Seed_arms_line_signals_within_one_live_bar_but_not_flow_signals()
    {
        var engine = new ApexScalperStrategy(Contract.UsStock("TEST"), candleInterval: Candle);
        engine.SeedFromBars(WarmupBars(30));

        // Two trades ⇒ exactly one completed live bar (the first close computes signals).
        await FeedLiveTrades(engine, tradeCount: 2);

        engine.Latest.Should().NotBeNull();

        // Line-fit signals are warmed by the synthetic price-structure seed — valid on the very
        // first live bar close.
        IsValid(engine, ApexScalperStrategy.SigControl).Should().BeTrue();
        IsValid(engine, ApexScalperStrategy.SigWedge).Should().BeTrue();

        // Flow signals ignore the seeded bars; with one live bar they cannot be valid yet.
        IsValid(engine, ApexScalperStrategy.SigDelta).Should().BeFalse();
        IsValid(engine, ApexScalperStrategy.SigKyle).Should().BeFalse();
    }

    [Fact]
    public async Task Composite_is_nonzero_within_one_live_bar_after_seeding()
    {
        var engine = new ApexScalperStrategy(Contract.UsStock("TEST"), candleInterval: Candle);
        engine.SeedFromBars(WarmupBars(30));

        await FeedLiveTrades(engine, tradeCount: 2);

        // The whole point of the seed: the composite leaves the stuck-at-zero "Warming" state.
        engine.Latest!.Composite.Should().NotBe(0);
    }

    [Fact]
    public async Task Without_seed_line_signals_stay_invalid_after_one_live_bar()
    {
        // Control: no warm-up seed ⇒ the line signals need _lineWindow live bars, so a single live
        // bar leaves them invalid (this is the behaviour the seed fixes).
        var engine = new ApexScalperStrategy(Contract.UsStock("TEST"), candleInterval: Candle);

        await FeedLiveTrades(engine, tradeCount: 2);

        IsValid(engine, ApexScalperStrategy.SigControl).Should().BeFalse();
        IsValid(engine, ApexScalperStrategy.SigWedge).Should().BeFalse();
    }

    [Fact]
    public void Default_options_carry_nonzero_ttl_multipliers()
    {
        // Regression: ApexV2Options.TtlMultipliers must not default to a zero-initialised
        // record-struct (a bare new() skips the primary-ctor defaults). Zero TTLs mark every signal
        // stale the instant it is computed, pinning the composite at 0 forever.
        var ttl = ApexV2Options.Default.TtlMultipliers;
        ttl.DeltaFootprint.Should().BeGreaterThan(0);
        ttl.ObiTapeSpeed.Should().BeGreaterThan(0);
        ttl.PocLines.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Flow_signals_warm_from_live_bars_after_enough_arrive()
    {
        var engine = new ApexScalperStrategy(Contract.UsStock("TEST"), candleInterval: Candle);
        engine.SeedFromBars(WarmupBars(30));

        // Seven trades ⇒ six completed live bars — enough for the delta signal's minimum window.
        await FeedLiveTrades(engine, tradeCount: 7);

        IsValid(engine, ApexScalperStrategy.SigDelta).Should().BeTrue();
    }
}
