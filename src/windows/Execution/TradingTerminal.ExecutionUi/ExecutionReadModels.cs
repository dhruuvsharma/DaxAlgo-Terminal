using System.Globalization;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Execution;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.ExecutionUi;

public enum ExecutionTone
{
    Neutral,
    Positive,
    Negative,
    Warning,
    Info,
    Accent,
}

public enum ExecutionLeaseStatus
{
    Held,
    Stale,
}

public enum ExecutionConnectionStatus
{
    Connected,
    Error,
    NotConfigured,
    AdapterUnavailable,
}

public enum ExecutionTimeRange
{
    SevenDays,
    ThirtyDays,
    NinetyDays,
    YearToDate,
}

/// <summary>The views of the selected book (or of all books).</summary>
public enum ExecutionDetailTab
{
    Overview,
    Positions,
    Orders,
    Fills,
    Quality,
    Risk,
    Audit,
}

public enum ExecutionManualOrderSide
{
    Buy,
    Sell,
}

/// <summary>
/// The order types a manually entered order may use. All four reach the venue: the OMS validates the
/// price shape of each, and the cTrader, Alpaca and Interactive Brokers adapters map every one of
/// them in both directions.
/// </summary>
public enum ExecutionManualOrderType
{
    /// <summary>Execute at the venue's available price.</summary>
    Market,

    /// <summary>Rest until the limit price or better is available.</summary>
    Limit,

    /// <summary>Rest until the stop price is reached, then execute at market.</summary>
    Stop,

    /// <summary>Rest until the stop price is reached, then execute subject to the limit.</summary>
    StopLimit,
}

public sealed record ExecutionConsoleSnapshot(
    IReadOnlyList<ExecutionAdapterReadModel> Adapters,
    IReadOnlyList<ExecutionBookReadModel> Books,
    ExecutionPortfolioAnalyticsReadModel PortfolioAnalytics,
    DateTime ObservedAtUtc,
    string? LastOperationMessage)
{
    public bool HasLiveExecution =>
        Adapters.Any(adapter => adapter.IsLive) ||
        Books.Any(book => book.IsLive);
}

public sealed record ExecutionAdapterReadModel(
    string Id,
    string DisplayName,
    string AccountLabel,
    ExecutionConnectionStatus Status,
    string StatusLabel,
    string StatusDetail,
    ExecutionTone Tone,
    bool IsRegistered,
    bool CanConnect,
    bool CanDisconnect,
    bool CanCreateBook,
    bool IsDemoOnly,
    string CredentialLabel,
    string CredentialDetail,
    IReadOnlyList<string> Capabilities,
    string EnvironmentLabel = "",
    ExecutionMode Mode = ExecutionMode.Paper,
    string BrokerAccountId = "",
    BrokerKind? LoginBroker = null,
    IBrokerLoginForm? LoginForm = null,
    bool IsRouted = false)
{
    public bool IsConnected => Status == ExecutionConnectionStatus.Connected;

    public bool IsUnavailable => Status == ExecutionConnectionStatus.AdapterUnavailable;

    public bool IsLive => Mode == ExecutionMode.Live;

    /// <summary>The in-process Paper adapter, which owns its books and needs no connection.</summary>
    public bool IsPaper => string.Equals(Id, "paper", StringComparison.Ordinal);

    public bool HasLoginForm => LoginForm is not null;

    public bool CanChangeExecutionMode =>
        IsRegistered && !IsUnavailable && !IsPaper && !CanDisconnect;

    public string ModeLabel => IsLive ? "LIVE" : "PAPER";

    public ExecutionTone ModeTone => IsLive ? ExecutionTone.Negative : ExecutionTone.Info;

    public string ModeSwitchLabel => IsLive ? "Switch to PAPER" : "Switch to LIVE";

    /// <summary>The environment as a chip: LIVE when real money moves, else the broker's own name for its test
    /// environment (DEMO, TESTNET, SANDBOX, PAPER), or LIVE ONLY for a broker that has none.</summary>
    public string ChipLabel => IsLive ? "LIVE" : EnvironmentLabel.Length > 0 ? EnvironmentLabel : "PAPER";

    public ExecutionTone ChipTone => IsLive
        ? ExecutionTone.Negative
        : string.Equals(EnvironmentLabel, "LIVE ONLY", StringComparison.Ordinal) ? ExecutionTone.Warning : ExecutionTone.Info;

    public string ConfirmationAccountLabel => string.IsNullOrWhiteSpace(BrokerAccountId)
        ? AccountLabel
        : BrokerAccountId;
}

public sealed record ExecutionLeaseReadModel(
    ExecutionLeaseStatus Status,
    long? FencingToken,
    string Detail)
{
    public bool IsHeld => Status == ExecutionLeaseStatus.Held;

    public string StatusLabel => IsHeld ? "Held" : "Stale";

    public string FenceLabel => FencingToken is { } token ? $"fence #{token}" : Detail;
}

