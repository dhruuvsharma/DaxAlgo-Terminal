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

public enum ExecutionDetailTab
{
    Positions,
    OpenOrders,
    History,
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
    IBrokerLoginForm? LoginForm = null)
{
    public bool IsConnected => Status == ExecutionConnectionStatus.Connected;

    public bool IsUnavailable => Status == ExecutionConnectionStatus.AdapterUnavailable;

    public bool IsLive => Mode == ExecutionMode.Live;

    public bool IsSimulated => string.Equals(Id, "simulated", StringComparison.Ordinal);

    public bool HasLoginForm => LoginForm is not null;

    public bool CanChangeExecutionMode =>
        IsRegistered && !IsUnavailable && !IsSimulated && !CanDisconnect;

    public string ModeLabel => IsLive ? "LIVE" : "PAPER";

    public ExecutionTone ModeTone => IsLive ? ExecutionTone.Negative : ExecutionTone.Info;

    public string ModeSwitchLabel => IsLive ? "Switch to PAPER" : "Switch to LIVE";

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
    ExecutionBookReadModel? Book);

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
    int WinningTrades)
{
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
    double TotalAcknowledgementLatencyMilliseconds)
{
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

public sealed record ExecutionBookCreateRequest(
    string Name,
    string AdapterId,
    IReadOnlyList<string> Strategies,
    InstrumentId Instrument = default,
    string Symbol = "");

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
