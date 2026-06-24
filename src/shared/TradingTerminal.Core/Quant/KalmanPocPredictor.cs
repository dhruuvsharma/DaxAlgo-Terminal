namespace TradingTerminal.Core.Quant;

/// <summary>
/// A linear <b>constant-velocity Kalman filter</b> that tracks the latent state of a single
/// volume-node price series (e.g. the Buy POC, Sell POC, or Total POC sampled once per completed
/// bar) and forecasts it forward.
///
/// <para><b>State-space model.</b> The hidden state is the 2-vector
/// <c>x = [p, v]ᵀ</c> — the node's <em>price</em> p and its per-bar <em>velocity</em> v (drift). We
/// assume the node drifts at a roughly constant velocity that is itself perturbed by random
/// acceleration:</para>
/// <code>
///   xₜ = F·xₜ₋₁ + wₜ ,   F = [[1, 1], [0, 1]]        (Δt = 1 bar)
///   zₜ = H·xₜ   + vₜ ,   H = [1, 0]                  (we observe price only)
/// </code>
/// with process noise <c>w ~ N(0, Q)</c> driven by a white-acceleration variance q and measurement
/// noise <c>v ~ N(0, r)</c>. The classic discrete-white-noise-acceleration Q is
/// <c>Q = q · [[1/4, 1/2], [1/2, 1]]</c>.
///
/// <para><b>Why a Kalman filter and not a regression line.</b> A line fit weights all bars in a
/// window equally and lags at turns; the filter is recursive (O(1) per bar, no window to carry),
/// optimally blends each new POC observation against the model's own prediction by their relative
/// uncertainties, and — crucially — exposes a calibrated <em>forecast variance</em>, so a downstream
/// signal can scale its conviction by how confident the predictor actually is.</para>
///
/// The filter is inherently stateful; <see cref="Forecast"/> is non-mutating (it projects a copy of
/// the state), so it is safe to call repeatedly between updates.
/// </summary>
public sealed class KalmanPocPredictor
{
    // ── State estimate x = [p, v] ──────────────────────────────────────────────────────────────
    private double _p;   // price
    private double _v;   // velocity (price change per bar)

    // ── Estimate covariance P (symmetric 2×2): [[p00, p01], [p10, p11]] ─────────────────────────
    private double _p00, _p01, _p10, _p11;

    private readonly double _q;   // process (acceleration) noise intensity
    private readonly double _r;   // measurement noise variance

    /// <summary>True once at least one observation has initialised the state.</summary>
    public bool IsInitialized { get; private set; }

    /// <summary>Current filtered price estimate p̂.</summary>
    public double Price => _p;

    /// <summary>Current filtered velocity estimate v̂ (price change per bar).</summary>
    public double Velocity => _v;

    /// <param name="processNoise">
    /// q — white-acceleration variance. Larger ⇒ the filter trusts new observations more and adapts
    /// faster (but is noisier). Default 1e-4 suits per-bar price-level series.
    /// </param>
    /// <param name="measurementNoise">
    /// r — variance of the POC observation. Larger ⇒ the filter smooths harder. Default 1e-2.
    /// </param>
    public KalmanPocPredictor(double processNoise = 1e-4, double measurementNoise = 1e-2)
    {
        _q = Math.Max(processNoise, 1e-12);
        _r = Math.Max(measurementNoise, 1e-12);
        Reset();
    }

    /// <summary>Clears all state back to uninitialised.</summary>
    public void Reset()
    {
        _p = _v = 0;
        // Large initial covariance ⇒ the first observation dominates (diffuse prior).
        _p00 = 1e6; _p01 = 0; _p10 = 0; _p11 = 1e6;
        IsInitialized = false;
    }

    /// <summary>
    /// Folds one new POC observation into the state via the predict→update cycle. Non-finite or
    /// non-positive observations are ignored (a bar with no traded POC leaves the state to coast).
    /// </summary>
    public void Update(double observation)
    {
        if (double.IsNaN(observation) || double.IsInfinity(observation) || observation <= 0) return;

        if (!IsInitialized)
        {
            // Seed price at the first observation, velocity at 0, with a tight price prior.
            _p = observation; _v = 0;
            _p00 = _r; _p01 = 0; _p10 = 0; _p11 = 1.0;
            IsInitialized = true;
            return;
        }

        // ── Predict: x⁻ = F·x, P⁻ = F·P·Fᵀ + Q ──────────────────────────────────────────────────
        // F = [[1,1],[0,1]] ⇒ p' = p + v, v' = v.
        var p = _p + _v;
        var v = _v;

        // F·P·Fᵀ for F=[[1,1],[0,1]]:
        //   [[p00+p10+p01+p11, p01+p11], [p10+p11, p11]]
        var n00 = _p00 + _p10 + _p01 + _p11;
        var n01 = _p01 + _p11;
        var n10 = _p10 + _p11;
        var n11 = _p11;

        // + Q (discrete white-noise acceleration, scaled by q).
        n00 += _q * 0.25;
        n01 += _q * 0.5;
        n10 += _q * 0.5;
        n11 += _q * 1.0;

        // ── Update with measurement z (H = [1,0], so innovation uses the price component only) ────
        var s = n00 + _r;                 // innovation covariance H·P⁻·Hᵀ + R
        if (s < 1e-300) { _p = p; _v = v; _p00 = n00; _p01 = n01; _p10 = n10; _p11 = n11; return; }
        var k0 = n00 / s;                 // Kalman gain (price)
        var k1 = n10 / s;                 // Kalman gain (velocity)

        var y = observation - p;          // innovation (measurement residual)
        _p = p + k0 * y;
        _v = v + k1 * y;

        // P = (I − K·H)·P⁻. With H=[1,0]: subtract K·[row 0 of P⁻].
        _p00 = (1 - k0) * n00;
        _p01 = (1 - k0) * n01;
        _p10 = n10 - k1 * n00;
        _p11 = n11 - k1 * n01;
    }

    /// <summary>
    /// Projects the current state <paramref name="stepsAhead"/> bars into the future without mutating
    /// the filter. Returns the forecast price p̂(t+n) = p + n·v and the forecast variance of that
    /// price (the [0,0] entry of the propagated covariance, i.e. P grown by n predict steps). A
    /// larger variance means a less trustworthy forecast.
    /// </summary>
    public (double Price, double Variance) Forecast(int stepsAhead)
    {
        if (!IsInitialized) return (0, double.PositiveInfinity);
        var n = Math.Max(0, stepsAhead);

        double p = _p, v = _v;
        double c00 = _p00, c01 = _p01, c10 = _p10, c11 = _p11;
        for (var i = 0; i < n; i++)
        {
            // x' = F·x
            p += v;
            // P' = F·P·Fᵀ + Q (same expansion as the predict step).
            var a00 = c00 + c10 + c01 + c11 + _q * 0.25;
            var a01 = c01 + c11 + _q * 0.5;
            var a10 = c10 + c11 + _q * 0.5;
            var a11 = c11 + _q * 1.0;
            c00 = a00; c01 = a01; c10 = a10; c11 = a11;
        }
        return (p, c00);
    }
}
