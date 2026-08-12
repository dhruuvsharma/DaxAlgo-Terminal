using System.Text;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Execution.Oms;

/// <summary>Stable identity of one execution-adapter implementation.</summary>
public readonly record struct ExecutionAdapterId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Stable broker-side trading-account identity.</summary>
public readonly record struct BrokerAccountId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>One independently serialized adapter/account execution resource.</summary>
public readonly record struct BrokerExecutionAccount(
    ExecutionAdapterId AdapterId,
    BrokerAccountId AccountId)
{
    /// <summary>Gets whether both parts of the routing identity are valid.</summary>
    public bool IsValid => AdapterId.IsValid && AccountId.IsValid;
}

/// <summary>Health of the adapter session independently of its data and execution permissions.</summary>
public enum ExecutionSessionHealth : byte
{
    /// <summary>No usable session exists.</summary>
    Disconnected = 0,

    /// <summary>The session exists but must not accept new execution commands.</summary>
    Degraded = 1,

    /// <summary>The session is healthy for the permissions explicitly reported alongside it.</summary>
    Healthy = 2,

    /// <summary>The venue is in a known maintenance state.</summary>
    Maintenance = 3,
}

/// <summary>
/// Immutable connection and authentication observation. Data connectivity deliberately does not
/// imply that execution is authenticated or certified.
/// </summary>
public sealed record BrokerExecutionSession(
    BrokerExecutionAccount Account,
    ExecutionSessionHealth Health,
    bool IsDataConnected,
    bool IsExecutionAuthenticated,
    bool IsExecutionCertified,
    DateTime ObservedAtUtc)
{
    /// <summary>Gets whether this observation admits an execution command.</summary>
    public bool CanExecute =>
        Account.IsValid &&
        Health == ExecutionSessionHealth.Healthy &&
        IsExecutionAuthenticated &&
        IsExecutionCertified &&
        ObservedAtUtc.Kind == DateTimeKind.Utc;
}

/// <summary>How the adapter can alter a working native order.</summary>
public enum BrokerReplaceSemantics : byte
{
    /// <summary>Native replacement is not supported.</summary>
    Unsupported = 0,

    /// <summary>The venue changes the existing native order in place.</summary>
    InPlace = 1,

    /// <summary>The venue implements replacement as an explicit cancel/new operation.</summary>
    CancelAndNew = 2,
}

/// <summary>UTC trading-session window reported by one capability snapshot.</summary>
public readonly record struct BrokerTradingHours(
    bool IsAlwaysOpen,
    TimeOnly OpensAtUtc,
    TimeOnly ClosesAtUtc)
{
    /// <summary>
    /// Gets exact UTC intervals measured from Sunday 00:00. When populated, these intervals take
    /// precedence over the legacy recurring daily window.
    /// </summary>
    public IReadOnlyList<BrokerWeeklyTradingInterval>? WeeklyIntervals { get; init; }

    /// <summary>Gets exact UTC holiday closures applied after the recurring weekly schedule.</summary>
    public IReadOnlyList<BrokerTradingClosure>? Closures { get; init; }

    /// <summary>A deterministic continuously open simulated session.</summary>
    public static BrokerTradingHours AlwaysOpen => new(true, default, default);

    /// <summary>Creates an immutable exact weekly UTC schedule.</summary>
    public static BrokerTradingHours FromWeeklyIntervals(
        IEnumerable<BrokerWeeklyTradingInterval> intervals,
        IEnumerable<BrokerTradingClosure>? closures = null)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var copy = intervals.ToArray();
        if (copy.Length == 0 || copy.Any(static interval => !interval.IsValid))
            throw new ArgumentException("At least one valid weekly trading interval is required.", nameof(intervals));
        var closureCopy = closures?.ToArray() ?? [];
        if (closureCopy.Any(static closure => !closure.IsValid))
            throw new ArgumentException("Every trading closure must be a valid UTC interval.", nameof(closures));
        return new BrokerTradingHours(false, default, default)
        {
            WeeklyIntervals = Array.AsReadOnly(copy),
            Closures = Array.AsReadOnly(closureCopy),
        };
    }

    /// <summary>Gets whether this declaration is usable for fail-closed admission.</summary>
    public bool IsValid =>
        IsAlwaysOpen
            ? WeeklyIntervals is null && Closures is null
            : WeeklyIntervals is { Count: > 0 }
                ? WeeklyIntervals.All(static interval => interval.IsValid) &&
                  (Closures is null || Closures.All(static closure => closure.IsValid))
                : OpensAtUtc != ClosesAtUtc;

    /// <summary>Gets whether an injected UTC instant is inside the declared session.</summary>
    public bool IsOpen(DateTime utcNow)
    {
        if (utcNow.Kind != DateTimeKind.Utc)
            return false;
        if (IsAlwaysOpen)
            return true;
        if (WeeklyIntervals is { Count: > 0 })
        {
            var secondOfWeek =
                ((int)utcNow.DayOfWeek * BrokerWeeklyTradingInterval.SecondsPerDay) +
                utcNow.TimeOfDay.TotalSeconds;
            return WeeklyIntervals.Any(interval => interval.Contains(secondOfWeek)) &&
                   (Closures is null || !Closures.Any(closure => closure.Contains(utcNow)));
        }
        if (OpensAtUtc == ClosesAtUtc)
            return false;

        var time = TimeOnly.FromDateTime(utcNow);
        return OpensAtUtc < ClosesAtUtc
            ? time >= OpensAtUtc && time < ClosesAtUtc
            : time >= OpensAtUtc || time < ClosesAtUtc;
    }
}

