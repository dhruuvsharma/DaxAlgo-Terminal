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
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Coinbase;

/// <summary>
/// Coinbase Advanced Trade orders: a CDP key signs a two-minute ES256 token per request, bound to the method,
/// host and path it authorises. An order is described by one <c>order_configuration</c> — market IOC, limit
/// GTC, limit FOK, smart-order-routed limit IOC, stop-limit GTC — and Coinbase refuses in HTTP 200 with
/// <c>success: false</c> and an <c>error_response</c>.
///
/// <para>The account is the key's portfolio (<c>/key_permissions</c>). Fees are reported in the quote
/// currency. Coinbase's sandbox answers with canned data rather than a matching engine, so it is not offered
/// as a paper environment. Written 2026-09-25 from the Advanced Trade reference; not yet run against a real
/// account.</para>
/// </summary>
internal sealed class CoinbaseOrderRoute : OrderRouteBase
{
    private const string HostName = "api.coinbase.com";

    public CoinbaseOrderRoute(IBrokerCredentialSource credentials, ILogger<CoinbaseOrderRoute> logger)
        : base(credentials, logger) { }

    internal CoinbaseOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Coinbase;
    public override string DisplayName => "Coinbase";
    public override string RouteId => "coinbase";
    public override string? PaperEnvironmentName => null;

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct, bool authenticated = true)
    {
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, $"https://{HostName}{path}{(query.Length > 0 ? "?" + query : string.Empty)}");
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            if (authenticated)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", CryptoAuth.CoinbaseJwt(Credential.Key.Trim(), Credential.Secret, method.Method, HostName, path, Now));
            }
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "message") is { Length: > 0 } m ? m : Str(root, "error"), text);
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var permissions = await CallAsync(HttpMethod.Get, "/api/v3/brokerage/key_permissions", string.Empty, null, ct).ConfigureAwait(false);
        if (permissions.TryGetProperty("can_trade", out var canTrade) && canTrade.ValueKind == JsonValueKind.False)
            throw new BrokerOrderRouteException("Coinbase: this key cannot trade — create it with the trade permission.", isRejection: true);
        var (total, available) = await CurrencyAsync("USD", ct).ConfigureAwait(false);
        var portfolio = Str(permissions, "portfolio_uuid");
        return new RouteAccount(portfolio.Length > 0 ? portfolio : KeyAccount(), "USD", total, available);
    }

    private async Task<(decimal Total, decimal Available)> CurrencyAsync(string currency, CancellationToken ct)
    {
        var root = await CallAsync(HttpMethod.Get, "/api/v3/brokerage/accounts", "limit=250", null, ct).ConfigureAwait(false);
        return ReadAccount(root, currency);
    }

    /// <summary><c>{"accounts":[{"currency","available_balance":{"value"},"hold":{"value"}}]}</c>.</summary>
    internal static (decimal Total, decimal Available) ReadAccount(JsonElement root, string currency)
    {
        if (root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array)
            foreach (var account in accounts.EnumerateArray())
                if (string.Equals(Str(account, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                {
                    var available = account.TryGetProperty("available_balance", out var a) ? Dec(a, "value") : 0m;
                    var hold = account.TryGetProperty("hold", out var h) ? Dec(h, "value") : 0m;
                    return (available + hold, available);
                }
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        // The public market endpoint: product rules need no key.
        var product = await CallAsync(HttpMethod.Get, $"/api/v3/brokerage/market/products/{Uri.EscapeDataString(symbol)}", string.Empty, null, ct, authenticated: false)
            .ConfigureAwait(false);
        return ReadInstrument(product) ?? throw new InvalidDataException($"Coinbase returned no rules for {symbol}.");
    }

    /// <summary><c>{"product_id","base_increment","price_increment","base_min_size","base_max_size",
    /// "base_currency_id","quote_currency_id"}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement p)
    {
        var step = Dec(p, "base_increment");
        var tick = Dec(p, "price_increment") is > 0 and var increment ? increment : Dec(p, "quote_increment");
        if (step <= 0 || tick <= 0)
            return null;
        return new RouteInstrument(
            Str(p, "product_id"), step, step, tick,
            Units(Dec(p, "base_min_size"), step, 1), Units(Dec(p, "base_max_size"), step, long.MaxValue / 2),
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.StopLimit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(p, "quote_currency_id"))
        {
            BaseAsset = Str(p, "base_currency_id"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var root = await CallAsync(HttpMethod.Post, "/api/v3/brokerage/orders", string.Empty, OrderBody(request), ct).ConfigureAwait(false);
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
        {
            var error = root.TryGetProperty("error_response", out var e) ? e : default;
            throw RefusedInBody($"{Str(error, "message")} ({Str(error, "error")}) {Str(root, "failure_reason")}".Trim(), root.GetRawText());
        }
        var orderId = root.TryGetProperty("success_response", out var ok) ? Str(ok, "order_id") : string.Empty;
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("Coinbase acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    internal JsonObject OrderBody(RouteOrderRequest request)
    {
        var size = Num(request.Quantity);
        JsonObject configuration = request.Type switch
        {
            RouteOrderType.Market => new JsonObject { ["market_market_ioc"] = new JsonObject { ["base_size"] = size } },
            RouteOrderType.StopLimit => new JsonObject
            {
                ["stop_limit_stop_limit_gtc"] = new JsonObject
                {
                    ["base_size"] = size,
                    ["limit_price"] = Num(request.LimitPrice!.Value),
                    ["stop_price"] = Num(request.StopPrice!.Value),
                    // A buy stop triggers on a rise and a sell stop on a fall — the ordinary protective and
                    // breakout stops; the other two directions are not expressible through this seam.
                    ["stop_direction"] = request.Side == OrderSide.Buy ? "STOP_DIRECTION_STOP_UP" : "STOP_DIRECTION_STOP_DOWN",
                },
            },
            _ => request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => new JsonObject
                {
                    ["sor_limit_ioc"] = new JsonObject { ["base_size"] = size, ["limit_price"] = Num(request.LimitPrice!.Value) },
                },
                RouteTimeInForce.FillOrKill => new JsonObject
                {
                    ["limit_limit_fok"] = new JsonObject { ["base_size"] = size, ["limit_price"] = Num(request.LimitPrice!.Value) },
                },
                _ => new JsonObject
                {
                    ["limit_limit_gtc"] = new JsonObject { ["base_size"] = size, ["limit_price"] = Num(request.LimitPrice!.Value), ["post_only"] = false },
                },
            },
        };
        return new JsonObject
        {
            ["client_order_id"] = Token(request.ClientOrderId, id => id.Length <= 64 ? id : Hex(id, 32)),
            ["product_id"] = request.Symbol,
            ["side"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["order_configuration"] = configuration,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var root = await CallAsync(HttpMethod.Post, "/api/v3/brokerage/orders/batch_cancel", string.Empty,
            new JsonObject { ["order_ids"] = new JsonArray(order.OrderId) }, ct).ConfigureAwait(false);
        if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0 &&
            results[0].TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
        {
            throw RefusedInBody(Str(results[0], "failure_reason"), root.GetRawText());
        }
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(HttpMethod.Get, "/api/v3/brokerage/orders/historical/batch",
            $"product_ids={Uri.EscapeDataString(symbol)}&order_status=OPEN", null, ct).ConfigureAwait(false);
        var orders = new List<RouteOrder>();
        if (root.TryGetProperty("orders", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var o in list.EnumerateArray())
                orders.Add(ReadOrder(o));
        return orders;
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var root = await CallAsync(HttpMethod.Get, $"/api/v3/brokerage/orders/historical/{Uri.EscapeDataString(orderId)}", string.Empty, null, ct)
            .ConfigureAwait(false);
        return root.TryGetProperty("order", out var order) ? ReadOrder(order) : null;
    }

    /// <summary><c>{"order_id","client_order_id","product_id","side","status","filled_size","average_filled_price",
    /// "total_fees","order_configuration":{…},"reject_reason","last_fill_time","created_time"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var configuration = o.TryGetProperty("order_configuration", out var c) && c.ValueKind == JsonValueKind.Object
            ? c.EnumerateObject().FirstOrDefault()
            : default;
        var terms = configuration.Value;
        var kind = configuration.Name ?? string.Empty;
        var filled = Dec(o, "filled_size");
        return new RouteOrder(
            Str(o, "order_id"),
            EngineId(Str(o, "client_order_id")),
            Str(o, "product_id"),
            Str(o, "side") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            kind.StartsWith("market", StringComparison.Ordinal) ? RouteOrderType.Market
                : kind.StartsWith("stop_limit", StringComparison.Ordinal) ? RouteOrderType.StopLimit
                : RouteOrderType.Limit,
            kind.Contains("fok", StringComparison.Ordinal) ? RouteTimeInForce.FillOrKill
                : kind.Contains("ioc", StringComparison.Ordinal) ? RouteTimeInForce.ImmediateOrCancel
                : RouteTimeInForce.GoodTillCancelled,
            Dec(terms, "base_size"),
            Positive(terms, "limit_price"),
            Positive(terms, "stop_price"),
            Str(o, "status") switch
            {
                "PENDING" or "QUEUED" => RouteOrderStatus.PendingNew,
                "OPEN" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "FILLED" => RouteOrderStatus.Filled,
                "CANCELLED" => RouteOrderStatus.Cancelled,
                "CANCEL_QUEUED" => RouteOrderStatus.PendingCancel,
                "EXPIRED" => RouteOrderStatus.Expired,
                "FAILED" => RouteOrderStatus.Rejected,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            Positive(o, "average_filled_price"),
            Dec(o, "total_fees"),
            // Coinbase charges in the quote currency: the part after the dash in the product id.
            Str(o, "product_id").Split('-') is [_, var quote] ? quote : string.Empty,
            Str(o, "reject_reason") is { Length: > 0 } reason && reason != "REJECT_REASON_UNSPECIFIED" ? reason : null,
            ParseTime(Str(o, "last_fill_time") is { Length: > 0 } fill ? fill : Str(o, "created_time"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await CurrencyAsync(rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var product = await CallAsync(HttpMethod.Get, $"/api/v3/brokerage/market/products/{Uri.EscapeDataString(symbol)}", string.Empty, null, ct, authenticated: false)
            .ConfigureAwait(false);
        return Positive(product, "price") is { } price ? new RoutePrice(price, Now.UtcDateTime) : null;
    }
}