public sealed record ExecutionBookReadModel(
    string Id,
    string Name,
    string AdapterId,
    string AdapterName,
    IReadOnlyList<string> Strategies,
    string ProfitAndLoss,
    ExecutionTone ProfitAndLossTone,
    ExecutionLeaseReadModel Lease,
    bool IsIntakePaused,
    bool AdmissionOpen,
    int OpenRealPositionCount,
    IReadOnlyList<ExecutionPositionReadModel> Positions,
    IReadOnlyList<ExecutionOrderReadModel> Orders,
    IReadOnlyList<ExecutionHistoryReadModel> History,
    IReadOnlyList<ExecutionReconciliationReadModel> ReconciliationCases,
    ExecutionRiskReadModel Risk,
    IReadOnlyList<ExecutionLedgerEventReadModel> LedgerEvents,
    ExecutionPortfolioAnalyticsReadModel Analytics,
    ExecutionMode Mode = ExecutionMode.Paper)
{
    public IReadOnlyList<ExecutionTradableInstrumentReadModel> TradableInstruments { get; init; } =
        Array.Empty<ExecutionTradableInstrumentReadModel>();

    public bool SupportsKill { get; init; } = true;

    /// <summary>Every fill the book received this session, newest first, with its cost against the price at send.</summary>
    public IReadOnlyList<ExecutionFillReadModel> Fills { get; init; } = Array.Empty<ExecutionFillReadModel>();

    /// <summary>What one of this book's units is, and the latest price it has for its instrument.</summary>
    public ExecutionBookUnitReadModel Unit { get; init; } = ExecutionBookUnitReadModel.None;

    /// <summary>Book units per unit of a bound strategy's position.</summary>
    public long UnitsPerStrategyUnit { get; init; } = 1;

    /// <summary>The book's net position in its own units (its single instrument, or the sum across a paper
    /// book's instruments).</summary>
    public decimal PositionUnits { get; init; }

    /// <summary>Opening equity plus realized P&amp;L plus open P&amp;L at the latest price.</summary>
    public decimal NetAssetValue { get; init; }

    public decimal RealizedProfitAndLoss { get; init; }

    public decimal UnrealizedProfitAndLoss { get; init; }

    /// <summary>Realized today (UTC).</summary>
    public decimal DayRealizedProfitAndLoss { get; init; }

    public decimal GrossExposure { get; init; }

    public decimal NetExposure { get; init; }

    /// <summary>The running strategy this book copies, when one is bound.</summary>
    public ExecutionStrategyLinkReadModel? StrategyLink { get; init; }

    /// <summary>Why the selected strategy cannot be used on this book — the account cannot trade its instrument, the
    /// card is not connected, another book holds the symbol — or empty when it can.</summary>
    public string StrategyWarning { get; init; } = string.Empty;

    public bool HasStrategyWarning => StrategyWarning.Length > 0;

    /// <summary>The book copies a strategy but has not been told its instrument yet: the strategy's window has not
    /// run since the book was created.</summary>
    public bool IsAwaitingInstrument { get; init; }

    public int WorkingOrderCount => Orders.Count(order => order.IsOpen);

    public int OpenReconciliationCaseCount =>
        ReconciliationCases.Count(item => !string.Equals(item.Status, "Resolved", StringComparison.Ordinal));

    public bool CanSubmitManualOrder => AdmissionOpen && TradableInstruments.Count > 0;

    public string StrategySummary => Strategies.Count switch
    {
        0 => "unbound",
        1 => Strategies[0],
        _ => $"{Strategies.Count} strategies",
    };

    public string Summary => $"{AdapterName}  |  {ModeLabel}  |  {StrategySummary}";

    public string ServiceStatus =>
        $"{AdapterName}  |  lease {Lease.StatusLabel.ToUpperInvariant()}  |  {Lease.FenceLabel}";

    public string AdmissionLabel => IsIntakePaused ? "Intake paused" : AdmissionOpen ? "Gate open" : "Gate blocked";

    public ExecutionTone AdmissionTone => IsIntakePaused
        ? ExecutionTone.Warning
        : AdmissionOpen ? ExecutionTone.Positive : ExecutionTone.Negative;

    public ExecutionTone Tone => AdmissionTone;

    public string IntakeCommandLabel => IsIntakePaused ? "Start" : "Stop";

    public bool IsLive => Mode == ExecutionMode.Live;

    public string ModeLabel => IsLive ? "LIVE" : "PAPER";

    public ExecutionTone ModeTone => IsLive ? ExecutionTone.Negative : ExecutionTone.Info;

    public bool HasOpenRealPositions => OpenRealPositionCount > 0;

    public string PositionWarning =>
        $"Book '{Name}' has {OpenRealPositionCount} open real " +
        $"position{(OpenRealPositionCount == 1 ? string.Empty : "s")}. A strategy you start manages " +
        "alongside them; it will not flatten first.";
}