/// <summary>One exact UTC holiday closure, either one date or recurring annually.</summary>
public readonly record struct BrokerTradingClosure(
    DateOnly DateUtc,
    bool IsRecurring,
    uint StartSecond,
    uint EndSecond)
{
    /// <summary>Gets whether the closure is non-empty and bounded by one UTC day.</summary>
    public bool IsValid => StartSecond < EndSecond && EndSecond <= BrokerWeeklyTradingInterval.SecondsPerDay;

    /// <summary>Gets whether one UTC instant lies inside this closure.</summary>
    public bool Contains(DateTime utcNow)
    {
        if (!IsValid || utcNow.Kind != DateTimeKind.Utc)
            return false;
        var date = DateOnly.FromDateTime(utcNow);
        var matchesDate = IsRecurring
            ? date.Month == DateUtc.Month && date.Day == DateUtc.Day
            : date == DateUtc;
        if (!matchesDate)
            return false;
        var secondOfDay = utcNow.TimeOfDay.TotalSeconds;
        return secondOfDay >= StartSecond && secondOfDay < EndSecond;
    }
}

/// <summary>One exact UTC trading interval measured in seconds from Sunday 00:00.</summary>
public readonly record struct BrokerWeeklyTradingInterval(uint StartSecond, uint EndSecond)
{
    /// <summary>Number of seconds in one day.</summary>
    public const int SecondsPerDay = 24 * 60 * 60;

    /// <summary>Number of seconds in one week.</summary>
    public const int SecondsPerWeek = 7 * SecondsPerDay;

    /// <summary>Gets whether the interval is non-empty and bounded by one UTC week.</summary>
    public bool IsValid => StartSecond < EndSecond && EndSecond <= SecondsPerWeek;

    /// <summary>Gets whether a UTC second-of-week is inside this half-open interval.</summary>
    public bool Contains(double secondOfWeek) =>
        IsValid &&
        double.IsFinite(secondOfWeek) &&
        secondOfWeek >= StartSecond &&
        secondOfWeek < EndSecond;
}

/// <summary>One native command budget over a fixed deterministic window.</summary>
public readonly record struct BrokerRateLimit(int MaximumCommands, TimeSpan Window)
{
    /// <summary>Gets whether the rate-limit declaration can be enforced exactly.</summary>
    public bool IsValid => MaximumCommands > 0 && Window > TimeSpan.Zero;
}

