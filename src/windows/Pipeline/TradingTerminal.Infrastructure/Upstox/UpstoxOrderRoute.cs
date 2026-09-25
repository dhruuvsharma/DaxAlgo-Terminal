using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Upstox;

/// <summary>
/// Upstox v2 orders, with the day's access token the login window stores.
///
/// <para>Symbols are Upstox instrument keys, <c>SEGMENT|ISIN</c> (<c>NSE_EQ|INE002A01018</c>) as its market data
/// writes them; <c>NSE_EQ:INE002A01018</c> is read the same. Orders are delivery (<c>product: D</c>); stop is
/// <c>SL-M</c> and stop-limit <c>SL</c>. The engine's id rides as the order's <c>tag</c>. The last-price answer
/// is keyed by <c>SEGMENT:TRADINGSYMBOL</c>, not by the instrument key asked for, so it is matched on its
/// <c>instrument_token</c>. The position is the long-term holding (settled and T1) plus the day's delivery
/// position. Written 2026-09-25 from Upstox's v2 API reference; not yet run against a real account.</para>
/// </summary>
internal sealed class UpstoxOrderRoute : IndianOrderRoute
{
    private const string Host = "https://api.upstox.com/v2";

    public UpstoxOrderRoute(IBrokerCredentialSource credentials, ILogger<UpstoxOrderRoute> logger)
        : base(credentials, logger) { }

    internal UpstoxOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Upstox;
    public override string DisplayName => "Upstox";
    public override string RouteId => "upstox";

    /// <summary>The instrument key as Upstox's API wants it: <c>SEGMENT|ISIN</c>.</summary>
    internal static string Key(string symbol)
    {
        var trimmed = symbol.Trim();
        return trimmed.Contains('|') ? trimmed : trimmed.Replace(':', '|');
    }

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct)
    {
        RequireLive(environment);
        var token = Session;
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Host + pathAndQuery);
            if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || Str(root, "status") == "error")
            throw Refused(status, Words(root), text, status is >= 200 and < 300 ? true : null);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    /// <summary><c>{"status":"error","errors":[{"errorCode","message"}]}</c>.</summary>
    internal static string? Words(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var words = string.Join("; ", errors.EnumerateArray()
                .Select(e => Str(e, "errorCode") is { Length: > 0 } code ? $"{Str(e, "message")} ({code})" : Str(e, "message"))
                .Where(s => s.Length > 0));
            if (words.Length > 0) return words;
        }

        return Str(root, "message") is { Length: > 0 } message ? message : null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var profile = await CallAsync(environment, HttpMethod.Get, "/user/profile", null, ct).ConfigureAwait(false);
        var funds = await CallAsync(environment, HttpMethod.Get, "/user/get-funds-and-margin?segment=SEC", null, ct).ConfigureAwait(false);
        var equity = funds.TryGetProperty("equity", out var e) ? e : default;
        var available = Dec(equity, "available_margin");
        return new RouteAccount(Str(profile, "user_id"), "INR", available + Dec(equity, "used_margin"), available);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        await PriceAsync(environment, symbol, ct).ConfigureAwait(false) is null
            ? throw new BrokerOrderRouteException($"Upstox does not know {symbol} — write the instrument key, e.g. NSE_EQ|INE002A01018.", isRejection: true)
            : Delivery(symbol.Trim());

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Post, "/order/place",
            SubmitBody(request, Token(request.ClientOrderId, id => Tag(id, 40))), ct).ConfigureAwait(false);
        var id = Str(data, "order_id");
        if (id.Length == 0)
            throw new InvalidDataException($"Upstox answered the order without an id: {SignInProof.Snippet(data.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string tag) => new()
    {
        ["quantity"] = request.Quantity,
        ["product"] = "D",
        ["validity"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "IOC" : "DAY",
        ["price"] = request.LimitPrice ?? 0m,
        ["tag"] = tag,
        ["instrument_token"] = Key(request.Symbol),
        ["order_type"] = request.Type switch
        {
            RouteOrderType.Limit => "LIMIT",
            RouteOrderType.Stop => "SL-M",
            RouteOrderType.StopLimit => "SL",
            _ => "MARKET",
        },
        ["transaction_type"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
        ["disclosed_quantity"] = 0,
        ["trigger_price"] = request.StopPrice ?? 0m,
        ["is_amo"] = false,
    };

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Delete, $"/order/cancel?order_id={Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Get, "/order/retrieve-all", null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Array
            ? [.. data.EnumerateArray().Where(o => string.Equals(Str(o, "instrument_token"), Key(symbol), StringComparison.OrdinalIgnoreCase)).Select(o => ReadOrder(o, symbol))]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var data = await CallAsync(environment, HttpMethod.Get, $"/order/details?order_id={Uri.EscapeDataString(orderId)}", null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object && Str(data, "order_id").Length > 0 ? ReadOrder(data, symbol) : null;
    }

    /// <summary><c>{"order_id","status","instrument_token","transaction_type","order_type","validity","quantity","filled_quantity",
    /// "price","trigger_price","average_price","status_message","tag","exchange_timestamp","order_timestamp"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filled_quantity");
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
        var time = Str(o, "exchange_timestamp") is { Length: > 0 } t ? t : Str(o, "order_timestamp");
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
        var holdings = await CallAsync(environment, HttpMethod.Get, "/portfolio/long-term-holdings", null, ct).ConfigureAwait(false);
        var positions = await CallAsync(environment, HttpMethod.Get, "/portfolio/short-term-positions", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(holdings, positions, symbol));
    }

    /// <summary>Holdings <c>[{"instrument_token","quantity","t1_quantity"}]</c> plus delivery positions
    /// <c>[{"instrument_token","product":"D","quantity"}]</c>.</summary>
    internal static decimal ReadPosition(JsonElement holdings, JsonElement positions, string symbol)
    {
        var key = Key(symbol);
        bool Matches(JsonElement row) => string.Equals(Str(row, "instrument_token"), key, StringComparison.OrdinalIgnoreCase);
        var held = holdings.ValueKind == JsonValueKind.Array ? holdings.EnumerateArray().Where(Matches).Sum(h => Dec(h, "quantity") + Dec(h, "t1_quantity")) : 0m;
        var today = positions.ValueKind == JsonValueKind.Array
            ? positions.EnumerateArray().Where(p => Matches(p) && Str(p, "product") == "D").Sum(p => Dec(p, "quantity"))
            : 0m;
        return held + today;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var key = Key(symbol);
        var data = await CallAsync(environment, HttpMethod.Get, $"/market-quote/ltp?instrument_key={Uri.EscapeDataString(key)}", null, ct).ConfigureAwait(false);
        return ReadPrice(data, key, Now.UtcDateTime);
    }

    /// <summary><c>{"NSE_EQ:RELIANCE":{"instrument_token":"NSE_EQ|INE002A01018","last_price"}}</c>.</summary>
    internal static RoutePrice? ReadPrice(JsonElement data, string key, DateTime now)
    {
        if (data.ValueKind != JsonValueKind.Object) return null;
        foreach (var entry in data.EnumerateObject())
            if (string.Equals(Str(entry.Value, "instrument_token"), key, StringComparison.OrdinalIgnoreCase) && Positive(entry.Value, "last_price") is { } price)
                return new RoutePrice(price, now);
        return null;
    }
}
