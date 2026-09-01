using TradingTerminal.Core.Ml;

namespace DaxAlgo.Sdk.Quant;

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

    /// <summary>Captures the running mean/variance and sample count. The decay, clip and passthrough
    /// count are fixed hyper-parameters set at construction and so are not stored — a restore targets
    /// an instance already built with them.</summary>
    public FeatureScalerState SaveState()
    {
        var mean = new double[_mean.Length];
        var variance = new double[_variance.Length];
        Array.Copy(_mean, mean, _mean.Length);
        Array.Copy(_variance, variance, _variance.Length);
        return new FeatureScalerState(_mean.Length, _samples, mean, variance);
    }

    /// <summary>Restores state from <see cref="SaveState"/>; throws on a dimension mismatch.</summary>
    public void LoadState(FeatureScalerState state)
    {
        if (state.Dimensions != _mean.Length || state.Mean.Length != _mean.Length || state.Variance.Length != _mean.Length)
            throw new ArgumentException($"Expected {_mean.Length}-dimensional scaler state, got {state.Dimensions}.", nameof(state));
        Array.Copy(state.Mean, _mean, _mean.Length);
        Array.Copy(state.Variance, _variance, _variance.Length);
        _samples = state.Samples;
    }
}


/// <summary>
/// Online (ridge) gradient descent for <c>y = w·x</c>. Each <see cref="Update"/> takes one SGD step
/// on the squared loss with an L2 penalty: <c>w ← w + η·(e·x − ρ·w)</c>, where <c>e = y − w·x</c>.
/// First-order and O(d) per step — cheaper and higher-variance than the second-order
/// <see cref="OnlineLinearRegression"/> (RLS), and a useful alternative bias/variance profile.
///
/// <para>Assumes standardized inputs (the caller runs an <see cref="OnlineFeatureScaler"/> first), so
/// a fixed learning rate is stable. Pure C#, deterministic, single-threaded.</para>
/// </summary>
public sealed class OnlineGradientDescent : IOnlineForecaster
{
    /// <summary>Algorithm discriminator stored in <see cref="ForecasterState.Kind"/>.</summary>
    public const string ForecasterKind = "ogd";

    private readonly int _d;
    private readonly double[] _w;
    private readonly double _eta;   // learning rate
    private readonly double _l2;    // ridge penalty
    private long _samples;

    public OnlineGradientDescent(int dimensions, double learningRate = 0.05, double l2 = 1e-4)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (learningRate <= 0) throw new ArgumentOutOfRangeException(nameof(learningRate));
        if (l2 < 0) throw new ArgumentOutOfRangeException(nameof(l2));
        _d = dimensions;
        _eta = learningRate;
        _l2 = l2;
        _w = new double[_d];
    }

    public string Kind => ForecasterKind;
    public int Dimensions => _d;
    public long Samples => _samples;

    public double Predict(IReadOnlyList<double> features)
    {
        if (features.Count != _d) throw new ArgumentException($"Expected {_d} features, got {features.Count}.");
        double y = 0;
        for (var i = 0; i < _d; i++) y += _w[i] * features[i];
        return y;
    }

    public void Update(IReadOnlyList<double> features, double target)
    {
        if (features.Count != _d) throw new ArgumentException($"Expected {_d} features, got {features.Count}.");
        var error = target - Predict(features);
        for (var i = 0; i < _d; i++) _w[i] += _eta * (error * features[i] - _l2 * _w[i]);
        _samples++;
    }

    public ForecasterState SaveState()
    {
        var w = new double[_d];
        Array.Copy(_w, w, _d);
        return new ForecasterState(ForecasterKind, _d, _samples, w, Array.Empty<double>());
    }

    public void LoadState(ForecasterState state)
    {
        if (state.Kind != ForecasterKind)
            throw new ArgumentException($"Expected '{ForecasterKind}' state, got '{state.Kind}'.", nameof(state));
        if (state.Dimensions != _d || state.Coefficients.Length != _d)
            throw new ArgumentException($"Expected {_d}-dimensional state, got {state.Dimensions}.", nameof(state));
        Array.Copy(state.Coefficients, _w, _d);
        _samples = state.Samples;
    }
}

