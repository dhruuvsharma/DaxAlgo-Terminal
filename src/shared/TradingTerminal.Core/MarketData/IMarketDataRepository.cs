using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// The single facade for market data. Hides broker SDKs entirely — no <c>EClientSocket</c> /
/// <c>NTDirect</c> / Spotware proto types leak through. All callbacks are marshalled to the UI
/// dispatcher before the returned sequences yield, so consumers stay single-threaded.
///
/// Each call names the source <see cref="BrokerKind"/> explicitly — there is no "active
/// broker" anymore. The instrument-picker rows carry their broker
/// (<see cref="TradableInstrument.Broker"/>); callers thread that broker into every
/// subscribe / history call. Connection lifecycle is owned by <see cref="IBrokerSelector"/>;
/// this repository assumes the caller has already connected the broker they're asking for.
/// </summary>
public interface IMarketDataRepository
{
    /// <summary>
    /// Merged universe across every currently-connected broker. Each <see cref="TradableInstrument"/>
    /// carries the broker that supplied it, so the dropdown can group/render by source and route
    /// subsequent subscribe calls back to the same broker.
    /// </summary>
    Task<IReadOnlyList<TradableInstrument>> ListInstrumentsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Contract contract,
        BrokerKind broker,
        BarSize barSize,
        TimeSpan duration,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming bars from <paramref name="broker"/>. The sequence completes when
    /// <paramref name="ct"/> is cancelled or the connection is permanently lost. If the broker is
    /// not currently connected, the call throws <see cref="InvalidOperationException"/>.
    /// </summary>
    IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract,
        BrokerKind broker,
        BarSize barSize,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming tick-by-tick bid/ask quotes from <paramref name="broker"/>. Marshalled to the UI
    /// dispatcher before yielding so view-model consumers stay single-threaded. Cancellation via
    /// <paramref name="ct"/> is the unsubscribe path.
    /// </summary>
    IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        BrokerKind broker,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming L2 order-book snapshots from <paramref name="broker"/>, marshalled to the UI
    /// dispatcher. Only available when the broker supports depth (cTrader does; IB will when wired;
    /// NinjaTrader and Alpaca don't). Falls through whatever <see cref="NotSupportedException"/>
    /// the broker throws — callers should be ready to degrade to <see cref="SubscribeTicksAsync"/>.
    /// </summary>
    IAsyncEnumerable<DepthSnapshot> SubscribeDepthAsync(
        Contract contract,
        BrokerKind broker,
        int levels = 10,
        CancellationToken ct = default);
}
