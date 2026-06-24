using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.IndexKScore;

/// <summary>
/// Per-component, stateful K-score engine. Consumes finalized OHLCV bars and produces a
/// composite directional score K ∈ [-1.5, +1.5] together with the full per-indicator
/// signal breakdown. All 15 directional indicators and the ATR/STD volatility-confidence
/// multiplier are computed in one pass per bar.
///
/// <para>The math here is intentionally self-contained — the strategy-spec wording differs
/// slightly from the existing <see cref="Indicators"/> helpers (Wilder vs SMA smoothing, etc.),
/// so we re-implement to honor the spec literally rather than depending on shared helpers
/// that would diverge over time.</para>
///
/// <para>Not thread-safe; the host VM serializes <see cref="OnBar"/> calls per component.</para>
/// </summary>
public sealed class IndexKScoreCalculator
{
    public IndexKScoreParameters Parameters { get; }

    private readonly Queue<double> _closeBuf = new();
    private readonly Queue<double> _hi = new();
    private readonly Queue<double> _lo = new();
    private readonly Queue<double> _open = new();
    private readonly Queue<long> _vol = new();
    private readonly Queue<double> _tp = new();          // typical price (HLC/3) for CCI
    private readonly Queue<double> _vwapNum = new();     // running (typical-price × volume) for VWAP (session)
    private readonly Queue<long> _vwapVol = new();

    // MACD state — Wilder/EMA on close.
    private double _emaFast;
    private double _emaSlow;
    private double _macdSignalEma;
    private double _prevHist;
    private int _emaCount;

    // SuperTrend state.
    private double _atrSuperPrev;
    private double _superLine;
    private int _superDir;   // +1 bear, -1 bull (matches spec)
    private double _prevClose;
    private bool _hasPrevClose;
    private double _stUpper, _stLower;

    // Delta / CumDelta state.
    private readonly Queue<double> _deltas = new();      // signed close-vs-open volumes
    private readonly Queue<long> _buyVols = new();
    private readonly Queue<long> _sellVols = new();
    private readonly Queue<double> _recentBias = new();  // sign(close-open) for last 3 bars (RB)

    // ATR state — Wilder smoothing on TR; ATR-reg is SMA of ATR.
    private double _atrWilder;
    private int _atrCount;
    private readonly Queue<double> _atrReg = new();

    // RSI state — Wilder smoothing of gain/loss on close diffs.
    private double _avgGain;
    private double _avgLoss;
    private int _rsiSamples;

    // VWAP-session epoch boundary.
    private DateTime _sessionDateUtc = DateTime.MinValue;

    public IndexKScoreCalculator(IndexKScoreParameters parameters)
    {
        parameters.Validate();
        Parameters = parameters;
    }

    public bool HasOutput { get; private set; }

    /// <summary>One bar's worth of computed signals plus the composite K. Returned by
    /// <see cref="OnBar"/> after each finalized bar; the host VM stores the latest snapshot
    /// per component and rebuilds the index-level aggregate.</summary>
    public readonly record struct Snapshot(
        DateTime BarTimeUtc,
        double Close,
        double KRaw,
        double KFinal,
        double Confidence,
        SignalBreakdown Breakdown,
        bool Overbought,
        bool Oversold);

    public readonly record struct SignalBreakdown(
        double Rsi,
        double Macd,
        double Cci,
        double Ma9,
        double Ma21,
        double Ma50,
        double ThreeMa,
        double Vwap,
        double SuperTrend,
        double PocPos,
        double AtrReg,
        double Trd,
        double Delta,
        double CumDelta,
        double VolBs);

