using Microsoft.Extensions.DependencyInjection;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Infrastructure;
using TradingTerminal.Infrastructure.Strategies.Authoring.Verification;

namespace TradingTerminal.App.Basic.Tests;

/// <summary>
/// Real market data, from a venue that needs no account.
///
/// <para><b>Why this exists.</b> The screenshots of authored units were rendered off
/// <see cref="SyntheticDrive"/>, whose prices are a generated series and whose book is a decaying
/// ladder built to be deliberately lopsided. That is the right input for the verification ladder —
/// hostile, deterministic, and fast — and the wrong input for a picture somebody is asked to judge.
/// A footprint of invented prints tells you the code runs; it tells you nothing about whether the
/// window is any good.</para>
///
/// <para>Binance serves bars, depth and the trade tape publicly, so this needs no credentials and
/// touches no account. It captures a bounded window and hands back a
/// <see cref="SyntheticDrive.CapturedMarket"/>: the replay keeps the picture reproducible and keeps
/// the network out of the ladder itself.</para>
/// </summary>
internal static class BinanceCapture
{
    /// <summary>The canonical id everything is stamped with. It must be the drive's own, because the
    /// market view answers <c>RecentBars</c> and <c>LatestDepth</c> for that one instrument.</summary>
    private static readonly InstrumentId Id = SyntheticDrive.Instrument;

    /// <summary>
    /// Bars, then a live window of book and tape.
    /// </summary>
    /// <param name="symbol">A Binance spot symbol, e.g. <c>BTCUSDT</c>.</param>
    /// <param name="bars">How much history to fetch. 120 one-minute bars is two hours, which is what
    /// the synthetic series was sized at, so a unit's warm-up guards behave the same either way.</param>
    /// <param name="live">How long to listen for depth and prints. The tape on a liquid pair is busy
    /// enough that a few seconds is a real picture rather than a handful of dots.</param>
    public static async Task<SyntheticDrive.CapturedMarket> TakeAsync(
        string symbol = "BTCUSDT",
        int bars = 120,
        TimeSpan? live = null,
        CancellationToken ct = default)
    {
        var window = live ?? TimeSpan.FromSeconds(45);
        var contract = new Contract(symbol, "CRYPTO", "BINANCE", "USDT", "BINANCE");

        // Through the registered seam rather than the concrete client, which is internal by design:
        // a test that reaches past IBrokerClient would be testing something the application does not
        // use. This is the same registration the shell composes.
        var services = new ServiceCollection()
            .AddLogging()
            .AddInfrastructureCore()
            .AddKeylessBrokers()
            .BuildServiceProvider();

        var client = services.GetServices<IBrokerClient>().Single(c => c.Kind == BrokerKind.Binance);

        await client.ConnectAsync(ct).ConfigureAwait(false);

        var history = await client
            .RequestHistoricalBarsAsync(contract, BarSize.OneMinute, TimeSpan.FromMinutes(bars), ct)
            .ConfigureAwait(false);

        var captured = history
            .Select(bar => OhlcvBar.FromBar(bar, Id, BarSize.OneMinute, BrokerKind.Binance, isFinal: true))
            .ToList();

        var quotes = new List<Quote>();
        var trades = new List<TradePrint>();
        var depth = new List<DepthSnapshot>();

        using var listening = CancellationTokenSource.CreateLinkedTokenSource(ct);
        listening.CancelAfter(window);

        // All three at once, because a book without the tape that moved it is half a picture, and the
        // ordering between them is what a footprint and an imbalance monitor are actually reading.
        await Task.WhenAll(
            Collect(client.SubscribeTicksAsync(contract, listening.Token), tick => quotes.Add(
                new Quote(Id, tick.TimestampUtc, DateTime.UtcNow, tick.Bid, tick.Ask,
                    tick.BidSize, tick.AskSize, BrokerKind.Binance, quotes.Count, EventTimeApproximate: false))),
            Collect(client.SubscribeTradesAsync(contract, listening.Token), print => trades.Add(
                new TradePrint(Id, print.TimestampUtc, DateTime.UtcNow, print.Price, print.Size,
                    print.Aggressor, BrokerKind.Binance, trades.Count, EventTimeApproximate: false))),
            Collect(client.SubscribeDepthAsync(contract, levels: 20, listening.Token), depth.Add))
            .ConfigureAwait(false);

        await client.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        await services.DisposeAsync().ConfigureAwait(false);

        // Live events carry the wall clock, which is AFTER the last bar opened — so without this they
        // would all land in the final bar's open-ended window and the earlier bars would have no book
        // at all. Spreading them back across the history is what gives every bar a tape to show.
        return new SyntheticDrive.CapturedMarket(
            captured,
            Spread(quotes, captured, q => q, (q, at) => q with { EventTimeUtc = at }),
            Spread(trades, captured, t => t, (t, at) => t with { EventTimeUtc = at }),
            Spread(depth, captured, d => d, (d, at) => d with { TimestampUtc = at }));
    }

    /// <summary>Drains a stream until its window closes. Cancellation is the normal ending here, not
    /// a fault.</summary>
    private static async Task Collect<T>(IAsyncEnumerable<T> stream, Action<T> take)
    {
        try
        {
            await foreach (var item in stream.ConfigureAwait(false)) take(item);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Re-stamps a captured burst evenly across the bar history.
    ///
    /// <para>Twenty seconds of live events all carry timestamps after the newest bar. Replayed as
    /// captured they would every one fall in the last bar, so a hundred and nineteen bars would show
    /// an empty book and an empty tape — the same shape of defect as the drive that supplied no depth
    /// at all. Spreading them gives each bar its share.</para>
    /// </summary>
    private static IReadOnlyList<T> Spread<T>(
        IReadOnlyList<T> events,
        IReadOnlyList<OhlcvBar> bars,
        Func<T, T> identity,
        Func<T, DateTime, T> at)
    {
        if (events.Count == 0 || bars.Count == 0) return events;

        var from = bars[0].OpenTimeUtc;
        var to = bars[^1].OpenTimeUtc.AddMinutes(1);
        var step = (to - from).TotalMilliseconds / events.Count;

        return [.. events.Select((item, index) =>
            at(identity(item), from.AddMilliseconds(step * index)))];
    }
}
