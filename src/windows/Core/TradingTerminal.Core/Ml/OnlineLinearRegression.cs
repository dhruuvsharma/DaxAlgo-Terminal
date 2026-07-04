namespace TradingTerminal.Core.Ml;

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
