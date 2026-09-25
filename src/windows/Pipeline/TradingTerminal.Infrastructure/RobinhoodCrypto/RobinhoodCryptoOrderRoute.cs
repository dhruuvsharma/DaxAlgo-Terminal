using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;
using TradingTerminal.Infrastructure.Brokers;

namespace TradingTerminal.Infrastructure.RobinhoodCrypto;

/// <summary>
/// Robinhood Crypto Trading API orders, each request signed with the user's Ed25519 key exactly as the
/// market-data client signs.
///
/// <para><b>Live only</b> — Robinhood's crypto API has no paper environment, so the card is disabled until the
/// owner option, the stored key pair and the typed confirmation for the exact account are all in place.</para>
///
/// <para>An order names its type in a config object of the same name (<c>limit_order_config</c>…) holding the
/// asset quantity and prices; the client order id must be a UUID, derived from the engine's. The position is
/// the asset's holding. Robinhood folds its spread into the price and reports no separate fee.</para>
///
/// <para>Written 2026-09-25 from Robinhood's Crypto Trading API documentation; not yet run against a real
/// account.</para>
/// </summary>
internal sealed class RobinhoodCryptoOrderRoute : OrderRouteBase
{
    private const string Host = "https://trading.robinhood.com";

    public RobinhoodCryptoOrderRoute(IBrokerCredentialSource credentials, ILogger<RobinhoodCryptoOrderRoute> logger)
        : base(credentials, logger) { }

