namespace TradingTerminal.Core.Backtesting;

/// <summary>Where the engine pulls historical market data from.</summary>
public enum BacktestDataSource
{
    /// <summary>Replay from the canonical local store, scoped by instrument + time window. Default.</summary>
    LocalStore = 0,

    /// <summary>Replay a portable parquet file (recorder output, shipped tape, synth output).</summary>
    ParquetFile = 1,
}

/// <summary>
/// Tick-modeling fidelity, mirroring MT5's modeling modes. Higher modes are slower but truer; the
/// engine reports a modeling-quality estimate so a result's fidelity is visible, not assumed.
/// </summary>
public enum ModelingMode
{
    /// <summary>Replay captured quotes/trades exactly as recorded — highest fidelity. Default.</summary>
    RealTicks = 0,

    /// <summary>Synthesize an O→H→L→C tick path inside each bar when only bars are available.</summary>
    EveryTickFromBars = 1,

    /// <summary>One event per bar at its close — fastest, coarsest.</summary>
    BarClose = 2,

    /// <summary>One event per bar at its open.</summary>
    BarOpen = 3,
}

/// <summary>How accepted orders turn into fills against the simulated market.</summary>
public enum FillModelKind
{
    /// <summary>Fill at the touch (best bid/ask) plus configured slippage. Default.</summary>
    L1Touch = 0,

    /// <summary>Fill at the mid — optimistic; useful as an upper bound on achievable performance.</summary>
    MidPrice = 1,

    /// <summary>Defer market fills to the next bar's open — conservative for bar-mode backtests.</summary>
    NextBarOpen = 2,
}

/// <summary>Which transaction-cost model is charged per fill.</summary>
public enum CostModelKind
{
    Zero = 0,
    MakerTaker = 1,
    Bps = 2,
}

/// <summary>Whether the engine records the per-event timeline needed for the visual replay UI.</summary>
public enum VisualRecording
{
    /// <summary>No timeline capture — leanest memory profile. Default for sweeps and the CLI.</summary>
    Off = 0,

    /// <summary>Capture bars + per-event equity/position/drawdown for chart playback.</summary>
    On = 1,
}

/// <summary>
/// Where market data comes from and at what fidelity. Both <see cref="FromUtc"/> and
/// <see cref="ToUtc"/> are required for <see cref="BacktestDataSource.LocalStore"/>;
/// <see cref="ParquetPath"/> is required for <see cref="BacktestDataSource.ParquetFile"/>.
/// </summary>
public sealed record DataSpec(
    BacktestDataSource Source = BacktestDataSource.LocalStore,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null,
    ModelingMode Modeling = ModelingMode.RealTicks,
    string? ParquetPath = null,
    string? TradeParquetPath = null);

/// <summary>How orders fill: which fill model, how many ticks of slippage on market crosses, and an
/// optional order→fill latency to model reaction delay.</summary>
public sealed record ExecutionSpec(
    FillModelKind FillModel = FillModelKind.L1Touch,
    int SlippageTicks = 0,
    double LatencyMs = 0);

/// <summary>Transaction costs charged on each fill. Fields are consulted per the chosen
/// <see cref="CostModelKind"/>; defaults reproduce a zero-cost backtest exactly.</summary>
public sealed record CostSpec(
    CostModelKind Model = CostModelKind.Zero,
    double TakerFeePerUnit = 0,
    double MakerRebatePerUnit = 0,
    double FeeBps = 0);

/// <summary>
/// The complete, serializable description of one backtest run. Replaces the old flat
/// <c>BacktestConfig</c> by separating concerns — universe, data, execution, costs — so each can be
/// varied independently (e.g. the optimizer rewrites only <see cref="Parameters"/>; a cost-stress
/// study rewrites only <see cref="Cost"/>). Being a plain record, it round-trips through JSON for
/// the CLI, the Studio UI, and the Python authoring seam.
///
/// <see cref="StrategyId"/> identifies the kernel for id-based dispatch (CLI / optimizer / Python);
/// in-process C# callers may instead hand the engine a kernel instance directly and leave it blank.
/// </summary>
public sealed record RunSpec(
    Universe Universe,
    DataSpec Data,
    string StrategyId = "",
    StrategyParameters? Parameters = null,
    ExecutionSpec? Execution = null,
    CostSpec? Cost = null,
    double StartingCash = 100_000d,
    VisualRecording Visual = VisualRecording.Off)
{
    public ExecutionSpec ExecutionOrDefault => Execution ?? new ExecutionSpec();
    public CostSpec CostOrDefault => Cost ?? new CostSpec();
    public StrategyParameters ParametersOrEmpty => Parameters ?? StrategyParameters.Empty;
}
