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

namespace TradingTerminal.Infrastructure.Schwab;

/// <summary>
/// Charles Schwab Trader API equity orders, over the session the login window signs in. Schwab has no paper
/// environment, so this route is live-only: its card is disabled until the owner option, stored credentials
/// and the typed confirmation for the exact account are all in place.
///
/// <para><b>Account.</b> Paths take the account's hash, not its number (<c>/accounts/accountNumbers</c> maps
/// one to the other); the confirmation names the number. The first account is traded.</para>
///
/// <para><b>Answers.</b> A placed order comes back as <c>201</c> with an empty body and the new order's URL in
/// <c>Location</c> — the id is read from there. An order's fills are its <c>EXECUTION</c> activities, and the
/// average price is weighted from their legs. Schwab has no client order id; a replace issues a new order id,
/// so replace is off. Orders carry <c>SELL_SHORT</c> and <c>BUY_TO_COVER</c> from the position held.</para>
///
/// <para>Written 2026-09-25 from Schwab's Trader API reference and the schwab-py client; not yet run against
/// a real account.</para>
/// </summary>
internal sealed class SchwabOrderRoute : KeptSessionOrderRoute
{
    private const string Trader = "https://api.schwabapi.com/trader/v1";
    private const string MarketData = "https://api.schwabapi.com/marketdata/v1";

    private readonly SchwabOptions _options;
    private (string Number, string Hash)? _account;

