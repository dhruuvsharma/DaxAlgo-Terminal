using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.AngelOne;

/// <summary>
/// Angel One SmartAPI orders, with the API key and the day's JWT the login window stores, and the headers
/// SmartAPI asks of every call.
///
/// <para>Symbols are the market-data client's <c>EXCHANGE:TOKEN</c> (<c>NSE:2885</c>). An order needs the trading
/// symbol too (<c>RELIANCE-EQ</c>), read once from the quote answer, which names it. Orders are delivery;
/// stop and stop-limit go as the <c>STOPLOSS</c> variety. The engine's id rides as <c>ordertag</c>. The order
/// book lists the day's orders with <c>filledshares</c> and <c>averageprice</c>; the position is the holding
/// (settled and T1) plus the day's delivery position. Written 2026-09-25 from SmartAPI's reference; not yet
/// run against a real account.</para>
/// </summary>
internal sealed class AngelOneOrderRoute : IndianOrderRoute
{
    private const string Host = "https://apiconnect.angelone.in";

    private readonly ConcurrentDictionary<string, string> _tradingSymbols = new(StringComparer.OrdinalIgnoreCase);

    public AngelOneOrderRoute(IBrokerCredentialSource credentials, ILogger<AngelOneOrderRoute> logger)
        : base(credentials, logger) { }

