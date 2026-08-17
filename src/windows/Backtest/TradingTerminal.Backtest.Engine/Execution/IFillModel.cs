using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Execution;

/// <summary>
/// Decides whether a working order fills against the current quote and at what price.
/// <paramref name="tickSize"/> is passed per call because it varies per instrument in a portfolio
/// run. Depth-aware models may also consult the latest L2 snapshot for the same instrument.
/// </summary>
internal interface IFillModel
{
    bool TryFill(
        WorkingOrder order,
        Tick quote,
        double tickSize,
        DepthSnapshot? depth,
        out double fillPrice,
        out long fillQty);
}

internal static class FillModels
{
    public static IFillModel Create(FillModelKind kind, int slippageTicks) => kind switch
    {
        FillModelKind.L1Touch => new L1TouchFillModel(slippageTicks),
        FillModelKind.MidPrice => new MidPriceFillModel(slippageTicks),
        FillModelKind.NextBarOpen => new NextBarOpenFillModel(slippageTicks),
        FillModelKind.DepthWalk => new DepthWalkFillModel(slippageTicks),
        _ => throw new NotSupportedException($"Fill model '{kind}' is not supported."),
    };
}

/// <summary>
/// Level-1 fill model. Market orders cross the spread plus <c>slippageTicks * tickSize</c>; limits
/// fill when the opposite touch crosses the limit; stops trigger when the relevant touch crosses the
/// stop, then fill like a market order. Conservative: buys pay the ask, sells hit the bid.
/// </summary>
internal sealed class L1TouchFillModel : IFillModel
{
    private readonly int _slippageTicks;

    public L1TouchFillModel(int slippageTicks)
    {
        if (slippageTicks < 0) throw new ArgumentOutOfRangeException(nameof(slippageTicks));
        _slippageTicks = slippageTicks;
    }

    public bool TryFill(
        WorkingOrder o, Tick tick, double tickSize, DepthSnapshot? depth,
        out double fillPrice, out long fillQty)
    {
        _ = depth;
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
                goto case OrderType.Limit;

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

    public bool TryFill(
        WorkingOrder o, Tick tick, double tickSize, DepthSnapshot? depth,
        out double fillPrice, out long fillQty)
    {
        _ = depth;
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
/// Conservative bar-mode fill: market orders skip the decision tick and fill on the next quote.
/// </summary>
internal sealed class NextBarOpenFillModel : IFillModel
{
    private readonly L1TouchFillModel _touch;
    private readonly HashSet<string> _deferred = new(StringComparer.Ordinal);

    public NextBarOpenFillModel(int slippageTicks) => _touch = new L1TouchFillModel(slippageTicks);

    public bool TryFill(
        WorkingOrder o, Tick tick, double tickSize, DepthSnapshot? depth,
        out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;

        if (o.Request.Type != OrderType.Market)
            return _touch.TryFill(o, tick, tickSize, depth, out fillPrice, out fillQty);

        var key = o.BrokerOrderId;
        if (!_deferred.Add(key))
            return _touch.TryFill(o, tick, tickSize, depth, out fillPrice, out fillQty);

        return false;
    }
}

/// <summary>
/// Walks opposing L2 levels for market orders (VWAP of taken size). Limits/stops fall back to L1.
/// When depth is missing or empty, behaves like <see cref="L1TouchFillModel"/>.
/// </summary>
internal sealed class DepthWalkFillModel : IFillModel
{
    private readonly L1TouchFillModel _touch;
    private readonly int _slippageTicks;

    public DepthWalkFillModel(int slippageTicks)
    {
        _slippageTicks = slippageTicks;
        _touch = new L1TouchFillModel(slippageTicks);
    }

    public bool TryFill(
        WorkingOrder o, Tick tick, double tickSize, DepthSnapshot? depth,
        out double fillPrice, out long fillQty)
    {
        fillPrice = 0;
        fillQty = 0;

        if (o.Request.Type != OrderType.Market || depth is null)
            return _touch.TryFill(o, tick, tickSize, depth, out fillPrice, out fillQty);

        var remaining = o.Request.Quantity - o.FilledQuantity;
        if (remaining <= 0) return false;

        var levels = o.Request.Side == OrderSide.Buy ? depth.Asks : depth.Bids;
        if (levels.Count == 0)
            return _touch.TryFill(o, tick, tickSize, depth, out fillPrice, out fillQty);

        long taken = 0;
        double notional = 0;
        foreach (var lvl in levels)
        {
            if (taken >= remaining) break;
            if (lvl.Size <= 0 || lvl.Price <= 0) continue;
            var qty = Math.Min(remaining - taken, lvl.Size);
            taken += qty;
            notional += qty * lvl.Price;
        }

        if (taken <= 0)
            return _touch.TryFill(o, tick, tickSize, depth, out fillPrice, out fillQty);

        var slip = _slippageTicks * tickSize;
        var vwap = notional / taken;
        fillPrice = o.Request.Side == OrderSide.Buy ? vwap + slip : vwap - slip;
        fillQty = taken;
        return true;
    }
}
