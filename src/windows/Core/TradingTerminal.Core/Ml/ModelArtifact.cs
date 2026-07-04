using System.Text.Json.Serialization;

namespace TradingTerminal.Core.Ml;

/// <summary>
/// The serializable, versioned, self-describing unit a trained chart/strategy model is checkpointed
/// into — the keystone that lets a model survive an app restart, be browsed in the model registry,
/// reused across charts and strategies, and (later) published/imported. It carries not just the
/// learned weights but the <see cref="FeatureContract"/> (so a consumer knows exactly which feature
/// vector the weights expect), the tuning that produced it, its out-of-sample scoreboard
/// (<see cref="ModelMetrics"/> — the "model card" numbers), and provenance. Everything here is a
/// plain record so it round-trips through System.Text.Json unchanged.
///
/// <para>What is NOT stored: the transient learning harness (history rings, pending forecasts). Those
/// re-fill from live/stored data within a few bars; only the state that took time to learn — the
/// per-learner weights, the feature standardizer, and the running EWMAs — is persisted. That keeps
/// artifacts small (tens of KB) and portable.</para>
/// </summary>
/// <param name="SchemaVersion">On-disk format version. Bump <see cref="CurrentSchemaVersion"/> and
/// gate on it when the shape changes so old artifacts are rejected rather than misread.</param>
/// <param name="ModelKind">Which forecaster family produced this — e.g. <c>footprint-nextbar</c> or
/// <c>orderbook-micro</c>. A consumer must only load an artifact whose kind it understands.</param>
/// <param name="Algorithm">The online-learner kind behind the weights (e.g. <c>rls</c>), matching
/// <see cref="IOnlineForecaster.Kind"/>. Lets the registry key by algorithm and lets a restore refuse
/// weights from an incompatible learner.</param>
/// <param name="InstrumentKey">Canonical instrument identity as a portable string
/// (<c>InstrumentId.ToString()</c>) — deliberately a string, not the struct, so the on-disk format
/// doesn't couple to the domain type and artifacts stay shareable across machines.</param>
/// <param name="Timeframe">The bar interval / step cadence the model was trained at (e.g. <c>1m</c>,
/// <c>250ms</c>). Part of the registry key: a model is scoped to one instrument × one timeframe.</param>
/// <param name="Features">The exact ordered feature contract the weights were fit against.</param>
/// <param name="OptionsJson">The producer's tuning record, JSON-serialized — informational (the model
/// card) and a coarse compatibility hint; restores validate against the concrete bank shape rather
/// than re-parsing this, so it can never brick a load.</param>
/// <param name="Banks">The learned weights, grouped into named banks (e.g. a single <c>nextbar</c>
/// bank, or separate <c>direction</c> and <c>event</c> banks). Order within a bank is defined by the
/// producing predictor and mirrored on restore.</param>
/// <param name="Scaler">The online feature standardizer's state (μ/σ² per dimension).</param>
/// <param name="Scalars">Named running scalars the predictor needs to resume — EWMAs, tick-size
/// estimate, etc. A flat name/value bag keeps the schema stable as predictors add or drop scalars.</param>
/// <param name="Metrics">The rolling out-of-sample scoreboard at checkpoint time.</param>
/// <param name="SamplesTrained">Total observations folded into the model when it was captured.</param>
/// <param name="TrainedThroughUtc">Event time of the last bar/step the model had seen — the far edge
/// of its training window, which a model card must disclose (fits decay past it).</param>
/// <param name="CreatedUtc">When this artifact was written.</param>
public sealed record ModelArtifact(
    int SchemaVersion,
    string ModelKind,
    string Algorithm,
    string InstrumentKey,
    string Timeframe,
    FeatureContract Features,
    string OptionsJson,
    IReadOnlyList<BankState> Banks,
    FeatureScalerState Scaler,
    IReadOnlyList<ScalarState> Scalars,
    ModelMetrics Metrics,
    long SamplesTrained,
    DateTime TrainedThroughUtc,
    DateTime CreatedUtc)
{
    /// <summary>Current on-disk schema version. Increment on any breaking shape change.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The registry coordinate this artifact is filed under (kind × instrument × timeframe ×
    /// algorithm). Version is assigned by the registry on save, not carried here. Not serialized — it
    /// is derived from the fields above.</summary>
    [JsonIgnore]
    public ModelKey Key => new(ModelKind, InstrumentKey, Timeframe, Algorithm);

    /// <summary>Convenience: the named bank, or null when absent.</summary>
    public BankState? Bank(string name)
    {
        foreach (var b in Banks)
            if (string.Equals(b.Name, name, StringComparison.Ordinal)) return b;
        return null;
    }

    /// <summary>Reads a named scalar, or <paramref name="fallback"/> when absent.</summary>
    public double Scalar(string name, double fallback = 0.0)
    {
        foreach (var s in Scalars)
            if (string.Equals(s.Name, name, StringComparison.Ordinal)) return s.Value;
        return fallback;
    }
}

/// <summary>The ordered feature vector a set of weights was fit against — the contract a consumer
/// must reproduce exactly to reuse the model. <see cref="Names"/> doubles as documentation for the
/// (future) input-selection UI. <see cref="ComputeHash"/> gives a stable short fingerprint so the
/// registry can key models by feature set and a restore can reject a contract mismatch outright.</summary>
public sealed record FeatureContract(int Dimension, IReadOnlyList<string> Names)
{
    /// <summary>Deterministic 64-bit FNV-1a fingerprint of the ordered feature names + dimension,
    /// as lowercase hex. Non-cryptographic by design — this is an identity/compatibility key, not a
    /// security digest — so it stays dependency-free and cheap.</summary>
    public string ComputeHash()
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        void Fold(string s)
        {
            foreach (var ch in s)
            {
                hash ^= ch;
                hash *= prime;
            }
            hash ^= (byte)'|';
            hash *= prime;
        }
        Fold(Dimension.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var n in Names) Fold(n);
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>One named bank of same-shaped online learners (e.g. one learner per target × horizon).
/// The producing predictor owns the ordering and mirrors it on restore.</summary>
public sealed record BankState(string Name, IReadOnlyList<ForecasterState> Learners);

/// <summary>A named running scalar (EWMA, tick estimate, initialization flag as 0/1, …) captured so
/// the predictor can resume exactly. A flat bag rather than bespoke fields keeps the artifact schema
/// stable as predictors evolve their internal scalars.</summary>
public readonly record struct ScalarState(string Name, double Value);

/// <summary>The rolling out-of-sample scoreboard captured with a model — its "model card" numbers.
/// For direction models these are the flagship-horizon MAE (ticks) and directional hit-rate for the
/// model and its naive baseline; event calibration (Brier) rides along in
/// <see cref="ModelArtifact.Scalars"/> when the predictor tracks it.</summary>
public readonly record struct ModelMetrics(
    double MlMaeTicks,
    double MlHitRate,
    double BaselineMaeTicks,
    double BaselineHitRate,
    long ScoredCount)
{
    public static readonly ModelMetrics Empty = new(double.NaN, double.NaN, double.NaN, double.NaN, 0);
}
