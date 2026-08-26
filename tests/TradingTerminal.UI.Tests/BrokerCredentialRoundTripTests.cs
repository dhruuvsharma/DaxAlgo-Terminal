using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using TradingTerminal.App.Login;
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
}
