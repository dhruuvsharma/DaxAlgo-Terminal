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

namespace TradingTerminal.Infrastructure.IciciBreeze;

/// <summary>
/// ICICI Direct Breeze orders, each request signed exactly as the market-data client signs:
/// <c>X-Checksum: token SHA-256(timestamp + body + secret)</c> beside the app key and the day's session.
///
/// <para>Breeze reads a JSON body even on GET and DELETE, and answers <c>{"Success","Status","Error"}</c> — a
/// failure in <c>Error</c>, sometimes under HTTP 200. Symbols are Breeze stock codes (<c>NSE:RELIND</c>). Orders
/// are cash (delivery) orders; Breeze has no stop-market for cash, so stop-limit goes as <c>stoploss</c> with
/// its limit price, and a plain stop is not offered. The engine's id rides as <c>user_remark</c>.</para>
///
/// <para><b>Fills.</b> An order reports its quantity and what is still pending. The route counts a fill only
/// from a status that says one happened (executed or partly executed), or an executed quantity where Breeze
/// sends one — a cancelled order's pending quantity is not read as filled, because a wrong fill is worse than a
/// missed one: the engine's reconciliation catches a missed fill as a position mismatch and stops the book.
/// Shapes are from Breeze's API reference, read 2026-09-25; not yet run against a real account.</para>
/// </summary>
internal sealed class IciciBreezeOrderRoute : IndianOrderRoute
{
    private const string Root = "https://api.icicidirect.com/breezeapi/api/v1";

    public IciciBreezeOrderRoute(IBrokerCredentialSource credentials, ILogger<IciciBreezeOrderRoute> logger)
        : base(credentials, logger) { }

