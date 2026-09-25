using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Infrastructure.Brokers;

/// <summary>
/// A session that has to be kept alive: a short-lived access token and what renews it. Stored as JSON in
/// the credential store's session slot, which is opaque outside the broker's adapter.
/// </summary>
internal sealed record KeptSession
{
    [JsonPropertyName("access")] public string AccessToken { get; init; } = string.Empty;

    /// <summary>What renews the access token: an OAuth refresh token. Empty for a broker that renews by
    /// signing in again with the stored credentials (IG, Tradovate).</summary>
    [JsonPropertyName("refresh")] public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("expires")] public DateTimeOffset ExpiresUtc { get; init; }

    /// <summary>A second secret paired with the token: the OAuth 1.0a token secret (E*TRADE), IG's
    /// security token.</summary>
    [JsonPropertyName("secret")] public string Secret { get; init; } = string.Empty;

    /// <summary>Where the session says requests go: Questrade's <c>api_server</c>, Saxo's <c>base_uri</c>.</summary>
    [JsonPropertyName("server")] public string Server { get; init; } = string.Empty;

    /// <summary>Anything else a broker's session carries: Tradovate's market-data token, IG's account.</summary>
    [JsonPropertyName("extra")] public string Extra { get; init; } = string.Empty;

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>The stored session, or null when it is empty or not one of these — a session written by an
    /// older build, or pasted by hand.</summary>
    public static KeptSession? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{') return null;
        try
        {
            return JsonSerializer.Deserialize<KeptSession>(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Keeps one broker's <see cref="KeptSession"/> fresh: renews it shortly before it expires, once no matter
/// how many requests arrive together, and writes the renewed session back to the store.
///
/// <para><b>The write-back is the point.</b> Saxo and Questrade rotate their refresh tokens — each refresh
/// returns a new one and kills the old. A client that renewed in memory only would leave the store holding
/// a dead token, and the next start would fail as though the user had never signed in.</para>
///
/// <para><b>Whose session wins.</b> The store is re-read on every call, so a user who signs in again while
/// the terminal runs is picked up at once. A stored session is adopted only when it is neither the one this
/// keeper started from nor one it wrote itself — so if a write-back fails, the keeper carries on with the
/// renewed session it holds instead of falling back to the dead one still on disk.</para>
/// </summary>
internal sealed class SessionKeeper
{
    /// <summary>How long before expiry a session is renewed, so a request never leaves with a token that
    /// dies in flight.</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(90);

    private readonly BrokerKind _broker;
    private readonly IBrokerCredentialSource _credentials;
    private readonly IBrokerSessionStore _store;
    private readonly Func<BrokerCredential, KeptSession, CancellationToken, Task<KeptSession>> _renew;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _renewing = new(1, 1);
    private readonly Lock _gate = new();

    private KeptSession? _current;
    private string? _adoptedFrom;
    private string? _written;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IBrokerCredentialSource,
        System.Collections.Concurrent.ConcurrentDictionary<BrokerKind, SessionKeeper>> SharedKeepers = new();

    /// <summary>
    /// The one keeper for <paramref name="broker"/> over this credential store — created on first use, then
    /// shared by the broker's market-data client and its order route.
    ///
    /// <para><b>Why one.</b> Saxo and Questrade spend a refresh token when they renew. Two keepers renewing
    /// the same session would each spend the same token; the second is refused, and whichever lost would
    /// report a dead session that is in fact fine. Keyed by the credential source instance, so the
    /// application shares keepers and each test's fake gets its own.</para>
    /// </summary>
    public static SessionKeeper Shared(
        BrokerKind broker, IBrokerCredentialSource credentials, IBrokerSessionStore store,
        Func<BrokerCredential, KeptSession, CancellationToken, Task<KeptSession>> renew, ILogger logger, TimeProvider? time = null) =>
        SharedKeepers.GetOrCreateValue(credentials).GetOrAdd(broker, _ => new SessionKeeper(broker, credentials, store, renew, logger, time));

    /// <summary>A keeper for <paramref name="broker"/>'s session, read from <paramref name="credentials"/>
    /// and written back to <paramref name="store"/>. <paramref name="renew"/> renews it — a refresh-token
    /// grant, or a fresh sign-in from the stored credentials — and throws when the broker refuses.</summary>
    public SessionKeeper(
        BrokerKind broker, IBrokerCredentialSource credentials, IBrokerSessionStore store,
        Func<BrokerCredential, KeptSession, CancellationToken, Task<KeptSession>> renew, ILogger logger, TimeProvider? time = null)
    {
        _broker = broker;
        _credentials = credentials;
        _store = store;
        _renew = renew;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>A session good for at least <see cref="Margin"/>, renewed first when it is not.</summary>
    /// <exception cref="InvalidOperationException">There is no stored session, or it cannot be read.</exception>
    public async Task<KeptSession> CurrentAsync(CancellationToken ct)
    {
        var app = _credentials.For(_broker);
        var session = Adopt(app.Session);
        if (Fresh(session)) return session;

        await _renewing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Someone else may have renewed while this call waited.
            session = Adopt(_credentials.For(_broker).Session);
            if (Fresh(session)) return session;

            var renewed = await _renew(app, session, ct).ConfigureAwait(false);
            var text = renewed.ToJson();
            lock (_gate)
            {
                _current = renewed;
                _written = text;
            }

            _store.Renew(_broker, text);
            _logger.LogDebug("{Broker} session renewed; good until {Expires:u}.", _broker, renewed.ExpiresUtc);
            return renewed;
        }
        finally
        {
            _renewing.Release();
        }
    }

    /// <summary>Marks <paramref name="seen"/> as spent, so the next call renews it — after the broker
    /// answered 401 to a token the keeper believed was good.</summary>
    public void Invalidate(KeptSession seen)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_current, seen)) _current = seen with { ExpiresUtc = DateTimeOffset.MinValue };
        }
    }

    private bool Fresh(KeptSession session) =>
        session.ExpiresUtc != DateTimeOffset.MinValue && session.ExpiresUtc > _time.GetUtcNow() + Margin;

    private KeptSession Adopt(string stored)
    {
        lock (_gate)
        {
            if (_current is null || (stored != _adoptedFrom && stored != _written))
            {
                _current = KeptSession.Parse(stored)
                    ?? throw new InvalidOperationException($"No usable {_broker} session is stored — sign in again in the login window.");
                _adoptedFrom = stored;
                _written = null;
            }

            return _current;
        }
    }
}

