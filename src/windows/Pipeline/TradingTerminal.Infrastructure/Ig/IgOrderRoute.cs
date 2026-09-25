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

namespace TradingTerminal.Infrastructure.Ig;

/// <summary>
/// IG deals — CFDs and spread bets on FX, indices and commodities — over the session the login window signs in.
///
/// <para><b>Paper is IG's demo</b>, a separate gateway with its own login. The login row's environment
/// (<c>demo</c>) decides which one the session reaches, and a card for the other is refused with that said.</para>
///
/// <para><b>What is placed.</b> Market deals, and limit deals that fill at the level or better at once or not
/// at all — IG's <c>/positions/otc</c> with <c>FILL_OR_KILL</c> or <c>EXECUTE_AND_ELIMINATE</c>. A resting
/// order is an IG <i>working order</i>, a different object with its own lifecycle, which this route does not
/// place: a Day or GTC limit is refused rather than quietly sent as immediate. A market deal is sent
/// fill-or-kill whatever its time in force, which is how a market deal behaves anyway.</para>
///
/// <para><b>Answers.</b> A deal POST returns only a deal reference; the outcome — accepted at which level
/// and size, or rejected and why — is read from <c>/confirms/{reference}</c>, which the route asks for at once
/// and the engine asks again if it was not ready. Deals are sent with <c>forceOpen: false</c>, so an opposite
/// deal nets against the open position on a netting account; a hedging account opens a second position, and
/// the position the route reports is the net of both.</para>
///
/// <para>Value per point is IG's <c>valueOfOnePip</c> per contract — one point of the quoted price, in the
/// instrument's currency. Written 2026-09-25 from IG's REST reference; not yet run against a real account.</para>
/// </summary>
internal sealed class IgOrderRoute : KeptSessionOrderRoute
{
    private readonly IgOptions _options;

    public IgOrderRoute(IBrokerCredentialSource credentials, IBrokerSessionStore store, IOptions<IgOptions> options, ILogger<IgOrderRoute> logger)
        : base(BrokerKind.IgGroup, credentials, store, logger)
    {
        _options = options.Value;
    }

    internal IgOrderRoute(
        IBrokerCredentialSource credentials, IBrokerSessionStore store, IgOptions options, ILogger logger, TimeProvider time, HttpMessageHandler handler)
        : base(BrokerKind.IgGroup, credentials, store, logger, time, handler)
    {
        _options = options;
    }

    public override BrokerKind Broker => BrokerKind.IgGroup;
    public override string DisplayName => "IG";
    public override string RouteId => "ig";
    public override string? PaperEnvironmentName => "DEMO";

    private bool IsDemo => Credential.Extra.Trim().Equals("demo", StringComparison.OrdinalIgnoreCase);

    protected override RouteEnvironment? SessionEnvironment => IsDemo ? RouteEnvironment.Paper : RouteEnvironment.Live;

    private string ConfiguredHost => IsDemo ? _options.DemoRestBaseUrl : _options.RestBaseUrl;

    protected override Task<KeptSession> RenewAsync(BrokerCredential app, KeptSession session, CancellationToken ct) =>
        IgSignIn.SessionAsync(Http, ConfiguredHost, app, Now, ct);

    protected override void Authorize(HttpRequestMessage request, KeptSession session)
    {
        request.Headers.TryAddWithoutValidation("X-IG-API-KEY", Credential.Key.Trim());
        request.Headers.TryAddWithoutValidation("CST", session.AccessToken);
        request.Headers.TryAddWithoutValidation("X-SECURITY-TOKEN", session.Secret);
        request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=UTF-8");
    }

    private string HostOf(KeptSession session) => session.Server.Length > 0 ? session.Server : ConfiguredHost;

    private Task<Answer> SendVersionedAsync(RouteEnvironment environment, HttpMethod method, string path, int version, JsonNode? body, CancellationToken ct) =>
        CallAsync(environment, session =>
        {
            var request = body is null ? new HttpRequestMessage(method, HostOf(session) + path) : Json(method, HostOf(session) + path, body);
            request.Headers.TryAddWithoutValidation("Version", version.ToString(CultureInfo.InvariantCulture));
            return request;
        }, ct);

    private async Task<JsonElement> RequestAsync(RouteEnvironment environment, HttpMethod method, string path, int version, JsonNode? body, CancellationToken ct)
    {
        var answer = await SendVersionedAsync(environment, method, path, version, body, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Str(answer.Root, "errorCode") is { Length: > 0 } code ? code : null, answer.Body);
        return answer.Root;
    }

