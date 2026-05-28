using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Drives normalized data into the <see cref="IMarketDataHub"/> and <see cref="IMarketDataStore"/>.
/// Given a broker <see cref="Contract"/> AND its source <see cref="BrokerKind"/>, it resolves the
/// canonical <see cref="InstrumentId"/>, subscribes that broker's raw feed once, normalizes each
/// event, and fans it out. Calls are <em>ref-counted per (instrument, broker)</em>: N subscribers
/// to the same instrument share one broker stream, and the stream stops when the last handle is
/// disposed.
/// </summary>
public interface IMarketDataIngest
{
    /// <summary>Resolve (creating if needed) the canonical id for a broker contract on <paramref name="broker"/>.</summary>
    InstrumentId Resolve(Contract contract, BrokerKind broker);

    /// <summary>Start (or join) the quote + depth feed for this contract on <paramref name="broker"/>.
    /// Dispose the returned handle to release this subscriber's reference.</summary>
    IDisposable Subscribe(Contract contract, BrokerKind broker);

    /// <summary>Start (or join) a streaming-bar feed at the given size for this contract on <paramref name="broker"/>.</summary>
    IDisposable SubscribeBars(Contract contract, BrokerKind broker, BarSize size);

    /// <summary>Start (or join) the trade-tape feed for this contract on <paramref name="broker"/>.
    /// Brokers without a trade tape return a no-op handle; ingest swallows the
    /// <see cref="NotSupportedException"/>. Aggressor side is inferred via Lee-Ready when the
    /// broker doesn't report it natively — pair with <see cref="Subscribe"/> on the same
    /// instrument for the quote rule to work; otherwise inference falls back to the tick rule.</summary>
    IDisposable SubscribeTrades(Contract contract, BrokerKind broker);
}
