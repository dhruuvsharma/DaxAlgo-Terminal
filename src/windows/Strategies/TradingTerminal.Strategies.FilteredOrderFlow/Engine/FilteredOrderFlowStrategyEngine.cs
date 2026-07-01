using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Strategies.FilteredOrderFlow.Engine;

/// <summary>
/// Filtered order-flow imbalance — Anantha, Jain &amp; Maiti (2025), "Order-Flow Filtration and
/// Directional Association with Short-Horizon Returns" (arXiv:2507.22712).
///
/// <para>The paper shows that <b>trade-based</b> order-book imbalance OBI(T) — the net signed-trade
/// count over a backward window (their Eq. 17) — carries a clean, causal directional imprint on
/// short-horizon returns, and that this imprint <em>sharpens</em> when the parent orders of fleeting
/// / heavily-revised trades are filtered out (their lifetime / modification-count / modification-time
/// filters). Strong OBI regimes excite same-sign return regimes.</para>
///
/// <para><b>Implementation note (data fidelity).</b> The paper's structural filters operate on
/// per-order lifecycle data (order id + modification/cancel history) available in NSE tick-by-tick
/// feeds. Our broker feeds expose a <em>signed trade tape</em> but not order-by-order lifecycles, so
/// the lifetime/modification filters are approximated at the tape level by a "genuine-intent" filter
/// that drops sub-threshold (odd-lot / fleeting) prints — the implementable analog of removing trades
/// whose parent orders wouldn't have survived filtration. The strategy maintains both the
/// <b>unfiltered</b> and <b>filtered</b> OBI(T) so the paper's central comparison (does filtration
/// strengthen the directional signal?) is observable live; the directional signal fires off the
/// filtered series.</para>
///
/// <para>Signal: when the filtered OBI(T) enters a strong regime (|regime| ≥
/// <see cref="StrongRegime"/> on the nine-bin grid, <see cref="OrderFlowImbalance"/>), take a
/// same-sign position; exit after <see cref="HoldSeconds"/> of event time or when the regime decays
/// back through the neutral band. Display/signals only — no live execution.</para>
/// </summary>
public sealed class FilteredOrderFlowStrategy : IBacktestStrategy
{
    /// <summary>Length of the backward window over which OBI(T) is accumulated (paper: h = 10s).</summary>
    public double WindowSeconds { get; }

    /// <summary>Minimum trade size kept by the filtered series — the tape-level analog of the paper's
    /// parent-order filtration (drops fleeting / odd-lot prints). 0 ⇒ filtered == unfiltered.</summary>
    public long MinTradeSize { get; }

    /// <summary>Regime-index magnitude (1..4) the filtered OBI must reach to arm a directional signal.</summary>
    public int StrongRegime { get; }

    /// <summary>Event-time holding period before a position is flattened (paper forecast scale: ~1s,
    /// widened here to a tradeable hold).</summary>
    public double HoldSeconds { get; }

    public long Quantity { get; }

    private readonly Contract _contract;

    // Rolling event-time window of signed trades, with running counts maintained incrementally.
    private readonly Queue<TradeRec> _window = new();
    private long _buyAll, _sellAll, _buyFilt, _sellFilt;

    private double _lastBid, _lastAsk;
    private double _priorTradePrice;
    private AggressorSide _priorTradeClass = AggressorSide.Unknown;
    private DateTime _lastEventTime = DateTime.MinValue;

    private long _position;
    private DateTime _entryTime;
    private int _orderSeq;

    public FilteredOrderFlowStrategy(
        Contract contract,
        double windowSeconds = 10.0,
        long minTradeSize = 2,
        int strongRegime = 3,
        double holdSeconds = 5.0,
        long quantity = 1)
    {
        _contract = contract;
        WindowSeconds = windowSeconds > 0 ? windowSeconds : 10.0;
        MinTradeSize = Math.Max(0, minTradeSize);
        StrongRegime = Math.Clamp(strongRegime, 1, 4);
        HoldSeconds = holdSeconds > 0 ? holdSeconds : 5.0;
        Quantity = Math.Max(1, quantity);
    }

