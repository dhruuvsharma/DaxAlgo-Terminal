using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Backtest;

/// <summary>
/// Position + cash accounting for the backtester. Pairs entry fills with exit fills FIFO to
/// produce round-trip <see cref="Trade"/> records. Handles flips (buy that overshoots a
/// short, or vice versa): the part that closes the existing position emits a trade; the
/// remainder opens a new lot in the opposite direction.
/// </summary>
internal sealed class TradeLedger
{
    private readonly double _multiplier;
    private readonly List<Trade> _trades = new();
    private readonly Queue<Lot> _openLots = new();

    public TradeLedger(double multiplier, double startingCash)
    {
        _multiplier = multiplier;
        Cash = startingCash;
    }

    public double Cash { get; private set; }
    public long NetPosition { get; private set; }
    public IReadOnlyList<Trade> Trades => _trades;

    public void OnFill(DateTime utc, OrderSide side, long qty, double price)
    {
        var signed = side == OrderSide.Buy ? qty : -qty;
        Cash -= signed * price * _multiplier;

        var remaining = qty;
        while (remaining > 0 && _openLots.Count > 0 && Math.Sign(_openLots.Peek().SignedQty) != Math.Sign(signed))
        {
            var head = _openLots.Peek();
            var closeable = Math.Min(remaining, Math.Abs(head.SignedQty));
            var headSide = head.SignedQty > 0 ? OrderSide.Buy : OrderSide.Sell;
            var grossPnl = headSide == OrderSide.Buy
                ? (price - head.Price) * closeable
                : (head.Price - price) * closeable;

            _trades.Add(new Trade(head.OpenedUtc, utc, headSide, closeable, head.Price, price, grossPnl));

            remaining -= closeable;
            if (Math.Abs(head.SignedQty) == closeable)
                _openLots.Dequeue();
            else
                _openLots.Enqueue(_openLots.Dequeue() with { SignedQty = head.SignedQty - Math.Sign(head.SignedQty) * closeable });
        }

        if (remaining > 0)
        {
            var lotSign = signed > 0 ? +1 : -1;
            _openLots.Enqueue(new Lot(utc, price, lotSign * remaining));
        }

        NetPosition += signed;
    }

    public double Equity(double mid) => Cash + NetPosition * mid * _multiplier;

    private readonly record struct Lot(DateTime OpenedUtc, double Price, long SignedQty);
}