/// <summary>Why an exact instruction cannot be admitted without changing its meaning.</summary>
public enum ExecutionAdmissionFault : byte
{
    /// <summary>The instruction is admitted unchanged.</summary>
    None = 0,

    /// <summary>The connection/session observation is structurally invalid.</summary>
    InvalidSession = 1,

    /// <summary>Market data is not connected.</summary>
    DataDisconnected = 2,

    /// <summary>The adapter is data-only or otherwise lacks execution authentication.</summary>
    ExecutionNotAuthenticated = 3,

    /// <summary>The adapter/account has not passed execution certification.</summary>
    ExecutionNotCertified = 4,

    /// <summary>The session is degraded, disconnected, or under maintenance.</summary>
    SessionUnavailable = 5,

    /// <summary>The capability declaration itself is invalid.</summary>
    InvalidCapabilities = 6,

    /// <summary>The canonical instruction is structurally invalid.</summary>
    InvalidInstruction = 7,

    /// <summary>The native order type is unsupported.</summary>
    UnsupportedOrderType = 8,

    /// <summary>The native time in force is unsupported.</summary>
    UnsupportedTimeInForce = 9,

    /// <summary>The exact quantity is outside the declared minimum or maximum.</summary>
    QuantityOutOfRange = 10,

    /// <summary>The exact quantity exceeds native precision or is not on the lot-size grid.</summary>
    QuantityNotRepresentable = 11,

    /// <summary>An exact limit or stop price is outside the declared price band.</summary>
    PriceOutOfRange = 12,

    /// <summary>An exact limit or stop price exceeds precision or is not on the tick grid.</summary>
    PriceNotRepresentable = 13,

    /// <summary>The venue is outside its declared trading hours.</summary>
    OutsideTradingHours = 14,

    /// <summary>The requested replace operation is unsupported.</summary>
    ReplaceUnsupported = 15,
}

/// <summary>Fault-as-value exact normalization outcome; successful normalization is pass-through.</summary>
public readonly record struct ExecutionAdmissionResult(
    ExecutionAdmissionFault Fault,
    CanonicalOrderInstruction? NormalizedInstruction,
    string? Reason)
{
    /// <summary>Gets whether the original exact instruction is representable without alteration.</summary>
    public bool IsSuccess => Fault == ExecutionAdmissionFault.None && NormalizedInstruction is not null;
}

