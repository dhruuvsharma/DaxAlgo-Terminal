namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Stateful streaming indicators. Each holds an internal buffer and updates on a single
/// price input via <c>Push</c>; <c>IsReady</c> becomes true once the buffer is full
/// (or the warmup is complete for recursive estimators). Used by the textbook strategies
/// (Bollinger, MA-cross, RSI, MACD, ATR-based stops, etc.) so the math lives in one place.
///
/// Allocation-light: each indicator allocates one buffer at construction. None of these are
/// thread-safe — strategy code runs single-threaded under the engine, so locks would just
/// add overhead.
/// </summary>
public static class Indicators
{
    public sealed class SimpleMovingAverage
    {
        private readonly Queue<double> _buf;
        private double _sum;
        public int Period { get; }
        public SimpleMovingAverage(int period) { Period = period; _buf = new Queue<double>(period); }
        public bool IsReady => _buf.Count == Period;
        public double Value => _buf.Count == 0 ? 0 : _sum / _buf.Count;
        public void Push(double v)
        {
            _buf.Enqueue(v); _sum += v;
            while (_buf.Count > Period) _sum -= _buf.Dequeue();
        }
    }

    public sealed class RollingStdev
    {
        private readonly Queue<double> _buf;
        private double _sum;
        private double _sumSq;
        public int Period { get; }
        public RollingStdev(int period) { Period = period; _buf = new Queue<double>(period); }
        public bool IsReady => _buf.Count == Period;
        public double Mean => _buf.Count == 0 ? 0 : _sum / _buf.Count;
        public double Value
        {
            get
            {
                if (_buf.Count < 2) return 0;
                var n = _buf.Count;
                var mean = _sum / n;
                var variance = (_sumSq - n * mean * mean) / (n - 1);
                return variance > 0 ? Math.Sqrt(variance) : 0;
            }
        }
        public void Push(double v)
        {
            _buf.Enqueue(v); _sum += v; _sumSq += v * v;
            while (_buf.Count > Period)
            {
                var old = _buf.Dequeue();
                _sum -= old; _sumSq -= old * old;
            }
        }
    }

    /// <summary>EMA: y_t = α·x_t + (1-α)·y_{t-1}, with α = 2 / (period + 1).</summary>
    public sealed class ExponentialMovingAverage
    {
        public int Period { get; }
        public double Alpha { get; }
        public double Value { get; private set; }
        public bool IsReady => _count >= Period;
        private int _count;
        public ExponentialMovingAverage(int period)
        {
            Period = period;
            Alpha = 2.0 / (period + 1);
        }
        public void Push(double v)
        {
            if (_count == 0) Value = v;
            else Value = Alpha * v + (1 - Alpha) * Value;
            _count++;
        }
    }

    /// <summary>
    /// Wilder's RSI on price changes. Uses Wilder smoothing (α = 1/period) of gains/losses.
    /// Returns 0..100; values ≤ 30 are "oversold", ≥ 70 are "overbought" by convention.
    /// </summary>
    public sealed class RelativeStrengthIndex
    {
        public int Period { get; }
        private double _avgGain;
        private double _avgLoss;
        private double _prev;
        private int _samples;
        public RelativeStrengthIndex(int period) { Period = period; }
        public bool IsReady => _samples > Period;
        public double Value
        {
            get
            {
                if (_avgLoss == 0) return _avgGain > 0 ? 100 : 50;
                var rs = _avgGain / _avgLoss;
                return 100 - 100 / (1 + rs);
            }
        }
        public void Push(double v)
        {
            if (_samples == 0) { _prev = v; _samples = 1; return; }
            var delta = v - _prev;
            var gain = delta > 0 ? delta : 0;
            var loss = delta < 0 ? -delta : 0;
            if (_samples == 1)
            {
                _avgGain = gain; _avgLoss = loss;
            }
            else
            {
                var alpha = 1.0 / Period;
                _avgGain = (1 - alpha) * _avgGain + alpha * gain;
                _avgLoss = (1 - alpha) * _avgLoss + alpha * loss;
            }
            _prev = v;
            _samples++;
        }
    }

    /// <summary>
    /// Tick-level ATR proxy: rolling mean of |price_t - price_{t-1}|. True ATR uses
    /// daily high/low/close ranges; this is the tick analog.
    /// </summary>
    public sealed class AverageTrueRange
    {
        private readonly SimpleMovingAverage _sma;
        private double _prev;
        private bool _seeded;
        public int Period => _sma.Period;
        public bool IsReady => _sma.IsReady;
        public double Value => _sma.Value;
        public AverageTrueRange(int period) { _sma = new SimpleMovingAverage(period); }
        public void Push(double v)
        {
            if (!_seeded) { _prev = v; _seeded = true; return; }
            _sma.Push(Math.Abs(v - _prev));
            _prev = v;
        }
    }
}
