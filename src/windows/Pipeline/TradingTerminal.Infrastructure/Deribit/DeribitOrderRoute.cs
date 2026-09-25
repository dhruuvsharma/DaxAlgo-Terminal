using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.Deribit;

/// <summary>
/// Deribit orders over its JSON-RPC HTTP API: a client-credentials token from <c>public/auth</c>, renewed
/// before it expires, then <c>private/buy</c>, <c>private/sell</c>, <c>private/cancel</c>, <c>private/edit</c>.
///
/// <para><b>Amounts.</b> On an inverse ("reversed") future such as BTC-PERPETUAL the amount is US dollars in
/// multiples of the contract size, and profit settles in BTC; on a linear contract or spot it is the base
/// coin. One unit is one contract size either way. For an inverse contract the value of a point is not
/// constant — it is taken at the price when the book attaches, which is close enough for sizing and is
/// said so here rather than hidden.</para>
///
/// <para>Written 2026-09-25 from Deribit's v2 reference; paper is test.deribit.com (its own account and
/// keys). Not yet run against a real account.</para>
/// </summary>
internal sealed class DeribitOrderRoute : OrderRouteBase
{
    private readonly ConcurrentDictionary<RouteEnvironment, (string Token, DateTimeOffset Expires, string Key)> _tokens = new();

    public DeribitOrderRoute(IBrokerCredentialSource credentials, ILogger<DeribitOrderRoute> logger)
        : base(credentials, logger) { }

    internal DeribitOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Deribit;
    public override string DisplayName => "Deribit";
    public override string RouteId => "deribit";
    public override string? PaperEnvironmentName => "TESTNET";

    private static string Host(RouteEnvironment environment) =>
        environment == RouteEnvironment.Live ? "https://www.deribit.com" : "https://test.deribit.com";

