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
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.KuCoin;

/// <summary>
/// KuCoin spot orders on the trading account. Version-2 keys: the signature covers timestamp, method,
/// endpoint with query, and body, and the passphrase travels signed. Refusals are HTTP 200 with a
/// <c>code</c> other than <c>200000</c>.
///
/// <para>KuCoin reports an order's state as flags — <c>isActive</c> and <c>cancelExist</c> — and its filled
/// size and funds; the average price is funds ÷ size. KuCoin retired its sandbox, so there is no paper
/// environment. Written 2026-09-25 from KuCoin's spot reference; not yet run against a real account.</para>
/// </summary>
internal sealed class KuCoinOrderRoute : OrderRouteBase
{
    private const string Host = "https://api.kucoin.com";

    public KuCoinOrderRoute(IBrokerCredentialSource credentials, ILogger<KuCoinOrderRoute> logger)
        : base(credentials, logger) { }

    internal KuCoinOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.KuCoin;
    public override string DisplayName => "KuCoin";
    public override string RouteId => "kucoin";
    public override string? PaperEnvironmentName => null;

    private async Task<JsonElement> CallAsync(HttpMethod method, string pathAndQuery, JsonObject? body, CancellationToken ct, bool allowNotFound = false)
    {
        var json = body?.ToJsonString() ?? string.Empty;
        var (status, root, text) = await SendAsync(() =>
        {
            var stamp = Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new HttpRequestMessage(method, Host + pathAndQuery);
            if (body is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var credential = Credential;
            request.Headers.TryAddWithoutValidation("KC-API-KEY", credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("KC-API-TIMESTAMP", stamp);
            request.Headers.TryAddWithoutValidation("KC-API-SIGN", CryptoAuth.KuCoinSignature(stamp, method.Method, pathAndQuery, json, credential.Secret));
            request.Headers.TryAddWithoutValidation("KC-API-PASSPHRASE", CryptoAuth.KuCoinPassphrase(credential.Passphrase, credential.Secret));
            request.Headers.TryAddWithoutValidation("KC-API-KEY-VERSION", "2");
            return request;
        }, ct).ConfigureAwait(false);
        if (allowNotFound && status == 404)
            return default;
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "msg"), text);
        if (Str(root, "code") is { Length: > 0 } code && code != "200000")
            throw RefusedInBody($"{Str(root, "msg")} (code {code})", text);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var key = await CallAsync(HttpMethod.Get, "/api/v1/user/api-key", null, ct).ConfigureAwait(false);
        var (total, available) = await CurrencyAsync("USDT", ct).ConfigureAwait(false);
        var uid = Str(key, "uid");
        return new RouteAccount(uid.Length > 0 ? uid : KeyAccount(), "USDT", total, available);
    }

    private async Task<(decimal Total, decimal Available)> CurrencyAsync(string currency, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, $"/api/v1/accounts?currency={Uri.EscapeDataString(currency)}&type=trade", null, ct).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var account in data.EnumerateArray())
                if (string.Equals(Str(account, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                    return (Dec(account, "balance"), Dec(account, "available"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, $"/api/v2/symbols/{Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return ReadInstrument(data) ?? throw new InvalidDataException($"KuCoin returned no rules for {symbol}.");
    }

    /// <summary><c>{"symbol","baseCurrency","quoteCurrency","baseMinSize","baseMaxSize","baseIncrement","priceIncrement"}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement s)
    {
        var step = Dec(s, "baseIncrement");
        var tick = Dec(s, "priceIncrement");
        if (step <= 0 || tick <= 0)
            return null;
        return new RouteInstrument(
            Str(s, "symbol"), step, step, tick,
            Units(Dec(s, "baseMinSize"), step, 1), Units(Dec(s, "baseMaxSize"), step, long.MaxValue / 2),
            RouteOrderTypes.Market | RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(s, "quoteCurrency"))
        {
            BaseAsset = Str(s, "baseCurrency"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["clientOid"] = Token(request.ClientOrderId, id => id.Length <= 40 ? id : Hex(id, 32)),
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["symbol"] = request.Symbol,
            ["type"] = request.Type == RouteOrderType.Market ? "market" : "limit",
            ["size"] = Num(request.Quantity),
        };
        if (request.Type != RouteOrderType.Market)
        {
            body["price"] = Num(request.LimitPrice!.Value);
            body["timeInForce"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => "IOC",
                RouteTimeInForce.FillOrKill => "FOK",
                _ => "GTC",
            };
        }
        var data = await CallAsync(HttpMethod.Post, "/api/v1/orders", body, ct).ConfigureAwait(false);
        var orderId = Str(data, "orderId");
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("KuCoin acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(HttpMethod.Delete, $"/api/v1/orders/{Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, $"/api/v1/orders?status=active&symbol={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return data.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? [.. items.EnumerateArray().Select(ReadOrder)]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, $"/api/v1/orders/{Uri.EscapeDataString(orderId)}", null, ct, allowNotFound: true).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object ? ReadOrder(data) : null;
    }

    /// <summary><c>{"id","clientOid","symbol","side","type","price","size","dealSize","dealFunds","fee",
    /// "feeCurrency","timeInForce","isActive","cancelExist","createdAt"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var size = Dec(o, "size");
        var dealt = Dec(o, "dealSize");
        var funds = Dec(o, "dealFunds");
        var active = o.TryGetProperty("isActive", out var a) && a.ValueKind == JsonValueKind.True;
        var cancelled = o.TryGetProperty("cancelExist", out var c) && c.ValueKind == JsonValueKind.True;
        return new RouteOrder(
            Str(o, "id"),
            EngineId(Str(o, "clientOid")),
            Str(o, "symbol"),
            Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "type") == "market" ? RouteOrderType.Market : RouteOrderType.Limit,
            Str(o, "timeInForce") switch { "IOC" => RouteTimeInForce.ImmediateOrCancel, "FOK" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
            size,
            Positive(o, "price"),
            null,
            active
                ? dealt > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working
                : cancelled ? RouteOrderStatus.Cancelled
                : size > 0 && dealt >= size ? RouteOrderStatus.Filled
                : dealt > 0 ? RouteOrderStatus.Filled
                : RouteOrderStatus.Cancelled,
            dealt,
            dealt > 0 && funds > 0 ? funds / dealt : null,
            Dec(o, "fee"),
            Str(o, "feeCurrency"),
            null,
            Utc((long)Dec(o, "createdAt")));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await CurrencyAsync(rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/api/v1/market/orderbook/level1?symbol={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status == 200 && root.TryGetProperty("data", out var data) && Positive(data, "price") is { } price
            ? new RoutePrice(price, Utc((long)Dec(data, "time")))
            : null;
    }
}
