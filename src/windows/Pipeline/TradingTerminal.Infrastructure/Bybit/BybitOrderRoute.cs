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

namespace TradingTerminal.Infrastructure.Bybit;

/// <summary>
/// Bybit v5 spot orders on the unified account. Signed with HMAC-SHA256 over timestamp, key, receive window
/// and the query (GET) or JSON body (POST). Bybit answers refusals with HTTP 200 and a non-zero
/// <c>retCode</c>; those are read as refusals.
///
/// <para>A spot <i>market buy</i> is sized in the quote coin unless it says otherwise, so every market order
/// carries <c>marketUnit: baseCoin</c> — without it, "buy 0.01" would spend 0.01 USDT. Spot fees are taken
/// in the coin received: the base coin on a buy, the quote coin on a sell.</para>
///
/// <para>Written 2026-09-25 from Bybit's v5 reference; paper is the testnet (api-testnet.bybit.com, its own
/// keys). Not yet run against a real account.</para>
/// </summary>
internal sealed class BybitOrderRoute : OrderRouteBase
{
    private const string Window = "5000";

    public BybitOrderRoute(IBrokerCredentialSource credentials, ILogger<BybitOrderRoute> logger)
        : base(credentials, logger) { }

    internal BybitOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Bybit;
    public override string DisplayName => "Bybit";
    public override string RouteId => "bybit";
    public override string? PaperEnvironmentName => "TESTNET";

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api.bybit.com" : "https://api-testnet.bybit.com";

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct)
    {
        var json = body?.ToJsonString();
        var (status, root, text) = await SendAsync(() =>
        {
            var stamp = Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new HttpRequestMessage(method, $"{Host(environment)}{path}{(query.Length > 0 ? "?" + query : string.Empty)}");
            if (json is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("X-BAPI-API-KEY", Credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("X-BAPI-TIMESTAMP", stamp);
            request.Headers.TryAddWithoutValidation("X-BAPI-RECV-WINDOW", Window);
            request.Headers.TryAddWithoutValidation("X-BAPI-SIGN",
                CryptoAuth.BybitSignature(stamp, Credential.Key.Trim(), Window, json ?? query, Credential.Secret));
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "retMsg"), text);
        if (Dec(root, "retCode") != 0)
            throw RefusedInBody($"{Str(root, "retMsg")} (retCode {Str(root, "retCode")})", text);
        return root.TryGetProperty("result", out var result) ? result : root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var key = await CallAsync(environment, HttpMethod.Get, "/v5/user/query-api", string.Empty, null, ct).ConfigureAwait(false);
        var (total, available) = await CoinAsync(environment, "USDT", ct).ConfigureAwait(false);
        var user = Str(key, "userID");
        return new RouteAccount(user.Length > 0 ? user : KeyAccount(), "USDT", total, available);
    }

    /// <summary>(wallet balance, available) of one coin on the unified account.</summary>
    private async Task<(decimal Total, decimal Available)> CoinAsync(RouteEnvironment environment, string coin, CancellationToken ct)
    {
        var result = await CallAsync(environment, HttpMethod.Get, "/v5/account/wallet-balance",
            $"accountType=UNIFIED&coin={Uri.EscapeDataString(coin)}", null, ct).ConfigureAwait(false);
        return ReadCoin(result, coin);
    }

    /// <summary><c>{"list":[{"coin":[{"coin","walletBalance","availableToWithdraw"}]}]}</c>.</summary>
    internal static (decimal Total, decimal Available) ReadCoin(JsonElement result, string coin)
    {
        if (result.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var account in list.EnumerateArray())
                if (account.TryGetProperty("coin", out var coins) && coins.ValueKind == JsonValueKind.Array)
                    foreach (var c in coins.EnumerateArray())
                        if (string.Equals(Str(c, "coin"), coin, StringComparison.OrdinalIgnoreCase))
                        {
                            var total = Dec(c, "walletBalance");
                            var free = Dec(c, "availableToWithdraw");
                            return (total, free > 0 ? free : total);
                        }
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var result = await CallAsync(environment, HttpMethod.Get, "/v5/market/instruments-info",
            $"category=spot&symbol={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return ReadInstrument(result) ?? throw new InvalidDataException($"Bybit returned no rules for {symbol}.");
    }

    /// <summary><c>{"list":[{"symbol","baseCoin","quoteCoin","lotSizeFilter":{"basePrecision","minOrderQty",
    /// "maxOrderQty"},"priceFilter":{"tickSize"}}]}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement result)
    {
        if (!result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0)
            return null;
        var s = list[0];
        var lot = s.TryGetProperty("lotSizeFilter", out var l) ? l : default;
        var price = s.TryGetProperty("priceFilter", out var p) ? p : default;
        var step = Dec(lot, "basePrecision");
        var tick = Dec(price, "tickSize");
        if (step <= 0 || tick <= 0)
            return null;
        return new RouteInstrument(
            Str(s, "symbol"), step, step, tick,
            Units(Dec(lot, "minOrderQty"), step, 1), Units(Dec(lot, "maxOrderQty"), step, long.MaxValue / 2),
            RouteOrderTypes.Market | RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(s, "quoteCoin"))
        {
            BaseAsset = Str(s, "baseCoin"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var result = await CallAsync(environment, HttpMethod.Post, "/v5/order/create", string.Empty, OrderBody(request), ct).ConfigureAwait(false);
        var orderId = Str(result, "orderId");
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("Bybit acknowledged the order without an order id.", isRejection: false);
        return Pending(request, orderId);
    }

    internal JsonObject OrderBody(RouteOrderRequest request)
    {
        var body = new JsonObject
        {
            ["category"] = "spot",
            ["symbol"] = request.Symbol,
            ["side"] = request.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["orderType"] = request.Type == RouteOrderType.Market ? "Market" : "Limit",
            ["qty"] = Num(request.Quantity),
            ["orderLinkId"] = Token(request.ClientOrderId, id => id.Length <= 36 ? id : Hex(id, 32)),
        };
        if (request.Type == RouteOrderType.Market)
        {
            body["marketUnit"] = "baseCoin";
        }
        else
        {
            body["price"] = Num(request.LimitPrice!.Value);
            body["timeInForce"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => "IOC",
                RouteTimeInForce.FillOrKill => "FOK",
                _ => "GTC",
            };
        }
        return body;
    }

    private RouteOrder Pending(RouteOrderRequest request, string orderId) => new(
        orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
        request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Post, "/v5/order/cancel", string.Empty,
            new JsonObject { ["category"] = "spot", ["symbol"] = order.Symbol, ["orderId"] = order.OrderId }, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var result = await CallAsync(environment, HttpMethod.Get, "/v5/order/realtime",
            $"category=spot&symbol={Uri.EscapeDataString(symbol)}", null, ct).ConfigureAwait(false);
        return ReadOrders(result);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var query = $"category=spot&orderId={Uri.EscapeDataString(orderId)}";
        var open = ReadOrders(await CallAsync(environment, HttpMethod.Get, "/v5/order/realtime", query, null, ct).ConfigureAwait(false));
        if (open.Count > 0)
            return open[0];
        var history = ReadOrders(await CallAsync(environment, HttpMethod.Get, "/v5/order/history", query, null, ct).ConfigureAwait(false));
        return history.Count > 0 ? history[0] : null;
    }

    /// <summary><c>{"list":[{"orderId","orderLinkId","symbol","side","orderType","price","qty","timeInForce",
    /// "orderStatus","cumExecQty","cumExecValue","avgPrice","cumExecFee","updatedTime","rejectReason"}]}</c>.</summary>
    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement result)
    {
        var orders = new List<RouteOrder>();
        if (!result.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return orders;
        foreach (var o in list.EnumerateArray())
        {
            var executed = Dec(o, "cumExecQty");
            var value = Dec(o, "cumExecValue");
            var side = Str(o, "side") == "Sell" ? OrderSide.Sell : OrderSide.Buy;
            var (fee, feeCurrency) = ReadFee(o, side);
            orders.Add(new RouteOrder(
                Str(o, "orderId"),
                EngineId(Str(o, "orderLinkId")),
                Str(o, "symbol"),
                side,
                Str(o, "orderType") == "Market" ? RouteOrderType.Market : RouteOrderType.Limit,
                Str(o, "timeInForce") switch { "IOC" => RouteTimeInForce.ImmediateOrCancel, "FOK" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
                Dec(o, "qty"),
                Positive(o, "price"),
                Positive(o, "triggerPrice"),
                Str(o, "orderStatus") switch
                {
                    "New" or "Untriggered" or "Triggered" => RouteOrderStatus.Working,
                    "Created" => RouteOrderStatus.PendingNew,
                    "PartiallyFilled" => RouteOrderStatus.PartiallyFilled,
                    "Filled" => RouteOrderStatus.Filled,
                    "Cancelled" or "PartiallyFilledCanceled" or "Deactivated" => RouteOrderStatus.Cancelled,
                    "Rejected" => RouteOrderStatus.Rejected,
                    _ => RouteOrderStatus.Unknown,
                },
                executed,
                executed > 0 && value > 0 ? value / executed : Positive(o, "avgPrice"),
                fee,
                feeCurrency,
                Str(o, "rejectReason") is { Length: > 0 } reason && reason != "EC_NoError" ? reason : null,
                Utc((long)Dec(o, "updatedTime"))));
        }
        return orders;
    }

    /// <summary>The fee and its coin: <c>cumFeeDetail</c> names the coin when present; otherwise Bybit's spot
    /// rule — the coin received (base on a buy, quote on a sell), filled in by the adapter's rules.</summary>
    private static (decimal Fee, string Currency) ReadFee(JsonElement o, OrderSide side)
    {
        if (o.TryGetProperty("cumFeeDetail", out var detail) && detail.ValueKind == JsonValueKind.Object)
            foreach (var coin in detail.EnumerateObject())
                return (Dec(coin.Value), coin.Name);
        var fee = Dec(o, "cumExecFee");
        return (fee, fee == 0 ? string.Empty : side == OrderSide.Buy ? BaseOf(Str(o, "symbol")) : QuoteOf(Str(o, "symbol")));
    }

    private static readonly string[] Quotes = ["USDT", "USDC", "USDE", "BTC", "ETH", "EUR", "DAI", "BRL"];

    private static string QuoteOf(string symbol) => Quotes.FirstOrDefault(q => symbol.EndsWith(q, StringComparison.Ordinal)) ?? string.Empty;

    private static string BaseOf(string symbol) => QuoteOf(symbol) is { Length: > 0 } quote ? symbol[..^quote.Length] : string.Empty;

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await CoinAsync(environment, rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/v5/market/tickers?category=spot&symbol={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status == 200 && root.TryGetProperty("result", out var result) && result.TryGetProperty("list", out var list) &&
               list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0 && Positive(list[0], "lastPrice") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
