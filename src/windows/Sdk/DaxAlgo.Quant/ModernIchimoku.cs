namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// DaxAlgo-native Modern Ichimoku: Hosoda 9/26/52/26 untouched, plus GBB-inspired layers —
/// ATR-normalised cloud thickness grades, qualified TK / Kumo signals, and flat Kijun / Senkou B
/// levels. Not a port of TradingView Pine; reimplemented from the published design contract.
/// </summary>
public sealed class ModernIchimoku
{
    public const int DefaultTenkan = 9;
    public const int DefaultKijun = 26;
    public const int DefaultSenkouB = 52;
    public const int DefaultDisplacement = 26;
    public const int DefaultAtrPeriod = 14;
    public const int DefaultThicknessWindow = 100;
    public const int DefaultFlatRun = 3;
    public const double DefaultMinCloudDistanceAtr = 0.25d;

    private readonly int _tenkan;
    private readonly int _kijun;
    private readonly int _senkouB;
    private readonly int _displacement;
    private readonly double _minCloudDistanceAtr;
    private readonly int _flatRun;

    private readonly RollingWindow _tenkanHigh;
    private readonly RollingWindow _tenkanLow;
    private readonly RollingWindow _kijunHigh;
    private readonly RollingWindow _kijunLow;
    private readonly RollingWindow _senkouBHigh;
    private readonly RollingWindow _senkouBLow;
    private readonly RollingWindow _thicknessHistory;
    private readonly Atr _atr;
    private readonly Queue<double> _spanADelay = new();
    private readonly Queue<double> _spanBDelay = new();
    private readonly RollingWindow _closeLag;

    private double _prevTenkan = double.NaN;
    private double _prevKijun = double.NaN;
    private double _prevClose = double.NaN;
    private double _flatKijunValue = double.NaN;
    private int _flatKijunCount;
    private double _flatSenkouBValue = double.NaN;
    private int _flatSenkouBCount;
    private double _activeFlatKijun = double.NaN;
    private double _activeFlatSenkouB = double.NaN;

    public ModernIchimoku(
        int tenkan = DefaultTenkan,
        int kijun = DefaultKijun,
        int senkouB = DefaultSenkouB,
        int displacement = DefaultDisplacement,
        int atrPeriod = DefaultAtrPeriod,
        int thicknessWindow = DefaultThicknessWindow,
        int flatRun = DefaultFlatRun,
        double minCloudDistanceAtr = DefaultMinCloudDistanceAtr)
    {
        _tenkan = Math.Max(1, tenkan);
        _kijun = Math.Max(1, kijun);
        _senkouB = Math.Max(1, senkouB);
        _displacement = Math.Max(0, displacement);
        _flatRun = Math.Max(1, flatRun);
        _minCloudDistanceAtr = Math.Max(0d, minCloudDistanceAtr);

        _tenkanHigh = new RollingWindow(_tenkan);
        _tenkanLow = new RollingWindow(_tenkan);
        _kijunHigh = new RollingWindow(_kijun);
        _kijunLow = new RollingWindow(_kijun);
        _senkouBHigh = new RollingWindow(_senkouB);
        _senkouBLow = new RollingWindow(_senkouB);
        _thicknessHistory = new RollingWindow(Math.Max(8, thicknessWindow));
        _atr = new Atr(Math.Max(1, atrPeriod));
        // Capacity displacement+1 → Oldest is the close from `displacement` bars ago once full.
        _closeLag = new RollingWindow(Math.Max(1, _displacement + 1));
    }

    public enum ThicknessGrade : byte
    {
        Unknown = 0,
        Thin = 1,
        Normal = 2,
        Thick = 3,
        VeryThick = 4,
    }

    public enum SignalKind : byte
    {
        None = 0,
        TkBull = 1,
        TkBear = 2,
        KumoBull = 3,
        KumoBear = 4,
    }