/// <summary>
/// Recursive least squares (RLS) with exponential forgetting. Fits a linear model
/// <c>y = β·x</c> incrementally — each <see cref="Update"/> revises the coefficient
/// vector in O(d²) time without storing past samples. The forgetting factor
/// <see cref="Lambda"/> ∈ (0, 1] down-weights older observations; 1.0 = classical OLS,
/// 0.99 = the canonical "slowly adapt" choice in HFT alpha papers
/// (Aldridge 2013, "High-Frequency Trading").
///
/// Why RLS in HFT: market regimes shift on hourly-to-daily timescales. A model fit once
/// on yesterday and frozen overfits to old structure; a model that retrains from scratch
/// every tick wastes information. RLS with λ ≈ 0.99 occupies the middle ground that
/// works in practice — fast adaptation, no full re-fit, bounded state.
///
/// Pure C#, no NuGet adds. Stateful, single-threaded.
/// </summary>
public sealed class OnlineLinearRegression : IOnlineForecaster
{
    /// <summary>Algorithm discriminator stored in <see cref="ForecasterState.Kind"/>.</summary>
    public const string ForecasterKind = "rls";

    private readonly int _d;
    private readonly double[] _beta;
    private readonly double[,] _p;   // d × d inverse-covariance proxy
    private readonly double[] _scratchPX;
    private long _samples;

    public OnlineLinearRegression(int dimensions, double lambda = 0.99, double initialDiagonal = 1e3)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (lambda <= 0 || lambda > 1) throw new ArgumentOutOfRangeException(nameof(lambda));
        _d = dimensions;
        Lambda = lambda;
        _beta = new double[_d];
        _p = new double[_d, _d];
        for (var i = 0; i < _d; i++) _p[i, i] = initialDiagonal;
        _scratchPX = new double[_d];
    }

    public string Kind => ForecasterKind;
    public int Dimensions => _d;
    public double Lambda { get; }
    public long Samples => _samples;
    public IReadOnlyList<double> Coefficients => _beta;

    /// <summary>Predict y given features. Length of <paramref name="features"/> must match Dimensions.</summary>
    public double Predict(IReadOnlyList<double> features)
    {
        if (features.Count != _d) throw new ArgumentException($"Expected {_d} features, got {features.Count}.");
        double y = 0;
        for (var i = 0; i < _d; i++) y += _beta[i] * features[i];
        return y;
    }

    /// <summary>Apply one observation. Updates β and the P matrix in place.</summary>
    public void Update(IReadOnlyList<double> features, double y)
    {
        if (features.Count != _d) throw new ArgumentException($"Expected {_d} features, got {features.Count}.");

        // px = P · x   ;   denom = λ + xᵀ·P·x
        double denom = Lambda;
        for (var i = 0; i < _d; i++)
        {
            double s = 0;
            for (var j = 0; j < _d; j++) s += _p[i, j] * features[j];
            _scratchPX[i] = s;
            denom += features[i] * s;
        }
        if (denom == 0) return;

        // Innovation: e = y - xᵀ·β
        double pred = Predict(features);
        double e = y - pred;
        double scale = e / denom;

        // β ← β + (P x / denom) · e
        for (var i = 0; i < _d; i++) _beta[i] += _scratchPX[i] * scale;

        // P ← (P - (P x xᵀ P) / denom) / λ
        for (var i = 0; i < _d; i++)
            for (var j = 0; j < _d; j++)
                _p[i, j] = (_p[i, j] - _scratchPX[i] * _scratchPX[j] / denom) / Lambda;

        _samples++;
    }

    /// <summary>Captures β, the P matrix (row-major) and the sample count into a snapshot. The
    /// forgetting factor is a fixed hyper-parameter set at construction, so it is not stored — a
    /// restore targets an instance already built with the intended <see cref="Lambda"/>.</summary>
    public ForecasterState SaveState()
    {
        var beta = new double[_d];
        Array.Copy(_beta, beta, _d);
        var cov = new double[_d * _d];
        for (var i = 0; i < _d; i++)
            for (var j = 0; j < _d; j++)
                cov[i * _d + j] = _p[i, j];
        return new ForecasterState(ForecasterKind, _d, _samples, beta, cov);
    }

    /// <summary>Restores state from <see cref="SaveState"/>. The snapshot must be an RLS state of
    /// matching dimension (β length d, covariance length d²); anything else throws so a corrupt or
    /// mismatched artifact fails loudly rather than training from a garbled prior.</summary>
    public void LoadState(ForecasterState state)
    {
        if (state.Kind != ForecasterKind)
            throw new ArgumentException($"Expected '{ForecasterKind}' state, got '{state.Kind}'.", nameof(state));
        if (state.Dimensions != _d || state.Coefficients.Length != _d)
            throw new ArgumentException($"Expected {_d}-dimensional state, got {state.Dimensions}.", nameof(state));
        if (state.Covariance.Length != _d * _d)
            throw new ArgumentException($"Expected a {_d}×{_d} covariance ({_d * _d} entries), got {state.Covariance.Length}.", nameof(state));

        Array.Copy(state.Coefficients, _beta, _d);
        for (var i = 0; i < _d; i++)
            for (var j = 0; j < _d; j++)
                _p[i, j] = state.Covariance[i * _d + j];
        _samples = state.Samples;
    }
}

