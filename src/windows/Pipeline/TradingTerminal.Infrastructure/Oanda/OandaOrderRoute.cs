using System.Collections.Concurrent;
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

namespace TradingTerminal.Infrastructure.Oanda;

/// <summary>
/// OANDA v20 orders: FX, metals and CFDs, with the token and account id the login window stores (the
/// account id rides in the key half of the row).
///
/// <para><b>Paper is OANDA's practice environment</b> (api-fxpractice.oanda.com), with its own token and
/// account: a practice token is refused by the live host and the other way round.</para>
///
/// <para><b>Units are signed</b> — a sell is negative units — and an order carries no fill price: a filled
/// order names its <c>fillingTransactionID</c>, and the price and commission are read from that transaction
/// once and cached. A market order is filled or cancelled in the answer to the POST itself, so the route
/// reads it there. OANDA takes a market order only as FOK or IOC; a Day or GTC market order is sent FOK,
/// which is what a market order does anyway. Stop is OANDA's stop-entry order (a market order when
/// touched); there is no stop-limit. Replacing an OANDA order issues a new order id, so replace is off.</para>
///
/// <para>Value per point is one unit in the <i>quote</i> currency; for a pair not quoted in the account
/// currency it is approximate. Written 2026-09-25 from OANDA's v20 reference; not yet run against a real
/// account.</para>
/// </summary>
internal sealed class OandaOrderRoute : OrderRouteBase
{
    private readonly ConcurrentDictionary<string, (decimal Price, decimal Fee)> _fills = new(StringComparer.Ordinal);

    public OandaOrderRoute(IBrokerCredentialSource credentials, ILogger<OandaOrderRoute> logger)
        : base(credentials, logger) { }

    internal OandaOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Oanda;
    public override string DisplayName => "OANDA";
    public override string RouteId => "oanda";
    public override string? PaperEnvironmentName => "PRACTICE";

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api-fxtrade.oanda.com/v3" : "https://api-fxpractice.oanda.com/v3";

    private string Account => Credential.Key.Trim() is { Length: > 0 } account
        ? account
        : throw new BrokerOrderRouteException("OANDA: no account id is stored — enter it in the login window.", isRejection: true);

