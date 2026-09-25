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

namespace TradingTerminal.Infrastructure.Saxo;

/// <summary>
/// Saxo Bank OpenAPI orders — FX, stocks, CFDs — over the session the login window signs in.
///
/// <para><b>Paper is Saxo's simulation environment</b> (gateway.saxobank.com/sim/openapi), which has its own
/// app registration and sign-in; the session reaches whichever environment <c>SaxoBank:RestBaseUrl</c> names,
/// and a card for the other one is refused with that said.</para>
///
/// <para><b>Symbols</b> are the market-data client's <c>ASSETTYPE:SYMBOL</c> (<c>FxSpot:EURUSD</c>), resolved to
/// Saxo's Uic the same way. The route trades the client's default account. Each order carries an
/// <c>x-request-id</c>, so a retried POST inside Saxo's duplicate window is not placed twice, and an
/// <c>ExternalReference</c> that maps back to the engine's id.</para>
///
/// <para><b>Order state.</b> <c>/port/v1/orders</c> lists working orders only, so a filled, cancelled or
/// expired order is read from the audit trail (<c>/cs/v1/audit/orderactivities</c>) — the last activity says
/// where it ended and at what average price. The audit trail can lag the fill by seconds; until it shows the
/// order the engine keeps asking. A replace here can change the order's type, and is off.</para>
///
/// <para>Value per point is one unit in the instrument's currency, times the contract size where Saxo gives one —
/// approximate where that is not the account currency. Written 2026-09-25 from Saxo's OpenAPI reference; not
/// yet run against a real account.</para>
/// </summary>
internal sealed class SaxoOrderRoute : KeptSessionOrderRoute
{
    private const string LiveHost = "https://gateway.saxobank.com/openapi";
    private const string SimHost = "https://gateway.saxobank.com/sim/openapi";

    private readonly SaxoOptions _options;
    private readonly ConcurrentDictionary<string, (long Uic, string AssetType)> _instruments = new(StringComparer.OrdinalIgnoreCase);
    private (string ClientKey, string AccountKey, string AccountId)? _account;

