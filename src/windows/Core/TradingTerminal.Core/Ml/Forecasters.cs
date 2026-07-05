namespace TradingTerminal.Core.Ml;

/// <summary>
/// The selectable online-learner algorithms behind <see cref="IOnlineForecaster"/>. The predictors'
/// feature engineering, walk-forward scoring, warm-start and checkpoint machinery are all
/// algorithm-agnostic; this enum is what a window's "Model" selector switches between. The string
/// <see cref="Forecasters.Tag"/> of each kind is what lands in <see cref="ModelArtifact.Algorithm"/>
/// and the registry key, so models trained with different algorithms are versioned independently.
/// </summary>
public enum LearnerKind
{
    /// <summary>Recursive least squares with exponential forgetting — adaptive, second-order,
    /// tracks regime shifts. The default.</summary>
    Rls,

    /// <summary>Online (ridge) gradient descent — first-order, cheap, a different bias/variance
    /// trade-off than RLS.</summary>
    OnlineGradientDescent,

    /// <summary>Exponentially-weighted mean of the target, ignoring features — a naive persistence
    /// baseline to measure the feature-driven models against.</summary>
    EwmaBaseline,

    /// <summary>Online logistic regression — calibrated probabilities for the binary event heads
    /// (spread-widen / depth-drain / sweep-jump); not meaningful for the unbounded direction heads.</summary>
    Logistic,
}

/// <summary>Factory + naming for the <see cref="LearnerKind"/> family. Central so the predictors, the
/// registry keys, and the (future) UI selector all agree on the same tags and display names.</summary>
public static class Forecasters
{
    /// <summary>Builds a fresh learner of the given kind for a <paramref name="dimensions"/>-length
    /// feature vector. <paramref name="lambda"/> is the RLS forgetting factor; other learners use
    /// their own defaults and ignore it.</summary>
    public static IOnlineForecaster Create(LearnerKind kind, int dimensions, double lambda) => kind switch
    {
        LearnerKind.Rls => new OnlineLinearRegression(dimensions, lambda),
        LearnerKind.OnlineGradientDescent => new OnlineGradientDescent(dimensions),
        LearnerKind.EwmaBaseline => new EwmaForecaster(dimensions),
        LearnerKind.Logistic => new OnlineLogisticRegression(dimensions),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>The stable string tag stored in artifacts / registry keys (matches the learner's
    /// <see cref="IOnlineForecaster.Kind"/>).</summary>
    public static string Tag(LearnerKind kind) => kind switch
    {
        LearnerKind.Rls => OnlineLinearRegression.ForecasterKind,
        LearnerKind.OnlineGradientDescent => OnlineGradientDescent.ForecasterKind,
        LearnerKind.EwmaBaseline => EwmaForecaster.ForecasterKind,
        LearnerKind.Logistic => OnlineLogisticRegression.ForecasterKind,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Parses a tag back to its kind; throws on an unknown tag.</summary>
    public static LearnerKind Parse(string tag) => tag switch
    {
        OnlineLinearRegression.ForecasterKind => LearnerKind.Rls,
        OnlineGradientDescent.ForecasterKind => LearnerKind.OnlineGradientDescent,
        EwmaForecaster.ForecasterKind => LearnerKind.EwmaBaseline,
        OnlineLogisticRegression.ForecasterKind => LearnerKind.Logistic,
        _ => throw new ArgumentOutOfRangeException(nameof(tag), $"Unknown learner tag '{tag}'."),
    };

    /// <summary>Human-readable name for a selector.</summary>
    public static string DisplayName(LearnerKind kind) => kind switch
    {
        LearnerKind.Rls => "RLS (adaptive)",
        LearnerKind.OnlineGradientDescent => "Online gradient descent",
        LearnerKind.EwmaBaseline => "EWMA baseline",
        LearnerKind.Logistic => "Logistic (events)",
        _ => kind.ToString(),
    };

    /// <summary>The learners a window's "Model" selector offers for the <em>direction / regression</em>
    /// banks. Logistic is intentionally excluded — it only fits the binary event heads (where it rides
    /// on <c>EventLearner</c>), and would squash an unbounded price-move target to a probability.</summary>
    public static IReadOnlyList<LearnerOption> DirectionChoices { get; } = new[]
    {
        new LearnerOption(LearnerKind.Rls, DisplayName(LearnerKind.Rls)),
        new LearnerOption(LearnerKind.OnlineGradientDescent, DisplayName(LearnerKind.OnlineGradientDescent)),
        new LearnerOption(LearnerKind.EwmaBaseline, DisplayName(LearnerKind.EwmaBaseline)),
    };
}

/// <summary>A learner choice for a UI selector: the <see cref="LearnerKind"/> plus its display name.</summary>
public sealed record LearnerOption(LearnerKind Kind, string Name);
