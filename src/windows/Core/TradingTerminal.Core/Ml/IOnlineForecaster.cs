namespace TradingTerminal.Core.Ml;

/// <summary>
/// The pluggable online-learner seam the chart/strategy forecasters build their banks from. The
/// feature engineering, walk-forward score-then-learn harness, warm-start and metrics all live in
/// the predictors and are algorithm-agnostic; only the learner behind each (target × horizon) slot
/// varies. Today the sole implementation is <see cref="OnlineLinearRegression"/> (RLS); defining the
/// seam now lets alternative online learners (logistic, passive-aggressive, AR/EWMA baselines, …) be
/// dropped in behind a factory without touching the harness.
///
/// <para>The two serialization hooks are what make a learner checkpointable into a
/// <see cref="ModelArtifact"/>: <see cref="SaveState"/> captures everything that took data to learn,
/// and <see cref="LoadState"/> restores it into a freshly-constructed instance whose fixed
/// hyper-parameters already match. Implementations are stateful and single-threaded (the caller
/// confines each to one thread).</para>
/// </summary>
public interface IOnlineForecaster
{
    /// <summary>Discriminator identifying the algorithm — matches <see cref="ForecasterState.Kind"/>
    /// and <see cref="ModelArtifact.Algorithm"/>. A restore refuses state from a different kind.</summary>
    string Kind { get; }

    /// <summary>Feature-vector length this learner expects.</summary>
    int Dimensions { get; }

    /// <summary>Observations folded in so far — the readiness read-out.</summary>
    long Samples { get; }

    /// <summary>Predict the target from a feature vector of length <see cref="Dimensions"/>.</summary>
    double Predict(IReadOnlyList<double> features);

    /// <summary>Fold one (features, target) observation into the model, in place.</summary>
    void Update(IReadOnlyList<double> features, double target);

    /// <summary>Capture the learned state into a serializable snapshot.</summary>
    ForecasterState SaveState();

    /// <summary>Restore learned state previously produced by <see cref="SaveState"/> on a learner of
    /// the same <see cref="Kind"/> and <see cref="Dimensions"/>. Throws on a shape/kind mismatch.</summary>
    void LoadState(ForecasterState state);
}

/// <summary>
/// The serializable learned state of one <see cref="IOnlineForecaster"/>. Deliberately shaped for
/// linear online learners: a coefficient vector every such learner has, plus an optional flattened
/// covariance/second-moment matrix (RLS keeps one; plain gradient learners leave it empty). Numeric
/// buffers are plain arrays so System.Text.Json round-trips them unambiguously.
/// </summary>
/// <param name="Kind">Algorithm discriminator (must match the target learner's <see cref="IOnlineForecaster.Kind"/>).</param>
/// <param name="Dimensions">Feature-vector length the state was captured at.</param>
/// <param name="Samples">Observations folded in when captured.</param>
/// <param name="Coefficients">The weight vector β (length = Dimensions).</param>
/// <param name="Covariance">Row-major flattened d×d auxiliary matrix (RLS inverse-covariance proxy);
/// empty for learners that keep none.</param>
public sealed record ForecasterState(
    string Kind,
    int Dimensions,
    long Samples,
    double[] Coefficients,
    double[] Covariance);