/// <summary>
/// Immutable execution capability snapshot. It extends the slice-1 type/TIF capability vocabulary
/// with exact native grids, bands, replace semantics, hours, and command rate limits.
/// </summary>
public sealed record BrokerExecutionCapabilities(
    string Version,
    VenueCapabilities CanonicalCapabilities,
    byte QuantityPrecision,
    ScaledQuantity MinimumQuantity,
    ScaledQuantity MaximumQuantity,
    ScaledQuantity LotSize,
    bool SupportsFractionalQuantity,
    byte PricePrecision,
    ScaledPrice TickSize,
    ScaledPrice? MinimumPrice,
    ScaledPrice? MaximumPrice,
    BrokerReplaceSemantics ReplaceSemantics,
    bool SupportsNativeBracket,
    bool SupportsNativeOco,
    BrokerTradingHours TradingHours,
    BrokerRateLimit RateLimit)
{
    /// <summary>
    /// Validates and normalizes one instruction. Success returns the original instruction unchanged;
    /// this method never rounds, clamps, changes type/TIF, or substitutes a different order.
    /// </summary>
    public ExecutionAdmissionResult Normalize(
        CanonicalOrderInstruction? instruction,
        DateTime utcNow,
        bool isReplace = false)
    {
        if (!IsValid())
            return Rejected(ExecutionAdmissionFault.InvalidCapabilities, "The adapter capability snapshot is invalid.");
        if (instruction is null || instruction.Validate() != OrderDomainFault.None)
            return Rejected(ExecutionAdmissionFault.InvalidInstruction, "The canonical instruction is invalid.");
        if (!TradingHours.IsOpen(utcNow))
            return Rejected(ExecutionAdmissionFault.OutsideTradingHours, "The adapter reports that trading is closed.");
        if (isReplace && ReplaceSemantics != BrokerReplaceSemantics.InPlace)
        {
            return Rejected(
                ExecutionAdmissionFault.ReplaceUnsupported,
                ReplaceSemantics == BrokerReplaceSemantics.CancelAndNew
                    ? "Cancel-and-new replacement requires a later child-order identity model."
                    : "The adapter does not support native replacement.");
        }

        var canonicalFault = CanonicalCapabilities.Validate(instruction.Terms);
        if (canonicalFault == OrderDomainFault.UnsupportedOrderType)
            return Rejected(ExecutionAdmissionFault.UnsupportedOrderType, "The adapter cannot represent the requested order type.");
        if (canonicalFault == OrderDomainFault.UnsupportedTimeInForce)
            return Rejected(ExecutionAdmissionFault.UnsupportedTimeInForce, "The adapter cannot represent the requested time in force.");
        if (canonicalFault != OrderDomainFault.None)
            return Rejected(ExecutionAdmissionFault.InvalidInstruction, $"Canonical terms are invalid: {canonicalFault}.");

        var quantity = instruction.Terms.Quantity;
        if (!Within(quantity, MinimumQuantity, MaximumQuantity))
            return Rejected(ExecutionAdmissionFault.QuantityOutOfRange, "The exact quantity is outside the adapter minimum/maximum.");
        if ((!SupportsFractionalQuantity && !quantity.TryGetWholeUnits(out _)) ||
            EffectiveScale(quantity.Coefficient, quantity.Scale) > QuantityPrecision ||
            !IsMultiple(quantity.Coefficient, quantity.Scale, LotSize.Coefficient, LotSize.Scale))
        {
            return Rejected(
                ExecutionAdmissionFault.QuantityNotRepresentable,
                "The exact quantity is not representable at the adapter precision and lot size.");
        }

        var priceFault = ValidatePrice(instruction.Terms.LimitPrice) ??
                         ValidatePrice(instruction.Terms.StopPrice);
        if (priceFault.HasValue)
            return priceFault.Value;

        return new ExecutionAdmissionResult(ExecutionAdmissionFault.None, instruction, null);
    }

    private bool IsValid()
    {
        if (string.IsNullOrWhiteSpace(Version) ||
            Version.Length > 256 ||
            QuantityPrecision > ScaledValueMath.MaximumScale ||
            PricePrecision > ScaledValueMath.MaximumScale ||
            !Enum.IsDefined(ReplaceSemantics) ||
            !MinimumQuantity.IsValid ||
            !MaximumQuantity.IsValid ||
            !LotSize.IsValid ||
            MinimumQuantity.Coefficient <= 0 ||
            MaximumQuantity.Coefficient <= 0 ||
            LotSize.Coefficient <= 0 ||
            !TickSize.IsValid ||
            TickSize.Coefficient <= 0 ||
            !TradingHours.IsValid ||
            !RateLimit.IsValid)
        {
            return false;
        }

        if (!ScaledValueMath.TryComparePositive(
                MinimumQuantity.Coefficient,
                MinimumQuantity.Scale,
                MaximumQuantity.Coefficient,
                MaximumQuantity.Scale,
                out var quantityComparison) ||
            quantityComparison > 0)
        {
            return false;
        }

        if (MinimumPrice is { } minimum && (!minimum.IsValid || minimum.Coefficient <= 0) ||
            MaximumPrice is { } maximum && (!maximum.IsValid || maximum.Coefficient <= 0))
        {
            return false;
        }

        return MinimumPrice is not { } min ||
               MaximumPrice is not { } max ||
               ScaledValueMath.TryComparePositive(
                   min.Coefficient,
                   min.Scale,
                   max.Coefficient,
                   max.Scale,
                   out var priceComparison) &&
               priceComparison <= 0;
    }

    private ExecutionAdmissionResult? ValidatePrice(ScaledPrice? value)
    {
        if (!value.HasValue)
            return null;

        var price = value.Value;
        if (MinimumPrice is { } minimum &&
            (!ScaledValueMath.TryComparePositive(
                price.Coefficient,
                price.Scale,
                minimum.Coefficient,
                minimum.Scale,
                out var minimumComparison) || minimumComparison < 0) ||
            MaximumPrice is { } maximum &&
            (!ScaledValueMath.TryComparePositive(
                price.Coefficient,
                price.Scale,
                maximum.Coefficient,
                maximum.Scale,
                out var maximumComparison) || maximumComparison > 0))
        {
            return Rejected(ExecutionAdmissionFault.PriceOutOfRange, "An exact order price is outside the adapter price band.");
        }

        if (EffectiveScale(price.Coefficient, price.Scale) > PricePrecision ||
            !IsMultiple(price.Coefficient, price.Scale, TickSize.Coefficient, TickSize.Scale))
        {
            return Rejected(
                ExecutionAdmissionFault.PriceNotRepresentable,
                "An exact order price is not representable at the adapter precision and tick size.");
        }

        return null;
    }

    private static bool Within(
        in ScaledQuantity value,
        in ScaledQuantity minimum,
        in ScaledQuantity maximum) =>
        ScaledValueMath.TryComparePositive(
            value.Coefficient,
            value.Scale,
            minimum.Coefficient,
            minimum.Scale,
            out var minimumComparison) &&
        minimumComparison >= 0 &&
        ScaledValueMath.TryComparePositive(
            value.Coefficient,
            value.Scale,
            maximum.Coefficient,
            maximum.Scale,
            out var maximumComparison) &&
        maximumComparison <= 0;

    private static byte EffectiveScale(long coefficient, byte scale)
    {
        Int128 normalized = coefficient;
        var normalizedScale = (int)scale;
        ScaledValueMath.Normalize(ref normalized, ref normalizedScale);
        return (byte)normalizedScale;
    }

    private static bool IsMultiple(
        long valueCoefficient,
        byte valueScale,
        long incrementCoefficient,
        byte incrementScale) =>
        valueCoefficient > 0 &&
        incrementCoefficient > 0 &&
        ScaledValueMath.TryAlign(
            valueCoefficient,
            valueScale,
            incrementCoefficient,
            incrementScale,
            out var value,
            out var increment,
            out _) &&
        value % increment == 0;

    private static ExecutionAdmissionResult Rejected(ExecutionAdmissionFault fault, string reason) =>
        new(fault, null, reason);
}

