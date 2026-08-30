namespace DaxAlgo.Sdk.Quant;

/// <summary>
/// Ordinary least squares over a rolling window: the slope, the intercept, and how much of the
/// variation the fit actually explains.
///
/// <para>Windowed rather than cumulative because every use here is about a relationship that changes
/// — a hedge ratio, a beta, a price-impact coefficient. A regression over all of history reports the
/// average of every regime it has lived through, which is a number that describes no particular
/// moment.</para>
///
/// <para><see cref="RSquared"/> is not decoration. A slope fitted to points with no relationship is
/// still a slope, and it is confidently wrong; gating on the fit quality is the difference between a
/// hedge ratio and a random number with two decimal places.</para>
/// </summary>
public sealed class OnlineRegression
{
    private readonly RollingWindow _x;
    private readonly RollingWindow _y;

    private bool _fitValid;
    private double _slope;
    private double _intercept;
    private double _correlation;
    private double _rSquared;
    private double _standardError;
    private double _slopeStandardError;

    /// <param name="period">How many observation pairs to fit over.</param>
    public OnlineRegression(int period = 60)
    {
        _x = new RollingWindow(period);
        _y = new RollingWindow(period);
    }

    /// <summary>The window length in observation pairs.</summary>
    public int Period => _x.Capacity;

    /// <summary>Pairs currently in the window.</summary>
    public int Count => _x.Count;

    /// <summary>True once the window is full. A two-point regression fits perfectly and means
    /// nothing, so the fit quality cannot be used as the warm-up gate.</summary>
    public bool IsReady => _x.IsFull;

    /// <summary>The fitted slope — the hedge ratio, the beta, or Kyle's lambda depending on what was
    /// fed in.</summary>
    public double Slope
    {
        get { EnsureFit(); return _slope; }
    }

    /// <summary>The fitted intercept.</summary>
    public double Intercept
    {
        get { EnsureFit(); return _intercept; }
    }

    /// <summary>Pearson correlation between the two series, in [-1, 1].</summary>
    public double Correlation
    {
        get { EnsureFit(); return _correlation; }
    }

    /// <summary>The share of y's variance the fit explains, in [0, 1].</summary>
    public double RSquared
    {
        get { EnsureFit(); return _rSquared; }
    }

    /// <summary>The residual standard deviation — the natural unit for "how far from the fit is far",
    /// and what a pairs spread's z-score should be measured in.</summary>
    public double StandardError
    {
        get { EnsureFit(); return _standardError; }
    }

    /// <summary>
    /// The standard error of <see cref="Slope"/> itself — how precisely the slope is known.
    ///
    /// <para>Distinct from <see cref="StandardError"/>, which is the spread of the points about the
    /// line. This is what turns a slope into a claim: <c>Slope / SlopeStandardError</c> is the
    /// t-statistic, and a beta of 0.4 that could equally have been 0.0 is not a beta of 0.4.</para>
    /// </summary>
    public double SlopeStandardError
    {
        get { EnsureFit(); return _slopeStandardError; }
    }

    /// <summary>The slope over its own standard error. Conventionally, above two in magnitude is a
    /// slope worth believing — though see <see cref="OrnsteinUhlenbeck"/> for a case where the
    /// conventional threshold is the wrong one.</summary>
    public double SlopeTStatistic => Num.SafeDiv(Slope, SlopeStandardError);

    /// <summary>Adds one observation pair.</summary>
    public void Update(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return;

        _x.Update(x);
        _y.Update(y);
        _fitValid = false;
    }

    /// <summary>What the fit predicts for <paramref name="x"/>.</summary>
    public double Predict(double x) => Intercept + (Slope * x);

    /// <summary>How far an observation sits from the fit, in residual standard deviations — the
    /// entry signal for a pairs or basis trade.</summary>
    public double ResidualZScore(double x, double y) => Num.SafeDiv(y - Predict(x), StandardError);

    /// <summary>Empties the window.</summary>
    public void Reset()
    {
        _x.Reset();
        _y.Reset();
        _fitValid = false;
    }

