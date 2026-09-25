using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Tradier;

/// <summary>
/// Tradier equity orders over its brokerage REST API, with the bearer token the login window stores.
///
/// <para><b>Paper is Tradier's sandbox</b> (sandbox.tradier.com), which takes its own token: a production token
/// is refused there exactly like a bad one, and the other way round. There is one token slot, so the card's
/// environment has to match the token pasted.</para>
///
/// <para><b>Intent.</b> Tradier wants <c>sell_short</c> and <c>buy_to_cover</c> spelled out, so the route reads the
/// position before each order; an order that would cross zero is refused as two orders' worth. Every list
/// answer may be an object for one entry and an array for several (<c>orders.order</c>,
/// <c>positions.position</c>, <c>profile.account</c>), and <c>"null"</c> for none — all three are read.</para>
///
/// <para>No IOC or FOK for equities, and no in-place quantity change, so replace is off. Written 2026-09-25
/// from Tradier's brokerage API reference; not yet run against a real account.</para>
/// </summary>
internal sealed class TradierOrderRoute : OrderRouteBase
{
    private readonly Dictionary<RouteEnvironment, string> _accounts = [];

    public TradierOrderRoute(IBrokerCredentialSource credentials, ILogger<TradierOrderRoute> logger)
        : base(credentials, logger) { }

    internal TradierOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Tradier;
    public override string DisplayName => "Tradier";
    public override string RouteId => "tradier";
    public override string? PaperEnvironmentName => "SANDBOX";

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://api.tradier.com/v1" : "https://sandbox.tradier.com/v1";

