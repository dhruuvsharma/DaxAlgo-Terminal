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

namespace TradingTerminal.Infrastructure.ETrade;

/// <summary>
/// E*TRADE equity orders, signed with OAuth 1.0a over the session the login window signs in.
///
/// <para><b>Live only.</b> E*TRADE's sandbox answers every call with the same canned data rather than an
/// account, which the engine's reconciliation would read as a broker that ignores its orders — so there is no
/// paper card, and a session signed into the sandbox is refused.</para>
///
/// <para><b>Preview, then place.</b> E*TRADE places only an order it has previewed: the route sends the
/// preview, takes its <c>previewId</c> and places the identical order with it. The client order id (twenty
/// letters and digits) rides both and maps back to the engine's. Orders carry <c>SELL_SHORT</c> and
/// <c>BUY_TO_COVER</c> from the position held. The route trades the first active brokerage account; the
/// confirmation names its account id, and paths use its <c>accountIdKey</c>. A replace here is a
/// cancel-and-new with a new order id, so replace is off.</para>
///
/// <para>Written 2026-09-25 from E*TRADE's v1 API reference; not yet run against a real account.</para>
/// </summary>
internal sealed class ETradeOrderRoute : KeptSessionOrderRoute
{
    private const string Host = "https://api.etrade.com";

    private readonly ETradeOptions _options;
    private (string Id, string Key)? _account;

    public ETradeOrderRoute(IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<ETradeOptions> options, ILogger<ETradeOrderRoute> logger)
        : base(BrokerKind.ETrade, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal ETradeOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, ETradeOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.ETrade, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.ETrade;
    public override string DisplayName => "E*TRADE";
    public override string RouteId => "etrade";
    public override string? PaperEnvironmentName => null;

    private bool SandboxSession => _options.RestBaseUrl.Contains("apisb.", StringComparison.OrdinalIgnoreCase);

    /// <summary>OAuth 1.0a signs every request with the consumer key and secret and the access token and secret.</summary>
    protected override void Authorize(HttpRequestMessage request, KeptSession session)
    {
        var app = Credential;
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1.Authorization(
            request.Method.Method, request.RequestUri!.AbsoluteUri, app.Key.Trim(), app.Secret.Trim(), session.AccessToken, session.Secret,
            Now.ToUnixTimeSeconds(), OAuth1.Nonce()));
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }

    /// <summary>Renewal keeps the same token and restarts the two-hour idle clock — exactly as the market-data
    /// client renews, since whichever asks first owns the shared keeper.</summary>
    protected override async Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct)
    {
        var url = $"{_options.RestBaseUrl}/oauth/renew_access_token";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", OAuth1.Authorization(
            "GET", url, app.Key.Trim(), app.Secret.Trim(), session.AccessToken, session.Secret, Now.ToUnixTimeSeconds(), OAuth1.Nonce()));
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"E*TRADE would not renew the session (HTTP {(int)response.StatusCode}) — sessions end at midnight US Eastern; sign in again. {SignInProof.Snippet(body)}");
        return session with { ExpiresUtc = Now + ETradeSignIn.Renewal };
    }

    private void RequireLive(RouteEnvironment environment)
    {
        if (environment != RouteEnvironment.Live)
            throw new BrokerOrderRouteException("E*TRADE has no paper environment this route can trade.", isRejection: true);
        if (SandboxSession)
            throw new BrokerOrderRouteException(
                "E*TRADE: the session is signed into the sandbox, which answers with canned data rather than an account. "
                + "Point ETrade:RestBaseUrl at https://api.etrade.com and sign in again to trade.", isRejection: true);
    }

    private async Task<(int Status, JsonElement Root)> RequestAsync(Func<KeptSession, HttpRequestMessage> build, CancellationToken ct, bool allowEmpty = false)
    {
        var answer = await CallAsync(RouteEnvironment.Live, build, ct).ConfigureAwait(false);
        if (allowEmpty && answer.Status == 204)
            return (204, answer.Root);
        if (answer.Status is < 200 or >= 300 || answer.Root.TryGetProperty("Error", out _))
            throw Refused(answer.Status, Words(answer.Root), answer.Body, answer.Status is >= 200 and < 300 ? true : null);
        return (answer.Status, answer.Root);
    }

    /// <summary><c>{"Error":{"code","message"}}</c>.</summary>
    internal static string? Words(JsonElement root) =>
        root.TryGetProperty("Error", out var error) && Str(error, "message") is { Length: > 0 } message
            ? Str(error, "code") is { Length: > 0 } code ? $"{message} ({code})" : message
            : null;

    private async Task<(string Id, string Key)> AccountOfAsync(CancellationToken ct)
    {
        if (_account is { } known) return known;
        var (_, root) = await RequestAsync(_ => new HttpRequestMessage(HttpMethod.Get, $"{Host}/v1/accounts/list.json"), ct).ConfigureAwait(false);
        var account = PickAccount(root) ?? throw new BrokerOrderRouteException("E*TRADE: the session reaches no active brokerage account.", isRejection: true);
        _account = account;
        return account;
    }

    /// <summary><c>{"AccountListResponse":{"Accounts":{"Account":[{"accountId","accountIdKey","institutionType","accountStatus"}]}}}</c>.</summary>
    internal static (string Id, string Key)? PickAccount(JsonElement root)
    {
        if (!root.TryGetProperty("AccountListResponse", out var response) || !response.TryGetProperty("Accounts", out var accounts) ||
            !accounts.TryGetProperty("Account", out var list))
            return null;
        var rows = list.ValueKind == JsonValueKind.Array ? list.EnumerateArray().ToArray() : [list];
        foreach (var a in rows)
            if (Str(a, "accountStatus") is "ACTIVE" or "" && Str(a, "institutionType") is "BROKERAGE" or "" && Str(a, "accountIdKey").Length > 0)
                return (Str(a, "accountId"), Str(a, "accountIdKey"));
        return null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        RequireLive(environment);
        var (id, key) = await AccountOfAsync(ct).ConfigureAwait(false);
        var (_, root) = await RequestAsync(_ => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/v1/accounts/{key}/balance.json?instType=BROKERAGE&realTimeNAV=true"), ct).ConfigureAwait(false);
        var computed = root.TryGetProperty("BalanceResponse", out var b) && b.TryGetProperty("Computed", out var c) ? c : default;
        var total = Dec(computed, "cashBalance") is not 0 and var cash ? cash : Dec(computed, "netCash");
        return new RouteAccount(id, "USD", total, Dec(computed, "cashAvailableForInvestment"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        var quote = await QuoteAsync(symbol, ct).ConfigureAwait(false)
            ?? throw new BrokerOrderRouteException($"E*TRADE does not know the symbol {symbol}.", isRejection: true);
        return Equity(symbol.Trim().ToUpperInvariant(), (decimal)quote.Price,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill);
    }

    private async Task<PolledQuote?> QuoteAsync(string symbol, CancellationToken ct)
    {
        var (_, root) = await RequestAsync(_ => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/v1/market/quote/{Uri.EscapeDataString(symbol.Trim())}.json?detailFlag=ALL"), ct).ConfigureAwait(false);
        return RealETradeClient.ParseQuote(root, 1) is { Price: > 0 } quote ? quote : null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, key) = await AccountOfAsync(ct).ConfigureAwait(false);
        var position = await PositionAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var clientId = Token(request.ClientOrderId, id => Alphanumeric(id, 20));
        var order = OrderBody(request, IntentFor(request.Side, request.Quantity, position.Quantity));

        var (_, preview) = await RequestAsync(_ => Json(HttpMethod.Post, $"{Host}/v1/accounts/{key}/orders/preview.json",
            new JsonObject { ["PreviewOrderRequest"] = Envelope(clientId, order, null) }), ct).ConfigureAwait(false);
        var previewId = PreviewId(preview)
            ?? throw new BrokerOrderRouteException($"E*TRADE: the preview returned no preview id — {SignInProof.Snippet(preview.GetRawText())}", isRejection: true);

        var (_, placed) = await RequestAsync(_ => Json(HttpMethod.Post, $"{Host}/v1/accounts/{key}/orders/place.json",
            new JsonObject { ["PlaceOrderRequest"] = Envelope(clientId, order, previewId) }), ct).ConfigureAwait(false);
        var orderId = PlacedId(placed);
        if (orderId.Length == 0)
            throw new InvalidDataException($"E*TRADE placed the order without an id: {SignInProof.Snippet(placed.GetRawText())}");
        return new RouteOrder(orderId, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject OrderBody(RouteOrderRequest request, EquityIntent intent)
    {
        var order = new JsonObject
        {
            ["allOrNone"] = false,
            ["priceType"] = request.Type switch
            {
                RouteOrderType.Limit => "LIMIT",
                RouteOrderType.Stop => "STOP",
                RouteOrderType.StopLimit => "STOP_LIMIT",
                _ => "MARKET",
            },
            ["orderTerm"] = request.TimeInForce switch
            {
                RouteTimeInForce.GoodTillCancelled => "GOOD_UNTIL_CANCEL",
                RouteTimeInForce.ImmediateOrCancel => "IMMEDIATE_OR_CANCEL",
                RouteTimeInForce.FillOrKill => "FILL_OR_KILL",
                _ => "GOOD_FOR_DAY",
            },
            ["marketSession"] = "REGULAR",
            ["Instrument"] = new JsonArray(new JsonObject
            {
                ["Product"] = new JsonObject { ["securityType"] = "EQ", ["symbol"] = request.Symbol.Trim().ToUpperInvariant() },
                ["orderAction"] = intent switch
                {
                    EquityIntent.Buy => "BUY",
                    EquityIntent.BuyToCover => "BUY_TO_COVER",
                    EquityIntent.SellShort => "SELL_SHORT",
                    _ => "SELL",
                },
                ["quantityType"] = "QUANTITY",
                ["quantity"] = request.Quantity,
            }),
        };
        if (request.LimitPrice is { } limit) order["limitPrice"] = limit;
        if (request.StopPrice is { } stop) order["stopPrice"] = stop;
        return order;
    }

    internal static JsonObject Envelope(string clientId, JsonObject order, long? previewId)
    {
        var envelope = new JsonObject { ["orderType"] = "EQ", ["clientOrderId"] = clientId };
        if (previewId is { } id) envelope["PreviewIds"] = new JsonArray(new JsonObject { ["previewId"] = id });
        envelope["Order"] = new JsonArray(order.DeepClone());
        return envelope;
    }

    /// <summary><c>{"PreviewOrderResponse":{"PreviewIds":[{"previewId"}]}}</c>.</summary>
    internal static long? PreviewId(JsonElement root) =>
        root.TryGetProperty("PreviewOrderResponse", out var r) && r.TryGetProperty("PreviewIds", out var ids) &&
        ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0 && (long)Dec(ids[0], "previewId") is > 0 and var id
            ? id
            : null;

    /// <summary><c>{"PlaceOrderResponse":{"OrderIds":[{"orderId"}]}}</c>.</summary>
    internal static string PlacedId(JsonElement root) =>
        root.TryGetProperty("PlaceOrderResponse", out var r) && r.TryGetProperty("OrderIds", out var ids) &&
        ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0
            ? Str(ids[0], "orderId")
            : string.Empty;

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, key) = await AccountOfAsync(ct).ConfigureAwait(false);
        _ = await RequestAsync(_ => Json(HttpMethod.Put, $"{Host}/v1/accounts/{key}/orders/cancel.json",
            new JsonObject { ["CancelOrderRequest"] = new JsonObject { ["orderId"] = long.Parse(order.OrderId, System.Globalization.CultureInfo.InvariantCulture) } }),
            ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, key) = await AccountOfAsync(ct).ConfigureAwait(false);
        var (status, root) = await RequestAsync(_ => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/v1/accounts/{key}/orders.json?symbol={Uri.EscapeDataString(symbol.Trim())}&count=100"), ct, allowEmpty: true).ConfigureAwait(false);
        return status == 204 ? [] : ReadOrders(root, symbol);
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
        (await OrdersAsync(environment, symbol, ct).ConfigureAwait(false)).FirstOrDefault(o => o.OrderId == orderId);

    /// <summary><c>{"OrdersResponse":{"Order":[{"orderId","OrderDetail":[{"placedTime","executedTime","status","orderTerm","priceType",
    /// "limitPrice","stopPrice","Instrument":[{"Product":{"symbol"},"orderAction","orderedQuantity","filledQuantity","averageExecutionPrice",
    /// "estimatedCommission"}]}]}]}}</c>.</summary>
    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("OrdersResponse", out var response) || !response.TryGetProperty("Order", out var orders) ||
            orders.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<RouteOrder>();
        foreach (var o in orders.EnumerateArray())
        {
            var detail = o.TryGetProperty("OrderDetail", out var details) && details.ValueKind == JsonValueKind.Array && details.GetArrayLength() > 0
                ? details[0]
                : default;
            var instrument = detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty("Instrument", out var instruments) &&
                             instruments.ValueKind == JsonValueKind.Array && instruments.GetArrayLength() > 0
                ? instruments[0]
                : default;
            var product = instrument.ValueKind == JsonValueKind.Object && instrument.TryGetProperty("Product", out var p) ? p : default;
            if (!string.Equals(Str(product, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            list.Add(ReadOrder(o, detail, instrument, symbol));
        }

        return list;
    }

    private RouteOrder ReadOrder(JsonElement o, JsonElement detail, JsonElement instrument, string symbol)
    {
        var filled = Dec(instrument, "filledQuantity");
        var status = Str(detail, "status") switch
        {
            "EXECUTED" or "DONE_TRADE_EXECUTED" => RouteOrderStatus.Filled,
            "PARTIAL" or "INDIVIDUAL_FILLS" => RouteOrderStatus.PartiallyFilled,
            "OPEN" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            "CANCELLED" => RouteOrderStatus.Cancelled,
            "CANCEL_REQUESTED" => RouteOrderStatus.PendingCancel,
            "EXPIRED" => RouteOrderStatus.Expired,
            "REJECTED" => RouteOrderStatus.Rejected,
            _ => RouteOrderStatus.Unknown,
        };
        var fee = Dec(instrument, "estimatedCommission");
        var executed = (long)Dec(detail, "executedTime");
        var placed = (long)Dec(detail, "placedTime");
        return new RouteOrder(
            Str(o, "orderId"),
            EngineId(Str(o, "clientOrderId")),
            symbol,
            Str(instrument, "orderAction") is "BUY" or "BUY_TO_COVER" ? OrderSide.Buy : OrderSide.Sell,
            Str(detail, "priceType") switch
            {
                "LIMIT" => RouteOrderType.Limit,
                "STOP" => RouteOrderType.Stop,
                "STOP_LIMIT" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(detail, "orderTerm") switch
            {
                "GOOD_UNTIL_CANCEL" => RouteTimeInForce.GoodTillCancelled,
                "IMMEDIATE_OR_CANCEL" => RouteTimeInForce.ImmediateOrCancel,
                "FILL_OR_KILL" => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            Dec(instrument, "orderedQuantity"),
            Positive(detail, "limitPrice"),
            Positive(detail, "stopPrice"),
            status,
            filled,
            Positive(instrument, "averageExecutionPrice"),
            fee,
            fee > 0 ? "USD" : string.Empty,
            null,
            Utc(executed > 0 ? executed : placed));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, key) = await AccountOfAsync(ct).ConfigureAwait(false);
        var (status, root) = await RequestAsync(_ => new HttpRequestMessage(HttpMethod.Get, $"{Host}/v1/accounts/{key}/portfolio.json"), ct, allowEmpty: true)
            .ConfigureAwait(false);
        return new RoutePosition(symbol, status == 204 ? 0m : ReadPosition(root, symbol));
    }

    /// <summary><c>{"PortfolioResponse":{"AccountPortfolio":[{"Position":[{"quantity","positionType":"LONG|SHORT","Product":{"symbol"}}]}]}}</c>.</summary>
    internal static decimal ReadPosition(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("PortfolioResponse", out var response) || !response.TryGetProperty("AccountPortfolio", out var portfolios) ||
            portfolios.ValueKind != JsonValueKind.Array)
            return 0m;
        var net = 0m;
        foreach (var portfolio in portfolios.EnumerateArray())
            if (portfolio.TryGetProperty("Position", out var positions) && positions.ValueKind == JsonValueKind.Array)
                foreach (var p in positions.EnumerateArray())
                {
                    var product = p.TryGetProperty("Product", out var pr) ? pr : default;
                    if (!string.Equals(Str(product, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    var quantity = Math.Abs(Dec(p, "quantity"));
                    net += Str(p, "positionType") == "SHORT" ? -quantity : quantity;
                }

        return net;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        return await QuoteAsync(symbol, ct).ConfigureAwait(false) is { } quote
            ? new RoutePrice(decimal.Round((decimal)quote.Price, 6), quote.TimeUtc)
            : null;
    }
}
