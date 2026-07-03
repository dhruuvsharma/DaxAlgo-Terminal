namespace TradingTerminal.Core.Ml;

/// <summary>
/// Per-dimension exponentially-weighted online standardizer (Welford with decay). RLS on raw,
/// differently-scaled features is numerically fragile — one outlier bar can blow up the inverse
/// covariance — so every feature is transformed to <c>(x − μ) / √(σ² + ε)</c> and clamped to
/// ±<c>clip</c> before it reaches the learner. The first <c>passthroughDimensions</c> dimensions
/// (the bias term) are copied through untouched, because standardizing a constant would zero it.
/// Deterministic, single-threaded, pure C#.
/// </summary>
public sealed class OnlineFeatureScaler
{
    private const double Epsilon = 1e-12;

    private readonly double _alpha;
    private readonly double _clip;
    private readonly int _passthrough;
    private readonly double[] _mean;
    private readonly double[] _variance;
    private long _samples;

    public OnlineFeatureScaler(int dimensions, double halfLifeSamples = 64, double clip = 5.0, int passthroughDimensions = 1)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (halfLifeSamples <= 0) throw new ArgumentOutOfRangeException(nameof(halfLifeSamples));
        if (clip <= 0) throw new ArgumentOutOfRangeException(nameof(clip));
        if (passthroughDimensions < 0 || passthroughDimensions > dimensions)
            throw new ArgumentOutOfRangeException(nameof(passthroughDimensions));

        _alpha = 1.0 - Math.Pow(2.0, -1.0 / halfLifeSamples);
        _clip = clip;
        _passthrough = passthroughDimensions;
        _mean = new double[dimensions];
        _variance = new double[dimensions];
    }

    public int Dimensions => _mean.Length;
    public long Samples => _samples;

    /// <summary>Folds one raw feature vector into the running mean/variance estimates.</summary>
    public void Observe(IReadOnlyList<double> raw)
    {
        if (raw.Count != _mean.Length) throw new ArgumentException($"Expected {_mean.Length} features, got {raw.Count}.");

        if (_samples == 0)
        {
            for (var i = _passthrough; i < _mean.Length; i++) _mean[i] = raw[i];
        }
        else
        {
            for (var i = _passthrough; i < _mean.Length; i++)
            {
                var diff = raw[i] - _mean[i];
                var increment = _alpha * diff;
                _mean[i] += increment;
                _variance[i] = (1.0 - _alpha) * (_variance[i] + diff * increment);
            }
        }
        _samples++;
    }

    /// <summary>Standardizes <paramref name="raw"/> into <paramref name="destination"/> using the
    /// current statistics. Dimensions whose variance is still degenerate (no spread observed yet)
    /// map to 0 — a neutral input — instead of exploding against √ε.</summary>
    public void Transform(IReadOnlyList<double> raw, double[] destination)
    {
        if (raw.Count != _mean.Length) throw new ArgumentException($"Expected {_mean.Length} features, got {raw.Count}.");
        if (destination.Length != _mean.Length) throw new ArgumentException($"Destination must have length {_mean.Length}.");

        for (var i = 0; i < _passthrough; i++) destination[i] = raw[i];
        for (var i = _passthrough; i < _mean.Length; i++)
        {
            if (_samples == 0 || _variance[i] < Epsilon)
            {
                destination[i] = 0;
                continue;
            }
            var z = (raw[i] - _mean[i]) / Math.Sqrt(_variance[i] + Epsilon);
            destination[i] = Math.Clamp(z, -_clip, _clip);
        }
    }

    public void Reset()
    {
        Array.Clear(_mean);
        Array.Clear(_variance);
        _samples = 0;
    }
}
