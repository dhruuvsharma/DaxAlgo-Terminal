using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// The single facade for market data. Hides Interactive Brokers entirely — no <c>EClientSocket</c>,
/// no <c>EWrapper</c> types leak through. All callbacks are marshalled to the UI dispatcher
/// before the returned sequences yield, so consumers stay single-threaded.
/// </summary>
public interface IMarketDataRepository
{
    IObservable<ConnectionState> ConnectionState { get; }

    Task ConnectAsync(CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Contract contract,
        BarSize barSize,
        TimeSpan duration,
        CancellationToken ct = default);

    /// <summary>
    /// Streaming bars. The sequence completes when <paramref name="ct"/> is cancelled
    /// or the connection is permanently lost. If currently disconnected, the call
    /// throws <see cref="InvalidOperationException"/>.
    /// </summary>
    IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract,
        BarSize barSize,
        CancellationToken ct = default);
}