    private void EnsureFit()
    {
        if (_fitValid) return;
        _fitValid = true;

        var n = _x.Count;
        if (n < 2)
        {
            _slope = _intercept = _correlation = _rSquared = _standardError = _slopeStandardError = 0d;
            return;
        }

        var meanX = _x.Mean;
        var meanY = _y.Mean;

        var covariance = 0d;
        var varianceX = 0d;
        var varianceY = 0d;
        for (var i = 0; i < n; i++)
        {
            var dx = _x[i] - meanX;
            var dy = _y[i] - meanY;
            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        _slope = Num.SafeDiv(covariance, varianceX);
        _intercept = meanY - (_slope * meanX);
        _correlation = Num.Clamp(Num.SafeDiv(covariance, Math.Sqrt(varianceX * varianceY)), -1d, 1d);
        _rSquared = _correlation * _correlation;

        // Residual variance carries two fitted parameters, so the divisor is n - 2. With three points
        // and n - 1 the standard error comes out flatteringly small on exactly the samples too short
        // to trust.
        if (n < 3)
        {
            _standardError = 0d;
            _slopeStandardError = 0d;
            return;
        }

        var residuals = 0d;
        for (var i = 0; i < n; i++)
        {
            var residual = _y[i] - (_intercept + (_slope * _x[i]));
            residuals += residual * residual;
        }

        _standardError = Math.Sqrt(Math.Max(0d, residuals / (n - 2)));
        _slopeStandardError = Num.SafeDiv(_standardError, Math.Sqrt(varianceX));
    }
}

/// <summary>
/// Rolling correlation between two series, for the cases where the slope is not wanted and only the
/// co-movement is.
///
/// <para>A thin wrapper over <see cref="OnlineRegression"/> rather than a second implementation of
/// the same sums, so a correlation and a beta computed over the same window cannot disagree.</para>
/// </summary>
public sealed class RollingCorrelation : IEstimator
{
    private readonly OnlineRegression _fit;

    /// <param name="period">The window length in observation pairs.</param>
    public RollingCorrelation(int period = 60) => _fit = new OnlineRegression(period);

    /// <summary>The window length.</summary>
    public int Period => _fit.Period;

    /// <inheritdoc/>
    public double Value => _fit.Correlation;

    /// <inheritdoc/>
    public bool IsReady => _fit.IsReady;

    /// <summary>The slope of y on x over the same window — a beta, when x is the benchmark.</summary>
    public double Beta => _fit.Slope;

    /// <summary>Adds one observation pair and returns the correlation.</summary>
    public double Update(double x, double y)
    {
        _fit.Update(x, y);
        return _fit.Correlation;
    }

    /// <inheritdoc/>
    public void Reset() => _fit.Reset();
}

/// <summary>
/// An Ornstein-Uhlenbeck fit, reported as the number that decides whether the trade is worth taking:
/// the half-life — and gated by a test that a random walk actually fails.
///
/// <para>The process is <c>dX = θ(μ − X)dt + σdW</c>. It is fitted in Dickey-Fuller form, regressing
/// each <b>change</b> on the level that preceded it: <c>ΔX = a + γ·X + ε</c>, where
/// <c>γ = φ − 1</c>. Same fit as regressing the level on its own lag, arranged so the quantity being
/// tested is the coefficient itself.</para>
///
/// <para><b>Why not R².</b> The obvious gate — "φ is below one and the regression explains
/// something" — is not merely weak, it is backwards. For a random walk the best predictor of the next
/// level is the current one, so the lagged-level regression has an R² near 1; for a fast-reverting
/// series with <c>φ = 0.5</c> it is about 0.25. Ranking by fit quality therefore prefers exactly the
/// series that must be rejected. And least squares is biased downward under a unit root
/// (<c>E[φ̂] ≈ 1 − 5.3/T</c>), so φ̂ &lt; 1 is the <i>expected</i> reading on a walk rather than
/// evidence against one.</para>
///
/// <para>So the gate is the Dickey-Fuller statistic <c>γ̂ / SE(γ̂)</c> against
/// <see cref="CriticalValue"/>, which is the standard 5% critical value for this regression and is
/// nowhere near the ±2 a t-statistic is usually read at — the null distribution is not Student's t.
/// The failure it prevents is a strategy that adds to a loser forever, on the belief that a walk must
/// come back.</para>
/// </summary>
public sealed class OrnsteinUhlenbeck
{
    /// <summary>The 5% Dickey-Fuller critical value for a regression with a constant and no trend.
    /// A statistic more negative than this rejects the random walk. Not −2: the null distribution of
    /// this statistic is not Student's t, and reading it as though it were passes roughly one walk in
    /// three.</summary>
    public const double CriticalValue = -2.86d;