    internal AngelOneOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.AngelOne;
    public override string DisplayName => "Angel One";
    public override string RouteId => "angel-one";

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        RequireLive(environment);
        var jwt = AngelSession.Read(Session).Jwt;
        var key = Credential.Key.Trim();
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Host + path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            AngelHeaders.Apply(request, key, jwt);
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || AngelAnswer.IsRefusal(root))
            throw Refused(status, AngelAnswer.Words(root), text, status is >= 200 and < 300 ? true : null);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var profile = await CallAsync(environment, HttpMethod.Get, "/rest/secure/angelbroking/user/v1/getProfile", null, ct).ConfigureAwait(false);
        var rms = await CallAsync(environment, HttpMethod.Get, "/rest/secure/angelbroking/user/v1/getRMS", null, ct).ConfigureAwait(false);
        return new RouteAccount(Str(profile, "clientcode"), "INR", Dec(rms, "net"), Dec(rms, "availablecash"));
    }

    /// <summary>The quote answer for one token: <c>{"fetched":[{"exchange","tradingSymbol","symbolToken","ltp"}]}</c>.</summary>
    private async Task<JsonElement?> QuoteAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exchange, token) = Split(symbol);
        var data = await CallAsync(environment, HttpMethod.Post, "/rest/secure/angelbroking/market/v1/quote/",
            new JsonObject { ["mode"] = "LTP", ["exchangeTokens"] = new JsonObject { [exchange] = new JsonArray(token) } }, ct).ConfigureAwait(false);
        var row = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("fetched", out var fetched) && fetched.ValueKind == JsonValueKind.Array
            ? fetched.EnumerateArray().FirstOrDefault(r => Str(r, "symbolToken") == token)
            : default;
        if (row.ValueKind != JsonValueKind.Object) return null;
        if (Str(row, "tradingSymbol") is { Length: > 0 } tradingSymbol) _tradingSymbols[symbol.Trim()] = tradingSymbol;
        return row;
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await QuoteAsync(environment, symbol, ct).ConfigureAwait(false) is null
            ? throw new BrokerOrderRouteException($"Angel One does not know {symbol} — write it EXCHANGE:TOKEN, e.g. NSE:2885.", isRejection: true)
            : Delivery(symbol.Trim().ToUpperInvariant());

    private async Task<string> TradingSymbolAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        if (_tradingSymbols.TryGetValue(symbol.Trim(), out var known)) return known;
        _ = await QuoteAsync(environment, symbol, ct).ConfigureAwait(false);
        return _tradingSymbols.TryGetValue(symbol.Trim(), out known)
            ? known
            : throw new BrokerOrderRouteException($"Angel One named no trading symbol for {symbol}.", isRejection: true);
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var tradingSymbol = await TradingSymbolAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var data = await CallAsync(environment, HttpMethod.Post, "/rest/secure/angelbroking/order/v1/placeOrder",
            SubmitBody(request, tradingSymbol, Token(request.ClientOrderId, id => Alphanumeric(id, 20))), ct).ConfigureAwait(false);
        var id = Str(data, "orderid");
        if (id.Length == 0)
            throw new InvalidDataException($"Angel One answered the order without an id: {SignInProof.Snippet(data.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string tradingSymbol, string tag)
    {
        var (exchange, token) = Split(request.Symbol);
        var stop = request.Type is RouteOrderType.Stop or RouteOrderType.StopLimit;
        return new JsonObject
        {
            ["variety"] = stop ? "STOPLOSS" : "NORMAL",
            ["tradingsymbol"] = tradingSymbol,
            ["symboltoken"] = token,
            ["transactiontype"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["exchange"] = exchange,
            ["ordertype"] = request.Type switch
            {
                RouteOrderType.Limit => "LIMIT",
                RouteOrderType.Stop => "STOPLOSS_MARKET",
                RouteOrderType.StopLimit => "STOPLOSS_LIMIT",
                _ => "MARKET",
            },
            ["producttype"] = "DELIVERY",
            ["duration"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "IOC" : "DAY",
            ["price"] = Num(request.LimitPrice ?? 0m),
            ["triggerprice"] = Num(request.StopPrice ?? 0m),
            ["quantity"] = Num(request.Quantity),
            ["ordertag"] = tag,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Post, "/rest/secure/angelbroking/order/v1/cancelOrder",
            new JsonObject
            {
                ["variety"] = order.Type is RouteOrderType.Stop or RouteOrderType.StopLimit ? "STOPLOSS" : "NORMAL",
                ["orderid"] = order.OrderId,
            }, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Get, "/rest/secure/angelbroking/order/v1/getOrderBook", null, ct).ConfigureAwait(false);
        var (exchange, token) = Split(symbol);
        return data.ValueKind == JsonValueKind.Array
            ? [.. data.EnumerateArray()
                .Where(o => Str(o, "symboltoken") == token && string.Equals(Str(o, "exchange"), exchange, StringComparison.OrdinalIgnoreCase))
                .Select(o => ReadOrder(o, symbol))]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
        (await OrdersAsync(environment, symbol, ct).ConfigureAwait(false)).FirstOrDefault(o => o.OrderId == orderId);

    /// <summary><c>{"orderid","status","symboltoken","exchange","transactiontype","ordertype","duration","quantity","filledshares",
    /// "price","triggerprice","averageprice","text","ordertag","updatetime"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filledshares");
        var status = Str(o, "status").ToLowerInvariant() switch
        {
            "complete" => RouteOrderStatus.Filled,
            "cancelled" => RouteOrderStatus.Cancelled,
            "rejected" => RouteOrderStatus.Rejected,
            "cancel pending" => RouteOrderStatus.PendingCancel,
            "put order req received" or "validation pending" or "open pending" or "after market order req received" => RouteOrderStatus.PendingNew,
            "open" or "trigger pending" or "modified" or "modify pending" or "modify validation pending" =>
                filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        return new RouteOrder(
            Str(o, "orderid"),
            EngineId(Str(o, "ordertag")),
            symbol,
            Str(o, "transactiontype") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "ordertype") switch
            {
                "LIMIT" => RouteOrderType.Limit,
                "STOPLOSS_MARKET" => RouteOrderType.Stop,
                "STOPLOSS_LIMIT" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "duration") == "IOC" ? RouteTimeInForce.ImmediateOrCancel : RouteTimeInForce.Day,
            Dec(o, "quantity"),
            Positive(o, "price"),
            Positive(o, "triggerprice"),
            status,
            filled,
            Positive(o, "averageprice"),
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(o, "text") is { Length: > 0 } reason ? reason : null,
            IndianTime.ToUtc(Str(o, "updatetime"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var holdings = await CallAsync(environment, HttpMethod.Get, "/rest/secure/angelbroking/portfolio/v1/getHolding", null, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, HttpMethod.Get, "/rest/secure/angelbroking/order/v1/getPosition", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>[{"exchange","symboltoken","quantity","t1quantity"}]</c> plus delivery positions
    /// <c>[{"exchange","symboltoken","producttype":"DELIVERY","netqty"}]</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var (exchange, token) = Split(symbol);
        bool Matches(JsonElement row) => Str(row, "symboltoken") == token && string.Equals(Str(row, "exchange"), exchange, StringComparison.OrdinalIgnoreCase);
        var held = holdings.ValueKind == JsonValueKind.Array ? holdings.EnumerateArray().Where(Matches).Sum(h => Dec(h, "quantity") + Dec(h, "t1quantity")) : 0m;
        var today = positions.ValueKind == JsonValueKind.Array
            ? positions.EnumerateArray().Where(p => Matches(p) && Str(p, "producttype") == "DELIVERY").Sum(p => Dec(p, "netqty"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await QuoteAsync(environment, symbol, ct).ConfigureAwait(false) is { } row && Positive(row, "ltp") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
}