    private async Task<JsonElement> CallAsync(
        RouteEnvironment environment, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(method, Host(environment) + path);
            if (form is not null)
                request.Content = new FormUrlEncodedContent(form);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Credential.Secret.Trim());
            request.Headers.Accept.ParseAdd("application/json");
            return request;
        }, ct).ConfigureAwait(false);
        if (status == 401)
            throw Refused(status, $"{Words(root) ?? "the token was refused"} — the sandbox and production issue separate tokens; "
                + $"this card trades {(environment == RouteEnvironment.Live ? "production" : "the sandbox")}", body);
        if (status is < 200 or >= 300 || root.TryGetProperty("errors", out _))
            throw Refused(status, Words(root), body, status is >= 500 or 408 ? false : status is >= 200 and < 300 ? true : null);
        return root;
    }

    /// <summary><c>{"errors":{"error":["…"]}}</c> or <c>{"fault":{"faultstring":"…"}}</c>.</summary>
    internal static string? Words(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors))
        {
            var messages = OneOrMany(errors, "error").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText()).ToArray();
            if (messages.Length > 0) return string.Join("; ", messages);
        }

        return root.TryGetProperty("fault", out var fault) && Str(fault, "faultstring") is { Length: > 0 } text ? text : null;
    }

    /// <summary>A Tradier list: an array, a single object, or <c>"null"</c>/absent for none.</summary>
    internal static IEnumerable<JsonElement> OneOrMany(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return [];
        return value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().ToArray(),
            JsonValueKind.Object or JsonValueKind.String => [value],
            _ => [],
        };
    }

    private async Task<string> AccountIdAsync(RouteEnvironment environment, CancellationToken ct)
    {
        lock (_accounts)
            if (_accounts.TryGetValue(environment, out var known)) return known;
        var profile = await CallAsync(environment, HttpMethod.Get, "/user/profile", null, ct).ConfigureAwait(false);
        var account = PickAccount(profile) ?? throw new BrokerOrderRouteException("Tradier: the token reaches no account.", isRejection: true);
        lock (_accounts) _accounts[environment] = account;
        return account;
    }

    /// <summary><c>{"profile":{"account":{…}|[…]}}</c> — the first active account, else the first.</summary>
    internal static string? PickAccount(JsonElement root)
    {
        var accounts = root.TryGetProperty("profile", out var profile) ? OneOrMany(profile, "account").ToArray() : [];
        var active = accounts.FirstOrDefault(a => Str(a, "status").Equals("active", StringComparison.OrdinalIgnoreCase));
        var pick = active.ValueKind == JsonValueKind.Object ? active : accounts.FirstOrDefault();
        return pick.ValueKind == JsonValueKind.Object && Str(pick, "account_number") is { Length: > 0 } number ? number : null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var balances = await CallAsync(environment, HttpMethod.Get, $"/accounts/{account}/balances", null, ct).ConfigureAwait(false);
        var (total, available) = ReadCash(balances);
        return new RouteAccount(account, "USD", total, available);
    }

    /// <summary><c>{"balances":{"total_cash","margin":{"stock_buying_power"}|"cash":{"cash_available"}}}</c>.</summary>
    internal static (decimal Total, decimal Available) ReadCash(JsonElement root)
    {
        if (!root.TryGetProperty("balances", out var b)) return (0m, 0m);
        var available = b.TryGetProperty("cash", out var cash) && cash.ValueKind == JsonValueKind.Object ? Dec(cash, "cash_available")
            : b.TryGetProperty("margin", out var margin) && margin.ValueKind == JsonValueKind.Object ? Dec(margin, "stock_buying_power")
            : Dec(b, "total_cash");
        return (Dec(b, "total_cash"), available);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var quote = await QuoteAsync(environment, symbol, ct).ConfigureAwait(false)
            ?? throw new BrokerOrderRouteException($"Tradier does not know the symbol {symbol}.", isRejection: true);
        return Equity(symbol.Trim().ToUpperInvariant(), Positive(quote, "last") ?? Positive(quote, "bid"),
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled);
    }

    private async Task<JsonElement?> QuoteAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await CallAsync(environment, HttpMethod.Get, $"/markets/quotes?symbols={Uri.EscapeDataString(symbol.Trim())}", null, ct).ConfigureAwait(false);
        var quote = root.TryGetProperty("quotes", out var quotes) ? OneOrMany(quotes, "quote").FirstOrDefault() : default;
        return quote.ValueKind == JsonValueKind.Object ? quote : null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var position = await PositionAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var form = SubmitForm(request, IntentFor(request.Side, request.Quantity, position.Quantity), Token(request.ClientOrderId, id => Tag(id, 255)));
        var root = await CallAsync(environment, HttpMethod.Post, $"/accounts/{account}/orders", form, ct).ConfigureAwait(false);
        var id = root.TryGetProperty("order", out var ack) ? Str(ack, "id") : string.Empty;
        if (id.Length == 0)
            throw new InvalidDataException($"Tradier accepted the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static List<KeyValuePair<string, string>> SubmitForm(RouteOrderRequest request, EquityIntent intent, string tag)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("class", "equity"),
            new("symbol", request.Symbol.Trim().ToUpperInvariant()),
            new("side", intent switch
            {
                EquityIntent.Buy => "buy",
                EquityIntent.BuyToCover => "buy_to_cover",
                EquityIntent.SellShort => "sell_short",
                _ => "sell",
            }),
            new("quantity", Num(request.Quantity)),
            new("type", request.Type switch
            {
                RouteOrderType.Limit => "limit",
                RouteOrderType.Stop => "stop",
                RouteOrderType.StopLimit => "stop_limit",
                _ => "market",
            }),
            new("duration", request.TimeInForce == RouteTimeInForce.GoodTillCancelled ? "gtc" : "day"),
            new("tag", tag),
        };
        if (request.LimitPrice is { } limit) form.Add(new("price", Num(limit)));
        if (request.StopPrice is { } stop) form.Add(new("stop", Num(stop)));
        return form;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        _ = await CallAsync(environment, HttpMethod.Delete, $"/accounts/{account}/orders/{Uri.EscapeDataString(order.OrderId)}", null, ct).ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await CallAsync(environment, HttpMethod.Get, $"/accounts/{account}/orders?includeTags=true", null, ct).ConfigureAwait(false);
        var orders = root.TryGetProperty("orders", out var list) ? OneOrMany(list, "order") : [];
        return [.. orders
            .Where(o => o.ValueKind == JsonValueKind.Object && string.Equals(Str(o, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(o => ReadOrder(o, symbol))];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        try
        {
            var root = await CallAsync(environment, HttpMethod.Get, $"/accounts/{account}/orders/{Uri.EscapeDataString(orderId)}?includeTags=true", null, ct)
                .ConfigureAwait(false);
            return root.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Object ? ReadOrder(order, symbol) : null;
        }
        catch (BrokerOrderRouteException exception) when (exception.IsRejection && exception.Message.Contains("HTTP 404", StringComparison.Ordinal))
        {
            return null;
        }
    }

    /// <summary><c>{"id","type","symbol","side","quantity","status","duration","price","stop_price","avg_fill_price",
    /// "exec_quantity","create_date","transaction_date","tag","reason_description"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var filled = Dec(o, "exec_quantity");
        var status = Str(o, "status") switch
        {
            "filled" => RouteOrderStatus.Filled,
            "partially_filled" => RouteOrderStatus.PartiallyFilled,
            "open" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            "canceled" => RouteOrderStatus.Cancelled,
            "expired" => RouteOrderStatus.Expired,
            "rejected" or "error" => RouteOrderStatus.Rejected,
            "pending" or "calculated" or "accepted_for_bidding" or "held" => RouteOrderStatus.PendingNew,
            _ => RouteOrderStatus.Unknown,
        };
        var side = Str(o, "side");
        return new RouteOrder(
            Str(o, "id"),
            EngineId(Str(o, "tag")),
            symbol,
            side is "buy" or "buy_to_cover" ? OrderSide.Buy : OrderSide.Sell,
            Str(o, "type") switch
            {
                "limit" => RouteOrderType.Limit,
                "stop" => RouteOrderType.Stop,
                "stop_limit" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(o, "duration") == "gtc" ? RouteTimeInForce.GoodTillCancelled : RouteTimeInForce.Day,
            Dec(o, "quantity"),
            Positive(o, "price"),
            Positive(o, "stop_price"),
            status,
            filled,
            Positive(o, "avg_fill_price"),
            0m,
            string.Empty,
            Str(o, "reason_description") is { Length: > 0 } reason ? reason : null,
            ParseTime(Str(o, "transaction_date") is { Length: > 0 } t ? t : Str(o, "create_date"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var account = await AccountIdAsync(environment, ct).ConfigureAwait(false);
        var root = await CallAsync(environment, HttpMethod.Get, $"/accounts/{account}/positions", null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(root, symbol));
    }

    /// <summary><c>{"positions":{"position":{…}|[…]}}</c> or <c>{"positions":"null"}</c>; quantity is signed.</summary>
    internal static decimal ReadPosition(JsonElement root, string symbol) =>
        root.TryGetProperty("positions", out var positions)
            ? OneOrMany(positions, "position")
                .Where(p => p.ValueKind == JsonValueKind.Object && string.Equals(Str(p, "symbol"), symbol.Trim(), StringComparison.OrdinalIgnoreCase))
                .Sum(p => Dec(p, "quantity"))
            : 0m;

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var quote = await QuoteAsync(environment, symbol, ct).ConfigureAwait(false);
        if (quote is not { } q) return null;
        var price = Positive(q, "last") ?? (Positive(q, "bid") is { } bid && Positive(q, "ask") is { } ask ? (bid + ask) / 2 : null);
        return price is { } p ? new RoutePrice(p, Now.UtcDateTime) : null;
    }
}
