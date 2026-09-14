using TradingTerminal.Core.Domain;

namespace DaxAlgo.Blocks;

/// <summary>Loads stored bars and trades from before the unit started.</summary>
[BlockCard(
    "history",
    "stored bars and trades from the past, for warm-up and backfill",
    Does = "Reads bars and trade prints the terminal has already recorded, for any time range.",
    Needs = "An InstrumentId and a UTC time range.",
    Limits = "Returns what was recorded — gaps stay gaps. At most MaxItems items per call; page through longer ranges. Await it inside StartAsync or post the result back with schedule.Post.",
    Order = 30)]
public interface IHistory
{
    /// <summary>The most items one call returns.</summary>
    const int MaxItems = 100_000;

    /// <summary>Recorded bars of one size between two UTC times, oldest first.</summary>
    Task<IReadOnlyList<OhlcvBar>> BarsAsync(
        InstrumentId instrument, BarSize size, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);

    /// <summary>Recorded trade prints between two UTC times, oldest first.</summary>
    Task<IReadOnlyList<TradePrint>> TradesAsync(
        InstrumentId instrument, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default);
}
