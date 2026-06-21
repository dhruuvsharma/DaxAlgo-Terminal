using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Backtesting;

/// <summary>
/// One tradable instrument in a backtest, with the per-instrument economics the engine needs to
/// turn price moves into cash: <see cref="TickSize"/> (minimum increment, drives slippage and the
/// fill model), <see cref="ContractMultiplier"/> (dollars per point — 50 for ES, 1 for stocks and
/// crypto), and an optional <see cref="Source"/> to scope reads to one broker's data when the
/// canonical store is split per broker (<c>null</c> = all brokers merged).
/// </summary>
public sealed record InstrumentSpec(
    InstrumentId Id,
    Contract Contract,
    double TickSize = 0.01,
    double ContractMultiplier = 1.0,
    BrokerKind? Source = null);

/// <summary>
/// The set of instruments a single run trades. A one-element universe is the classic
/// single-instrument backtest; multiple elements is a portfolio run sharing one cash account, with
/// the engine merging every instrument's event stream in event-time order. The strategy kernel sees
/// which instrument each callback is for via the <see cref="InstrumentId"/> argument, so the same
/// kernel works for both shapes.
/// </summary>
public sealed record Universe(IReadOnlyList<InstrumentSpec> Instruments)
{
    public static Universe Single(InstrumentSpec instrument) => new(new[] { instrument });

    public static Universe Of(params InstrumentSpec[] instruments) => new(instruments);

    public bool IsSingleInstrument => Instruments.Count == 1;

    /// <summary>The first instrument — convenient for single-instrument runs and as a default scope.</summary>
    public InstrumentSpec Primary => Instruments[0];

    public InstrumentSpec? Find(InstrumentId id) => Instruments.FirstOrDefault(i => i.Id == id);
}
