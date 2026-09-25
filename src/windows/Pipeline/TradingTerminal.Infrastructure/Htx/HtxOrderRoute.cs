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

namespace TradingTerminal.Infrastructure.Htx;

/// <summary>
/// HTX (Huobi) spot orders. Signature version 2: the authentication parameters travel in the query string,
/// signed with HMAC-SHA256 over method, host, path and the sorted query; a POST body is not signed. Every
/// order names the spot account id, which is read at connect. Refusals are <c>status: error</c> with
/// <c>err-code</c> and <c>err-msg</c>.
///
/// <para><b>Limit orders only</b> — a market buy is sized in the quote currency — with immediate-or-cancel
/// and fill-or-kill as order types. Buy fees are taken in the base currency, sell fees in the quote. HTX has
/// no test environment. Written 2026-09-25 from HTX's spot reference; not yet run against a real account.</para>
/// </summary>
internal sealed class HtxOrderRoute : OrderRouteBase
{
    private const string HostName = "api.huobi.pro";
    private string? _spotAccount;

    public HtxOrderRoute(IBrokerCredentialSource credentials, ILogger<HtxOrderRoute> logger)
        : base(credentials, logger) { }

    internal HtxOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Htx;
    public override string DisplayName => "HTX";
    public override string RouteId => "htx";
    public override string? PaperEnvironmentName => null;

