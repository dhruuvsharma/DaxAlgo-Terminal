using TradingTerminal.Core.Brokers;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Which brokers each <see cref="AppEdition"/> exposes, split by whether the broker needs
/// credentials. The <b>keyless</b> set (public crypto feeds + the in-process Simulated feed) needs
/// no API key or account, so it ships in every edition — including Basic, which never shows a
/// credential form. The <b>credentialed</b> set is added from Intermediate up.
/// </summary>
/// <remarks>
/// This is the single source of truth the shells and the login screen agree on. It carries no
/// registration logic (that lives in <c>Infrastructure.DependencyInjection</c>'s
/// <c>AddKeylessBrokers</c> / <c>AddCredentialedBrokers</c>) — just the allow-list.
/// </remarks>
public static class BrokerEditionPolicy
{
    /// <summary>Brokers usable with no API key or account. Available in every edition.</summary>
    public static readonly IReadOnlyList<BrokerKind> Keyless =
    [
        BrokerKind.Binance,
        BrokerKind.Coinbase,
        BrokerKind.Bybit,
        BrokerKind.Kraken,
        BrokerKind.Okx,
        BrokerKind.Simulated,
    ];

    /// <summary>Brokers that require credentials / a login form. Added from Intermediate up.</summary>
    public static readonly IReadOnlyList<BrokerKind> Credentialed =
    [
        BrokerKind.InteractiveBrokers,
        BrokerKind.NinjaTrader,
        BrokerKind.CTrader,
        BrokerKind.Alpaca,
        BrokerKind.IronBeam,
        BrokerKind.LondonStrategicEdge,
        BrokerKind.Upstox,
    ];

    /// <summary>The brokers a given edition exposes.</summary>
    public static IReadOnlyList<BrokerKind> BrokersFor(AppEdition edition) =>
        edition == AppEdition.Basic
            ? Keyless
            : [.. Keyless, .. Credentialed];

    /// <summary>Whether a broker needs credentials (drives the login screen's keyless-vs-form path).</summary>
    public static bool RequiresCredentials(BrokerKind broker) => Credentialed.Contains(broker);
}
