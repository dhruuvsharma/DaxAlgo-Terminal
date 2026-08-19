using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;

namespace DaxAlgo.Sdk;

/// <summary>
/// Read-only market-data projection for the strategy's declared instrument set and data
/// requirement. Implementations reject instruments outside <see cref="Instruments"/> and clamp
/// every recent-data request to a host-owned bound. This contract exposes neither a broker feed,
/// the market-data hub/store, a source selector, nor another strategy's data.
/// </summary>
public interface IMarketDataView
{
    /// <summary>The complete canonical instrument set visible to this strategy or visualizer.</summary>
    IReadOnlySet<InstrumentId> Instruments { get; }

    /// <summary>The streams authorized for this projection.</summary>
    StrategyDataRequirement DataRequirement { get; }

    /// <summary>Returns at most <paramref name="maxCount"/> recent bars, oldest to newest.</summary>
    IReadOnlyList<OhlcvBar> RecentBars(InstrumentId instrument, BarSize size, int maxCount);

    /// <summary>Returns at most <paramref name="maxCount"/> recent quotes, oldest to newest.</summary>
    IReadOnlyList<Quote> RecentQuotes(InstrumentId instrument, int maxCount);

    /// <summary>Returns the latest authorized depth snapshot, or <see langword="null"/> when absent.</summary>
    DepthSnapshot? LatestDepth(InstrumentId instrument);

    /// <summary>Returns at most <paramref name="maxCount"/> recent trade prints, oldest to newest.</summary>
    IReadOnlyList<TradePrint> RecentTrades(InstrumentId instrument, int maxCount);
}

/// <summary>Read-only current values for the kernel's declared parameter schema.</summary>
public interface IParameters
{
    /// <summary>The declaration governing these values.</summary>
    StrategyParameterSchema Schema { get; }

    /// <summary>Reads a whole-number value.</summary>
    int GetInt(string name);

    /// <summary>Reads a 64-bit whole-number value.</summary>
    long GetLong(string name);

    /// <summary>Reads a real-number value.</summary>
    double GetDouble(string name);

    /// <summary>Reads a Boolean value.</summary>
    bool GetBool(string name);

    /// <summary>Reads a choice or text value.</summary>
    string GetString(string name);

    /// <summary>Reads a free-text value.</summary>
    string GetText(string name);

    /// <summary>Reads an enum-compatible choice value.</summary>
    TEnum GetEnum<TEnum>(string name) where TEnum : struct, System.Enum;

    /// <summary>Reads a canonical instrument value.</summary>
    InstrumentId GetInstrument(string name);
}

/// <summary>How a target position should be entered.</summary>
public enum VirtualEntryKind
{
    /// <summary>Take the position at the current price.</summary>
    Market = 0,

    /// <summary>Wait for a better price than the current one: below to buy, above to sell.</summary>
    Limit = 1,

    /// <summary>Wait for a worse price than the current one: above to buy, below to sell. A breakout.</summary>
    Stop = 2,
}

/// <summary>
/// A desired position in the strategy's private model portfolio. It is an intent only: it cannot
/// identify a broker, venue, account, or execution route.
///
/// <para>It may state an entry <em>price condition</em>, because that is an economic decision the
/// strategy owns - the same way it already states a protective stop and a profit target. It still
/// names nothing about where or how the order is routed.</para>
/// </summary>
/// <param name="Instrument">An instrument from the context's declared set.</param>
/// <param name="TargetUnits">The desired signed model position in reference units; zero means flat.</param>
/// <param name="ProtectiveStopPrice">Optional model-book protective stop.</param>
/// <param name="ProfitTargetPrice">Optional model-book profit target.</param>
/// <param name="EntryKind">How to enter: immediately, or resting until a price is reached.</param>
/// <param name="EntryTriggerPrice">
/// The price a non-market entry waits for. Required when <paramref name="EntryKind"/> is not
/// <see cref="VirtualEntryKind.Market"/>, and ignored when it is.
/// </param>
public sealed record VirtualTargetIntent(
    InstrumentId Instrument,
    double TargetUnits,
    double? ProtectiveStopPrice = null,
    double? ProfitTargetPrice = null,
    VirtualEntryKind EntryKind = VirtualEntryKind.Market,
    double? EntryTriggerPrice = null)
{
    /// <summary>True when the entry rests on a price rather than trading immediately.</summary>
    public bool IsPendingEntry =>
        EntryKind != VirtualEntryKind.Market && EntryTriggerPrice is > 0d;
}

