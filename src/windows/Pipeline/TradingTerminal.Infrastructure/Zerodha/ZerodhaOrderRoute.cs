using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Zerodha;

/// <summary>
/// Zerodha Kite Connect orders, with the API key and the day's access token the login window stores
/// (<c>Authorization: token api_key:access_token</c>).
///
/// <para>Symbols are the market-data client's <c>EXCHANGE:TRADINGSYMBOL</c> (<c>NSE:INFY</c>). Orders are regular
/// delivery (<c>CNC</c>) orders; stop is <c>SL-M</c> and stop-limit <c>SL</c>. The engine's id rides as the
/// order's <c>tag</c> (twenty letters and digits). <c>/orders</c> lists the day's orders with cumulative
/// <c>filled_quantity</c> and <c>average_price</c>; one order's history is <c>/orders/{id}</c>. The position is
/// the holding (settled and T1) plus the day's CNC trades. Written 2026-09-25 from the Kite Connect v3
/// reference; not yet run against a real account.</para>
/// </summary>
internal sealed class ZerodhaOrderRoute : IndianOrderRoute
{
    private const string Host = "https://api.kite.trade";

    public ZerodhaOrderRoute(IBrokerCredentialSource credentials, ILogger<ZerodhaOrderRoute> logger)
        : base(credentials, logger) { }

    internal ZerodhaOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Zerodha;
    public override string DisplayName => "Zerodha";
    public override string RouteId => "zerodha";