    public readonly record struct Snapshot(
        double Tenkan,
        double Kijun,
        double SpanA,
        double SpanB,
        double Chikou,
        double Atr,
        double ThicknessAtr,
        ThicknessGrade Grade,
        double PriceToCloudAtr,
        SignalKind RawSignal,
        bool Qualified,
        double FlatKijun,
        double FlatSenkouB);

    /// <summary>Folds one bar and returns the values to plot on this bar (spans already displaced).</summary>
    public Snapshot Update(double high, double low, double open, double close)
    {
        if (!double.IsFinite(high) || !double.IsFinite(low) || !double.IsFinite(close))
            return default;

        _tenkanHigh.Update(high);
        _tenkanLow.Update(low);
        _kijunHigh.Update(high);
        _kijunLow.Update(low);
        _senkouBHigh.Update(high);
        _senkouBLow.Update(low);
        var atr = _atr.Update(high, low, close);

        var tenkan = Midpoint(_tenkanHigh, _tenkanLow);
        var kijun = Midpoint(_kijunHigh, _kijunLow);
        var spanARaw = double.IsFinite(tenkan) && double.IsFinite(kijun) ? (tenkan + kijun) * 0.5d : double.NaN;
        var spanBRaw = Midpoint(_senkouBHigh, _senkouBLow);

        EnqueueFinite(_spanADelay, spanARaw);
        EnqueueFinite(_spanBDelay, spanBRaw);
        _closeLag.Update(close);

        var spanA = DequeueWhenReady(_spanADelay, _displacement);
        var spanB = DequeueWhenReady(_spanBDelay, _displacement);
        // Streaming snapshot: Chikou plot value is filled in batch by ModernIchimokuChart.
        // Lagged close (26 ago) is what qualification compares against.
        var laggedClose = _closeLag.IsFull ? _closeLag.Oldest : double.NaN;
        var chikou = laggedClose;

        var thicknessAtr = double.NaN;
        var grade = ThicknessGrade.Unknown;
        if (double.IsFinite(spanA) && double.IsFinite(spanB) && atr > Num.Epsilon)
        {
            thicknessAtr = Math.Abs(spanA - spanB) / atr;
            _thicknessHistory.Update(thicknessAtr);
            grade = GradeThickness(thicknessAtr);
        }

        var cloudTop = MaxFinite(spanA, spanB);
        var cloudBot = MinFinite(spanA, spanB);
        var priceToCloudAtr = double.NaN;
        if (atr > Num.Epsilon && double.IsFinite(cloudTop) && double.IsFinite(cloudBot))
        {
            if (close > cloudTop) priceToCloudAtr = (close - cloudTop) / atr;
            else if (close < cloudBot) priceToCloudAtr = (cloudBot - close) / atr;
            else priceToCloudAtr = 0d;
        }

        UpdateFlat(ref _flatKijunValue, ref _flatKijunCount, ref _activeFlatKijun, kijun);
        UpdateFlat(ref _flatSenkouBValue, ref _flatSenkouBCount, ref _activeFlatSenkouB, spanB);

        var raw = SignalKind.None;
        if (double.IsFinite(tenkan) && double.IsFinite(kijun) &&
            double.IsFinite(_prevTenkan) && double.IsFinite(_prevKijun))
        {
            if (_prevTenkan <= _prevKijun && tenkan > kijun) raw = SignalKind.TkBull;
            else if (_prevTenkan >= _prevKijun && tenkan < kijun) raw = SignalKind.TkBear;
        }

        if (raw == SignalKind.None &&
            double.IsFinite(cloudTop) && double.IsFinite(cloudBot) &&
            double.IsFinite(_prevClose) && atr > Num.Epsilon)
        {
            if (_prevClose <= cloudTop && close > cloudTop) raw = SignalKind.KumoBull;
            else if (_prevClose >= cloudBot && close < cloudBot) raw = SignalKind.KumoBear;
        }

        var qualified = false;
        if (raw != SignalKind.None && atr > Num.Epsilon)
        {
            var bullishBar = close >= open;
            var bearishBar = close <= open;
            var farEnough = !double.IsFinite(priceToCloudAtr) || priceToCloudAtr >= _minCloudDistanceAtr;
            // Chikou momentum: today's close vs price `displacement` bars ago.
            var chikouOk = !double.IsFinite(laggedClose) ||
                (raw is SignalKind.TkBull or SignalKind.KumoBull
                    ? close >= laggedClose
                    : close <= laggedClose);

            qualified = farEnough && chikouOk &&
                (raw is SignalKind.TkBull or SignalKind.KumoBull ? bullishBar : bearishBar);
        }

        _prevTenkan = tenkan;
        _prevKijun = kijun;
        _prevClose = close;

        return new Snapshot(
            tenkan, kijun, spanA, spanB, chikou, atr, thicknessAtr, grade, priceToCloudAtr,
            raw, qualified, _activeFlatKijun, _activeFlatSenkouB);
    }