    public SaxoOrderRoute(IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<SaxoOptions> options, ILogger<SaxoOrderRoute> logger)
        : base(BrokerKind.SaxoBank, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal SaxoOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, SaxoOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.SaxoBank, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.SaxoBank;
    public override string DisplayName => "Saxo Bank";
    public override string RouteId => "saxo-bank";
    public override string? PaperEnvironmentName => "SIM";

    protected override RouteEnvironment? SessionEnvironment =>
        _options.RestBaseUrl.Contains("/sim/", StringComparison.OrdinalIgnoreCase) ? RouteEnvironment.Paper : RouteEnvironment.Live;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        SaxoSignIn.TokenAsync(Http, _options.AuthBaseUrl, app,
            [new("grant_type", "refresh_token"), new("refresh_token", session.RefreshToken), new("redirect_uri", session.Extra)],
            Now, session, ct);

    private static string Host(RouteEnvironment environment) => environment == RouteEnvironment.Live ? LiveHost : SimHost;

    private async Task<JsonElement> RequestAsync(RouteEnvironment environment, Func<KeptSession, HttpRequestMessage> build, CancellationToken ct)
    {
        var answer = await CallAsync(environment, build, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Words(answer.Root), answer.Body);
        if (answer.Root.TryGetProperty("ErrorInfo", out _))
            throw RefusedInBody(Words(answer.Root), answer.Body);
        return answer.Root;
    }

    private Task<JsonElement> GetJsonAsync(RouteEnvironment environment, string path, CancellationToken ct) =>
        RequestAsync(environment, _ => new HttpRequestMessage(HttpMethod.Get, Host(environment) + path), ct);

    /// <summary><c>{"ErrorInfo":{"ErrorCode","Message"}}</c>, or the same two fields at the top level.</summary>
    internal static string? Words(JsonElement root)
    {
        var error = root.TryGetProperty("ErrorInfo", out var info) ? info : root;
        var code = Str(error, "ErrorCode");
        var message = Str(error, "Message");
        var words = string.Join(": ", new[] { code, message }.Where(s => s.Length > 0));
        return words.Length > 0 ? words : null;
    }

    private async Task<(string ClientKey, string AccountKey, string AccountId)> AccountKeysAsync(RouteEnvironment environment, CancellationToken ct)
    {
        if (_account is { } known) return known;
        var client = await GetJsonAsync(environment, "/port/v1/clients/me", ct).ConfigureAwait(false);
        var keys = (Str(client, "ClientKey"), Str(client, "DefaultAccountKey"), Str(client, "DefaultAccountId"));
        if (keys.Item1.Length == 0 || keys.Item2.Length == 0)
            throw new BrokerOrderRouteException("Saxo Bank: the session names no client or default account.", isRejection: true);
        _account = keys;
        return keys;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var (clientKey, accountKey, accountId) = await AccountKeysAsync(environment, ct).ConfigureAwait(false);
        var balance = await GetJsonAsync(environment,
            $"/port/v1/balances?ClientKey={Uri.EscapeDataString(clientKey)}&AccountKey={Uri.EscapeDataString(accountKey)}", ct).ConfigureAwait(false);
        return new RouteAccount(accountId, Str(balance, "Currency"), Dec(balance, "CashBalance"), Dec(balance, "CashAvailableForTrading"));
    }

    private async Task<(long Uic, string AssetType)> ResolveAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        if (_instruments.TryGetValue(symbol, out var known)) return known;
        var (asset, name) = RealSaxoClient.Split(symbol.Trim());
        if (long.TryParse(name, NumberStyles.Integer, CultureInfo.InvariantCulture, out var uic)) return _instruments[symbol] = (uic, asset);
        var keyword = name.Contains(':') ? name[..name.IndexOf(':')] : name;
        var root = await GetJsonAsync(environment,
            $"/ref/v1/instruments?Keywords={Uri.EscapeDataString(keyword)}&AssetTypes={Uri.EscapeDataString(asset)}", ct).ConfigureAwait(false);
        uic = RealSaxoClient.PickUic(root, name)
            ?? throw new BrokerOrderRouteException($"Saxo Bank found no {asset} instrument {name}.", isRejection: true);
        return _instruments[symbol] = (uic, asset);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (uic, asset) = await ResolveAsync(environment, symbol, ct).ConfigureAwait(false);
        var details = await GetJsonAsync(environment, $"/ref/v1/instruments/details/{uic}/{Uri.EscapeDataString(asset)}?FieldGroups=OrderSetting", ct)
            .ConfigureAwait(false);
        return ReadInstrument(details, symbol.Trim())
            ?? throw new BrokerOrderRouteException($"Saxo Bank: {symbol} is not tradable on this account.", isRejection: true);
    }

