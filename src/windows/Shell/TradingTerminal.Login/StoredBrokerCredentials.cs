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
/// </summary>
public sealed class StoredBrokerCredentials : IBrokerCredentialSource
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
            var record = Current().KeysFor(broker);

            // ApiSecret and Passphrase decrypt on read; a record written under a different Windows
            // account decrypts to null rather than throwing, which is why the null-coalescing is here
            // and not an argument that it cannot happen.
            return new BrokerCredential(
                Key: record.ApiKey ?? string.Empty,
                Secret: record.ApiSecret ?? string.Empty,
                Passphrase: record.Passphrase ?? string.Empty);
        }
        catch (Exception ex)
        {
            // A broken store must not take a broker client down with it. Report nothing configured,
            // loudly — the client then says "needs a key", which is the truth.
            _logger.LogWarning(ex, "Could not read stored credentials for {Broker}.", broker);
            return BrokerCredential.None;
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
