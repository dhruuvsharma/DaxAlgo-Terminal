using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Configuration;

namespace TradingTerminal.App.Login.Forms;

/// <summary>
/// One public crypto venue's two login rows, described rather than hand-written.
///
/// <para>The first six dual-mode venues each have a keyless and a keyed form class, twelve files that
/// differ in a name and a few labels. The twelve venues added on 2026-09-25 would have made that
/// thirty-six; instead each is one entry in <see cref="PublicVenueLogins.All"/>, and two classes —
/// <see cref="KeylessVenueLoginFormViewModel"/> and <see cref="KeyedVenueLoginFormViewModel"/> — read it.</para>
/// </summary>
/// <param name="Broker">The venue's kind, shared by both rows: one venue, one client, one market.</param>
/// <param name="Name">The venue's name as users know it.</param>
/// <param name="Credentials">The venue's options slot for a key, resolved from the container.</param>
/// <param name="WhereToGetAKey">How to create a read-only key there, and the venue's trap if it has one.</param>
public sealed record PublicVenueLogin(
    BrokerKind Broker,
    string Name,
    Func<IServiceProvider, CryptoApiCredentials> Credentials,
    string WhereToGetAKey)
{
    public bool UsesPassphrase { get; init; }

    public string KeyLabel { get; init; } = "API key";

    public string SecretLabel { get; init; } = "API secret";

    public string WhatAKeyBuys { get; init; } = "account balances, private streams";
}

/// <summary>The twelve venues whose login rows are described here, and their registration.</summary>
public static class PublicVenueLogins
{
    private static Func<IServiceProvider, CryptoApiCredentials> Slot<TOptions>() where TOptions : CryptoVenueOptions =>
        sp => sp.GetRequiredService<IOptions<TOptions>>().Value.Credentials;

    public static IReadOnlyList<PublicVenueLogin> All { get; } =
    [
        new(BrokerKind.Bitget, "Bitget", Slot<BitgetOptions>(),
            "Create an API key with read-only permission. The passphrase is the one you set when creating "
            + "the key — not your account password.")
        {
            UsesPassphrase = true,
            SecretLabel = "Secret key",
        },

        new(BrokerKind.KuCoin, "KuCoin", Slot<KuCoinOptions>(),
            "Create an API key with the General (read) permission only. The passphrase is the one you set "
            + "for this key; KuCoin cannot show it again.")
        {
            UsesPassphrase = true,
        },

        new(BrokerKind.GateIo, "Gate.io", Slot<GateIoOptions>(),
            "Create an APIv4 key with read-only spot permission.")
        {
            SecretLabel = "Secret",
        },

        new(BrokerKind.Gemini, "Gemini", Slot<GeminiOptions>(),
            "Create an API key with the Auditor role, which is read-only. Gemini shows the secret once.")
        {
            WhatAKeyBuys = "balances, private order events",
        },

        new(BrokerKind.CryptoCom, "Crypto.com", Slot<CryptoComOptions>(),
            "Create an Exchange API key with read-only access (not a Crypto.com App key).")
        {
            SecretLabel = "Secret key",
        },

        new(BrokerKind.Upbit, "Upbit", Slot<UpbitOptions>(),
            "Create an Open API key with only the view-accounts permission, and allow this machine's IP "
            + "address — Upbit refuses a key used from an address it was not issued for.")
        {
            KeyLabel = "Access key",
            SecretLabel = "Secret key",
        },

        new(BrokerKind.Bithumb, "Bithumb", Slot<BithumbOptions>(),
            "Create an API 2.0 key with only the view-assets permission. If the key is limited to certain "
            + "IP addresses, include this machine's.")
        {
            KeyLabel = "Access key",
            SecretLabel = "Secret key",
        },

        new(BrokerKind.Bitfinex, "Bitfinex", Slot<BitfinexOptions>(),
            "Create an API key with read access to Wallets and nothing else.")
        {
            SecretLabel = "API key secret",
        },

        new(BrokerKind.Bitstamp, "Bitstamp", Slot<BitstampOptions>(),
            "Create an API key with only the account-balance permission, then activate it from the email "
            + "Bitstamp sends — a key that was never activated is refused like a wrong one."),

        new(BrokerKind.Bitvavo, "Bitvavo", Slot<BitvavoOptions>(),
            "Create an API key with the View permission only.")
        {
            WhatAKeyBuys = "balances, private order events",
        },

        new(BrokerKind.Htx, "HTX", Slot<HtxOptions>(),
            "Create an API key with read-only permission. A key not bound to an IP address expires after "
            + "90 days.")
        {
            KeyLabel = "Access key",
            SecretLabel = "Secret key",
        },

        new(BrokerKind.Mexc, "MEXC", Slot<MexcOptions>(),
            "Create an API key with only the view-account-details permission.")
        {
            KeyLabel = "Access key",
            SecretLabel = "Secret key",
        },
    ];