    // ── Live, read-only state surfaced to the view-model for charting / dashboards ──────────────
    /// <summary>Filtered trade-based OBI(T) over the current window, in [−1, 1].</summary>
    public double FilteredObi { get; private set; }
    /// <summary>Unfiltered (all-trades) OBI(T) over the current window, in [−1, 1].</summary>
    public double UnfilteredObi { get; private set; }
    /// <summary>Signed regime index (−4..+4) of the filtered OBI.</summary>
    public int FilteredRegime { get; private set; }
    /// <summary>Trades currently inside the window (filtered count).</summary>
    public long FilteredTradesInWindow => _buyFilt + _sellFilt;
    /// <summary>Trades currently inside the window (all).</summary>
    public long UnfilteredTradesInWindow => _buyAll + _sellAll;
    /// <summary>Current net position from this strategy's signals (+long / −short / 0 flat).</summary>
    public long Position => _position;

    /// <summary>Raised on the dispatcher (the host marshals OnTick/OnTrade) after state updates,
    /// so the window can append a chart sample. Coalesce redraws off this — do not redraw per call.</summary>
    public event Action? Updated;

    public Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct) => Task.CompletedTask;

    public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        _lastBid = tick.Bid;
        _lastAsk = tick.Ask;
        return Task.CompletedTask;
    }

    public async Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        // Sign the trade: prefer the broker-reported aggressor, else the tick rule against the quote.
        var side = trade.Aggressor;
        if (side == AggressorSide.Unknown && _lastAsk > _lastBid)
            side = Microstructure.ClassifyAggressor(trade.Price, _lastBid, _lastAsk, _priorTradePrice, _priorTradeClass);
        _priorTradePrice = trade.Price;
        if (side != AggressorSide.Unknown) _priorTradeClass = side;
        if (side == AggressorSide.Unknown) return; // unclassifiable first prints are skipped (paper excludes them)

        var t = trade.EventTimeUtc;
        if (t > _lastEventTime) _lastEventTime = t;

        var kept = trade.Size >= MinTradeSize;
        _window.Enqueue(new TradeRec(t, side, kept));
        Accumulate(side, kept, +1);

        EvictExpired(_lastEventTime);
        Recompute();

        await MaybeTradeAsync(router, ct);
        Updated?.Invoke();
    }

    private void Accumulate(AggressorSide side, bool kept, int delta)
    {
        if (side == AggressorSide.Buy) { _buyAll += delta; if (kept) _buyFilt += delta; }
        else { _sellAll += delta; if (kept) _sellFilt += delta; }
    }

    private void EvictExpired(DateTime now)
    {
        var cutoff = now - TimeSpan.FromSeconds(WindowSeconds);
        while (_window.Count > 0 && _window.Peek().Time < cutoff)
        {
            var old = _window.Dequeue();
            Accumulate(old.Side, old.Kept, -1);
        }
    }

    private void Recompute()
    {
        UnfilteredObi = OrderFlowImbalance.TradeImbalance(_buyAll, _sellAll);
        FilteredObi = OrderFlowImbalance.TradeImbalance(_buyFilt, _sellFilt);
        FilteredRegime = OrderFlowImbalance.Regime(FilteredObi);
    }

    private async Task MaybeTradeAsync(IOrderRouter router, CancellationToken ct)
    {
        // Need a populated filtered window before acting.
        if (FilteredTradesInWindow <= 0) return;

        var strong = Math.Abs(FilteredRegime) >= StrongRegime;
        var lean = Math.Sign(FilteredRegime);

        if (_position == 0)
        {
            if (strong)
            {
                _position = lean > 0 ? Quantity : -Quantity;
                _entryTime = _lastEventTime;
                await Submit(router, lean > 0 ? OrderSide.Buy : OrderSide.Sell, Quantity, ct);
            }
            return;
        }

        // In a position: flatten on hold-time expiry or when the regime decays back to neutral
        // (the directional excitation the entry was predicated on has faded).
        var held = (_lastEventTime - _entryTime).TotalSeconds;
        var decayed = FilteredRegime == 0 || Math.Sign(FilteredRegime) == -Math.Sign(_position);
        if (held >= HoldSeconds || decayed)
        {
            var exit = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
            await Submit(router, exit, Math.Abs(_position), ct);
            _position = 0;
        }
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        if (_position == 0) return;
        var side = _position > 0 ? OrderSide.Sell : OrderSide.Buy;
        await Submit(router, side, Math.Abs(_position), ct);
        _position = 0;
    }

    private Task Submit(IOrderRouter router, OrderSide side, long qty, CancellationToken ct) =>
        router.PlaceOrderAsync(new OrderRequest(
            ClientOrderId: $"fof-{++_orderSeq}",
            Contract: _contract,
            Side: side,
            Type: OrderType.Market,
            Quantity: qty), ct);

    private readonly record struct TradeRec(DateTime Time, AggressorSide Side, bool Kept);
}
