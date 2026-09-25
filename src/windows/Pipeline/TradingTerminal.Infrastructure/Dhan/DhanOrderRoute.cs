using System.Globalization;
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

namespace TradingTerminal.Infrastructure.Dhan;

/// <summary>
/// DhanHQ v2 orders, with the client id and the day's access token the login window stores.
///
/// <para>Symbols are the market-data client's <c>SEGMENT:SECURITYID</c> (<c>NSE_EQ:2885</c>), which is exactly what
/// an order carries. Orders are delivery (<c>CNC</c>); stop is <c>STOP_LOSS_MARKET</c> and stop-limit
/// <c>STOP_LOSS</c>. The engine's id rides as <c>correlationId</c>. Orders report <c>filledQty</c> and
/// <c>averageTradedPrice</c>. The position is the holding plus the day's CNC position. Written 2026-09-25 from
/// the DhanHQ v2 reference; not yet run against a real account.</para>
/// </summary>
internal sealed class DhanOrderRoute : IndianOrderRoute
{
    private const string Host = "https://api.dhan.co/v2";

    public DhanOrderRoute(IBrokerCredentialSource credentials, ILogger<DhanOrderRoute> logger)
        : base(credentials, logger) { }

    internal DhanOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Dhan;
    public override string DisplayName => "Dhan";
    public override string RouteId => "dhan";

