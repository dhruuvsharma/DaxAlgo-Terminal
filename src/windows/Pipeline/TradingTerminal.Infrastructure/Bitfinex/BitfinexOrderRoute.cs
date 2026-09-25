using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;
using TradingTerminal.Infrastructure.Crypto;

namespace TradingTerminal.Infrastructure.Bitfinex;

/// <summary>
/// Bitfinex v2 exchange-wallet orders. Signed with HMAC-SHA384 over <c>/api/</c> + path + nonce + body.
/// Answers are positional arrays: an order is <c>[ID, GID, CID, SYMBOL, MTS_CREATE, MTS_UPDATE, AMOUNT,
/// AMOUNT_ORIG, TYPE, …, STATUS (13), …, PRICE (16), PRICE_AVG (17), …]</c> with the amounts signed (negative
/// sells) and the remaining amount in AMOUNT. A refusal is <c>["error", code, message]</c>, which Bitfinex
/// can send with a 500.
///
/// <para>Client ids are integers, so the engine's id is hashed into one and remembered. Prices have five
/// significant digits, so the tick is taken at the price when the book attaches. Fees come from the order's
/// trades. Bitfinex's paper trading is a separate sub-account on the same API with TEST pairs, so it is
/// reached with that sub-account's keys and symbols rather than a different host. Written 2026-09-25 from
/// Bitfinex's v2 reference; not yet run against a real account.</para>
/// </summary>
internal sealed class BitfinexOrderRoute : OrderRouteBase
{
    private const string Host = "https://api.bitfinex.com";
    private const string PublicHost = "https://api-pub.bitfinex.com";
    private long _lastNonce;

    public BitfinexOrderRoute(IBrokerCredentialSource credentials, ILogger<BitfinexOrderRoute> logger)
        : base(credentials, logger) { }

    internal BitfinexOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.Bitfinex;
    public override string DisplayName => "Bitfinex";
    public override string RouteId => "bitfinex";
    public override string? PaperEnvironmentName => null;

    /// <summary><c>BTCUSD</c> or <c>DOGE:USD</c> as Bitfinex's trading symbol, <c>tBTCUSD</c>.</summary>
    internal static string Trading(string symbol) => symbol.StartsWith('t') && symbol.Length > 1 && char.IsUpper(symbol[1]) ? symbol : "t" + symbol.ToUpperInvariant();

    /// <summary>(base, quote) of a trading symbol: six letters split in half, or split at the colon.</summary>
    internal static (string Base, string Quote) Assets(string symbol)
    {
        var pair = Trading(symbol)[1..];
        var colon = pair.IndexOf(':');
        return colon > 0 ? (pair[..colon], pair[(colon + 1)..]) : pair.Length == 6 ? (pair[..3], pair[3..]) : (pair, string.Empty);
    }

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

