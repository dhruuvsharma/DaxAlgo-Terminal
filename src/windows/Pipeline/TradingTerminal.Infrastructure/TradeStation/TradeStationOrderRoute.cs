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

namespace TradingTerminal.Infrastructure.TradeStation;

/// <summary>
/// TradeStation v3 equity orders, over the session the login window signs in.
///
/// <para><b>Paper is TradeStation's SIM</b>: the same token reaches the SIM accounts through sim-api.tradestation.com,
/// so one sign-in serves both environments. The session must carry the <c>Trade</c> scope, which sign-ins before
/// 2026-09-25 did not ask for — TradeStation refuses their orders until the user signs in again.</para>
///
/// <para><b>Account.</b> The route trades the first active Margin account, then Cash — the account the live
/// confirmation names. Futures live in a separate Futures account and are refused rather than sent to the
/// equities one. Orders carry <c>BUY</c>, <c>SELL</c>, <c>SELLSHORT</c> or <c>BUYTOCOVER</c> from the
/// position held. TradeStation has no client order id, so an order is matched by the id the POST returns.
/// Replace keeps the order id and is on.</para>
///
/// <para>Written 2026-09-25 from TradeStation's v3 reference; not yet run against a real account.</para>
/// </summary>
internal sealed class TradeStationOrderRoute : KeptSessionOrderRoute
{
    private readonly TradeStationOptions _options;
    private readonly Dictionary<RouteEnvironment, string> _accounts = [];

