using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Kraken;

/// <summary>
/// Kraken spot orders over the REST API: form posts signed with HMAC-SHA512 of the path and SHA-256 of
/// nonce + form, under the base64 secret.
///
/// <para><b>Nonces must rise on every call on a key</b> — two requests in one millisecond fail with "invalid
/// nonce" for a reason that has nothing to do with the order — so this route hands out nonces from a
/// counter that never repeats. Kraken answers errors with HTTP 200 and an <c>error</c> array; a service or
/// internal error there is an unknown outcome, the rest are refusals.</para>
///
/// <para>Symbols arrive as the market-data client writes them (<c>BTC/USD</c>) and are sent as Kraken's
/// altname (<c>XBTUSD</c>); balances are keyed by Kraken's asset codes (<c>XXBT</c>, <c>ZUSD</c>). Kraken has
/// no spot test environment and its API names no account (the key stands in). Written 2026-09-25 from
/// Kraken's REST reference; not yet run against a real account.</para>
/// </summary>
internal sealed class KrakenOrderRoute : OrderRouteBase
{
    private const string Host = "https://api.kraken.com";
    private long _lastNonce;

    public KrakenOrderRoute(IBrokerCredentialSource credentials, ILogger<KrakenOrderRoute> logger)
        : base(credentials, logger) { }

    internal KrakenOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Kraken;
    public override string DisplayName => "Kraken";
    public override string RouteId => "kraken";
    public override string? PaperEnvironmentName => null;

