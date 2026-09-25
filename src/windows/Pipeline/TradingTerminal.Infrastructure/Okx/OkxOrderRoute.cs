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

namespace TradingTerminal.Infrastructure.Okx;

/// <summary>
/// OKX v5 spot orders in cash mode. Signed with base64 HMAC-SHA256 over ISO timestamp, method, path with
/// query, and body; the passphrase travels in its own header. Demo trading is the same host with the header
/// <c>x-simulated-trading: 1</c> and demo keys.
///
/// <para>OKX reports a refusal twice over: HTTP 200 with <c>code</c> non-zero, and per order an
/// <c>sCode</c> with <c>sMsg</c> — the second carries the reason. Immediate-or-cancel and fill-or-kill are
/// order types (<c>ioc</c>, <c>fok</c>). A market buy is sized in the base currency only when it says so
/// (<c>tgtCcy: base_ccy</c>). Client ids are 32 letters and digits. Limit orders are amended in place.</para>
///
/// <para>Written 2026-09-25 from OKX's v5 reference. Not yet run against a real account.</para>
/// </summary>
internal sealed class OkxOrderRoute : OrderRouteBase
{
    private const string Host = "https://www.okx.com";

    public OkxOrderRoute(IBrokerCredentialSource credentials, ILogger<OkxOrderRoute> logger)
        : base(credentials, logger) { }

