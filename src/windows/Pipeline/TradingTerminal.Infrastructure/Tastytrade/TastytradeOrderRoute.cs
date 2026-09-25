using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Tastytrade;

/// <summary>
/// tastytrade equity orders, over the session the login window signs in.
///
/// <para><b>Paper is tastytrade's sandbox</b> (api.cert.tastyworks.com), which has its own login and accounts;
/// a session is signed into whichever host <c>Tastytrade:RestBaseUrl</c> names, and a card for the other
/// environment is refused with that said.</para>
///
/// <para><b>Legs.</b> Every order is a list of legs, each with an action that states whether it opens or
/// closes — <c>Buy to Open</c>, <c>Sell to Close</c>, <c>Sell to Open</c>, <c>Buy to Close</c> — read from the
/// position held. A limit price carries a <c>price-effect</c> (a buy is a debit, a sell a credit). Fills are
/// listed per leg, and the average price is weighted from them. tastytrade has no client order id, and a
/// replace is a full re-statement of the order, so replace is off. Equities only; futures are refused.</para>
///
/// <para>Written 2026-09-25 from tastytrade's open API reference; not yet run against a real account.</para>
/// </summary>
internal sealed class TastytradeOrderRoute : KeptSessionOrderRoute
{
    private const string LiveHost = "https://api.tastyworks.com";
    private const string SandboxHost = "https://api.cert.tastyworks.com";

    private readonly TastytradeOptions _options;
    private string _account = string.Empty;