    internal RobinhoodCryptoOrderRoute(IBrokerCredentialSource credentials, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(credentials, logger, time, handler) { }

    public override BrokerKind Broker => BrokerKind.RobinhoodCrypto;
    public override string DisplayName => "Robinhood Crypto";
    public override string RouteId => "robinhood-crypto";
    public override string? PaperEnvironmentName => null;

    private async Task<(int Status, JsonElement Root)> CallAsync(
        RouteEnvironment environment, HttpMethod method, string pathAndQuery, JsonNode? body, CancellationToken ct, bool allowNotFound = false)
    {
        if (environment != RouteEnvironment.Live)
            throw new BrokerOrderRouteException("Robinhood Crypto has no paper environment.", isRejection: true);
        var text = body?.ToJsonString() ?? string.Empty;
        var (status, root, raw) = await SendAsync(() => RobinhoodSigning.Signed(method, Host, pathAndQuery, text, Credential, Now), ct).ConfigureAwait(false);
        if (allowNotFound && status == 404) return (status, root);
        if (status is < 200 or >= 300)
            throw Refused(status, Words(root), raw);
        return (status, root);
    }

    /// <summary><c>{"type","errors":[{"detail","attr"}]}</c> or <c>{"detail"}</c>.</summary>
    internal static string? Words(JsonElement root)
    {
        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var parts = errors.EnumerateArray()
                .Select(e => Str(e, "attr") is { Length: > 0 } attr ? $"{attr}: {Str(e, "detail")}" : Str(e, "detail"))
                .Where(s => s.Length > 0).ToArray();
            if (parts.Length > 0) return string.Join("; ", parts);
        }

        return Str(root, "detail") is { Length: > 0 } detail ? detail : null;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get, "/api/v1/crypto/trading/accounts/", null, ct).ConfigureAwait(false);
        var power = Dec(root, "buying_power");
        return new RouteAccount(Str(root, "account_number"), Str(root, "buying_power_currency") is { Length: > 0 } c ? c : "USD", power, power);
    }

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get,
            $"/api/v1/crypto/trading/trading_pairs/?symbol={Uri.EscapeDataString(symbol.Trim())}", null, ct).ConfigureAwait(false);
        return ReadInstrument(root, symbol.Trim()) ?? throw new BrokerOrderRouteException($"Robinhood Crypto does not trade {symbol}.", isRejection: true);
    }

    /// <summary><c>{"results":[{"symbol","asset_code","quote_code","quote_increment","asset_increment","min_order_size","max_order_size","status"}]}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) return null;
        foreach (var p in results.EnumerateArray())
        {
            if (!string.Equals(Str(p, "symbol"), symbol, StringComparison.OrdinalIgnoreCase)) continue;
            if (Str(p, "status") is { Length: > 0 } status && status != "tradable") return null;
            var step = Dec(p, "asset_increment");
            var tick = Dec(p, "quote_increment");
            if (step <= 0 || tick <= 0) return null;
            return new RouteInstrument(
                Str(p, "symbol"), step, step, tick, Units(Dec(p, "min_order_size"), step, 1), Units(Dec(p, "max_order_size"), step, long.MaxValue / 2),
                RouteOrderTypes.Market | RouteOrderTypes.Limit | RouteOrderTypes.Stop | RouteOrderTypes.StopLimit,
                RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled,
                SupportsReplace: false, Str(p, "quote_code") is { Length: > 0 } quote ? quote : "USD")
            {
                BaseAsset = Str(p, "asset_code"),
            };
        }

        return null;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        var body = SubmitBody(request, Token(request.ClientOrderId, Uuid));
        var (_, root) = await CallAsync(environment, HttpMethod.Post, "/api/v1/crypto/trading/orders/", body, ct).ConfigureAwait(false);
        if (Str(root, "id").Length == 0)
            throw new InvalidDataException($"Robinhood Crypto answered the order without an id: {SignInProof.Snippet(root.GetRawText())}");
        return ReadOrder(root, request.Symbol) with { ClientOrderId = request.ClientOrderId };
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string clientId)
    {
        var tif = request.TimeInForce == RouteTimeInForce.Day ? "gfd" : "gtc";
        var (type, config) = request.Type switch
        {
            RouteOrderType.Limit => ("limit", new JsonObject
            {
                ["asset_quantity"] = Num(request.Quantity), ["limit_price"] = Num(request.LimitPrice!.Value), ["time_in_force"] = tif,
            }),
            RouteOrderType.Stop => ("stop_loss", new JsonObject
            {
                ["asset_quantity"] = Num(request.Quantity), ["stop_price"] = Num(request.StopPrice!.Value), ["time_in_force"] = tif,
            }),
            RouteOrderType.StopLimit => ("stop_limit", new JsonObject
            {
                ["asset_quantity"] = Num(request.Quantity), ["limit_price"] = Num(request.LimitPrice!.Value),
                ["stop_price"] = Num(request.StopPrice!.Value), ["time_in_force"] = tif,
            }),
            _ => ("market", new JsonObject { ["asset_quantity"] = Num(request.Quantity) }),
        };
        return new JsonObject
        {
            ["symbol"] = request.Symbol.Trim(),
            ["client_order_id"] = clientId,
            ["side"] = request.Side == OrderSide.Buy ? "buy" : "sell",
            ["type"] = type,
            [$"{type}_order_config"] = config,
        };
    }

    public override async Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        _ = await CallAsync(environment, HttpMethod.Post, $"/api/v1/crypto/trading/orders/{Uri.EscapeDataString(order.OrderId)}/cancel/", null, ct)
            .ConfigureAwait(false);

    public override async Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get,
            $"/api/v1/crypto/trading/orders/?symbol={Uri.EscapeDataString(symbol.Trim())}&state=open", null, ct).ConfigureAwait(false);
        return root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? [.. results.EnumerateArray().Select(o => ReadOrder(o, symbol))]
            : [];
    }

    public override async Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct)
    {
        if (orderId.Length == 0) return null;
        var (status, root) = await CallAsync(environment, HttpMethod.Get, $"/api/v1/crypto/trading/orders/{Uri.EscapeDataString(orderId)}/", null, ct,
            allowNotFound: true).ConfigureAwait(false);
        return status == 404 ? null : ReadOrder(root, symbol);
    }

    /// <summary><c>{"id","client_order_id","symbol","side","type","state":"open|partially_filled|filled|canceled|failed",
    /// "average_price","filled_asset_quantity","updated_at","{type}_order_config":{"asset_quantity","limit_price","stop_price","time_in_force"}}</c>.</summary>
    internal RouteOrder ReadOrder(JsonElement o, string symbol)
    {
        var type = Str(o, "type");
        var config = o.TryGetProperty($"{type}_order_config", out var c) ? c : default;
        var filled = Dec(o, "filled_asset_quantity");
        return new RouteOrder(
            Str(o, "id"),
            EngineId(Str(o, "client_order_id")),
            symbol,
            Str(o, "side") == "sell" ? OrderSide.Sell : OrderSide.Buy,
            type switch
            {
                "limit" => RouteOrderType.Limit,
                "stop_loss" => RouteOrderType.Stop,
                "stop_limit" => RouteOrderType.StopLimit,
                _ => RouteOrderType.Market,
            },
            Str(config, "time_in_force") == "gfd" ? RouteTimeInForce.Day : RouteTimeInForce.GoodTillCancelled,
            Dec(config, "asset_quantity"),
            Positive(config, "limit_price"),
            Positive(config, "stop_price"),
            Str(o, "state") switch
            {
                "filled" => RouteOrderStatus.Filled,
                "partially_filled" => RouteOrderStatus.PartiallyFilled,
                "open" => filled > 0 ? RouteOrderStatus.PartiallyFilled : RouteOrderStatus.Working,
                "canceled" => RouteOrderStatus.Cancelled,
                "failed" => RouteOrderStatus.Rejected,
                "pending" => RouteOrderStatus.PendingNew,
                _ => RouteOrderStatus.Unknown,
            },
            filled,
            Positive(o, "average_price"),
            0m,
            string.Empty,
            null,
            ParseTime(Str(o, "updated_at") is { Length: > 0 } updated ? updated : Str(o, "created_at"), Now.UtcDateTime));
    }

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await RulesAsync(environment, symbol, ct).ConfigureAwait(false);
        var (_, root) = await CallAsync(environment, HttpMethod.Get,
            $"/api/v1/crypto/trading/holdings/?asset_code={Uri.EscapeDataString(rules.BaseAsset)}", null, ct).ConfigureAwait(false);
        var holding = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? results.EnumerateArray().Where(h => string.Equals(Str(h, "asset_code"), rules.BaseAsset, StringComparison.OrdinalIgnoreCase)).Sum(h => Dec(h, "total_quantity"))
            : 0m;
        return new RoutePosition(symbol, holding);
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var (_, root) = await CallAsync(environment, HttpMethod.Get,
            $"/api/v1/crypto/marketdata/best_bid_ask/?symbol={Uri.EscapeDataString(symbol.Trim())}", null, ct).ConfigureAwait(false);
        var r = root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0 ? results[0] : default;
        return Positive(r, "price") is { } price ? new RoutePrice(price, ParseTime(Str(r, "timestamp"), Now.UtcDateTime)) : null;
    }
}