    /// <summary><c>{"CurrencyCode","TickSize","TickSizeScheme":{"DefaultTickSize"},"Format":{"OrderDecimals"},"AmountDecimals",
    /// "MinimumTradeSize","LotSize","ContractSize","IsTradable","SupportedOrderTypes":[…]}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement d, string symbol)
    {
        if (d.TryGetProperty("IsTradable", out var tradable) && tradable.ValueKind == JsonValueKind.False) return null;
        var unit = Dec(d, "LotSize") is > 0 and var lot ? lot : Step((int)Dec(d, "AmountDecimals"));
        var tick = Dec(d, "TickSize") is > 0 and var t ? t
            : d.TryGetProperty("TickSizeScheme", out var scheme) && Dec(scheme, "DefaultTickSize") is > 0 and var st ? st
            : d.TryGetProperty("Format", out var format) ? Step((int)Dec(format, "OrderDecimals") is > 0 and var od ? od : (int)Dec(format, "Decimals"))
            : 0m;
        if (tick <= 0) return null;
        var types = RouteOrderTypes.None;
        if (d.TryGetProperty("SupportedOrderTypes", out var supported) && supported.ValueKind == JsonValueKind.Array)
            foreach (var type in supported.EnumerateArray().Select(e => e.GetString()))
                types |= type switch
                {
                    "Market" => RouteOrderTypes.Market,
                    "Limit" => RouteOrderTypes.Limit,
                    "Stop" => RouteOrderTypes.Stop,
                    "StopLimit" => RouteOrderTypes.StopLimit,
                    _ => RouteOrderTypes.None,
                };
        if (types == RouteOrderTypes.None) types = RouteOrderTypes.Market | RouteOrderTypes.Limit;
        var contract = Dec(d, "ContractSize") is > 0 and var size ? size : 1m;
        return new RouteInstrument(
            symbol, unit, unit * contract, tick, Units(Dec(d, "MinimumTradeSize"), unit, 1), 100_000_000,
            types, RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, Str(d, "CurrencyCode") is { Length: > 0 } currency ? currency : "USD");
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var (_, accountKey, _) = await AccountKeysAsync(environment, ct).ConfigureAwait(false);
        var (uic, asset) = await ResolveAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var reference = Token(request.ClientOrderId, id => Tag(id, 50));
        var body = SubmitBody(request, accountKey, uic, asset, reference);
        var root = await RequestAsync(environment, _ =>
        {
            var message = Json(HttpMethod.Post, $"{Host(environment)}/trade/v2/orders", body);
            message.Headers.TryAddWithoutValidation("x-request-id", reference);
            return message;
        }, ct).ConfigureAwait(false);
        var id = Str(root, "OrderId");
        if (id.Length == 0)
            throw new InvalidDataException($"Saxo Bank answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return new RouteOrder(id, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string accountKey, long uic, string assetType, string reference)
    {
        var body = new JsonObject
        {
            ["AccountKey"] = accountKey,
            ["Uic"] = uic,
            ["AssetType"] = assetType,
            ["BuySell"] = request.Side == OrderSide.Buy ? "Buy" : "Sell",
            ["Amount"] = request.Quantity,
            ["OrderType"] = request.Type switch
            {
                RouteOrderType.Limit => "Limit",
                RouteOrderType.Stop => "Stop",
                RouteOrderType.StopLimit => "StopLimit",
                _ => "Market",
            },
            ["OrderDuration"] = new JsonObject
            {
                ["DurationType"] = request.TimeInForce switch
                {
                    RouteTimeInForce.GoodTillCancelled => "GoodTillCancel",
                    RouteTimeInForce.ImmediateOrCancel => "ImmediateOrCancel",
                    RouteTimeInForce.FillOrKill => "FillOrKill",
                    _ => "DayOrder",
                },
            },
            ["ManualOrder"] = false,
            ["ExternalReference"] = reference,
        };
        // A stop-limit's trigger is OrderPrice and its limit StopLimitPrice; a plain stop or limit uses OrderPrice.
        switch (request.Type)
        {
            case RouteOrderType.Limit:
                body["OrderPrice"] = request.LimitPrice!.Value;
                break;
            case RouteOrderType.Stop:
                body["OrderPrice"] = request.StopPrice!.Value;
                break;
            case RouteOrderType.StopLimit:
                body["OrderPrice"] = request.StopPrice!.Value;
                body["StopLimitPrice"] = request.LimitPrice!.Value;
                break;
        }

        return body;
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct)
    {
        var (_, accountKey, _) = await AccountKeysAsync(environment, ct).ConfigureAwait(false);
        _ = await RequestAsync(environment, _ => new HttpRequestMessage(HttpMethod.Delete,
            $"{Host(environment)}/trade/v2/orders/{Uri.EscapeDataString(order.OrderId)}?AccountKey={Uri.EscapeDataString(accountKey)}"), ct).ConfigureAwait(false);
    }

    /// <summary>Working orders with nothing filled. One that has ended — or filled in part, whose average price only
    /// the audit trail knows — is left out, so the engine reads it through <see cref="OrderAsync"/>.</summary>
    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (uic, asset) = await ResolveAsync(environment, symbol, ct).ConfigureAwait(false);
        var root = await GetJsonAsync(environment, "/port/v1/orders/me?$top=500", ct).ConfigureAwait(false);
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array) return [];
        return [.. data.EnumerateArray()
            .Where(o => (long)Dec(o, "Uic") == uic && string.Equals(Str(o, "AssetType"), asset, StringComparison.OrdinalIgnoreCase) &&
                        Dec(o, "FilledAmount") == 0)
            .Select(o => ReadWorking(o, symbol))];
    }

