using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Execution;
using TradingTerminal.Core.Trading;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// What every broker's order route does the same way: send and read, tell a refusal from an unknown
/// outcome, parse numbers exactly, and translate the engine's client order ids into what each broker
/// accepts.
///
/// <para><b>Refusal or unknown — the one judgement that matters.</b> A route that calls a timeout a refusal
/// tells the engine an order is not on the book when it may be, and the engine would then send it again. So
/// only an answer the broker <i>gave</i> — a 4xx status with its words, or its own error code in a 200 — is a
/// refusal; a timeout, a dropped connection or a 5xx is an unknown outcome that the engine reconciles.</para>
///
/// <para><b>Numbers are decimal, parsed from the text the broker sent.</b> A double would turn 0.1 BTC into
/// 0.1000000000000000055511151231257827, which is not a whole number of units of anything.</para>
/// </summary>
internal abstract class OrderRouteBase : IBrokerOrderRoute, IDisposable
{
    private const int MaximumClientIds = 20_000;

    private readonly IBrokerCredentialSource _credentials;
    private readonly ConcurrentDictionary<string, string> _engineIdByToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RouteInstrument> _rules = new(StringComparer.OrdinalIgnoreCase);

    protected OrderRouteBase(IBrokerCredentialSource credentials, ILogger logger, TimeProvider? time = null, HttpMessageHandler? handler = null)
    {
        _credentials = credentials;
        Logger = logger;
        Time = time ?? TimeProvider.System;
        Http = new HttpClient(handler ?? new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DaxAlgoTerminal/1.0");
    }

    protected ILogger Logger { get; }

    protected HttpClient Http { get; }

    protected TimeProvider Time { get; }

    protected DateTimeOffset Now => Time.GetUtcNow();

    /// <summary>This broker's credentials as stored now — read on every request, never captured.</summary>
    protected BrokerCredential Credential => _credentials.For(Broker);

    public abstract BrokerKind Broker { get; }

    public abstract string DisplayName { get; }

    public abstract string RouteId { get; }

    public abstract string? PaperEnvironmentName { get; }

    public abstract Task<RouteAccount> ConnectAsync(RouteEnvironment environment, CancellationToken ct);

    public virtual Task<RouteAccount> AccountAsync(RouteEnvironment environment, CancellationToken ct) => ConnectAsync(environment, ct);

    public async Task<RouteInstrument> InstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct)
    {
        var rules = await FetchInstrumentAsync(environment, symbol, ct).ConfigureAwait(false);
        _rules[$"{environment}|{symbol}"] = rules;
        return rules;
    }

    /// <summary>The broker's rules for one symbol, read from its instrument endpoint.</summary>
    protected abstract Task<RouteInstrument> FetchInstrumentAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    /// <summary>Rules read earlier for this symbol, or read now.</summary>
    protected async Task<RouteInstrument> RulesAsync(RouteEnvironment environment, string symbol, CancellationToken ct) =>
        _rules.TryGetValue($"{environment}|{symbol}", out var rules) ? rules : await InstrumentAsync(environment, symbol, ct).ConfigureAwait(false);

    public abstract Task<RouteOrder> SubmitAsync(RouteEnvironment environment, RouteOrderRequest request, CancellationToken ct);

    public abstract Task CancelAsync(RouteEnvironment environment, RouteOrder order, CancellationToken ct);

    public virtual Task<RouteOrder> ReplaceAsync(
        RouteEnvironment environment, RouteOrder order, decimal quantity, decimal? limitPrice, decimal? stopPrice, CancellationToken ct) =>
        throw new NotSupportedException($"{DisplayName} orders are not amended in place by this route.");

    public abstract Task<IReadOnlyList<RouteOrder>> OrdersAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    public abstract Task<RouteOrder?> OrderAsync(RouteEnvironment environment, string symbol, string orderId, string clientOrderId, CancellationToken ct);

    public abstract Task<RoutePosition> PositionAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    public abstract Task<RoutePrice?> PriceAsync(RouteEnvironment environment, string symbol, CancellationToken ct);

