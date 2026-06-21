using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Backtesting;

/// <summary>One sample of the account through time: mark-to-market <see cref="Equity"/>, realized-only
/// <see cref="Balance"/>, and the running <see cref="Drawdown"/> as a positive fraction of peak
/// equity. The Studio report draws the balance/equity/drawdown curves from these.</summary>
public sealed record EquitySample(DateTime TimestampUtc, double Equity, double Balance, double Drawdown);

/// <summary>
/// A completed round-trip (entry fill → flattening exit). Carries the per-trade detail an MT5-grade
/// report needs: fees, holding time, and the maximum favorable / adverse excursion reached while the
/// position was open (the inputs to the MFE/MAE scatter). PnL figures are in account currency.
/// </summary>
public sealed record RoundTripTrade(
    InstrumentId Instrument,
    DateTime EntryUtc,
    DateTime ExitUtc,
    OrderSide Side,
    long Quantity,
    double EntryPrice,
    double ExitPrice,
    double GrossPnl,
    double Fees,
    double MaxFavorableExcursion,
    double MaxAdverseExcursion)
{
    public double NetPnl => GrossPnl - Fees;
    public TimeSpan HoldingTime => ExitUtc - EntryUtc;
    public bool IsWin => NetPnl > 0;
}

/// <summary>Per-instrument contribution within a portfolio run — lets the report attribute P&amp;L
/// and activity to each symbol rather than only reporting the blended total.</summary>
public sealed record InstrumentReport(InstrumentId Instrument, double NetPnl, int TradeCount, double WinRate);

/// <summary>Headline run facts that don't depend on the (extensible) metric set.</summary>
public sealed record RunSummary(
    DateTime StartUtc,
    DateTime EndUtc,
    double StartingCash,
    double EndingEquity,
    long EventsProcessed,
    double EngineMilliseconds)
{
    public double NetProfit => EndingEquity - StartingCash;
    public double TotalReturn => StartingCash == 0 ? 0 : NetProfit / StartingCash;
}

/// <summary>
/// An open, string-keyed bag of computed metrics. Deliberately not a fixed record: new analytics
/// (deflated Sharpe, probability of backtest overfitting, tail ratios) become a new key and a new
/// well-known accessor, never a breaking schema change for the CLI, UI, or stored results. Unknown
/// keys read as <c>NaN</c> via <see cref="GetOr"/> so older readers degrade gracefully.
/// </summary>
public sealed class MetricSet
{
    private readonly IReadOnlyDictionary<string, double> _metrics;

    public MetricSet(IReadOnlyDictionary<string, double> metrics) => _metrics = metrics;

    public static MetricSet Empty { get; } = new(new Dictionary<string, double>());

    public double this[string key] => _metrics[key];
    public bool Has(string key) => _metrics.ContainsKey(key);
    public double GetOr(string key, double fallback = double.NaN) =>
        _metrics.TryGetValue(key, out var v) ? v : fallback;
    public IReadOnlyDictionary<string, double> All => _metrics;

    // Strongly-typed accessors over the open bag for the well-known metrics.
    public double Sharpe => GetOr(Keys.Sharpe);
    public double Sortino => GetOr(Keys.Sortino);
    public double Calmar => GetOr(Keys.Calmar);
    public double MaxDrawdown => GetOr(Keys.MaxDrawdown);
    public double ProfitFactor => GetOr(Keys.ProfitFactor);
    public double WinRate => GetOr(Keys.WinRate);
    public double Expectancy => GetOr(Keys.Expectancy);

    /// <summary>Canonical metric keys. Producers and consumers reference these rather than literals.</summary>
    public static class Keys
    {
        public const string Sharpe = "sharpe";
        public const string Sortino = "sortino";
        public const string Calmar = "calmar";
        public const string Omega = "omega";
        public const string MaxDrawdown = "max_drawdown";
        public const string UlcerIndex = "ulcer_index";
        public const string ProfitFactor = "profit_factor";
        public const string WinRate = "win_rate";
        public const string Expectancy = "expectancy";
        public const string RecoveryFactor = "recovery_factor";
        public const string DownsideDeviation = "downside_deviation";
        public const string MaxConsecutiveLosses = "max_consecutive_losses";
        public const string AvgHoldingSeconds = "avg_holding_seconds";
    }
}

/// <summary>
/// The complete result of a run: headline summary, the extensible metric set, the round-trip ledger,
/// the equity timeline, and per-instrument attribution for portfolio runs. The optional visual
/// timeline (chart bars + per-event markers for replay) is attached separately by the engine when
/// <see cref="RunSpec.Visual"/> is on, to keep sweep results lean.
/// </summary>
public sealed record BacktestReport(
    RunSummary Summary,
    MetricSet Metrics,
    IReadOnlyList<RoundTripTrade> Trades,
    IReadOnlyList<EquitySample> Equity,
    IReadOnlyList<InstrumentReport> PerInstrument);