/// <summary>Combines independent session authorization with exact capability normalization.</summary>
public static class BrokerExecutionAdmission
{
    /// <summary>Evaluates one immutable session/capability snapshot without side effects.</summary>
    public static ExecutionAdmissionResult Evaluate(
        BrokerExecutionSession? session,
        BrokerExecutionCapabilities? capabilities,
        CanonicalOrderInstruction? instruction,
        DateTime utcNow,
        bool isReplace = false)
    {
        if (session is null || !session.Account.IsValid || session.ObservedAtUtc.Kind != DateTimeKind.Utc)
            return Rejected(ExecutionAdmissionFault.InvalidSession, "The adapter session identity or timestamp is invalid.");
        if (!session.IsDataConnected)
            return Rejected(ExecutionAdmissionFault.DataDisconnected, "Market data is disconnected.");
        if (!session.IsExecutionAuthenticated)
            return Rejected(
                ExecutionAdmissionFault.ExecutionNotAuthenticated,
                "The adapter is data-connected but not authenticated for execution.");
        if (!session.IsExecutionCertified)
            return Rejected(
                ExecutionAdmissionFault.ExecutionNotCertified,
                "The adapter/account is not certified for execution.");
        if (session.Health != ExecutionSessionHealth.Healthy)
            return Rejected(ExecutionAdmissionFault.SessionUnavailable, $"The execution session is {session.Health}.");
        if (capabilities is null)
            return Rejected(ExecutionAdmissionFault.InvalidCapabilities, "No execution capability snapshot is available.");
        return capabilities.Normalize(instruction, utcNow, isReplace);
    }

