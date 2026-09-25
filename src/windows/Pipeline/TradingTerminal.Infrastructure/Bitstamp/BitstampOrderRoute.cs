using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bitstamp;

/// <summary>
/// Bitstamp v2 orders: form posts under the v2 authentication headers, whose signature covers key, method,
/// host, path, query, content type, nonce, timestamp, version and body — the content type only when there is
/// a body. Market orders are sized in the base currency at their own path (<c>/buy/market/{pair}/</c>).
///
/// <para>An order's fills are its <c>transactions</c>; filled quantity and average price are summed from
/// them, and so is the fee, which is in the quote currency. Bitstamp has no test environment and names no
/// account in its API (the key stands in). Written 2026-09-25 from Bitstamp's v2 reference; not yet run
/// against a real account.</para>
/// </summary>
internal sealed class BitstampOrderRoute : OrderRouteBase
{
    private const string HostName = "www.bitstamp.net";

    public BitstampOrderRoute(IBrokerCredentialSource credentials, ILogger<BitstampOrderRoute> logger)
        : base(credentials, logger) { }

    internal BitstampOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Bitstamp;
    public override string DisplayName => "Bitstamp";
    public override string RouteId => "bitstamp";
    public override string? PaperEnvironmentName => null;

    private static string Pair(string symbol) => symbol.Trim().ToLowerInvariant();