    internal IciciBreezeOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.IciciBreeze;
    public override string DisplayName => "ICICI Breeze";
    public override string RouteId => "icici-breeze";

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + ".000Z";

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, JsonObject body, CancellationToken ct)
    {
        RequireLive(environment);
        var app = Credential;
        var session = Session;
        var json = body.ToJsonString();
        var (status, root, text) = await SendAsync(() =>
        {
            var stamp = Iso(Now.UtcDateTime);
            var request = new HttpRequestMessage(method, $"{Root}/{path}") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            request.Headers.TryAddWithoutValidation("X-Checksum", "token " + RealIciciBreezeClient.Checksum(stamp, json, app.Secret.Trim()));
            request.Headers.TryAddWithoutValidation("X-Timestamp", stamp);
            request.Headers.TryAddWithoutValidation("X-AppKey", app.Key.Trim());
            request.Headers.TryAddWithoutValidation("X-SessionToken", session);
            return request;
        }, ct).ConfigureAwait(false);
        var error = root.TryGetProperty("Error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        if (status is < 200 or >= 300 || !string.IsNullOrEmpty(error))
        {
            // No orders, no positions: Breeze says so as an error.
            if (status is >= 200 and < 300 && error!.Contains("no data", StringComparison.OrdinalIgnoreCase))
                return default;
            throw Refused(status, error, text, status is >= 200 and < 300 ? true : null);
        }

        return root.TryGetProperty("Success", out var success) ? success : default;
    }

    private static IEnumerable<JsonElement> Rows(JsonElement success) =>
        success.ValueKind == JsonValueKind.Array ? success.EnumerateArray() : success.ValueKind == JsonValueKind.Object ? [success] : [];

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var funds = await CallAsync(environment, HttpMethod.Get, "funds", new JsonObject(), ct).ConfigureAwait(false);
        var account = Credential.Account.Trim() is { Length: > 0 } user ? user : KeyAccount();
        return new RouteAccount(account, "INR", Dec(funds, "total_bank_balance"), Dec(funds, "allocated_equity") - Dec(funds, "block_by_trade_equity"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        if (await PriceAsync(environment, symbol, ct).ConfigureAwait(false) is null)
            throw new BrokerOrderRouteException($"ICICI Breeze does not know {symbol} — write it EXCHANGE:STOCKCODE, e.g. NSE:RELIND.", isRejection: true);
        return Delivery(symbol.Trim().ToUpperInvariant()) with
        {
            OrderTypes = RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.StopLimit,
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var success = await CallAsync(environment, HttpMethod.Post, "order",
            SubmitBody(request, Token(request.ClientOrderId, id => Alphanumeric(id, 20))), ct).ConfigureAwait(false);
        var id = Str(success, "order_id");
        if (id.Length == 0)
            throw new InvalidDataException($"ICICI Breeze answered the order without an id: {SignInProof.Snippet(success.ValueKind == JsonValueKind.Undefined ? "" : success.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string remark)
    {
        var (exchange, code) = Split(request.Symbol);
        if (request.Type == RouteOrderType.Stop)
            throw new BrokerOrderRouteException("ICICI Breeze has no stop-market order for cash; send a stop-limit.", isRejection: true);
        return new JsonObject
        {
            ["stock_code"] = code.ToUpperInvariant(),
            ["exchange_code"] = exchange,
            ["product"] = "cash",
            ["action"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["order_type"] = request.Type switch
            {
                RouteOrderType.Limit => "limit",
                RouteOrderType.StopLimit => "stoploss",
                _ => "market",
            },
            ["stoploss"] = request.StopPrice is { } stop ? Num(stop) : string.Empty,
            ["quantity"] = Num(request.Quantity),
            ["price"] = request.LimitPrice is { } limit ? Num(limit) : string.Empty,
            ["validity"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "ioc" : "day",
            ["disclosed_quantity"] = "0",
            ["expiry_date"] = string.Empty,
            ["right"] = string.Empty,
            ["strike_price"] = string.Empty,
            ["user_remark"] = remark,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var (exchange, _) = Split(order.Symbol);
        _ = await CallAsync(environment, HttpMethod.Delete, "order", new JsonObject { ["order_id"] = order.OrderId, ["exchange_code"] = exchange }, ct)
            .ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exchange, code) = Split(symbol);
        var now = Now.UtcDateTime;
        var success = await CallAsync(environment, HttpMethod.Get, "order", new JsonObject
        {
            ["exchange_code"] = exchange,
            ["from_date"] = Iso(now.Date.AddDays(-1)),
            ["to_date"] = Iso(now.AddMinutes(1)),
        }, ct).ConfigureAwait(false);
        return [.. Rows(success).Where(o => string.Equals(Str(o, "stock_code"), code, StringComparison.OrdinalIgnoreCase)).Select(o => ReadOrder(o, symbol))];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var (exchange, _) = Split(symbol);
        var success = await CallAsync(environment, HttpMethod.Get, "order", new JsonObject { ["exchange_code"] = exchange, ["order_id"] = orderId }, ct)
            .ConfigureAwait(false);
        return Rows(success).Where(o => Str(o, "order_id") == orderId).Select(o => ReadOrder(o, symbol)).FirstOrDefault();
    }

    /// <summary><c>{"order_id","stock_code","action","order_type","status","quantity","pending_quantity","executed_quantity","price",
    /// "stoploss","average_price","validity","user_remark","order_datetime"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var quantity = Dec(o, "quantity");
        var statusText = Str(o, "status").Trim().ToLowerInvariant();
        var filled = o.TryGetProperty("executed_quantity", out _)
            ? Dec(o, "executed_quantity")
            : statusText switch
            {
                "executed" => quantity,
                "partially executed" => Math.Max(0m, quantity - Dec(o, "pending_quantity")),
                _ => 0m,
            };
        var status = statusText switch
        {
            "executed" => RouteOrderStatus.Filled,
            "partially executed" => RouteOrderStatus.PartiallyFilled,
            "cancelled" => RouteOrderStatus.Cancelled,
            "expired" => RouteOrderStatus.Expired,
            "rejected" => RouteOrderStatus.Rejected,
            "ordered" or "requested" or "freezed" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        return new RouteOrder(
            Str(o, "order_id"),
            EngineId(Str(o, "user_remark")),
            symbol,
            Str(o, "action").Equals("sell", StringComparison.OrdinalIgnoreCase) ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "order_type").ToLowerInvariant() switch
            {
                "limit" => RouteOrderType.Limit,
                "stoploss" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "validity").Equals("ioc", StringComparison.OrdinalIgnoreCase) ? RouteTimeInForce.ImmediateOrCancel : RouteTimeInForce.Day,
            quantity,
            Positive(o, "price"),
            Positive(o, "stoploss"),
            status,
            filled,
            Positive(o, "average_price"),
            0m,
            string.Empty,
            null,
            IndianTime.ToUtc(Str(o, "order_datetime"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exchange, code) = Split(symbol);
        var now = Now.UtcDateTime;
        var holdings = await CallAsync(environment, HttpMethod.Get, "portfolioholdings", new JsonObject
        {
            ["exchange_code"] = exchange,
            ["from_date"] = Iso(now.AddYears(-10)),
            ["to_date"] = Iso(now),
            ["stock_code"] = code.ToUpperInvariant(),
            ["portfolio_type"] = string.Empty,
        }, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, HttpMethod.Get, "portfoliopositions", new JsonObject(), ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>[{"stock_code","quantity"}]</c> plus the day's cash positions
    /// <c>[{"stock_code","product_type":"Cash","action":"Buy|Sell","quantity"}]</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var (_, code) = Split(symbol);
        bool Matches(JsonElement row) => string.Equals(Str(row, "stock_code"), code, StringComparison.OrdinalIgnoreCase);
        var held = Rows(holdings).Where(Matches).Sum(h => Dec(h, "quantity"));
        var today = Rows(positions)
            .Where(p => Matches(p) && Str(p, "product_type").Equals("cash", StringComparison.OrdinalIgnoreCase))
            .Sum(p => Str(p, "action").Equals("sell", StringComparison.OrdinalIgnoreCase) ? -Math.Abs(Dec(p, "quantity")) : Math.Abs(Dec(p, "quantity")));
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (exchange, code) = Split(symbol);
        var success = await CallAsync(environment, HttpMethod.Get, "quotes", new JsonObject
        {
            ["stock_code"] = code.ToUpperInvariant(),
            ["exchange_code"] = exchange,
            ["expiry_date"] = string.Empty,
            ["product_type"] = "cash",
            ["right"] = string.Empty,
            ["strike_price"] = string.Empty,
        }, ct).ConfigureAwait(false);
        return Rows(success).Select(r => Positive(r, "ltp")).FirstOrDefault(p => p is not null) is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
