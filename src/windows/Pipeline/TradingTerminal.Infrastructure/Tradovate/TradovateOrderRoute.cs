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

namespace TradingTerminal.Infrastructure.Tradovate;

/// <summary>
/// Tradovate futures orders, over the session the login window signs in.
///
/// <para><b>Paper is Tradovate's demo</b>, a separate host and account. The login row's environment
/// (<c>demo</c>) decides which the session reaches, and a card for the other is refused with that said.</para>
///
/// <para><b>An order is three records.</b> The order says its status; its <i>version</i> says quantity, type and
/// prices; its <i>fills</i> say what traded at what price. The route lists the account's orders and the user's
/// fills once per poll — two requests however many orders are working — and reads each order's version once
/// and caches it (a version changes only on a modify, which this route does not send). Orders are sent with
/// <c>isAutomated: true</c>, which the exchanges require of an order no human clicked.</para>
///
/// <para><b>Rate limits.</b> Tradovate answers a request past its limit with a <c>p-ticket</c> and a wait; the
/// route reports that as a refusal (nothing was placed) rather than retrying, and the engine's poll tries again
/// next interval. A one-second poll while an order works is two requests a second; if Tradovate objects,
/// raise <c>Execution:Routes:PollIntervalMilliseconds</c>. Tradovate has no REST quote, so there is no
/// reference price. Written 2026-09-25 from Tradovate's REST reference; not yet run against a real account.</para>
/// </summary>
internal sealed class TradovateOrderRoute : KeptSessionOrderRoute
{
    private readonly TradovateOptions _options;
    private readonly ConcurrentDictionary<string, long> _contracts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, JsonElement> _versions = new();
    private (long Id, string Name)? _account;

