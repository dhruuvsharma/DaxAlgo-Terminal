using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Drives normalized data into the <see cref="IMarketDataHub"/> and <see cref="IMarketDataStore"/>.
/// Given a broker <see cref="Contract"/>, it resolves the canonical <see cref="InstrumentId"/>,
/// subscribes the active broker's raw feed once, normalizes each event, and fans it out. Calls
/// are <em>ref-counted per instrument</em>: N subscribers to the same instrument share one broker
/// stream, and the stream stops when the last handle is disposed.
/// </summary>
public interface IMarketDataIngest
{
    /// <summary>Resolve (creating if needed) the canonical id for a broker contract on the active broker.</summary>
    InstrumentId Resolve(Contract contract);

    /// <summary>Start (or join) the quote + depth feed for this contract. Dispose the returned
    /// handle to release this subscriber's reference.</summary>
    IDisposable Subscribe(Contract contract);

    /// <summary>Start (or join) a streaming-bar feed at the given size for this contract.</summary>
    IDisposable SubscribeBars(Contract contract, BarSize size);
}