/// <summary>
/// The strategy's only output capability. Submitted targets affect only its host-owned virtual
/// book; this surface cannot place, amend, cancel, or inspect real orders.
/// </summary>
public interface IVirtualBook
{
    /// <summary>Submits one target to the strategy's model portfolio.</summary>
    void SubmitTarget(VirtualTargetIntent intent);

    /// <summary>Convenience form for submitting a target position in reference units.</summary>
    void SetTargetPosition(
        InstrumentId instrument,
        double targetUnits,
        double? protectiveStopPrice = null,
        double? profitTargetPrice = null) =>
        SubmitTarget(new VirtualTargetIntent(
            instrument,
            targetUnits,
            protectiveStopPrice,
            profitTargetPrice));

    /// <summary>
    /// Submits a target that waits for <paramref name="triggerPrice"/> before entering: a buy limit
    /// or sell limit when <paramref name="kind"/> is <see cref="VirtualEntryKind.Limit"/>, a buy stop
    /// or sell stop when it is <see cref="VirtualEntryKind.Stop"/>. The direction comes from the sign
    /// of <paramref name="targetUnits"/>.
    ///
    /// <para>The host refuses a trigger on the side that would fire immediately - a buy limit at or
    /// above the current price, for instance - because that is a market order, not a pending one.</para>
    /// </summary>
    void SetPendingEntry(
        InstrumentId instrument,
        double targetUnits,
        VirtualEntryKind kind,
        double triggerPrice,
        double? protectiveStopPrice = null,
        double? profitTargetPrice = null) =>
        SubmitTarget(new VirtualTargetIntent(
            instrument,
            targetUnits,
            protectiveStopPrice,
            profitTargetPrice,
            kind,
            triggerPrice));
}

/// <summary>Host-rendered alert importance.</summary>
public enum AlertLevel
{
    /// <summary>Informational state that may not require action.</summary>
    Information,

    /// <summary>State that may require user attention.</summary>
    Warning,

    /// <summary>An error in strategy or visualizer computation.</summary>
    Error,

    /// <summary>Urgent state requiring attention.</summary>
    Critical,
}

/// <summary>Wire limits enforced by every host alert sink.</summary>
public static class AlertLimits
{
    /// <summary>Maximum alert message length in UTF-16 code units.</summary>
    public const int MaxMessageLength = 512;

    /// <summary>Maximum deduplication-key length in UTF-16 code units.</summary>
    public const int MaxDedupeKeyLength = 128;
}

/// <summary>
/// Bounded user-alert capability. The host limits message size and rate, mediates every destination,
/// and may throttle repeated alerts. A non-empty dedupe key expresses that equivalent alerts may be
/// coalesced; it never selects a transport or recipient.
/// </summary>
public interface IAlertSink
{
    /// <summary>
    /// Offers one bounded alert to the host for throttled, mediated delivery. Messages and keys
    /// must not exceed <see cref="AlertLimits.MaxMessageLength"/> and
    /// <see cref="AlertLimits.MaxDedupeKeyLength"/>, respectively.
    /// </summary>
    void Alert(string message, AlertLevel level, string? dedupeKey = null);

    /// <summary>Offers an alert only when <paramref name="condition"/> is true.</summary>
    void AlertIf(bool condition, string message, AlertLevel level, string? dedupeKey = null)
    {
        if (condition)
            Alert(message, level, dedupeKey);
    }
}

/// <summary>The complete capability set supplied to a sandboxed strategy kernel.</summary>
public interface IStrategyRuntimeContext
{
    /// <summary>The strategy-scoped market-data projection.</summary>
    IMarketDataView Data { get; }

    /// <summary>The deterministic host clock.</summary>
    IClock Clock { get; }

    /// <summary>The current read-only parameter values.</summary>
    IParameters Parameters { get; }

    /// <summary>The strategy's private model-portfolio output.</summary>
    IVirtualBook Book { get; }

    /// <summary>The host-mediated alert sink.</summary>
    IAlertSink Alerts { get; }
}

/// <summary>
/// The complete capability set supplied to a sandboxed visualizer. It intentionally has no virtual
/// book or other trading output — a visualizer draws, it does not trade.
/// </summary>
public interface IVisualizerContext
{
    /// <summary>The visualizer-scoped market-data projection.</summary>
    IMarketDataView Data { get; }

    /// <summary>The deterministic host clock.</summary>
    IClock Clock { get; }

    /// <summary>The current read-only parameter values.</summary>
    IParameters Parameters { get; }

    /// <summary>The host-mediated alert sink.</summary>
    IAlertSink Alerts { get; }
}
