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

namespace TradingTerminal.Infrastructure.Fyers;

/// <summary>
/// Fyers API v3 orders, with the app id and the day's access token the login window stores
/// (<c>Authorization: APP_ID:ACCESS_TOKEN</c>).
///
/// <para>Symbols are Fyers' own (<c>NSE:RELIANCE-EQ</c>), which orders carry as written. Orders are delivery
/// (<c>CNC</c>); Fyers numbers its order types (1 limit, 2 market, 3 stop, 4 stop-limit), sides (1 buy, −1 sell)
/// and statuses (1 cancelled, 2 filled, 4 in transit, 5 rejected, 6 pending, 7 expired). The engine's id rides
/// as <c>orderTag</c>. The position is the holding plus the day's CNC position. Written 2026-09-25 from the
/// Fyers v3 reference; not yet run against a real account.</para>
/// </summary>
internal sealed class FyersOrderRoute : IndianOrderRoute
{
    private const string Host = "https://api-t1.fyers.in";

    public FyersOrderRoute(IBrokerCredentialSource credentials, ILogger<FyersOrderRoute> logger)
        : base(credentials, logger) { }

    internal FyersOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Fyers;
    public override string DisplayName => "Fyers";
    public override string RouteId => "fyers";

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        RequireLive(environment);
        var authorization = $"{Credential.Key.Trim()}:{Session}";
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Host + path);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || Str(root, "s") == "error")
            throw Refused(status, Words(root), text, status is >= 200 and < 300 ? true : null);
        return root;
    }

    /// <summary><c>{"s":"error","code","message"}</c>.</summary>
    internal static string? Words(JsonElement root) =>
        Str(root, "message") is { Length: > 0 } message ? Str(root, "code") is { Length: > 0 } code ? $"{message} ({code})" : message : null;

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var profile = await CallAsync(environment, HttpMethod.Get, "/api/v3/profile", null, ct).ConfigureAwait(false);
        var funds = await CallAsync(environment, HttpMethod.Get, "/api/v3/funds", null, ct).ConfigureAwait(false);
        var (total, available) = ReadFunds(funds);
        var data = profile.TryGetProperty("data", out var d) ? d : profile;
        return new RouteAccount(Str(data, "fy_id"), "INR", total, available);
    }

    /// <summary><c>{"fund_limit":[{"id":1,"title":"Total Balance","equityAmount"},{"id":10,"title":"Available Balance","equityAmount"}]}</c>.</summary>
    internal static (decimal Total, decimal Available) ReadFunds(JsonElement root)
    {
        if (!root.TryGetProperty("fund_limit", out var rows) || rows.ValueKind != JsonValueKind.Array) return (0m, 0m);
        decimal Row(int id, string title) => rows.EnumerateArray()
            .Where(r => (int)Dec(r, "id") == id || Str(r, "title").Equals(title, StringComparison.OrdinalIgnoreCase))
            .Select(r => Dec(r, "equityAmount")).FirstOrDefault();
        return (Row(1, "Total Balance"), Row(10, "Available Balance"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await PriceAsync(environment, symbol, ct).ConfigureAwait(false) is null
            ? throw new BrokerOrderRouteException($"Fyers does not know {symbol} — write it EXCHANGE:SYMBOL-SERIES, e.g. NSE:RELIANCE-EQ.", isRejection: true)
            : Delivery(symbol.Trim().ToUpperInvariant());

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Post, "/api/v3/orders/sync",
            SubmitBody(request, Token(request.ClientOrderId, id => Alphanumeric(id, 30))), ct).ConfigureAwait(false);
        var id = Str(root, "id");
        if (id.Length == 0)
            throw new InvalidDataException($"Fyers answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string tag) => new()
    {
        ["symbol"] = request.Symbol.Trim().ToUpperInvariant(),
        ["qty"] = request.Quantity,
        ["type"] = request.Type switch
        {
            RouteOrderType.Limit => 1,
            RouteOrderType.Stop => 3,
            RouteOrderType.StopLimit => 4,
            _ => 2,
        },
        ["side"] = request.Side == OrderSide.Buy ? 1 : -1,
        ["productType"] = "CNC",
        ["limitPrice"] = request.LimitPrice ?? 0m,
        ["stopPrice"] = request.StopPrice ?? 0m,
        ["validity"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "IOC" : "DAY",
        ["disclosedQty"] = 0,
        ["offlineOrder"] = false,
        ["orderTag"] = tag,
    };

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Delete, "/api/v3/orders/sync", new JsonObject { ["id"] = order.OrderId }, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, "/api/v3/orders", null, ct).ConfigureAwait(false);
        return ReadOrders(root, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var root = await CallAsync(environment, HttpMethod.Get, $"/api/v3/orders?id={Uri.EscapeDataString(orderId)}", null, ct).ConfigureAwait(false);
        return ReadOrders(root, symbol).FirstOrDefault(o => o.OrderId == orderId);
    }

    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement root, string symbol) =>
        root.TryGetProperty("orderBook", out var book) && book.ValueKind == JsonValueKind.Array
            ? [.. book.EnumerateArray()
                .Where(o => string.Equals(Str(o, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(o => ReadOrder(o, symbol))]
            : [];

    /// <summary><c>{"id","symbol","qty","filledQty","type","side","limitPrice","stopPrice","tradedPrice","status","message",
    /// "orderValidity","orderTag","orderDateTime"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filledQty");
        var status = (int)Dec(o, "status") switch
        {
            2 => RouteOrderStatus.Filled,
            1 => RouteOrderStatus.Cancelled,
            5 => RouteOrderStatus.Rejected,
            7 => RouteOrderStatus.Expired,
            4 => RouteOrderStatus.PendingNew,
            6 => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        return new RouteOrder(
            Str(o, "id"),
            EngineId(Str(o, "orderTag")),
            symbol,
            Dec(o, "side") < 0 ? OrderSide.Sell : OrderSide.Buy,
            (int)Dec(o, "type") switch
            {
                1 => RouteOrderType.Limit,
                3 => RouteOrderType.Stop,
                4 => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "orderValidity") == "IOC" ? RouteTimeInForce.ImmediateOrCancel : RouteTimeInForce.Day,
            Dec(o, "qty"),
            Positive(o, "limitPrice"),
            Positive(o, "stopPrice"),
            status,
            filled,
            Positive(o, "tradedPrice"),
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(o, "message") is { Length: > 0 } reason ? reason : null,
            IndianTime.ToUtc(Str(o, "orderDateTime"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var holdings = await CallAsync(environment, HttpMethod.Get, "/api/v3/holdings", null, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, HttpMethod.Get, "/api/v3/positions", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary><c>{"holdings":[{"symbol","quantity"}]}</c> plus <c>{"netPositions":[{"symbol","productType":"CNC","netQty"}]}</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        bool Matches(JsonElement row) => string.Equals(Str(row, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase);
        var held = holdings.TryGetProperty("holdings", out var h) && h.ValueKind == JsonValueKind.Array
            ? h.EnumerateArray().Where(Matches).Sum(row => Dec(row, "quantity"))
            : 0m;
        var today = positions.TryGetProperty("netPositions", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Where(row => Matches(row) && Str(row, "productType") == "CNC").Sum(row => Dec(row, "netQty"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, $"/data/quotes?symbols={Uri.EscapeDataString(symbol.Trim().ToUpperInvariant())}", null, ct)
            .ConfigureAwait(false);
        var row = root.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.Array && d.GetArrayLength() > 0 ? d[0] : default;
        return Str(row, "s") != "error" && row.ValueKind == JsonValueKind.Object && row.TryGetProperty("v", out var v) && Positive(v, "lp") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