    private static ExecutionAdmissionResult Rejected(ExecutionAdmissionFault fault, string reason) =>
        new(fault, null, reason);
}

/// <summary>Stable identity of a local adapter dispatch receipt.</summary>
public readonly record struct DispatchReceiptId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>Kind of immutable command published through one account worker.</summary>
public enum BrokerAdapterCommandKind : byte
{
    /// <summary>Submit a new native order.</summary>
    Submit = 0,

    /// <summary>Cancel a known native order.</summary>
    Cancel = 1,

    /// <summary>Replace a known native order.</summary>
    Replace = 2,
}

/// <summary>Local proof that an immutable command crossed the adapter dispatch boundary.</summary>
public sealed record BrokerDispatchReceipt(
    DispatchReceiptId ReceiptId,
    BrokerExecutionAccount Account,
    BrokerAdapterCommandKind CommandKind,
    ClientOrderId ClientOrderId,
    CausationId CausationId,
    DateTime DispatchedAtUtc)
{
    /// <summary>Gets whether all receipt fields are valid and UTC.</summary>
    public bool IsValid =>
        ReceiptId.IsValid &&
        Account.IsValid &&
        Enum.IsDefined(CommandKind) &&
        ClientOrderId.IsValid &&
        CausationId.IsValid &&
        DispatchedAtUtc.Kind == DateTimeKind.Utc;

    /// <summary>Stable value persisted in the existing SubmissionRecorded event payload.</summary>
    public string ToLedgerValue() =>
        $"v1|{Encode(ReceiptId.Value)}|{Encode(Account.AdapterId.Value)}|{Encode(Account.AccountId.Value)}|{(byte)CommandKind}";

    /// <summary>Reads only the durable adapter/account binding from a version-1 receipt payload.</summary>
    public static bool TryReadAccountLedgerValue(string? value, out BrokerExecutionAccount account)
    {
        account = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var parts = value.Split('|');
        if (parts.Length != 5 || !string.Equals(parts[0], "v1", StringComparison.Ordinal))
            return false;
        try
        {
            account = new BrokerExecutionAccount(
                new ExecutionAdapterId(Decode(parts[2])),
                new BrokerAccountId(Decode(parts[3])));
            return account.IsValid;
        }
        catch (FormatException)
        {
            account = default;
            return false;
        }
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string Decode(string value) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(value));
}

/// <summary>Immutable submit command.</summary>
public sealed record BrokerSubmitCommand(
    CanonicalOrderInstruction Instruction,
    CausationId CausationId,
    string CapabilityVersion)
{
    // Opaque, one-use authority attached only by ExecutionCoordinator immediately before a
    // fully guarded LIVE dispatch. It is intentionally absent from the public constructor.
    internal object? LiveGuardrailAdmission { get; init; }
}

/// <summary>Exactly one client-id or broker-id lookup.</summary>
public readonly record struct BrokerOrderQuery(
    ClientOrderId? ClientOrderId,
    BrokerOrderId? BrokerOrderId)
{
    /// <summary>Creates a client-order-id lookup.</summary>
    public static BrokerOrderQuery ByClientId(ClientOrderId value) => new(value, null);

    /// <summary>Creates a broker-order-id lookup.</summary>
    public static BrokerOrderQuery ByBrokerId(BrokerOrderId value) => new(null, value);

    /// <summary>Gets whether exactly one valid lookup identity was supplied.</summary>
    public bool IsValid =>
        ClientOrderId.HasValue != BrokerOrderId.HasValue &&
        (!ClientOrderId.HasValue || ClientOrderId.Value.IsValid) &&
        (!BrokerOrderId.HasValue || BrokerOrderId.Value.IsValid);
}

/// <summary>Immutable cancel command supporting client-id or broker-id lookup.</summary>
public sealed record BrokerCancelCommand(BrokerOrderQuery Order, CausationId CausationId)
{
    // See BrokerSubmitCommand.LiveGuardrailAdmission.
    internal object? LiveGuardrailAdmission { get; init; }
}

