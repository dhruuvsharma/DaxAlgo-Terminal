using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;

namespace TradingTerminal.App.Login;

/// <summary>
/// The one implementation of <see cref="IBrokerCredentialSource"/>: it reads whatever the login forms
/// wrote into the DPAPI credential store.
///
/// <para><b>Registered once, serves every broker.</b> A new adapter needs no wiring here — it asks for
/// its own <see cref="BrokerKind"/> and gets whatever its login form saved. That is the point of having
/// a single seam instead of an interface per venue: the number of places a credential path can be left
/// unconnected stays at one, no matter how many brokers the catalogue grows to.</para>
///
/// <para><b>Why it re-reads rather than caching for the session.</b> A user pastes a key into the login
/// window while the application is already running; if the source had captured the file at startup, the
/// key would take effect only after a restart — and the failure would look like a rejected key rather
/// than a stale read. So it reloads, and holds the result for <see cref="Freshness"/> to keep a polling
/// client off the disk. Two seconds of staleness on a credential is invisible; a whole session of it is
/// a support ticket.</para>
///
/// <para><b>It also takes renewed sessions back</b> (<see cref="IBrokerSessionStore"/>): a broker that rotates
/// its refresh token on every use leaves the store holding a dead one unless the client writes the new one
/// here.</para>
/// </summary>
public sealed class StoredBrokerCredentials : IBrokerCredentialSource, IBrokerSessionStore
{
    /// <summary>How long a load is reused. Short enough that pasting a key feels immediate, long enough
    /// that a two-second quote poll does not re-read and re-decrypt the file on every tick.</summary>
    internal static readonly TimeSpan Freshness = TimeSpan.FromSeconds(2);

    private readonly CredentialStore _store;
    private readonly ILogger<StoredBrokerCredentials> _logger;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;

    private StoredCredentials? _cached;
    private long _loadedAt;

    public StoredBrokerCredentials(
        CredentialStore store, ILogger<StoredBrokerCredentials> logger, TimeProvider? time = null)
    {
        _store = store;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _loadedAt = long.MinValue;
    }

    public BrokerCredential For(BrokerKind broker)
    {
        try
        {
            var current = Current();

            // Ironbeam and Upstox predate the per-broker map and keep their own named fields. Read them, so
            // their order routes and the execution console see what their logins store (2026-09-25). For
            // Ironbeam, Extra says which gateway the username belongs to; Upstox's daily access token is its
            // session, as it is for the other Indian brokers.
            if (broker == BrokerKind.IronBeam && !current.BrokerKeys.ContainsKey(broker.ToString()))
            {
                return new BrokerCredential(current.IronBeamUsername ?? string.Empty, current.IronBeamApiKey ?? string.Empty)
                {
                    Extra = current.IronBeamIsLive ? "live" : "demo",
                };
            }

            // cTrader's row keeps the OAuth app, the access token and the ctid account in named fields too;
            // the execution console's cTrader route reads them from here (2026-09-25).
            if (broker == BrokerKind.CTrader && !current.BrokerKeys.ContainsKey(broker.ToString()))
            {
                return new BrokerCredential(current.CTraderClientId ?? string.Empty, current.CTraderClientSecret ?? string.Empty)
                {
                    Session = current.CTraderAccessToken ?? string.Empty,
                    Account = current.CTraderAccountId > 0
                        ? current.CTraderAccountId.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : string.Empty,
                    Extra = current.CTraderIsLive ? "live" : "demo",
                };
            }

            // Interactive Brokers signs in inside TWS or IB Gateway, so its row stores where TWS listens rather
            // than a key: the host as Key, and the port, the API client id and the paper/live choice in Extra. No
            // secret — there is none, and a fresh store still reads as nothing configured. The order route
            // connects there (2026-09-25).
            if (broker == BrokerKind.InteractiveBrokers && !current.BrokerKeys.ContainsKey(broker.ToString()))
            {
                return new BrokerCredential(current.Host ?? string.Empty, string.Empty)
                {
                    Extra = string.Join('|',
                        current.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        current.ClientId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        current.AccountType),
                };
            }

            if (broker == BrokerKind.Upstox && !current.BrokerKeys.ContainsKey(broker.ToString()))
            {
                return new BrokerCredential(current.UpstoxApiKey ?? string.Empty, current.UpstoxApiSecret ?? string.Empty)
                {
                    Session = current.UpstoxAccessToken ?? string.Empty,
                };
            }

            var record = current.KeysFor(broker);

            // ApiSecret and Passphrase decrypt on read; a record written under a different Windows
            // account decrypts to null rather than throwing, which is why the null-coalescing is here
            // and not an argument that it cannot happen.
            return new BrokerCredential(
                Key: record.ApiKey ?? string.Empty,
                Secret: record.ApiSecret ?? string.Empty,
                Passphrase: record.Passphrase ?? string.Empty)
            {
                Account = record.Account ?? string.Empty,
                Session = record.Session ?? string.Empty,
                Extra = record.Extra ?? string.Empty,
            };
        }
        catch (Exception ex)
        {
            // A broken store must not take a broker client down with it. Report nothing configured,
            // loudly — the client then says "needs a key", which is the truth.
            _logger.LogWarning(ex, "Could not read stored credentials for {Broker}.", broker);
            return BrokerCredential.None;
        }
    }

    public void Renew(BrokerKind broker, string session)
    {
        lock (_gate)
        {
            try
            {
                // Load fresh rather than from the cache: the write must not put back a key the user
                // changed in the last two seconds.
                var stored = _store.Load();
                var record = stored.KeysFor(broker);
                stored.SetSession(broker, record.Account ?? string.Empty, record.Extra ?? string.Empty, session, _time.GetUtcNow());
                _store.Save(stored);
                _cached = stored;
                _loadedAt = _time.GetTimestamp();
            }
            catch (Exception ex)
            {
                // The renewed session still works for this run; only the next start loses it.
                _logger.LogWarning(ex, "Could not store the renewed {Broker} session; the next start will need a sign-in.", broker);
            }
        }
    }

    private StoredCredentials Current()
    {
        lock (_gate)
        {
            var now = _time.GetTimestamp();
            if (_cached is not null && _time.GetElapsedTime(_loadedAt, now) < Freshness) return _cached;

            _cached = _store.Load();
            _loadedAt = now;
            return _cached;
        }
    }
}
