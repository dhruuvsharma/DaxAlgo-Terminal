using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.Configuration;

/// <summary>How the <c>Simulated</c> broker sources its feed.</summary>
public enum SimulatedFeedMode
{
    /// <summary>Generate a deterministic random-walk feed in-process. Needs no recorded data and
    /// no network — the fully-offline default.</summary>
    Synthetic,

    /// <summary>Replay recorded data from the local market-data store on a speed-scaled clock,
    /// re-emitting it as if it were arriving live.</summary>
    Replay,
}

/// <summary>
/// Settings for the in-process <c>Simulated</c> broker, bound from the <c>SimulatedBroker</c>
/// configuration section. Drives both the offline synthetic feed and DB replay used by the dev
/// launch profiles.
/// </summary>
public sealed class SimulatedBrokerOptions
{
    public const string SectionName = "SimulatedBroker";

    /// <summary>Synthetic random-walk (default) or replay from the local store.</summary>
    public SimulatedFeedMode Mode { get; set; } = SimulatedFeedMode.Synthetic;

    // ---- Replay knobs --------------------------------------------------------------------

    /// <summary>Wall-clock scaling for replay: 1.0 = real time, 60 = a minute per second.
    /// Also compresses the synthetic cadence proportionally.</summary>
    public double SpeedMultiplier { get; set; } = 1.0;

    /// <summary>Restart the replay from the beginning of the window once the stored data is
    /// exhausted, so a window keeps moving during a long dev session.</summary>
    public bool Loop { get; set; } = true;

    /// <summary>Cap (seconds, pre-scaling) on the idle wait between two consecutive stored events,
    /// so overnight/weekend gaps don't stall the replay.</summary>
    public int MaxGapSeconds { get; set; } = 2;

    /// <summary>Replay window: the last N days of stored data, ending now.</summary>
    public int ReplayLookbackDays { get; set; } = 30;

    // ---- Synthetic knobs -----------------------------------------------------------------

    /// <summary>Bar size emitted by the synthetic bar stream.</summary>
    public BarSize SyntheticBarSize { get; set; } = BarSize.OneMinute;

    /// <summary>Interval (ms) between synthetic quote/tick/trade emissions.</summary>
    public int SyntheticTickIntervalMs { get; set; } = 250;

    /// <summary>Interval (ms) between synthetic bar emissions (a fresh OHLC each tick).</summary>
    public int SyntheticBarIntervalMs { get; set; } = 2000;

    /// <summary>Starting price for a synthetic instrument's random walk.</summary>
    public double SyntheticStartPrice { get; set; } = 100.0;

    /// <summary>Per-step standard deviation of the synthetic walk, as a fraction of price.</summary>
    public double SyntheticVolatility { get; set; } = 0.0015;

    /// <summary>Seed for the synthetic walk, so reruns are reproducible.</summary>
    public int Seed { get; set; } = 1234;

    /// <summary>Symbols surfaced by <c>ListInstrumentsAsync</c> in Synthetic mode. Replay mode
    /// instead lists whatever instruments the store already holds.</summary>
    public string[] Instruments { get; set; } = ["AAPL", "MSFT", "ES", "NQ", "BTCUSD"];
}
