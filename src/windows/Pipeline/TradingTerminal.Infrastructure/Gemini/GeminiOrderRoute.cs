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

namespace TradingTerminal.Infrastructure.Gemini;

/// <summary>
/// Gemini orders. Every private call is a POST whose whole request — path, nonce, parameters — rides base64
/// in the <c>X-GEMINI-PAYLOAD</c> header, signed with HMAC-SHA384.
///
/// <para>Gemini's API has no market order; its own advice is a limit with <c>immediate-or-cancel</c>, which
/// is what this route offers. An order is live, cancelled, or neither (done). The nonce must rise on every
/// call on a key, so it comes from a counter. Gemini's <c>tick_size</c> is the <i>quantity</i> step; the
/// price step is <c>quote_increment</c>. Paper is Gemini's sandbox (api.sandbox.gemini.com, its own account
/// and keys). Written 2026-09-25 from Gemini's REST reference; not yet run against a real account.</para>
/// </summary>
internal sealed class GeminiOrderRoute : OrderRouteBase
{
    private long _lastNonce;

    public GeminiOrderRoute(IBrokerCredentialSource credentials, ILogger<GeminiOrderRoute> logger)
        : base(credentials, logger) { }

    internal GeminiOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Gemini;
    public override string DisplayName => "Gemini";
    public override string RouteId => "gemini";
    public override string? PaperEnvironmentName => "SANDBOX";

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api.gemini.com" : "https://api.sandbox.gemini.com";

    private static string Api(string symbol) => symbol.Trim().ToLowerInvariant();

    private long NextNonce()
    {
        var candidate = Now.ToUnixTimeMilliseconds();
        while (true)
        {
            var last = Interlocked.Read(ref _lastNonce);
            var next = Math.Max(candidate, last + 1);
            if (Interlocked.CompareExchange(ref _lastNonce, next, last) == last)
                return next;
        }
    }

