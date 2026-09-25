namespace TradingTerminal.Core.Brokers;

/// <summary>How a broker's sign-in is completed — which decides what the login form asks for.</summary>
public enum SignInStyle
{
    /// <summary>A browser page on the broker's site redirects back with a one-time code or token, which
    /// the user pastes (the whole redirected URL is fine). Zerodha, Fyers, ICICI Breeze, Schwab, Saxo…</summary>
    Browser,

    /// <summary>A code the user has to hand — the six digits from an authenticator app — is exchanged
    /// directly, with no browser. Angel One, 5paisa.</summary>
    Code,

    /// <summary>The stored credentials alone produce a session; the user presses Sign in and nothing
    /// else. Alice Blue, tastytrade, Tradovate, IG.</summary>
    Credentials,

    /// <summary>The broker issues the token on its own site; the user pastes it and there is nothing to
    /// exchange. Dhan, Trading 212.</summary>
    Token,
}

/// <summary>What a sign-in produced.</summary>
/// <param name="Ok">True when a session was issued.</param>
/// <param name="Session">The session material the broker's client spends — opaque outside that broker's
/// adapter. Empty when <paramref name="Ok"/> is false.</param>
/// <param name="Detail">What to tell the user: the broker's own words on a refusal.</param>
/// <param name="Account">The account the broker says the session belongs to, when it says.</param>
public readonly record struct SessionIssue(bool Ok, string Session = "", string Detail = "", string Account = "")
{
    public static SessionIssue Issued(string session, string account = "") => new(true, session, string.Empty, account);

    public static SessionIssue Refused(string detail) => new(false, string.Empty, detail);
}

/// <summary>
/// Turns what a user brings back from a broker's sign-in into a session a client can spend.
///
/// <para><b>One seam for every broker with a sign-in step.</b> The login window collects the proof and the
/// infrastructure layer knows how each broker exchanges it, and neither can see the other — so, like
/// <see cref="IBrokerCredentialSource"/> and <see cref="IBrokerCredentialVerifier"/>, the seam lives in Core.
/// One interface rather than one per broker: Upstox's own <c>IUpstoxAuthService</c> is the shape this
/// replaces for everything added after it, because a dozen of those is a dozen chances for one to be
/// wired to nothing.</para>
/// </summary>
public interface IBrokerSessionIssuer
{
    /// <summary>True when this issuer knows <paramref name="broker"/>'s sign-in.</summary>
    bool Issues(BrokerKind broker);

    /// <summary>How <paramref name="broker"/>'s sign-in is completed.</summary>
    SignInStyle StyleOf(BrokerKind broker);

    /// <summary>
    /// The page to open for a <see cref="SignInStyle.Browser"/> sign-in, or null for the others.
    ///
    /// <para>Asynchronous because building it can take a call: an OAuth 1.0a broker (E*TRADE) must fetch
    /// a request token before there is a page to send the user to.</para>
    /// </summary>
    /// <param name="broker">The broker to sign in to.</param>
    /// <param name="app">The registered app's key (and secret, where the URL needs one).</param>
    /// <param name="redirectUri">The redirect URL registered with the broker for the app.</param>
    /// <param name="ct">Cancels the call a page may need first.</param>
    Task<string?> SignInUrlAsync(BrokerKind broker, BrokerCredential app, string redirectUri, CancellationToken ct = default);

    /// <summary>
    /// Exchanges <paramref name="proof"/> for a session. <b>Never throws</b> for a refusal or an unreachable
    /// broker — both come back as <see cref="SessionIssue.Refused"/> with the reason.
    /// </summary>
    /// <param name="broker">The broker to sign in to.</param>
    /// <param name="app">Everything the login row holds for the broker: keys, account, extra.</param>
    /// <param name="proof">What the user brought back: the redirected URL or the code in it for a browser
    /// sign-in, the six-digit code for a code sign-in, the token itself for a pasted one. Empty for a
    /// credentials sign-in.</param>
    /// <param name="redirectUri">The redirect URL registered with the broker, for a browser sign-in.</param>
    /// <param name="ct">Cancels the exchange.</param>
    Task<SessionIssue> SignInAsync(
        BrokerKind broker, BrokerCredential app, string proof, string redirectUri, CancellationToken ct = default);

    /// <summary>An issuer that knows no broker — what an edition composes when no infrastructure is wired.</summary>
    public static IBrokerSessionIssuer None { get; } = new NoIssuer();

    private sealed class NoIssuer : IBrokerSessionIssuer
    {
        public bool Issues(BrokerKind broker) => false;

        public SignInStyle StyleOf(BrokerKind broker) => SignInStyle.Token;

        public Task<string?> SignInUrlAsync(BrokerKind broker, BrokerCredential app, string redirectUri, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<SessionIssue> SignInAsync(
            BrokerKind broker, BrokerCredential app, string proof, string redirectUri, CancellationToken ct = default) =>
            Task.FromResult(SessionIssue.Refused($"No sign-in is wired for {broker} in this build."));
    }
}

/// <summary>
/// Where a client writes back a session it renewed.
///
/// <para><b>Why a client writes at all.</b> Some brokers rotate their refresh tokens: every refresh returns a
/// new one and invalidates the old (Saxo, Questrade). A client that refreshed in memory only would leave the
/// store holding a dead token, and the next start would fail as if the user had never signed in. The
/// renewed session is written back through this seam — the same shape as
/// <see cref="IBrokerCredentialSource"/>, and in Core for the same reason: the store is in the login layer
/// and the client in the infrastructure layer, and neither can see the other.</para>
/// </summary>
public interface IBrokerSessionStore
{
    /// <summary>Replaces <paramref name="broker"/>'s stored session, keeping its keys and account.</summary>
    void Renew(BrokerKind broker, string session);

    /// <summary>A store that keeps nothing — what an edition composes without a credential store.</summary>
    public static IBrokerSessionStore None { get; } = new NoStore();

    private sealed class NoStore : IBrokerSessionStore
    {
        public void Renew(BrokerKind broker, string session) { }
    }
}
