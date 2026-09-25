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

namespace TradingTerminal.Infrastructure.AliceBlue;

/// <summary>
/// Alice Blue orders over the ANT REST API the market-data client uses, with the user id and the day's
/// session the login window stores (<c>Authorization: Bearer USERID SESSION</c>).
///
/// <para><b>Which API.</b> Alice Blue has since published a new open API on another host with other field
/// names. This route stays on ANT because that is the API the stored session was issued for; its field names
/// are the ones Alice Blue's own Python client (pya3) reads, and are the least certain of any route here.</para>
///
/// <para>Symbols are the market-data client's <c>EXCHANGE:TOKEN</c> (<c>NSE:2885</c>); an order needs the trading
/// symbol too, read once from the scrip quote (<c>TSymbl</c>). Orders are delivery (<c>CNC</c>). ANT answers a
/// refusal as <c>{"stat":"Not_Ok","emsg"}</c>, often under HTTP 200. Written 2026-09-25; not yet run against a
/// real account.</para>
/// </summary>
internal sealed class AliceBlueOrderRoute : IndianOrderRoute
{
    private const string Root = "https://ant.aliceblueonline.com/rest/AliceBlueAPIService/api";

    private readonly ConcurrentDictionary<string, string> _tradingSymbols = new(StringComparer.OrdinalIgnoreCase);

    public AliceBlueOrderRoute(IBrokerCredentialSource credentials, ILogger<AliceBlueOrderRoute> logger)
        : base(credentials, logger) { }

    internal AliceBlueOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.AliceBlue;
    public override string DisplayName => "Alice Blue";
    public override string RouteId => "alice-blue";

