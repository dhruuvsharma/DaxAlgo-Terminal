using TradingTerminal.Core.Domain;

namespace DaxAlgo.Blocks;

/// <summary>Changes the unit's positions. Using this block makes the unit a strategy.</summary>
[BlockCard(
    "orders",
    "change positions — using this block makes the unit a strategy",
    Does = "Sets the position you want per instrument, or a price at which to enter; the host works the orders and records the fills.",
    Needs = "An InstrumentId the unit subscribed to through the market block, so the host has prices to fill against.",
    Limits = "Positions are in instrument units, signed: positive long, negative short, 0 flat. The latest call per instrument replaces the previous one. An order is worked at that instrument's next price (quote, trade, bar or depth), so one placed from a timer or the page waits for it. Each instrument is its own simulated account. Read what actually happened through the portfolio block.",
    Types = [typeof(EntryKind)],
    Order = 70)]
public interface IOrders
{
    /// <summary>Moves the position in <paramref name="instrument"/> to <paramref name="units"/> now, with an optional protective stop and take-profit.</summary>
    void SetTarget(InstrumentId instrument, double units, double? stopPrice = null, double? takeProfitPrice = null);

    /// <summary>Moves the position to <paramref name="units"/> only once price reaches <paramref name="price"/>, as a limit or stop entry.</summary>
    void EnterAt(
        InstrumentId instrument, double units, EntryKind kind, double price,
        double? stopPrice = null, double? takeProfitPrice = null);

    /// <summary>Cancels any pending entry for <paramref name="instrument"/> without changing the position.</summary>
    void CancelEntry(InstrumentId instrument);

    /// <summary>Closes the position in <paramref name="instrument"/> and cancels its pending entry.</summary>
    void Flatten(InstrumentId instrument);

    /// <summary>Closes every position and cancels every pending entry.</summary>
    void FlattenAll();
}

/// <summary>How a pending entry triggers.</summary>
public enum EntryKind
{
    /// <summary>Fills at the price or better: buy at or below, sell at or above.</summary>
    Limit,

    /// <summary>Triggers once price trades through: buy at or above, sell at or below.</summary>
    Stop,
}