    private async Task<JsonElement> PrivateAsync(string path, string form, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() =>
        {
            var nonce = Guid.NewGuid().ToString();
            var stamp = Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var contentType = form.Length > 0 ? "application/x-www-form-urlencoded" : string.Empty;
            var request = new HttpRequestMessage(HttpMethod.Post, $"https://{HostName}{path}");
            if (form.Length > 0)
                request.Content = new StringContent(form, Encoding.UTF8, contentType);
            var key = Credential.Key.Trim();
            request.Headers.TryAddWithoutValidation("X-Auth", "BITSTAMP " + key);
            request.Headers.TryAddWithoutValidation("X-Auth-Signature", CryptoAuth.BitstampSignature(
                key, "POST", HostName, path, string.Empty, contentType, nonce, stamp, form, Credential.Secret));
            request.Headers.TryAddWithoutValidation("X-Auth-Nonce", nonce);
            request.Headers.TryAddWithoutValidation("X-Auth-Timestamp", stamp);
            request.Headers.TryAddWithoutValidation("X-Auth-Version", "v2");
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, $"{Str(root, "reason")} {Str(root, "code")}".Trim(), body);
        if (Str(root, "status") == "error")
            throw RefusedInBody(root.TryGetProperty("reason", out var reason) ? reason.GetRawText() : body, body);
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var usd = await PrivateAsync("/api/v2/account_balances/usd/", string.Empty, ct).ConfigureAwait(false);
        return new RouteAccount(KeyAccount(), "USD", Dec(usd, "total"), Dec(usd, "available"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{HostName}/api/v2/trading-pairs-info/"), ct)
            .ConfigureAwait(false);
        EnsureSuccess(status, body, null);
        return ReadInstrument(root, symbol) ?? throw new InvalidDataException($"Bitstamp returned no rules for {symbol}.");
    }

    /// <summary><c>[{"name":"BTC/USD","url_symbol":"btcusd","base_decimals","counter_decimals","minimum_order"}]</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement pairs, string symbol)
    {
        if (pairs.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var p in pairs.EnumerateArray())
        {
            if (!string.Equals(Str(p, "url_symbol"), Pair(symbol), StringComparison.Ordinal))
                continue;
            var names = Str(p, "name").Split('/');
            var step = Step((int)Dec(p, "base_decimals"));
            var tick = Step((int)Dec(p, "counter_decimals"));
            return new RouteInstrument(Pair(symbol), step, step, tick, 1, long.MaxValue / 2,
                RouteOrderTypes.Market | RouteOrderTypes.Limit,
                RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
                SupportsReplace: false, names.Length == 2 ? names[1] : string.Empty)
            {
                BaseAsset = names.Length == 2 ? names[0] : string.Empty,
            };
        }
        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var client = Token(request.ClientOrderId, id => id.Length <= 180 ? id : Hex(id, 32));
        var form = $"amount={Num(request.Quantity)}&client_order_id={Uri.EscapeDataString(client)}";
        string path;
        if (request.Type == RouteOrderType.Market)
        {
            path = $"/api/v2/{side}/market/{Pair(request.Symbol)}/";
        }
        else
        {
            path = $"/api/v2/{side}/{Pair(request.Symbol)}/";
            form += $"&price={Num(request.LimitPrice!.Value)}";
            if (request.TimeInForce == RouteTimeInForce.ImmediateOrCancel)
                form += "&ioc_order=True";
            else if (request.TimeInForce == RouteTimeInForce.FillOrKill)
                form += "&fok_order=True";
        }
        var root = await PrivateAsync(path, form, ct).ConfigureAwait(false);
        var orderId = Str(root, "id");
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("Bitstamp acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.Working, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await PrivateAsync("/api/v2/cancel_order/", $"id={Uri.EscapeDataString(order.OrderId)}", ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await PrivateAsync($"/api/v2/open_orders/{Pair(symbol)}/", string.Empty, ct).ConfigureAwait(false);
        var orders = new List<RouteOrder>();
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var o in root.EnumerateArray())
            {
                var original = Dec(o, "amount_at_create");
                var remaining = Dec(o, "amount");
                orders.Add(new RouteOrder(
                    Str(o, "id"), EngineId(Str(o, "client_order_id")), Pair(symbol),
                    Str(o, "type") == "1" ? OrderSide.Sell : OrderSide.Buy,
                    RouteOrderType.Limit, RouteTimeInForce.GoodTillCancelled,
                    original > 0 ? original : remaining, Positive(o, "price"), null,
                    original > remaining && original > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                    // An open order's fills and average come from its status; the engine asks for it when the
                    // filled quantity moves, which it sees here.
                    original > remaining && original > 0 ? original - remaining : 0, null, 0, string.Empty, null,
                    ParseTime(Str(o, "datetime"), Now.UtcDateTime)));
            }

        // The list has no fill prices; an order with fills is read again from its status, which has them.
        for (var i = 0; i < orders.Count; i++)
            if (orders[i].FilledQuantity > 0 && await OrderAsync(environment, symbol, orders[i].OrderId, orders[i].ClientOrderId, ct).ConfigureAwait(false) is { } detailed)
                orders[i] = detailed;
        return orders;
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var root = await PrivateAsync("/api/v2/order_status/", $"id={Uri.EscapeDataString(orderId)}", ct).ConfigureAwait(false);
        return ReadStatus(root, symbol, rules.BaseAsset.ToLowerInvariant(), rules.Currency.ToLowerInvariant(), rules.Currency);
    }

    /// <summary><c>{"id","status":"Open|Finished|Canceled","transactions":[{"price","btc","usd","fee"}],
    /// "amount_remaining","client_order_id","type"}</c>. Fills are keyed by lower-case currency code.</summary>
    internal RouteOrder? ReadStatus(JsonElement root, string symbol, string baseCode, string quoteCode, string quote)
    {
        var id = Str(root, "id");
        if (id.Length == 0)
            return null;
        decimal filled = 0, cost = 0, fee = 0;
        if (root.TryGetProperty("transactions", out var transactions) && transactions.ValueKind == JsonValueKind.Array)
            foreach (var t in transactions.EnumerateArray())
            {
                filled += Dec(t, baseCode);
                cost += Dec(t, quoteCode);
                fee += Dec(t, "fee");
            }
        var remaining = Dec(root, "amount_remaining");
        return new RouteOrder(
            id, EngineId(Str(root, "client_order_id")), Pair(symbol),
            Str(root, "type") == "1" ? OrderSide.Sell : OrderSide.Buy,
            RouteOrderType.Limit, RouteTimeInForce.GoodTillCancelled,
            filled + remaining, null, null,
            Str(root, "status") switch
            {
                "Open" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "Finished" => RouteOrderStatus.Filled,
                "Canceled" => RouteOrderStatus.Cancelled,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            filled > 0 && cost > 0 ? cost / filled : null,
            fee,
            quote,
            null,
            Now.UtcDateTime);
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var balance = await PrivateAsync($"/api/v2/account_balances/{rules.BaseAsset.ToLowerInvariant()}/", string.Empty, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, Dec(balance, "total"));
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{HostName}/api/v2/ticker/{Pair(symbol)}/"), ct)
            .ConfigureAwait(false);
        return status == 200 && Positive(root, "last") is { } price ? new RoutePrice(price, Now.UtcDateTime) : null;
    }
}
