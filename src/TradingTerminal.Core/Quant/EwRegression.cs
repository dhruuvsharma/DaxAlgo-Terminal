namespace TradingTerminal.Core.Quant;

/// <summary>
/// Result of an exponentially weighted linear regression of y on x.
/// </summary>
/// <param name="Slope">β: weighted least-squares slope.</param>
/// <param name="Intercept">α: weighted least-squares intercept.</param>
/// <param name="RSquared">Weighted coefficient of determination R² ∈ [0, 1].</param>
/// <param name="ResidualStdev">σ_res: weighted standard deviation of the residuals.</param>
/// <param name="EffectiveSampleSize">Kish effective N = (Σw)² / Σw² — for SE / dof scaling.</param>
/// <param name="WeightSum">Σw: total weight applied.</param>
/// <param name="Count">Number of observations.</param>
public sealed record EwRegressionResult(
    double Slope,
    double Intercept,
    double RSquared,
    double ResidualStdev,
    double EffectiveSampleSize,
    double WeightSum,
    int Count)
{
    /// <summary>Fitted value ŷ = α + β·x at an arbitrary x.</summary>
    public double Predict(double x) => Intercept + Slope * x;
}

/// <summary>
/// Exponentially weighted linear regression of y on x. Each observation i (0 = oldest,
/// n−1 = newest) receives weight wᵢ = δ^(n−1−i) · externalWeightᵢ, so the most recent point gets
/// full weight and older points decay geometrically with the forgetting factor δ ∈ (0, 1].
/// The optional external weight folds in e.g. trade volume.
///
/// <para>Two forms are provided and are algebraically identical:</para>
/// <list type="bullet">
/// <item><see cref="Fit"/> — batch / from-scratch over arrays (two-pass-stable accumulation of
/// weighted moments).</item>
/// <item><see cref="Accumulator"/> — O(1) recursive update with the same decayed weighting, for
/// streaming use. Tests compare the two.</item>
/// </list>
///
/// <para>Math: minimise Σ wᵢ (yᵢ − α − β xᵢ)². With weighted means x̄_w, ȳ_w,
/// β = S_xy / S_xx where S_xy = Σ wᵢ(xᵢ−x̄_w)(yᵢ−ȳ_w), S_xx = Σ wᵢ(xᵢ−x̄_w)²; α = ȳ_w − β x̄_w.
/// R² = 1 − S_res / S_tot with weighted sums of squares. σ_res = sqrt(S_res / Σw).</para>
/// </summary>
public static class EwRegression
{
    /// <summary>
    /// Batch fit. <paramref name="x"/> and <paramref name="y"/> must be the same non-zero length;
    /// ordered oldest → newest. <paramref name="delta"/> ∈ (0, 1] is the forgetting factor
    /// (default 0.9). <paramref name="externalWeights"/>, when supplied, multiplies the decay
    /// weight per observation (e.g. volume); pass null for decay-only weighting.
    /// </summary>
    public static EwRegressionResult Fit(
        IReadOnlyList<double> x,
        IReadOnlyList<double> y,
        double delta = 0.9,
        IReadOnlyList<double>? externalWeights = null)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Count != y.Count) throw new ArgumentException("x and y must have equal length.");
        if (externalWeights is not null && externalWeights.Count != x.Count)
            throw new ArgumentException("externalWeights must match x length.");
        if (delta <= 0 || delta > 1) throw new ArgumentOutOfRangeException(nameof(delta));

        var n = x.Count;
        if (n < 2) return new EwRegressionResult(0, n == 1 ? y[0] : 0, 0, 0, n, 0, n);

        // Weighted moments.
        double sw = 0, sw2 = 0, swx = 0, swy = 0;
        for (var i = 0; i < n; i++)
        {
            var w = Math.Pow(delta, n - 1 - i);
            if (externalWeights is not null) w *= externalWeights[i];
            if (w < 0) w = 0;
            sw += w; sw2 += w * w;
            swx += w * x[i]; swy += w * y[i];
        }
        if (sw <= 0) return new EwRegressionResult(0, 0, 0, 0, 0, 0, n);

        var mx = swx / sw;
        var my = swy / sw;

        double sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            var w = Math.Pow(delta, n - 1 - i);
            if (externalWeights is not null) w *= externalWeights[i];
            if (w < 0) w = 0;
            var dx = x[i] - mx;
            var dy = y[i] - my;
            sxx += w * dx * dx;
            sxy += w * dx * dy;
            syy += w * dy * dy;
        }

        var slope = sxx > 1e-300 ? sxy / sxx : 0.0;
        var intercept = my - slope * mx;

        // Residual SS and R².
        double sres = 0;
        for (var i = 0; i < n; i++)
        {
            var w = Math.Pow(delta, n - 1 - i);
            if (externalWeights is not null) w *= externalWeights[i];
            if (w < 0) w = 0;
            var e = y[i] - (intercept + slope * x[i]);
            sres += w * e * e;
        }
        var r2 = syy > 1e-300 ? Math.Clamp(1.0 - sres / syy, 0.0, 1.0) : 0.0;
        var sigma = Math.Sqrt(sres / sw);
        var effN = sw2 > 1e-300 ? sw * sw / sw2 : n;

        return new EwRegressionResult(slope, intercept, r2, sigma, effN, sw, n);
    }

    /// <summary>
    /// Weighted residual series eᵢ = yᵢ − (α + β·xᵢ) using a fitted result. Oldest → newest,
    /// aligned to the input arrays. Returned unweighted (the raw residual); multiply by the decay
    /// weight if a weighted series is needed.
    /// </summary>
    public static double[] Residuals(IReadOnlyList<double> x, IReadOnlyList<double> y, EwRegressionResult fit)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(y);
        if (x.Count != y.Count) throw new ArgumentException("x and y must have equal length.");
        var e = new double[x.Count];
        for (var i = 0; i < x.Count; i++) e[i] = y[i] - fit.Predict(x[i]);
        return e;
    }

    /// <summary>
    /// O(1) recursive exponentially weighted regression. On each <see cref="Add"/> all existing
    /// accumulated moments are scaled by δ then the new (optionally externally weighted)
    /// observation is folded in — equivalent to the batch geometric weighting with the newest
    /// point at full weight. Read <see cref="Result"/> for the current fit.
    /// </summary>
    public sealed class Accumulator
    {
        private readonly double _delta;
        private double _sw, _sw2, _swx, _swy, _swxx, _swxy, _swyy;
        private int _n;

        public Accumulator(double delta = 0.9)
        {
            if (delta <= 0 || delta > 1) throw new ArgumentOutOfRangeException(nameof(delta));
            _delta = delta;
        }

        /// <summary>Number of observations folded in.</summary>
        public int Count => _n;

        /// <summary>Folds one observation. <paramref name="weight"/> is the external weight (default 1).</summary>
        public void Add(double x, double y, double weight = 1.0)
        {
            if (weight < 0) weight = 0;
            // Decay all prior mass, then add the fresh full-weight point.
            _sw *= _delta; _sw2 *= _delta * _delta;
            _swx *= _delta; _swy *= _delta;
            _swxx *= _delta; _swxy *= _delta; _swyy *= _delta;

            _sw += weight; _sw2 += weight * weight;
            _swx += weight * x; _swy += weight * y;
            _swxx += weight * x * x; _swxy += weight * x * y; _swyy += weight * y * y;
            _n++;
        }

        /// <summary>Current fit from the accumulated moments.</summary>
        public EwRegressionResult Result()
        {
            if (_n < 2 || _sw <= 0) return new EwRegressionResult(0, 0, 0, 0, _sw, _sw, _n);
            var mx = _swx / _sw;
            var my = _swy / _sw;
            var sxx = _swxx - _sw * mx * mx;
            var sxy = _swxy - _sw * mx * my;
            var syy = _swyy - _sw * my * my;
            var slope = sxx > 1e-300 ? sxy / sxx : 0.0;
            var intercept = my - slope * mx;
            // S_res = S_yy − β·S_xy (regression identity).
            var sres = Math.Max(0, syy - slope * sxy);
            var r2 = syy > 1e-300 ? Math.Clamp(1.0 - sres / syy, 0.0, 1.0) : 0.0;
            var sigma = Math.Sqrt(sres / _sw);
            var effN = _sw2 > 1e-300 ? _sw * _sw / _sw2 : _n;
            return new EwRegressionResult(slope, intercept, r2, sigma, effN, _sw, _n);
        }
    }
}
