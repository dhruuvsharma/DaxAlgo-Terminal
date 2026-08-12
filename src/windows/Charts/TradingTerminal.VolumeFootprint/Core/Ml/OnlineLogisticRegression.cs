namespace TradingTerminal.Core.Ml;

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
