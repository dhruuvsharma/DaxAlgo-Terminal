using TradingTerminal.Core.Backtesting;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Backtest.Engine.Stats;

/// <summary>
/// Captures the visual-replay backdrop while a run streams: aggregates the charted instrument's quote
/// mids into fixed-interval OHLC bars, then assembles trade markers from the finished ledger. Cost is
/// O(1) per quote and memory is bounded by (run span / interval), so it never grows unbounded — but
/// it only runs when visual recording is requested. Charts the primary instrument for portfolio runs.
/// </summary>
internal sealed class VisualRecorder
{
    private readonly InstrumentId _instrument;
    private readonly TimeSpan _interval;
    private readonly List<VisualBar> _bars = new();

    private DateTime _barStart;
    private double _open, _high, _low, _close;
    private bool _hasBar;

    public VisualRecorder(InstrumentId instrument, TimeSpan interval)
    {
        _instrument = instrument;
        _interval = interval > TimeSpan.Zero ? interval : TimeSpan.FromMinutes(1);
    }

    public void OnMid(InstrumentId instrument, DateTime ts, double mid)
    {
        if (instrument != _instrument) return;

        if (!_hasBar)
        {
            StartBar(ts, mid);
            return;
        }

        if (ts - _barStart >= _interval)
        {
            FlushBar();
            StartBar(ts, mid);
        }
        else
        {
            if (mid > _high) _high = mid;
            if (mid < _low) _low = mid;
            _close = mid;
        }
    }

    public VisualTimeline Build(IReadOnlyList<RoundTripTrade> trades)
    {
        if (_hasBar) FlushBar();

        var markers = new List<TradeMarker>(trades.Count * 2);
        foreach (var t in trades)
        {
            if (t.Instrument != _instrument) continue;
            var exitSide = t.Side == TradingTerminal.Core.Trading.OrderSide.Buy
                ? TradingTerminal.Core.Trading.OrderSide.Sell
                : TradingTerminal.Core.Trading.OrderSide.Buy;
            markers.Add(new TradeMarker(t.EntryUtc, t.EntryPrice, t.Side, IsEntry: true, _instrument));
            markers.Add(new TradeMarker(t.ExitUtc, t.ExitPrice, exitSide, IsEntry: false, _instrument));
        }

        return new VisualTimeline(_instrument, _bars, markers);
    }

    private void StartBar(DateTime ts, double mid)
    {
        _barStart = ts;
        _open = _high = _low = _close = mid;
        _hasBar = true;
    }

    private void FlushBar() => _bars.Add(new VisualBar(_barStart, _open, _high, _low, _close));
}