public sealed record ExecutionBookNavigationReadModel(
    string Id,
    string Name,
    string Summary,
    string ProfitAndLoss,
    ExecutionTone Tone,
    bool IsAllBooks,
    ExecutionBookReadModel? Book)
{
    /// <summary>Broker and symbol (<c>cTrader · EURUSD</c>), or how many books "All books" covers.</summary>
    public string Detail { get; init; } = string.Empty;

    /// <summary>The book's net position in its units, signed.</summary>
    public string Position { get; init; } = string.Empty;

    /// <summary>The bound strategy and the book's size (<c>EMA Cross ×1,000</c>), or "Manual only".</summary>
    public string Strategy { get; init; } = string.Empty;

    /// <summary>Something that needs attention (<c>1 recon case</c>, <c>intake paused</c>), or empty.</summary>
    public string Alert { get; init; } = string.Empty;

    public bool HasAlert => Alert.Length > 0;
}

/// <summary>The whole desk at a glance: every book's value, today's result, exposure, risk and what needs attention.</summary>
public sealed record ExecutionDeskSummary(
    string NetAssetValue,
    string BooksLine,
    string DayRealized,
    ExecutionTone DayRealizedTone,
    string OpenProfitAndLoss,
    ExecutionTone OpenProfitAndLossTone,
    string GrossNet,
    string Leverage,
    string ValueAtRisk,
    string ValueAtRiskDetail,
    int WorkingOrders,
    string WorkingOrdersDetail,
    int OpenReconciliationCases,
    int UnknownOutcomes)
{
    public static readonly ExecutionDeskSummary Empty = new(
        "$0.00", "no books", "$0.00", ExecutionTone.Neutral, "$0.00", ExecutionTone.Neutral,
        "$0 / $0", "0.00× leverage", "n/a", "no history", 0, "none", 0, 0);

    public bool HasReconciliationCases => OpenReconciliationCases > 0;

    public bool HasUnknownOutcomes => UnknownOutcomes > 0;

    public string ReconciliationLabel => OpenReconciliationCases == 1 ? "1 recon case" : $"{OpenReconciliationCases} recon cases";

    public string UnknownLabel => $"{UnknownOutcomes} unknown";
}

/// <summary>The selected book's (or all books') value and P&amp;L split, for the overview.</summary>
public sealed record ExecutionSelectionSummary(
    string NetAssetValue,
    string Realized,
    ExecutionTone RealizedTone,
    string Unrealized,
    ExecutionTone UnrealizedTone,
    string DayRealized,
    ExecutionTone DayRealizedTone,
    string GrossExposure,
    string NetExposure,
    string Leverage)
{
    public static readonly ExecutionSelectionSummary Empty = new(
        "$0.00", "$0.00", ExecutionTone.Neutral, "$0.00", ExecutionTone.Neutral, "$0.00", ExecutionTone.Neutral,
        "$0", "$0", "0.00×");
}

/// <summary>One pre-trade check on the ticket: passed, a caution, or a stop.</summary>
public sealed record ExecutionTicketCheck(string Text, ExecutionTone Tone)
{
    public string Glyph => Tone switch
    {
        ExecutionTone.Positive => "✓",
        ExecutionTone.Negative => "✕",
        _ => "!",
    };
}

public sealed record ExecutionPositionReadModel(
    string BookName,
    string Instrument,
    string Side,
    ExecutionTone SideTone,
    string ConfiguredRoute,
    string ModelUnits,
    string TargetQuantity,
    string RealQuantity,
    string Delta,
    bool HasDivergence,
    string AveragePrice,
    string LastPrice,
    string UnrealizedProfitAndLoss,
    string RealizedProfitAndLoss,
    ExecutionTone ProfitAndLossTone)
{
    public ExecutionTone Tone => HasDivergence ? ExecutionTone.Warning : ProfitAndLossTone;

    /// <summary>The position in the broker's own quantity (units × the book's unit size).</summary>
    public string NativeQuantity { get; init; } = "-";

    /// <summary>Position × latest price × multiplier, in the account currency.</summary>
    public string MarketValue { get; init; } = "-";

    /// <summary>Market value as a share of the book's net asset value.</summary>
    public string PercentOfNav { get; init; } = "-";

    public ExecutionTone UnrealizedTone { get; init; } = ExecutionTone.Neutral;

    public ExecutionTone RealizedTone { get; init; } = ExecutionTone.Neutral;
}