    public Snapshot? OnBar(Bar bar)
    {
        var p = Parameters;

        // Push close / HLC / OHLCV buffers (cap at the longest window any indicator needs).
        var maxWin = Math.Max(p.Ma50Length, Math.Max(p.AtrRegLength, Math.Max(p.PocLookback, p.MacdSlow + p.MacdSignal)));
        maxWin = Math.Max(maxWin, Math.Max(p.TrdLength, p.DeltaLookback));
        maxWin = Math.Max(maxWin, Math.Max(p.StdLength, p.CciLength));

        _closeBuf.Enqueue(bar.Close); while (_closeBuf.Count > maxWin) _closeBuf.Dequeue();
        _hi.Enqueue(bar.High); while (_hi.Count > maxWin) _hi.Dequeue();
        _lo.Enqueue(bar.Low); while (_lo.Count > maxWin) _lo.Dequeue();
        _open.Enqueue(bar.Open); while (_open.Count > maxWin) _open.Dequeue();
        _vol.Enqueue(bar.Volume); while (_vol.Count > maxWin) _vol.Dequeue();

        var typical = (bar.High + bar.Low + bar.Close) / 3.0;
        _tp.Enqueue(typical); while (_tp.Count > maxWin) _tp.Dequeue();

        // VWAP-session — reset numerator/denominator on a new UTC day.
        var dayUtc = bar.TimestampUtc.Date;
        if (p.VwapSession && dayUtc != _sessionDateUtc)
        {
            _vwapNum.Clear(); _vwapVol.Clear();
            _sessionDateUtc = dayUtc;
        }
        _vwapNum.Enqueue(typical * bar.Volume);
        _vwapVol.Enqueue(bar.Volume);
        // No size cap on the VWAP queues if session-mode is on — they reset on the day-boundary
        // anyway. If session-mode is off, cap at maxWin to bound memory.
        if (!p.VwapSession)
        {
            while (_vwapNum.Count > maxWin) _vwapNum.Dequeue();
            while (_vwapVol.Count > maxWin) _vwapVol.Dequeue();
        }

        // True range, ATR (Wilder), ATR_Reg = SMA of ATR.
        var trueRange = _hasPrevClose
            ? Math.Max(bar.High - bar.Low,
                       Math.Max(Math.Abs(bar.High - _prevClose), Math.Abs(bar.Low - _prevClose)))
            : bar.High - bar.Low;
        if (_atrCount == 0) _atrWilder = trueRange;
        else _atrWilder = (_atrWilder * (p.AtrLength - 1) + trueRange) / p.AtrLength;
        _atrCount++;
        _atrReg.Enqueue(_atrWilder); while (_atrReg.Count > p.AtrRegLength) _atrReg.Dequeue();

        // Supertrend (factor × ATR(supertrendAtrLength)).
        var stAtr = (_atrCount == 1) ? trueRange
                  : ((_atrSuperPrev * (p.SupertrendAtrLength - 1)) + trueRange) / p.SupertrendAtrLength;
        if (!_hasPrevClose) stAtr = trueRange;
        _atrSuperPrev = stAtr;
        var hl2 = (bar.High + bar.Low) * 0.5;
        var upperBasic = hl2 + p.SupertrendFactor * stAtr;
        var lowerBasic = hl2 - p.SupertrendFactor * stAtr;
        // Standard final-band recursion.
        var finalUpper = (_stUpper == 0 || upperBasic < _stUpper || _prevClose > _stUpper) ? upperBasic : _stUpper;
        var finalLower = (_stLower == 0 || lowerBasic > _stLower || _prevClose < _stLower) ? lowerBasic : _stLower;
        _stUpper = finalUpper; _stLower = finalLower;
        if (_superDir == 0) { _superDir = bar.Close > hl2 ? -1 : +1; _superLine = _superDir == -1 ? finalLower : finalUpper; }
        else
        {
            if (_superDir == -1 && bar.Close < finalLower) { _superDir = +1; _superLine = finalUpper; }
            else if (_superDir == +1 && bar.Close > finalUpper) { _superDir = -1; _superLine = finalLower; }
            else _superLine = _superDir == -1 ? finalLower : finalUpper;
        }

        // MACD on close (EMAs + signal-line EMA).
        var alphaFast = 2.0 / (p.MacdFast + 1);
        var alphaSlow = 2.0 / (p.MacdSlow + 1);
        var alphaSig = 2.0 / (p.MacdSignal + 1);
        if (_emaCount == 0) { _emaFast = _emaSlow = bar.Close; }
        else
        {
            _emaFast = alphaFast * bar.Close + (1 - alphaFast) * _emaFast;
            _emaSlow = alphaSlow * bar.Close + (1 - alphaSlow) * _emaSlow;
        }
        var macd = _emaFast - _emaSlow;
        if (_emaCount == 0) _macdSignalEma = macd;
        else _macdSignalEma = alphaSig * macd + (1 - alphaSig) * _macdSignalEma;
        var hist = macd - _macdSignalEma;
        _emaCount++;

        // RSI — Wilder smoothing on close-to-close diffs.
        double rsi;
        if (_rsiSamples == 0) { _prevClose = bar.Close; _rsiSamples = 1; rsi = 50; }
        else
        {
            var d = bar.Close - _prevClose;
            var gain = d > 0 ? d : 0;
            var loss = d < 0 ? -d : 0;
            if (_rsiSamples == 1) { _avgGain = gain; _avgLoss = loss; }
            else
            {
                var alphaR = 1.0 / p.RsiLength;
                _avgGain = (1 - alphaR) * _avgGain + alphaR * gain;
                _avgLoss = (1 - alphaR) * _avgLoss + alphaR * loss;
            }
            _rsiSamples++;
            rsi = _avgLoss == 0 ? (_avgGain > 0 ? 100 : 50) : 100 - 100 / (1 + _avgGain / _avgLoss);
        }

        // Delta / CumDelta / Vol B/S / RB on this bar.
        var signClose = Math.Sign(bar.Close - bar.Open);
        var deltaBar = signClose > 0 ? bar.Volume : (signClose < 0 ? -bar.Volume : 0);
        _deltas.Enqueue(deltaBar); while (_deltas.Count > p.DeltaLookback) _deltas.Dequeue();
        _buyVols.Enqueue(signClose > 0 ? bar.Volume : 0); while (_buyVols.Count > p.DeltaLookback) _buyVols.Dequeue();
        _sellVols.Enqueue(signClose < 0 ? bar.Volume : 0); while (_sellVols.Count > p.DeltaLookback) _sellVols.Dequeue();
        _recentBias.Enqueue(signClose); while (_recentBias.Count > 3) _recentBias.Dequeue();

        _prevClose = bar.Close;
        _hasPrevClose = true;

        // ── Compute signals for THIS bar ──────────────────────────────────────────────
        // (Continuous signals first.)
        var sigRsi = Clamp((rsi - 50) / 50.0, -1, 1);
        var overbought = rsi > p.RsiOverbought;
        var oversold = rsi < p.RsiOversold;
        if (overbought) sigRsi = 1;
        if (oversold) sigRsi = -1;

        var sigCci = Clamp(ComputeCci() / 100.0, -1, 1);

        var trdPos = ComputeTrdPosition(bar.Close);
        var sigTrd = double.IsNaN(trdPos) ? 0 : Clamp((trdPos - 50) / 50.0, -1, 1);

        var sigDelta = NormalizeAgainstMaxAbs(deltaBar, _deltas);

        var cumDelta = SumQueue(_deltas);
        var sigCumDelta = NormalizeAgainstMaxAbs(cumDelta, _deltas);  // pseudo-history of running deltas

        long buyVol = 0; foreach (var b in _buyVols) buyVol += b;
        long sellVol = 0; foreach (var s in _sellVols) sellVol += s;
        var sigVolBs = (buyVol + sellVol) == 0 ? 0 : (double)(buyVol - sellVol) / (buyVol + sellVol);

        // Binary signals.
        var sigMacd = ClassifyMacd(hist);
        _prevHist = hist;
        var ma9 = AverageLast(_closeBuf, p.Ma9Length);
        var ma21 = AverageLast(_closeBuf, p.Ma21Length);
        var ma50 = AverageLast(_closeBuf, p.Ma50Length);
        var sigMa9 = double.IsNaN(ma9) ? 0 : (bar.Close > ma9 ? 1 : bar.Close < ma9 ? -1 : 0);
        var sigMa21 = double.IsNaN(ma21) ? 0 : (bar.Close > ma21 ? 1 : bar.Close < ma21 ? -1 : 0);
        var sigMa50 = double.IsNaN(ma50) ? 0 : (bar.Close > ma50 ? 1 : bar.Close < ma50 ? -1 : 0);

        var fast = AverageLast(_closeBuf, p.Ma3Fast);
        var mid = AverageLast(_closeBuf, p.Ma3Mid);
        var slow = AverageLast(_closeBuf, p.Ma3Slow);
        double sig3Ma;
        if (double.IsNaN(fast) || double.IsNaN(mid) || double.IsNaN(slow)) sig3Ma = 0;
        else if (fast > mid && mid > slow) sig3Ma = 1;
        else if (fast < mid && mid < slow) sig3Ma = -1;
        else sig3Ma = 0;

        var vwap = ComputeVwap();
        var sigVwap = double.IsNaN(vwap) ? 0 : (bar.Close > vwap ? 1 : bar.Close < vwap ? -1 : 0);

        var sigSuper = _superDir == -1 ? 1 : _superDir == +1 ? -1 : 0;

        var poc = ComputePoc();
        var sigPoc = double.IsNaN(poc) ? 0 : (bar.Close > poc ? 1 : bar.Close < poc ? -1 : 0);

        double atrReg = _atrReg.Count == 0 ? double.NaN : SumQueue(_atrReg) / _atrReg.Count;
        var sigAtrReg = double.IsNaN(atrReg) || atrReg == 0 ? 0 : (_atrWilder > atrReg ? 1 : -1);

        // ── Volatility-confidence multiplier (atr_ratio + std_ratio)/2, clamped to [0.5, 1.5].
        var std = ComputeStd();
        var stdAvg = ComputeStdAverage(p.StdLength); // SMA of STD over the same length (spec wording)
        var atrRatio = (double.IsNaN(atrReg) || atrReg == 0) ? 1.0 : _atrWilder / atrReg;
        var stdRatio = (double.IsNaN(stdAvg) || stdAvg == 0) ? 1.0 : std / stdAvg;
        var confidence = Clamp((atrRatio + stdRatio) / 2.0, 0.5, 1.5);

        // ── K_raw = Σ signal_i × weight_i.
        var kRaw =
              sigSuper * p.WeightSuperTrend
            + sigMacd * p.WeightMacd
            + sigRsi * p.WeightRsi
            + sigVwap * p.WeightVwap
            + sig3Ma * p.Weight3Ma
            + sigCumDelta * p.WeightCumDelta
            + sigVolBs * p.WeightVolBs
            + sigCci * p.WeightCci
            + sigMa50 * p.WeightMa50
            + sigMa21 * p.WeightMa21
            + sigPoc * p.WeightPocPos
            + sigTrd * p.WeightTrd
            + sigMa9 * p.WeightMa9
            + sigDelta * p.WeightDelta
            + sigAtrReg * p.WeightAtrReg;
        var kFinal = Clamp(kRaw * confidence, -1.5, 1.5);

        HasOutput = true;
        return new Snapshot(
            bar.TimestampUtc, bar.Close, kRaw, kFinal, confidence,
            new SignalBreakdown(sigRsi, sigMacd, sigCci, sigMa9, sigMa21, sigMa50, sig3Ma,
                                sigVwap, sigSuper, sigPoc, sigAtrReg, sigTrd, sigDelta, sigCumDelta, sigVolBs),
            overbought, oversold);
    }

