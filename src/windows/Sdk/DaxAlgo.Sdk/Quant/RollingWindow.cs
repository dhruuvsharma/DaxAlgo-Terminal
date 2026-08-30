namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// A fixed-capacity ring of the most recent samples, with its statistics computed on demand.
///
/// <para>The buffer is the point. A window statistic written the obvious way re-sums the window on
/// every sample, which is O(n) per tick on a callback that fires hundreds of times a second; written
/// the clever way it keeps incremental sums, and a running sum of squares loses precision fast enough
/// to produce negative variances on real price series. This keeps the samples and computes over them
/// when asked — O(1) to add, O(n) per query, and queries happen per bar or per decision rather than
/// per tick.</para>
///
/// <para>Results are cached until the next <see cref="Update"/>, so reading <see cref="Mean"/> and
/// <see cref="StandardDeviation"/> in the same frame costs one pass, not two.</para>
/// </summary>
public sealed class RollingWindow
{
    private readonly double[] _buffer;
    private int _next;
    private int _count;

    private bool _statisticsValid;
    private double _sum;
    private double _mean;
    private double _variance;
    private double _minimum;
    private double _maximum;

    /// <param name="capacity">How many samples to keep. Floored at one.</param>
    public RollingWindow(int capacity)
    {
        Capacity = Math.Max(1, capacity);
        _buffer = new double[Capacity];
    }

    /// <summary>How many samples the window holds when full.</summary>
    public int Capacity { get; }

    /// <summary>How many samples it holds now.</summary>
    public int Count => _count;

    /// <summary>True once <see cref="Count"/> has reached <see cref="Capacity"/>.</summary>
    public bool IsFull => _count >= Capacity;

    /// <summary>The most recent sample, or zero when empty.</summary>
    public double Newest => _count == 0 ? 0d : this[0];

    /// <summary>The oldest sample still in the window, or zero when empty.</summary>
    public double Oldest => _count == 0 ? 0d : this[_count - 1];

    /// <summary>
    /// A sample by age: <c>[0]</c> is the newest, <c>[1]</c> the one before it.
    ///
    /// <para>Newest-first because that is how a strategy reads it — "the last close" is index zero
    /// whether the window is full or still filling, so an author never has to compute an offset from
    /// <see cref="Count"/>.</para>
    /// </summary>
    public double this[int age]
    {
        get
        {
            if (age < 0 || age >= _count) throw new ArgumentOutOfRangeException(nameof(age));
            var index = _next - 1 - age;
            if (index < 0) index += Capacity;
            return _buffer[index];
        }
    }

    /// <summary>The sum of the window.</summary>
    public double Sum
    {
        get { EnsureStatistics(); return _sum; }
    }

    /// <summary>The arithmetic mean, or zero when empty.</summary>
    public double Mean
    {
        get { EnsureStatistics(); return _mean; }
    }

    /// <summary>The sample variance (Bessel-corrected), or zero with fewer than two samples.</summary>
    public double Variance
    {
        get { EnsureStatistics(); return _variance; }
    }

    /// <summary>The sample standard deviation.</summary>
    public double StandardDeviation => Math.Sqrt(Variance);

    /// <summary>The smallest sample in the window, or zero when empty.</summary>
    public double Minimum
    {
        get { EnsureStatistics(); return _minimum; }
    }

    /// <summary>The largest sample in the window, or zero when empty.</summary>
    public double Maximum
    {
        get { EnsureStatistics(); return _maximum; }
    }

    /// <summary>The window's range — what a Donchian channel or a normalised position within the range
    /// is built from.</summary>
    public double Range => Maximum - Minimum;

    /// <summary>
    /// Adds one sample, evicting the oldest once full. Non-finite samples are ignored: one
    /// <c>NaN</c> in the buffer makes every statistic over it <c>NaN</c> for the whole window length.
    /// </summary>
    public void Update(double sample)
    {
        if (!double.IsFinite(sample)) return;

        _buffer[_next] = sample;
        _next = (_next + 1) % Capacity;
        if (_count < Capacity) _count++;
        _statisticsValid = false;
    }

    /// <summary>
    /// Where <paramref name="value"/> sits within the window, 0 at the minimum and 1 at the maximum —
    /// the stochastic-oscillator normalisation, and the honest way to compare a level against a
    /// range whose width changes with the instrument.
    /// </summary>
    public double PositionOf(double value) =>
        Num.Clamp(Num.SafeDiv(value - Minimum, Range, 0.5d), 0d, 1d);

    /// <summary>How many standard deviations <paramref name="value"/> is from the window mean.</summary>
    public double ZScoreOf(double value) => Num.ZScore(value, Mean, StandardDeviation);

    /// <summary>
    /// The value below which <paramref name="fraction"/> of the window lies, by linear interpolation
    /// between order statistics.
    ///
    /// <para>Allocates a sorted copy, so it belongs in a per-bar decision rather than a per-tick
    /// callback. A percentile is the right tool for a threshold that must hold across instruments —
    /// "wider than 90% of the spreads I have seen" transfers where "wider than two ticks" does
    /// not.</para>
    /// </summary>
    public double Quantile(double fraction)
    {
        if (_count == 0) return 0d;
        if (_count == 1) return this[0];

        var ordered = new double[_count];
        for (var i = 0; i < _count; i++) ordered[i] = this[i];
        Array.Sort(ordered);

        var position = Num.Clamp(fraction, 0d, 1d) * (_count - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper ? ordered[lower] : Num.Lerp(ordered[lower], ordered[upper], position - lower);
    }

    /// <summary>The median — <see cref="Quantile"/> at 0.5, named because that is what it is used for.</summary>
    public double Median() => Quantile(0.5d);

    /// <summary>Empties the window.</summary>
    public void Reset()
    {
        Array.Clear(_buffer);
        _next = 0;
        _count = 0;
        _statisticsValid = false;
    }

    private void EnsureStatistics()
    {
        if (_statisticsValid) return;
        _statisticsValid = true;

        if (_count == 0)
        {
            _sum = _mean = _variance = _minimum = _maximum = 0d;
            return;
        }

        var sum = 0d;
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        for (var i = 0; i < _count; i++)
        {
            var value = this[i];
            sum += value;
            if (value < minimum) minimum = value;
            if (value > maximum) maximum = value;
        }

        _sum = sum;
        _mean = sum / _count;
        _minimum = minimum;
        _maximum = maximum;

        if (_count < 2)
        {
            _variance = 0d;
            return;
        }

        // Two-pass, against the mean just computed. The one-pass sum-of-squares form is what produces
        // the negative variances that show up as NaN standard deviations on a high-priced instrument.
        var squares = 0d;
        for (var i = 0; i < _count; i++)
        {
            var deviation = this[i] - _mean;
            squares += deviation * deviation;
        }

        _variance = squares / (_count - 1);
    }
}
