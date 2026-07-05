namespace TradingTerminal.Core.Ml;

/// <summary>
/// Naive persistence baseline: predicts the exponentially-weighted mean of the target, ignoring the
/// features entirely. <c>ŷ ← ŷ + α·(y − ŷ)</c>. It is the "does the feature-driven model actually
/// beat trivial?" comparator a user can switch a bank to. For a 0/1 event target it converges to the
/// event's base rate (climatology); for a signed move it converges to the recent average drift.
///
/// <para>Satisfies <see cref="IOnlineForecaster"/> for interface uniformity — it accepts a
/// <paramref name="dimensions"/>-length vector but never reads it. Pure C#, deterministic.</para>
/// </summary>
public sealed class EwmaForecaster : IOnlineForecaster
{
    /// <summary>Algorithm discriminator stored in <see cref="ForecasterState.Kind"/>.</summary>
    public const string ForecasterKind = "ewma";

    private readonly int _d;
    private readonly double _alpha;
    private double _ewma;
    private bool _initialized;
    private long _samples;

    public EwmaForecaster(int dimensions, double alpha = 0.05)
    {
        if (dimensions <= 0) throw new ArgumentOutOfRangeException(nameof(dimensions));
        if (alpha <= 0 || alpha > 1) throw new ArgumentOutOfRangeException(nameof(alpha));
        _d = dimensions;
        _alpha = alpha;
    }

    public string Kind => ForecasterKind;
    public int Dimensions => _d;
    public long Samples => _samples;

    /// <summary>The current EWMA of the target — independent of <paramref name="features"/>.</summary>
    public double Predict(IReadOnlyList<double> features) => _ewma;

    public void Update(IReadOnlyList<double> features, double target)
    {
        if (!_initialized)
        {
            _ewma = target;
            _initialized = true;
        }
        else
        {
            _ewma += _alpha * (target - _ewma);
        }
        _samples++;
    }

    // The EWMA scalar plus its initialization flag (as 0/1) ride in the Coefficients payload; there
    // is no covariance matrix.
    public ForecasterState SaveState() =>
        new(ForecasterKind, _d, _samples, new[] { _ewma, _initialized ? 1.0 : 0.0 }, Array.Empty<double>());

    public void LoadState(ForecasterState state)
    {
        if (state.Kind != ForecasterKind)
            throw new ArgumentException($"Expected '{ForecasterKind}' state, got '{state.Kind}'.", nameof(state));
        if (state.Coefficients.Length < 1)
            throw new ArgumentException("EWMA state must carry at least the running mean.", nameof(state));
        _ewma = state.Coefficients[0];
        _initialized = state.Coefficients.Length > 1 ? state.Coefficients[1] > 0.5 : true;
        _samples = state.Samples;
    }
}