/// <summary>
/// Online logistic regression for a binary target: <c>P(y=1) = σ(w·x)</c>, updated by one L2-penalized
/// SGD step on the log-loss (<c>w ← w + η·(e·x − ρ·w)</c>, <c>e = y − σ(w·x)</c>). Unlike a linear
/// probability model it cannot leave [0, 1] and calibrates better near the extremes — the right fit
/// for the order book's spread-widen / depth-drain / sweep-jump event heads. Not meaningful for the
/// unbounded direction heads (σ squashes the output to a probability).
///
/// <para>Assumes standardized inputs. Pure C#, deterministic, single-threaded.</para>
/// </summary>
public sealed class OnlineLogisticRegression : IOnlineForecaster
{
    /// <summary>Algorithm discriminator stored in <see cref="ForecasterState.Kind"/>.</summary>
    public const string ForecasterKind = "logistic";

    private readonly int _d;
    private readonly double[] _w;
    private readonly double _eta;
    private readonly double _l2;
    private long _samples;

    public OnlineLogisticRegression(int dimensions, double learningRate = 0.1, double l2 = 1e-4)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (learningRate <= 0) throw new ArgumentOutOfRangeException(nameof(learningRate));
        if (l2 < 0) throw new ArgumentOutOfRangeException(nameof(l2));
        _d = dimensions;
        _eta = learningRate;
        _l2 = l2;
        _w = new double[_d];
    }

    public string Kind => ForecasterKind;
    public int Dimensions => _d;
    public long Samples => _samples;

    /// <summary>The predicted probability P(y=1) ∈ (0, 1).</summary>
    public double Predict(IReadOnlyList<double> features)
    {
        if (features.Count != _d) throw new ArgumentException($"Expected {_d} features, got {features.Count}.");
        double z = 0;
        for (var i = 0; i < _d; i++) z += _w[i] * features[i];
        return Sigmoid(z);
    }

    public void Update(IReadOnlyList<double> features, double target)
    {
        if (features.Count != _d) throw new ArgumentException($"Expected {_d} features, got {features.Count}.");
        var error = target - Predict(features);
        for (var i = 0; i < _d; i++) _w[i] += _eta * (error * features[i] - _l2 * _w[i]);
        _samples++;
    }

    private static double Sigmoid(double z) => 1.0 / (1.0 + Math.Exp(-Math.Clamp(z, -30.0, 30.0)));

    public ForecasterState SaveState()
    {
        var w = new double[_d];
        Array.Copy(_w, w, _d);
        return new ForecasterState(ForecasterKind, _d, _samples, w, Array.Empty<double>());
    }

    public void LoadState(ForecasterState state)
    {
        if (state.Kind != ForecasterKind)
            throw new ArgumentException($"Expected '{ForecasterKind}' state, got '{state.Kind}'.", nameof(state));
        if (state.Dimensions != _d || state.Coefficients.Length != _d)
            throw new ArgumentException($"Expected {_d}-dimensional state, got {state.Dimensions}.", nameof(state));
        Array.Copy(state.Coefficients, _w, _d);
        _samples = state.Samples;
    }
}