public sealed record ExecutionOrderReadModel(
    string BookName,
    string ClientOrderId,
    string Instrument,
    string Side,
    ExecutionTone SideTone,
    string Quantity,
    string OrderType,
    string State,
    ExecutionTone StateTone,
    string ConfiguredBroker,
    string Age,
    DateTime LastUpdatedUtc)
{
    public ExecutionTone Tone => StateTone;

    public bool IsOpen => State is "Working" or "PartiallyFilled" or "PendingCancel" or "PendingReplace";

    public string TimeInForce { get; init; } = "-";

    public string LimitPrice { get; init; } = "-";

    public string StopPrice { get; init; } = "-";

    public string Filled { get; init; } = "-";

    /// <summary>Manual, Kill, Flatten, or the strategy that sent it.</summary>
    public string Source { get; init; } = "-";

    public DateTime PlacedAtUtc { get; init; }

    public string Placed => PlacedAtUtc == default
        ? "-"
        : PlacedAtUtc.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}

/// <summary>One fill on the blotter. Slippage is against the price the order was sent against: positive is a
/// cost, negative an improvement.</summary>
public sealed record ExecutionFillReadModel(
    DateTime OccurredAtUtc,
    string BookName,
    string ClientOrderId,
    string BrokerOrderId,
    string Instrument,
    string Side,
    ExecutionTone SideTone,
    string Units,
    string Quantity,
    string Price,
    string ArrivalPrice,
    double? SlippageBasisPoints,
    string Fee,
    string RealizedProfitAndLoss,
    ExecutionTone RealizedTone,
    string Source)
{
    public string Time => OccurredAtUtc.ToString("MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string Slippage => SlippageBasisPoints is { } bps ? $"{bps:+0.0;-0.0;0.0} bps" : "-";

    public ExecutionTone SlippageTone => SlippageBasisPoints switch
    {
        > 0d => ExecutionTone.Negative,
        < 0d => ExecutionTone.Positive,
        _ => ExecutionTone.Neutral,
    };
}

/// <summary>
/// What one of a book's units is: <paramref name="UnitSize"/> of <paramref name="UnitAsset"/> (1 share, 0.01 lot
/// = 1,000 EUR, 0.00001 BTC), worth <paramref name="ValuePerPoint"/> in <paramref name="Currency"/> per price point,
/// and the latest price the book has for its instrument.
/// </summary>
public sealed record ExecutionBookUnitReadModel(
    string Symbol,
    decimal UnitSize,
    string UnitAsset,
    decimal ValuePerPoint,
    string Currency,
    decimal? ReferencePrice,
    DateTime? ReferencePriceObservedAtUtc)
{
    public static readonly ExecutionBookUnitReadModel None = new(string.Empty, 1m, string.Empty, 1m, string.Empty, null, null);

    public bool HasPrice => ReferencePrice is > 0m;

    public string UnitLabel => UnitSize == 1m && UnitAsset.Length == 0
        ? "1 unit = 1 share or contract"
        : $"1 unit = {ExecutionFormatting.Units(UnitSize)}{AssetSuffix}";

    public string PriceDisplay => ReferencePrice is { } price && price > 0m ? ExecutionFormatting.Price(price) : "no price";

    /// <summary><paramref name="units"/> in the broker's own quantity.</summary>
    public string Native(decimal units) => $"{ExecutionFormatting.Units(units * UnitSize)}{AssetSuffix}";

    /// <summary>Account-currency value of <paramref name="units"/> at the latest price, or null without one.</summary>
    public decimal? Notional(decimal units) => ReferencePrice is { } price && price > 0m ? Math.Abs(units) * price * ValuePerPoint : null;

    public string PriceAge(DateTime nowUtc)
    {
        if (ReferencePriceObservedAtUtc is not { } observed)
            return HasPrice ? "age unknown" : "no price";
        var age = nowUtc - observed;
        return age.TotalSeconds < 1d ? "under 1 s old"
            : age.TotalSeconds < 120d ? $"{age.TotalSeconds:0} s old"
            : age.TotalMinutes < 120d ? $"{age.TotalMinutes:0} min old"
            : "stale";
    }

    private string AssetSuffix => UnitAsset.Length == 0 ? string.Empty : $" {UnitAsset}";
}

/// <summary>The running strategy a book copies and what became of its last target.</summary>
public sealed record ExecutionStrategyLinkReadModel(
    string Strategy,
    bool IsRunning,
    long? LastTargetUnits,
    string LastOutcome,
    bool LastOutcomeSucceeded,
    DateTime? LastAtUtc)
{
    /// <summary>Why the strategy's position could not be turned into a target at all (no single instrument, a size
    /// that does not divide it), or empty. Shown as the book's warning.</summary>
    public string Problem { get; init; } = string.Empty;

    public string Status => IsRunning ? "Running" : "Window closed";

    public ExecutionTone StatusTone => IsRunning ? ExecutionTone.Positive : ExecutionTone.Neutral;

    public string LastTargetDisplay => LastTargetUnits is { } units
        ? $"{units.ToString("+#,##0;-#,##0;0", CultureInfo.InvariantCulture)} units"
        : "none yet";

    public string LastAtDisplay => LastAtUtc is { } at ? at.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC" : "-";

    public ExecutionTone OutcomeTone => LastAtUtc is null
        ? ExecutionTone.Neutral
        : LastOutcomeSucceeded ? ExecutionTone.Positive : ExecutionTone.Warning;
}

public sealed record ExecutionHistoryReadModel(
    DateTime OccurredAtUtc,
    string BookName,
    string Instrument,
    string Event,
    string Detail,
    string Quantity,
    string Price,
    string ProfitAndLoss,
    ExecutionTone Tone)
{
    public string Date => OccurredAtUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string Time => OccurredAtUtc.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}

public sealed record ExecutionReconciliationReadModel(
    string Subject,
    string Detail,
    string Type,
    ExecutionTone TypeTone,
    string Status)
{
    public ExecutionTone Tone => TypeTone;
}

public sealed record ExecutionRiskReadModel(
    IReadOnlyList<ExecutionRiskUsageReadModel> Usage,
    string EscalationLine);

public sealed record ExecutionRiskUsageReadModel(
    string Label,
    string Value,
    double Percentage,
    ExecutionTone Tone);

public sealed record ExecutionLedgerEventReadModel(
    DateTime OccurredAtUtc,
    string Timestamp,
    string Message,
    string Hash,
    ExecutionTone Tone);

public sealed record ExecutionTradeHistoryPoint(
    DateTime ClosedAtUtc,
    string Instrument,
    decimal RealizedProfitAndLoss);

public sealed record ExecutionEquityPointReadModel(DateTime TimestampUtc, decimal Equity);

public sealed record ExecutionDailyPnlPointReadModel(DateTime DateUtc, decimal RealizedProfitAndLoss);

/// <summary>
/// A period's performance. Trades are closed trades — each fill that reduced a position, net of its fees and its
/// share of the opening fees — so win rate, profit factor and expectancy are after costs.
/// <paramref name="ValueAtRisk95"/> is one-day historical VaR at 95% from the period's daily P&amp;L, measured
/// only once <see cref="MinimumRiskObservations"/> active days exist (<paramref name="RiskObservations"/> says
/// how many there were).
/// </summary>
public sealed record ExecutionMetricResult(
    decimal Equity,
    decimal NetProfitAndLoss,
    decimal ReturnPercent,
    double Sharpe,
    decimal MaxDrawdownPercent,
    decimal WinRatePercent,
    int OpenPositions,
    decimal NetExposure,
    int TradeCount,
    int WinningTrades,
    decimal GrossProfit = 0m,
    decimal GrossLoss = 0m,
    int LosingTrades = 0,
    double Sortino = 0d,
    double AnnualizedReturnPercent = 0d,
    decimal ValueAtRisk95 = 0m,
    int RiskObservations = 0)
{
    /// <summary>Fewer active days than this and a 5th percentile is one or two observations: not reported.</summary>
    public const int MinimumRiskObservations = 20;

    public bool HasValueAtRisk => RiskObservations >= MinimumRiskObservations;

    public string SortinoDisplay => Sortino.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Annualised return over the depth of the worst drawdown.</summary>
    public string CalmarDisplay => MaxDrawdownPercent < 0m
        ? (AnnualizedReturnPercent / (double)-MaxDrawdownPercent).ToString("0.00", CultureInfo.InvariantCulture)
        : "n/a";

    public string ProfitFactorDisplay => GrossLoss < 0m
        ? (GrossProfit / -GrossLoss).ToString("0.00", CultureInfo.InvariantCulture)
        : GrossProfit > 0m ? "∞" : "n/a";

    public decimal Expectancy => TradeCount == 0 ? 0m : (GrossProfit + GrossLoss) / TradeCount;

    public string ExpectancyDisplay => TradeCount == 0 ? "n/a" : ExecutionFormatting.SignedMoney(Expectancy);

    public ExecutionTone ExpectancyTone => Expectancy switch
    {
        > 0m => ExecutionTone.Positive,
        < 0m => ExecutionTone.Negative,
        _ => ExecutionTone.Neutral,
    };

    public string AverageWinLossDisplay =>
        $"{(WinningTrades == 0 ? "-" : ExecutionFormatting.Money(GrossProfit / WinningTrades))} / " +
        $"{(LosingTrades == 0 ? "-" : ExecutionFormatting.Money(GrossLoss / LosingTrades))}";

    public string PayoffDisplay => WinningTrades > 0 && LosingTrades > 0 && GrossLoss < 0m
        ? $"payoff {(GrossProfit / WinningTrades / (-GrossLoss / LosingTrades)).ToString("0.00", CultureInfo.InvariantCulture)}"
        : "payoff n/a";

    public string TradesDisplay => $"{TradeCount.ToString("N0", CultureInfo.InvariantCulture)} closed · {WinningTrades.ToString("N0", CultureInfo.InvariantCulture)} won";

    public string ValueAtRiskDisplay => HasValueAtRisk ? ExecutionFormatting.Money(ValueAtRisk95) : "n/a";

    public string ValueAtRiskDetail => HasValueAtRisk
        ? Equity > 0m ? $"{ValueAtRisk95 / Equity * 100m:0.00}% of equity · {RiskObservations} days" : $"{RiskObservations} days"
        : $"{RiskObservations} of {MinimumRiskObservations} days needed";

    public string EquityDisplay => ExecutionFormatting.Money(Equity);

    public string NetProfitAndLossDisplay => ExecutionFormatting.SignedMoney(NetProfitAndLoss);

    public string ReturnDisplay => ExecutionFormatting.SignedPercent(ReturnPercent);

    public string SharpeDisplay => Sharpe.ToString("0.00", CultureInfo.InvariantCulture);

    public string MaxDrawdownDisplay => $"{MaxDrawdownPercent:0.0}%";

    public string WinRateDisplay => $"{WinRatePercent:0}%";

    public string OpenPositionsDisplay => OpenPositions.ToString("N0", CultureInfo.InvariantCulture);

    public string NetExposureDisplay => ExecutionFormatting.CompactMoney(NetExposure, signed: true);

    public ExecutionTone ProfitAndLossTone => NetProfitAndLoss switch
    {
        > 0m => ExecutionTone.Positive,
        < 0m => ExecutionTone.Negative,
        _ => ExecutionTone.Neutral,
    };

    public ExecutionTone DrawdownTone => MaxDrawdownPercent < 0m ? ExecutionTone.Negative : ExecutionTone.Neutral;
}

public sealed record ExecutionPeriodAnalyticsReadModel(
    ExecutionTimeRange Range,
    string Label,
    ExecutionMetricResult Metrics,
    IReadOnlyList<ExecutionEquityPointReadModel> EquitySeries,
    IReadOnlyList<ExecutionDailyPnlPointReadModel> DailyProfitAndLossSeries);

public sealed record ExecutionExposureReadModel(
    string BookId,
    string BookName,
    decimal LongExposure,
    decimal ShortExposure,
    decimal NetExposure,
    double LongPercentage,
    double ShortPercentage)
{
    public string LongDisplay => ExecutionFormatting.CompactMoney(LongExposure, signed: true);

    public string ShortDisplay => ExecutionFormatting.CompactMoney(ShortExposure, signed: true);

    public string NetDisplay => ExecutionFormatting.CompactMoney(NetExposure, signed: true);

    public ExecutionTone Tone => NetExposure switch
    {
        > 0m => ExecutionTone.Positive,
        < 0m => ExecutionTone.Negative,
        _ => ExecutionTone.Neutral,
    };
}

/// <summary>
/// How orders were worked. The basis-point figures compare each fill with the price its order was sent against
/// (the price the risk check used): positive is a cost. <paramref name="ImplementationShortfall"/> is that
/// difference in money across every measured fill, plus all fees.
/// </summary>
public sealed record ExecutionQualityReadModel(
    int Orders,
    int FilledOrders,
    int Rejects,
    int Cancels,
    int ReconciliationCases,
    int UnknownOutcomes,
    int SlippageObservationCount,
    double TotalSlippageTicks,
    int AcknowledgementObservationCount,
    double TotalAcknowledgementLatencyMilliseconds,
    int Fills = 0,
    int SlippageBasisPointObservations = 0,
    double TotalSlippageBasisPoints = 0d,
    decimal Fees = 0m,
    decimal ImplementationShortfall = 0m,
    decimal Turnover = 0m)
{
    public double CancelRatePercent => Orders == 0 ? 0d : Cancels * 100d / Orders;

    public string CancelRateDisplay => $"{CancelRatePercent:0.0}%";

    public double AverageSlippageBasisPoints =>
        SlippageBasisPointObservations == 0 ? 0d : TotalSlippageBasisPoints / SlippageBasisPointObservations;

    public string AverageSlippageBasisPointsDisplay =>
        SlippageBasisPointObservations == 0 ? "n/a" : $"{AverageSlippageBasisPoints:+0.0;-0.0;0.0} bps";

    public ExecutionTone SlippageTone => SlippageBasisPointObservations == 0
        ? ExecutionTone.Neutral
        : AverageSlippageBasisPoints > 0d ? ExecutionTone.Negative : ExecutionTone.Positive;

    public string FeesDisplay => ExecutionFormatting.Money(Fees);

    public string FeesDetail => Turnover > 0m ? $"{Fees / Turnover * 10_000m:0.00} bps of volume" : "no volume";

    public string ImplementationShortfallDisplay =>
        SlippageBasisPointObservations == 0 && Fees == 0m ? "n/a" : ExecutionFormatting.SignedMoney(ImplementationShortfall);

    public ExecutionTone ImplementationShortfallTone => ImplementationShortfall switch
    {
        > 0m => ExecutionTone.Negative,
        < 0m => ExecutionTone.Positive,
        _ => ExecutionTone.Neutral,
    };

    public string TurnoverDisplay => ExecutionFormatting.CompactMoney(Turnover);

    public string FillsDetail => $"{Fills.ToString("N0", CultureInfo.InvariantCulture)} fills · {SlippageBasisPointObservations.ToString("N0", CultureInfo.InvariantCulture)} measured";

    public string OrdersDetail => $"{FilledOrders.ToString("N0", CultureInfo.InvariantCulture)} of {Orders.ToString("N0", CultureInfo.InvariantCulture)} orders";

    public double FillRatePercent => Orders == 0 ? 0d : FilledOrders * 100d / Orders;

    public double RejectRatePercent => Orders == 0 ? 0d : Rejects * 100d / Orders;

    public double AverageSlippageTicks =>
        SlippageObservationCount == 0 ? 0d : TotalSlippageTicks / SlippageObservationCount;

    public double AverageAcknowledgementLatencyMilliseconds =>
        AcknowledgementObservationCount == 0
            ? 0d
            : TotalAcknowledgementLatencyMilliseconds / AcknowledgementObservationCount;

    public string FillRateDisplay => $"{FillRatePercent:0.0}%";

    public string AverageSlippageDisplay =>
        SlippageObservationCount == 0 ? "n/a" : $"{AverageSlippageTicks:0.00} tk";

    public string RejectRateDisplay => $"{RejectRatePercent:0.0}%";

    public string AverageAcknowledgementDisplay =>
        AcknowledgementObservationCount == 0 ? "n/a" : $"{AverageAcknowledgementLatencyMilliseconds:0} ms";
}

public sealed record ExecutionPortfolioAnalyticsReadModel(
    IReadOnlyList<ExecutionPeriodAnalyticsReadModel> Periods,
    IReadOnlyList<ExecutionExposureReadModel> ExposureByBook,
    ExecutionQualityReadModel ExecutionQuality)
{
    /// <summary>
    /// The analytics for one range. Every well-formed portfolio carries a period per range, including
    /// one with no books at all, so a miss here is a construction bug rather than absent data - and it
    /// says which range, because the LINQ default ("Sequence contains no matching element") named
    /// nothing and cost a silent window failure to track down.
    /// </summary>
    public ExecutionPeriodAnalyticsReadModel Period(ExecutionTimeRange range) =>
        Periods.FirstOrDefault(item => item.Range == range)
        ?? throw new InvalidOperationException(
            $"Portfolio analytics carry no {range} period; they were built with an incomplete range set.");
}

public sealed record ExecutionBookBreakdownReadModel(
    string BookId,
    string BookName,
    ExecutionTone Tone,
    string Equity,
    string DayProfitAndLoss,
    ExecutionTone DayProfitAndLossTone,
    string Return,
    ExecutionTone ReturnTone,
    string Sharpe,
    string Trades);

/// <summary>A new book. <paramref name="UnitsPerStrategyUnit"/> is its size: how many of the book's own units
/// (a share, a contract, the broker's volume step) one unit of a bound strategy's position stands for.</summary>
public sealed record ExecutionBookCreateRequest(
    string Name,
    string AdapterId,
    IReadOnlyList<string> Strategies,
    InstrumentId Instrument = default,
    string Symbol = "",
    long UnitsPerStrategyUnit = 1);

/// <summary>The account a routed broker's LIVE credentials reach, read before anything is enabled.</summary>
public sealed record ExecutionLiveAccountProbe(bool IsSuccess, string AccountId, string Message);

public sealed record ExecutionAdapterConnectRequest(
    string AdapterId,
    string KeyId = "",
    string SecretKey = "",
    string Host = "",
    int Port = 0,
    int ClientId = 0,
    string AccountId = "",
    string OAuthClientId = "",
    string OAuthClientSecret = "",
    string OAuthAccessToken = "")
{
    public override string ToString() => $"{AdapterId}|{AccountId}";
}

public sealed class ExecutionModeChangeRequest
{
    public ExecutionModeChangeRequest(
        string adapterId,
        string accountId,
        ExecutionMode mode,
        string typedConfirmation = "",
        string keyId = "",
        string secretKey = "",
        string host = "",
        int port = 0,
        int clientId = 0,
        string oauthClientId = "",
        string oauthClientSecret = "",
        string oauthAccessToken = "")
    {
        AdapterId = adapterId ?? throw new ArgumentNullException(nameof(adapterId));
        AccountId = accountId ?? throw new ArgumentNullException(nameof(accountId));
        Mode = mode;
        TypedConfirmation = typedConfirmation ?? throw new ArgumentNullException(nameof(typedConfirmation));
        KeyId = keyId ?? throw new ArgumentNullException(nameof(keyId));
        SecretKey = secretKey ?? throw new ArgumentNullException(nameof(secretKey));
        Host = host ?? throw new ArgumentNullException(nameof(host));
        Port = port;
        ClientId = clientId;
        OAuthClientId = oauthClientId ?? throw new ArgumentNullException(nameof(oauthClientId));
        OAuthClientSecret = oauthClientSecret ?? throw new ArgumentNullException(nameof(oauthClientSecret));
        OAuthAccessToken = oauthAccessToken ?? throw new ArgumentNullException(nameof(oauthAccessToken));
    }

    public string AdapterId { get; }

    public string AccountId { get; }

    public ExecutionMode Mode { get; }

    public string TypedConfirmation { get; }

    public string KeyId { get; }

    public string SecretKey { get; }

    public string Host { get; }

    public int Port { get; }

    public int ClientId { get; }

    public string OAuthClientId { get; }

    public string OAuthClientSecret { get; }

    public string OAuthAccessToken { get; }

    public override string ToString() => $"{AdapterId}|{AccountId}|{Mode}";
}

public sealed record ExecutionTradableInstrumentReadModel(
    InstrumentId Instrument,
    string Symbol)
{
    public string DisplayName => $"{Symbol}  |  #{Instrument.Value}";
}

public sealed record ExecutionManualOrderRequest(
    string BookId,
    InstrumentId Instrument,
    string Symbol,
    ExecutionManualOrderSide Side,
    ScaledQuantity Quantity,
    ExecutionManualOrderType OrderType,
    ScaledPrice? LimitPrice,
    ScaledPrice? StopPrice = null)
{
    public CanonicalOrderType CanonicalOrderType => OrderType switch
    {
        ExecutionManualOrderType.Limit => CanonicalOrderType.Limit,
        ExecutionManualOrderType.Stop => CanonicalOrderType.Stop,
        ExecutionManualOrderType.StopLimit => CanonicalOrderType.StopLimit,
        _ => CanonicalOrderType.Market,
    };

    /// <summary>
    /// True when the supplied prices match the order type. The OMS enforces the same rule, but
    /// checking here turns a fail-closed rejection deep in the ledger into an answerable message.
    /// </summary>
    public bool HasWellFormedPriceTerms => OrderType switch
    {
        ExecutionManualOrderType.Market => LimitPrice is null && StopPrice is null,
        ExecutionManualOrderType.Limit => LimitPrice is not null && StopPrice is null,
        ExecutionManualOrderType.Stop => LimitPrice is null && StopPrice is not null,
        ExecutionManualOrderType.StopLimit => LimitPrice is not null && StopPrice is not null,
        _ => false,
    };
}

public readonly record struct ExecutionCommandResult(bool IsSuccess, string Message)
{
    public static ExecutionCommandResult Success(string message) => new(true, message);

    public static ExecutionCommandResult Failure(string message) => new(false, message);
}

internal static class ExecutionFormatting
{
    internal static string Money(decimal value) => value.ToString("$#,##0.00;-$#,##0.00;$0.00", CultureInfo.InvariantCulture);

    internal static string SignedMoney(decimal value) => value switch
    {
        > 0m => $"+{Money(value)}",
        < 0m => Money(value),
        _ => "$0.00",
    };

    internal static string SignedPercent(decimal value) => value switch
    {
        > 0m => $"+{value:0.0}%",
        < 0m => $"{value:0.0}%",
        _ => "0.0%",
    };

    /// <summary>A price with its own precision: no rounding, no trailing zeros, thousands separated.</summary>
    internal static string Price(decimal value) =>
        (value / 1.000000000000000000000000000000000m).ToString("#,##0.##########", CultureInfo.InvariantCulture);

    internal static string Units(decimal value) =>
        (value / 1.000000000000000000000000000000000m).ToString("#,##0.##########", CultureInfo.InvariantCulture);

    internal static string SignedUnits(decimal value) => value switch
    {
        > 0m => $"+{Units(value)}",
        < 0m => $"-{Units(-value)}",
        _ => "0",
    };

    internal static ExecutionTone ToneOf(decimal value) => value switch
    {
        > 0m => ExecutionTone.Positive,
        < 0m => ExecutionTone.Negative,
        _ => ExecutionTone.Neutral,
    };

    internal static string CompactMoney(decimal value, bool signed = false)
    {
        var absolute = Math.Abs(value);
        var body = absolute switch
        {
            >= 1_000_000m => $"${absolute / 1_000_000m:0.#}m",
            >= 1_000m => $"${absolute / 1_000m:0.#}k",
            _ => $"${absolute:0}",
        };
        if (value < 0m)
            return $"-{body}";
        return signed && value > 0m ? $"+{body}" : body;
    }
}