    private async Task<JsonElement> CallAsync(
        RouteEnvironment environment, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        RequireLive(environment);
        var authorization = $"token {Credential.Key.Trim()}:{Session}";
        var (status, root, body) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Host + path);
            if (form is not null) request.Content = new FormUrlEncodedContent(form);
            request.Headers.TryAddWithoutValidation("X-Kite-Version", "3");
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || Str(root, "status") == "error")
            throw Refused(status, Words(root), body, status is >= 200 and < 300 ? true : null);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    /// <summary><c>{"status":"error","message","error_type"}</c>.</summary>
    internal static string? Words(JsonElement root) =>
        Str(root, "message") is { Length: > 0 } message ? Str(root, "error_type") is { Length: > 0 } type ? $"{message} ({type})" : message : null;

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var profile = await CallAsync(environment, HttpMethod.Get, "/user/profile", null, ct).ConfigureAwait(false);
        var margins = await CallAsync(environment, HttpMethod.Get, "/user/margins/equity", null, ct).ConfigureAwait(false);
        var available = margins.TryGetProperty("available", out var a) ? a : default;
        return new RouteAccount(Str(profile, "user_id"), "INR", Dec(available, "cash"), Dec(margins, "net"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await PriceAsync(environment, symbol, ct).ConfigureAwait(false) is null
            ? throw new BrokerOrderRouteException($"Zerodha does not know {symbol} — write it EXCHANGE:TRADINGSYMBOL, e.g. NSE:INFY.", isRejection: true)
            : Delivery(symbol.Trim().ToUpperInvariant());

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Post, "/orders/regular",
            SubmitForm(request, Token(request.ClientOrderId, id => Alphanumeric(id, 20))), ct).ConfigureAwait(false);
        var id = Str(data, "order_id");
        if (id.Length == 0)
            throw new InvalidDataException($"Zerodha answered the order without an id: {SignInProof.Snippet(data.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static List<KeyValuePair<string, string>> SubmitForm(RouteOrderRequest request, string tag)
    {
        var (exchange, tradingSymbol) = Split(request.Symbol);
        var form = new List<KeyValuePair<string, string>>
        {
            new("tradingsymbol", tradingSymbol.ToUpperInvariant()),
            new("exchange", exchange),
            new("transaction_type", request.Side == OrderSide.Buy ? "BUY" : "SELL"),
            new("order_type", request.Type switch
            {
                RouteOrderType.Limit => "LIMIT",
                RouteOrderType.Stop => "SL-M",
                RouteOrderType.StopLimit => "SL",
                _ => "MARKET",
            }),
            new("quantity", Num(request.Quantity)),
            new("product", "CNC"),
            new("validity", request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "IOC" : "DAY"),
            new("tag", tag),
        };
        if (request.LimitPrice is { } limit) form.Add(new("price", Num(limit)));
        if (request.StopPrice is { } stop) form.Add(new("trigger_price", Num(stop)));
        return form;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Delete, $"/orders/regular/{Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Get, "/orders", null, ct).ConfigureAwait(false);
        return ReadOrders(data, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var history = await CallAsync(environment, HttpMethod.Get, $"/orders/{Uri.EscapeDataString(orderId)}", null, ct).ConfigureAwait(false);
        return history.ValueKind == JsonValueKind.Array && history.GetArrayLength() > 0 ? ReadOrder(history[history.GetArrayLength() - 1], symbol) : null;
    }

    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement data, string symbol)
    {
        if (data.ValueKind != JsonValueKind.Array) return [];
        var (exchange, tradingSymbol) = Split(symbol);
        return [.. data.EnumerateArray()
            .Where(o => string.Equals(Str(o, "exchange"), exchange, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(Str(o, "tradingsymbol"), tradingSymbol, StringComparison.OrdinalIgnoreCase))
            .Select(o => ReadOrder(o, symbol))];
    }

    /// <summary><c>{"order_id","status","tradingsymbol","exchange","transaction_type","order_type","validity","quantity","filled_quantity",
    /// "price","trigger_price","average_price","status_message","tag","exchange_update_timestamp","order_timestamp"}</c> — Kite's
    /// timestamps are Indian time.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filled_quantity");
        var status = Str(o, "status") switch
        {
            "COMPLETE" => RouteOrderStatus.Filled,
            "CANCELLED" => RouteOrderStatus.Cancelled,
            "REJECTED" => RouteOrderStatus.Rejected,
            "CANCEL PENDING" => RouteOrderStatus.PendingCancel,
            "PUT ORDER REQ RECEIVED" or "VALIDATION PENDING" or "OPEN PENDING" or "AMO REQ RECEIVED" => RouteOrderStatus.PendingNew,
            "OPEN" or "TRIGGER PENDING" or "MODIFIED" or "MODIFY PENDING" or "MODIFY VALIDATION PENDING" =>
                filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var time = Str(o, "exchange_update_timestamp") is { Length: > 0 } t ? t : Str(o, "order_timestamp");
        return new RouteOrder(
            Str(o, "order_id"),
            EngineId(Str(o, "tag")),
            symbol,
            Str(o, "transaction_type") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "order_type") switch
            {
                "LIMIT" => RouteOrderType.Limit,
                "SL-M" => RouteOrderType.Stop,
                "SL" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "validity") == "IOC" ? RouteTimeInForce.ImmediateOrCancel : RouteTimeInForce.Day,
            Dec(o, "quantity"),
            Positive(o, "price"),
            Positive(o, "trigger_price"),
            status,
            filled,
            Positive(o, "average_price"),
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(o, "status_message") is { Length: > 0 } reason ? reason : null,
            IndianTime.ToUtc(time, Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var holdings = await CallAsync(environment, HttpMethod.Get, "/portfolio/holdings", null, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, HttpMethod.Get, "/portfolio/positions", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>[{"exchange","tradingsymbol","quantity","t1_quantity"}]</c> plus the day's CNC rows of
    /// <c>{"day":[{"exchange","tradingsymbol","product","quantity"}]}</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var (exchange, tradingSymbol) = Split(symbol);
        bool Matches(JsonElement row) =>
            string.Equals(Str(row, "exchange"), exchange, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Str(row, "tradingsymbol"), tradingSymbol, StringComparison.OrdinalIgnoreCase);
        var held = holdings.ValueKind == JsonValueKind.Array
            ? holdings.EnumerateArray().Where(Matches).Sum(h => Dec(h, "quantity") + Dec(h, "t1_quantity"))
            : 0m;
        var today = positions.TryGetProperty("day", out var day) && day.ValueKind == JsonValueKind.Array
            ? day.EnumerateArray().Where(p => Matches(p) && Str(p, "product") == "CNC").Sum(p => Dec(p, "quantity"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var key = symbol.Trim().ToUpperInvariant();
        var data = await CallAsync(environment, HttpMethod.Get, $"/quote/ltp?i={Uri.EscapeDataString(key)}", null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && data.TryGetProperty(key, out var quote) && Positive(quote, "last_price") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