    private static string Symbol(string symbol) => symbol.Trim().ToLowerInvariant();

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>> parameters, JsonObject? body, CancellationToken ct)
    {
        var (status, root, text) = await SendAsync(() =>
        {
            var query = CryptoAuth.HtxQuery(parameters.Concat(
            [
                new("AccessKeyId", Credential.Key.Trim()),
                new("SignatureMethod", "HmacSHA256"),
                new("SignatureVersion", "2"),
                new("Timestamp", CryptoAuth.HtxTimestamp(Now)),
            ]));
            var signature = CryptoAuth.HtxSignature(method.Method, HostName, path, query, Credential.Secret);
            var request = new HttpRequestMessage(method, $"https://{HostName}{path}?{query}&Signature={Uri.EscapeDataString(signature)}");
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            return request;
        }, ct).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw Refused(status, Str(root, "err-msg"), text);
        if (Str(root, "status") == "error")
            throw RefusedInBody($"{Str(root, "err-msg")} ({Str(root, "err-code")})", text);
        return root.TryGetProperty("data", out var data) ? data : root;
    }

    private async Task<string> SpotAccountAsync(CancellationToken ct)
    {
        if (_spotAccount is { } known)
            return known;
        var accounts = await CallAsync(HttpMethod.Get, "/v1/account/accounts", [], null, ct).ConfigureAwait(false);
        if (accounts.ValueKind == JsonValueKind.Array)
            foreach (var account in accounts.EnumerateArray())
                if (Str(account, "type") == "spot")
                    return _spotAccount = Str(account, "id");
        throw new BrokerOrderRouteException("HTX: this key has no spot account.", isRejection: true);
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        _spotAccount = null;
        var account = await SpotAccountAsync(ct).ConfigureAwait(false);
        var (total, available) = await BalanceAsync("usdt", ct).ConfigureAwait(false);
        return new RouteAccount(account, "usdt", total, available);
    }

    /// <summary>(trade + frozen, trade) of one currency: <c>{"list":[{"currency","type":"trade|frozen","balance"}]}</c>.</summary>
    private async Task<(decimal Total, decimal Available)> BalanceAsync(string currency, CancellationToken ct)
    {
        var account = await SpotAccountAsync(ct).ConfigureAwait(false);
        var data = await CallAsync(HttpMethod.Get, $"/v1/account/accounts/{account}/balance", [], null, ct).ConfigureAwait(false);
        decimal trade = 0, frozen = 0;
        if (data.TryGetProperty("list", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
                if (string.Equals(Str(entry, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                {
                    if (Str(entry, "type") == "trade")
                        trade += Dec(entry, "balance");
                    else if (Str(entry, "type") == "frozen")
                        frozen += Dec(entry, "balance");
                }
        return (trade + frozen, trade);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"https://{HostName}/v1/common/symbols"), ct).ConfigureAwait(false);
        EnsureSuccess(status, body, Str(root, "err-msg"));
        return ReadInstrument(root, symbol) ?? throw new InvalidDataException($"HTX returned no rules for {symbol}.");
    }

    /// <summary><c>{"data":[{"symbol","base-currency","quote-currency","price-precision","amount-precision",
    /// "min-order-amt","max-order-amt"}]}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var s in data.EnumerateArray())
        {
            if (Str(s, "symbol") != Symbol(symbol))
                continue;
            var step = Step((int)Dec(s, "amount-precision"));
            var tick = Step((int)Dec(s, "price-precision"));
            return new RouteInstrument(
                Symbol(symbol), step, step, tick,
                Units(Dec(s, "min-order-amt"), step, 1), Units(Dec(s, "max-order-amt"), step, long.MaxValue / 2),
                RouteOrderTypes.Limit,
                RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
                SupportsReplace: false, Str(s, "quote-currency"))
            {
                BaseAsset = Str(s, "base-currency"),
            };
        }
        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var side = request.Side == OrderSide.Buy ? "buy" : "sell";
        var body = new JsonObject
        {
            ["account-id"] = await SpotAccountAsync(ct).ConfigureAwait(false),
            ["symbol"] = Symbol(request.Symbol),
            ["type"] = request.TimeInForce switch
            {
                RouteTimeInForce.ImmediateOrCancel => $"{side}-ioc",
                RouteTimeInForce.FillOrKill => $"{side}-limit-fok",
                _ => $"{side}-limit",
            },
            ["amount"] = Num(request.Quantity),
            ["price"] = Num(request.LimitPrice ?? throw new BrokerOrderRouteException("HTX orders need a limit price.", isRejection: true)),
            ["client-order-id"] = Token(request.ClientOrderId, id => id.Length <= 64 ? id : Hex(id, 32)),
            ["source"] = "spot-api",
        };
        var data = await CallAsync(HttpMethod.Post, "/v1/order/orders/place", [], body, ct).ConfigureAwait(false);
        var orderId = data.ValueKind == JsonValueKind.String ? data.GetString() ?? string.Empty : data.GetRawText();
        if (orderId.Length == 0)
            throw new BrokerOrderRouteException("HTX acknowledged the order without an order id.", isRejection: false);
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(HttpMethod.Post, $"/v1/order/orders/{Uri.EscapeDataString(order.OrderId)}/submitcancel", [], [], ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await SpotAccountAsync(ct).ConfigureAwait(false);
        var data = await CallAsync(HttpMethod.Get, "/v1/order/openOrders",
            [new("account-id", account), new("symbol", Symbol(symbol))], null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Array ? [.. data.EnumerateArray().Select(ReadOrder)] : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var data = await CallAsync(HttpMethod.Get, $"/v1/order/orders/{Uri.EscapeDataString(orderId)}", [], null, ct).ConfigureAwait(false);
        return data.ValueKind == JsonValueKind.Object ? ReadOrder(data) : null;
    }

    /// <summary><c>{"id","client-order-id","symbol","type":"buy-limit","amount","price","state",
    /// "field-amount"|"filled-amount","field-cash-amount"|"filled-cash-amount","field-fees"|"filled-fees","created-at"}</c>
    /// — the single-order answer spells "filled" as "field".</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var type = Str(o, "type");
        var filled = Dec(o, "field-amount") is > 0 and var f ? f : Dec(o, "filled-amount");
        var cash = Dec(o, "field-cash-amount") is > 0 and var c ? c : Dec(o, "filled-cash-amount");
        var fee = Dec(o, "field-fees") is > 0 and var fe ? fe : Dec(o, "filled-fees");
        var sell = type.StartsWith("sell", StringComparison.Ordinal);
        var symbol = Str(o, "symbol");
        return new RouteOrder(
            Str(o, "id"),
            EngineId(Str(o, "client-order-id")),
            symbol,
            sell ? OrderSide.Sell : OrderSide.Buy,
            type.Contains("market", StringComparison.Ordinal) ? RouteOrderType.Market : RouteOrderType.Limit,
            type.EndsWith("fok", StringComparison.Ordinal) ? RouteTimeInForce.FillOrKill
                : type.EndsWith("ioc", StringComparison.Ordinal) ? RouteTimeInForce.ImmediateOrCancel
                : RouteTimeInForce.GoodTillCancelled,
            Dec(o, "amount"),
            Positive(o, "price"),
            null,
            Str(o, "state") switch
            {
                "created" or "submitted" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "partial-filled" => RouteOrderStatus.PartiallyFilled,
                "filled" => RouteOrderStatus.Filled,
                "partial-canceled" or "canceled" => RouteOrderStatus.Cancelled,
                "canceling" => RouteOrderStatus.PendingCancel,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            filled > 0 && cash > 0 ? cash / filled : null,
            fee,
            // HTX takes the buyer's fee in the coin bought and the seller's in the coin received.
            fee == 0 ? string.Empty : sell ? QuoteOf(symbol) : BaseOf(symbol),
            null,
            Utc((long)Dec(o, "created-at")));
    }

    private static readonly string[] Quotes = ["usdt", "usdc", "btc", "eth", "ht", "husd", "trx"];

    private static string QuoteOf(string symbol) => Quotes.FirstOrDefault(q => symbol.EndsWith(q, StringComparison.Ordinal)) ?? string.Empty;

    private static string BaseOf(string symbol) => QuoteOf(symbol) is { Length: > 0 } quote ? symbol[..^quote.Length] : string.Empty;

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await BalanceAsync(rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"https://{HostName}/market/detail/merged?symbol={Uri.EscapeDataString(Symbol(symbol))}"), ct).ConfigureAwait(false);
        return status == 200 && root.TryGetProperty("tick", out var tick) && Positive(tick, "close") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}