    private string ClientId => Credential.Account.Trim() is { Length: > 0 } id
        ? id
        : throw new BrokerOrderRouteException("Dhan: no client id is stored — enter it in the login window.", isRejection: true);

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        RequireLive(environment);
        var (token, client) = (Session, ClientId);
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Host + path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("access-token", token);
            request.Headers.TryAddWithoutValidation("client-id", client);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || Str(root, "errorCode").Length > 0 || Str(root, "status") == "failure")
            throw Refused(status, Words(root), text, status is >= 200 and < 300 ? true : null);
        return root;
    }

    /// <summary><c>{"errorType","errorCode","errorMessage"}</c>.</summary>
    internal static string? Words(JsonElement root) =>
        Str(root, "errorMessage") is { Length: > 0 } message
            ? Str(root, "errorCode") is { Length: > 0 } code ? $"{message} ({code})" : message
            : Str(root, "remarks") is { Length: > 0 } remarks ? remarks : null;

    private static (string Segment, string SecurityId) Parts(string symbol) => Split(symbol, "NSE_EQ");

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var funds = await CallAsync(environment, HttpMethod.Get, "/fundlimit", null, ct).ConfigureAwait(false);
        // Dhan's own spelling: "availabelBalance".
        var available = Dec(funds, "availabelBalance") is not 0 and var a ? a : Dec(funds, "availableBalance");
        return new RouteAccount(Str(funds, "dhanClientId") is { Length: > 0 } id ? id : ClientId, "INR", Dec(funds, "sodLimit"), available);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await PriceAsync(environment, symbol, ct).ConfigureAwait(false) is null
            ? throw new BrokerOrderRouteException($"Dhan does not know {symbol} — write it SEGMENT:SECURITYID, e.g. NSE_EQ:2885.", isRejection: true)
            : Delivery(symbol.Trim().ToUpperInvariant());

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Post, "/orders",
            SubmitBody(request, ClientId, Token(request.ClientOrderId, id => Tag(id, 25))), ct).ConfigureAwait(false);
        var id = Str(root, "orderId");
        if (id.Length == 0)
            throw new InvalidDataException($"Dhan answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string clientId, string correlationId)
    {
        var (segment, securityId) = Parts(request.Symbol);
        return new JsonObject
        {
            ["dhanClientId"] = clientId,
            ["correlationId"] = correlationId,
            ["transactionType"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["exchangeSegment"] = segment,
            ["productType"] = "CNC",
            ["orderType"] = request.Type switch
            {
                RouteOrderType.Limit => "LIMIT",
                RouteOrderType.Stop => "STOP_LOSS_MARKET",
                RouteOrderType.StopLimit => "STOP_LOSS",
                _ => "MARKET",
            },
            ["validity"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "IOC" : "DAY",
            ["securityId"] = securityId,
            ["quantity"] = request.Quantity,
            ["price"] = request.LimitPrice ?? 0m,
            ["triggerPrice"] = request.StopPrice ?? 0m,
            ["afterMarketOrder"] = false,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Delete, $"/orders/{Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, "/orders", null, ct).ConfigureAwait(false);
        return ReadOrders(root, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var root = await CallAsync(environment, HttpMethod.Get, $"/orders/{Uri.EscapeDataString(orderId)}", null, ct).ConfigureAwait(false);
        return ReadOrders(root, symbol).FirstOrDefault(o => o.OrderId == orderId);
    }

    /// <summary>An array of orders, or one order on its own.</summary>
    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement root, string symbol)
    {
        var (segment, securityId) = Parts(symbol);
        var rows = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToArray() : root.ValueKind == JsonValueKind.Object ? [root] : [];
        return [.. rows
            .Where(o => Str(o, "securityId") == securityId && string.Equals(Str(o, "exchangeSegment"), segment, StringComparison.OrdinalIgnoreCase))
            .Select(o => ReadOrder(o, symbol))];
    }

    /// <summary><c>{"orderId","correlationId","orderStatus","transactionType","exchangeSegment","orderType","validity","securityId","quantity",
    /// "filledQty","price","triggerPrice","averageTradedPrice","omsErrorDescription","updateTime","createTime"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filledQty");
        var status = Str(o, "orderStatus") switch
        {
            "TRADED" => RouteOrderStatus.Filled,
            "PART_TRADED" => RouteOrderStatus.PartiallyFilled,
            "CANCELLED" => RouteOrderStatus.Cancelled,
            "EXPIRED" => RouteOrderStatus.Expired,
            "REJECTED" => RouteOrderStatus.Rejected,
            "TRANSIT" => RouteOrderStatus.PendingNew,
            "PENDING" or "CONFIRM" or "TRIGGERED" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var time = Str(o, "updateTime") is { Length: > 0 } t ? t : Str(o, "createTime");
        return new RouteOrder(
            Str(o, "orderId"),
            EngineId(Str(o, "correlationId")),
            symbol,
            Str(o, "transactionType") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "orderType") switch
            {
                "LIMIT" => RouteOrderType.Limit,
                "STOP_LOSS_MARKET" => RouteOrderType.Stop,
                "STOP_LOSS" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "validity") == "IOC" ? RouteTimeInForce.ImmediateOrCancel : RouteTimeInForce.Day,
            Dec(o, "quantity"),
            Positive(o, "price"),
            Positive(o, "triggerPrice"),
            status,
            filled,
            Positive(o, "averageTradedPrice"),
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(o, "omsErrorDescription") is { Length: > 0 } reason ? reason : null,
            IndianTime.ToUtc(time, Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        JsonElement holdings;
        try
        {
            holdings = await CallAsync(environment, HttpMethod.Get, "/holdings", null, ct).ConfigureAwait(false);
        }
        catch (BrokerOrderRouteException exception) when (exception.Message.Contains("no holding", StringComparison.OrdinalIgnoreCase))
        {
            // Dhan answers an empty demat account with an error rather than an empty list.
            holdings = default;
        }

        var positions = await CallAsync(environment, HttpMethod.Get, "/positions", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>[{"securityId","exchange","totalQty"}]</c> plus CNC positions
    /// <c>[{"securityId","exchangeSegment","productType":"CNC","netQty"}]</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var (segment, securityId) = Parts(symbol);
        var held = holdings.ValueKind == JsonValueKind.Array
            ? holdings.EnumerateArray().Where(h => Str(h, "securityId") == securityId).Sum(h => Dec(h, "totalQty"))
            : 0m;
        var today = positions.ValueKind == JsonValueKind.Array
            ? positions.EnumerateArray()
                .Where(p => Str(p, "securityId") == securityId && string.Equals(Str(p, "exchangeSegment"), segment, StringComparison.OrdinalIgnoreCase) &&
                            Str(p, "productType") == "CNC")
                .Sum(p => Dec(p, "netQty"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (segment, securityId) = Parts(symbol);
        if (!long.TryParse(securityId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        var root = await CallAsync(environment, HttpMethod.Post, "/marketfeed/ltp", new JsonObject { [segment] = new JsonArray(id) }, ct).ConfigureAwait(false);
        return root.TryGetProperty("data", out var data) && data.TryGetProperty(segment, out var bySegment) &&
               bySegment.TryGetProperty(securityId, out var quote) && Positive(quote, "last_price") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
