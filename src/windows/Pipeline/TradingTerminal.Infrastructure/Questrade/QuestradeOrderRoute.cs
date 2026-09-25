using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Questrade;

/// <summary>
/// Questrade orders — Canadian and US stocks — over the session kept from the refresh token the login window
/// stores.
///
/// <para><b>Read this first.</b> Questrade documents its order endpoints as open to <i>partner</i> apps only; a
/// personal API token reads accounts and quotes but is expected to be refused when it places an order. The
/// route is written so that a partner app works and a personal one is refused in Questrade's own words, rather
/// than not offering Questrade at all.</para>
///
/// <para><b>Paper is Questrade's practice account</b>, whose token comes from practicelogin.questrade.com: the
/// session reaches practice when <c>Questrade:AuthBaseUrl</c> names the practice login, and a card for the
/// other environment is refused with that said. Requests go to the API server each token names. The route
/// trades the primary active account; symbols are looked up once for Questrade's numeric id. A sell with no
/// position is a short sale on a margin account, so no intent is sent. Questrade has no client order id, and a
/// replace issues a new order id, so replace is off.</para>
///
/// <para>Written 2026-09-25 from Questrade's API documentation; not yet run against a real account.</para>
/// </summary>
internal sealed class QuestradeOrderRoute : KeptSessionOrderRoute
{
    private readonly QuestradeOptions _options;
    private readonly ConcurrentDictionary<string, long> _ids = new(StringComparer.OrdinalIgnoreCase);
    private string _account = string.Empty;

