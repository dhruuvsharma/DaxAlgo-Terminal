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

namespace TradingTerminal.Infrastructure.Bitvavo;

/// <summary>
/// Bitvavo v2 orders, in euros. Signed with HMAC-SHA256 over timestamp, method, <c>/v2</c> path with query,
/// and body. Client order ids must be UUIDs, so the engine's id is turned into one and remembered. Prices
/// have five significant digits, so the tick is taken at the price when the book attaches.
///
/// <para>Bitvavo has no test environment and names no account in its API (the key stands in). Written
/// 2026-09-25 from Bitvavo's v2 reference; not yet run against a real account.</para>
/// </summary>
internal sealed class BitvavoOrderRoute : OrderRouteBase
{
    private const string Host = "https://api.bitvavo.com";

    public BitvavoOrderRoute(IBrokerCredentialSource credentials, ILogger<BitvavoOrderRoute> logger)
        : base(credentials, logger) { }

    internal BitvavoOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Bitvavo;
    public override string DisplayName => "Bitvavo";
    public override string RouteId => "bitvavo";
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
            request.Headers.TryAddWithoutValidation("Bitvavo-Access-Key", Credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("Bitvavo-Access-Timestamp", stamp);
            request.Headers.TryAddWithoutValidation("Bitvavo-Access-Signature", CryptoAuth.BitvavoSignature(stamp, method.Method, pathAndQuery, json, Credential.Secret));
            request.Headers.TryAddWithoutValidation("Bitvavo-Access-Window", "10000");
            return request;
        }, ct).ConfigureAwait(false);
        // 240 is "order not found": an answer, not a failure, when asking about an order.
        if (allowNotFound && Dec(root, "errorCode") == 240)
            return default;
        if (status is < 200 or >= 300)
            throw Refused(status, $"{Str(root, "error")} (errorCode {Str(root, "errorCode")})", text);
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var (total, available) = await BalanceAsync("EUR", ct).ConfigureAwait(false);
        return new RouteAccount(KeyAccount(), "EUR", total, available);
    }

    private async Task<(decimal Total, decimal Available)> BalanceAsync(string symbol, CancellationToken ct)
    {
        var balances = await CallAsync(HttpMethod.Get, $"/v2/balance?symbol={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        if (balances.ValueKind == JsonValueKind.Array)
            foreach (var b in balances.EnumerateArray())
                if (string.Equals(Str(b, "symbol"), symbol, StringComparison.OrdinalIgnoreCase))
                    return (Dec(b, "available") + Dec(b, "inOrder"), Dec(b, "available"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var price = await PriceAsync(environment, symbol, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Bitvavo has no price for {symbol}, so its price step cannot be known.");
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Host}/v2/markets?market={Uri.EscapeDataString(symbol)}"), ct)
            .ConfigureAwait(false);
        EnsureSuccess(status, body, Str(root, "error"));
        return ReadInstrument(root, price.Price) ?? throw new InvalidDataException($"Bitvavo returned no rules for {symbol}.");
    }

    /// <summary><c>{"market","base","quote","pricePrecision":5,"quantityDecimals":8,"minOrderInBaseAsset",
    /// "maxOrderInBaseAsset"}</c>; <c>pricePrecision</c> is significant digits.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement m, decimal price)
    {
        if (Str(m, "market").Length == 0)
            return null;
        var decimals = m.TryGetProperty("quantityDecimals", out _) ? (int)Dec(m, "quantityDecimals") : 8;
        var step = Step(decimals);
        var significant = (int)Dec(m, "pricePrecision") is > 0 and var digits ? digits : 5;
        var exponent = (int)Math.Floor(Math.Log10((double)price)) - (significant - 1);
        var tick = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++)
            tick = exponent < 0 ? tick / 10m : tick * 10m;
        return new RouteInstrument(
            Str(m, "market"), step, step, tick,
            Units(Dec(m, "minOrderInBaseAsset"), step, 1), Units(Dec(m, "maxOrderInBaseAsset"), step, long.MaxValue / 2),
            RouteOrderTypes.Market | RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(m, "quote"))
        {
            BaseAsset = Str(m, "base"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["market"] = request.Symbol,
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["orderType"] = request.Type == RouteOrderType.Market ? "market" : "limit",
            ["amount"] = Num(request.Quantity),
            ["clientOrderId"] = Token(request.ClientOrderId, Uuid),
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
        return ReadOrder(await CallAsync(HttpMethod.Post, "/v2/order", body, ct).ConfigureAwait(false));
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(HttpMethod.Delete, $"/v2/order?market={Uri.EscapeDataString(order.Symbol)}&orderId={Uri.EscapeDataString(order.OrderId)}", null, ct)
            .ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var orders = await CallAsync(HttpMethod.Get, $"/v2/ordersOpen?market={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return orders.ValueKind == JsonValueKind.Array ? [.. orders.EnumerateArray().Select(ReadOrder)] : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var order = await CallAsync(HttpMethod.Get, $"/v2/order?market={Uri.EscapeDataString(symbol)}&orderId={Uri.EscapeDataString(orderId)}", null, ct,
            allowNotFound: true).ConfigureAwait(false);
        return order.ValueKind == JsonValueKind.Object ? ReadOrder(order) : null;
    }

    /// <summary><c>{"orderId","clientOrderId","market","status","side","orderType","amount","price","timeInForce",
    /// "filledAmount","filledAmountQuote","feePaid","feeCurrency","updated"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var filled = Dec(o, "filledAmount");
        var quote = Dec(o, "filledAmountQuote");
        return new RouteOrder(
            Str(o, "orderId"),
            EngineId(Str(o, "clientOrderId")),
            Str(o, "market"),
            Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "orderType") == "market" ? RouteOrderType.Market : RouteOrderType.Limit,
            Str(o, "timeInForce") switch { "IOC" => RouteTimeInForce.ImmediateOrCancel, "FOK" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
            Dec(o, "amount"),
            Positive(o, "price"),
            null,
            Str(o, "status") switch
            {
                "new" or "awaitingTrigger" => RouteOrderStatus.Working,
                "partiallyFilled" => RouteOrderStatus.PartiallyFilled,
                "filled" => RouteOrderStatus.Filled,
                "expired" => RouteOrderStatus.Expired,
                "rejected" => RouteOrderStatus.Rejected,
                { } s when s.StartsWith("canceled", StringComparison.Ordinal) => RouteOrderStatus.Cancelled,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            filled > 0 && quote > 0 ? quote / filled : null,
            Dec(o, "feePaid"),
            Str(o, "feeCurrency"),
            null,
            Utc((long)Dec(o, "updated")));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await BalanceAsync(rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Host}/v2/ticker/price?market={Uri.EscapeDataString(symbol)}"), ct)
            .ConfigureAwait(false);
        return status == 200 && Positive(root, "price") is { } price ? new RoutePrice(price, Now.UtcDateTime) : null;
    }
}
