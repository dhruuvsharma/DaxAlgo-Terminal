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

namespace TradingTerminal.Infrastructure.Bitget;

/// <summary>
/// Bitget v2 spot orders. Signed with base64 HMAC-SHA256 over timestamp, method, path with query, and body,
/// plus the passphrase header. Refusals are HTTP 200 with a <c>code</c> other than <c>00000</c>.
///
/// <para><b>Limit orders only.</b> A Bitget spot market buy is sized in the quote coin, which cannot be a
/// whole number of units of the base; rather than approximate a quantity, market orders are not offered —
/// an immediate-or-cancel limit is the exact substitute. Bitget has no spot test environment. Written
/// 2026-09-25 from Bitget's v2 spot reference; not yet run against a real account.</para>
/// </summary>
internal sealed class BitgetOrderRoute : OrderRouteBase
{
    private const string Host = "https://api.bitget.com";

    public BitgetOrderRoute(IBrokerCredentialSource credentials, ILogger<BitgetOrderRoute> logger)
        : base(credentials, logger) { }

    internal BitgetOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Bitget;
    public override string DisplayName => "Bitget";
    public override string RouteId => "bitget";
    public override string? PaperEnvironmentName => null;

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct)
    {
        var json = body?.ToJsonString() ?? string.Empty;
        var pathAndQuery = query.Length > 0 ? $"{path}?{query}" : path;
        var (status, root, text) = await SendAsync(() =>
        {
            var stamp = Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new HttpRequestMessage(method, Host + pathAndQuery);
            if (body is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var credential = Credential;
            request.Headers.TryAddWithoutValidation("ACCESS-KEY", credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("ACCESS-TIMESTAMP", stamp);
            request.Headers.TryAddWithoutValidation("ACCESS-PASSPHRASE", credential.Passphrase);
            request.Headers.TryAddWithoutValidation("ACCESS-SIGN", CryptoAuth.BitgetSignature(stamp, method.Method, pathAndQuery, json, credential.Secret));
            request.Headers.TryAddWithoutValidation("locale", "en-US");
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "msg"), text);
        if (Str(root, "code") is { Length: > 0 } code && code != "00000")
            throw RefusedInBody($"{Str(root, "msg")} (code {code})", text);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var info = await CallAsync(HttpMethod.Get, "/api/v2/spot/account/info", string.Empty, null, ct).ConfigureAwait(false);
        var (total, available) = await CoinAsync("USDT", ct).ConfigureAwait(false);
        var user = Str(info, "userId");
        return new RouteAccount(user.Length > 0 ? user : KeyAccount(), "USDT", total, available);
    }

    private async Task<(decimal Total, decimal Available)> CoinAsync(string coin, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, "/api/v2/spot/account/assets", $"coin={Uri.EscapeDataString(coin)}", null, ct).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var asset in data.EnumerateArray())
                if (string.Equals(Str(asset, "coin"), coin, StringComparison.OrdinalIgnoreCase))
                    return (Dec(asset, "available") + Dec(asset, "frozen") + Dec(asset, "locked"), Dec(asset, "available"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, "/api/v2/spot/public/symbols", $"symbol={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return ReadInstrument(data) ?? throw new InvalidDataException($"Bitget returned no rules for {symbol}.");
    }

    /// <summary><c>[{"symbol","baseCoin","quoteCoin","minTradeAmount","maxTradeAmount","quantityPrecision",
    /// "pricePrecision"}]</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;
        var s = data[0];
        var step = Step((int)Dec(s, "quantityPrecision"));
        var tick = Step((int)Dec(s, "pricePrecision"));
        return new RouteInstrument(
            Str(s, "symbol"), step, step, tick,
            Units(Dec(s, "minTradeAmount"), step, 1), Units(Dec(s, "maxTradeAmount"), step, long.MaxValue / 2),
            RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(s, "quoteCoin"))
        {
            BaseAsset = Str(s, "baseCoin"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["symbol"] = request.Symbol,
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["orderType"] = "limit",
            ["force"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => "ioc",
                RouteTimeInForce.FillOrKill => "fok",
                _ => "gtc",
            },
            ["price"] = Num(request.LimitPrice ?? throw new BrokerOrderRouteException("Bitget orders need a limit price.", isRejection: true)),
            ["size"] = Num(request.Quantity),
            ["clientOid"] = Token(request.ClientOrderId, id => id.Length <= 40 ? id : Hex(id, 32)),
        };
        var data = await CallAsync(HttpMethod.Post, "/api/v2/spot/trade/place-order", string.Empty, body, ct).ConfigureAwait(false);
        var orderId = Str(data, "orderId");
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("Bitget acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(HttpMethod.Post, "/api/v2/spot/trade/cancel-order", string.Empty,
            new JsonObject { ["symbol"] = order.Symbol, ["orderId"] = order.OrderId }, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        ReadOrders(await CallAsync(HttpMethod.Get, "/api/v2/spot/trade/unfilled-orders", $"symbol={Uri.EscapeDataString(symbol)}", null, ct)
            .ConfigureAwait(false));

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var orders = ReadOrders(await CallAsync(HttpMethod.Get, "/api/v2/spot/trade/orderInfo", $"orderId={Uri.EscapeDataString(orderId)}", null, ct)
            .ConfigureAwait(false));
        return orders.Count > 0 ? orders[0] : null;
    }

    /// <summary><c>[{"orderId","clientOid","symbol","side","orderType","force","price","size","baseVolume",
    /// "priceAvg","status","feeDetail":"{\"BTC\":{\"totalFee\":-0.0001}}","uTime"}]</c>. The fee detail is a
    /// JSON string keyed by coin; a charge is negative.</summary>
    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement data)
    {
        var orders = new List<RouteOrder>();
        if (data.ValueKind != JsonValueKind.Array)
            return orders;
        foreach (var o in data.EnumerateArray())
        {
            var (fee, feeCurrency) = ReadFee(Str(o, "feeDetail"));
            var force = Str(o, "force");
            orders.Add(new RouteOrder(
                Str(o, "orderId"),
                EngineId(Str(o, "clientOid")),
                Str(o, "symbol"),
                Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
                Str(o, "orderType") == "market" ? RouteOrderType.Market : RouteOrderType.Limit,
                force switch { "ioc" => RouteTimeInForce.ImmediateOrCancel, "fok" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
                Dec(o, "size"),
                Positive(o, "price"),
                null,
                Str(o, "status") switch
                {
                    "init" or "new" => RouteOrderStatus.PendingNew,
                    "live" => RouteOrderStatus.Working,
                    "partially_filled" => RouteOrderStatus.PartiallyFilled,
                    "filled" => RouteOrderStatus.Filled,
                    "cancelled" => RouteOrderStatus.Cancelled,
                    _ => RouteOrderStatus.Unknown,
                },
                Dec(o, "baseVolume"),
                Positive(o, "priceAvg"),
                fee,
                feeCurrency,
                null,
                Utc((long)Dec(o, "uTime"))));
        }
        return orders;
    }

    internal static (decimal Fee, string Currency) ReadFee(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
            return (0m, string.Empty);
        var root = Parse(detail);
        if (root.ValueKind == JsonValueKind.Object)
            foreach (var coin in root.EnumerateObject())
                if (coin.Value.ValueKind == JsonValueKind.Object && coin.Value.TryGetProperty("totalFee", out var total))
                    return (Math.Abs(Dec(total)), coin.Name);
        return (0m, string.Empty);
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await CoinAsync(rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, "/api/v2/spot/market/tickers", $"symbol={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && Positive(data[0], "lastPr") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
