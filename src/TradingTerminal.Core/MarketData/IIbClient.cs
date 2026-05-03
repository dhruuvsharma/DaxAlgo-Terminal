using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Internal abstraction over the TWS API. The repository owns this; nothing else should depend on it.
/// Implementations are responsible for marshalling raw IB callbacks onto a single producer thread —
/// the repository takes care of UI-thread dispatch above this seam.
/// </summary>
public interface IIbClient : IAsyncDisposable
{
    IObservable<ConnectionState> ConnectionState { get; }

    Task ConnectAsync(string host, int port, int clientId, CancellationToken ct = default);

    Task DisconnectAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> RequestHistoricalBarsAsync(
        Contract contract,
        BarSize barSize,
        TimeSpan duration,
        CancellationToken ct = default);

    IAsyncEnumerable<Bar> SubscribeBarsAsync(
        Contract contract,
        BarSize barSize,
        CancellationToken ct = default);
}
