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

namespace TradingTerminal.Infrastructure.IronBeam;

/// <summary>
/// Ironbeam futures orders over its REST API v2, with the username and API key the login window stores.
///
/// <para><b>Paper is Ironbeam's demo</b> (demo.ironbeamapi.com), which has its own usernames: the login row's
/// live switch says which gateway the stored username belongs to, and a card for the other is refused with
/// that said. <c>POST /auth</c> trades the key for a bearer token, kept until the API answers 401.</para>
///
/// <para><b>From the API's own OpenAPI document</b> (docs.ironbeamapi.com), read 2026-09-25: order type and
/// duration are sent as the codes it defines (<c>"1"</c> market … <c>"4"</c> stop-limit; <c>"0"</c> day,
/// <c>"1"</c> good-till-cancel), and <c>waitForOrderId</c> asks for the order id in the answer. An order
/// reports its cumulative <c>fillQuantity</c> and average <c>fillPrice</c>. Symbols are exchange symbols
/// (<c>XCME:ES.Z26</c>); value per point is the definition's increment value over its increment. There is no
/// client order id, and a replace may issue a new order id, so replace is off. The route trades the first
/// account the login reaches. Not yet run against a real account.</para>
/// </summary>
internal sealed class IronBeamOrderRoute : OrderRouteBase
{
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private (RouteEnvironment Environment, string Value)? _token;
    private (RouteEnvironment Environment, string Value)? _account;

    public IronBeamOrderRoute(IBrokerCredentialSource credentials, ILogger<IronBeamOrderRoute> logger)
        : base(credentials, logger) { }

    internal IronBeamOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.IronBeam;
    public override string DisplayName => "Ironbeam";
    public override string RouteId => "ironbeam";
    public override string? PaperEnvironmentName => "DEMO";

    private bool LiveLogin => Credential.Extra.Trim().Equals("live", StringComparison.OrdinalIgnoreCase);

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://live.ironbeamapi.com/v2" : "https://demo.ironbeamapi.com/v2";

    private void RequireEnvironment(RouteEnvironment environment)
    {
        if ((environment == RouteEnvironment.Live) == LiveLogin) return;
        throw new BrokerOrderRouteException(
            $"Ironbeam: the stored username is for the {(LiveLogin ? "live" : "demo")} gateway, and this card trades {(environment == RouteEnvironment.Live ? "live" : "demo")}. "
            + "Change the login row's live switch and enter that gateway's username and key.", isRejection: true);
    }

