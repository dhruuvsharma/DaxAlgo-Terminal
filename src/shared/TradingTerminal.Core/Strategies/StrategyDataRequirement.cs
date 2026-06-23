namespace TradingTerminal.Core.Strategies;

/// <summary>
/// What market data a <em>strategy</em> consumes — its appetite, declared by the
/// strategy itself. This is orthogonal to broker data-<em>capability</em> (what a
/// broker can actually serve): a strategy states what it needs; the wiring then
/// gates that need against the connected broker's capability matrix.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="L1"/> + <see cref="Bars"/> is the universal baseline for every
/// strategy: the live view-model base aggregates bars from the L1 quote stream,
/// so any strategy that subscribes to quotes already has both.
/// </para>
/// <para>
/// <see cref="Depth"/> and <see cref="TradeTape"/> are the informative extras —
/// they are not always available and must gate on broker capability before a
/// strategy that requires them is offered or started.
/// </para>
/// </remarks>
[Flags]
public enum StrategyDataRequirement
{
    /// <summary>No declared requirement.</summary>
    None = 0,

    /// <summary>Level-1 top-of-book quotes (best bid/ask). Part of the universal baseline.</summary>
    L1 = 1,

    /// <summary>OHLCV bars, aggregated downstream from the L1 quote stream. Part of the universal baseline.</summary>
    Bars = 2,

    /// <summary>Level-2 market depth (order book). Informative extra; gates on broker capability.</summary>
    Depth = 4,

    /// <summary>The trade tape (time and sales). Informative extra; gates on broker capability.</summary>
    TradeTape = 8,
}