    /// <summary><c>{"OrderId","BuySell","Amount","FilledAmount","Price","StopLimitPrice","OpenOrderType","Duration":{"DurationType"},
    /// "Status","ExternalReference","OrderTime"}</c> — a working order with nothing filled.</summary>
    internal RouteOrder ReadWorking(JsonElement o, string symbol)
    {
        var type = OrderType(Str(o, "OpenOrderType"));
        var duration = o.TryGetProperty("Duration", out var d) ? Str(d, "DurationType") : string.Empty;
        return new RouteOrder(
            Str(o, "OrderId"),
            EngineId(Str(o, "ExternalReference")),
            symbol,
            Str(o, "BuySell") == "Sell" ? OrderSide.Sell : OrderSide.Buy,
            type,
            Duration(duration),
            Dec(o, "Amount"),
            type == RouteOrderType.StopLimit ? Positive(o, "StopLimitPrice") : type == RouteOrderType.Limit ? Positive(o, "Price") : null,
            type is RouteOrderType.Stop or RouteOrderType.StopLimit ? Positive(o, "Price") : null,
            RouteOrderStatus.Working,
            0m,
            null,
            0m,
            string.Empty,
            null,
            ParseTime(Str(o, "OrderTime"), Now.UtcDateTime));
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var root = await GetJsonAsync(environment, $"/cs/v1/audit/orderactivities?OrderId={Uri.EscapeDataString(orderId)}&FieldGroups=DisplayAndFormat", ct)
            .ConfigureAwait(false);
        return ReadActivities(root, symbol, orderId);
    }

    /// <summary>
    /// <c>{"Data":[{"OrderId","Status":"Placed|Working|Fill|FinalFill|Cancelled|Expired|Rejected|…","Amount","FilledAmount",
    /// "FillAmount","AveragePrice","ExecutionPrice","BuySell","OrderType","Duration":{"DurationType"},"ActivityTime","ExternalReference","SubStatus"}]}</c>
    /// — the latest activity says where the order stands; the cumulative fill and average come from it, or are summed
    /// from the fill activities when Saxo gives only per-fill amounts. Null when the trail does not show the order yet.
    /// </summary>
    internal RouteOrder? ReadActivities(JsonElement root, string symbol, string orderId)
    {
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
        var rows = data.EnumerateArray().Where(a => Str(a, "OrderId") == orderId)
            .OrderBy(a => ParseTime(Str(a, "ActivityTime"), DateTime.MinValue)).ToArray();
        if (rows.Length == 0) return null;
        var last = rows[^1];
        var (summedQuantity, summedNotional) = (0m, 0m);
        foreach (var fill in rows.Where(a => Str(a, "Status") is "Fill" or "FinalFill"))
        {
            var amount = Dec(fill, "FillAmount");
            summedQuantity += amount;
            summedNotional += amount * Dec(fill, "ExecutionPrice");
        }

        var filled = Dec(last, "FilledAmount") is > 0 and var cumulative ? cumulative : summedQuantity;
        var average = Positive(last, "AveragePrice") ?? (summedQuantity > 0 && summedQuantity == filled ? summedNotional / summedQuantity : null);
        var status = Str(last, "Status") switch
        {
            "FinalFill" => RouteOrderStatus.Filled,
            "Fill" => RouteOrderStatus.PartiallyFilled,
            "Cancelled" => RouteOrderStatus.Cancelled,
            "Expired" or "DoneForDay" => RouteOrderStatus.Expired,
            "Rejected" or "Failed" => RouteOrderStatus.Rejected,
            "Placed" or "Working" or "Changed" or "Activated" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            _ => RouteOrderStatus.Unknown,
        };
        var type = OrderType(Str(last, "OrderType"));
        var duration = last.TryGetProperty("Duration", out var d) ? Str(d, "DurationType") : string.Empty;
        return new RouteOrder(
            orderId,
            EngineId(Str(last, "ExternalReference")),
            symbol,
            Str(last, "BuySell") == "Sell" ? OrderSide.Sell : OrderSide.Buy,
            type,
            Duration(duration),
            Dec(last, "Amount"),
            null,
            null,
            status,
            filled,
            average,
            0m,
            string.Empty,
            status == RouteOrderStatus.Rejected && Str(last, "SubStatus") is { Length: > 0 } reason ? reason : null,
            ParseTime(Str(last, "ActivityTime"), Now.UtcDateTime));
    }

