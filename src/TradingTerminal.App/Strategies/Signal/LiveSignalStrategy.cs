using TradingTerminal.Core.Strategies;

namespace TradingTerminal.App.Strategies.Signal;

/// <summary>
/// <see cref="ITradingStrategy"/> descriptor for a live "signal generator" wrapper around
/// a backtest strategy. One instance per <c>BacktestStrategyCatalog</c> entry — together
/// they populate the left-pane Strategies list with all signal-mode hosts.
///
/// The wrapped strategy's logic doesn't change; the host runs it against the live tick
/// stream and surfaces every <c>PlaceOrderAsync</c> as a notification + log row.
/// </summary>
internal sealed class LiveSignalStrategy : ITradingStrategy
{
    public LiveSignalStrategy(string id, string displayName, string description)
    {
        Id = id;
        DisplayName = displayName;
        Description = description;
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string Description { get; }
}
