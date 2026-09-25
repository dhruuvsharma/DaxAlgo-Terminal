using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Binance;

/// <summary>
/// Binance spot orders, and the shape MEXC copied: <c>/api/v3/order</c> and friends, parameters in the query
/// string, an HMAC-SHA256 signature of that query appended as <c>signature</c>.
///
/// <para><b>Fees</b> are read from <c>/api/v3/myTrades</c> for any order with a fill — the order endpoints
/// do not carry them. Binance takes the fee in the coin bought unless the account pays with BNB, and the
/// engine needs to know which to reconcile the balance.</para>
///
/// <para>Written 2026-09-25 from Binance's spot API reference; the testnet (testnet.binance.vision, its own
/// keys) is the paper environment. Not yet run against a real account.</para>
/// </summary>
internal abstract class BinanceShapedOrderRoute : OrderRouteBase
{
    protected BinanceShapedOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider? time = null, HttpMessageHandler? handler = null)
        : base(credentials, logger, time, handler) { }

    protected abstract string Host(RouteEnvironment environment);

    protected abstract string KeyHeader { get; }

    /// <summary>The currency the account's cash is reported in.</summary>
    protected virtual string CashCurrency => "USDT";

    protected virtual string Signature(string query, string secret) => CryptoAuth.BinanceSignature(query, secret);

    private Task<(int Status, JsonElement Root, string Body)> SignedAsync(
        RouteEnvironment environment, HttpMethod method, string path, string query, CancellationToken ct) =>
        SendAsync(() =>
        {
            var signed = (query.Length > 0 ? query + "&" : string.Empty) + $"timestamp={Now.ToUnixTimeMilliseconds()}&recvWindow=5000";
            var request = new HttpRequestMessage(method, $"{Host(environment)}{path}?{signed}&signature={Signature(signed, Credential.Secret)}");
            request.Headers.TryAddWithoutValidation(KeyHeader, Credential.Key.Trim());
            return request;
        }, ct);

    private async Task<JsonElement> ReadAsync(RouteEnvironment environment, HttpMethod method, string path, string query, CancellationToken ct)
    {
        var (status, root, body) = await SignedAsync(environment, method, path, query, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "msg"), body, IsRejection(status, root));
        return root;
    }

    /// <summary>-1007 is Binance saying it timed out waiting for its own engine: the order may exist.</summary>
    private static bool IsRejection(int status, JsonElement root) =>
        status is >= 400 and < 500 and not 408 && Dec(root, "code") is not (-1007 or -1006 or -1000);

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var root = await ReadAsync(environment, HttpMethod.Get, "/api/v3/account", string.Empty, ct).ConfigureAwait(false);
        return ReadAccount(root);
    }

    protected virtual RouteAccount ReadAccount(JsonElement root)
    {
        var (total, free) = Balance(root, CashCurrency);
        var uid = Str(root, "uid");
        return new RouteAccount(uid.Length > 0 ? uid : KeyAccount(), CashCurrency, total, free);
    }

    /// <summary>(free + locked, free) of one asset in an account answer.</summary>
    internal static (decimal Total, decimal Free) Balance(JsonElement account, string asset)
    {
        if (account.TryGetProperty("balances", out var balances) && balances.ValueKind == JsonValueKind.Array)
            foreach (var balance in balances.EnumerateArray())
                if (string.Equals(Str(balance, "asset"), asset, StringComparison.OrdinalIgnoreCase))
                    return (Dec(balance, "free") + Dec(balance, "locked"), Dec(balance, "free"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{Host(environment)}/api/v3/exchangeInfo?symbol={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        EnsureSuccess(status, body, Str(root, "msg"));
        return ReadInstrument(root, symbol) ?? throw new InvalidDataException($"{DisplayName} returned no rules for {symbol}.");
    }

    /// <summary><c>{"symbols":[{"symbol","baseAsset","quoteAsset","orderTypes":[…],"filters":[PRICE_FILTER, LOT_SIZE…]}]}</c>.</summary>
    internal virtual RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("symbols", out var symbols) || symbols.ValueKind != JsonValueKind.Array || symbols.GetArrayLength() == 0)
            return null;
        var s = symbols[0];
        decimal tick = 0, step = 0, minQty = 0, maxQty = 0;
        if (s.TryGetProperty("filters", out var filters))
            foreach (var filter in filters.EnumerateArray())
            {
                switch (Str(filter, "filterType"))
                {
                    case "PRICE_FILTER":
                        tick = Dec(filter, "tickSize");
                        break;
                    case "LOT_SIZE":
                        step = Dec(filter, "stepSize");
                        minQty = Dec(filter, "minQty");
                        maxQty = Dec(filter, "maxQty");
                        break;
                }
            }
        if (tick <= 0 || step <= 0)
            return null;
        var types = RouteOrderTypes.None;
        if (s.TryGetProperty("orderTypes", out var orderTypes))
            foreach (var type in orderTypes.EnumerateArray())
                types |= type.GetString() switch
                {
                    "MARKET" => RouteOrderTypes.Market,
                    "LIMIT" => RouteOrderTypes.Limit,
                    "STOP_LOSS_LIMIT" => RouteOrderTypes.StopLimit,
                    _ => RouteOrderTypes.None,
                };
        return new RouteInstrument(
            Str(s, "symbol"), step, step, tick,
            Units(minQty, step, 1), Units(maxQty, step, long.MaxValue / 2),
            types, RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(s, "quoteAsset"))
        {
            BaseAsset = Str(s, "baseAsset"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var root = await ReadAsync(environment, HttpMethod.Post, "/api/v3/order", OrderQuery(request), ct).ConfigureAwait(false);
        return ReadOrder(root) ?? throw new BrokerOrderRouteException($"{DisplayName} acknowledged the order without an order id.", isRejection: false);
    }

    /// <summary>The query for one new order, in Binance's vocabulary.</summary>
    internal virtual string OrderQuery(RouteOrderRequest request)
    {
        var client = Token(request.ClientOrderId, id => id.Length <= 36 ? id : Hex(id, 32));
        var query = $"symbol={Uri.EscapeDataString(request.Symbol)}&side={(request.Side == OrderSide.Buy ? "BUY" : "SELL")}"
                    + $"&quantity={Num(request.Quantity)}&newClientOrderId={Uri.EscapeDataString(client)}&newOrderRespType=FULL";
        return request.Type switch
        {
            RouteOrderType.Market => query + "&type=MARKET",
            RouteOrderType.StopLimit => query + $"&type=STOP_LOSS_LIMIT&timeInForce={Tif(request.TimeInForce)}&price={Num(request.LimitPrice!.Value)}&stopPrice={Num(request.StopPrice!.Value)}",
            _ => query + $"&type=LIMIT&timeInForce={Tif(request.TimeInForce)}&price={Num(request.LimitPrice!.Value)}",
        };
    }

    protected static string Tif(RouteTimeInForce tif) => tif switch
    {
        RouteTimeInForce.ImmediateOrCancel => "IOC",
        RouteTimeInForce.FillOrKill => "FOK",
        _ => "GTC",
    };

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await ReadAsync(environment, HttpMethod.Delete, "/api/v3/order",
            $"symbol={Uri.EscapeDataString(order.Symbol)}&orderId={Uri.EscapeDataString(order.OrderId)}", ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await ReadAsync(environment, HttpMethod.Get, "/api/v3/openOrders", $"symbol={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        var orders = new List<RouteOrder>();
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (ReadOrder(item) is { } order)
                    orders.Add(order.FilledQuantity > 0 ? await WithFeesAsync(environment, order, ct).ConfigureAwait(false) : order);
        return orders;
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var (status, root, body) = await SignedAsync(environment, HttpMethod.Get, "/api/v3/order",
            $"symbol={Uri.EscapeDataString(symbol)}&orderId={Uri.EscapeDataString(orderId)}", ct).ConfigureAwait(false);
        if (Dec(root, "code") == -2013)
            return null;
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "msg"), body);
        return ReadOrder(root) is { } order && order.FilledQuantity > 0 ? await WithFeesAsync(environment, order, ct).ConfigureAwait(false) : ReadOrder(root);
    }

    /// <summary>An order with its fee, summed from its trades.</summary>
    private async Task<RouteOrder> WithFeesAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, order.Symbol, ct).ConfigureAwait(false);
        var root = await ReadAsync(environment, HttpMethod.Get, "/api/v3/myTrades",
            $"symbol={Uri.EscapeDataString(order.Symbol)}&orderId={Uri.EscapeDataString(order.OrderId)}", ct).ConfigureAwait(false);
        var (fee, currency) = SumFees(root, rules.BaseAsset, rules.Currency);
        return order with { Fee = fee, FeeCurrency = currency };
    }

    /// <summary>
    /// <c>[{"commission","commissionAsset",…}]</c> summed by asset. When an order's fees fell in more than one
    /// asset the base asset is reported (it is the one that changes the balance the engine reconciles), then
    /// the quote asset; a fee in a third asset (BNB) is reported as such and is not cash in the ledger.
    /// </summary>
    internal static (decimal Fee, string Currency) SumFees(JsonElement trades, string baseAsset, string quoteAsset)
    {
        var byAsset = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (trades.ValueKind == JsonValueKind.Array)
            foreach (var trade in trades.EnumerateArray())
            {
                var asset = Str(trade, "commissionAsset");
                byAsset[asset] = byAsset.GetValueOrDefault(asset) + Dec(trade, "commission");
            }
        foreach (var asset in new[] { baseAsset, quoteAsset })
            if (asset.Length > 0 && byAsset.TryGetValue(asset, out var fee))
                return (fee, asset);
        return byAsset.Count > 0 ? (byAsset.First().Value, byAsset.First().Key) : (0m, string.Empty);
    }

    /// <summary>An order answer: <c>{"orderId","clientOrderId","status","origQty","executedQty",
    /// "cummulativeQuoteQty","price","stopPrice","type","side","timeInForce","updateTime","fills":[…]}</c>.</summary>
    internal RouteOrder? ReadOrder(JsonElement o)
    {
        var orderId = Str(o, "orderId");
        if (orderId.Length == 0)
            return null;
        var executed = Dec(o, "executedQty");
        var cost = Dec(o, "cummulativeQuoteQty");
        var fee = 0m;
        var feeCurrency = string.Empty;
        if (o.TryGetProperty("fills", out var fills) && fills.ValueKind == JsonValueKind.Array && fills.GetArrayLength() > 0)
        {
            foreach (var fill in fills.EnumerateArray())
                fee += Dec(fill, "commission");
            feeCurrency = Str(fills[0], "commissionAsset");
        }

        var time = (long)Dec(o, "updateTime");
        return new RouteOrder(
            orderId,
            EngineId(Str(o, "clientOrderId")),
            Str(o, "symbol"),
            Str(o, "side") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "type") switch
            {
                "MARKET" => RouteOrderType.Market,
                "STOP_LOSS" => RouteOrderType.Stop,
                "STOP_LOSS_LIMIT" or "TAKE_PROFIT_LIMIT" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Limit,
            },
            Str(o, "timeInForce") switch { "IOC" => RouteTimeInForce.ImmediateOrCancel, "FOK" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
            Dec(o, "origQty"),
            Positive(o, "price"),
            Positive(o, "stopPrice"),
            Status(Str(o, "status")),
            executed,
            executed > 0 && cost > 0 ? cost / executed : null,
            fee,
            feeCurrency,
            null,
            Utc(time > 0 ? time : (long)Dec(o, "transactTime")));
    }

    protected virtual RouteOrderStatus Status(string status) => status switch
    {
        "NEW" => RouteOrderStatus.Working,
        "PENDING_NEW" => RouteOrderStatus.PendingNew,
        "PARTIALLY_FILLED" => RouteOrderStatus.PartiallyFilled,
        "FILLED" => RouteOrderStatus.Filled,
        "CANCELED" or "PARTIALLY_CANCELED" => RouteOrderStatus.Cancelled,
        "PENDING_CANCEL" => RouteOrderStatus.PendingCancel,
        "REJECTED" => RouteOrderStatus.Rejected,
        "EXPIRED" or "EXPIRED_IN_MATCH" => RouteOrderStatus.Expired,
        _ => RouteOrderStatus.Unknown,
    };

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var root = await ReadAsync(environment, HttpMethod.Get, "/api/v3/account", string.Empty, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, Balance(root, rules.BaseAsset).Total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, $"{Host(environment)}/api/v3/ticker/price?symbol={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status is >= 200 and < 300 && Positive(root, "price") is { } price ? new RoutePrice(price, Now.UtcDateTime) : null;
    }
}

/// <summary>Binance spot. Paper is the spot testnet, which issues its own keys at testnet.binance.vision.</summary>
internal sealed class BinanceOrderRoute : BinanceShapedOrderRoute
{
    public BinanceOrderRoute(IBrokerCredentialSource credentials, ILogger<BinanceOrderRoute> logger)
        : base(credentials, logger) { }

    internal BinanceOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Binance;
    public override string DisplayName => "Binance";
    public override string RouteId => "binance";
    public override string? PaperEnvironmentName => "TESTNET";
    protected override string KeyHeader => "X-MBX-APIKEY";

    protected override string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api.binance.com" : "https://testnet.binance.vision";
}
