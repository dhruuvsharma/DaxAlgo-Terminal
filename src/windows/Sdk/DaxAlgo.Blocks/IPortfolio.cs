using TradingTerminal.Core.Domain;

namespace DaxAlgo.Blocks;

/// <summary>Reads the unit's positions, pending entries, fills and P&amp;L.</summary>
[BlockCard(
    "portfolio",
    "read positions, fills and P&L",
    Does = "Shows what the orders block actually achieved: position per instrument, pending entries, recent fills and account P&L.",
    Needs = "Nothing. A visualizer sees an empty portfolio.",
    Limits = "Read-only. Values reflect fills up to the last event the unit handled. P&L is net of commission and slippage.",
    Types = [typeof(PositionView), typeof(PendingEntryView), typeof(FillView), typeof(AccountView)],
    Order = 80)]
public interface IPortfolio
{
    /// <summary>The position in one instrument; zero units when there is none.</summary>
    PositionView Position(InstrumentId instrument);

    /// <summary>Every open position.</summary>
    IReadOnlyList<PositionView> Positions { get; }

    /// <summary>Every pending entry waiting for its price.</summary>
    IReadOnlyList<PendingEntryView> PendingEntries { get; }

    /// <summary>The most recent fills, oldest first.</summary>
    IReadOnlyList<FillView> RecentFills(int count);

    /// <summary>Equity and P&amp;L across every instrument.</summary>
    AccountView Account { get; }

    /// <summary>Calls <paramref name="handler"/> for every fill; dispose to stop.</summary>
    IDisposable OnFill(Action<FillView> handler);
}

/// <summary>One position.</summary>
public sealed record PositionView(
    InstrumentId Instrument,
    double Units,
    double AveragePrice,
    double UnrealizedPnl,
    double RealizedPnl,
    double? StopPrice,
    double? TakeProfitPrice);

/// <summary>One pending entry.</summary>
public sealed record PendingEntryView(
    InstrumentId Instrument,
    double Units,
    EntryKind Kind,
    double Price);

/// <summary>One fill. <c>Units</c> is signed: positive bought, negative sold.</summary>
public sealed record FillView(
    InstrumentId Instrument,
    DateTime TimeUtc,
    double Units,
    double Price,
    double Commission);

/// <summary>The account across every instrument.</summary>
public sealed record AccountView(
    double Equity,
    double RealizedPnl,
    double UnrealizedPnl,
    double Commission);