    private async Task<string> TokenAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var key = Credential.Key.Trim();
        if (_tokens.TryGetValue(environment, out var cached) && cached.Key == key && cached.Expires > Now.AddMinutes(1))
            return cached.Token;
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/api/v2/public/auth?grant_type=client_credentials&client_id={Uri.EscapeDataString(key)}"
            + $"&client_secret={Uri.EscapeDataString(Credential.Secret.Trim())}"), ct).ConfigureAwait(false);
        var result = Result(status, root, body);
        var token = Str(result, "access_token");
        if (token.Length == 0)
            throw new BrokerOrderRouteException("Deribit issued no access token.", isRejection: true);
        _tokens[environment] = (token, Now.AddSeconds((double)Math.Max(60m, Dec(result, "expires_in"))), key);
        return token;
    }

    private JsonElement Result(int status, JsonElement root, string body)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            // 10028 is Deribit throttling and 13888/11044 are its engine timing out: the order may exist.
            var code = (long)Dec(error, "code");
            throw new BrokerOrderRouteException(
                $"Deribit: {Str(error, "message")} (code {code}){(error.TryGetProperty("data", out var data) ? " " + data.GetRawText() : string.Empty)}",
                isRejection: code is not (13888 or 11044 or 10028));
        }
        if (status is < 200 or >= 300)
            throw Refused(status, null, body);
        return root.TryGetProperty("result", out var result) ? result : root;
    }

    private async Task<JsonElement> PrivateAsync(RouteEnvironment environment, string method, string query, CancellationToken ct)
    {
        var token = await TokenAsync(environment, ct).ConfigureAwait(false);
        var (status, root, body) = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"{Host(environment)}/api/v2/private/{method}?{query}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }, ct).ConfigureAwait(false);
        return Result(status, root, body);
    }

    private async Task<JsonElement> PublicAsync(RouteEnvironment environment, string method, string query, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host(environment)}/api/v2/public/{method}?{query}"), ct).ConfigureAwait(false);
        return Result(status, root, body);
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var summary = await PrivateAsync(environment, "get_account_summary", "currency=BTC&extended=true", ct).ConfigureAwait(false);
        var id = Str(summary, "id");
        return new RouteAccount(id.Length > 0 ? id : KeyAccount(), "BTC", Dec(summary, "equity"), Dec(summary, "available_funds"));
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var instrument = await PublicAsync(environment, "get_instrument", $"instrument_name={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        var price = await PriceAsync(environment, symbol, ct).ConfigureAwait(false);
        return ReadInstrument(instrument, price?.Price) ?? throw new InvalidDataException($"Deribit returned no rules for {symbol}.");
    }

    /// <summary><c>{"instrument_name","tick_size","min_trade_amount","contract_size","base_currency",
    /// "quote_currency","instrument_type":"reversed"|"linear","kind"}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement i, decimal? price)
    {
        var unit = Dec(i, "contract_size") is > 0 and var size ? size : Dec(i, "min_trade_amount");
        var tick = Dec(i, "tick_size");
        if (unit <= 0 || tick <= 0)
            return null;
        var inverse = Str(i, "instrument_type") == "reversed";
        // An inverse contract's dollar amount is its notional; per point it is worth amount ÷ price.
        var valuePerPoint = inverse ? (price is > 0 ? decimal.Round(unit / price.Value, 12) : unit) : unit;
        return new RouteInstrument(
            Str(i, "instrument_name"), unit, valuePerPoint, tick,
            Units(Dec(i, "min_trade_amount"), unit, 1), long.MaxValue / 2,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.StopLimit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: true, Str(i, "quote_currency"));
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var result = await PrivateAsync(environment, request.Side == OrderSide.Buy ? "buy" : "sell", OrderQuery(request), ct).ConfigureAwait(false);
        return result.TryGetProperty("order", out var order)
            ? ReadOrder(order)
            : throw new BrokerOrderRouteException("Deribit acknowledged the order without an order.", isRejection: false);
    }

    internal string OrderQuery(RouteOrderRequest request)
    {
        var label = Token(request.ClientOrderId, id => id.Length <= 64 ? id : Hex(id, 32));
        var query = $"instrument_name={Uri.EscapeDataString(request.Symbol)}&amount={Num(request.Quantity)}&label={Uri.EscapeDataString(label)}";
        var tif = request.TimeInForce switch
        {
            RouteTimeInForce.ImmediateOrCancel => "immediate_or_cancel",
            RouteTimeInForce.FillOrKill => "fill_or_kill",
            _ => "good_til_cancelled",
        };
        return request.Type switch
        {
            RouteOrderType.Market => query + "&type=market",
            RouteOrderType.StopLimit => query + $"&type=stop_limit&price={Num(request.LimitPrice!.Value)}&trigger_price={Num(request.StopPrice!.Value)}&trigger=last_price&time_in_force={tif}",
            _ => query + $"&type=limit&price={Num(request.LimitPrice!.Value)}&time_in_force={tif}",
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await PrivateAsync(environment, "cancel", $"order_id={Uri.EscapeDataString(order.OrderId)}", ct).ConfigureAwait(false);

    public override async Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct)
    {
        var query = $"order_id={Uri.EscapeDataString(order.OrderId)}&amount={Num(quantity)}";
        if (limitPrice is { } price)
            query += $"&price={Num(price)}";
        if (stopPrice is { } trigger)
            query += $"&trigger_price={Num(trigger)}";
        var result = await PrivateAsync(environment, "edit", query, ct).ConfigureAwait(false);
        return result.TryGetProperty("order", out var edited) ? ReadOrder(edited) : order;
    }

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var result = await PrivateAsync(environment, "get_open_orders_by_instrument", $"instrument_name={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        return result.ValueKind == JsonValueKind.Array ? [.. result.EnumerateArray().Select(ReadOrder)] : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        try
        {
            return ReadOrder(await PrivateAsync(environment, "get_order_state", $"order_id={Uri.EscapeDataString(orderId)}", ct).ConfigureAwait(false));
        }
        catch (BrokerOrderRouteException exception) when (exception.Message.Contains("order_not_found", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
    }

    /// <summary><c>{"order_id","label","instrument_name","direction","order_type","time_in_force","amount","price",
    /// "trigger_price","order_state","filled_amount","average_price","commission","last_update_timestamp"}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o) => new(
        Str(o, "order_id"),
        EngineId(Str(o, "label")),
        Str(o, "instrument_name"),
        Str(o, "direction") == "sell" ? OrderSide.Sell : OrderSide.Buy,
        Str(o, "order_type") switch { "market" => RouteOrderType.Market, "stop_limit" => RouteOrderType.StopLimit, _ => RouteOrderType.Limit },
        Str(o, "time_in_force") switch
        {
            "immediate_or_cancel" => RouteTimeInForce.ImmediateOrCancel,
            "fill_or_kill" => RouteTimeInForce.FillOrKill,
            _ => RouteTimeInForce.GoodTillCancelled,
        },
        Dec(o, "amount"),
        Positive(o, "price"),
        Positive(o, "trigger_price"),
        Str(o, "order_state") switch
        {
            "open" or "untriggered" or "triggered" => Dec(o, "filled_amount") > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
            "filled" => RouteOrderStatus.Filled,
            "cancelled" => RouteOrderStatus.Cancelled,
            "rejected" => RouteOrderStatus.Rejected,
            _ => RouteOrderStatus.Unknown,
        },
        Dec(o, "filled_amount"),
        Positive(o, "average_price"),
        Math.Abs(Dec(o, "commission")),
        string.Empty,
        Str(o, "cancel_reason") is { Length: > 0 } reason && reason != "user_request" ? reason : null,
        Utc((long)Dec(o, "last_update_timestamp")));

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var position = await PrivateAsync(environment, "get_position", $"instrument_name={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        // Deribit's size is already signed: negative is short.
        return new RoutePosition(symbol, Dec(position, "size"));
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var ticker = await PublicAsync(environment, "ticker", $"instrument_name={Uri.EscapeDataString(symbol)}", ct).ConfigureAwait(false);
        return Positive(ticker, "last_price") is { } price ? new RoutePrice(price, Utc((long)Dec(ticker, "timestamp"))) : null;
    }
}