    private async Task<string> TokenAsync(RouteEnvironment environment, CancellationToken ct)
    {
        if (_token is { } token && token.Environment == environment) return token.Value;
        await _authGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } fresh && fresh.Environment == environment) return fresh.Value;
            var app = Credential;
            if (!app.IsConfigured)
                throw new BrokerOrderRouteException("Ironbeam: no username and API key are stored — enter them in the login window.", isRejection: true);
            var body = new JsonObject { ["username"] = app.Key.Trim(), ["password"] = app.Secret.Trim() }.ToJsonString();
            var (status, root, text) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, Host(environment) + "/auth")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }, ct).ConfigureAwait(false);
            if (status is < 200 or >= 300 || Str(root, "token").Length == 0)
                throw Refused(status, Str(root, "message") is { Length: > 0 } m ? m : "no token issued", text, status is >= 500 ? false : true);
            _token = (environment, Str(root, "token"));
            return _token.Value.Value;
        }
        finally
        {
            _authGate.Release();
        }
    }

    private async Task<JsonElement> CallAsync(RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        RequireEnvironment(environment);
        for (var attempt = 1; ; attempt++)
        {
            var token = await TokenAsync(environment, ct).ConfigureAwait(false);
            var (status, root, text) = await SendAsync(() =>
            {
                var request = new HttpRequestMessage(method, Host(environment) + path);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }, ct).ConfigureAwait(false);
            if (status == 401 && attempt == 1)
            {
                // The token was checked before the request was read; nothing was placed.
                lock (_authGate)
                    if (_token is { } held && held.Value == token) _token = null;
                continue;
            }

            if (status is < 200 or >= 300)
                throw Refused(status, Str(root, "message"), text);
            if (Str(root, "status") is "ERROR" or "FATAL")
                throw RefusedInBody(Str(root, "message"), text);
            return root;
        }
    }

    private async Task<string> AccountIdAsync(RouteEnvironment environment, CancellationToken ct)
    {
        if (_account is { } known && known.Environment == environment) return known.Value;
        var root = await CallAsync(environment, HttpMethod.Get, "/account/getAllAccounts", null, ct).ConfigureAwait(false);
        var account = root.TryGetProperty("accounts", out var accounts) && accounts.ValueKind == JsonValueKind.Array && accounts.GetArrayLength() > 0
            ? accounts[0].GetString()
            : null;
        if (string.IsNullOrWhiteSpace(account))
            throw new BrokerOrderRouteException("Ironbeam: the login reaches no account.", isRejection: true);
        _account = (environment, account);
        return account;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await CallAsync(environment, HttpMethod.Get, $"/account/{account}/balance?balanceType=CURRENT_OPEN", null, ct).ConfigureAwait(false);
        var balance = root.TryGetProperty("balances", out var rows) && rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() > 0 ? rows[0] : default;
        return new RouteAccount(account, Str(balance, "currencyCode") is { Length: > 0 } c ? c : "USD",
            Dec(balance, "cashBalance"), Dec(balance, "cashBalanceAvailable"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, $"/info/security/definitions?symbols={Uri.EscapeDataString(symbol.Trim())}", null, ct)
            .ConfigureAwait(false);
        return ReadInstrument(root, symbol.Trim())
            ?? throw new BrokerOrderRouteException($"Ironbeam has no tradable definition for {symbol}.", isRejection: true);
    }

    /// <summary><c>{"securityDefinitions":[{"exchSym","minPriceIncrement","minPriceIncrementValue","currencyCode","allowTrading"}]}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("securityDefinitions", out var rows) || rows.ValueKind != JsonValueKind.Array) return null;
        foreach (var d in rows.EnumerateArray())
        {
            if (!string.Equals(Str(d, "exchSym"), symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (d.TryGetProperty("allowTrading", out var allowed) && allowed.ValueKind == JsonValueKind.False) return null;
            var tick = Dec(d, "minPriceIncrement");
            var tickValue = Dec(d, "minPriceIncrementValue");
            if (tick <= 0 || tickValue <= 0) return null;
            return new RouteInstrument(Str(d, "exchSym"), 1m, tickValue / tick, tick, 1, 10_000,
                RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
                RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled,
                SupportsReplace: false, Str(d, "currencyCode") is { Length: > 0 } currency ? currency : "USD");
        }

        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await CallAsync(environment, HttpMethod.Post, $"/order/{account}/place", SubmitBody(request, account), ct).ConfigureAwait(false);
        var id = Str(root, "orderId");
        if (id.Length == 0)
            throw new InvalidDataException($"Ironbeam answered the order without an order id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string account)
    {
        var body = new JsonObject
        {
            ["accountId"] = account,
            ["exchSym"] = request.Symbol.Trim(),
            ["side"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["quantity"] = request.Quantity,
            ["orderType"] = request.Type switch
            {
                RouteOrderType.Limit => "2",
                RouteOrderType.Stop => "3",
                RouteOrderType.StopLimit => "4",
                _ => "1",
            },
            ["duration"] = request.TimeInForce == RouteTimeInForce.GoodTillCancelled ? "1" : "0",
            ["waitForOrderId"] = true,
        };
        if (request.LimitPrice is { } limit) body["limitPrice"] = limit;
        if (request.StopPrice is { } stop) body["stopPrice"] = stop;
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        _ = await CallAsync(environment, HttpMethod.Delete, $"/order/{account}/cancel/{Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await CallAsync(environment, HttpMethod.Get, $"/order/{account}/ANY", null, ct).ConfigureAwait(false);
        return ReadOrders(root, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
        (await OrdersAsync(environment, symbol, ct).ConfigureAwait(false)).FirstOrDefault(o => o.OrderId == orderId);

    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement root, string symbol) =>
        root.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array
            ? [.. orders.EnumerateArray()
                .Where(o => string.Equals(Str(o, "exchSym"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(o => ReadOrder(o, symbol))]
            : [];

    /// <summary><c>{"orderId","exchSym","status","side","quantity","limitPrice","stopPrice","orderType","duration","fillQuantity",
    /// "fillPrice","fillDate","orderError":{"errorText"}}</c> — types and durations arrive as names or as the codes the request uses.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "fillQuantity");
        var status = Str(o, "status") switch
        {
            "FILLED" or "COMPLETE" => RouteOrderStatus.Filled,
            "PARTIALLY_FILLED" => RouteOrderStatus.PartiallyFilled,
            "CANCELLED" or "REPLACED" => RouteOrderStatus.Cancelled,
            "EXPIRED" or "DONE_FOR_DAY" => RouteOrderStatus.Expired,
            "REJECTED" => RouteOrderStatus.Rejected,
            "PENDING_CANCEL" or "QUEUED_CANCEL" => RouteOrderStatus.PendingCancel,
            "SUBMITTED" or "PENDING_NEW" or "QUEUED_NEW" => RouteOrderStatus.PendingNew,
            "NEW" or "ACCEPTED_FOR_BIDDING" or "CALCULATED" or "STOPPED" or "SUSPENDED" or "PENDING_REPLACE" or "CANCEL_REJECTED" =>
                filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var error = o.TryGetProperty("orderError", out var e) ? Str(e, "errorText") : string.Empty;
        return new RouteOrder(
            Str(o, "orderId"),
            string.Empty,
            symbol,
            Str(o, "side") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            Str(o, "orderType") switch
            {
                "LIMIT" or "2" => RouteOrderType.Limit,
                "STOP" or "3" => RouteOrderType.Stop,
                "STOP_LIMIT" or "4" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "duration") is "GOOD_TILL_CANCEL" or "1" ? RouteTimeInForce.GoodTillCancelled : RouteTimeInForce.Day,
            Dec(o, "quantity"),
            Positive(o, "limitPrice"),
            Positive(o, "stopPrice"),
            status,
            filled,
            Positive(o, "fillPrice"),
            0m,
            string.Empty,
            error.Length > 0 ? error : null,
            ParseTime(Str(o, "fillDate"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await CallAsync(environment, HttpMethod.Get, $"/account/{account}/positions", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(root, symbol));
    }

    /// <summary><c>{"positions":[{"exchSym","quantity","side":"LONG|SHORT"}]}</c>.</summary>
    internal static decimal ReadPosition(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("positions", out var rows) || rows.ValueKind != JsonValueKind.Array) return 0m;
        var net = 0m;
        foreach (var p in rows.EnumerateArray())
        {
            if (!string.Equals(Str(p, "exchSym"), symbol.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var quantity = Math.Abs(Dec(p, "quantity"));
            net += Str(p, "side") == "SHORT" ? -quantity : quantity;
        }

        return net;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, $"/market/quotes?symbols={Uri.EscapeDataString(symbol.Trim())}", null, ct).ConfigureAwait(false);
        var q = root.TryGetProperty("Quotes", out var quotes) && quotes.ValueKind == JsonValueKind.Array && quotes.GetArrayLength() > 0 ? quotes[0] : default;
        var price = Positive(q, "l") ?? (Positive(q, "b") is { } bid && Positive(q, "a") is { } ask ? (bid + ask) / 2 : null);
        return price is { } p ? new RoutePrice(p, Utc((long)Dec(q, "tt"))) : null;
    }
}