    private async Task<(int Status, JsonElement Root)> CallAsync(
        RouteEnvironment environment, HttpMethod method, string path, JsonNode? body, CancellationToken ct, bool allowNotFound = false)
    {
        var url = $"{Host(environment)}/accounts/{Uri.EscapeDataString(Account)}{path}";
        var (status, root, text) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, url);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential.Secret.Trim());
            request.Headers.TryAddWithoutValidation("Accept-Datetime-Format", "RFC3339");
            return request;
        }, ct).ConfigureAwait(false);
        if (allowNotFound && status == 404)
            return (status, root);
        if (status is < 200 or >= 300)
            throw Refused(status, Words(root, status == 401 ? environment : null), text);
        return (status, root);
    }

    /// <summary><c>{"errorMessage","errorCode","orderRejectTransaction":{"rejectReason"}}</c>.</summary>
    internal static string? Words(JsonElement root, RouteEnvironment? unauthorised = null)
    {
        var reject = root.TryGetProperty("orderRejectTransaction", out var r) ? Str(r, "rejectReason") : string.Empty;
        var message = Str(root, "errorMessage");
        var words = string.Join(" — ", new[] { message, reject }.Where(s => s.Length > 0));
        if (unauthorised is { } environment)
            words = $"{(words.Length > 0 ? words : "the token was refused")}; practice and live issue separate tokens, and this card trades "
                + (environment == RouteEnvironment.Live ? "live" : "practice");
        return words.Length > 0 ? words : null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get, "/summary", null, ct).ConfigureAwait(false);
        var account = ReadAccount(root, Account);
        AccountCurrency = account.Currency;
        return account;
    }

    /// <summary><c>{"account":{"id","currency","balance","marginAvailable"}}</c>.</summary>
    internal static RouteAccount ReadAccount(JsonElement root, string fallbackId)
    {
        var account = root.TryGetProperty("account", out var a) ? a : root;
        return new RouteAccount(Str(account, "id") is { Length: > 0 } id ? id : fallbackId, Str(account, "currency"),
            Dec(account, "balance"), Dec(account, "marginAvailable"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get, $"/instruments?instruments={Uri.EscapeDataString(symbol.Trim())}", null, ct)
            .ConfigureAwait(false);
        return ReadInstrument(root, symbol.Trim()) ?? throw new BrokerOrderRouteException($"OANDA: this account cannot trade {symbol}.", isRejection: true);
    }

    /// <summary><c>{"instruments":[{"name":"EUR_USD","displayPrecision":5,"tradeUnitsPrecision":0,"minimumTradeSize":"1",
    /// "maximumOrderUnits":"100000000"}]}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("instruments", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var i in list.EnumerateArray())
        {
            if (!string.Equals(Str(i, "name"), symbol, StringComparison.OrdinalIgnoreCase))
                continue;
            var unit = Step((int)Dec(i, "tradeUnitsPrecision"));
            var tick = Step((int)Dec(i, "displayPrecision"));
            var name = Str(i, "name");
            var underscore = name.IndexOf('_');
            return new RouteInstrument(
                name, unit, unit, tick, Units(Dec(i, "minimumTradeSize"), unit, 1), Units(Dec(i, "maximumOrderUnits"), unit, 100_000_000),
                RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop,
                RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
                SupportsReplace: false, underscore > 0 ? name[(underscore + 1)..] : "USD");
        }

        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = SubmitBody(request, Token(request.ClientOrderId, id => id.Length <= 128 ? id : Hex(id, 32)));
        var (_, root) = await CallAsync(environment, HttpMethod.Post, "/orders", body, ct).ConfigureAwait(false);
        return ReadSubmit(root, request, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string clientId)
    {
        var units = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;
        var order = new JsonObject
        {
            ["type"] = request.Type switch
            {
                RouteOrderType.Limit => "LIMIT",
                RouteOrderType.Stop => "STOP",
                _ => "MARKET",
            },
            ["instrument"] = request.Symbol.Trim(),
            ["units"] = Num(units),
            ["timeInForce"] = (request.Type, request.TimeInForce) switch
            {
                (_, RouteTimeInForce.ImmediateOrCancel) => "IOC",
                (_, RouteTimeInForce.FillOrKill) => "FOK",
                (RouteOrderType.Market, _) => "FOK",
                (_, RouteTimeInForce.Day) => "GFD",
                _ => "GTC",
            },
            ["positionFill"] = "DEFAULT",
            ["clientExtensions"] = new JsonObject { ["id"] = clientId, ["tag"] = "daxalgo" },
        };
        if (request.Type == RouteOrderType.Limit)
            order["price"] = Num(request.LimitPrice!.Value);
        if (request.Type == RouteOrderType.Stop)
            order["price"] = Num(request.StopPrice!.Value);
        return new JsonObject { ["order"] = order };
    }

    /// <summary>The POST's answer: <c>orderCreateTransaction</c>, then <c>orderFillTransaction</c> or
    /// <c>orderCancelTransaction</c> when the order ended at once (a market order always does).</summary>
    internal RouteOrder ReadSubmit(JsonElement root, RouteOrderRequest request, DateTime now)
    {
        var id = root.TryGetProperty("orderCreateTransaction", out var create) ? Str(create, "id") : string.Empty;
        if (id.Length == 0)
            throw new InvalidDataException($"OANDA answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        var status = RouteOrderStatus.Working;
        var filled = 0m;
        decimal? average = null;
        var fee = 0m;
        string? reason = null;
        if (root.TryGetProperty("orderFillTransaction", out var fill) && fill.ValueKind == JsonValueKind.Object)
        {
            filled = Math.Abs(Dec(fill, "units"));
            average = Positive(fill, "price");
            fee = Math.Abs(Dec(fill, "commission"));
            status = filled >= request.Quantity ? RouteOrderStatus.Filled : RouteOrderStatus.PartiallyFilled;
            if (average is { } price) _fills[id] = (price, fee);
        }

        if (root.TryGetProperty("orderCancelTransaction", out var cancel) && cancel.ValueKind == JsonValueKind.Object)
        {
            status = RouteOrderStatus.Cancelled;
            reason = Str(cancel, "reason") is { Length: > 0 } words ? words : null;
        }

        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, status, filled, average, fee, fee > 0 ? AccountCurrency : string.Empty, reason, now);
    }

    /// <summary>OANDA charges commission in the account currency, which a fee needs named; read on connect.</summary>
    private string AccountCurrency { get; set; } = string.Empty;

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Put, $"/orders/{Uri.EscapeDataString(order.OrderId)}/cancel", null, ct).ConfigureAwait(false);

    /// <summary>Pending orders only: a filled one is read through <see cref="OrderAsync"/>, which fetches its fill
    /// transaction — listing every recent filled order would fetch fifty transactions on the first poll.</summary>
    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get, $"/pendingOrders", null, ct).ConfigureAwait(false);
        return root.TryGetProperty("orders", out var orders) && orders.ValueKind == JsonValueKind.Array
            ? [.. orders.EnumerateArray()
                .Where(o => string.Equals(Str(o, "instrument"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                .Select(o => ReadOrder(o, symbol, null))]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var specifier = orderId.Length > 0 ? orderId : "@" + Token(clientOrderId, id => id.Length <= 128 ? id : Hex(id, 32));
        var (status, root) = await CallAsync(environment, HttpMethod.Get, $"/orders/{Uri.EscapeDataString(specifier)}", null, ct, allowNotFound: true)
            .ConfigureAwait(false);
        if (status == 404 || !root.TryGetProperty("order", out var order))
            return null;
        (decimal Price, decimal Fee)? fill = null;
        if (Str(order, "state") == "FILLED")
        {
            var id = Str(order, "id");
            if (!_fills.TryGetValue(id, out var known) && Str(order, "fillingTransactionID") is { Length: > 0 } transactionId)
            {
                var (_, transaction) = await CallAsync(environment, HttpMethod.Get, $"/transactions/{Uri.EscapeDataString(transactionId)}", null, ct)
                    .ConfigureAwait(false);
                if (transaction.TryGetProperty("transaction", out var t) && Positive(t, "price") is { } price)
                    _fills[id] = known = (price, Math.Abs(Dec(t, "commission")));
            }

            if (known.Price > 0) fill = known;
        }

        return ReadOrder(order, symbol, fill);
    }

    /// <summary><c>{"id","type","instrument","units","timeInForce","price","state":"PENDING|FILLED|TRIGGERED|CANCELLED",
    /// "clientExtensions":{"id"},"createTime","filledTime","cancelledTime"}</c>. A filled order's price comes from its fill.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol, (decimal Price, decimal Fee)? fill)
    {
        var units = Dec(o, "units");
        var quantity = Math.Abs(units);
        var state = Str(o, "state");
        var filled = state == "FILLED" && fill is not null;
        var type = Str(o, "type");
        var clientId = o.TryGetProperty("clientExtensions", out var ext) ? Str(ext, "id") : string.Empty;
        var time = Str(o, "filledTime") is { Length: > 0 } f ? f : Str(o, "cancelledTime") is { Length: > 0 } c ? c : Str(o, "createTime");
        return new RouteOrder(
            Str(o, "id"),
            EngineId(clientId),
            symbol,
            units < 0 ? OrderSide.Sell : OrderSide.Buy,
            type switch { "LIMIT" => RouteOrderType.Limit, "STOP" => RouteOrderType.Stop, _ => RouteOrderType.Market },
            Str(o, "timeInForce") switch
            {
                "IOC" => RouteTimeInForce.ImmediateOrCancel,
                "FOK" => RouteTimeInForce.FillOrKill,
                "GFD" => RouteTimeInForce.Day,
                _ => RouteTimeInForce.GoodTillCancelled,
            },
            quantity,
            type == "LIMIT" ? Positive(o, "price") : null,
            type == "STOP" ? Positive(o, "price") : null,
            state switch
            {
                "PENDING" => RouteOrderStatus.Working,
                "TRIGGERED" => RouteOrderStatus.Working,
                "CANCELLED" => RouteOrderStatus.Cancelled,
                // Filled without a readable fill price is not a fill the engine can book exactly.
                "FILLED" => filled ? RouteOrderStatus.Filled : RouteOrderStatus.Unknown,
                _ => RouteOrderStatus.Unknown,
            },
            filled ? quantity : 0m,
            filled ? fill!.Value.Price : null,
            filled ? fill!.Value.Fee : 0m,
            filled && fill!.Value.Fee > 0 ? AccountCurrency : string.Empty,
            null,
            ParseTime(time, Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root) = await CallAsync(environment, HttpMethod.Get, $"/positions/{Uri.EscapeDataString(symbol.Trim())}", null, ct, allowNotFound: true)
            .ConfigureAwait(false);
        return new RoutePosition(symbol, status == 404 ? 0m : ReadPosition(root));
    }

    /// <summary><c>{"position":{"long":{"units":"100"},"short":{"units":"-50"}}}</c> — short units are already negative.</summary>
    internal static decimal ReadPosition(JsonElement root)
    {
        if (!root.TryGetProperty("position", out var p)) return 0m;
        var longUnits = p.TryGetProperty("long", out var l) ? Dec(l, "units") : 0m;
        var shortUnits = p.TryGetProperty("short", out var s) ? Dec(s, "units") : 0m;
        return longUnits + (shortUnits > 0 ? -shortUnits : shortUnits);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get, $"/pricing?instruments={Uri.EscapeDataString(symbol.Trim())}", null, ct)
            .ConfigureAwait(false);
        return ReadPrice(root, Now.UtcDateTime);
    }

    /// <summary><c>{"prices":[{"time","bids":[{"price"}],"asks":[{"price"}]}]}</c> — the mid.</summary>
    internal static RoutePrice? ReadPrice(JsonElement root, DateTime now)
    {
        if (!root.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array || prices.GetArrayLength() == 0)
            return null;
        var p = prices[0];
        static decimal? Best(JsonElement side) =>
            side.ValueKind == JsonValueKind.Array && side.GetArrayLength() > 0 && Dec(side[0], "price") is > 0 and var price ? price : null;
        var bid = p.TryGetProperty("bids", out var bids) ? Best(bids) : null;
        var ask = p.TryGetProperty("asks", out var asks) ? Best(asks) : null;
        if (bid is null || ask is null) return null;
        return new RoutePrice((bid.Value + ask.Value) / 2, ParseTime(Str(p, "time"), now));
    }
}
