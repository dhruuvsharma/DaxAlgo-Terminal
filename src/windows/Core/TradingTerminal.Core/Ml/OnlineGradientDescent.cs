namespace TradingTerminal.Core.Ml;

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