/// <summary>The OAuth 2 token response every broker here shares, read the same way.</summary>
internal static class OAuthTokens
{
    /// <summary>
    /// Reads <c>access_token</c>, <c>refresh_token</c> and <c>expires_in</c>. A response that carries no new
    /// refresh token keeps <paramref name="previous"/>'s — not every broker rotates, and some omit it from a
    /// refresh answer.
    /// </summary>
    /// <exception cref="InvalidOperationException">The answer has no access token; the message is the
    /// broker's own refusal where it gave one.</exception>
    public static KeptSession Read(JsonElement root, DateTimeOffset now, KeptSession? previous = null, string broker = "The broker")
    {
        var access = SignInProof.Text(root, "access_token");
        if (string.IsNullOrWhiteSpace(access))
            throw new InvalidOperationException($"{broker} issued no access token: {Refusal(root) ?? root.GetRawText()}");

        var refresh = SignInProof.Text(root, "refresh_token");
        var expiresIn = double.TryParse(SignInProof.Text(root, "expires_in"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0 ? seconds : 1800;

        return (previous ?? new KeptSession()) with
        {
            AccessToken = access,
            RefreshToken = string.IsNullOrWhiteSpace(refresh) ? previous?.RefreshToken ?? string.Empty : refresh,
            ExpiresUtc = now.AddSeconds(expiresIn),
        };
    }

    /// <summary>The broker's words in an OAuth error answer, or null.</summary>
    public static string? Refusal(JsonElement root) =>
        new[] { "error_description", "message", "error", "errorText", "errorCode" }
            .Select(name => SignInProof.Text(root, name))
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));

    /// <summary>POSTs a form to a token endpoint and reads the answer; a refusal throws with the broker's
    /// words, which is what the keeper and the login window both show.</summary>
    public static async Task<KeptSession> PostFormAsync(
        HttpClient http, string url, IEnumerable<KeyValuePair<string, string>> form, DateTimeOffset now, KeptSession? previous,
        string broker, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        var (status, root, body) = await SignInProof.PostAsync(http, url, new FormUrlEncodedContent(form), ct, headers).ConfigureAwait(false);
        if (status is < 200 or >= 300)
            throw new InvalidOperationException($"{broker} refused: {Refusal(root) ?? SignInProof.Snippet(body)} (HTTP {status})");
        return Read(root, now, previous, broker);
    }

    /// <summary>A sign-in answer from a token call: the session, or the broker's refusal.</summary>
    public static async Task<SessionIssue> IssueAsync(Func<Task<KeptSession>> exchange, string account = "")
    {
        try
        {
            return SessionIssue.Issued((await exchange().ConfigureAwait(false)).ToJson(), account);
        }
        catch (InvalidOperationException ex)
        {
            return SessionIssue.Refused(ex.Message);
        }
    }
}
