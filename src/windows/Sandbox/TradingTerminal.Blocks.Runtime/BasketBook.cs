using DaxAlgo.Blocks;
using DaxAlgo.Sdk;
using TradingTerminal.Core.Domain;
using TradingTerminal.Sandbox.Portfolio;
using TradingTerminal.Sandbox.Runtime;

namespace TradingTerminal.Blocks.Runtime;

/// <summary>
/// The orders and portfolio blocks: one simulated account per instrument the unit trades.
///
/// <para><b>A basket of single-instrument accounts rather than a new basket engine.</b> The simulator
/// behind <see cref="ModelPortfolioAccount"/> is proven, bounded and tested, and it models one
/// position. An arbitrage needs two positions, not one position in two instruments, so each leg gets
/// its own account and the account view sums them. What that does not model is margin shared between
/// legs, which the single-instrument runtime does not model either.</para>
///
/// <para><b>An order is worked at its instrument's next price.</b> A handler, a timer or a message from
/// the page only records what the unit wants; the account moves when a quote, trade, bar or depth
/// update for that instrument arrives, in the same window as that event's handlers. That is what keeps
/// a fill at a real price rather than at a price the unit last happened to see.</para>
/// </summary>
internal sealed class BasketBook(Func<DateTime> now, Action<string> warn, int fillWindow = 512) : IOrders, IPortfolio
{
    /// <summary>What each simulated account starts with — the simulator's own starting equity.</summary>
    internal const double StartingEquity = 100_000d;

    private readonly Dictionary<InstrumentId, ModelPortfolioAccount> _accounts = new();
    private readonly Dictionary<InstrumentId, VirtualTargetIntent> _pending = new();
    private readonly Dictionary<InstrumentId, double> _marks = new();
    private readonly HashSet<InstrumentId> _warnedWaiting = [];
    private readonly RingBuffer<FillView> _fills = new(fillWindow);
    private readonly List<Action<FillView>> _fillHandlers = [];

    /// <summary>True once the unit has used the orders block — the unit is a strategy.</summary>
    public bool Touched { get; private set; }

    // ── orders ──────────────────────────────────────────────────────────────────────────────────

    public void SetTarget(InstrumentId instrument, double units, double? stopPrice = null, double? takeProfitPrice = null)
    {
        Require(instrument, units);
        RequirePrice(stopPrice, nameof(stopPrice));
        RequirePrice(takeProfitPrice, nameof(takeProfitPrice));
        Record(new VirtualTargetIntent(instrument, units, stopPrice, takeProfitPrice));
    }

    public void EnterAt(
        InstrumentId instrument, double units, EntryKind kind, double price,
        double? stopPrice = null, double? takeProfitPrice = null)
    {
        Require(instrument, units);
        RequirePrice(price, nameof(price));
        RequirePrice(stopPrice, nameof(stopPrice));
        RequirePrice(takeProfitPrice, nameof(takeProfitPrice));
        Record(new VirtualTargetIntent(
            instrument, units, stopPrice, takeProfitPrice,
            kind == EntryKind.Stop ? VirtualEntryKind.Stop : VirtualEntryKind.Limit,
            price));
    }

    public void CancelEntry(InstrumentId instrument)
    {
        Require(instrument, 0d);

        // A plain target at the current position is how the account is told the entry is off.
        if (_accounts.TryGetValue(instrument, out var account) && account.Snapshot.PendingEntry is not null)
            Record(new VirtualTargetIntent(instrument, account.Snapshot.PositionUnits));
        else if (_pending.TryGetValue(instrument, out var intent) && intent.IsPendingEntry)
            _pending.Remove(instrument);
    }

    public void Flatten(InstrumentId instrument)
    {
        Require(instrument, 0d);
        Record(new VirtualTargetIntent(instrument, 0d));
    }

    public void FlattenAll()
    {
        Touched = true;
        foreach (var instrument in _accounts.Keys.Concat(_pending.Keys).Distinct().ToArray())
            Record(new VirtualTargetIntent(instrument, 0d));
    }

    // ── portfolio ───────────────────────────────────────────────────────────────────────────────

