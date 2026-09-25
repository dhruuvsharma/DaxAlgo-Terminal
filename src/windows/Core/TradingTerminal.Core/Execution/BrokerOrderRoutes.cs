using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Core.Execution;

// The order-routing seam (2026-09-25). A broker's order API is implemented in the infrastructure layer,
// which holds its signing and session code; the execution engine consumes it. Neither can see the other,
// so — like IBrokerClient, IBrokerCredentialSource and IBrokerSessionIssuer — the seam lives here.
//
// Numbers are decimal and native: a route speaks the broker's own quantities and prices. Turning them into
// the engine's exact whole-unit values is the engine's job (RoutedExecutionAdapter), done once for every
// broker rather than thirty-nine times.

/// <summary>Which of a broker's environments an order goes to.</summary>
public enum RouteEnvironment
{
    /// <summary>The broker's own paper, demo, sandbox or testnet environment — no money moves.</summary>
    Paper,

    /// <summary>The real account. Reached only through the execution engine's live gate.</summary>
    Live,
}

public enum RouteOrderType
{
    Market,
    Limit,
    Stop,
    StopLimit,
}

public enum RouteTimeInForce
{
    Day,
    GoodTillCancelled,
    ImmediateOrCancel,
    FillOrKill,
}

[Flags]
public enum RouteOrderTypes
{
    None = 0,
    Market = 1,
    Limit = 2,
    Stop = 4,
    StopLimit = 8,
}

[Flags]
public enum RouteTimesInForce
{
    None = 0,
    Day = 1,
    GoodTillCancelled = 2,
    ImmediateOrCancel = 4,
    FillOrKill = 8,
}

/// <summary>Where an order stands, in one vocabulary for every broker.</summary>
public enum RouteOrderStatus
{
    /// <summary>Sent, not yet acknowledged.</summary>
    PendingNew,

    /// <summary>Resting at the broker, nothing filled.</summary>
    Working,

    PartiallyFilled,

    Filled,

    Cancelled,

    Expired,

    Rejected,

    /// <summary>A cancel was requested and has not been confirmed.</summary>
    PendingCancel,

    /// <summary>The broker's status could not be read as any of the above.</summary>
    Unknown,
}

/// <summary>
/// The rules a broker applies to one instrument, as the route found them.
///
/// <para><c>Symbol</c> is the broker's symbol exactly as orders carry it. <c>UnitSize</c> is the native quantity
/// one engine unit stands for: the engine trades whole units, so this is 1 for shares and contracts and the
/// exchange's quantity step for crypto (0.00001 BTC) — a fill of 0.00030 BTC is then exactly 30 units.
/// <c>ValuePerPoint</c> is the account-currency value of one unit moving one price point (UnitSize for spot, the
/// contract multiplier for futures); risk sizing reads it. <c>TickSize</c> is the price grid — limit and stop
/// prices off it are refused before dispatch. <c>MinimumUnits</c>/<c>MaximumUnits</c> bound an order in engine
/// units, and <c>Currency</c> is what prices and cash are in.</para>
/// </summary>
public sealed record RouteInstrument(
    string Symbol,
    decimal UnitSize,
    decimal ValuePerPoint,
    decimal TickSize,
    long MinimumUnits,
    long MaximumUnits,
    RouteOrderTypes OrderTypes,
    RouteTimesInForce TimesInForce,
    bool SupportsReplace,
    string Currency)
{
    /// <summary>For spot crypto, the asset bought (<c>BTC</c> in BTCUSDT); empty otherwise. An exchange that
    /// charges its fee in this asset delivers less than was bought, and the engine adds the fee back when it
    /// compares its own fills with the balance.</summary>
    public string BaseAsset { get; init; } = string.Empty;

    /// <summary>Whether the rules are usable: positive sizes and a non-empty order vocabulary.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Symbol) &&
        UnitSize > 0 && ValuePerPoint > 0 && TickSize > 0 &&
        MinimumUnits > 0 && MaximumUnits >= MinimumUnits &&
        OrderTypes != RouteOrderTypes.None && TimesInForce != RouteTimesInForce.None &&
        !string.IsNullOrWhiteSpace(Currency);
}

/// <summary>The account an environment's credentials reach. <c>AccountId</c> is the broker's own account
/// identifier — what a live confirmation is bound to.</summary>
public sealed record RouteAccount(string AccountId, string Currency, decimal CashTotal, decimal CashAvailable);