    /// <summary>A nonce strictly above every earlier one: microseconds, bumped past the last on a collision.</summary>
    private string NextNonce()
    {
        var candidate = Now.ToUnixTimeMilliseconds() * 1000;
        while (true)
        {
            var last = Interlocked.Read(ref _lastNonce);
            var next = Math.Max(candidate, last + 1);
            if (Interlocked.CompareExchange(ref _lastNonce, next, last) == last)
                return next.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private async Task<JsonElement> PrivateAsync(string path, string form, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() =>
        {
            var nonce = NextNonce();
            var data = form.Length > 0 ? $"nonce={nonce}&{form}" : $"nonce={nonce}";
            var request = new HttpRequestMessage(HttpMethod.Post, Host + path)
            {
                Content = new StringContent(data, Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            request.Headers.TryAddWithoutValidation("API-Key", Credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("API-Sign", CryptoAuth.KrakenSignature(path, nonce, data, Credential.Secret.Trim()));
            return request;
        }, ct).ConfigureAwait(false);
        return Result(status, root, body);
    }

    private JsonElement Result(int status, JsonElement root, string body)
    {
        if (status is < 200 or >= 300)
            throw Refused(status, null, body);
        if (root.TryGetProperty("error", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
        {
            var words = string.Join("; ", errors.EnumerateArray().Select(e => e.GetString()));
            // A service-side failure says nothing about whether the order reached the book.
            var unknown = words.Contains("EService:", StringComparison.Ordinal) || words.Contains("EGeneral:Internal", StringComparison.Ordinal);
            throw new BrokerOrderRouteException($"Kraken: {words}", isRejection: !unknown);
        }
        return root.TryGetProperty("result", out var result) ? result : root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var balances = await PrivateAsync("/0/private/Balance", string.Empty, ct).ConfigureAwait(false);
        var usd = Dec(balances, "ZUSD");
        return new RouteAccount(KeyAccount(), "ZUSD", usd, usd);
    }

    /// <summary><c>BTC/USD</c> (or <c>XBTUSD</c>) as Kraken's altname: no slash, BTC as XBT, DOGE as XDG.</summary>
    internal static string Altname(string symbol)
    {
        var parts = symbol.ToUpperInvariant().Split('/');
        static string Asset(string a) => a switch { "BTC" => "XBT", "DOGE" => "XDG", _ => a };
        return parts.Length == 2 ? Asset(parts[0]) + Asset(parts[1]) : symbol.ToUpperInvariant();
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/0/public/AssetPairs?pair={Uri.EscapeDataString(Altname(symbol))}"), ct).ConfigureAwait(false);
        return ReadInstrument(Result(status, root, body), symbol) ?? throw new InvalidDataException($"Kraken returned no rules for {symbol}.");
    }

    /// <summary><c>{"XXBTZUSD":{"altname","base","quote","pair_decimals","lot_decimals","ordermin","tick_size"}}</c>.
    /// The instrument keeps the caller's symbol; orders are sent under the altname.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement result, string symbol)
    {
        if (result.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var pair in result.EnumerateObject())
        {
            var p = pair.Value;
            var step = Step((int)Dec(p, "lot_decimals"));
            var tick = Dec(p, "tick_size") is > 0 and var t ? t : Step((int)Dec(p, "pair_decimals"));
            return new RouteInstrument(
                symbol, step, step, tick, Units(Dec(p, "ordermin"), step, 1), long.MaxValue / 2,
                RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.StopLimit,
                RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel,
                SupportsReplace: false, Str(p, "quote"))
            {
                BaseAsset = Str(p, "base"),
            };
        }
        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var result = await PrivateAsync("/0/private/AddOrder", OrderForm(request), ct).ConfigureAwait(false);
        var txid = result.TryGetProperty("txid", out var ids) && ids.ValueKind == JsonValueKind.Array && ids.GetArrayLength() > 0
            ? ids[0].GetString() ?? string.Empty
            : string.Empty;
        if (txid.Length == 0)
            throw new BrokerOrderRouteException("Kraken acknowledged the order without a transaction id.", isRejection: false);
        return new RouteOrder(txid, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, request.StopPrice, RouteOrderStatus.PendingNew, 0, null, 0, string.Empty, null, Now.UtcDateTime);
    }

    internal string OrderForm(RouteOrderRequest request)
    {
        var userref = Token(request.ClientOrderId, id => (Math.Abs(BitConverter.ToInt32(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0)) % int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var form = $"pair={Uri.EscapeDataString(Altname(request.Symbol))}&type={(request.Side == OrderSide.Buy ? "buy" : "sell")}"
                   + $"&volume={Num(request.Quantity)}&userref={userref}";
        return request.Type switch
        {
            RouteOrderType.Market => form + "&ordertype=market",
            RouteOrderType.StopLimit => form + $"&ordertype=stop-loss-limit&price={Num(request.StopPrice!.Value)}&price2={Num(request.LimitPrice!.Value)}{TimeInForce(request.TimeInForce)}",
            _ => form + $"&ordertype=limit&price={Num(request.LimitPrice!.Value)}{TimeInForce(request.TimeInForce)}",
        };
    }

    private static string TimeInForce(RouteTimeInForce tif) => tif == RouteTimeInForce.ImmediateOrCancel ? "&timeinforce=IOC" : "&timeinforce=GTC";

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await PrivateAsync("/0/private/CancelOrder", $"txid={Uri.EscapeDataString(order.OrderId)}", ct).ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var result = await PrivateAsync("/0/private/OpenOrders", string.Empty, ct).ConfigureAwait(false);
        var altname = Altname(symbol);
        return result.TryGetProperty("open", out var open)
            ? [.. ReadOrders(open, symbol)
                .Where(order => string.Equals(order.Symbol, altname, StringComparison.OrdinalIgnoreCase))
                .Select(order => order with { Symbol = symbol, FeeCurrency = rules.Currency })]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var result = await PrivateAsync("/0/private/QueryOrders", $"txid={Uri.EscapeDataString(orderId)}", ct).ConfigureAwait(false);
        return ReadOrders(result, symbol).Select(order => order with { Symbol = symbol, FeeCurrency = rules.Currency }).FirstOrDefault();
    }

    /// <summary><c>{"OXXXX":{"status","vol","vol_exec","cost","fee","price","userref","descr":{"pair","type",
    /// "ordertype","price","price2"},"opentm","closetm","reason"}}</c>. The fee is in the quote currency.</summary>
    internal IReadOnlyList<RouteOrder> ReadOrders(JsonElement orders, string symbol)
    {
        var list = new List<RouteOrder>();
        if (orders.ValueKind != JsonValueKind.Object)
            return list;
        foreach (var entry in orders.EnumerateObject())
        {
            var o = entry.Value;
            var descr = o.TryGetProperty("descr", out var d) ? d : default;
            var volume = Dec(o, "vol");
            var executed = Dec(o, "vol_exec");
            var orderType = Str(descr, "ordertype");
            var status = Str(o, "status") switch
            {
                "pending" => RouteOrderStatus.PendingNew,
                "open" => executed > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "closed" => RouteOrderStatus.Filled,
                "canceled" => RouteOrderStatus.Cancelled,
                "expired" => RouteOrderStatus.Expired,
                _ => RouteOrderStatus.Unknown,
            };
            var closed = (long)(Dec(o, "closetm") * 1000);
            list.Add(new RouteOrder(
                entry.Name,
                EngineId(Str(o, "userref")),
                Str(descr, "pair"),
                Str(descr, "type") == "sell" ? OrderSide.Sell : OrderSide.Buy,
                orderType switch { "market" => RouteOrderType.Market, "stop-loss-limit" => RouteOrderType.StopLimit, _ => RouteOrderType.Limit },
                RouteTimeInForce.GoodTillCancelled,
                volume,
                orderType == "stop-loss-limit" ? Positive(descr, "price2") : Positive(descr, "price"),
                orderType == "stop-loss-limit" ? Positive(descr, "price") : null,
                status,
                executed,
                executed > 0 ? Positive(o, "price") ?? (Dec(o, "cost") is > 0 and var cost ? cost / executed : null) : null,
                Dec(o, "fee"),
                string.Empty,
                Str(o, "reason") is { Length: > 0 } reason ? reason : null,
                Utc(closed > 0 ? closed : (long)(Dec(o, "opentm") * 1000))));
        }
        return list;
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var balances = await PrivateAsync("/0/private/Balance", string.Empty, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, Dec(balances, rules.BaseAsset));
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get,
            $"{Host}/0/public/Ticker?pair={Uri.EscapeDataString(Altname(symbol))}"), ct).ConfigureAwait(false);
        if (status != 200)
            return null;
        foreach (var pair in Result(status, root, body).EnumerateObject())
            if (pair.Value.TryGetProperty("c", out var last) && last.ValueKind == JsonValueKind.Array && Dec(last[0]) is > 0 and var price)
                return new RoutePrice(price, Now.UtcDateTime);
        return null;
    }
}
