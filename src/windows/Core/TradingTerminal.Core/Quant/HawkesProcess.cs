namespace TradingTerminal.Core.Quant;

/// <summary>
/// A univariate <b>Hawkes self-exciting point process</b> intensity tracker for trade-arrival times.
///
/// <para><b>Model.</b> The conditional intensity (instantaneous arrival rate) is</para>
/// <code>
///   λ(t) = μ + Σ_{tᵢ &lt; t} α · e^{−β (t − tᵢ)}
/// </code>
/// where μ ≥ 0 is the baseline rate, every past event adds an excitation of size α that decays at
/// rate β, so a burst of trades raises the probability of further trades — the empirical "trades
/// beget trades" clustering of the tape. Stationarity (a finite long-run rate) requires the
/// branching ratio <c>n = α/β &lt; 1</c>.
///
/// <para><b>Why this beats a rolling arrival count.</b> A simple count-per-window rate weights a
/// trade from the start of the window the same as one that just printed, and is blind to clustering.
/// The Hawkes intensity weights recent arrivals exponentially and explicitly captures the
/// self-excitation, so it spikes faster and decays more naturally at the onset/end of an aggressive
/// sweep — a sharper, less laggy "tape speed".</para>
///
/// <para><b>Recursive evaluation.</b> Maintaining the decayed excitation sum
/// <c>S(t) = Σ_{tᵢ ≤ t} e^{−β (t − tᵢ)}</c> lets each new event update in O(1):
/// on an event at time t, decay <c>S ← S · e^{−β·Δ}</c> (Δ since the previous event) then add 1.
/// The intensity at any later time is <c>λ(t) = μ + α · S · e^{−β·(t − t_last)}</c>.</para>
///
/// The tracker is stateful; the static <see cref="IntensityAt"/> helper is pure for unit testing.
/// </summary>
public sealed class HawkesProcess
{
    private readonly double _mu;
    private readonly double _alpha;
    private readonly double _beta;

    private double _excitation;        // S: decayed excitation sum at _lastTime
    private double _lastTime;          // seconds
    private bool _seeded;

    /// <param name="baselineMu">μ — baseline (exogenous) arrival rate. 0 makes the value a pure
    /// self-excitation/clustering measure, which is fine for a z-scored signal.</param>
    /// <param name="alpha">α — excitation jump per event. Keep α &lt; β for stationarity.</param>
    /// <param name="beta">β — exponential decay rate (1/β ≈ excitation half-life in seconds × ln2).</param>
    public HawkesProcess(double baselineMu, double alpha, double beta)
    {
        _mu = Math.Max(0, baselineMu);
        _alpha = Math.Max(0, alpha);
        _beta = Math.Max(1e-9, beta);
    }

    /// <summary>Current decayed excitation sum S at the last event time (diagnostic).</summary>
    public double Excitation => _excitation;

    /// <summary>Resets the process to its empty state.</summary>
    public void Reset()
    {
        _excitation = 0;
        _lastTime = 0;
        _seeded = false;
    }

    /// <summary>
    /// Registers an arrival at <paramref name="timeSeconds"/> (monotone non-decreasing). Decays the
    /// running excitation to this time, then adds one unit of self-excitation.
    /// </summary>
    public void Add(double timeSeconds)
    {
        if (!_seeded)
        {
            _excitation = 1.0;   // first event contributes its own excitation
            _lastTime = timeSeconds;
            _seeded = true;
            return;
        }
        var dt = timeSeconds - _lastTime;
        if (dt > 0) _excitation *= Math.Exp(-_beta * dt);
        _excitation += 1.0;
        _lastTime = timeSeconds;
    }

    /// <summary>
    /// Conditional intensity λ(t) evaluated at <paramref name="nowSeconds"/> (≥ the last event time),
    /// decaying the stored excitation forward to <paramref name="nowSeconds"/>. Returns μ before the
    /// first event.
    /// </summary>
    public double Intensity(double nowSeconds)
    {
        if (!_seeded) return _mu;
        var dt = nowSeconds - _lastTime;
        var decayed = dt > 0 ? _excitation * Math.Exp(-_beta * dt) : _excitation;
        return _mu + _alpha * decayed;
    }

    /// <summary>
    /// Pure helper: the Hawkes intensity at <paramref name="now"/> given the full set of past
    /// <paramref name="eventTimes"/> (seconds) and parameters (μ, α, β). O(n); intended for tests and
    /// one-off evaluation rather than the hot path (use the recursive instance for streaming).
    /// </summary>
    public static double IntensityAt(IReadOnlyList<double> eventTimes, double now, double mu, double alpha, double beta)
    {
        ArgumentNullException.ThrowIfNull(eventTimes);
        beta = Math.Max(1e-9, beta);
        double s = 0;
        for (var i = 0; i < eventTimes.Count; i++)
        {
            var dt = now - eventTimes[i];
            if (dt >= 0) s += Math.Exp(-beta * dt);
        }
        return Math.Max(0, mu) + Math.Max(0, alpha) * s;
    }
}