    public PositionView Position(InstrumentId instrument) =>
        _accounts.TryGetValue(instrument, out var account)
            ? View(instrument, account.Snapshot)
            : new PositionView(instrument, 0d, 0d, 0d, 0d, null, null);

    public IReadOnlyList<PositionView> Positions =>
        [.. _accounts.Where(a => a.Value.Snapshot.PositionUnits != 0d).Select(a => View(a.Key, a.Value.Snapshot))];

    public IReadOnlyList<PendingEntryView> PendingEntries =>
        [.. _accounts
            .Where(a => a.Value.Snapshot.PendingEntry is not null)
            .Select(a =>
            {
                var entry = a.Value.Snapshot.PendingEntry!.Value;
                return new PendingEntryView(a.Key, entry.SignedTargetUnits, entry.IsStop ? EntryKind.Stop : EntryKind.Limit, entry.TriggerPrice);
            })];

    public IReadOnlyList<FillView> RecentFills(int count) => _fills.Newest(count);

    public AccountView Account
    {
        get
        {
            double realized = 0d, unrealized = 0d, commission = 0d;
            foreach (var (instrument, account) in _accounts)
            {
                var view = View(instrument, account.Snapshot);
                realized += view.RealizedPnl;
                unrealized += view.UnrealizedPnl;
                commission += account.Snapshot.CommissionTotal;
            }

            return new AccountView(StartingEquity + realized + unrealized, realized, unrealized, commission);
        }
    }

    public IDisposable OnFill(Action<FillView> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _fillHandlers.Add(handler);
        return new Disposer(() => _fillHandlers.Remove(handler));
    }

    // ── the price window ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Works the account for the event's instrument after its handlers have run: opens the window at
    /// the event's price, submits what the unit asked for, reconciles and commits. Returns true when the
    /// portfolio changed. Unit thread only.
    /// </summary>
    public bool OnPrice(MarketEvent evt)
    {
        if (!Reference(evt, out var bid, out var ask, out var last, out var barClose, out var mark))
            return false;

        var instrument = evt.Instrument;
        _marks[instrument] = mark;

        var hasAccount = _accounts.TryGetValue(instrument, out var account);
        var hasIntent = _pending.TryGetValue(instrument, out var intent);
        if (!hasAccount && !hasIntent) return false;

        account ??= _accounts[instrument] = new ModelPortfolioAccount(instrument);
        var before = account.Snapshot;

        if (barClose is double close) account.BeginBar(close);
        else account.BeginTick(bid, ask, last);

        if (account.LastFault != ModelPortfolioFault.None)
        {
            account.Rollback();
            warn($"The simulated account for instrument {instrument.Value} could not open at this price ({account.LastFault}).");
            return false;
        }

        if (hasIntent) account.Book.SubmitTarget(intent!);

        account.ReconcileToTargets();
        if (account.LastFault == ModelPortfolioFault.None) account.Commit();

        if (account.LastFault != ModelPortfolioFault.None)
        {
            var fault = account.LastFault;
            account.Rollback();

            // Dropped rather than retried: the same request would be refused at every later price.
            if (hasIntent)
            {
                _pending.Remove(instrument);
                warn($"The order for instrument {instrument.Value} was refused ({fault}).");
            }

            return false;
        }

        if (hasIntent) _pending.Remove(instrument);

        var after = account.Snapshot;
        Fill(instrument, before, after, mark);
        return !before.Equals(after);
    }

    private void Record(VirtualTargetIntent intent)
    {
        Touched = true;
        _pending[intent.Instrument] = intent;

        if (!_marks.ContainsKey(intent.Instrument) && _warnedWaiting.Add(intent.Instrument))
            warn($"An order for instrument {intent.Instrument.Value} is waiting for its first price — subscribe to it through the market block.");
    }

