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
/// per-broker discovery) rather than a hardcoded list. The curated <see cref="Fallback"/> below is
/// used only when no source is wired or the registry is genuinely empty (e.g. a fresh install with
/// no persisted instruments and no broker connected), so the app is never instrument-less.</para>
///
/// <para>This mirrors the <c>UiThread.Marshal</c> / <c>InMemoryLogSink.UiPost</c> startup-hook
/// pattern: UI.Core stays WPF-/host-free and the composition root injects the live behaviour.</para>
/// </summary>
public static class SignalInstrumentCatalog
{
    /// <summary>App sets this once at startup to a registry-backed provider. When null or empty the
    /// curated <see cref="Fallback"/> is used. Read on every <see cref="All"/> access so the picker
    /// reflects instruments discovered after launch (as brokers connect).</summary>
    public static Func<IReadOnlyList<SignalInstrument>>? Source { get; set; }

    /// <summary>The live instrument universe: the wired <see cref="Source"/> when it yields anything,
    /// otherwise the curated <see cref="Fallback"/>.</summary>
    public static IReadOnlyList<SignalInstrument> All =>
        Source?.Invoke() is { Count: > 0 } live ? live : Fallback;

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

    private const string CatForex = "Spot Forex";
    private const string CatFutEquity = "Equity Index Futures";
    private const string CatFutEnergy = "Energy Futures";
    private const string CatFutMetal = "Metals Futures";
    private const string CatFutAgri = "Agricultural Futures";
    private const string CatFutRates = "Interest-Rate Futures";
    private const string CatFutFx = "FX Futures";
    private const string CatStockEtf = "Index ETFs";
    private const string CatCommodityEtf = "Commodity ETFs";
    private const string CatStockBlue = "US Stocks";
    private const string CatIndex = "Cash Indices";

    // Curated last-resort fallback — used only when no registry source is wired or the registry is
    // empty (fresh install, no broker). One representative ticker per asset class.
    private static readonly IReadOnlyList<SignalInstrument> Fallback = new SignalInstrument[]
    {
        // Spot Forex
        new("EUR.USD —  Euro / US Dollar",       CatForex, Cash("EUR", "USD")),
        new("GBP.USD —  Pound / US Dollar",      CatForex, Cash("GBP", "USD")),
        new("USD.JPY —  US Dollar / Yen",        CatForex, Cash("USD", "JPY")),
        new("AUD.USD —  Aussie / US Dollar",     CatForex, Cash("AUD", "USD")),
        new("USD.CAD —  US Dollar / Loonie",     CatForex, Cash("USD", "CAD")),
        new("USD.CHF —  US Dollar / Swiss",      CatForex, Cash("USD", "CHF")),
        new("NZD.USD —  Kiwi / US Dollar",       CatForex, Cash("NZD", "USD")),
        new("EUR.GBP —  Euro / Pound",           CatForex, Cash("EUR", "GBP")),
        new("EUR.JPY —  Euro / Yen",             CatForex, Cash("EUR", "JPY")),

        // Equity Index Futures
        new("ES  —  E-mini S&P 500",      CatFutEquity, ContFut("ES",  "CME")),
        new("NQ  —  E-mini Nasdaq 100",   CatFutEquity, ContFut("NQ",  "CME")),
        new("YM  —  E-mini Dow",          CatFutEquity, ContFut("YM",  "CBOT")),
        new("RTY —  E-mini Russell 2000", CatFutEquity, ContFut("RTY", "CME")),
        new("MES —  Micro E-mini S&P",    CatFutEquity, ContFut("MES", "CME")),
        new("MNQ —  Micro E-mini Nasdaq", CatFutEquity, ContFut("MNQ", "CME")),

        // Index ETFs
        new("SPY  —  S&P 500 ETF",          CatStockEtf, Contract.UsStock("SPY", "ARCA")),
        new("QQQ  —  Nasdaq 100 ETF",       CatStockEtf, Contract.UsStock("QQQ", "NASDAQ")),
        new("IWM  —  Russell 2000 ETF",     CatStockEtf, Contract.UsStock("IWM", "ARCA")),
        new("DIA  —  Dow Jones ETF",        CatStockEtf, Contract.UsStock("DIA", "ARCA")),

        // Single-name stocks
        new("AAPL  —  Apple",              CatStockBlue, Contract.UsStock("AAPL")),
        new("MSFT  —  Microsoft",          CatStockBlue, Contract.UsStock("MSFT")),
        new("NVDA  —  NVIDIA",             CatStockBlue, Contract.UsStock("NVDA")),
        new("TSLA  —  Tesla",              CatStockBlue, Contract.UsStock("TSLA")),
        new("META  —  Meta Platforms",     CatStockBlue, Contract.UsStock("META")),

        // Commodity ETFs
        new("GLD  —  Gold ETF",           CatCommodityEtf, Contract.UsStock("GLD", "ARCA")),
        new("USO  —  Crude Oil ETF",      CatCommodityEtf, Contract.UsStock("USO", "ARCA")),

        // Energy / Metals futures
        new("CL  —  Crude Oil (WTI)",    CatFutEnergy, ContFut("CL", "NYMEX")),
        new("NG  —  Natural Gas",        CatFutEnergy, ContFut("NG", "NYMEX")),
        new("GC  —  Gold",               CatFutMetal,  ContFut("GC", "COMEX")),
        new("SI  —  Silver",             CatFutMetal,  ContFut("SI", "COMEX")),
        new("HG  —  Copper",             CatFutMetal,  ContFut("HG", "COMEX")),

        // Agri / Rates / FX futures
        new("ZC  —  Corn",                CatFutAgri,  ContFut("ZC", "CBOT")),
        new("ZS  —  Soybeans",            CatFutAgri,  ContFut("ZS", "CBOT")),
        new("ZB  —  30-Yr Treasury Bond", CatFutRates, ContFut("ZB", "CBOT")),
        new("ZN  —  10-Yr Treasury Note", CatFutRates, ContFut("ZN", "CBOT")),
        new("6E  —  Euro FX",             CatFutFx,    ContFut("6E", "CME")),
        new("6J  —  Japanese Yen",        CatFutFx,    ContFut("6J", "CME")),

        // Cash indices
        new("SPX —  S&P 500 Index",       CatIndex, Index("SPX", "CBOE")),
        new("NDX —  Nasdaq 100 Index",    CatIndex, Index("NDX", "NASDAQ")),
        new("VIX —  Volatility Index",    CatIndex, Index("VIX", "CBOE")),
    };

    private static Contract ContFut(string symbol, string exchange) =>
        new(symbol, "CONTFUT", exchange, "USD", PrimaryExchange: string.Empty);

    private static Contract Cash(string baseCcy, string quoteCcy) =>
        new(baseCcy, "CASH", "IDEALPRO", quoteCcy, PrimaryExchange: string.Empty);

    private static Contract Index(string symbol, string exchange) =>
        new(symbol, "IND", exchange, "USD", PrimaryExchange: string.Empty);
}
