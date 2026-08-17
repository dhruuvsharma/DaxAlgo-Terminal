using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// Decides whether a working order fills against the current quote and at what price.
/// <paramref name="tickSize"/> is passed per call because it varies per instrument in a portfolio
/// run. The first cut fills the whole remaining quantity on a single quote; queue position and
/// partial fills are out of scope.
/// </summary>
internal interface IFillModel
{
    bool TryFill(WorkingOrder order, Tick quote, double tickSize, out double fillPrice, out long fillQty);
}

internal static class FillModels
{
    public static IFillModel Create(FillModelKind kind, int slippageTicks) => kind switch
    {
        FillModelKind.L1Touch => new L1TouchFillModel(slippageTicks),
        FillModelKind.MidPrice => new MidPriceFillModel(slippageTicks),
        FillModelKind.NextBarOpen => new NextBarOpenFillModel(slippageTicks),
        _ => throw new NotSupportedException($"Fill model '{kind}' is not supported."),
    };
}

/// <summary>
/// Level-1 fill model. Market orders cross the spread plus <c>slippageTicks * tickSize</c>; limits
/// fill when the opposite touch crosses the limit; stops trigger when the relevant touch crosses the
/// stop, then fill like a market order. Conservative: buys pay the ask, sells hit the bid.
/// Ported from the legacy <c>L1FillModel</c>, generalized to a per-call tick size.
/// </summary>
internal sealed class L1TouchFillModel : IFillModel
{
    private readonly int _slippageTicks;

    public L1TouchFillModel(int slippageTicks)
    {
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        _slippageTicks = slippageTicks;
    }

    public bool TryFill(WorkingOrder o, Tick tick, double tickSize, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var slip = _slippageTicks * tickSize;
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
                if (isBuy && tick.Ask <= lp) { fillPrice = Math.Min(tick.Ask, lp); fillQty = remaining; return true; }
                if (!isBuy && tick.Bid >= lp) { fillPrice = Math.Max(tick.Bid, lp); fillQty = remaining; return true; }
                return false;
            }

            case OrderType.Stop:
            {
                var sp = o.Request.StopPrice!.Value;
                if (isBuy && tick.Ask >= sp) { fillPrice = tick.Ask + slip; fillQty = remaining; return true; }
                if (!isBuy && tick.Bid <= sp) { fillPrice = tick.Bid - slip; fillQty = remaining; return true; }
                return false;
            }

            case OrderType.StopLimit:
                goto case OrderType.Limit; // out of scope for the first cut — treat as a limit

            default:
                return false;
        }
    }
}

/// <summary>Optimistic mid fill — upper bound on achievable performance (green rung).</summary>
internal sealed class MidPriceFillModel : IFillModel
{
    private readonly int _slippageTicks;

    public MidPriceFillModel(int slippageTicks)
    {
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        _slippageTicks = slippageTicks;
    }

    public bool TryFill(WorkingOrder o, Tick tick, double tickSize, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;
        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var mid = (tick.Bid + tick.Ask) / 2.0;
        var slip = _slippageTicks * tickSize;
        var isBuy = o.Request.Side == OrderSide.Buy;

        switch (o.Request.Type)
        {
            case OrderType.Market:
                fillPrice = isBuy ? mid + slip : mid - slip;
                fillQty = remaining;
                return true;

            case OrderType.Limit:
            {
                var lp = o.Request.LimitPrice!.Value;
                if (isBuy && mid <= lp) { fillPrice = Math.Min(mid, lp); fillQty = remaining; return true; }
                if (!isBuy && mid >= lp) { fillPrice = Math.Max(mid, lp); fillQty = remaining; return true; }
                return false;
            }

            case OrderType.Stop:
            {
                var sp = o.Request.StopPrice!.Value;
                if (isBuy && mid >= sp) { fillPrice = mid + slip; fillQty = remaining; return true; }
                if (!isBuy && mid <= sp) { fillPrice = mid - slip; fillQty = remaining; return true; }
                return false;
            }

            case OrderType.StopLimit:
                goto case OrderType.Limit;

            default:
                return false;
        }
    }
}

/// <summary>
/// Conservative bar-mode fill: market orders skip the decision tick and fill on the <em>next</em>
/// quote (stand-in for next bar open when only L1 ticks are available). Limits/stops still evaluate
/// on the current tick so resting liquidity is not frozen for a bar.
/// </summary>
internal sealed class NextBarOpenFillModel : IFillModel
{
    private readonly L1TouchFillModel _touch;
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);

    public NextBarOpenFillModel(int slippageTicks) => _touch = new L1TouchFillModel(slippageTicks);

    public bool TryFill(WorkingOrder o, Tick tick, double tickSize, out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;

        if (o.Request.Type != OrderType.Market)
            return _touch.TryFill(o, tick, tickSize, out fillPrice, out fillQty);

        var key = o.BrokerOrderId;
        if (!_deferred.Add(key))
        {
            // Second+ sighting: fill like L1 touch (next event ≈ next open).
            return _touch.TryFill(o, tick, tickSize, out fillPrice, out fillQty);
        }

        // First sighting on this order — wait for the next quote/event.
        return false;
    }
}