    public TradeStationOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<TradeStationOptions> options, ILogger<TradeStationOrderRoute> logger)
        : base(BrokerKind.TradeStation, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal TradeStationOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, TradeStationOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.TradeStation, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.TradeStation;
    public override string DisplayName => "TradeStation";
    public override string RouteId => "tradestation";
    public override string? PaperEnvironmentName => "SIM";

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        TradeStationSignIn.TokenAsync(Http, _options.AuthBaseUrl,
            [new("grant_type", "refresh_token"), new("client_id", app.Key.Trim()), new("client_secret", app.Secret.Trim()),
             new("refresh_token", session.RefreshToken)], Now, session, ct);

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api.tradestation.com/v3" : "https://sim-api.tradestation.com/v3";

    private async Task<JsonElement> RequestAsync(RouteEnvironment environment, Func<KeptSession, HttpRequestMessage> build, CancellationToken ct)
    {
        var answer = await CallAsync(environment, build, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return answer.Root;
    }

    private Task<JsonElement> GetJsonAsync(RouteEnvironment environment, string path, CancellationToken ct) =>
        RequestAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get, Host(environment) + path), ct);

    /// <summary><c>{"Message"}</c>, <c>{"Errors":[{"Message"}]}</c> or <c>{"Orders":[{"Error","Message"}]}</c>.</summary>
    internal static string? Words(JsonElement root)
    {
        if (Str(root, "Message") is { Length: > 0 } message) return message;
        foreach (var list in new[] { "Errors", "Orders" })
            if (root.TryGetProperty(list, out var rows) && rows.ValueKind == JsonValueKind.Array)
                foreach (var row in rows.EnumerateArray())
                    if (Str(row, "Error").Length > 0 && Str(row, "Message") is { Length: > 0 } words)
                        return words;
        return null;
    }

    private async Task<string> AccountIdAsync(RouteEnvironment environment, CancellationToken ct)
    {
        lock (_accounts)
            if (_accounts.TryGetValue(environment, out var known)) return known;
        var root = await GetJsonAsync(environment, "/brokerage/accounts", ct).ConfigureAwait(false);
        var account = PickAccount(root)
            ?? throw new BrokerOrderRouteException($"TradeStation: the session reaches no active Margin or Cash account in {(environment == RouteEnvironment.Live ? "live" : "SIM")}.", isRejection: true);
        lock (_accounts) _accounts[environment] = account;
        return account;
    }

    /// <summary><c>{"Accounts":[{"AccountID","AccountType":"Margin|Cash|Futures|Crypto","Status":"Active"}]}</c>.</summary>
    internal static string? PickAccount(JsonElement root)
    {
        if (!root.TryGetProperty("Accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) return null;
        var active = accounts.EnumerateArray().Where(a => Str(a, "Status").Equals("Active", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var type in new[] { "Margin", "Cash" })
            foreach (var account in active)
                if (Str(account, "AccountType").Equals(type, StringComparison.OrdinalIgnoreCase))
                    return Str(account, "AccountID");
        return null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await GetJsonAsync(environment, $"/brokerage/accounts/{Uri.EscapeDataString(account)}/balances", ct).ConfigureAwait(false);
        var balance = root.TryGetProperty("Balances", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0] : default;
        return new RouteAccount(account, "USD", Dec(balance, "CashBalance"), Dec(balance, "BuyingPower"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await GetJsonAsync(environment, $"/marketdata/symbols/{Uri.EscapeDataString(symbol.Trim())}", ct).ConfigureAwait(false);
        return ReadInstrument(root, symbol.Trim())
            ?? throw new BrokerOrderRouteException($"TradeStation: {symbol} is not a stock this route trades ({Words(root) ?? "no symbol details"}).", isRejection: true);
    }

    /// <summary><c>{"Symbols":[{"Symbol","AssetType":"STOCK","PriceFormat":{"Increment","PointValue"},"QuantityFormat":{"Increment","MinimumTradeQuantity"}}]}</c>.
    /// Stocks only: anything else is refused, since it would need an account of another type.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("Symbols", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0) return null;
        var s = rows[0];
        if (Str(s, "AssetType") is not ("STOCK" or "ETF")) return null;
        var price = s.TryGetProperty("PriceFormat", out var pf) ? pf : default;
        var quantity = s.TryGetProperty("QuantityFormat", out var qf) ? qf : default;
        var tick = Dec(price, "Increment") is > 0 and var increment ? increment : 0.01m;
        var unit = Dec(quantity, "Increment") is > 0 and var step ? step : 1m;
        var point = Dec(price, "PointValue") is > 0 and var value ? value : 1m;
        return new RouteInstrument(
            Str(s, "Symbol") is { Length: > 0 } name ? name : symbol, unit, unit * point, tick,
            Units(Dec(quantity, "MinimumTradeQuantity"), unit, 1), 10_000_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: true, Str(s, "Currency") is { Length: > 0 } currency ? currency : "USD");
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var position = await PositionAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var body = SubmitBody(account, request, IntentFor(request.Side, request.Quantity, position.Quantity));
        var root = await RequestAsync(environment, _ => Json(HttpMethod.Post, $"{Host(environment)}/orderexecution/orders", body), ct).ConfigureAwait(false);
        var ack = root.TryGetProperty("Orders", out var orders) && orders.ValueKind == JsonValueKind.Array && orders.GetArrayLength() > 0 ? orders[0] : default;
        if (Str(ack, "Error").Length > 0)
            throw RefusedInBody(Str(ack, "Message"), root.GetRawText());
        var id = Str(ack, "OrderID");
        if (id.Length == 0)
            throw new InvalidDataException($"TradeStation answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(string account, RouteOrderRequest request, EquityIntent intent)
    {
        var body = new JsonObject
        {
            ["AccountID"] = account,
            ["Symbol"] = request.Symbol.Trim().ToUpperInvariant(),
            ["Quantity"] = Num(request.Quantity),
            ["OrderType"] = request.Type switch
            {
                RouteOrderType.Limit => "Limit",
                RouteOrderType.Stop => "StopMarket",
                RouteOrderType.StopLimit => "StopLimit",
                _ => "Market",
            },
            ["TradeAction"] = intent switch
            {
                EquityIntent.Buy => "BUY",
                EquityIntent.BuyToCover => "BUYTOCOVER",
                EquityIntent.SellShort => "SELLSHORT",
                _ => "SELL",
            },
            ["TimeInForce"] = new JsonObject
            {
                ["Duration"] = request.TimeInForce switch
                {
                    RouteTimeInForce.GoodTillCancelled => "GTC",
                    RouteTimeInForce.ImmediateOrCancel => "IOC",
                    RouteTimeInForce.FillOrKill => "FOK",
                    _ => "DAY",
                },
            },
            ["Route"] = "Intelligent",
        };
        if (request.LimitPrice is { } limit) body["LimitPrice"] = Num(limit);
        if (request.StopPrice is { } stop) body["StopPrice"] = Num(stop);
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await RequestAsync(environment, _ => new HttpRequestMessage(HttpMethod.Delete,
            $"{Host(environment)}/orderexecution/orders/{Uri.EscapeDataString(order.OrderId)}"), ct).ConfigureAwait(false);

    public override async Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct)
    {
        var body = new JsonObject { ["Quantity"] = Num(quantity) };
        if (limitPrice is { } limit) body["LimitPrice"] = Num(limit);
        if (stopPrice is { } stop) body["StopPrice"] = Num(stop);
        var root = await RequestAsync(environment, _ => Json(HttpMethod.Put,
            $"{Host(environment)}/orderexecution/orders/{Uri.EscapeDataString(order.OrderId)}", body), ct).ConfigureAwait(false);
        if (Str(root, "Error").Length > 0)
            throw RefusedInBody(Str(root, "Message"), root.GetRawText());
        return order with { Quantity = quantity, LimitPrice = limitPrice, StopPrice = stopPrice, UpdatedAtUtc = Now.UtcDateTime };
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await GetJsonAsync(environment, $"/brokerage/accounts/{Uri.EscapeDataString(account)}/orders", ct).ConfigureAwait(false);
        return ReadOrders(root, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var answer = await CallAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/brokerage/accounts/{Uri.EscapeDataString(account)}/orders/{Uri.EscapeDataString(orderId)}"), ct).ConfigureAwait(false);
        if (answer.Status == 404) return null;
        if (answer.Status is < 200 or >= 300) throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return ReadOrders(answer.Root, symbol).FirstOrDefault(o => o.OrderId == orderId);
    }

    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("Orders", out var orders) || orders.ValueKind != JsonValueKind.Array) return [];
        var list = new List<RouteOrder>();
        foreach (var o in orders.EnumerateArray())
        {
            var leg = o.TryGetProperty("Legs", out var legs) && legs.ValueKind == JsonValueKind.Array && legs.GetArrayLength() > 0 ? legs[0] : default;
            if (string.Equals(Str(leg, "Symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                list.Add(ReadOrder(o, leg, symbol));
        }

        return list;
    }

    /// <summary><c>{"OrderID","Status":"OPN|FLL|FPR|CAN|REJ|EXP|…","OrderType","Duration","LimitPrice","StopPrice","FilledPrice",
    /// "Legs":[{"Symbol","BuyOrSell","QuantityOrdered","ExecQuantity","ExecutionPrice"}],"CommissionFee","OpenedDateTime","ClosedDateTime","RejectReason"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, JsonElement leg, string symbol)
    {
        var filled = Dec(leg, "ExecQuantity");
        var status = Status(Str(o, "Status"), filled);
        var fee = Dec(o, "CommissionFee");
        var closed = Str(o, "ClosedDateTime");
        return new RouteOrder(
            Str(o, "OrderID"),
            string.Empty,
            symbol,
            Str(leg, "BuyOrSell").StartsWith("Buy", StringComparison.OrdinalIgnoreCase) ? OrderSide.Buy : OrderSide.Sell,
            Str(o, "OrderType") switch
            {
                "Limit" => RouteOrderType.Limit,
                "StopMarket" => RouteOrderType.Stop,
                "StopLimit" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "Duration") switch
            {
                "GTC" => RouteTimeInForce.GoodTillCancelled,
                "IOC" => RouteTimeInForce.ImmediateOrCancel,
                "FOK" => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            Dec(leg, "QuantityOrdered"),
            Positive(o, "LimitPrice"),
            Positive(o, "StopPrice"),
            status,
            filled,
            Positive(o, "FilledPrice") ?? Positive(leg, "ExecutionPrice"),
            fee,
            fee > 0 ? "USD" : string.Empty,
            Str(o, "RejectReason") is { Length: > 0 } reason ? reason : Str(o, "StatusDescription") is { Length: > 0 } d && status == RouteOrderStatus.Rejected ? d : null,
            ParseTime(closed.Length > 0 ? closed : Str(o, "OpenedDateTime"), Now.UtcDateTime));
    }

    /// <summary>TradeStation's three-letter statuses in the route's vocabulary.</summary>
    internal static RouteOrderStatus Status(string code, decimal filled) => code switch
    {
        "FLL" => RouteOrderStatus.Filled,
        "FPR" or "FLP" => RouteOrderStatus.PartiallyFilled,
        "CAN" or "OUT" or "BRC" or "TSC" => RouteOrderStatus.Cancelled,
        "EXP" => RouteOrderStatus.Expired,
        "REJ" or "BRO" or "DOA" or "LAT" => RouteOrderStatus.Rejected,
        "UCN" or "CSN" or "ECN" => RouteOrderStatus.PendingCancel,
        "ACK" or "DON" or "REC" or "PLA" or "OSO" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.PendingNew,
        "OPN" or "DIS" or "CHG" or "CND" or "STP" or "SUS" or "RPD" or "RSN" or "UCH" or "RJC" =>
            filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
        _ => RouteOrderStatus.Unknown,
    };

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await GetJsonAsync(environment, $"/brokerage/accounts/{Uri.EscapeDataString(account)}/positions", ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(root, symbol));
    }

    /// <summary><c>{"Positions":[{"Symbol","Quantity","LongShort":"Long|Short"}]}</c>; a short is made negative
    /// whether or not the quantity already is.</summary>
    internal static decimal ReadPosition(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("Positions", out var rows) || rows.ValueKind != JsonValueKind.Array) return 0m;
        var net = 0m;
        foreach (var p in rows.EnumerateArray())
        {
            if (!string.Equals(Str(p, "Symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var quantity = Math.Abs(Dec(p, "Quantity"));
            net += Str(p, "LongShort").Equals("Short", StringComparison.OrdinalIgnoreCase) ? -quantity : quantity;
        }

        return net;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await GetJsonAsync(environment, $"/marketdata/quotes/{Uri.EscapeDataString(symbol.Trim())}", ct).ConfigureAwait(false);
        var quote = root.TryGetProperty("Quotes", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0] : default;
        var price = Positive(quote, "Last") ?? (Positive(quote, "Bid") is { } bid && Positive(quote, "Ask") is { } ask ? (bid + ask) / 2 : null);
        return price is { } p ? new RoutePrice(p, ParseTime(Str(quote, "TradeTime"), Now.UtcDateTime)) : null;
    }
}
