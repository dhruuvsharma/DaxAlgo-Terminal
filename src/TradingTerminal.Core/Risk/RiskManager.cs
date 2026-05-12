using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Risk;

/// <summary>
/// Default <see cref="IRiskManager"/>. Tracks net position per symbol and realised PnL per
/// UTC trading day. Two caps:
///   1. <see cref="RiskOptions.MaxPositionPerSymbol"/> — rejects orders whose worst-case
///      post-fill absolute position would exceed the cap.
///   2. <see cref="RiskOptions.MaxDailyLoss"/> — once the day's realised PnL is at-or-below
///      <c>-MaxDailyLoss</c>, every new submission is rejected until UTC midnight rolls.
///
/// Fills are recorded through <see cref="RecordFill"/> at the order-router layer (not the
/// strategy), so cap accounting stays consistent regardless of who calls Submit.
///
/// Thread-affinity matches the backtest engine: single logical timeline. The internal
/// dictionaries are not safe for unsynchronised parallel writes; the live OMS will wrap
/// this with a lock when threaded order callbacks are wired up.
/// </summary>
public sealed class RiskManager : IRiskManager
{
    private readonly RiskOptions _options;
    private readonly Dictionary<string, long> _positionBySymbol = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenFills = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _avgEntryPriceBySymbol = new(StringComparer.Ordinal);
    private DateTime _currentDayUtc;
    private double _realisedPnlToday;

    public RiskManager(RiskOptions options)
    {
        _options = options;
    }

    /// <summary>Current net signed position per symbol — exposed for telemetry / tests.</summary>
    public long PositionFor(string symbol) =>
        _positionBySymbol.TryGetValue(symbol, out var p) ? p : 0;

    /// <summary>Realised PnL accumulated since the current UTC trading day began.</summary>
    public double RealisedPnlToday => _realisedPnlToday;

    public (bool Allowed, string? RejectReason) Evaluate(OrderRequest request)
    {
        if (_options.MaxDailyLoss > 0 && _realisedPnlToday <= -_options.MaxDailyLoss)
            return (false, $"Daily loss cap hit ({_realisedPnlToday:F2})");

        if (_options.MaxPositionPerSymbol > 0)
        {
            var symbol = request.Contract.Symbol;
            var current = PositionFor(symbol);
            var delta = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;
            var afterFill = current + delta;
            if (Math.Abs(afterFill) > _options.MaxPositionPerSymbol)
                return (false, $"Would exceed per-symbol cap of {_options.MaxPositionPerSymbol}");
        }

        return (true, null);
    }

    public void RecordFill(string symbol, OrderEvent fillEvent)
    {
        if (fillEvent.LastFillQuantity <= 0 || fillEvent.LastFillPrice is not { } price) return;

        var dedupKey = $"{fillEvent.ClientOrderId}|{fillEvent.FilledQuantity}|{fillEvent.LastFillQuantity}";
        if (!_seenFills.Add(dedupKey)) return;

        RollDayIfNeeded(fillEvent.TimestampUtc);

        var qty = fillEvent.LastFillQuantity;
        var signed = fillEvent.Side == OrderSide.Buy ? qty : -qty;
        var prev = PositionFor(symbol);
        var prevAvg = _avgEntryPriceBySymbol.TryGetValue(symbol, out var a) ? a : 0.0;
        var next = prev + signed;

        var (realised, newAvg) = ApplyFill(prev, prevAvg, signed, price);
        _realisedPnlToday += realised * _options.DefaultContractMultiplier;

        _positionBySymbol[symbol] = next;
        if (next == 0)
            _avgEntryPriceBySymbol.Remove(symbol);
        else
            _avgEntryPriceBySymbol[symbol] = newAvg;
    }

    private void RollDayIfNeeded(DateTime fillTimeUtc)
    {
        var day = fillTimeUtc.Date;
        if (_currentDayUtc == default) { _currentDayUtc = day; return; }
        if (day != _currentDayUtc)
        {
            _currentDayUtc = day;
            _realisedPnlToday = 0;
        }
    }

    private static (double Realised, double NewAvg) ApplyFill(long prevQty, double prevAvg, long signedFill, double price)
    {
        if (prevQty == 0) return (0, price);

        var nextQty = prevQty + signedFill;
        var sameSide = Math.Sign(prevQty) == Math.Sign(signedFill);

        if (sameSide)
        {
            var newAvg = ((prevAvg * Math.Abs(prevQty)) + (price * Math.Abs(signedFill))) / Math.Abs(nextQty);
            return (0, newAvg);
        }

        var closingQty = Math.Min(Math.Abs(prevQty), Math.Abs(signedFill));
        var realisedPerUnit = prevQty > 0 ? price - prevAvg : prevAvg - price;
        var realised = realisedPerUnit * closingQty;

        if (Math.Abs(signedFill) <= Math.Abs(prevQty))
            return (realised, prevAvg);

        return (realised, price);
    }
}
