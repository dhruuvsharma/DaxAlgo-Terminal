using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Upbit;

/// <summary>
/// Upbit orders — and Bithumb's, whose v1 API copies Upbit's. Each call carries an HS256 token with the key,
/// a fresh nonce, and when there are parameters a SHA-512 hash of them as a query string.
///
/// <para><b>Limit orders only.</b> An Upbit market buy is sized in won, not coins, so it is not offered;
/// an immediate-or-cancel limit is the exact substitute. <b>Price steps depend on the price</b> — a coin
/// above two million won moves in steps of a thousand — so the tick is taken from the won tick table at
/// the price when the book attaches. The table is Upbit's published one as remembered here; a step that has
/// since changed is refused by Upbit with its own message, never silently rounded.</para>
///
/// <para>Neither exchange has a test environment or names an account in its API (the key stands in).
/// Written 2026-09-25 from Upbit's and Bithumb's v1 references; not yet run against a real account.</para>
/// </summary>
internal abstract class UpbitShapedOrderRoute : OrderRouteBase
{
    protected UpbitShapedOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider? time = null, HttpMessageHandler? handler = null)
        : base(credentials, logger, time, handler) { }

    public override string? PaperEnvironmentName => null;

    protected abstract string Host { get; }

    /// <summary>Extra claims (Bithumb adds a millisecond timestamp).</summary>
    protected virtual IEnumerable<KeyValuePair<string, object>> ExtraClaims() => [];

    /// <summary>The open-orders query: Upbit's <c>/v1/orders/open</c>, Bithumb's <c>state=wait</c>.</summary>
    protected abstract string OpenOrdersPath(string market);

    private async Task<JsonElement> CallAsync(HttpMethod method, string path, string query, JsonObject? body, CancellationToken ct, bool allowNotFound = false)
    {
        // The hash covers the parameters as a query string, whether they travel in the URL or the body.
        var hashed = body is null ? query : string.Join('&', body.Select(p => $"{p.Key}={p.Value}"));
        var (status, root, text) = await SendAsync(() =>
        {
            var claims = new List<KeyValuePair<string, object>>
            {
                new("access_key", Credential.Key.Trim()),
                new("nonce", Guid.NewGuid().ToString()),
            };
            if (hashed.Length > 0)
            {
                claims.Add(new("query_hash", Convert.ToHexStringLower(SHA512.HashData(Encoding.UTF8.GetBytes(hashed)))));
                claims.Add(new("query_hash_alg", "SHA512"));
            }
            claims.AddRange(ExtraClaims());
            var request = new HttpRequestMessage(method, $"{Host}{path}{(query.Length > 0 ? "?" + query : string.Empty)}");
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CryptoAuth.JwtHs256(claims, Credential.Secret));
            return request;
        }, ct).ConfigureAwait(false);
        if (allowNotFound && status == 404)
            return default;
        if (status is < 200 or >= 300)
        {
            var error = root.TryGetProperty("error", out var e) ? e : default;
            throw Refused(status, $"{Str(error, "message")} ({Str(error, "name")})", text);
        }
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var (total, available) = await BalanceAsync("KRW", ct).ConfigureAwait(false);
        return new RouteAccount(KeyAccount(), "KRW", total, available);
    }

    private async Task<(decimal Total, decimal Available)> BalanceAsync(string currency, CancellationToken ct)
    {
        var accounts = await CallAsync(HttpMethod.Get, "/v1/accounts", string.Empty, null, ct).ConfigureAwait(false);
        if (accounts.ValueKind == JsonValueKind.Array)
            foreach (var account in accounts.EnumerateArray())
                if (string.Equals(Str(account, "currency"), currency, StringComparison.OrdinalIgnoreCase))
                    return (Dec(account, "balance") + Dec(account, "locked"), Dec(account, "balance"));
        return (0m, 0m);
    }

    /// <summary>The won price step at <paramref name="price"/>, from the KRW tick table.</summary>
    internal static decimal KrwTick(decimal price) => price switch
    {
        >= 2_000_000m => 1_000m,
        >= 1_000_000m => 500m,
        >= 500_000m => 100m,
        >= 100_000m => 50m,
        >= 10_000m => 10m,
        >= 1_000m => 1m,
        >= 100m => 0.1m,
        >= 10m => 0.01m,
        >= 1m => 0.001m,
        _ => 0.0001m,
    };

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var price = await PriceAsync(environment, symbol, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"{DisplayName} has no price for {symbol}, so its price step cannot be known.");
        var parts = symbol.Split('-');
        if (parts.Length != 2)
            throw new InvalidDataException($"{DisplayName} markets are written QUOTE-BASE (KRW-BTC); {symbol} is not.");
        var tick = parts[0] == "KRW" ? KrwTick(price.Price) : 0.00000001m;
        const decimal step = 0.00000001m;
        return new RouteInstrument(symbol, step, step, tick, 1, long.MaxValue / 2,
            RouteOrderTypes.Limit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, parts[0])
        {
            BaseAsset = parts[1],
        };
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["market"] = request.Symbol,
            ["side"] = request.Side == OrderSide.Buy ? "bid" : "ask",
            ["volume"] = Num(request.Quantity),
            ["price"] = Num(request.LimitPrice ?? throw new BrokerOrderRouteException($"{DisplayName} orders need a limit price.", isRejection: true)),
            ["ord_type"] = "limit",
            ["identifier"] = Token(request.ClientOrderId, id => id.Length <= 64 ? id : Hex(id, 32)),
        };
        if (request.TimeInForce is RouteTimeInForce.ImmediateOrCancel or RouteTimeInForce.FillOrKill)
            body["time_in_force"] = request.TimeInForce == RouteTimeInForce.FillOrKill ? "fok" : "ioc";
        return ReadOrder(await CallAsync(HttpMethod.Post, "/v1/orders", string.Empty, body, ct).ConfigureAwait(false));
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(HttpMethod.Delete, "/v1/order", $"uuid={Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var path = OpenOrdersPath(symbol);
        var question = path.IndexOf('?');
        var answer = await CallAsync(HttpMethod.Get, path[..question], path[(question + 1)..], null, ct).ConfigureAwait(false);
        var orders = answer.ValueKind == JsonValueKind.Array ? answer.EnumerateArray().Select(ReadOrder).ToList() : [];
        // A list answer carries no trades, so no fill price; an order with fills is read again on its own.
        for (var i = 0; i < orders.Count; i++)
            if (orders[i].FilledQuantity > 0 && await OrderAsync(environment, symbol, orders[i].OrderId, orders[i].ClientOrderId, ct).ConfigureAwait(false) is { } detailed)
                orders[i] = detailed;
        return orders;
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var order = await CallAsync(HttpMethod.Get, "/v1/order", $"uuid={Uri.EscapeDataString(orderId)}", null, ct, allowNotFound: true).ConfigureAwait(false);
        return order.ValueKind == JsonValueKind.Object ? ReadOrder(order) : null;
    }

    /// <summary><c>{"uuid","side":"bid|ask","ord_type","price","state":"wait|watch|done|cancel","market","volume",
    /// "executed_volume","paid_fee","identifier","created_at","trades":[{"price","volume","funds"}]}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o)
    {
        var executed = Dec(o, "executed_volume");
        decimal? average = null;
        if (o.TryGetProperty("trades", out var trades) && trades.ValueKind == JsonValueKind.Array && trades.GetArrayLength() > 0)
        {
            var volume = trades.EnumerateArray().Sum(t => Dec(t, "volume"));
            var funds = trades.EnumerateArray().Sum(t => Dec(t, "funds"));
            average = volume > 0 ? funds / volume : null;
        }
        var market = Str(o, "market");
        return new RouteOrder(
            Str(o, "uuid"),
            EngineId(Str(o, "identifier")),
            market,
            Str(o, "side") == "ask" ? OrderSide.Sell : OrderSide.Buy,
            RouteOrderType.Limit,
            Str(o, "time_in_force") switch { "ioc" => RouteTimeInForce.ImmediateOrCancel, "fok" => RouteTimeInForce.FillOrKill, _ => RouteTimeInForce.GoodTillCancelled },
            Dec(o, "volume"),
            Positive(o, "price"),
            null,
            Str(o, "state") switch
            {
                "wait" or "watch" => executed > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "done" => RouteOrderStatus.Filled,
                "cancel" => RouteOrderStatus.Cancelled,
                _ => RouteOrderStatus.Unknown,
            },
            executed,
            // Without the trades (a list answer), the average is not known yet; the poll asks per order.
            executed > 0 ? average : null,
            Dec(o, "paid_fee"),
            market.Split('-')[0],
            null,
            ParseTime(Str(o, "created_at"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (total, _) = await BalanceAsync(rules.BaseAsset, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/v1/ticker?markets={Uri.EscapeDataString(symbol)}"), ct).ConfigureAwait(false);
        return status == 200 && root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && Positive(root[0], "trade_price") is { } price
            ? new RoutePrice(price, Now.UtcDateTime)
            : null;
    }
}

internal sealed class UpbitOrderRoute : UpbitShapedOrderRoute
{
    public UpbitOrderRoute(IBrokerCredentialSource credentials, ILogger<UpbitOrderRoute> logger)
        : base(credentials, logger) { }

    internal UpbitOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Upbit;
    public override string DisplayName => "Upbit";
    public override string RouteId => "upbit";
    protected override string Host => "https://api.upbit.com";

    protected override string OpenOrdersPath(string market) => $"/v1/orders/open?market={Uri.EscapeDataString(market)}";
}

internal sealed class BithumbOrderRoute : UpbitShapedOrderRoute
{
    public BithumbOrderRoute(IBrokerCredentialSource credentials, ILogger<BithumbOrderRoute> logger)
        : base(credentials, logger) { }

    internal BithumbOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Bithumb;
    public override string DisplayName => "Bithumb";
    public override string RouteId => "bithumb";
    protected override string Host => "https://api.bithumb.com";

    protected override IEnumerable<KeyValuePair<string, object>> ExtraClaims() => [new("timestamp", Now.ToUnixTimeMilliseconds())];

    protected override string OpenOrdersPath(string market) => $"/v1/orders?market={Uri.EscapeDataString(market)}&state=wait";
}
