using TradingTerminal.Core.Domain;

namespace TradingTerminal.Execution;

/// <summary>Whether an intent's signed units are a target position or a change from current position.</summary>
public enum TradeIntentQuantityMode : byte
{
    /// <summary>Converge the venue position to <see cref="TradeIntent.SignedUnits"/>.</summary>
    TargetPosition = 0,

    /// <summary>Change the venue position by <see cref="TradeIntent.SignedUnits"/>.</summary>
    Delta = 1,
}

/// <summary>
/// One canonical economic instruction from unified-execution ADR D1/D4. Quantity, price, and money
/// are exact coefficient/scale values; policy and strategy provenance make the instruction
/// reproducible and explainable without ambient state.
/// </summary>
/// <param name="Instrument">The canonical instrument to trade.</param>
/// <param name="QuantityMode">Whether <paramref name="SignedUnits"/> is a target or a delta.</param>
/// <param name="SignedUnits">Signed exact units: positive long, negative short.</param>
/// <param name="ProtectiveStopPrice">Optional exact stop price on the protective side of entry.</param>
/// <param name="ProfitTargetPrice">Optional exact profit-target price.</param>
/// <param name="EntryLimitPrice">
/// Optional exact price the entry may not trade through. With <paramref name="EntryStopPrice"/> this
/// states the entry's price condition: neither is a market entry, limit alone a limit entry, stop
/// alone a stop entry, both a stop-limit entry. These are economic terms the strategy or operator
/// owns - they still name no venue, broker, account, or route.
/// </param>
/// <param name="EntryStopPrice">Optional exact price at which the entry activates.</param>
/// <param name="EstimatedRoundTripCostPerUnit">Exact cost assumption used during sizing.</param>
/// <param name="StrategyId">Stable strategy provenance supplied by the host.</param>
/// <param name="StrategyNoteId">The originating strategy signal's numeric note identifier.</param>
/// <param name="PolicyVersion">Immutable host policy version used to produce this intent.</param>
public readonly record struct TradeIntent(
    InstrumentId Instrument,
    TradeIntentQuantityMode QuantityMode,
    ScaledQuantity SignedUnits,
    ScaledPrice? ProtectiveStopPrice,
    ScaledPrice? ProfitTargetPrice,
    ScaledMoney EstimatedRoundTripCostPerUnit,
    string StrategyId,
    long StrategyNoteId,
    string PolicyVersion,
    ScaledPrice? EntryLimitPrice = null,
    ScaledPrice? EntryStopPrice = null);
