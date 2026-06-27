using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// A point-in-time position in one instrument. <see cref="Quantity"/> is signed (positive long,
/// negative short); <see cref="UnrealizedPnl"/> is marked to the latest seen price and
/// <see cref="RealizedPnl"/> accumulates over closed quantity. PnL is in account currency (the
/// instrument's contract multiplier is already applied).
/// </summary>
public readonly record struct Position(
    InstrumentId Instrument,
    long Quantity,
    double AveragePrice,
    double UnrealizedPnl,
    double RealizedPnl)
{
    public bool IsFlat => Quantity == 0;
    public bool IsLong => Quantity > 0;
    public bool IsShort => Quantity < 0;

    public static Position Flat(InstrumentId instrument) => new(instrument, 0, 0, 0, 0);
}

/// <summary>
/// Read-only view of the account a strategy kernel may query mid-run to size orders and check
/// exposure. It is deliberately read-only — the only way to change positions is to submit orders
/// through <see cref="IStrategyContext.Router"/>, so all state transitions flow through the
/// engine's accounting and stay deterministic.
/// </summary>
public interface IPortfolioView
{
    /// <summary>Free cash (realized PnL net of fees), excluding open-position marks.</summary>
    double Cash { get; }

    /// <summary>Cash plus the mark-to-market value of every open position.</summary>
    double Equity { get; }

    /// <summary>The current position in one instrument; a flat position if none is held.</summary>
    Position PositionOf(InstrumentId instrument);

    /// <summary>Every non-flat position across the universe.</summary>
    IReadOnlyCollection<Position> OpenPositions { get; }
}