    public SchwabOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<SchwabOptions> options, ILogger<SchwabOrderRoute> logger)
        : base(BrokerKind.CharlesSchwab, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal SchwabOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, SchwabOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.CharlesSchwab, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.CharlesSchwab;
    public override string DisplayName => "Charles Schwab";
    public override string RouteId => "charles-schwab";
    public override string? PaperEnvironmentName => null;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        SchwabSignIn.TokenAsync(Http, _options.AuthBaseUrl, app,
            [new("grant_type", "refresh_token"), new("refresh_token", session.RefreshToken)], Now, session, ct);

    private async Task<JsonElement> RequestAsync(Func<KeptSession, HttpRequestMessage> build, CancellationToken ct)
    {
        var answer = await CallAsync(RouteEnvironment.Live, build, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return answer.Root;
    }

    private Task<JsonElement> GetJsonAsync(string url, CancellationToken ct) =>
        RequestAsync(_ => new HttpRequestMessage(HttpMethod.Get, url), ct);

    /// <summary><c>{"message","errors":["…"]}</c> — Schwab's error shape on the Trader API.</summary>
    internal static string? Words(JsonElement root)
    {
        var parts = new List<string>();
        if (Str(root, "message") is { Length: > 0 } message) parts.Add(message);
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
            foreach (var error in errors.EnumerateArray())
                parts.Add(error.ValueKind == JsonValueKind.String ? error.GetString() ?? string.Empty : Str(error, "detail") is { Length: > 0 } d ? d : error.GetRawText());
        var words = string.Join("; ", parts.Where(p => p.Length > 0));
        return words.Length > 0 ? words : null;
    }

    private async Task<(string Number, string Hash)> AccountIdsAsync(CancellationToken ct)
    {
        if (_account is { } known) return known;
        var root = await GetJsonAsync($"{Trader}/accounts/accountNumbers", ct).ConfigureAwait(false);
        var account = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? (Str(root[0], "accountNumber"), Str(root[0], "hashValue"))
            : (string.Empty, string.Empty);
        if (account.Item2.Length == 0)
            throw new BrokerOrderRouteException("Charles Schwab: the session reaches no account.", isRejection: true);
        _account = account;
        return account;
    }

    private void RequireLive(RouteEnvironment environment)
    {
        if (environment != RouteEnvironment.Live)
            throw new BrokerOrderRouteException("Charles Schwab has no paper environment.", isRejection: true);
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        RequireLive(environment);
        var (number, hash) = await AccountIdsAsync(ct).ConfigureAwait(false);
        var root = await GetJsonAsync($"{Trader}/accounts/{hash}", ct).ConfigureAwait(false);
        var (total, available) = ReadCash(root);
        return new RouteAccount(number, "USD", total, available);
    }

    /// <summary><c>{"securitiesAccount":{"currentBalances":{"cashBalance"|"totalCash","availableFunds"|"cashAvailableForTrading"}}}</c> —
    /// a margin account names the first pair, a cash account the second.</summary>
    internal static (decimal Total, decimal Available) ReadCash(JsonElement root)
    {
        var balances = root.TryGetProperty("securitiesAccount", out var a) && a.TryGetProperty("currentBalances", out var b) ? b : default;
        var total = Dec(balances, "cashBalance") is not 0 and var cash ? cash : Dec(balances, "totalCash");
        var available = Dec(balances, "availableFunds") is not 0 and var funds ? funds : Dec(balances, "cashAvailableForTrading");
        return (total, available);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        var quote = await QuoteAsync(symbol, ct).ConfigureAwait(false)
            ?? throw new BrokerOrderRouteException($"Charles Schwab does not know the symbol {symbol}.", isRejection: true);
        if (Str(quote.Asset, "assetMainType") is { Length: > 0 } type && type is not ("EQUITY" or "ETF" or "MUTUAL_FUND"))
            throw new BrokerOrderRouteException($"Charles Schwab: {symbol} is {type}; this route trades equities.", isRejection: true);
        return Equity(symbol.Trim().ToUpperInvariant(), Positive(quote.Quote, "lastPrice") ?? Positive(quote.Quote, "bidPrice"),
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill);
    }

    private async Task<(JsonElement Asset, JsonElement Quote)?> QuoteAsync(string symbol, CancellationToken ct)
    {
        var root = await GetJsonAsync($"{MarketData}/quotes?symbols={Uri.EscapeDataString(symbol.Trim())}&fields=quote", ct).ConfigureAwait(false);
        foreach (var property in root.EnumerateObject())
            if (string.Equals(property.Name, symbol.Trim(), StringComparison.OrdinalIgnoreCase) && property.Value.TryGetProperty("quote", out var quote))
                return (property.Value, quote);
        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, hash) = await AccountIdsAsync(ct).ConfigureAwait(false);
        var position = await PositionAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var body = SubmitBody(request, IntentFor(request.Side, request.Quantity, position.Quantity));
        var answer = await CallAsync(RouteEnvironment.Live, _ => Json(HttpMethod.Post, $"{Trader}/accounts/{hash}/orders", body), ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        var id = OrderIdFrom(answer.Location);
        if (id.Length == 0)
            throw new InvalidDataException("Charles Schwab accepted the order but named no order id in Location.");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    /// <summary>The last path segment of <c>…/accounts/{hash}/orders/{orderId}</c>.</summary>
    internal static string OrderIdFrom(Uri? location)
    {
        if (location is null) return string.Empty;
        var path = location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString;
        var last = path.TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
        return last.All(char.IsAsciiDigit) ? last : string.Empty;
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, EquityIntent intent)
    {
        var body = new JsonObject
        {
            ["orderType"] = request.Type switch
            {
                RouteOrderType.Limit => "LIMIT",
                RouteOrderType.Stop => "STOP",
                RouteOrderType.StopLimit => "STOP_LIMIT",
                _ => "MARKET",
            },
            ["session"] = "NORMAL",
            ["duration"] = request.TimeInForce switch
            {
                RouteTimeInForce.GoodTillCancelled => "GOOD_TILL_CANCEL",
                RouteTimeInForce.ImmediateOrCancel => "IMMEDIATE_OR_CANCEL",
                RouteTimeInForce.FillOrKill => "FILL_OR_KILL",
                _ => "DAY",
            },
            ["orderStrategyType"] = "SINGLE",
            ["orderLegCollection"] = new JsonArray(new JsonObject
            {
                ["instruction"] = intent switch
                {
                    EquityIntent.Buy => "BUY",
                    EquityIntent.BuyToCover => "BUY_TO_COVER",
                    EquityIntent.SellShort => "SELL_SHORT",
                    _ => "SELL",
                },
                ["quantity"] = request.Quantity,
                ["instrument"] = new JsonObject { ["symbol"] = request.Symbol.Trim().ToUpperInvariant(), ["assetType"] = "EQUITY" },
            }),
        };
        if (request.LimitPrice is { } limit) body["price"] = Num(limit);
        if (request.StopPrice is { } stop) body["stopPrice"] = Num(stop);
        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, hash) = await AccountIdsAsync(ct).ConfigureAwait(false);
        _ = await RequestAsync(_ => new HttpRequestMessage(HttpMethod.Delete, $"{Trader}/accounts/{hash}/orders/{Uri.EscapeDataString(order.OrderId)}"), ct)
            .ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, hash) = await AccountIdsAsync(ct).ConfigureAwait(false);
        // Seven days back reaches a GTC order still working; Schwab requires both ends of the window.
        var to = Now.UtcDateTime;
        var from = to.AddDays(-7);
        var root = await GetJsonAsync($"{Trader}/accounts/{hash}/orders?fromEnteredTime={Iso(from)}&toEnteredTime={Iso(to.AddMinutes(1))}&maxResults=500", ct)
            .ConfigureAwait(false);
        return root.ValueKind == JsonValueKind.Array
            ? [.. root.EnumerateArray().Where(o => string.Equals(LegSymbol(o), symbol.Trim(), StringComparison.OrdinalIgnoreCase)).Select(o => ReadOrder(o, symbol))]
            : [];
    }

    private static string Iso(DateTime utc) => Uri.EscapeDataString(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        RequireLive(environment);
        if (orderId.Length == 0) return null;
        var (_, hash) = await AccountIdsAsync(ct).ConfigureAwait(false);
        var answer = await CallAsync(RouteEnvironment.Live, _ => new HttpRequestMessage(HttpMethod.Get,
            $"{Trader}/accounts/{hash}/orders/{Uri.EscapeDataString(orderId)}"), ct).ConfigureAwait(false);
        if (answer.Status == 404) return null;
        if (answer.Status is < 200 or >= 300) throw Refused(answer.Status, Words(answer.Root), answer.Body);
        return ReadOrder(answer.Root, symbol);
    }

    private static string LegSymbol(JsonElement o) =>
        o.TryGetProperty("orderLegCollection", out var legs) && legs.ValueKind == JsonValueKind.Array && legs.GetArrayLength() > 0 &&
        legs[0].TryGetProperty("instrument", out var instrument)
            ? Str(instrument, "symbol")
            : string.Empty;

    /// <summary><c>{"orderId","status","orderType","duration","quantity","filledQuantity","price","stopPrice","enteredTime","closeTime",
    /// "statusDescription","orderLegCollection":[{"instruction"}],"orderActivityCollection":[{"activityType":"EXECUTION","executionLegs":[{"quantity","price"}]}]}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "filledQuantity");
        var (executed, notional) = (0m, 0m);
        if (o.TryGetProperty("orderActivityCollection", out var activities) && activities.ValueKind == JsonValueKind.Array)
            foreach (var activity in activities.EnumerateArray())
                if (Str(activity, "activityType") == "EXECUTION" && activity.TryGetProperty("executionLegs", out var legs) && legs.ValueKind == JsonValueKind.Array)
                    foreach (var leg in legs.EnumerateArray())
                    {
                        executed += Dec(leg, "quantity");
                        notional += Dec(leg, "quantity") * Dec(leg, "price");
                    }

        var instruction = o.TryGetProperty("orderLegCollection", out var orderLegs) && orderLegs.ValueKind == JsonValueKind.Array && orderLegs.GetArrayLength() > 0
            ? Str(orderLegs[0], "instruction")
            : string.Empty;
        var closed = Str(o, "closeTime");
        return new RouteOrder(
            Str(o, "orderId"),
            string.Empty,
            symbol,
            instruction is "BUY" or "BUY_TO_COVER" or "BUY_TO_OPEN" or "BUY_TO_CLOSE" ? OrderSide.Buy : OrderSide.Sell,
            Str(o, "orderType") switch
            {
                "LIMIT" => RouteOrderType.Limit,
                "STOP" => RouteOrderType.Stop,
                "STOP_LIMIT" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "duration") switch
            {
                "GOOD_TILL_CANCEL" => RouteTimeInForce.GoodTillCancelled,
                "IMMEDIATE_OR_CANCEL" => RouteTimeInForce.ImmediateOrCancel,
                "FILL_OR_KILL" => RouteTimeInForce.FillOrKill,
                _ => RouteTimeInForce.Day,
            },
            Dec(o, "quantity"),
            Positive(o, "price"),
            Positive(o, "stopPrice"),
            Status(Str(o, "status"), filled),
            filled,
            executed > 0 && executed == filled ? notional / executed : null,
            0m,
            string.Empty,
            Str(o, "status") == "REJECTED" && Str(o, "statusDescription") is { Length: > 0 } reason ? reason : null,
            ParseTime(closed.Length > 0 ? closed : Str(o, "enteredTime"), Now.UtcDateTime));
    }

    internal static RouteOrderStatus Status(string status, decimal filled) => status switch
    {
        "FILLED" => RouteOrderStatus.Filled,
        "CANCELED" or "REPLACED" => RouteOrderStatus.Cancelled,
        "EXPIRED" => RouteOrderStatus.Expired,
        "REJECTED" => RouteOrderStatus.Rejected,
        "PENDING_CANCEL" or "PENDING_RECALL" => RouteOrderStatus.PendingCancel,
        "NEW" or "PENDING_ACKNOWLEDGEMENT" or "AWAITING_MANUAL_REVIEW" => RouteOrderStatus.PendingNew,
        "WORKING" or "QUEUED" or "ACCEPTED" or "PENDING_ACTIVATION" or "PENDING_REPLACE" or "AWAITING_PARENT_ORDER" or "AWAITING_CONDITION"
            or "AWAITING_STOP_CONDITION" or "AWAITING_RELEASE_TIME" or "AWAITING_UR_OUT" =>
            filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
        _ => RouteOrderStatus.Unknown,
    };

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        var (_, hash) = await AccountIdsAsync(ct).ConfigureAwait(false);
        var root = await GetJsonAsync($"{Trader}/accounts/{hash}?fields=positions", ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(root, symbol));
    }

    /// <summary><c>{"securitiesAccount":{"positions":[{"longQuantity","shortQuantity","instrument":{"symbol"}}]}}</c>.</summary>
    internal static decimal ReadPosition(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("securitiesAccount", out var account) || !account.TryGetProperty("positions", out var positions) ||
            positions.ValueKind != JsonValueKind.Array)
            return 0m;
        var net = 0m;
        foreach (var p in positions.EnumerateArray())
            if (p.TryGetProperty("instrument", out var instrument) && string.Equals(Str(instrument, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                net += Dec(p, "longQuantity") - Math.Abs(Dec(p, "shortQuantity"));
        return net;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireLive(environment);
        if (await QuoteAsync(symbol, ct).ConfigureAwait(false) is not { } quote) return null;
        var q = quote.Quote;
        var price = Positive(q, "lastPrice") ?? (Positive(q, "bidPrice") is { } bid && Positive(q, "askPrice") is { } ask ? (bid + ask) / 2 : null);
        var time = (long)Dec(q, "tradeTime");
        return price is { } p ? new RoutePrice(p, Utc(time)) : null;
    }
}