/// <summary>Immutable replace command supporting client-id or broker-id lookup.</summary>
public sealed record BrokerReplaceCommand(
    BrokerOrderQuery Order,
    CanonicalOrderTerms ReplacementTerms,
    CausationId CausationId,
    string CapabilityVersion)
{
    // See BrokerSubmitCommand.LiveGuardrailAdmission.
    internal object? LiveGuardrailAdmission { get; init; }
}

/// <summary>Stable adapter rejection/failure mapping.</summary>
public enum BrokerAdapterCommandFault : byte
{
    /// <summary>No fault occurred.</summary>
    None = 0,

    /// <summary>The immutable command is structurally invalid.</summary>
    InvalidCommand = 1,

    /// <summary>The session cannot execute, including valid data-only states.</summary>
    ExecutionUnavailable = 2,

    /// <summary>The exact command cannot be represented without alteration.</summary>
    UnsupportedCapability = 3,

    /// <summary>The account's deterministic native command budget is exhausted.</summary>
    RateLimited = 4,

    /// <summary>No matching native order exists.</summary>
    OrderNotFound = 5,

    /// <summary>An idempotency or identity conflict was observed.</summary>
    Conflict = 6,

    /// <summary>The wrapped simulation rejected the command before dispatch.</summary>
    VenueRejected = 7,

    /// <summary>The wrapped simulation outcome is not provably known.</summary>
    OutcomeUnknown = 8,
}

/// <summary>Whether the adapter published a command beyond its local boundary.</summary>
public enum BrokerAdapterCommandStatus : byte
{
    /// <summary>A local receipt was produced and callbacks may follow asynchronously.</summary>
    Dispatched = 0,

    /// <summary>The command was rejected with proof that it was not dispatched.</summary>
    RejectedBeforeDispatch = 1,

    /// <summary>The command conflicts with a prior idempotent identity.</summary>
    Conflict = 2,
}

/// <summary>Value result returned before external order acknowledgement.</summary>
public sealed record BrokerAdapterCommandResult(
    BrokerAdapterCommandStatus Status,
    BrokerAdapterCommandFault Fault,
    BrokerDispatchReceipt? DispatchReceipt,
    int ScheduledEventCount,
    string? Reason)
{
    /// <summary>Gets whether one valid local dispatch receipt was produced.</summary>
    public bool IsDispatched =>
        Status == BrokerAdapterCommandStatus.Dispatched &&
        Fault == BrokerAdapterCommandFault.None &&
        DispatchReceipt is { IsValid: true };
}

/// <summary>Query result independent of a broker SDK.</summary>
public readonly record struct BrokerOrderQueryResult(
    bool Found,
    BrokerAdapterCommandFault Fault,
    VenueOrderSnapshot? Order,
    string? Reason = null);

/// <summary>Exact account position snapshot for reconciliation.</summary>
public sealed record BrokerPositionSnapshot(
    InstrumentId Instrument,
    ScaledQuantity Quantity,
    DateTime ObservedAtUtc);

/// <summary>Exact account cash snapshot for reconciliation.</summary>
public sealed record BrokerCashSnapshot(
    string Currency,
    ScaledMoney Total,
    ScaledMoney Available,
    DateTime ObservedAtUtc);

/// <summary>Point-in-time reconciliation snapshot exposed by every adapter.</summary>
public sealed record BrokerReconciliationSnapshot(
    BrokerExecutionAccount Account,
    DateTime CapturedAtUtc,
    IReadOnlyList<VenueOrderSnapshot> OpenOrders,
    IReadOnlyList<VenueOrderSnapshot> CompletedOrders,
    IReadOnlyList<BrokerPositionSnapshot> Positions,
    IReadOnlyList<BrokerCashSnapshot> Cash);

/// <summary>Stable asynchronous event identity.</summary>
public readonly record struct BrokerAdapterEventId(string Value)
{
    /// <summary>Gets whether the identity is populated and bounded.</summary>
    public bool IsValid => IdentityValue.IsValid(Value);
}