    public TastytradeOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<TastytradeOptions> options, ILogger<TastytradeOrderRoute> logger)
        : base(BrokerKind.Tastytrade, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal TastytradeOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, TastytradeOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.Tastytrade, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.Tastytrade;
    public override string DisplayName => "tastytrade";
    public override string RouteId => "tastytrade";
    public override string? PaperEnvironmentName => "SANDBOX";

    /// <summary>The sandbox when the configured host is the cert host.</summary>
    protected override RouteEnvironment? SessionEnvironment =>
        _options.RestBaseUrl.Contains(".cert.", StringComparison.OrdinalIgnoreCase) ? RouteEnvironment.Paper : RouteEnvironment.Live;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        TastytradeSignIn.RefreshAsync(Http, _options.RestBaseUrl, app, session.RefreshToken, Now, ct);

    private static string Host(RouteEnvironment environment) => environment == RouteEnvironment.Live ? LiveHost : SandboxHost;

    private async Task<JsonElement> RequestAsync(RouteEnvironment environment, Func<KeptSession, HttpRequestMessage> build, CancellationToken ct)
    {
        var answer = await CallAsync(environment, build, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return answer.Root.TryGetProperty("data", out var data) ? data : answer.Root;
    }

    private Task<JsonElement> GetJsonAsync(RouteEnvironment environment, string path, CancellationToken ct) =>
        RequestAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get, Host(environment) + path), ct);

    /// <summary><c>{"error":{"code","message","errors":[{"code","message"}]}}</c> — the detail rows say why.</summary>
    internal static string? Words(JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error)) return null;
        var parts = new List<string> { Str(error, "message") };
        if (error.TryGetProperty("errors", out var rows) && rows.ValueKind == JsonValueKind.Array)
            parts.AddRange(rows.EnumerateArray().Select(r => Str(r, "message")));
        var words = string.Join("; ", parts.Where(p => p.Length > 0));
        return words.Length > 0 ? words : null;
    }

    private static IEnumerable<JsonElement> Items(JsonElement data) =>
        data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array ? items.EnumerateArray() : [];

    private async Task<string> AccountNumberAsync(RouteEnvironment environment, CancellationToken ct)
    {
        if (_account.Length > 0) return _account;
        var data = await GetJsonAsync(environment, "/customers/me/accounts", ct).ConfigureAwait(false);
        _account = PickAccount(data)
            ?? throw new BrokerOrderRouteException("tastytrade: the session reaches no open account.", isRejection: true);
        return _account;
    }

    /// <summary><c>{"items":[{"account":{"account-number","is-closed"},"authority-level"}]}</c> — the first open account.</summary>
    internal static string? PickAccount(JsonElement data)
    {
        foreach (var item in Items(data))
        {
            var account = item.TryGetProperty("account", out var a) ? a : item;
            if (account.TryGetProperty("is-closed", out var closed) && closed.ValueKind == JsonValueKind.True) continue;
            if (Str(account, "account-number") is { Length: > 0 } number) return number;
        }

        return null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var data = await GetJsonAsync(environment, $"/accounts/{account}/balances", ct).ConfigureAwait(false);
        return new RouteAccount(account, "USD", Dec(data, "cash-balance"), Dec(data, "equity-buying-power"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        if (symbol.TrimStart().StartsWith('/'))
            throw new BrokerOrderRouteException($"tastytrade: {symbol} is a future; this route trades equities.", isRejection: true);
        var data = await GetJsonAsync(environment, $"/instruments/equities/{Uri.EscapeDataString(symbol.Trim())}", ct).ConfigureAwait(false);
        var price = await PriceAsync(environment, symbol, ct).ConfigureAwait(false);
        return ReadInstrument(data, symbol.Trim(), price?.Price)
            ?? throw new BrokerOrderRouteException($"tastytrade: {symbol} is not an active equity.", isRejection: true);
    }

    /// <summary><c>{"symbol","active","is-closing-only","tick-sizes":[{"value":"0.0001","threshold":"1.0"},{"value":"0.01"}]}</c> —
    /// the tick is the first whose threshold lies above the price, else the last.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement data, string symbol, decimal? price)
    {
        if (Str(data, "symbol").Length == 0 || (data.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False))
            return null;
        var tick = 0.01m;
        if (data.TryGetProperty("tick-sizes", out var ticks) && ticks.ValueKind == JsonValueKind.Array && ticks.GetArrayLength() > 0)
        {
            tick = Dec(ticks[ticks.GetArrayLength() - 1], "value");
            if (price is { } p)
                foreach (var row in ticks.EnumerateArray())
                    if (Dec(row, "threshold") is > 0 and var threshold && p < threshold)
                    {
                        tick = Dec(row, "value");
                        break;
                    }
        }

        return new RouteInstrument(Str(data, "symbol"), 1m, 1m, tick > 0 ? tick : 0.01m, 1, 10_000_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel,
            SupportsReplace: false, "USD");
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var position = await PositionAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var body = SubmitBody(request, IntentFor(request.Side, request.Quantity, position.Quantity));
        var data = await RequestAsync(environment, _ => Json(HttpMethod.Post, $"{Host(environment)}/accounts/{account}/orders", body), ct).ConfigureAwait(false);
        var order = data.TryGetProperty("order", out var o) ? o : default;
        if (Str(order, "id").Length == 0)
            throw new InvalidDataException($"tastytrade answered the order without an id: {SignInProof.Snippet(data.GetRawText())}");
        return ReadOrder(order, request.Symbol) with { ClientOrderId = request.ClientOrderId };
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, EquityIntent intent)
    {
        var body = new JsonObject
        {
            ["time-in-force"] = request.TimeInForce switch
            {
                RouteTimeInForce.GoodTillCancelled => "GTC",
                RouteTimeInForce.ImmediateOrCancel => "IOC",
                _ => "Day",
            },
            ["order-type"] = request.Type switch
            {
                RouteOrderType.Limit => "Limit",
                RouteOrderType.Stop => "Stop",
                RouteOrderType.StopLimit => "Stop Limit",
                _ => "Market",
            },
            ["legs"] = new JsonArray(new JsonObject
            {
                ["instrument-type"] = "Equity",
                ["symbol"] = request.Symbol.Trim().ToUpperInvariant(),
                ["quantity"] = Num(request.Quantity),
                ["action"] = intent switch
                {
                    EquityIntent.Buy => "Buy to Open",
                    EquityIntent.BuyToCover => "Buy to Close",
                    EquityIntent.SellShort => "Sell to Open",
                    _ => "Sell to Close",
                },
            }),
        };
        if (request.LimitPrice is { } limit)
        {
            body["price"] = Num(limit);
            body["price-effect"] = request.Side == OrderSide.Buy ? "Debit" : "Credit";
        }

        if (request.StopPrice is { } stop) body["stop-trigger"] = Num(stop);
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        _ = await RequestAsync(environment, _ => new HttpRequestMessage(HttpMethod.Delete,
            $"{Host(environment)}/accounts/{account}/orders/{Uri.EscapeDataString(order.OrderId)}"), ct).ConfigureAwait(false);
    }

    /// <summary>Today's orders and every open one.</summary>
    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var data = await GetJsonAsync(environment, $"/accounts/{account}/orders/live", ct).ConfigureAwait(false);
        return [.. Items(data).Where(o => string.Equals(LegSymbol(o), symbol.Trim(), StringComparison.OrdinalIgnoreCase)).Select(o => ReadOrder(o, symbol))];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var answer = await CallAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/accounts/{account}/orders/{Uri.EscapeDataString(orderId)}"), ct).ConfigureAwait(false);
        if (answer.Status == 404) return null;
        if (answer.Status is < 200 or >= 300) throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return answer.Root.TryGetProperty("data", out var data) ? ReadOrder(data, symbol) : null;
    }

    private static JsonElement FirstLeg(JsonElement o) =>
        o.TryGetProperty("legs", out var legs) && legs.ValueKind == JsonValueKind.Array && legs.GetArrayLength() > 0 ? legs[0] : default;

    private static string LegSymbol(JsonElement o) => Str(FirstLeg(o), "symbol");

    /// <summary><c>{"id","status","order-type","time-in-force","size","price","stop-trigger","reject-reason","updated-at",
    /// "legs":[{"symbol","quantity","remaining-quantity","action","fills":[{"quantity","fill-price"}]}]}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var leg = FirstLeg(o);
        var (filled, notional) = (0m, 0m);
        if (leg.ValueKind == JsonValueKind.Object && leg.TryGetProperty("fills", out var fills) && fills.ValueKind == JsonValueKind.Array)
            foreach (var fill in fills.EnumerateArray())
            {
                filled += Dec(fill, "quantity");
                notional += Dec(fill, "quantity") * Dec(fill, "fill-price");
            }

        var quantity = Dec(leg, "quantity") is > 0 and var q ? q : Dec(o, "size");
        var status = Str(o, "status") switch
        {
            "Filled" => RouteOrderStatus.Filled,
            "Cancelled" or "Removed" or "Partially Removed" => RouteOrderStatus.Cancelled,
            "Expired" => RouteOrderStatus.Expired,
            "Rejected" => RouteOrderStatus.Rejected,
            "Cancel Requested" => RouteOrderStatus.PendingCancel,
            "Received" or "In Flight" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.PendingNew,
            "Routed" or "Live" or "Contingent" or "Replace Requested" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var action = Str(leg, "action");
        return new RouteOrder(
            Str(o, "id"),
            string.Empty,
            symbol,
            action.StartsWith("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
            Str(o, "order-type") switch
            {
                "Limit" => RouteOrderType.Limit,
                "Stop" => RouteOrderType.Stop,
                "Stop Limit" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "time-in-force") switch
            {
                "GTC" or "GTC Ext" => RouteTimeInForce.GoodTillCancelled,
                "IOC" => RouteTimeInForce.ImmediateOrCancel,
                _ => RouteTimeInForce.Day,
            },
            quantity,
            Positive(o, "price"),
            Positive(o, "stop-trigger"),
            status,
            filled,
            filled > 0 ? notional / filled : null,
            0m,
            string.Empty,
            Str(o, "reject-reason") is { Length: > 0 } reason ? reason : null,
            ParseTime(Str(o, "updated-at"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var data = await GetJsonAsync(environment, $"/accounts/{account}/positions", ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(data, symbol));
    }

    /// <summary><c>{"items":[{"symbol","quantity","quantity-direction":"Long|Short|Zero"}]}</c>.</summary>
    internal static decimal ReadPosition(JsonElement data, string symbol) =>
        Items(data)
            .Where(p => string.Equals(Str(p, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            .Sum(p => Str(p, "quantity-direction") switch
            {
                "Short" => -Math.Abs(Dec(p, "quantity")),
                "Zero" => 0m,
                _ => Math.Abs(Dec(p, "quantity")),
            });

    /// <summary>A quote from <c>/market-data/by-type</c>; null when that endpoint is not open to the account —
    /// the live quotes stream over DXLink, which this route does not open.</summary>
    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var answer = await CallAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/market-data/by-type?equity={Uri.EscapeDataString(symbol.Trim())}"), ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300 || !answer.Root.TryGetProperty("data", out var data)) return null;
        var quote = Items(data).FirstOrDefault();
        var price = Positive(quote, "last") ?? Positive(quote, "mark")
            ?? (Positive(quote, "bid") is { } bid && Positive(quote, "ask") is { } ask ? (bid + ask) / 2 : null);
        return price is { } p ? new RoutePrice(p, ParseTime(Str(quote, "updated-at"), Now.UtcDateTime)) : null;
    }
}