    public QuestradeOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<QuestradeOptions> options, ILogger<QuestradeOrderRoute> logger)
        : base(BrokerKind.Questrade, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal QuestradeOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, QuestradeOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.Questrade, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.Questrade;
    public override string DisplayName => "Questrade";
    public override string RouteId => "questrade";
    public override string? PaperEnvironmentName => "PRACTICE";

    protected override RouteEnvironment? SessionEnvironment =>
        _options.AuthBaseUrl.Contains("practice", StringComparison.OrdinalIgnoreCase) ? RouteEnvironment.Paper : RouteEnvironment.Live;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        QuestradeSignIn.ExchangeAsync(Http, _options.AuthBaseUrl, session.RefreshToken, Now, ct);

    private async Task<Answer> ApiAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        await CallAsync(environment, session =>
        {
            var url = session.Server.TrimEnd('/') + path;
            return body is null ? new HttpRequestMessage(method, url) : Json(method, url, body);
        }, ct).ConfigureAwait(false);

    private async Task<JsonElement> RequestAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var answer = await ApiAsync(environment, method, path, body, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return answer.Root;
    }

    /// <summary><c>{"code","message"}</c>.</summary>
    internal static string? Words(JsonElement root) =>
        Str(root, "message") is { Length: > 0 } message ? Str(root, "code") is { Length: > 0 } code ? $"{message} ({code})" : message : null;

    private async Task<string> AccountNumberAsync(RouteEnvironment environment, CancellationToken ct)
    {
        if (_account.Length > 0) return _account;
        var root = await RequestAsync(environment, HttpMethod.Get, "/v1/accounts", null, ct).ConfigureAwait(false);
        _account = PickAccount(root) ?? throw new BrokerOrderRouteException("Questrade: the session reaches no active account.", isRejection: true);
        return _account;
    }

    /// <summary><c>{"accounts":[{"number","status","isPrimary"}]}</c> — the primary active account, else the first active.</summary>
    internal static string? PickAccount(JsonElement root)
    {
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) return null;
        var active = accounts.EnumerateArray().Where(a => Str(a, "status").Equals("Active", StringComparison.OrdinalIgnoreCase)).ToArray();
        var primary = active.FirstOrDefault(a => a.TryGetProperty("isPrimary", out var p) && p.ValueKind == JsonValueKind.True);
        var pick = primary.ValueKind == JsonValueKind.Object ? primary : active.FirstOrDefault();
        return pick.ValueKind == JsonValueKind.Object && Str(pick, "number") is { Length: > 0 } number ? number : null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Get, $"/v1/accounts/{account}/balances", null, ct).ConfigureAwait(false);
        var combined = root.TryGetProperty("combinedBalances", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0] : default;
        return new RouteAccount(account, Str(combined, "currency") is { Length: > 0 } c ? c : "CAD", Dec(combined, "cash"), Dec(combined, "buyingPower"));
    }

    private async Task<long> IdAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        if (_ids.TryGetValue(symbol.Trim(), out var id)) return id;
        var root = await RequestAsync(environment, HttpMethod.Get, $"/v1/symbols/search?prefix={Uri.EscapeDataString(symbol.Trim())}", null, ct).ConfigureAwait(false);
        if (root.TryGetProperty("symbols", out var symbols) && symbols.ValueKind == JsonValueKind.Array)
            foreach (var s in symbols.EnumerateArray())
                if (string.Equals(Str(s, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase) && (long)Dec(s, "symbolId") is > 0 and var found)
                    return _ids[symbol.Trim()] = found;
        throw new BrokerOrderRouteException($"Questrade does not know the symbol {symbol}.", isRejection: true);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var id = await IdAsync(environment, symbol, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Get, $"/v1/symbols/{id}", null, ct).ConfigureAwait(false);
        var price = await PriceAsync(environment, symbol, ct).ConfigureAwait(false);
        return ReadInstrument(root, symbol.Trim(), price?.Price) ?? throw new BrokerOrderRouteException($"Questrade: {symbol} is not tradable.", isRejection: true);
    }

    /// <summary><c>{"symbols":[{"symbol","currency","isTradable","minTicks":[{"pivot","minTick"}]}]}</c> — the tick is the one whose
    /// pivot is the highest at or below the price.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol, decimal? price)
    {
        if (!root.TryGetProperty("symbols", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() == 0) return null;
        var s = rows[0];
        if (s.TryGetProperty("isTradable", out var tradable) && tradable.ValueKind == JsonValueKind.False) return null;
        var tick = 0m;
        if (s.TryGetProperty("minTicks", out var ticks) && ticks.ValueKind == JsonValueKind.Array)
            foreach (var row in ticks.EnumerateArray().OrderBy(r => Dec(r, "pivot")))
                if (tick == 0m || price is not { } p || Dec(row, "pivot") <= p)
                    tick = Dec(row, "minTick");
        return new RouteInstrument(Str(s, "symbol") is { Length: > 0 } name ? name : symbol, 1m, 1m, tick > 0 ? tick : EquityTick(price), 1, 10_000_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(s, "currency") is { Length: > 0 } currency ? currency : "CAD");
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var id = await IdAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Post, $"/v1/accounts/{account}/orders", SubmitBody(request, account, id), ct).ConfigureAwait(false);
        var orderId = Str(root, "orderId");
        if (orderId.Length == 0)
            throw new InvalidDataException($"Questrade answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string account, long symbolId)
    {
        var body = new JsonObject
        {
            ["accountNumber"] = account,
            ["symbolId"] = symbolId,
            ["quantity"] = request.Quantity,
            ["isAllOrNone"] = false,
            ["isAnonymous"] = false,
            ["orderType"] = request.Type switch
            {
                RouteOrderType.Limit => "Limit",
                RouteOrderType.Stop => "Stop",
                RouteOrderType.StopLimit => "StopLimit",
                _ => "Market",
            },
            ["timeInForce"] = request.TimeInForce switch
            {
                RouteTimeInForce.GoodTillCancelled => "GoodTillCanceled",
                RouteTimeInForce.ImmediateOrCancel => "ImmediateOrCancel",
                RouteTimeInForce.FillOrKill => "FillOrKill",
                _ => "Day",
            },
            ["action"] = request.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["primaryRoute"] = "AUTO",
            ["secondaryRoute"] = "AUTO",
        };
        if (request.LimitPrice is { } limit) body["limitPrice"] = limit;
        if (request.StopPrice is { } stop) body["stopPrice"] = stop;
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        _ = await RequestAsync(environment, HttpMethod.Delete, $"/v1/accounts/{account}/orders/{Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Get, $"/v1/accounts/{account}/orders?stateFilter=Open", null, ct).ConfigureAwait(false);
        return ReadOrders(root, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var answer = await ApiAsync(environment, HttpMethod.Get, $"/v1/accounts/{account}/orders/{Uri.EscapeDataString(orderId)}", null, ct).ConfigureAwait(false);
        if (answer.Status == 404) return null;
        if (answer.Status is < 200 or >= 300) throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return ReadOrders(answer.Root, symbol).FirstOrDefault(o => o.OrderId == orderId);
    }

    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement root, string symbol) =>
        root.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array
            ? [.. orders.EnumerateArray()
                .Where(o => string.Equals(Str(o, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(o => ReadOrder(o, symbol))]
            : [];

    /// <summary><c>{"id","symbol","totalQuantity","filledQuantity","side","orderType","limitPrice","stopPrice","timeInForce","state",
    /// "avgExecPrice","commissionCharged","updateTime","rejectionReason"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filledQuantity");
        var fee = Dec(o, "commissionCharged");
        return new RouteOrder(
            Str(o, "id"),
            string.Empty,
            symbol,
            Str(o, "side") is "Buy" or "Cov" or "BTO" or "BTC" ? OrderSide.Buy : OrderSide.Sell,
            Str(o, "orderType") switch
            {
                "Limit" => RouteOrderType.Limit,
                "Stop" => RouteOrderType.Stop,
                "StopLimit" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "timeInForce") switch
            {
                "GoodTillCanceled" => RouteTimeInForce.GoodTillCancelled,
                "ImmediateOrCancel" => RouteTimeInForce.ImmediateOrCancel,
                "FillOrKill" => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            Dec(o, "totalQuantity"),
            Positive(o, "limitPrice"),
            Positive(o, "stopPrice"),
            Str(o, "state") switch
            {
                "Executed" => RouteOrderStatus.Filled,
                "Partial" => RouteOrderStatus.PartiallyFilled,
                "Canceled" or "PartialCanceled" or "Replaced" => RouteOrderStatus.Cancelled,
                "Expired" => RouteOrderStatus.Expired,
                "Rejected" or "Failed" => RouteOrderStatus.Rejected,
                "CancelPending" => RouteOrderStatus.PendingCancel,
                "Pending" or "PendingRiskReview" => RouteOrderStatus.PendingNew,
                "Accepted" or "Queued" or "Triggered" or "Activated" or "Stopped" or "Suspended" or "ReplacePending" or "ContingentOrder" =>
                    filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            Positive(o, "avgExecPrice"),
            fee,
            fee > 0 ? "CAD" : string.Empty,
            Str(o, "rejectionReason") is { Length: > 0 } reason ? reason : null,
            ParseTime(Str(o, "updateTime"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountNumberAsync(environment, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Get, $"/v1/accounts/{account}/positions", null, ct).ConfigureAwait(false);
        var net = root.TryGetProperty("positions", out var rows) && rows.ValueKind == JsonValueKind.Array
            ? rows.EnumerateArray().Where(p => string.Equals(Str(p, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase)).Sum(p => Dec(p, "openQuantity"))
            : 0m;
        return new RoutePosition(symbol, net);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var id = await IdAsync(environment, symbol, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Get, $"/v1/markets/quotes/{id}", null, ct).ConfigureAwait(false);
        var q = root.TryGetProperty("quotes", out var quotes) && quotes.ValueKind == JsonValueKind.Array && quotes.GetArrayLength() > 0 ? quotes[0] : default;
        var price = Positive(q, "lastTradePrice") ?? (Positive(q, "bidPrice") is { } bid && Positive(q, "askPrice") is { } ask ? (bid + ask) / 2 : null);
        return price is { } p ? new RoutePrice(p, ParseTime(Str(q, "lastTradeTime"), Now.UtcDateTime)) : null;
    }
}
