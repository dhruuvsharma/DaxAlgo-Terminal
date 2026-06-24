using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Strategy for deciding whether a pending order fills against the current L1 quote and
/// at what price. The first cut fills the full remaining quantity on a single tick;
/// queue-position effects and partial fills are out of scope.
/// </summary>
public interface IFillModel
{
    bool TryFill(PendingOrder order, Tick tick, out double fillPrice, out long fillQty);
}

/// <summary>
/// Level-1 fill model. Market orders cross the spread plus <c>slippageTicks * tickSize</c>;
/// limits fill when the opposite touch crosses the limit; stops trigger when the relevant
/// touch crosses the stop, then fill at touch + slippage like a market order.
///
/// Conservative: we use the side of the book that pays the spread (buy-at-ask, sell-at-bid).
/// </summary>
public sealed class L1FillModel : IFillModel
{
    private readonly double _tickSize;
    private readonly int _slippageTicks;

    public L1FillModel(double tickSize, int slippageTicks)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize));
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        _tickSize = tickSize;
        _slippageTicks = slippageTicks;
    }

    public bool TryFill(PendingOrder o, Tick tick, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var slip = _slippageTicks * _tickSize;
        var isBuy = o.Request.Side == OrderSide.Buy;

        switch (o.Request.Type)
        {
            case OrderType.Market:
                fillPrice = isBuy ? tick.Ask + slip : tick.Bid - slip;
                fillQty = remaining;
                return true;

            case OrderType.Limit:
            {
                var lp = o.Request.LimitPrice!.Value;
                if (isBuy && tick.Ask <= lp)
                {
                    fillPrice = Math.Min(tick.Ask, lp);
                    fillQty = remaining;
                    return true;
                }
                if (!isBuy && tick.Bid >= lp)
                {
                    fillPrice = Math.Max(tick.Bid, lp);
                    fillQty = remaining;
                    return true;
                }
                return false;
            }

            case OrderType.Stop:
            {
                var sp = o.Request.StopPrice!.Value;
                if (isBuy && tick.Ask >= sp)
                {
                    fillPrice = tick.Ask + slip;
                    fillQty = remaining;
                    return true;
                }
                if (!isBuy && tick.Bid <= sp)
                {
                    fillPrice = tick.Bid - slip;
                    fillQty = remaining;
                    return true;
                }
                return false;
            }

            case OrderType.StopLimit:
                // Out of scope for the first cut — treat as a limit immediately.
                goto case OrderType.Limit;

            default:
                return false;
        }
    }
}