    private async Task<JsonElement> PrivateAsync(string path, JsonObject? body, CancellationToken ct)
    {
        var json = (body ?? []).ToJsonString();
        var (status, root, text) = await SendAsync(() =>
        {
            var nonce = NextNonce();
            var request = new HttpRequestMessage(HttpMethod.Post, $"{Host}/{path}")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("bfx-nonce", nonce);
            request.Headers.TryAddWithoutValidation("bfx-apikey", Credential.Key.Trim());
            request.Headers.TryAddWithoutValidation("bfx-signature", CryptoAuth.BitfinexSignature(path, nonce, json, Credential.Secret));
            return request;
        }, ct).ConfigureAwait(false);
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() >= 3 && root[0].ValueKind == JsonValueKind.String && root[0].GetString() == "error")
        {
            // 20060 is maintenance, 10114 a nonce collision: neither placed anything.
            throw new BrokerOrderRouteException($"Bitfinex: {root[2].GetString()} (code {root[1].GetRawText()})", isRejection: true);
        }
        if (status is < 200 or >= 300)
            throw Refused(status, null, text);
        return root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var user = await PrivateAsync("v2/auth/r/info/user", null, ct).ConfigureAwait(false);
        var (total, available) = await WalletAsync("USD", ct).ConfigureAwait(false);
        var id = user.ValueKind == JsonValueKind.Array && user.GetArrayLength() > 0 ? user[0].GetRawText() : string.Empty;
        return new RouteAccount(id.Length > 0 ? id : KeyAccount(), "USD", total, available);
    }

    /// <summary>(balance, available) in the exchange wallet: <c>[[type, currency, balance, unsettled, available], …]</c>.</summary>
    private async Task<(decimal Total, decimal Available)> WalletAsync(string currency, CancellationToken ct)
    {
        var wallets = await PrivateAsync("v2/auth/r/wallets", null, ct).ConfigureAwait(false);
        if (wallets.ValueKind == JsonValueKind.Array)
            foreach (var w in wallets.EnumerateArray())
                if (w.ValueKind == JsonValueKind.Array && w.GetArrayLength() >= 5 && w[0].GetString() == "exchange" &&
                    string.Equals(w[1].GetString(), currency, StringComparison.OrdinalIgnoreCase))
                    return (Dec(w[2]), w[4].ValueKind == JsonValueKind.Number ? Dec(w[4]) : Dec(w[2]));
        return (0m, 0m);
    }

    /// <summary>A price step for five significant digits at <paramref name="price"/>.</summary>
    internal static decimal FiveSignificant(decimal price)
    {
        var digits = (int)Math.Floor(Math.Log10((double)price));
        var exponent = digits - 4;
        var tick = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++)
            tick = exponent < 0 ? tick / 10m : tick * 10m;
        return tick;
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var price = await PriceAsync(environment, symbol, ct).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Bitfinex has no price for {symbol}, so its price step cannot be known.");
        var (status, root, body) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{PublicHost}/v2/conf/pub:info:pair"), ct).ConfigureAwait(false);
        EnsureSuccess(status, body, null);
        var (baseAsset, quote) = Assets(symbol);
        const decimal step = 0.00000001m;
        var minimum = MinimumOrder(root, Trading(symbol)[1..]);
        return new RouteInstrument(symbol, step, step, FiveSignificant(price.Price), Units(minimum, step, 1), long.MaxValue / 2,
            RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.StopLimit,
            RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, quote)
        {
            BaseAsset = baseAsset,
        };
    }

    /// <summary><c>[[["BTCUSD",[null,null,null,"0.00006","2000.0",…]],…]]</c> — the minimum order at index 3.</summary>
    internal static decimal MinimumOrder(JsonElement root, string pair)
    {
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Array)
            foreach (var entry in root[0].EnumerateArray())
                if (entry.ValueKind == JsonValueKind.Array && entry.GetArrayLength() == 2 && entry[0].GetString() == pair &&
                    entry[1].ValueKind == JsonValueKind.Array && entry[1].GetArrayLength() > 3)
                    return Dec(entry[1][3]);
        return 0m;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var cid = Token(request.ClientOrderId, id =>
            (BitConverter.ToInt64(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(id)), 0) & 0x1FFF_FFFF_FFFF).ToString(System.Globalization.CultureInfo.InvariantCulture));
        var signed = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;
        var body = new JsonObject
        {
            ["symbol"] = Trading(request.Symbol),
            ["amount"] = Num(signed),
            ["cid"] = long.Parse(cid, System.Globalization.CultureInfo.InvariantCulture),
        };
        switch (request.Type)
        {
            case RouteOrderType.Market:
                body["type"] = "EXCHANGE MARKET";
                break;
            case RouteOrderType.StopLimit:
                body["type"] = "EXCHANGE STOP LIMIT";
                body["price"] = Num(request.StopPrice!.Value);
                body["price_aux_limit"] = Num(request.LimitPrice!.Value);
                break;
            default:
                body["type"] = request.TimeInForce switch
                {
                    RouteTimeInForce.ImmediateOrCancel => "EXCHANGE IOC",
                    RouteTimeInForce.FillOrKill => "EXCHANGE FOK",
                    _ => "EXCHANGE LIMIT",
                };
                body["price"] = Num(request.LimitPrice!.Value);
                break;
        }
        var root = await PrivateAsync("v2/auth/w/order/submit", body, ct).ConfigureAwait(false);
        // [MTS, "on-req", null, null, [[ORDER…]], null, "SUCCESS", "Submitting 1 orders."]
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 6 && root[6].GetString() is { } outcome && outcome != "SUCCESS")
            throw RefusedInBody($"{outcome}: {root[7].GetString()}", root.GetRawText());
        var order = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 4 && root[4].ValueKind == JsonValueKind.Array && root[4].GetArrayLength() > 0
            ? ReadOrder(root[4][0], request.Symbol)
            : null;
        return order ?? throw new BrokerOrderRouteException("Bitfinex acknowledged the order without an order.", isRejection: false);
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await PrivateAsync("v2/auth/w/order/cancel", new JsonObject { ["id"] = long.Parse(order.OrderId, System.Globalization.CultureInfo.InvariantCulture) }, ct)
            .ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await PrivateAsync($"v2/auth/r/orders/{Trading(symbol)}", null, ct).ConfigureAwait(false);
        var orders = new List<RouteOrder>();
        if (root.ValueKind == JsonValueKind.Array)
            foreach (var item in root.EnumerateArray())
                if (ReadOrder(item, symbol) is { } order)
                    orders.Add(order.FilledQuantity > 0 ? await WithFeesAsync(order, ct).ConfigureAwait(false) : order);
        return orders;
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        var id = long.Parse(orderId, System.Globalization.CultureInfo.InvariantCulture);
        var history = await PrivateAsync($"v2/auth/r/orders/{Trading(symbol)}/hist", new JsonObject { ["id"] = new JsonArray(id) }, ct).ConfigureAwait(false);
        var order = history.ValueKind == JsonValueKind.Array && history.GetArrayLength() > 0 ? ReadOrder(history[0], symbol) : null;
        order ??= (await OrdersAsync(environment, symbol, ct).ConfigureAwait(false)).FirstOrDefault(o => o.OrderId == orderId);
        return order is { FilledQuantity: > 0 } ? await WithFeesAsync(order, ct).ConfigureAwait(false) : order;
    }

    /// <summary>The order's fees from its trades: <c>[[ID, PAIR, MTS, ORDER_ID, EXEC_AMOUNT, EXEC_PRICE, TYPE,
    /// ORDER_PRICE, MAKER, FEE (negative), FEE_CURRENCY, CID], …]</c>.</summary>
    private async Task<RouteOrder> WithFeesAsync(RouteOrder order, CancellationToken ct)
    {
        var trades = await PrivateAsync($"v2/auth/r/order/{Trading(order.Symbol)}:{order.OrderId}/trades", null, ct).ConfigureAwait(false);
        var fee = 0m;
        var currency = string.Empty;
        if (trades.ValueKind == JsonValueKind.Array)
            foreach (var t in trades.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() > 10)
                {
                    fee += Math.Abs(Dec(t[9]));
                    currency = t[10].GetString() ?? currency;
                }
        return order with { Fee = fee, FeeCurrency = currency };
    }

    internal RouteOrder? ReadOrder(JsonElement o, string symbol)
    {
        if (o.ValueKind != JsonValueKind.Array || o.GetArrayLength() < 18)
            return null;
        var remaining = Dec(o[6]);
        var original = Dec(o[7]);
        var type = o[8].GetString() ?? string.Empty;
        var status = o[13].GetString() ?? string.Empty;
        var filled = Math.Abs(original) - Math.Abs(remaining);
        return new RouteOrder(
            o[0].GetRawText(),
            EngineId(o[2].ValueKind == JsonValueKind.Number ? o[2].GetRawText() : string.Empty),
            symbol,
            original < 0 ? OrderSide.Sell : OrderSide.Buy,
            type.Contains("MARKET", StringComparison.Ordinal) ? RouteOrderType.Market
                : type.Contains("STOP LIMIT", StringComparison.Ordinal) ? RouteOrderType.StopLimit
                : RouteOrderType.Limit,
            type.Contains("FOK", StringComparison.Ordinal) ? RouteTimeInForce.FillOrKill
                : type.Contains("IOC", StringComparison.Ordinal) ? RouteTimeInForce.ImmediateOrCancel
                : RouteTimeInForce.GoodTillCancelled,
            Math.Abs(original),
            Dec(o[16]) is > 0 and var price ? price : null,
            null,
            status.StartsWith("ACTIVE", StringComparison.Ordinal) ? RouteOrderStatus.Working
                : status.StartsWith("PARTIALLY FILLED", StringComparison.Ordinal) ? RouteOrderStatus.PartiallyFilled
                : status.StartsWith("EXECUTED", StringComparison.Ordinal) ? RouteOrderStatus.Filled
                : status.StartsWith("CANCELED", StringComparison.Ordinal) ? RouteOrderStatus.Cancelled
                : status.Length > 0 ? RouteOrderStatus.Rejected
                : RouteOrderStatus.Unknown,
            Math.Max(0m, filled),
            Dec(o[17]) is > 0 and var average ? average : null,
            0m,
            string.Empty,
            status.StartsWith("ACTIVE", StringComparison.Ordinal) || status.StartsWith("EXECUTED", StringComparison.Ordinal) ? null : status,
            Utc((long)Dec(o[5])));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (total, _) = await WalletAsync(Assets(symbol).Base, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, total);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (status, root, _) = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{PublicHost}/v2/ticker/{Trading(symbol)}"), ct)
            .ConfigureAwait(false);
        return status == 200 && root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 6 && Dec(root[6]) is > 0 and var last
            ? new RoutePrice(last, Now.UtcDateTime)
            : null;
    }
}