    public void Reset()
    {
        _closeBuf.Clear(); _hi.Clear(); _lo.Clear(); _open.Clear(); _vol.Clear();
        _tp.Clear(); _vwapNum.Clear(); _vwapVol.Clear();
        _deltas.Clear(); _buyVols.Clear(); _sellVols.Clear(); _recentBias.Clear();
        _atrReg.Clear();
        _emaFast = _emaSlow = _macdSignalEma = _prevHist = 0;
        _emaCount = _atrCount = _rsiSamples = 0;
        _atrWilder = _atrSuperPrev = _superLine = 0;
        _superDir = 0; _stUpper = _stLower = 0;
        _avgGain = _avgLoss = 0;
        _prevClose = 0; _hasPrevClose = false;
        _sessionDateUtc = DateTime.MinValue;
        HasOutput = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;

    private double ClassifyMacd(double hist)
    {
        if (hist > 0 && hist > _prevHist) return +1;
        if (hist < 0 && hist < _prevHist) return -1;
        return 0;
    }

    private static double AverageLast(Queue<double> q, int n)
    {
        if (q.Count < n) return double.NaN;
        var sum = 0.0; var skip = q.Count - n;
        var i = 0;
        foreach (var v in q) { if (i++ >= skip) sum += v; }
        return sum / n;
    }

    private double ComputeCci()
    {
        var n = Parameters.CciLength;
        if (_tp.Count < n) return 0;
        var skip = _tp.Count - n;
        var sum = 0.0; var idx = 0;
        foreach (var t in _tp) { if (idx++ >= skip) sum += t; }
        var mean = sum / n;
        var mad = 0.0; idx = 0;
        foreach (var t in _tp) { if (idx++ >= skip) mad += Math.Abs(t - mean); }
        mad /= n;
        if (mad < 1e-12) return 0;
        var tpNow = 0.0;
        foreach (var t in _tp) tpNow = t; // last
        return (tpNow - mean) / (0.015 * mad);
    }

    private double ComputeTrdPosition(double close)
    {
        var n = Parameters.TrdLength;
        if (_hi.Count < n) return double.NaN;
        var skip = _hi.Count - n;
        var hi = double.MinValue; var lo = double.MaxValue;
        var idxH = 0;
        foreach (var h in _hi) { if (idxH++ >= skip && h > hi) hi = h; }
        var idxL = 0;
        foreach (var l in _lo) { if (idxL++ >= skip && l < lo) lo = l; }
        if (hi <= lo) return double.NaN;
        return (close - lo) / (hi - lo) * 100;
    }

    private static long SumQueue(Queue<long> q) { long s = 0; foreach (var v in q) s += v; return s; }
    private static double SumQueue(Queue<double> q) { var s = 0.0; foreach (var v in q) s += v; return s; }

    /// <summary>Normalizes a signed scalar against the running max-|v| of the supplied history;
    /// returns 0 when the history is empty or the max is zero. Used by Delta and CumDelta.</summary>
    private static double NormalizeAgainstMaxAbs(double v, Queue<double> history)
    {
        var maxAbs = 0.0;
        foreach (var x in history) { var a = Math.Abs(x); if (a > maxAbs) maxAbs = a; }
        if (maxAbs < 1e-12) return 0;
        return Clamp(v / maxAbs, -1, 1);
    }

    private double ComputeVwap()
    {
        if (_vwapVol.Count == 0) return double.NaN;
        long volSum = 0; foreach (var v in _vwapVol) volSum += v;
        if (volSum == 0) return double.NaN;
        double num = 0; foreach (var v in _vwapNum) num += v;
        return num / volSum;
    }

    /// <summary>POC ≈ HLC/3 of the highest-volume bar within the lookback window
    /// (simplified volume profile, per the spec).</summary>
    private double ComputePoc()
    {
        var n = Parameters.PocLookback;
        if (_vol.Count < 2) return double.NaN;
        var window = Math.Min(n, _vol.Count);
        var skip = _vol.Count - window;
        long bestVol = -1; int bestIdx = -1;
        var idx = 0;
        foreach (var v in _vol)
        {
            if (idx >= skip && v > bestVol) { bestVol = v; bestIdx = idx; }
            idx++;
        }
        if (bestIdx < 0) return double.NaN;
        // Fetch the corresponding HLC.
        var hi = ItemAt(_hi, bestIdx);
        var lo = ItemAt(_lo, bestIdx);
        var cl = ItemAt(_closeBuf, bestIdx);
        return (hi + lo + cl) / 3.0;
    }

    private static double ItemAt(Queue<double> q, int idx)
    {
        var i = 0;
        foreach (var v in q) { if (i++ == idx) return v; }
        return double.NaN;
    }

    private double ComputeStd()
    {
        var n = Parameters.StdLength;
        if (_closeBuf.Count < n) return double.NaN;
        var skip = _closeBuf.Count - n;
        var sum = 0.0; var idx = 0;
        foreach (var v in _closeBuf) { if (idx++ >= skip) sum += v; }
        var mean = sum / n;
        var sse = 0.0; idx = 0;
        foreach (var v in _closeBuf) { if (idx++ >= skip) { var d = v - mean; sse += d * d; } }
        return Math.Sqrt(sse / n);
    }

    /// <summary>Average STD over the last <paramref name="window"/> bars by recomputing the
    /// rolling STD at each step within the window. Cheap because windows are small (≤50).</summary>
    private double ComputeStdAverage(int window)
    {
        if (_closeBuf.Count < window * 2) return double.NaN;
        // Walk the last <window> bars worth of trailing STDs.
        var closes = new double[_closeBuf.Count];
        var i = 0; foreach (var v in _closeBuf) closes[i++] = v;
        var n = Parameters.StdLength;
        var sumStd = 0.0; var samples = 0;
        for (var t = closes.Length - window; t < closes.Length; t++)
        {
            if (t - n + 1 < 0) continue;
            var sum = 0.0;
            for (var k = t - n + 1; k <= t; k++) sum += closes[k];
            var mean = sum / n;
            var sse = 0.0;
            for (var k = t - n + 1; k <= t; k++) { var d = closes[k] - mean; sse += d * d; }
            sumStd += Math.Sqrt(sse / n); samples++;
        }
        if (samples == 0) return double.NaN;
        return sumStd / samples;
    }
}