    public TradovateOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<TradovateOptions> options, ILogger<TradovateOrderRoute> logger)
        : base(BrokerKind.Tradovate, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal TradovateOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, TradovateOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.Tradovate, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.Tradovate;
    public override string DisplayName => "Tradovate";
    public override string RouteId => "tradovate";
    public override string? PaperEnvironmentName => "DEMO";

    private bool IsDemo => Credential.Extra.Trim().Equals("demo", StringComparison.OrdinalIgnoreCase);

    protected override RouteEnvironment? SessionEnvironment => IsDemo ? RouteEnvironment.Paper : RouteEnvironment.Live;

    private string RestBase => IsDemo ? _options.DemoRestBaseUrl : _options.RestBaseUrl;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        TradovateSignIn.AccessTokenAsync(Http, RestBase, app, _options.AppId, Now, ct);

    private async Task<JsonElement> RequestAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var answer = await CallAsync(environment, _ => body is null
            ? new HttpRequestMessage(method, RestBase + path)
            : Json(method, RestBase + path, body), ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        if (Str(answer.Root, "p-ticket").Length > 0)
            throw new BrokerOrderRouteException(
                $"Tradovate: rate limited — asked to wait {Str(answer.Root, "p-time")} s; nothing was sent.", isRejection: true);
        if (Words(answer.Root) is { } refusal)
            throw RefusedInBody(refusal, answer.Body);
        return answer.Root;
    }

    private Task<JsonElement> GetJsonAsync(RouteEnvironment environment, string path, CancellationToken ct) =>
        RequestAsync(environment, HttpMethod.Get, path, null, ct);

    /// <summary><c>{"errorText"}</c>, or a place/cancel's <c>{"failureReason","failureText"}</c>.</summary>
    internal static string? Words(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (Str(root, "errorText") is { Length: > 0 } error) return error;
        var reason = Str(root, "failureReason");
        var text = Str(root, "failureText");
        if (reason.Length == 0 && text.Length == 0) return null;
        return string.Join(": ", new[] { reason, text }.Where(s => s.Length > 0));
    }

    private async Task<(long Id, string Name)> AccountOfAsync(RouteEnvironment environment, CancellationToken ct)
    {
        if (_account is { } known) return known;
        var root = await GetJsonAsync(environment, "/account/list", ct).ConfigureAwait(false);
        var account = PickAccount(root) ?? throw new BrokerOrderRouteException("Tradovate: the session reaches no active account.", isRejection: true);
        _account = account;
        return account;
    }

    /// <summary><c>[{"id","name","active","archived"}]</c> — the first active, unarchived account.</summary>
    internal static (long Id, string Name)? PickAccount(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in root.EnumerateArray())
        {
            if (a.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False) continue;
            if (a.TryGetProperty("archived", out var archived) && archived.ValueKind == JsonValueKind.True) continue;
            if ((long)Dec(a, "id") is > 0 and var id) return (id, Str(a, "name"));
        }

        return null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var (id, name) = await AccountOfAsync(environment, ct).ConfigureAwait(false);
        var cash = await RequestAsync(environment, HttpMethod.Post, "/cashBalance/getcashbalancesnapshot", new JsonObject { ["accountId"] = id }, ct)
            .ConfigureAwait(false);
        var total = Dec(cash, "totalCashValue");
        var available = Dec(cash, "netLiq") is not 0 and var liq ? liq - Dec(cash, "initialMargin") : total;
        return new RouteAccount(name, "USD", total, available);
    }

    private async Task<long> ContractIdAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        if (_contracts.TryGetValue(symbol.Trim(), out var id)) return id;
        var contract = await GetJsonAsync(environment, $"/contract/find?name={Uri.EscapeDataString(symbol.Trim())}", ct).ConfigureAwait(false);
        id = (long)Dec(contract, "id");
        if (id <= 0) throw new BrokerOrderRouteException($"Tradovate knows no contract {symbol}.", isRejection: true);
        return _contracts[symbol.Trim()] = id;
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var contract = await GetJsonAsync(environment, $"/contract/find?name={Uri.EscapeDataString(symbol.Trim())}", ct).ConfigureAwait(false);
        if ((long)Dec(contract, "id") is > 0 and var id) _contracts[symbol.Trim()] = id;
        else throw new BrokerOrderRouteException($"Tradovate knows no contract {symbol}.", isRejection: true);
        var maturity = await GetJsonAsync(environment, $"/contractMaturity/item?id={(long)Dec(contract, "contractMaturityId")}", ct).ConfigureAwait(false);
        var product = await GetJsonAsync(environment, $"/product/item?id={(long)Dec(maturity, "productId")}", ct).ConfigureAwait(false);
        var currency = await GetJsonAsync(environment, $"/currency/item?id={(long)Dec(product, "currencyId")}", ct).ConfigureAwait(false);
        return ReadInstrument(Str(contract, "name") is { Length: > 0 } name ? name : symbol.Trim(), product, Str(currency, "name"))
            ?? throw new BrokerOrderRouteException($"Tradovate gave no tick size or point value for {symbol}.", isRejection: true);
    }

    /// <summary>The product's <c>{"valuePerPoint","tickSize"}</c>: one contract, worth the point value per point.</summary>
    internal static RouteInstrument? ReadInstrument(string symbol, JsonElement product, string currency)
    {
        var point = Dec(product, "valuePerPoint");
        var tick = Dec(product, "tickSize");
        if (point <= 0 || tick <= 0) return null;
        return new RouteInstrument(symbol, 1m, point, tick, 1, 10_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, currency.Length > 0 ? currency : "USD");
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var (id, name) = await AccountOfAsync(environment, ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Post, "/order/placeorder", SubmitBody(request, id, name), ct).ConfigureAwait(false);
        var orderId = Str(root, "orderId");
        if (orderId.Length == 0)
            throw new InvalidDataException($"Tradovate answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, long accountId, string accountName)
    {
        var body = new JsonObject
        {
            ["accountSpec"] = accountName,
            ["accountId"] = accountId,
            ["action"] = request.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["symbol"] = request.Symbol.Trim(),
            ["orderQty"] = request.Quantity,
            ["orderType"] = request.Type switch
            {
                RouteOrderType.Limit => "Limit",
                RouteOrderType.Stop => "Stop",
                RouteOrderType.StopLimit => "StopLimit",
                _ => "Market",
            },
            ["timeInForce"] = request.TimeInForce switch
            {
                RouteTimeInForce.GoodTillCancelled => "GTC",
                RouteTimeInForce.ImmediateOrCancel => "IOC",
                RouteTimeInForce.FillOrKill => "FOK",
                _ => "Day",
            },
            ["isAutomated"] = true,
        };
        if (request.LimitPrice is { } limit) body["price"] = limit;
        if (request.StopPrice is { } stop) body["stopPrice"] = stop;
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await RequestAsync(environment, HttpMethod.Post, "/order/cancelorder",
            new JsonObject { ["orderId"] = long.Parse(order.OrderId, CultureInfo.InvariantCulture), ["isAutomated"] = true }, ct).ConfigureAwait(false);

    /// <summary>The account's working orders in this contract and every order of the last day, with their fills.</summary>
    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (accountId, _) = await AccountOfAsync(environment, ct).ConfigureAwait(false);
        var contractId = await ContractIdAsync(environment, symbol, ct).ConfigureAwait(false);
        var orders = await GetJsonAsync(environment, $"/order/deps?masterid={accountId}", ct).ConfigureAwait(false);
        if (orders.ValueKind != JsonValueKind.Array) return [];
        var since = Now.UtcDateTime.AddDays(-1);
        var wanted = orders.EnumerateArray()
            .Where(o => (long)Dec(o, "contractId") == contractId &&
                        (!Terminal(Str(o, "ordStatus")) || ParseTime(Str(o, "timestamp"), DateTime.MinValue) >= since))
            .ToArray();
        if (wanted.Length == 0) return [];
        var fills = await GetJsonAsync(environment, "/fill/list", ct).ConfigureAwait(false);
        var list = new List<RouteOrder>();
        foreach (var order in wanted)
            list.Add(await ReadOrderAsync(environment, order, fills, symbol, ct).ConfigureAwait(false));
        return list;
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (!long.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) return null;
        var order = await GetJsonAsync(environment, $"/order/item?id={id}", ct).ConfigureAwait(false);
        if ((long)Dec(order, "id") != id) return null;
        var fills = await GetJsonAsync(environment, $"/fill/deps?masterid={id}", ct).ConfigureAwait(false);
        return await ReadOrderAsync(environment, order, fills, symbol, ct).ConfigureAwait(false);
    }

    private static bool Terminal(string status) => status is "Filled" or "Canceled" or "Rejected" or "Expired" or "Completed";

    private async Task<RouteOrder> ReadOrderAsync(RouteEnvironment environment, JsonElement order, JsonElement fills, string symbol, CancellationToken ct)
    {
        var id = (long)Dec(order, "id");
        if (!_versions.TryGetValue(id, out var version))
        {
            var versions = await GetJsonAsync(environment, $"/orderVersion/deps?masterid={id}", ct).ConfigureAwait(false);
            version = LatestVersion(versions);
            if (version.ValueKind == JsonValueKind.Object) _versions[id] = version;
        }

        return ReadOrder(order, version, fills, symbol);
    }

    /// <summary><c>[{"id","orderId","orderQty","orderType","price","stopPrice","timeInForce"}]</c> — the newest.</summary>
    internal static JsonElement LatestVersion(JsonElement versions) =>
        versions.ValueKind == JsonValueKind.Array && versions.GetArrayLength() > 0
            ? versions.EnumerateArray().MaxBy(v => Dec(v, "id"))
            : default;

    /// <summary>
    /// An order from its three records: <c>{"id","action","ordStatus","timestamp"}</c>, its version, and the fills
    /// <c>[{"orderId","qty","price","active"}]</c> — only active fills count; a busted fill is inactive.
    /// </summary>
    internal static RouteOrder ReadOrder(JsonElement order, JsonElement version, JsonElement fills, string symbol)
    {
        var id = (long)Dec(order, "id");
        var (filled, notional) = (0m, 0m);
        if (fills.ValueKind == JsonValueKind.Array)
            foreach (var fill in fills.EnumerateArray())
            {
                if ((long)Dec(fill, "orderId") != id || (fill.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)) continue;
                filled += Dec(fill, "qty");
                notional += Dec(fill, "qty") * Dec(fill, "price");
            }

        var status = Str(order, "ordStatus") switch
        {
            "Filled" or "Completed" when filled > 0 => RouteOrderStatus.Filled,
            "Completed" => RouteOrderStatus.Cancelled,
            "Filled" => RouteOrderStatus.Unknown,
            "Canceled" => RouteOrderStatus.Cancelled,
            "Expired" => RouteOrderStatus.Expired,
            "Rejected" => RouteOrderStatus.Rejected,
            "PendingCancel" => RouteOrderStatus.PendingCancel,
            "PendingNew" => RouteOrderStatus.PendingNew,
            "Working" or "PendingReplace" or "Suspended" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var quantity = Dec(version, "orderQty");
        if (status == RouteOrderStatus.Filled && quantity > 0 && filled < quantity) status = RouteOrderStatus.PartiallyFilled;
        return new RouteOrder(
            id.ToString(CultureInfo.InvariantCulture),
            string.Empty,
            symbol,
            Str(order, "action") == "Sell" ? OrderSide.Sell : OrderSide.Buy,
            Str(version, "orderType") switch
            {
                "Limit" => RouteOrderType.Limit,
                "Stop" => RouteOrderType.Stop,
                "StopLimit" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(version, "timeInForce") switch
            {
                "GTC" => RouteTimeInForce.GoodTillCancelled,
                "IOC" => RouteTimeInForce.ImmediateOrCancel,
                "FOK" => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            quantity,
            Positive(version, "price"),
            Positive(version, "stopPrice"),
            status,
            filled,
            filled > 0 ? notional / filled : null,
            0m,
            string.Empty,
            null,
            ParseTime(Str(order, "timestamp"), DateTime.UtcNow));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (accountId, _) = await AccountOfAsync(environment, ct).ConfigureAwait(false);
        var contractId = await ContractIdAsync(environment, symbol, ct).ConfigureAwait(false);
        var positions = await GetJsonAsync(environment, $"/position/deps?masterid={accountId}", ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(positions, contractId));
    }

    /// <summary><c>[{"contractId","netPos"}]</c> — signed contracts.</summary>
    internal static decimal ReadPosition(JsonElement positions, long contractId) =>
        positions.ValueKind == JsonValueKind.Array
            ? positions.EnumerateArray().Where(p => (long)Dec(p, "contractId") == contractId).Sum(p => Dec(p, "netPos"))
            : 0m;

    /// <summary>Tradovate quotes stream over its WebSocket only; there is no REST price to read.</summary>
    public override Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        Task.FromResult<RoutePrice?>(null);
}