    // ── HTTP ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sends and reads, whatever the status — brokers put their refusals in the body. A transport
    /// failure propagates as itself, which the engine treats as an unknown outcome.</summary>
    protected async Task<(int Status, JsonElement Root, string Body)> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        using var request = build();
        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ((int)response.StatusCode, Parse(body), body);
    }

    /// <summary>The body as JSON, or an empty object when it is not JSON (an HTML error page).</summary>
    protected static JsonElement Parse(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

    /// <summary>
    /// A refusal in the broker's words. 4xx statuses are refusals — the broker read the request and said no
    /// — except 408, which is a timeout. 5xx statuses and anything without a status are unknown outcomes.
    /// </summary>
    protected BrokerOrderRouteException Refused(int status, string? words, string body, bool? isRejection = null) =>
        new($"{DisplayName}: {Words(words, body)} (HTTP {status})",
            isRejection ?? status is >= 400 and < 500 and not 408);

    /// <summary>A refusal the broker gave inside a successful HTTP answer (its own error code).</summary>
    protected BrokerOrderRouteException RefusedInBody(string? words, string body) =>
        new($"{DisplayName}: {Words(words, body)}", isRejection: true);

    /// <summary>Throws the broker's refusal unless the status is 2xx.</summary>
    protected void EnsureSuccess(int status, string body, string? words)
    {
        if (status is < 200 or >= 300)
            throw Refused(status, words, body);
    }

    private static string Words(string? words, string body) =>
        !string.IsNullOrWhiteSpace(words) ? words.Trim() : SignInProof.Snippet(body);

    // ── Numbers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>A decimal as the broker expects it: invariant, no exponent, no trailing zeros.</summary>
    protected static string Num(decimal value) => value.ToString("0.############################", CultureInfo.InvariantCulture);

    /// <summary>A JSON number or numeric string as an exact decimal, or 0.</summary>
    protected static decimal Dec(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number => decimal.TryParse(element.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0m,
        JsonValueKind.String => decimal.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0m,
        _ => 0m,
    };

    protected static decimal Dec(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? Dec(value) : 0m;

    /// <summary>A positive decimal, or null when absent or zero — how most brokers spell "no price".</summary>
    protected static decimal? Positive(JsonElement obj, string name) => Dec(obj, name) is > 0 and var value ? value : null;

    protected static string Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                _ => string.Empty,
            }
            : string.Empty;

    /// <summary>10^-decimals: a step or tick given as a number of decimal places.</summary>
    protected static decimal Step(int decimals)
    {
        var step = 1m;
        for (var i = 0; i < decimals; i++)
            step /= 10m;
        return step;
    }

    /// <summary>A quantity bound expressed in units of <paramref name="unit"/>, at least 1.</summary>
    protected static long Units(decimal native, decimal unit, long fallback)
    {
        if (native <= 0 || unit <= 0)
            return fallback;
        var units = decimal.Ceiling(native / unit);
        return units >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)units);
    }

    /// <summary>A Unix time in whatever unit it arrived, as UTC; now when absent.</summary>
    protected DateTime Utc(long epoch) => epoch > 0 ? Crypto.CryptoConvert.MsUtc(epoch) : Now.UtcDateTime;

    /// <summary>An ISO time as UTC, or <paramref name="fallback"/>. Fractions past the seven digits .NET reads
    /// (OANDA sends nanoseconds) are dropped rather than failing the parse.</summary>
    protected static DateTime ParseTime(string text, DateTime fallback)
    {
        const DateTimeStyles Styles = DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal;
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, Styles, out var time))
            return time;
        var trimmed = System.Text.RegularExpressions.Regex.Replace(text ?? string.Empty, @"(\.\d{7})\d+", "$1");
        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, Styles, out time) ? time : fallback;
    }

    // ── Equities ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>What a US equity order has to say about the position it acts on.</summary>
    internal enum EquityIntent
    {
        Buy,
        Sell,
        SellShort,
        BuyToCover,
    }

    /// <summary>
    /// The intent a US broker needs for an equity order, from the position now held. A sell that would take a
    /// long position through zero into a short is refused before it is sent: the brokers that ask for the intent
    /// (Tradier, TradeStation, Schwab, tastytrade, E*TRADE) take a sell-to-close and a sell-short as two orders,
    /// and splitting one order in two is a decision for the strategy, not the route.
    /// </summary>
    protected EquityIntent IntentFor(OrderSide side, decimal quantity, decimal position)
    {
        if (side == OrderSide.Buy)
        {
            if (position >= 0) return EquityIntent.Buy;
            if (quantity <= -position) return EquityIntent.BuyToCover;
            throw CrossesZero(side, quantity, position);
        }

        if (position <= 0) return EquityIntent.SellShort;
        if (quantity <= position) return EquityIntent.Sell;
        throw CrossesZero(side, quantity, position);
    }

    private BrokerOrderRouteException CrossesZero(OrderSide side, decimal quantity, decimal position) =>
        new($"{DisplayName}: {(side == OrderSide.Buy ? "buying" : "selling")} {Num(quantity)} with {Num(position)} held would cross from "
            + $"{(position > 0 ? "long to short" : "short to long")}; {DisplayName} takes that as two orders — close {Num(Math.Abs(position))} first.",
            isRejection: true);

    /// <summary>The US equity price grid: a cent from $1, a hundredth of a cent below.</summary>
    protected static decimal EquityTick(decimal? price) => price is > 0 and < 1 ? 0.0001m : 0.01m;

    /// <summary>The rules every US equity shares: whole shares, a point is a dollar a share.</summary>
    protected static RouteInstrument Equity(string symbol, decimal? price, RouteOrderTypes types, RouteTimesInForce times, bool replace = false) =>
        new(symbol, 1m, 1m, EquityTick(price), 1, 10_000_000, types, times, replace, "USD");

    /// <summary>Letters, digits and hyphens only, at most <paramref name="length"/> — a hash when stripping is
    /// not enough.</summary>
    protected static string Tag(string id, int length)
    {
        var kept = new string(id.Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return kept.Length == id.Length && kept.Length <= length ? kept : Hex(id, Math.Min(length, 32));
    }
    // ── Client order ids ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The engine's client order id as this broker accepts it. Brokers disagree — OKX takes 32 letters and
    /// digits, Bitvavo a UUID, Bitfinex a number — so each route maps the id, and the mapping is remembered
    /// so the broker's answers can be matched back to the engine's order.
    /// </summary>
    protected string Token(string engineId, Func<string, string> transform)
    {
        var token = transform(engineId);
        if (_engineIdByToken.Count >= MaximumClientIds)
            _engineIdByToken.Clear();
        _engineIdByToken[token] = engineId;
        return token;
    }

    /// <summary>The engine's id for a broker-side token, or the token itself when it is not one of ours.</summary>
    protected string EngineId(string? token) =>
        string.IsNullOrEmpty(token) ? string.Empty : _engineIdByToken.TryGetValue(token, out var engineId) ? engineId : token;

    /// <summary>Letters and digits only, at most <paramref name="length"/> — a hash when stripping is not enough.</summary>
    protected static string Alphanumeric(string id, int length)
    {
        var stripped = new string(id.Where(char.IsAsciiLetterOrDigit).ToArray());
        return stripped.Length <= length ? stripped : Hex(id, length);
    }

    /// <summary>The first <paramref name="length"/> hex digits of SHA-256 of <paramref name="text"/>.</summary>
    protected static string Hex(string text, int length) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..length];

    /// <summary>A UUID derived from the id, for brokers that accept nothing else.</summary>
    protected static string Uuid(string id) => new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(id))[..16]).ToString();

    /// <summary>For a broker whose API names no account: a stable identity derived from the API key. It names
    /// the key rather than the account — the closest thing the API exposes.</summary>
    protected string KeyAccount() => $"key-{Hex(Credential.Key.Trim(), 12)}";

    public void Dispose() => Http.Dispose();
}
