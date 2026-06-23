using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

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
/// Curated catalog of instruments the live signal generator can stream. Trimmed to one
/// representative ticker per asset class — users edit this file to add their own. (The
/// RSI strategy has a wider catalog of its own; intentionally not shared so each
/// strategy assembly stays self-contained.)
/// </summary>
public static class SignalInstrumentCatalog
{
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

    // The single global instrument universe, shared by every strategy window and the App tools
    // (regime / Markov / recorder) via SignalInstrument. Users edit this one list to add symbols.
    public static IReadOnlyList<SignalInstrument> All { get; } = new SignalInstrument[]
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
