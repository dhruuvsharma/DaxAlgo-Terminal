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

namespace TradingTerminal.Infrastructure.CryptoCom;

/// <summary>
/// Crypto.com Exchange v1 orders: JSON-RPC-shaped POSTs whose signature covers method, id, key, the
/// parameters flattened in sorted key order, and the nonce. A refusal is a non-zero <c>code</c>.
///
/// <para>Paper is Crypto.com's UAT sandbox (uat-api.3ona.co, its own registration and keys). Written
/// 2026-09-25 from the Exchange v1 reference; not yet run against a real account.</para>
/// </summary>
internal sealed class CryptoComOrderRoute : OrderRouteBase
{
    private long _id;

    public CryptoComOrderRoute(IBrokerCredentialSource credentials, ILogger<CryptoComOrderRoute> logger)
        : base(credentials, logger) { }

    internal CryptoComOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.CryptoCom;
    public override string DisplayName => "Crypto.com";
    public override string RouteId => "crypto-com";
    public override string? PaperEnvironmentName => "UAT SANDBOX";

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api.crypto.com/exchange/v1" : "https://uat-api.3ona.co/exchange/v1";

    /// <summary>Crypto.com's parameter string: keys in order, each followed by its value; lists and objects
    /// flattened the same way, a null written as <c>null</c>.</summary>
    internal static string ParamString(JsonNode? node) => node switch
    {
        JsonObject obj => string.Concat(obj.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + (p.Value is null ? "null" : ParamString(p.Value)))),
        JsonArray array => string.Concat(array.Select(ParamString)),
        JsonValue value => value.TryGetValue<string>(out var text) ? text : value.ToJsonString(),
        _ => "null",
    };

    private async Task<JsonElement> PrivateAsync(RouteEnvironment environment, string method, JsonObject parameters, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() =>
        {
            var id = Interlocked.Increment(ref _id);
            var nonce = Now.ToUnixTimeMilliseconds();
            var key = Credential.Key.Trim();
            var sig = CryptoAuth.CryptoComSignature(method, id.ToString(System.Globalization.CultureInfo.InvariantCulture), key,
                ParamString(parameters), nonce.ToString(System.Globalization.CultureInfo.InvariantCulture), Credential.Secret);
            var envelope = new JsonObject
            {
                ["id"] = id, ["method"] = method, ["api_key"] = key, ["params"] = parameters.DeepClone(), ["nonce"] = nonce, ["sig"] = sig,
            };
            return new HttpRequestMessage(HttpMethod.Post, $"{Host(environment)}/{method}")
            {
                Content = new StringContent(envelope.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        }, ct).ConfigureAwait(false);
        var code = Dec(root, "code");
        if (status is < 200 or >= 300 || code != 0)
            throw Refused(status, $"{Str(root, "message")} (code {code})", body, status >= 500 ? false : code != 0 ? true : null);
        return root.TryGetProperty("result", out var result) ? result : root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var accounts = await PrivateAsync(environment, "private/get-accounts", [], ct).ConfigureAwait(false);
        var balance = await PrivateAsync(environment, "private/user-balance", [], ct).ConfigureAwait(false);
        var uuid = accounts.TryGetProperty("master_account", out var master) ? Str(master, "uuid") : string.Empty;
        var summary = balance.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : default;
        return new RouteAccount(uuid.Length > 0 ? uuid : KeyAccount(), "USD",
            Dec(summary, "total_cash_balance"), Dec(summary, "total_available_balance"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{Host(environment)}/public/get-instruments"), ct)
            .ConfigureAwait(false);
        EnsureSuccess(status, body, Str(root, "message"));
        return ReadInstrument(root, symbol) ?? throw new InvalidDataException($"Crypto.com returned no rules for {symbol}.");
    }

    /// <summary><c>{"result":{"data":[{"symbol","inst_type","base_ccy","quote_ccy","price_tick_size","qty_tick_size"}]}}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("result", out var result) || !result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var i in data.EnumerateArray())
        {
            if (!string.Equals(Str(i, "symbol"), symbol, StringComparison.OrdinalIgnoreCase))
                continue;
            var step = Dec(i, "qty_tick_size");
            var tick = Dec(i, "price_tick_size");
            if (step <= 0 || tick <= 0)
                return null;
            return new RouteInstrument(
                Str(i, "symbol"), step, step, tick, 1, long.MaxValue / 2,
                RouteOrderTypes.Market | RouteOrderTypes.Limit,
                RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
                SupportsReplace: false, Str(i, "quote_ccy"))
            {
                BaseAsset = Str(i, "base_ccy"),
            };
        }
        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["instrument_name"] = request.Symbol,
            ["side"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["type"] = request.Type == RouteOrderType.Market ? "MARKET" : "LIMIT",
            ["quantity"] = Num(request.Quantity),
            ["client_oid"] = Token(request.ClientOrderId, id => id.Length <= 36 ? id : Hex(id, 32)),
        };
        if (request.Type != RouteOrderType.Market)
        {
            parameters["price"] = Num(request.LimitPrice!.Value);
            parameters["time_in_force"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => "IMMEDIATE_OR_CANCEL",
                RouteTimeInForce.FillOrKill => "FILL_OR_KILL",
                _ => "GOOD_TILL_CANCEL",
            };
        }
        var result = await PrivateAsync(environment, "private/create-order", parameters, ct).ConfigureAwait(false);
        var orderId = Str(result, "order_id");
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("Crypto.com acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await PrivateAsync(environment, "private/cancel-order",
            new JsonObject { ["order_id"] = order.OrderId, ["instrument_name"] = order.Symbol }, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var result = await PrivateAsync(environment, "private/get-open-orders", new JsonObject { ["instrument_name"] = symbol }, ct).ConfigureAwait(false);
        return result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array ? [.. data.EnumerateArray().Select(ReadOrder)] : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var result = await PrivateAsync(environment, "private/get-order-detail", new JsonObject { ["order_id"] = orderId }, ct).ConfigureAwait(false);
        return Str(result, "order_id").Length > 0 ? ReadOrder(result) : null;
    }

    /// <summary><c>{"order_id","client_oid","instrument_name","side","order_type","limit_price","quantity",
    /// "status","cumulative_quantity","avg_price","cumulative_fee","fee_instrument_name","update_time","reason"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var filled = Dec(o, "cumulative_quantity");
        return new RouteOrder(
            Str(o, "order_id"),
            EngineId(Str(o, "client_oid")),
            Str(o, "instrument_name"),
            Str(o, "side") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "order_type") == "MARKET" ? RouteOrderType.Market : RouteOrderType.Limit,
            Str(o, "time_in_force") switch
            {
                "IMMEDIATE_OR_CANCEL" => RouteTimeInForce.ImmediateOrCancel,
                "FILL_OR_KILL" => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.GoodTillCancelled,
            },
            Dec(o, "quantity"),
            Positive(o, "limit_price"),
            null,
            Str(o, "status") switch
            {
                "NEW" or "PENDING" => RouteOrderStatus.PendingNew,
                "ACTIVE" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "FILLED" => RouteOrderStatus.Filled,
                "CANCELED" => RouteOrderStatus.Cancelled,
                "REJECTED" => RouteOrderStatus.Rejected,
                "EXPIRED" => RouteOrderStatus.Expired,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            Positive(o, "avg_price"),
            Math.Abs(Dec(o, "cumulative_fee")),
            Str(o, "fee_instrument_name"),
            Str(o, "reason") is { Length: > 0 } reason && reason != "0" ? reason : null,
            Utc((long)Dec(o, "update_time")));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var balance = await PrivateAsync(environment, "private/user-balance", [], ct).ConfigureAwait(false);
        if (balance.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
            data[0].TryGetProperty("position_balances", out var positions) && positions.ValueKind == JsonValueKind.Array)
        {
            foreach (var position in positions.EnumerateArray())
                if (string.Equals(Str(position, "instrument_name"), rules.BaseAsset, StringComparison.OrdinalIgnoreCase))
                    return new RoutePosition(symbol, Dec(position, "quantity"));
        }
        return new RoutePosition(symbol, 0m);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/public/get-tickers?instrument_name={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status == 200 && root.TryGetProperty("result", out var result) && result.TryGetProperty("data", out var data) &&
               data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && Positive(data[0], "a") is { } price
            ? new RoutePrice(price, Utc((long)Dec(data[0], "t")))
            : null;
    }
}