    /// <summary>Batch helper: one snapshot per input bar (aligned).</summary>
    public static IReadOnlyList<Snapshot> FromBars(
        IReadOnlyList<(double High, double Low, double Open, double Close)> bars,
        int tenkan = DefaultTenkan,
        int kijun = DefaultKijun,
        int senkouB = DefaultSenkouB,
        int displacement = DefaultDisplacement)
    {
        if (bars is null || bars.Count == 0) return Array.Empty<Snapshot>();
        var eng = new ModernIchimoku(tenkan, kijun, senkouB, displacement);
        var list = new Snapshot[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            list[i] = eng.Update(b.High, b.Low, b.Open, b.Close);
        }
        return list;
    }

    private ThicknessGrade GradeThickness(double thicknessAtr)
    {
        if (!_thicknessHistory.IsFull && _thicknessHistory.Count < 8)
            return ThicknessGrade.Unknown;

        var n = _thicknessHistory.Count;
        var sorted = new double[n];
        for (var age = 0; age < n; age++)
            sorted[age] = _thicknessHistory[age];
        Array.Sort(sorted);

        var rank = 0;
        for (var i = 0; i < n; i++)
        {
            if (sorted[i] <= thicknessAtr) rank = i + 1;
            else break;
        }
        var pct = rank / (double)n;
        if (pct < 0.25d) return ThicknessGrade.Thin;
        if (pct < 0.55d) return ThicknessGrade.Normal;
        if (pct < 0.80d) return ThicknessGrade.Thick;
        return ThicknessGrade.VeryThick;
    }

    private void UpdateFlat(ref double last, ref int run, ref double active, double value)
    {
        if (!double.IsFinite(value))
        {
            last = double.NaN;
            run = 0;
            return;
        }

        if (double.IsFinite(last) && Math.Abs(value - last) < Num.Epsilon)
        {
            run++;
            if (run >= _flatRun) active = value;
        }
        else
        {
            last = value;
            run = 1;
            // Keep active until replaced by a new flat run; age-out is left to the consumer.
        }
    }

    private static double Midpoint(RollingWindow highs, RollingWindow lows)
    {
        if (!highs.IsFull || !lows.IsFull) return double.NaN;
        return (highs.Maximum + lows.Minimum) * 0.5d;
    }

    private static void EnqueueFinite(Queue<double> queue, double value) =>
        queue.Enqueue(double.IsFinite(value) ? value : double.NaN);

    private static double DequeueWhenReady(Queue<double> queue, int delay)
    {
        if (delay <= 0)
            return queue.Count > 0 ? queue.ToArray()[^1] : double.NaN;
        if (queue.Count <= delay) return double.NaN;
        return queue.Dequeue();
    }

    private static double MaxFinite(double a, double b)
    {
        if (double.IsFinite(a) && double.IsFinite(b)) return Math.Max(a, b);
        if (double.IsFinite(a)) return a;
        if (double.IsFinite(b)) return b;
        return double.NaN;
    }

    private static double MinFinite(double a, double b)
    {
        if (double.IsFinite(a) && double.IsFinite(b)) return Math.Min(a, b);
        if (double.IsFinite(a)) return a;
        if (double.IsFinite(b)) return b;
        return double.NaN;
    }
}