    internal OkxOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Okx;
    public override string DisplayName => "OKX";
    public override string RouteId => "okx";
    public override string? PaperEnvironmentName => "DEMO";

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string pathAndQuery, JsonObject? body, CancellationToken ct)
    {
        var json = body?.ToJsonString() ?? string.Empty;
        var (status, root, text) = await SendAsync(() =>
        {
            var stamp = CryptoAuth.OkxTimestamp(Now);
            var request = new HttpRequestMessage(method, Host + pathAndQuery);
            if (body is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            var credential = Credential;
            request.Headers.TryAddWithoutValidation("OK-ACCESS-KEY", credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("OK-ACCESS-TIMESTAMP", stamp);
            request.Headers.TryAddWithoutValidation("OK-ACCESS-PASSPHRASE", credential.Passphrase);
            request.Headers.TryAddWithoutValidation("OK-ACCESS-SIGN", CryptoAuth.OkxSignature(stamp, method.Method, pathAndQuery, json, credential.Secret));
            if (environment == RouteEnvironment.Paper)
                request.Headers.TryAddWithoutValidation("x-simulated-trading", "1");
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "msg"), text);
        if (Str(root, "code") is { Length: > 0 } code && code != "0")
            throw RefusedInBody(FirstOrderMessage(root) ?? $"{Str(root, "msg")} (code {code})", text);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    /// <summary>The per-order <c>sMsg</c>, which says why when the envelope only says "failed".</summary>
    private static string? FirstOrderMessage(JsonElement root) =>
        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 &&
        Str(data[0], "sMsg") is { Length: > 0 } message
            ? $"{message} (sCode {Str(data[0], "sCode")})"
            : null;

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var config = await CallAsync(environment, HttpMethod.Get, "/api/v5/account/config", null, ct).ConfigureAwait(false);
        var (total, available) = await CurrencyAsync(environment, "USDT", ct).ConfigureAwait(false);
        var uid = config.ValueKind == JsonValueKind.Array && config.GetArrayLength() > 0 ? Str(config[0], "uid") : string.Empty;
        return new RouteAccount(uid.Length > 0 ? uid : KeyAccount(), "USDT", total, available);
    }

    private async Task<(decimal Total, decimal Available)> CurrencyAsync(RouteEnvironment environment, string currency, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Get, $"/api/v5/account/balance?ccy={Uri.EscapeDataString(currency)}", null, ct).ConfigureAwait(false);
        return ReadBalance(data, currency);
    }

    /// <summary><c>[{"details":[{"ccy","cashBal","availBal"}]}]</c>.</summary>
    internal static (decimal Total, decimal Available) ReadBalance(JsonElement data, string currency)
    {
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var account in data.EnumerateArray())
                if (account.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                    foreach (var d in details.EnumerateArray())
                        if (string.Equals(Str(d, "ccy"), currency, StringComparison.OrdinalIgnoreCase))
                            return (Dec(d, "cashBal"), Dec(d, "availBal"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Get, $"/api/v5/public/instruments?instType=SPOT&instId={Uri.EscapeDataString(symbol)}", null, ct)
            .ConfigureAwait(false);
        return ReadInstrument(data) ?? throw new InvalidDataException($"OKX returned no rules for {symbol}.");
    }

    /// <summary><c>[{"instId","baseCcy","quoteCcy","tickSz","lotSz","minSz","maxLmtSz"}]</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
            return null;
        var s = data[0];
        var step = Dec(s, "lotSz");
        var tick = Dec(s, "tickSz");
        if (step <= 0 || tick <= 0)
            return null;
        return new RouteInstrument(
            Str(s, "instId"), step, step, tick,
            Units(Dec(s, "minSz"), step, 1), Units(Dec(s, "maxLmtSz"), step, long.MaxValue / 2),
            RouteOrderTypes.Market | RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: true, Str(s, "quoteCcy"))
        {
            BaseAsset = Str(s, "baseCcy"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Post, "/api/v5/trade/order", OrderBody(request), ct).ConfigureAwait(false);
        var ack = data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : default;
        if (Str(ack, "sCode") is { Length: > 0 } sCode && sCode != "0")
            throw RefusedInBody($"{Str(ack, "sMsg")} (sCode {sCode})", ack.GetRawText());
        var orderId = Str(ack, "ordId");
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("OKX acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    internal JsonObject OrderBody(RouteOrderRequest request)
    {
        var body = new JsonObject
        {
            ["instId"] = request.Symbol,
            ["tdMode"] = "cash",
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["sz"] = Num(request.Quantity),
            ["clOrdId"] = Token(request.ClientOrderId, id => Alphanumeric(id, 32)),
        };
        if (request.Type == RouteOrderType.Market)
        {
            body["ordType"] = "market";
            body["tgtCcy"] = "base_ccy";
        }
        else
        {
            body["ordType"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => "ioc",
                RouteTimeInForce.FillOrKill => "fok",
                _ => "limit",
            };
            body["px"] = Num(request.LimitPrice!.Value);
        }
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var data = await CallAsync(environment, HttpMethod.Post, "/api/v5/trade/cancel-order",
            new JsonObject { ["instId"] = order.Symbol, ["ordId"] = order.OrderId }, ct).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && Str(data[0], "sCode") is { Length: > 0 } code && code != "0")
            throw RefusedInBody($"{Str(data[0], "sMsg")} (sCode {code})", data.GetRawText());
    }

    public override async Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct)
    {
        var body = new JsonObject { ["instId"] = order.Symbol, ["ordId"] = order.OrderId, ["newSz"] = Num(quantity) };
        if (limitPrice is { } price)
            body["newPx"] = Num(price);
        var data = await CallAsync(environment, HttpMethod.Post, "/api/v5/trade/amend-order", body, ct).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 && Str(data[0], "sCode") is { Length: > 0 } code && code != "0")
            throw RefusedInBody($"{Str(data[0], "sMsg")} (sCode {code})", data.GetRawText());
        return order with { Quantity = quantity, LimitPrice = limitPrice ?? order.LimitPrice, UpdatedAtUtc = Now.UtcDateTime };
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        ReadOrders(await CallAsync(environment, HttpMethod.Get, $"/api/v5/trade/orders-pending?instType=SPOT&instId={Uri.EscapeDataString(symbol)}", null, ct)
            .ConfigureAwait(false));

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var orders = ReadOrders(await CallAsync(environment, HttpMethod.Get,
            $"/api/v5/trade/order?instId={Uri.EscapeDataString(symbol)}&ordId={Uri.EscapeDataString(orderId)}", null, ct).ConfigureAwait(false));
        return orders.Count > 0 ? orders[0] : null;
    }

    /// <summary><c>[{"ordId","clOrdId","instId","side","ordType","px","sz","state","accFillSz","avgPx","fee",
    /// "feeCcy","uTime"}]</c>. OKX writes a fee charged as a negative number.</summary>
    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement data)
    {
        var orders = new List<RouteOrder>();
        if (data.ValueKind != JsonValueKind.Array)
            return orders;
        foreach (var o in data.EnumerateArray())
        {
            var type = Str(o, "ordType");
            orders.Add(new RouteOrder(
                Str(o, "ordId"),
                EngineId(Str(o, "clOrdId")),
                Str(o, "instId"),
                Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
                type == "market" ? RouteOrderType.Market : RouteOrderType.Limit,
                type switch { "ioc" => RouteTimeInForce.ImmediateOrCancel, "fok" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
                Dec(o, "sz"),
                Positive(o, "px"),
                null,
                Str(o, "state") switch
                {
                    "live" => RouteOrderStatus.Working,
                    "partially_filled" => RouteOrderStatus.PartiallyFilled,
                    "filled" => RouteOrderStatus.Filled,
                    "canceled" or "mmp_canceled" => RouteOrderStatus.Cancelled,
                    _ => RouteOrderStatus.Unknown,
                },
                Dec(o, "accFillSz"),
                Positive(o, "avgPx"),
                Math.Abs(Dec(o, "fee")),
                Str(o, "feeCcy"),
                null,
                Utc((long)Dec(o, "uTime"))));
        }
        return orders;
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await CurrencyAsync(environment, rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/api/v5/market/ticker?instId={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status == 200 && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array &&
               data.GetArrayLength() > 0 && Positive(data[0], "last") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