    public override async Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct)
    {
        var session = await SessionAsync(ct).ConfigureAwait(false);
        var root = await RequestAsync(environment, HttpMethod.Get, "/accounts", 1, null, ct).ConfigureAwait(false);
        return ReadAccount(root, session.Extra)
            ?? throw new BrokerOrderRouteException($"IG: the session's account {session.Extra} is not among the accounts it lists.", isRejection: true);
    }

    /// <summary><c>{"accounts":[{"accountId","currency","preferred","balance":{"balance","available"}}]}</c> — the session's
    /// current account, else the preferred one.</summary>
    internal static RouteAccount? ReadAccount(JsonElement root, string current)
    {
        if (!root.TryGetProperty("accounts", out var accounts) || accounts.ValueKind != JsonValueKind.Array) return null;
        JsonElement pick = default;
        foreach (var account in accounts.EnumerateArray())
        {
            if (current.Length > 0 && Str(account, "accountId") == current) { pick = account; break; }
            if (current.Length == 0 && account.TryGetProperty("preferred", out var preferred) && preferred.ValueKind == JsonValueKind.True) pick = account;
        }

        if (pick.ValueKind != JsonValueKind.Object) return null;
        var balance = pick.TryGetProperty("balance", out var b) ? b : default;
        return new RouteAccount(Str(pick, "accountId"), Str(pick, "currency"), Dec(balance, "balance"), Dec(balance, "available"));
    }

    private async Task<JsonElement> MarketAsync(RouteEnvironment environment, string epic, CancellationToken ct) =>
        await RequestAsync(environment, HttpMethod.Get, $"/markets/{Uri.EscapeDataString(epic.Trim())}", 3, null, ct).ConfigureAwait(false);

    protected override async Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        ReadInstrument(await MarketAsync(environment, symbol, ct).ConfigureAwait(false), symbol.Trim())
        ?? throw new BrokerOrderRouteException($"IG: {symbol} is not a market this account can deal.", isRejection: true);

    /// <summary><c>{"instrument":{"epic","expiry","valueOfOnePip","contractSize","currencies":[{"code","isDefault"}]},
    /// "dealingRules":{"minDealSize":{"value"}},"snapshot":{"decimalPlacesFactor","marketStatus"}}</c>.</summary>
    internal static RouteInstrument? ReadInstrument(JsonElement root, string symbol)
    {
        if (!root.TryGetProperty("instrument", out var instrument)) return null;
        var snapshot = root.TryGetProperty("snapshot", out var s) ? s : default;
        var minimum = root.TryGetProperty("dealingRules", out var rules) && rules.TryGetProperty("minDealSize", out var min) ? Dec(min, "value") : 0m;
        // IG publishes no size step; sizes go to two decimals, or finer when the minimum itself is finer.
        var unit = Math.Min(0.01m, minimum > 0 ? Step(Decimals(minimum)) : 0.01m);
        var tick = Step((int)Dec(snapshot, "decimalPlacesFactor"));
        var perContract = Dec(instrument, "valueOfOnePip") is > 0 and var pip ? pip : Dec(instrument, "contractSize") is > 0 and var size ? size : 1m;
        var currency = string.Empty;
        if (instrument.TryGetProperty("currencies", out var currencies) && currencies.ValueKind == JsonValueKind.Array)
            foreach (var c in currencies.EnumerateArray())
                if (currency.Length == 0 || (c.TryGetProperty("isDefault", out var d) && d.ValueKind == JsonValueKind.True))
                    currency = Str(c, "code");
        return new RouteInstrument(
            symbol, unit, unit * perContract, tick, Units(minimum, unit, 1), 100_000_000,
            RouteOrderTypes.Market | RouteOrderTypes.Limit,
            RouteTimesInForce.Day | RouteTimesInForce.GoodTillCancelled | RouteTimesInForce.ImmediateOrCancel | RouteTimesInForce.FillOrKill,
            SupportsReplace: false, currency.Length > 0 ? currency : "USD");
    }

    private static int Decimals(decimal value)
    {
        var text = Num(value);
        var dot = text.IndexOf('.');
        return dot < 0 ? 0 : text.Length - dot - 1;
    }

    public override async Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct)
    {
        if (request.Type == RouteOrderType.Limit && request.TimeInForce is not (RouteTimeInForce.ImmediateOrCancel or RouteTimeInForce.FillOrKill))
            throw new BrokerOrderRouteException(
                "IG: a resting limit is an IG working order, which this route does not place — send the limit IOC or FOK.", isRejection: true);
        var market = await MarketAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var rules = await RulesAsync(environment, request.Symbol, ct).ConfigureAwait(false);
        var expiry = market.TryGetProperty("instrument", out var instrument) && Str(instrument, "expiry") is { Length: > 0 } e ? e : "-";
        var reference = Token(request.ClientOrderId, id => Reference(id));
        var root = await RequestAsync(environment, HttpMethod.Post, "/positions/otc", 2,
            SubmitBody(request, expiry, rules.Currency, reference), ct).ConfigureAwait(false);
        var dealReference = Str(root, "dealReference") is { Length: > 0 } answered ? answered : reference;

        // The outcome is in the confirmation, which IG may take a moment to publish.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (await ConfirmAsync(environment, request.Symbol, dealReference, ct).ConfigureAwait(false) is { } confirmed)
                return confirmed with { ClientOrderId = request.ClientOrderId };
            await Task.Delay(TimeSpan.FromMilliseconds(250 * (attempt + 1)), Time, ct).ConfigureAwait(false);
        }

        return new RouteOrder(dealReference, request.ClientOrderId, request.Symbol, request.Side, request.Type, request.TimeInForce, request.Quantity,
            request.LimitPrice, null, RouteOrderStatus.PendingNew, 0m, null, 0m, string.Empty, null, Now.UtcDateTime);
    }

    /// <summary>A deal reference IG accepts: letters, digits, <c>_</c> and <c>-</c>, at most 30.</summary>
    internal static string Reference(string id)
    {
        var kept = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-').ToArray());
        return kept.Length == id.Length && kept.Length <= 30 ? kept : Hex(id, 30);
    }

    internal static JsonObject SubmitBody(RouteOrderRequest request, string expiry, string currency, string reference)
    {
        var body = new JsonObject
        {
            ["epic"] = request.Symbol.Trim(),
            ["expiry"] = expiry,
            ["direction"] = request.Side == OrderSide.Buy ? "BUY" : "SELL",
            ["size"] = request.Quantity,
            ["orderType"] = request.Type == RouteOrderType.Limit ? "LIMIT" : "MARKET",
            ["timeInForce"] = request.TimeInForce == RouteTimeInForce.ImmediateOrCancel ? "EXECUTE_AND_ELIMINATE" : "FILL_OR_KILL",
            ["guaranteedStop"] = false,
            ["forceOpen"] = false,
            ["currencyCode"] = currency,
            ["dealReference"] = reference,
        };
        if (request.Type == RouteOrderType.Limit) body["level"] = request.LimitPrice!.Value;
        return body;
    }

    private async Task<RouteOrder?> ConfirmAsync(RouteEnvironment environment, string symbol, string dealReference, CancellationToken ct)
    {
        var answer = await SendVersionedAsync(environment, HttpMethod.Get, $"/confirms/{Uri.EscapeDataString(dealReference)}", 1, null, ct).ConfigureAwait(false);
        if (answer.Status == 404) return null;
        if (answer.Status is < 200 or >= 300)
            throw Refused(answer.Status, Str(answer.Root, "errorCode") is { Length: > 0 } code ? code : null, answer.Body);
        return ReadConfirm(answer.Root, symbol, dealReference);
    }

    /// <summary><c>{"dealReference","dealId","dealStatus":"ACCEPTED|REJECTED","reason","status","direction","size","level","date"}</c> —
    /// an accepted deal is filled at <c>level</c> for <c>size</c>; a rejected one carries IG's reason.</summary>
    internal RouteOrder ReadConfirm(JsonElement root, string symbol, string dealReference)
    {
        var accepted = Str(root, "dealStatus") == "ACCEPTED";
        var size = Dec(root, "size");
        return new RouteOrder(
            dealReference,
            EngineId(dealReference),
            symbol,
            Str(root, "direction") == "SELL" ? OrderSide.Sell : OrderSide.Buy,
            RouteOrderType.Market,
            RouteTimeInForce.FillOrKill,
            size,
            null,
            null,
            accepted ? RouteOrderStatus.Filled : Str(root, "dealStatus") == "REJECTED" ? RouteOrderStatus.Rejected : RouteOrderStatus.Unknown,
            accepted ? size : 0m,
            accepted ? Positive(root, "level") : null,
            0m,
            string.Empty,
            accepted ? null : Str(root, "reason") is { Length: > 0 } reason ? reason : null,
            ParseTime(Str(root, "date"), Now.UtcDateTime));
    }

    public override Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct) =>
        throw new BrokerOrderRouteException("IG deals here fill or are killed at once; there is nothing resting to cancel.", isRejection: true);

    /// <summary>Nothing rests: every deal this route places ends in its confirmation.</summary>
    public override Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        RequireEnvironment(environment);
        return Task.FromResult<IReadOnlyList<RouteOrder>>([]);
    }

    public override Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct) =>
        orderId.Length == 0 ? Task.FromResult<RouteOrder?>(null) : ConfirmAsync(environment, symbol, orderId, ct);

    public override async Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var root = await RequestAsync(environment, HttpMethod.Get, "/positions", 2, null, ct).ConfigureAwait(false);
        return new RoutePosition(symbol, ReadPosition(root, symbol));
    }

    /// <summary><c>{"positions":[{"position":{"size","direction":"BUY|SELL"},"market":{"epic"}}]}</c> — the net over every
    /// open position in the epic.</summary>
    internal static decimal ReadPosition(JsonElement root, string epic)
    {
        if (!root.TryGetProperty("positions", out var rows) || rows.ValueKind != JsonValueKind.Array) return 0m;
        var net = 0m;
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("market", out var market) || !string.Equals(Str(market, "epic"), epic.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var position = row.TryGetProperty("position", out var p) ? p : default;
            var size = Math.Abs(Dec(position, "size"));
            net += Str(position, "direction") == "SELL" ? -size : size;
        }

        return net;
    }

    public override async Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var market = await MarketAsync(environment, symbol, ct).ConfigureAwait(false);
        var snapshot = market.TryGetProperty("snapshot", out var s) ? s : default;
        return Positive(snapshot, "bid") is { } bid && Positive(snapshot, "offer") is { } offer
            ? new RoutePrice((bid + offer) / 2, Now.UtcDateTime)
            : null;
    }
}