    private static RouteOrderType OrderType(string type) => type switch
    {
        "Limit" => RouteOrderType.Limit,
        "Stop" or "StopIfTraded" => RouteOrderType.Stop,
        "StopLimit" => RouteOrderType.StopLimit,
        _ => RouteOrderType.Market,
    };

    private static RouteTimeInForce Duration(string duration) => duration switch
    {
        "GoodTillCancel" => RouteTimeInForce.GoodTillCancelled,
        "ImmediateOrCancel" => RouteTimeInForce.ImmediateOrCancel,
        "FillOrKill" => RouteTimeInForce.FillOrKill,
        _ => RouteTimeInForce.Day,
    };

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (uic, asset) = await ResolveAsync(environment, symbol, ct).ConfigureAwait(false);
        var (_, _, accountId) = await AccountKeysAsync(environment, ct).ConfigureAwait(false);
        var root = await GetJsonAsync(environment, "/port/v1/netpositions/me?FieldGroups=NetPositionBase", ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(root, uic, asset, accountId));
    }

    /// <summary><c>{"Data":[{"NetPositionBase":{"AccountId","Amount","AssetType","Uic"}}]}</c> — the amount is signed.</summary>
    internal static decimal ReadPosition(JsonElement root, long uic, string assetType, string accountId)
    {
        if (!root.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array) return 0m;
        var net = 0m;
        foreach (var row in data.EnumerateArray())
        {
            var b = row.TryGetProperty("NetPositionBase", out var nb) ? nb : row;
            if ((long)Dec(b, "Uic") == uic && string.Equals(Str(b, "AssetType"), assetType, StringComparison.OrdinalIgnoreCase) &&
                (Str(b, "AccountId").Length == 0 || Str(b, "AccountId") == accountId))
                net += Dec(b, "Amount");
        }

        return net;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (uic, asset) = await ResolveAsync(environment, symbol, ct).ConfigureAwait(false);
        var root = await GetJsonAsync(environment, $"/trade/v1/infoprices?Uic={uic}&AssetType={Uri.EscapeDataString(asset)}&FieldGroups=Quote", ct)
            .ConfigureAwait(false);
        var quote = root.TryGetProperty("Quote", out var q) ? q : default;
        var price = Positive(quote, "Mid") ?? (Positive(quote, "Bid") is { } bid && Positive(quote, "Ask") is { } ask ? (bid + ask) / 2 : null);
        return price is { } p ? new RoutePrice(p, ParseTime(Str(root, "LastUpdated"), Now.UtcDateTime)) : null;
    }
}
