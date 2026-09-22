namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// LuxAlgo-inspired confirmation / contrarian signal engine over OHLC closes.
/// Compact standalone toolkit (not a clone of the proprietary LuxAlgo script): dual-EMA
/// confirmation crosses, ATR stretch bands, and contrarian reclaim fades.
/// </summary>
public sealed class LuxStyleSignals
{
    private readonly Ema _fast;
    private readonly Ema _slow;
    private readonly Atr _atr;
    private double _prevFast;
    private double _prevSlow;
    private double _prevClose;
    private bool _ready;

    public LuxStyleSignals(int fast = 8, int slow = 21, int atrPeriod = 14)
    {
        _fast = new Ema(fast);
        _slow = new Ema(slow);
        _atr = new Atr(atrPeriod);
    }

    public enum Kind : byte
    {
        None = 0,
        ConfirmBuy = 1,
        ConfirmSell = 2,
        StrongBuy = 3,
        StrongSell = 4,
        ContraBuy = 5,
        ContraSell = 6,
    }

    public readonly record struct Signal(Kind Kind, double Price, double Trail);

    /// <summary>Folds one bar; returns a signal for this bar (may be <see cref="Kind.None"/>).</summary>
    public Signal Update(double high, double low, double close)
    {
        if (!double.IsFinite(close)) return default;

        var atr = _atr.Update(high, low, close);
        var fast = _fast.Update(close);
        var slow = _slow.Update(close);

        if (!_fast.IsReady || !_slow.IsReady || !_atr.IsReady)
        {
            _prevFast = fast;
            _prevSlow = slow;
            _prevClose = close;
            return default;
        }

        var trail = slow;
        var upper = slow + 1.5 * atr;
        var lower = slow - 1.5 * atr;
        Kind kind = Kind.None;

        if (_ready)
        {
            var bullCross = _prevFast <= _prevSlow && fast > slow;
            var bearCross = _prevFast >= _prevSlow && fast < slow;

            if (bullCross && close >= trail)
                kind = close >= upper ? Kind.StrongBuy : Kind.ConfirmBuy;
            else if (bearCross && close <= trail)
                kind = close <= lower ? Kind.StrongSell : Kind.ConfirmSell;
            else if (close < lower && close > _prevClose)
                kind = Kind.ContraBuy;
            else if (close > upper && close < _prevClose)
                kind = Kind.ContraSell;
        }

        _prevFast = fast;
        _prevSlow = slow;
        _prevClose = close;
        _ready = true;
        return new Signal(kind, close, trail);
    }
}