    private async Task<JsonElement> PrivateAsync(RouteEnvironment environment, string path, JsonObject? parameters, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() =>
        {
            var payload = new JsonObject { ["request"] = path, ["nonce"] = NextNonce() };
            if (parameters is not null)
                foreach (var (name, value) in parameters)
                    payload[name] = value?.DeepClone();
            var (encoded, signature) = CryptoAuth.GeminiSignature(payload.ToJsonString(), Credential.Secret);
            var request = new HttpRequestMessage(HttpMethod.Post, Host(environment) + path)
            {
                Content = new StringContent(string.Empty, Encoding.UTF8, "text/plain"),
            };
            request.Headers.TryAddWithoutValidation("X-GEMINI-APIKEY", Credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("X-GEMINI-PAYLOAD", encoded);
            request.Headers.TryAddWithoutValidation("X-GEMINI-SIGNATURE", signature);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300 || Str(root, "result") == "error")
            throw Refused(status, $"{Str(root, "message")} ({Str(root, "reason")})", body, status is >= 500 or 408 ? false : null);
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var roles = await PrivateAsync(environment, "/v1/roles", null, ct).ConfigureAwait(false);
        var (total, available) = await CurrencyAsync(environment, "USD", ct).ConfigureAwait(false);
        var counterparty = Str(roles, "counterparty_id");
        return new RouteAccount(counterparty.Length > 0 ? counterparty : KeyAccount(), "USD", total, available);
    }

    private async Task<(decimal Total, decimal Available)> CurrencyAsync(RouteEnvironment environment, string currency, CancellationToken ct)
    {
        var balances = await PrivateAsync(environment, "/v1/balances", null, ct).ConfigureAwait(false);
        if (balances.ValueKind == JsonValueKind.Array)
            foreach (var balance in balances.EnumerateArray())
                if (string.Equals(Str(balance, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                    return (Dec(balance, "amount"), Dec(balance, "available"));
        return (0m, 0m);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/v1/symbols/details/{Uri.EscapeDataString(Api(symbol))}"), ct).ConfigureAwait(false);
        EnsureSuccess(status, body, Str(root, "message"));
        return ReadInstrument(root, symbol) ?? throw new InvalidDataException($"Gemini returned no rules for {symbol}.");
    }

    /// <summary><c>{"symbol","base_currency","quote_currency","tick_size" (quantity),"quote_increment" (price),"min_order_size"}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement d, string symbol)
    {
        var step = Dec(d, "tick_size");
        var tick = Dec(d, "quote_increment");
        if (step <= 0 || tick <= 0)
            return null;
        return new RouteInstrument(
            symbol, step, step, tick, Units(Dec(d, "min_order_size"), step, 1), long.MaxValue / 2,
            RouteOrderTypes.Limit | RouteOrderTypes.StopLimit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(d, "quote_currency"))
        {
            BaseAsset = Str(d, "base_currency"),
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var parameters = new JsonObject
        {
            ["client_order_id"] = Token(request.ClientOrderId, id => id.Length <= 100 ? id : Hex(id, 32)),
            ["symbol"] = Api(request.Symbol),
            ["amount"] = Num(request.Quantity),
            ["price"] = Num(request.LimitPrice ?? throw new BrokerOrderRouteException("Gemini orders need a limit price.", isRejection: true)),
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["type"] = request.Type == RouteOrderType.StopLimit ? "exchange stop limit" : "exchange limit",
        };
        if (request.Type == RouteOrderType.StopLimit)
            parameters["stop_price"] = Num(request.StopPrice!.Value);
        if (request.TimeInForce is RouteTimeInForce.ImmediateOrCancel or RouteTimeInForce.FillOrKill)
            parameters["options"] = new JsonArray(request.TimeInForce == RouteTimeInForce.FillOrKill ? "fill-or-kill" : "immediate-or-cancel");
        var order = await PrivateAsync(environment, "/v1/order/new", parameters, ct).ConfigureAwait(false);
        return ReadOrder(order, request.Symbol);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await PrivateAsync(environment, "/v1/order/cancel",
            new JsonObject { ["order_id"] = long.Parse(order.OrderId, System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var orders = await PrivateAsync(environment, "/v1/orders", null, ct).ConfigureAwait(false);
        return orders.ValueKind == JsonValueKind.Array
            ? [.. orders.EnumerateArray().Where(o => Str(o, "symbol") == Api(symbol)).Select(o => ReadOrder(o, symbol))]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        try
        {
            var order = await PrivateAsync(environment, "/v1/order/status",
                new JsonObject { ["order_id"] = long.Parse(orderId, System.Globalization.CultureInfo.InvariantCulture) }, ct).ConfigureAwait(false);
            return ReadOrder(order, symbol);
        }
        catch (BrokerOrderRouteException exception) when (exception.Message.Contains("OrderNotFound", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary><c>{"order_id","client_order_id","symbol","side","type","price","avg_execution_price","is_live",
    /// "is_cancelled","executed_amount","original_amount","options":[…],"timestampms","reason"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var executed = Dec(o, "executed_amount");
        var live = o.TryGetProperty("is_live", out var l) && l.ValueKind == JsonValueKind.True;
        var cancelled = o.TryGetProperty("is_cancelled", out var c) && c.ValueKind == JsonValueKind.True;
        var options = o.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
            ? opts.EnumerateArray().Select(x => x.GetString()).ToArray()
            : [];
        return new RouteOrder(
            Str(o, "order_id"),
            EngineId(Str(o, "client_order_id")),
            symbol,
            Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "type").Contains("stop", StringComparison.Ordinal) ? RouteOrderType.StopLimit : RouteOrderType.Limit,
            options.Contains("fill-or-kill") ? RouteTimeInForce.FillOrKill
                : options.Contains("immediate-or-cancel") ? RouteTimeInForce.ImmediateOrCancel
                : RouteTimeInForce.GoodTillCancelled,
            Dec(o, "original_amount"),
            Positive(o, "price"),
            Positive(o, "stop_price"),
            live ? executed > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working
                : cancelled ? RouteOrderStatus.Cancelled
                : RouteOrderStatus.Filled,
            executed,
            Positive(o, "avg_execution_price"),
            0m,
            string.Empty,
            Str(o, "reason") is { Length: > 0 } reason ? reason : null,
            Utc((long)Dec(o, "timestampms")));
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
            $"{Host(environment)}/v1/pubticker/{Uri.EscapeDataString(Api(symbol))}"), ct).ConfigureAwait(false);
        return status == 200 && Positive(root, "last") is { } price ? new RoutePrice(price, Now.UtcDateTime) : null;
    }
}