    private string UserId => Credential.Account.Trim().ToUpperInvariant() is { Length: > 0 } user
        ? user
        : throw new BrokerOrderRouteException("Alice Blue: no user id is stored — sign in in the login window.", isRejection: true);

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        RequireLive(environment);
        var authorization = $"Bearer {UserId} {Session}";
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, $"{Root}/{path}");
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return request;
        }, ct).ConfigureAwait(false);
        var first = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
        if (status is < 200 or >= 300 || Str(first, "stat") == "Not_Ok")
        {
            // An empty book or position list is reported as a refusal; it is not one.
            if (status is >= 200 and < 300 && Str(first, "emsg") is { } empty && empty.Contains("no data", StringComparison.OrdinalIgnoreCase))
                return JsonDocument.Parse("[]").RootElement.Clone();
            throw Refused(status, Str(first, "emsg"), text, status is >= 200 and < 300 ? true : null);
        }

        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var limits = await CallAsync(environment, HttpMethod.Get, "limits/getRmsLimits", null, ct).ConfigureAwait(false);
        var row = limits.ValueKind == JsonValueKind.Array && limits.GetArrayLength() > 0 ? limits[0] : limits;
        return new RouteAccount(UserId, "INR", Dec(row, "net"), Dec(row, "cashmarginavailable"));
    }

    private async Task<JsonElement?> QuoteAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exchange, token) = Split(symbol);
        var root = await CallAsync(environment, HttpMethod.Post, "ScripDetails/getScripQuoteDetails",
            new JsonObject { ["exch"] = exchange, ["symbol"] = token }, ct).ConfigureAwait(false);
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Str(root, "TSymbl") is { Length: > 0 } tradingSymbol) _tradingSymbols[symbol.Trim()] = tradingSymbol;
        return root;
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await QuoteAsync(environment, symbol, ct).ConfigureAwait(false) is null || !_tradingSymbols.ContainsKey(symbol.Trim())
            ? throw new BrokerOrderRouteException($"Alice Blue does not know {symbol} — write it EXCHANGE:TOKEN, e.g. NSE:2885.", isRejection: true)
            : Delivery(symbol.Trim().ToUpperInvariant());

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        if (!_tradingSymbols.ContainsKey(request.Symbol.Trim()))
            _ = await QuoteAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        if (!_tradingSymbols.TryGetValue(request.Symbol.Trim(), out var tradingSymbol))
            throw new BrokerOrderRouteException($"Alice Blue named no trading symbol for {request.Symbol}.", isRejection: true);

        var root = await CallAsync(environment, HttpMethod.Post, "placeOrder/executePlaceOrder",
            new JsonArray(SubmitBody(request, tradingSymbol, Token(request.ClientOrderId, id => Alphanumeric(id, 20)))), ct).ConfigureAwait(false);
        var ack = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 ? root[0] : root;
        var id = Str(ack, "NOrdNo");
        if (id.Length == 0)
            throw new InvalidDataException($"Alice Blue answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string tradingSymbol, string tag)
    {
        var (exchange, token) = Split(request.Symbol);
        return new JsonObject
        {
            ["complexty"] = "regular",
            ["discqty"] = "0",
            ["exch"] = exchange,
            ["pCode"] = "CNC",
            ["prctyp"] = request.Type switch
            {
                RouteOrderType.Limit => "L",
                RouteOrderType.Stop => "SL-M",
                RouteOrderType.StopLimit => "SL",
                _ => "MKT",
            },
            ["price"] = Num(request.LimitPrice ?? 0m),
            ["qty"] = request.Quantity,
            ["ret"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "IOC" : "DAY",
            ["symbol_id"] = token,
            ["trading_symbol"] = tradingSymbol,
            ["transtype"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["trigPrice"] = request.StopPrice is { } stop ? Num(stop) : string.Empty,
            ["orderTag"] = tag,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var (exchange, _) = Split(order.Symbol);
        _tradingSymbols.TryGetValue(order.Symbol.Trim(), out var tradingSymbol);
        _ = await CallAsync(environment, HttpMethod.Post, "placeOrder/cancelOrder", new JsonObject
        {
            ["exch"] = exchange,
            ["nestOrderNumber"] = order.OrderId,
            ["trading_symbol"] = tradingSymbol ?? string.Empty,
        }, ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, "placeOrder/fetchOrderBook", null, ct).ConfigureAwait(false);
        var (exchange, token) = Split(symbol);
        return root.ValueKind == JsonValueKind.Array
            ? [.. root.EnumerateArray()
                .Where(o => Str(o, "token") == token && string.Equals(Str(o, "Exchange"), exchange, StringComparison.OrdinalIgnoreCase))
                .Select(o => ReadOrder(o, symbol))]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
        (await OrdersAsync(environment, symbol, ct).ConfigureAwait(false)).FirstOrDefault(o => o.OrderId == orderId);

    /// <summary><c>{"Nstordno","Status","token","Exchange","Trsym","Trantype":"B|S","Prctype","Qty","Fillshares","Prc","Trgprc","Avgprc",
    /// "RejReason","remarks","OrderedTime"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "Fillshares");
        var status = Str(o, "Status").ToLowerInvariant() switch
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
            Str(o, "Nstordno"),
            EngineId(Str(o, "remarks")),
            symbol,
            Str(o, "Trantype") is "S" or "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "Prctype") switch
            {
                "L" => RouteOrderType.Limit,
                "SL-M" => RouteOrderType.Stop,
                "SL" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            RouteTimeInForce.Day,
            Dec(o, "Qty"),
            Positive(o, "Prc"),
            Positive(o, "Trgprc"),
            status,
            filled,
            Positive(o, "Avgprc"),
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(o, "RejReason") is { Length: > 0 } reason ? reason : null,
            IndianTime.ToUtc(Str(o, "OrderedTime"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var holdings = await CallAsync(environment, HttpMethod.Get, "positionAndHoldings/holdings", null, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, HttpMethod.Post, "positionAndHoldings/positionBook", new JsonObject { ["ret"] = "NET" }, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>{"HoldingVal":[{"Token1"|"Token","HUqty"|"Holdqty"}]}</c> plus CNC rows of the position book
    /// <c>[{"Token","Exchange","Pcode":"CNC","Netqty"}]</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var (exchange, token) = Split(symbol);
        var held = 0m;
        if (holdings.ValueKind == JsonValueKind.Object && holdings.TryGetProperty("HoldingVal", out var rows) && rows.ValueKind == JsonValueKind.Array)
            foreach (var row in rows.EnumerateArray())
                if (Str(row, "Token1") == token || Str(row, "Token") == token)
                    held += Dec(row, "HUqty") is not 0 and var hu ? hu : Dec(row, "Holdqty");
        var today = positions.ValueKind == JsonValueKind.Array
            ? positions.EnumerateArray()
                .Where(p => Str(p, "Token") == token && string.Equals(Str(p, "Exchange"), exchange, StringComparison.OrdinalIgnoreCase) && Str(p, "Pcode") == "CNC")
                .Sum(p => Dec(p, "Netqty"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await QuoteAsync(environment, symbol, ct).ConfigureAwait(false) is { } quote && Positive(quote, "LTP") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
}