    /// <summary>Registers both rows of every described venue — the keyless one and the keyed one.</summary>
    public static IServiceCollection AddPublicVenueLogins(this IServiceCollection services)
    {
        foreach (var venue in All)
        {
            services.AddSingleton<IBrokerLoginForm>(sp => new KeylessVenueLoginFormViewModel(
                venue, sp.GetRequiredService<IBrokerSelector>(), venue.Credentials(sp),
                sp.GetRequiredService<ILogger<KeylessVenueLoginFormViewModel>>()));

            services.AddSingleton<IBrokerLoginForm>(sp => new KeyedVenueLoginFormViewModel(
                venue, sp.GetRequiredService<IBrokerSelector>(), sp.GetRequiredService<CredentialStore>(),
                venue.Credentials(sp), sp.GetRequiredService<ILogger<KeyedVenueLoginFormViewModel>>(),
                sp.GetRequiredService<IBrokerCredentialVerifier>()));
        }

        return services;
    }
}

/// <summary>The keyless row of a described venue: public data, nothing to fill in.</summary>
public sealed class KeylessVenueLoginFormViewModel : BrokerLoginFormBase
{
    private readonly PublicVenueLogin _venue;
    private readonly CryptoApiCredentials _credentials;

    public KeylessVenueLoginFormViewModel(
        PublicVenueLogin venue, IBrokerSelector selector, CryptoApiCredentials credentials,
        ILogger<KeylessVenueLoginFormViewModel> logger)
        : base(selector, logger)
    {
        _venue = venue;
        _credentials = credentials;
    }

    public override BrokerKind Broker => _venue.Broker;

    public override string DisplayName => $"{_venue.Name} (no login)";

    public override bool CanSubmit => true;

    /// <summary>Drops any key the keyed row left, so this row means keyless.</summary>
    public override void ApplyToOptions() => _credentials.Clear();

    public override string GetSessionAccountLabel() => $"{_venue.Name} · Public data";

    public override string GetTimeoutErrorMessage() =>
        $"Connection timed out reaching {_venue.Name}. Check your internet connection.";

    public override string GetFailureMessage() =>
        $"Couldn't reach {_venue.Name} public market data. If the venue is blocked where you are, its hosts "
        + $"can be changed in the {_venue.Broker} section of appsettings.json.";

    public override void Load() { }

    public override void Save() { }
}

/// <summary>The keyed row of a described venue.</summary>
public sealed class KeyedVenueLoginFormViewModel : KeyedCryptoLoginFormBase
{
    private readonly PublicVenueLogin _venue;
    private readonly CryptoApiCredentials _credentials;

    public KeyedVenueLoginFormViewModel(
        PublicVenueLogin venue, IBrokerSelector selector, CredentialStore credentials,
        CryptoApiCredentials target, ILogger<KeyedVenueLoginFormViewModel> logger,
        IBrokerCredentialVerifier verifier)
        : base(selector, credentials, logger, verifier)
    {
        _venue = venue;
        _credentials = target;
    }

    public override BrokerKind Broker => _venue.Broker;

    protected override CryptoApiCredentials Target => _credentials;

    protected override string VenueName => _venue.Name;

    public override bool UsesPassphrase => _venue.UsesPassphrase;

    public override string KeyLabel => _venue.KeyLabel;

    public override string SecretLabel => _venue.SecretLabel;

    public override string WhereToGetAKey => _venue.WhereToGetAKey;

    protected override string WhatAKeyBuys => _venue.WhatAKeyBuys;
}