/// <summary>Base for asynchronous order, execution, commission, and position events.</summary>
public abstract record BrokerAdapterEvent(
    BrokerAdapterEventId EventId,
    BrokerExecutionAccount Account,
    ClientOrderId ClientOrderId,
    DateTime OccurredAtUtc);

/// <summary>Asynchronous non-fill order-state event.</summary>
public sealed record BrokerOrderEvent(
    BrokerAdapterEventId EventId,
    BrokerExecutionAccount Account,
    ClientOrderId ClientOrderId,
    DateTime OccurredAtUtc,
    VenueEvent VenueEvent)
    : BrokerAdapterEvent(EventId, Account, ClientOrderId, OccurredAtUtc);

/// <summary>Asynchronous exact fill event.</summary>
public sealed record BrokerExecutionEvent(
    BrokerAdapterEventId EventId,
    BrokerExecutionAccount Account,
    ClientOrderId ClientOrderId,
    DateTime OccurredAtUtc,
    VenueEvent VenueEvent)
    : BrokerAdapterEvent(EventId, Account, ClientOrderId, OccurredAtUtc);

/// <summary>
/// Asynchronous exact commission observation. The OMS continues to count the fee embedded in the
/// corresponding FillExecution; this separate event is reconciliation evidence, not a second fee.
/// </summary>
public sealed record BrokerCommissionEvent(
    BrokerAdapterEventId EventId,
    BrokerExecutionAccount Account,
    ClientOrderId ClientOrderId,
    DateTime OccurredAtUtc,
    CausationId CausationId,
    ScaledMoney Commission)
    : BrokerAdapterEvent(EventId, Account, ClientOrderId, OccurredAtUtc);

/// <summary>Asynchronous exact account-position observation.</summary>
public sealed record BrokerPositionEvent(
    BrokerAdapterEventId EventId,
    BrokerExecutionAccount Account,
    ClientOrderId ClientOrderId,
    DateTime OccurredAtUtc,
    CausationId CausationId,
    InstrumentId Instrument,
    ScaledQuantity Position)
    : BrokerAdapterEvent(EventId, Account, ClientOrderId, OccurredAtUtc);

/// <summary>
/// Formal broker execution seam. The contract contains no UI, infrastructure, SDK, socket, or
/// network type; slice 3 provides only the in-process simulated implementation.
/// </summary>
public interface IBrokerExecutionAdapter
{
    /// <summary>Gets the stable mode-neutral broker identity used by live confirmations.</summary>
    string BrokerId { get; }

    /// <summary>Gets the immutable paper/live environment selected for this connection.</summary>
    ExecutionMode Mode { get; }

    /// <summary>Gets the independently serialized adapter/account identity.</summary>
    BrokerExecutionAccount Account { get; }

    /// <summary>Gets the latest immutable connection/authentication observation.</summary>
    BrokerExecutionSession Session { get; }

    /// <summary>Gets the latest immutable exact execution capability snapshot.</summary>
    BrokerExecutionCapabilities Capabilities { get; }

    /// <summary>
    /// Raised asynchronously through the adapter's scheduler, never inline from submit, cancel, or
    /// replace. This preserves the local dispatch-receipt barrier.
    /// </summary>
    event Action<BrokerAdapterEvent>? EventReceived;

    /// <summary>Publishes one immutable submit command and returns before external acknowledgement.</summary>
    BrokerAdapterCommandResult Submit(BrokerSubmitCommand command);

    /// <summary>Publishes one immutable cancel command by client or broker id.</summary>
    BrokerAdapterCommandResult Cancel(BrokerCancelCommand command);

    /// <summary>Publishes one immutable replace command by client or broker id.</summary>
    BrokerAdapterCommandResult Replace(BrokerReplaceCommand command);

    /// <summary>Queries one order by client or broker id.</summary>
    BrokerOrderQueryResult Query(BrokerOrderQuery query);

    /// <summary>Captures open/completed orders, positions, and cash for later reconciliation.</summary>
    BrokerReconciliationSnapshot CaptureReconciliationSnapshot();
}
