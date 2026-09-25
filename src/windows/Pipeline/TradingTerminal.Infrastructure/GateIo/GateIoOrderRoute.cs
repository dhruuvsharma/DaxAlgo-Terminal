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

namespace TradingTerminal.Infrastructure.GateIo;

/// <summary>
/// Gate.io v4 spot orders. Signed with HMAC-SHA512 over method, path, query, SHA-512 of the body, and the
/// timestamp in seconds. Refusals come as 4xx with <c>label</c> and <c>message</c>.
///
/// <para><b>Limit orders only</b>, for the reason Bitget has them only: a market buy is sized in the quote
/// currency. A client id must start with <c>t-</c> and fit 28 characters. Gate's test network covers
/// futures, not spot, so there is no paper environment here. Written 2026-09-25 from Gate's v4 reference;
/// not yet run against a real account.</para>
/// </summary>
internal sealed class GateIoOrderRoute : OrderRouteBase
{
    private const string Host = "https://api.gateio.ws";

    public GateIoOrderRoute(IBrokerCredentialSource credentials, ILogger<GateIoOrderRoute> logger)
        : base(credentials, logger) { }

    internal GateIoOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.GateIo;
    public override string DisplayName => "Gate.io";
    public override string RouteId => "gate-io";
    public override string? PaperEnvironmentName => null;

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct, bool allowNotFound = false)
    {
        var json = body?.ToJsonString() ?? string.Empty;
        var (status, root, text) = await SendAsync(() =>
        {
            var seconds = Now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var request = new HttpRequestMessage(method, $"{Host}{path}{(query.Length > 0 ? "?" + query : string.Empty)}");
            if (body is not null)
                request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("KEY", Credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("Timestamp", seconds);
            request.Headers.TryAddWithoutValidation("SIGN", CryptoAuth.GateIoSignature(method.Method, path, query, json, seconds, Credential.Secret));
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }, ct).ConfigureAwait(false);
        if (allowNotFound && status == 404)
            return default;
        if (status is < 200 or >= 300)
            throw Refused(status, $"{Str(root, "message")} {Str(root, "label")}".Trim(), text);
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var detail = await CallAsync(HttpMethod.Get, "/api/v4/account/detail", string.Empty, null, ct).ConfigureAwait(false);
        var (total, available) = await CurrencyAsync("USDT", ct).ConfigureAwait(false);
        var user = Str(detail, "user_id");
        return new RouteAccount(user.Length > 0 ? user : KeyAccount(), "USDT", total, available);
    }

    private async Task<(decimal Total, decimal Available)> CurrencyAsync(string currency, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, "/api/v4/spot/accounts", $"currency={Uri.EscapeDataString(currency)}", null, ct).ConfigureAwait(false);
        if (data.ValueKind == JsonValueKind.Array)
            foreach (var account in data.EnumerateArray())
                if (string.Equals(Str(account, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                    return (Dec(account, "available") + Dec(account, "locked"), Dec(account, "available"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var pair = await CallAsync(HttpMethod.Get, $"/api/v4/spot/currency_pairs/{Uri.EscapeDataString(symbol)}", string.Empty, null, ct).ConfigureAwait(false);
        return ReadInstrument(pair) ?? throw new InvalidDataException($"Gate.io returned no rules for {symbol}.");
    }

    /// <summary><c>{"id","base","quote","min_base_amount","amount_precision","precision","trade_status"}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement p)
    {
        if (Str(p, "id").Length == 0)
            return null;
        var step = Step((int)Dec(p, "amount_precision"));
        var tick = Step((int)Dec(p, "precision"));
        return new RouteInstrument(
            Str(p, "id"), step, step, tick, Units(Dec(p, "min_base_amount"), step, 1), long.MaxValue / 2,
            RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(p, "quote"))
        {
            BaseAsset = Str(p, "base"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["text"] = Token(request.ClientOrderId, id => "t-" + Alphanumeric(id, 26)),
            ["currency_pair"] = request.Symbol,
            ["type"] = "limit",
            ["account"] = "spot",
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["amount"] = Num(request.Quantity),
            ["price"] = Num(request.LimitPrice ?? throw new BrokerOrderRouteException("Gate.io orders need a limit price.", isRejection: true)),
            ["time_in_force"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => "ioc",
                RouteTimeInForce.FillOrKill => "fok",
                _ => "gtc",
            },
        };
        return ReadOrder(await CallAsync(HttpMethod.Post, "/api/v4/spot/orders", string.Empty, body, ct).ConfigureAwait(false));
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(HttpMethod.Delete, $"/api/v4/spot/orders/{Uri.EscapeDataString(order.OrderId)}",
            $"currency_pair={Uri.EscapeDataString(order.Symbol)}", null, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, "/api/v4/spot/orders", $"currency_pair={Uri.EscapeDataString(symbol)}&status=open", null, ct)
            .ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Array ? [.. data.EnumerateArray().Select(ReadOrder)] : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var order = await CallAsync(HttpMethod.Get, $"/api/v4/spot/orders/{Uri.EscapeDataString(orderId)}",
            $"currency_pair={Uri.EscapeDataString(symbol)}", null, ct, allowNotFound: true).ConfigureAwait(false);
        return order.ValueKind == JsonValueKind.Object ? ReadOrder(order) : null;
    }

    /// <summary><c>{"id","text","currency_pair","side","type","amount","price","left","filled_total",
    /// "avg_deal_price","fee","fee_currency","status":"open|closed|cancelled","finish_as","update_time_ms"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var amount = Dec(o, "amount");
        var filled = Dec(o, "filled_amount") is > 0 and var f ? f : amount - Dec(o, "left");
        var total = Dec(o, "filled_total");
        return new RouteOrder(
            Str(o, "id"),
            EngineId(Str(o, "text")),
            Str(o, "currency_pair"),
            Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "type") == "market" ? RouteOrderType.Market : RouteOrderType.Limit,
            Str(o, "time_in_force") switch { "ioc" => RouteTimeInForce.ImmediateOrCancel, "fok" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
            amount,
            Positive(o, "price"),
            null,
            Str(o, "status") switch
            {
                "open" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "closed" => RouteOrderStatus.Filled,
                "cancelled" => RouteOrderStatus.Cancelled,
                _ => RouteOrderStatus.Unknown,
            },
            Math.Max(0m, filled),
            Positive(o, "avg_deal_price") ?? (filled > 0 && total > 0 ? total / filled : null),
            Dec(o, "fee"),
            Str(o, "fee_currency"),
            Str(o, "finish_as") is "filled" or "open" or "" ? null : Str(o, "finish_as"),
            Utc((long)Dec(o, "update_time_ms")));
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
            $"{Host}/api/v4/spot/tickers?currency_pair={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status == 200 && root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && Positive(root[0], "last") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
