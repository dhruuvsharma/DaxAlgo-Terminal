using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Backtest.Engine.Accounting;

/// <summary>
/// Cash + position accounting across every instrument in the universe. Fills update one shared cash
/// balance; positions are tracked per instrument with FIFO lots that pair entries to exits into
/// round-trip <see cref="RoundTripTrade"/> records. While a lot is open the portfolio tracks its
/// maximum favorable / adverse excursion from quote marks, so closed trades carry MFE/MAE for the
/// report. Handles flips (a fill that overshoots the opposite position closes it and opens the rest).
///
/// Single-threaded — mutated only from the engine's replay loop.
/// </summary>
internal sealed class Portfolio
{
    private readonly IReadOnlyDictionary<InstrumentId, double> _multipliers;
    private readonly IFeeModel _feeModel;
    private readonly Dictionary<InstrumentId, Book> _books = new();
    private readonly List<RoundTripTrade> _trades = new();

    public Portfolio(double startingCash, IReadOnlyDictionary<InstrumentId, double> multipliers, IFeeModel feeModel)
    {
        Cash = startingCash;
        _multipliers = multipliers;
        _feeModel = feeModel;
    }

    public double Cash { get; private set; }
    public double TotalFees { get; private set; }
    public IReadOnlyList<RoundTripTrade> Trades => _trades;

    private double Multiplier(InstrumentId id) => _multipliers.TryGetValue(id, out var m) ? m : 1.0;
    private Book BookOf(InstrumentId id) => _books.TryGetValue(id, out var b) ? b : (_books[id] = new Book());

    public void OnFill(InstrumentId id, DateTime utc, OrderSide side, long qty, double price, LiquidityFlag liquidity)
    {
        var mult = Multiplier(id);
        var book = BookOf(id);
        var signed = side == OrderSide.Buy ? qty : -qty;

        Cash -= signed * price * mult;
        var fee = _feeModel.Fee(side, qty, price, liquidity);
        Cash -= fee;
        TotalFees += fee;

        var remaining = qty;
        while (remaining > 0 && book.Lots.Count > 0 && Math.Sign(book.Lots.Peek().SignedQty) != Math.Sign(signed))
        {
            var head = book.Lots.Peek();
            var closeable = Math.Min(remaining, Math.Abs(head.SignedQty));
            var headSide = head.SignedQty > 0 ? OrderSide.Buy : OrderSide.Sell;
            var grossPoints = headSide == OrderSide.Buy ? price - head.Price : head.Price - price;
            var grossPnl = grossPoints * closeable * mult;
            var fees = ApportionFees(fee, closeable, qty);

            _trades.Add(new RoundTripTrade(
                Instrument: id,
                EntryUtc: head.OpenedUtc,
                ExitUtc: utc,
                Side: headSide,
                Quantity: closeable,
                EntryPrice: head.Price,
                ExitPrice: price,
                GrossPnl: grossPnl,
                Fees: fees,
                MaxFavorableExcursion: head.MaxFavorablePoints * closeable * mult,
                MaxAdverseExcursion: head.MaxAdversePoints * closeable * mult));

            book.RealizedPnl += grossPnl - fees;
            remaining -= closeable;
            if (Math.Abs(head.SignedQty) == closeable)
                book.Lots.Dequeue();
            else
                book.Lots.Enqueue(book.Lots.Dequeue() with { SignedQty = head.SignedQty - Math.Sign(head.SignedQty) * closeable });
        }

        if (remaining > 0)
            book.Lots.Enqueue(new Lot(utc, price, (signed > 0 ? +1 : -1) * remaining, 0, 0));

        book.NetPosition += signed;
    }

    /// <summary>Update the latest mark for an instrument and the open lots' favorable/adverse excursions.</summary>
    public void OnMark(InstrumentId id, double mark)
    {
        var book = BookOf(id);
        book.Mark = mark;
        if (book.Lots.Count == 0) return;

        // Queue<T> isn't index-mutable; rebuild with refreshed excursions (lot counts are tiny).
        var refreshed = new Queue<Lot>(book.Lots.Count);
        foreach (var lot in book.Lots)
        {
            var favorablePoints = lot.SignedQty > 0 ? mark - lot.Price : lot.Price - mark;
            refreshed.Enqueue(lot with
            {
                MaxFavorablePoints = Math.Max(lot.MaxFavorablePoints, favorablePoints),
                MaxAdversePoints = Math.Min(lot.MaxAdversePoints, favorablePoints),
            });
        }
        book.Lots = refreshed;
    }

    public double Equity()
    {
        var eq = Cash;
        foreach (var (id, book) in _books)
            eq += book.NetPosition * book.Mark * Multiplier(id);
        return eq;
    }

    public Position SnapshotOf(InstrumentId id)
    {
        if (!_books.TryGetValue(id, out var book) || book.NetPosition == 0)
            return Position.Flat(id);

        var mult = Multiplier(id);
        var avg = book.AveragePrice;
        var unrealized = (book.Mark - avg) * book.NetPosition * mult;
        return new Position(id, book.NetPosition, avg, unrealized, book.RealizedPnl);
    }

    public IReadOnlyCollection<Position> OpenPositions() =>
        _books.Where(kv => kv.Value.NetPosition != 0).Select(kv => SnapshotOf(kv.Key)).ToList();

    private static double ApportionFees(double totalFee, long part, long whole) =>
        whole == 0 ? 0 : totalFee * part / whole;

    private sealed class Book
    {
        public Queue<Lot> Lots = new();
        public long NetPosition;
        public double Mark;
        public double RealizedPnl;

        public double AveragePrice
        {
            get
            {
                long qty = 0;
                double notional = 0;
                foreach (var lot in Lots) { qty += Math.Abs(lot.SignedQty); notional += Math.Abs(lot.SignedQty) * lot.Price; }
                return qty == 0 ? 0 : notional / qty;
            }
        }
    }

    private readonly record struct Lot(
        DateTime OpenedUtc, double Price, long SignedQty, double MaxFavorablePoints, double MaxAdversePoints);
}
