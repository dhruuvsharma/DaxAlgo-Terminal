using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.UI;

/// <summary>
/// User-facing instrument label paired with the broker contract it resolves to AND the
/// source <see cref="Broker"/> that supplied it. The Broker field drives subscription
/// routing in multi-broker setups — the strategy host passes (Contract, Broker) to the
/// repository so the right backend is queried for ticks, bars, and depth. When the row
/// comes from the static fallback catalog (no broker connected yet), <see cref="Broker"/>
/// is null and the host resolves it lazily at Start to whichever broker is connected.
/// </summary>
public sealed record SignalInstrument(string DisplayName, string Category, Contract Contract, BrokerKind? Broker = null);

/// <summary>
/// The single global instrument universe shared by every picker (strategies, tools, charts).
///
/// <para><see cref="All"/> resolves through <see cref="Source"/> — the app wires that once at
/// startup (after the canonical <see cref="IInstrumentRegistry"/> is loaded) to
/// <see cref="FromRegistry"/>, so every dropdown shows the real discovered universe (persisted +
/// per-broker discovery).</para>
///
/// <para><b>There is no hardcoded fallback.</b> A curated list of 44 tickers used to stand in when
/// nothing was wired; it was removed on 2026-08-16 because it offered instruments the connected
/// broker may not carry, and picking one produced a subscription that could never resolve. An empty
/// dropdown before a broker connects is the honest answer — the pickers reload on
/// <see cref="IBrokerSelector.StateChanged"/>, so the list fills in as brokers come up.</para>
///
/// <para>This mirrors the <c>UiThread.Marshal</c> / <c>InMemoryLogSink.UiPost</c> startup-hook
/// pattern: UI.Core stays WPF-/host-free and the composition root injects the live behaviour.</para>
/// </summary>
public static class SignalInstrumentCatalog
{
    /// <summary>App sets this once at startup to a registry-backed provider. Read on every
    /// <see cref="All"/> access so the picker reflects instruments discovered after launch (as
    /// brokers connect). Null until the composition root wires it.</summary>
    public static Func<IReadOnlyList<SignalInstrument>>? Source { get; set; }

    /// <summary>The live instrument universe, or empty when no broker has contributed one yet.</summary>
    public static IReadOnlyList<SignalInstrument> All => Source?.Invoke() ?? [];

    /// <summary>Builds picker rows from the canonical instrument registry (broker-agnostic: each row's
    /// <c>Broker</c> is null and the host resolves it at Start to a connected broker). Used to wire
    /// <see cref="Source"/> at startup, and by the live-strategy base directly.</summary>
    public static IReadOnlyList<SignalInstrument> FromRegistry(IInstrumentRegistry registry) =>
        registry.All()
            .Select(i => new SignalInstrument(
                i.CanonicalSymbol,
                i.AssetClass.ToString(),
                new Contract(i.CanonicalSymbol, SecTypeFor(i.AssetClass), i.Exchange, i.Currency, i.Exchange),
                Broker: null))
            .ToList();

    private static string SecTypeFor(AssetClass assetClass) => assetClass switch
    {
        AssetClass.Future => "FUT",
        AssetClass.Forex => "CASH",
        AssetClass.Crypto => "CRYPTO",
        AssetClass.Option => "OPT",
        AssetClass.Index => "IND",
        _ => "STK",
    };
}