    private void Fill(InstrumentId instrument, SandboxPortfolioSnapshot before, SandboxPortfolioSnapshot after, double mark)
    {
        var units = after.PositionUnits - before.PositionUnits;
        if (units == 0d) return;

        var price = FillPrice(before, after);
        var fill = new FillView(
            instrument,
            now(),
            units,
            double.IsFinite(price) && price > 0d ? price : mark,
            after.CommissionTotal - before.CommissionTotal);

        _fills.Add(fill);
        foreach (var handler in _fillHandlers.ToArray())
        {
            try { handler(fill); }
            catch (Exception ex) { warn($"A fill handler failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// The price a position change happened at, recovered from the account's own books: the change in
    /// cost basis when a position grows, the change in realised profit when it shrinks, and the new
    /// average when it reverses.
    /// </summary>
    internal static double FillPrice(SandboxPortfolioSnapshot before, SandboxPortfolioSnapshot after)
    {
        var from = before.PositionQuantity;
        var to = after.PositionQuantity;

        if ((from == 0d || Math.Sign(from) == Math.Sign(to)) && Math.Abs(to) > Math.Abs(from))
            return (after.AverageEntryPrice * Math.Abs(to) - before.AverageEntryPrice * Math.Abs(from))
                / (Math.Abs(to) - Math.Abs(from));

        if (from != 0d && (to == 0d || Math.Sign(to) == Math.Sign(from)) && Math.Abs(to) < Math.Abs(from))
        {
            var closed = Math.Abs(from) - Math.Abs(to);
            var realized = after.RealizedGrossProfitLoss - before.RealizedGrossProfitLoss;
            return before.AverageEntryPrice + Math.Sign(from) * realized / closed;
        }

        return after.AverageEntryPrice;
    }

    private PositionView View(InstrumentId instrument, SandboxPortfolioSnapshot snapshot)
    {
        var mark = _marks.GetValueOrDefault(instrument, snapshot.AverageEntryPrice);
        var unrealized = snapshot.PositionQuantity == 0d ? 0d : (mark - snapshot.AverageEntryPrice) * snapshot.PositionQuantity;
        var realized = snapshot.RealizedGrossProfitLoss - snapshot.CommissionTotal - snapshot.SlippageTotal;

        return new PositionView(
            instrument,
            snapshot.PositionUnits,
            snapshot.AverageEntryPrice,
            unrealized,
            realized,
            snapshot.ProtectiveStopPrice,
            snapshot.ProfitTargetPrice);
    }

    /// <summary>
    /// The prices an event gives the account, by the rules the sandbox runtime uses. A forming bar is
    /// worked as a tick at its close: opening a bar window for every partial update would count one bar
    /// as many.
    /// </summary>
    private static bool Reference(
        MarketEvent evt, out double bid, out double ask, out double last, out double? barClose, out double mark)
    {
        bid = ask = last = mark = 0d;
        barClose = null;

        switch (evt.Payload)
        {
            case Quote quote when Valid(quote.Bid) && Valid(quote.Ask) && quote.Ask >= quote.Bid:
                (bid, ask, mark) = (quote.Bid, quote.Ask, quote.Mid);
                return true;

            case TradePrint trade when Valid(trade.Price):
                (last, mark) = (trade.Price, trade.Price);
                return true;

            case OhlcvBar bar when Valid(bar.Close):
                mark = bar.Close;
                if (bar.IsFinal) barClose = bar.Close;
                else last = bar.Close;
                return true;

            case DepthSnapshot depth when Valid(depth.BestBid) && Valid(depth.BestAsk) && depth.BestAsk >= depth.BestBid:
                (bid, ask, mark) = (depth.BestBid, depth.BestAsk, (depth.BestBid + depth.BestAsk) / 2d);
                return true;

            default:
                return false;
        }
    }

    private static bool Valid(double price) => double.IsFinite(price) && price > 0d;

    private static void Require(InstrumentId instrument, double units)
    {
        if (instrument.IsNone) throw new ArgumentException("Choose an instrument; this one is unset.", nameof(instrument));
        if (!double.IsFinite(units)) throw new ArgumentOutOfRangeException(nameof(units), "Units must be a finite number.");
    }

    private static void RequirePrice(double? price, string name)
    {
        if (price is double value && !Valid(value))
            throw new ArgumentOutOfRangeException(name, "A price must be a finite number above zero.");
    }
}