    private readonly OnlineRegression _fit;
    private double _previous;
    private bool _seeded;

    /// <param name="period">How many transitions to fit over.</param>
    public OrnsteinUhlenbeck(int period = 120) => _fit = new OnlineRegression(period);

    /// <summary>The window length in transitions.</summary>
    public int Period => _fit.Period;

    /// <summary>True once the window is full.</summary>
    public bool IsReady => _fit.IsReady;

    /// <summary>The autoregressive coefficient, <c>1 + γ</c>. Below one is reversion — but see
    /// <see cref="IsMeanReverting"/> for why that on its own proves nothing.</summary>
    public double Phi => 1d + _fit.Slope;

    /// <summary>The Dickey-Fuller statistic. Compare against <see cref="CriticalValue"/>, not
    /// against two.</summary>
    public double UnitRootStatistic => _fit.SlopeTStatistic;

    /// <summary>Speed of reversion per sample, <c>−ln(φ)</c>. Zero when the series is not reverting.</summary>
    public double Theta => Phi is > 0d and < 1d ? -Math.Log(Phi) : 0d;

    /// <summary>The long-run mean the process pulls toward — the level at which the expected change
    /// is zero.</summary>
    public double Mean => Num.SafeDiv(-_fit.Intercept, _fit.Slope);

    /// <summary>Samples to close half the gap to <see cref="Mean"/>, or
    /// <see cref="double.PositiveInfinity"/> when there is no reversion to time.</summary>
    public double HalfLife => Theta > 0d ? Math.Log(2d) / Theta : double.PositiveInfinity;

    /// <summary>
    /// The stationary standard deviation of the process, <c>σ_ε / √(1 − φ²)</c> — the width of the
    /// distribution the level actually wanders in.
    ///
    /// <para>This, not the residual standard deviation, is what a deviation should be measured
    /// against. The residual is the size of one step; dividing by it reports a spread sitting a
    /// normal distance from the mean as a twenty-sigma event on a slow-reverting series.</para>
    /// </summary>
    public double StationaryDeviation =>
        Phi is > 0d and < 1d
            ? Num.SafeDiv(_fit.StandardError, Math.Sqrt(1d - (Phi * Phi)))
            : 0d;

    /// <summary>True when the fit is warm, φ is inside the unit interval, and the Dickey-Fuller
    /// statistic rejects the random walk.</summary>
    public bool IsMeanReverting =>
        IsReady && Phi is > 0d and < 1d && UnitRootStatistic < CriticalValue;

    /// <summary>How far the current level sits from the long-run mean, in stationary standard
    /// deviations — the entry signal, and zero when the process is not reverting.</summary>
    public double Deviation =>
        IsMeanReverting ? Num.SafeDiv(_previous - Mean, StationaryDeviation) : 0d;

    /// <summary>Adds one observation of the spread or level being fitted.</summary>
    public void Update(double level)
    {
        if (!double.IsFinite(level)) return;

        // Change on lagged level: the Dickey-Fuller arrangement, so the tested coefficient is the
        // slope and its standard error comes straight out of the same fit.
        if (_seeded) _fit.Update(_previous, level - _previous);
        _previous = level;
        _seeded = true;
    }

    /// <summary>Empties the fit.</summary>
    public void Reset()
    {
        _fit.Reset();
        _previous = 0d;
        _seeded = false;
    }
}
