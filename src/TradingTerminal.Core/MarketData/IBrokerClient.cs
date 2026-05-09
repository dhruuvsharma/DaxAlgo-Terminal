using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Internal abstraction over a market-data + connection backend (IB, NinjaTrader, ...).
/// The repository owns this; nothing else should depend on it. Implementations are
/// responsible for marshalling raw broker callbacks onto a single producer thread —
/// the repository takes care of UI-thread dispatch above this seam.
///
/// Each implementation reads its own connection settings from injected options
/// (host/port/clientId for IB; account/dll-path for NinjaTrader), so <see cref="ConnectAsync"/>
/// takes only a cancellation token.
/// </summary>
public interface IBrokerClient : IAsyncDisposable
{
    BrokerKind Kind { get; }

    IObservable<ConnectionState> ConnectionState { get; }

    Task ConnectAsync(CancellationToken ct = default);

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

    /// <summary>
    /// Streaming tick-by-tick bid/ask quotes. The sequence completes when <paramref name="ct"/>
    /// is cancelled or the connection is permanently lost. Implementations are responsible
    /// for marshalling raw broker callbacks onto a single producer thread before yielding.
    /// </summary>
    IAsyncEnumerable<Tick> SubscribeTicksAsync(
        Contract contract,
        CancellationToken ct = default);
}