/// <summary>One order to send, native quantities and prices.</summary>
public sealed record RouteOrderRequest(
    string Symbol,
    string ClientOrderId,
    OrderSide Side,
    RouteOrderType Type,
    RouteTimeInForce TimeInForce,
    decimal Quantity,
    decimal? LimitPrice,
    decimal? StopPrice);

/// <summary>An order as the broker reports it. Quantities, prices and fees are native and cumulative;
/// <c>Fee</c> is in <c>FeeCurrency</c>, zero (and the currency empty) when the broker does not report one.</summary>
public sealed record RouteOrder(
    string OrderId,
    string ClientOrderId,
    string Symbol,
    OrderSide Side,
    RouteOrderType Type,
    RouteTimeInForce TimeInForce,
    decimal Quantity,
    decimal? LimitPrice,
    decimal? StopPrice,
    RouteOrderStatus Status,
    decimal FilledQuantity,
    decimal? AveragePrice,
    decimal Fee,
    string FeeCurrency,
    string? Reason,
    DateTime UpdatedAtUtc);

/// <summary>A net position in one symbol, native quantity, signed (negative is short).</summary>
public sealed record RoutePosition(string Symbol, decimal Quantity);

/// <summary>A recent price for sizing and market-order risk checks.</summary>
public sealed record RoutePrice(decimal Price, DateTime ObservedAtUtc);

/// <summary>
/// A broker's order API, in one shape for every broker.
///
/// <para><b>What a route does not do.</b> It keeps no order state, applies no risk rule and never decides
/// whether an order may be sent — the execution engine does all of that, identically for every broker, and
/// only then calls <see cref="SubmitAsync"/>. A route translates and signs.</para>
///
/// <para><b>Credentials</b> come from <see cref="IBrokerCredentialSource"/>, read on each call — the same
/// keys and sessions the login window stores, so a session renewed there is used at once.</para>
///
/// <para><b>Failures.</b> A broker refusing an order throws <see cref="BrokerOrderRouteException"/> with
/// <see cref="BrokerOrderRouteException.IsRejection"/> set and the broker's own words; anything else — a
/// timeout, a dropped connection, a response nobody can read — is an outcome the engine must reconcile,
/// never a rejection, because the order may have reached the book.</para>
/// </summary>
public interface IBrokerOrderRoute
{
    BrokerKind Broker { get; }

    /// <summary>The broker's name, for cards and messages.</summary>
    string DisplayName { get; }

    /// <summary>Stable identity for confirmations and ledgers (<c>binance</c>, <c>charles-schwab</c>).</summary>
    string RouteId { get; }

    /// <summary>What the broker calls its non-money environment (<c>TESTNET</c>, <c>DEMO</c>, <c>SANDBOX</c>),
    /// or null when it has none — such a broker can only be traded live.</summary>
    string? PaperEnvironmentName { get; }

    /// <summary>Signs in (where needed) and reads the account the credentials reach. Throws when refused.</summary>
    Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct);

    /// <summary>The broker's rules for <paramref name="symbol"/>.</summary>
    Task<RouteInstrument> InstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    /// <summary>Sends one order. Returns the broker's acknowledgement, which carries its order id.</summary>
    Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct);

    /// <summary>Requests cancellation. Completion is observed through <see cref="OrdersAsync"/>.</summary>
    Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct);

    /// <summary>Changes quantity or prices of a working order in place. Throws <see cref="NotSupportedException"/>
    /// for a broker whose instrument reports no replace support.</summary>
    Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct);

    /// <summary>Open orders (and, where the broker lists them together, recently finished ones) for
    /// <paramref name="symbol"/>.</summary>
    Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    /// <summary>One order by the broker's id (or, where the broker supports it, the client id), whatever its
    /// state — how a fill is seen on an order that has already left the open-orders list. Null when the broker
    /// does not know it.</summary>
    Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct);

    /// <summary>The net position in <paramref name="symbol"/> — for spot crypto, the base-asset balance.</summary>
    Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    /// <summary>The account's cash as it stands now.</summary>
    Task<RouteAccount> AccountAsync(RouteEnvironment environment, CancellationToken ct);

    /// <summary>A recent price for <paramref name="symbol"/>, or null when the broker gives none.</summary>
    Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct);
}

/// <summary>A broker's answer to an order command that was not success.</summary>
public sealed class BrokerOrderRouteException : Exception
{
    public BrokerOrderRouteException(string message, bool isRejection, Exception? inner = null)
        : base(message, inner)
    {
        IsRejection = isRejection;
    }

    /// <summary>True when the broker definitely refused — the order is not on its book. False means the
    /// outcome is not known and must be reconciled.</summary>
    public bool IsRejection { get; }
}
