using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradingTerminal.App.Login;
using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Configuration;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Brokers;
using Xunit;

namespace TradingTerminal.UI.Tests;

/// <summary>
/// The path a pasted API key actually travels: login form → DPAPI store → the client that spends it.
///
/// <para><b>Why this is the test that matters for keyed brokers.</b> Every other part of the flow has a
/// visible symptom when it breaks. This one does not: a form that saves to a slot nothing reads looks
/// exactly like a form that works — the key is accepted, the window closes, the charts fill from the
/// public feed. That was the real state of the keyed crypto rows before this seam existed, and nothing
/// in the suite noticed. These tests walk the whole path so it cannot quietly come apart again.</para>
///
/// <para>Everything is written to a temporary directory. Nothing here touches the real store — an
/// earlier suite did, and left a stray provider block in a developer's live settings.</para>
/// </summary>
public sealed class BrokerCredentialRoundTripTests : IDisposable
{
    private readonly string _directory;

    public BrokerCredentialRoundTripTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "daxalgo-credential-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(_directory);
        CredentialStore.DirectoryOverride = _directory;
    }

    public void Dispose()
    {
        CredentialStore.DirectoryOverride = null;
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }

    private static CredentialStore Store() => new(NullLogger<CredentialStore>.Instance);

    private static StoredBrokerCredentials Source(CredentialStore store) =>
        new(store, NullLogger<StoredBrokerCredentials>.Instance);

    [Fact]
    public void A_key_written_by_a_form_is_the_key_a_client_reads()
    {
        var store = Store();

        // What a login form does on Save.
        var stored = store.Load();
        stored.SetKeys(BrokerKind.Okx, apiKey: "public-key", secret: "the-secret", passphrase: "phrase");
        store.Save(stored);

        // What a broker client does at connect.
        var credential = Source(store).For(BrokerKind.Okx);

        Assert.Equal("public-key", credential.Key);
        Assert.Equal("the-secret", credential.Secret);
        Assert.Equal("phrase", credential.Passphrase);
        Assert.True(credential.IsPair);
    }

    [Fact]
    public void Ironbeam_and_Upstox_keys_in_their_own_named_fields_reach_the_credential_source()
    {
        // Both logins predate the per-broker map and still write named fields; the order routes and the
        // execution console read credentials only through the source, so it has to see them.
        var store = Store();
        var stored = store.Load();
        stored.IronBeamUsername = "ib-user";
        stored.IronBeamApiKey = "ib-key";
        stored.IronBeamIsLive = true;
        stored.UpstoxApiKey = "upstox-key";
        stored.UpstoxApiSecret = "upstox-secret";
        stored.UpstoxAccessToken = "upstox-day-token";
        store.Save(stored);

        var ironbeam = Source(store).For(BrokerKind.IronBeam);
        Assert.Equal(("ib-user", "ib-key", "live"), (ironbeam.Key, ironbeam.Secret, ironbeam.Extra));
        var upstox = Source(store).For(BrokerKind.Upstox);
        Assert.Equal(("upstox-key", "upstox-secret", "upstox-day-token"), (upstox.Key, upstox.Secret, upstox.Session));
        Assert.True(upstox.HasSession);
    }

    [Fact]
    public void A_broker_with_nothing_stored_reports_nothing_rather_than_throwing()
    {
        // The ordinary state for every broker the user has not set up. A client asks, gets nothing,
        // and says "needs a key" — it must not fail to construct or blow up at connect.
        var credential = Source(Store()).For(BrokerKind.Kraken);

        Assert.False(credential.IsConfigured);
        Assert.Equal(string.Empty, credential.Secret);
    }

    [Fact]
    public void A_bearer_token_broker_is_configured_without_a_key_half()
    {
        // Tradier and OANDA authenticate with a token alone. If IsConfigured demanded both halves,
        // their credentials would read as absent and the client would refuse a perfectly good token.
        var store = Store();
        var stored = store.Load();
        stored.SetKeys(BrokerKind.Tradier, apiKey: "sandbox", secret: "token-value", passphrase: null);
        store.Save(stored);

        var credential = Source(store).For(BrokerKind.Tradier);

        Assert.True(credential.IsConfigured);
        Assert.Equal("token-value", credential.Secret);
    }

    [Fact]
    public void Clearing_a_broker_really_removes_it()
    {
        // Choosing the keyless row clears the key. If it lingered, "keyless" would silently mean
        // "authenticated because you once pasted a key" — and the user could not get back to keyless.
        var store = Store();

        var stored = store.Load();
        stored.SetKeys(BrokerKind.Binance, "k", "s", null);
        store.Save(stored);

        stored = store.Load();
        stored.ClearKeys(BrokerKind.Binance);
        store.Save(stored);

        Assert.False(Source(store).For(BrokerKind.Binance).IsConfigured);
    }

    [Fact]
    public void Each_brokers_credentials_stay_their_own()
    {
        // Five crypto venues share one key/secret shape. A map keyed by the wrong thing would hand
        // Bybit's secret to Binance, which fails as an invalid signature and points nowhere near here.
        var store = Store();
        var stored = store.Load();
        stored.SetKeys(BrokerKind.Binance, "binance-key", "binance-secret", null);
        stored.SetKeys(BrokerKind.Bybit, "bybit-key", "bybit-secret", null);
        store.Save(stored);

        var source = Source(store);

        Assert.Equal("binance-secret", source.For(BrokerKind.Binance).Secret);
        Assert.Equal("bybit-secret", source.For(BrokerKind.Bybit).Secret);
    }

    [Fact]
    public void A_key_pasted_after_the_first_read_is_picked_up_without_a_restart()
    {
        // The scenario this whole seam exists for: the application is running, the user opens the
        // login window and pastes a key. A source that captured the file once at startup would keep
        // reporting "no key" until the next launch — and the symptom would look like a rejected key.
        var store = Store();
        var source = Source(store);

        Assert.False(source.For(BrokerKind.Coinbase).IsConfigured);

        var stored = store.Load();
        stored.SetKeys(BrokerKind.Coinbase, "organizations/x/apiKeys/y", "-----BEGIN EC PRIVATE KEY-----", null);
        store.Save(stored);

        // The source holds a load for a couple of seconds so a polling client stays off the disk.
        // Waiting that out is the honest way to prove the value is re-read rather than cached forever.
        System.Threading.Thread.Sleep(StoredBrokerCredentials.Freshness + TimeSpan.FromMilliseconds(250));

        Assert.True(source.For(BrokerKind.Coinbase).IsConfigured);
    }

    [Fact]
    public void Every_broker_kind_can_be_asked_about_without_error()
    {
        // A client is free to ask for its own kind whatever the catalogue says. None of the thirty-odd
        // values may throw — an unrecognised broker is "nothing stored", not an exception at connect.
        var source = Source(Store());

        foreach (var broker in Enum.GetValues<BrokerKind>())
        {
            var credential = source.For(broker);
            Assert.False(credential.IsConfigured);
        }
    }

    // ── Login rows, driven through Connect ──────────────────────────────────────────────────────
    // Here rather than in their own class because a form saves through the store, and the store's
    // directory override is process-wide: two classes setting it in parallel would write into each
    // other's directories.

    [Fact]
    public async Task A_keyed_row_takes_over_a_venue_the_keyless_row_connected()
    {
        // Auto Connect opens Binance keyless at startup. The keyed row then shows "Connected" too — one
        // client, one state — and with Connect disabled, a key typed into it went nowhere.
        var store = Store();
        var selector = new LiveStateSelector(BrokerKind.Binance);
        selector.Set(BrokerKind.Binance, ConnectionState.Connected);

        var options = new BinanceOptions();
        var form = new KeyedBinanceLoginFormViewModel(
            selector, store, Options.Create(options),
            NullLogger<KeyedBinanceLoginFormViewModel>.Instance, IBrokerCredentialVerifier.None);
        form.Initialize();

        Assert.Equal("Connected · no key", form.StatusText);
        Assert.Equal("Binance · Public data", form.GetSessionAccountLabel());
        Assert.False(form.ConnectCommand.CanExecute(null));

        form.ApiKey = "binance-key";
        form.ApiSecret = "binance-secret";
        Assert.True(form.ConnectCommand.CanExecute(null));

        await form.ConnectCommand.ExecuteAsync(null);

        Assert.Null(form.ErrorMessage);
        Assert.True(form.IsKeyInEffect);
        Assert.Equal("Connected", form.StatusText);
        Assert.Equal("Binance · API key", form.GetSessionAccountLabel());
        Assert.False(form.ConnectCommand.CanExecute(null));

        // Taken over in place: the feed is the same public stream, so nothing reconnected.
        Assert.False(selector.ConnectCalls.ContainsKey(BrokerKind.Binance));

        var saved = Source(store).For(BrokerKind.Binance);
        Assert.Equal("binance-key", saved.Key);
        Assert.Equal("binance-secret", saved.Secret);
    }

    [Fact]
    public async Task Tradier_has_its_token_in_the_store_when_the_client_connects()
    {
        // The client reads its token from the store and nowhere else. The form used to save only after
        // a successful connect, so the first connect found nothing — and never succeeded, and never saved.
        var store = Store();
        var source = Source(store);
        var selector = new LiveStateSelector(BrokerKind.Tradier);
        var seenAtConnect = string.Empty;
        selector.OnConnect = kind => seenAtConnect = source.For(kind).Secret;

        var form = new TradierLoginFormViewModel(
            selector, store, Options.Create(new TradierOptions()),
            NullLogger<TradierLoginFormViewModel>.Instance);
        form.Initialize();
        form.Token = "tradier-token";

        await form.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("tradier-token", seenAtConnect);
        Assert.True(form.IsConnected);
    }

    [Fact]
    public async Task Oanda_has_its_token_in_the_store_when_the_client_connects()
    {
        var store = Store();
        var source = Source(store);
        var selector = new LiveStateSelector(BrokerKind.Oanda);
        var seenAtConnect = BrokerCredential.None;
        selector.OnConnect = kind => seenAtConnect = source.For(kind);

        var form = new OandaLoginFormViewModel(
            selector, store, Options.Create(new OandaOptions()),
            NullLogger<OandaLoginFormViewModel>.Instance);
        form.Initialize();
        form.AccountId = "101-001-1234567-001";
        form.Token = "oanda-token";

        await form.ConnectCommand.ExecuteAsync(null);

        Assert.Equal("oanda-token", seenAtConnect.Secret);
        Assert.Equal("101-001-1234567-001", seenAtConnect.Key);
    }

    // ── Sign-in brokers (2026-09-25) ───────────────────────────────────────────────────────────

    /// <summary>An issuer that hands back a fixed session and remembers what it was given.</summary>
    private sealed class FakeIssuer(SessionIssue answer) : IBrokerSessionIssuer
    {
        public (BrokerCredential App, string Proof)? Last { get; private set; }

        public bool Issues(BrokerKind broker) => true;

        public SignInStyle StyleOf(BrokerKind broker) => SignInStyle.Browser;

        public Task<string?> SignInUrlAsync(BrokerKind broker, BrokerCredential app, string redirectUri, CancellationToken ct = default) =>
            Task.FromResult<string?>($"https://broker.example/login?k={app.Key}");

        public Task<SessionIssue> SignInAsync(BrokerKind broker, BrokerCredential app, string proof, string redirectUri, CancellationToken ct = default)
        {
            Last = (app, proof);
            return Task.FromResult(answer);
        }
    }

    private static SessionBrokerLoginFormViewModel SessionRow(BrokerKind kind, CredentialStore store, IBrokerSessionIssuer issuer, IBrokerSelector? selector = null) =>
        new(SessionBrokerLogins.All.Single(b => b.Broker == kind), selector ?? new LiveStateSelector(kind), store, issuer,
            NullLogger<SessionBrokerLoginFormViewModel>.Instance);

    [Fact]
    public async Task A_sign_in_stores_the_session_where_the_client_reads_it_and_unlocks_connect()
    {
        var store = Store();
        var issuer = new FakeIssuer(SessionIssue.Issued("access-token-1", "AB1234"));
        var form = SessionRow(BrokerKind.Zerodha, store, issuer);
        form.Initialize();

        form.ApiKey = "kite-key";
        form.ApiSecret = "kite-secret";
        Assert.False(form.SignInCommand.CanExecute(null), "nothing to exchange until the redirected address is pasted");
        Assert.False(form.ConnectCommand.CanExecute(null), "no session yet");

        form.Proof = "https://127.0.0.1/?request_token=rt";
        await form.SignInCommand.ExecuteAsync(null);

        Assert.Equal("kite-key", issuer.Last!.Value.App.Key);
        Assert.Equal("https://127.0.0.1/?request_token=rt", issuer.Last!.Value.Proof);
        Assert.True(form.HasSession);
        Assert.True(form.ConnectCommand.CanExecute(null));
        Assert.Equal(string.Empty, form.Proof);

        var seen = Source(store).For(BrokerKind.Zerodha);
        Assert.Equal("kite-key", seen.Key);
        Assert.Equal("kite-secret", seen.Secret);
        Assert.Equal("access-token-1", seen.Session);
        Assert.Equal("AB1234", seen.Account);
    }

    [Fact]
    public async Task A_refused_sign_in_says_so_and_keeps_connect_locked()
    {
        var store = Store();
        var form = SessionRow(BrokerKind.Zerodha, store, new FakeIssuer(SessionIssue.Refused("Token is invalid or has expired.")));
        form.Initialize();
        form.ApiKey = "k";
        form.ApiSecret = "s";
        form.Proof = "request_token=old";

        await form.SignInCommand.ExecuteAsync(null);

        Assert.False(form.HasSession);
        Assert.Contains("Token is invalid or has expired.", form.SignInMessage);
        Assert.False(form.ConnectCommand.CanExecute(null));
    }

    [Fact]
    public void A_code_sign_in_can_run_from_a_stored_setup_key_without_a_typed_code()
    {
        var form = SessionRow(BrokerKind.AngelOne, Store(), new FakeIssuer(SessionIssue.Issued("s")));
        form.ApiKey = "smart-key";
        form.Account = "A123";
        form.ApiSecret = "1234";
        Assert.False(form.SignInCommand.CanExecute(null), "no code and no setup key");

        form.Passphrase = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";
        Assert.True(form.SignInCommand.CanExecute(null));
    }

    [Fact]
    public void A_credentials_sign_in_needs_nothing_but_its_fields()
    {
        var form = SessionRow(BrokerKind.AliceBlue, Store(), new FakeIssuer(SessionIssue.Issued("s")));
        Assert.False(form.SignInCommand.CanExecute(null));
        form.Account = "AB123";
        form.ApiSecret = "api-key";
        Assert.True(form.SignInCommand.CanExecute(null));
    }

    [Fact]
    public async Task An_old_session_is_flagged_as_probably_expired_on_load()
    {
        var store = Store();
        var stored = store.Load();
        stored.SetKeys(BrokerKind.Fyers, "ABCD-100", "secret", null);
        stored.SetSession(BrokerKind.Fyers, "", "", "old-token", DateTimeOffset.UtcNow.AddDays(-2));
        store.Save(stored);

        var form = SessionRow(BrokerKind.Fyers, store, new FakeIssuer(SessionIssue.Issued("s")));
        form.Initialize();

        Assert.True(form.HasSession);
        Assert.Contains("expired", form.SignInMessage);
        await Task.CompletedTask;
    }

    // ── Renewed sessions (2026-09-25) ──────────────────────────────────────────────────────────

    [Fact]
    public void A_renewed_session_replaces_the_stored_one_and_keeps_the_keys_and_account()
    {
        var store = Store();
        var stored = store.Load();
        stored.SetKeys(BrokerKind.Questrade, "", null, null);
        stored.SetSession(BrokerKind.Questrade, "acct-1", "", "session-1", DateTimeOffset.UtcNow.AddHours(-1));
        store.Save(stored);

        var source = Source(store);
        source.Renew(BrokerKind.Questrade, "session-2");

        var seen = Source(store).For(BrokerKind.Questrade);
        Assert.Equal("session-2", seen.Session);
        Assert.Equal("acct-1", seen.Account);
        Assert.Equal("session-2", source.For(BrokerKind.Questrade).Session);
    }

    [Fact]
    public void Saving_a_row_does_not_put_back_a_session_a_client_has_since_renewed()
    {
        var store = Store();
        var stored = store.Load();
        stored.SetKeys(BrokerKind.SaxoBank, "app-key", "app-secret", null);
        stored.SetSession(BrokerKind.SaxoBank, "", "", "rotated-away", DateTimeOffset.UtcNow);
        store.Save(stored);

        // The login window opens and loads the row…
        var form = SessionRow(BrokerKind.SaxoBank, store, new FakeIssuer(SessionIssue.Issued("s")));
        form.Initialize();

        // …a running client rotates the refresh token…
        Source(store).Renew(BrokerKind.SaxoBank, "current");

        // …and the user presses Connect, which saves the row.
        form.ApplyToOptions();

        Assert.Equal("current", Source(store).For(BrokerKind.SaxoBank).Session);
    }

    [Fact]
    public void A_new_key_does_not_inherit_the_old_keys_session()
    {
        var store = Store();
        var stored = store.Load();
        stored.SetKeys(BrokerKind.SaxoBank, "old-key", "secret", null);
        stored.SetSession(BrokerKind.SaxoBank, "", "", "old-session", DateTimeOffset.UtcNow);
        store.Save(stored);

        var form = SessionRow(BrokerKind.SaxoBank, store, new FakeIssuer(SessionIssue.Issued("s")));
        form.Initialize();
        Source(store).Renew(BrokerKind.SaxoBank, "renewed-for-old-key");
        form.ApiKey = "new-key";
        form.Save();

        Assert.NotEqual("renewed-for-old-key", Source(store).For(BrokerKind.SaxoBank).Session);
    }

    [Fact]
    public void Every_us_and_global_broker_has_a_row_a_tile_and_a_sign_in_style()
    {
        BrokerKind[] added =
        [
            BrokerKind.CharlesSchwab, BrokerKind.TradeStation, BrokerKind.Tastytrade, BrokerKind.ETrade, BrokerKind.Tradovate,
            BrokerKind.SaxoBank, BrokerKind.IgGroup, BrokerKind.Questrade, BrokerKind.RobinhoodCrypto,
        ];

        foreach (var kind in added)
        {
            var row = SessionBrokerLogins.All.Single(b => b.Broker == kind);
            Assert.True(row.Style == SignInStyle.Credentials || row.ProofLabel is not null, $"{kind} asks for what its sign-in needs");
            var form = SessionRow(kind, Store(), new FakeIssuer(SessionIssue.Issued("s")));
            Assert.NotEqual("?", form.Badge);
        }
    }
}
