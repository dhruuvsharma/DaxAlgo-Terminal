using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.Rsi;

/// <summary>A user-facing instrument label paired with the IB contract it resolves to.</summary>
public sealed record TradeableInstrument(
    string DisplayName,
    string Category,
    Contract Contract);

/// <summary>
/// Curated catalog of instruments the RSI strategy can stream. Covers ETFs, single-name
/// stocks, continuous futures across asset classes, major spot FX, and cash indices.
/// Options aren't included because they require strike/expiry/right that don't fit a
/// generic dropdown.
/// </summary>
internal static class InstrumentCatalog
{
    private const string CatStockEtf = "Index ETFs";
    private const string CatStockBlue = "US Stocks";
    private const string CatCommodityEtf = "Commodity ETFs";
    private const string CatFutEquity = "Equity Index Futures";
    private const string CatFutEnergy = "Energy Futures";
    private const string CatFutMetal = "Metals Futures";
    private const string CatFutAgri = "Agricultural Futures";
    private const string CatFutRates = "Interest-Rate Futures";
    private const string CatFutFx = "FX Futures";
    private const string CatForex = "Spot Forex";
    private const string CatIndex = "Cash Indices";

    public static IReadOnlyList<TradeableInstrument> All { get; } = new TradeableInstrument[]
    {
        // Index ETFs
        new("SPY  —  S&P 500 ETF",          CatStockEtf, Contract.UsStock("SPY", "ARCA")),
        new("QQQ  —  Nasdaq 100 ETF",       CatStockEtf, Contract.UsStock("QQQ", "NASDAQ")),
        new("IWM  —  Russell 2000 ETF",     CatStockEtf, Contract.UsStock("IWM", "ARCA")),
        new("DIA  —  Dow Jones ETF",        CatStockEtf, Contract.UsStock("DIA", "ARCA")),
        new("VTI  —  Total US Market ETF",  CatStockEtf, Contract.UsStock("VTI", "ARCA")),
        new("EFA  —  EAFE Developed ETF",   CatStockEtf, Contract.UsStock("EFA", "ARCA")),
        new("EEM  —  Emerging Markets ETF", CatStockEtf, Contract.UsStock("EEM", "ARCA")),

        // US Single-Name Stocks
        new("AAPL  —  Apple",              CatStockBlue, Contract.UsStock("AAPL")),
        new("MSFT  —  Microsoft",          CatStockBlue, Contract.UsStock("MSFT")),
        new("NVDA  —  NVIDIA",             CatStockBlue, Contract.UsStock("NVDA")),
        new("TSLA  —  Tesla",              CatStockBlue, Contract.UsStock("TSLA")),
        new("AMZN  —  Amazon",             CatStockBlue, Contract.UsStock("AMZN")),
        new("META  —  Meta Platforms",     CatStockBlue, Contract.UsStock("META")),
        new("GOOGL —  Alphabet (Class A)", CatStockBlue, Contract.UsStock("GOOGL")),
        new("AMD   —  Advanced Micro",     CatStockBlue, Contract.UsStock("AMD")),
        new("NFLX  —  Netflix",            CatStockBlue, Contract.UsStock("NFLX")),
        new("JPM   —  JPMorgan Chase",     CatStockBlue, Contract.UsStock("JPM", "NYSE")),
        new("XOM   —  Exxon Mobil",        CatStockBlue, Contract.UsStock("XOM", "NYSE")),
        new("BRK B —  Berkshire Hathaway", CatStockBlue, Contract.UsStock("BRK B", "NYSE")),

        // Commodity / Sector ETFs
        new("GLD  —  Gold ETF",           CatCommodityEtf, Contract.UsStock("GLD", "ARCA")),
        new("SLV  —  Silver ETF",         CatCommodityEtf, Contract.UsStock("SLV", "ARCA")),
        new("USO  —  Crude Oil ETF",      CatCommodityEtf, Contract.UsStock("USO", "ARCA")),
        new("UNG  —  Natural Gas ETF",    CatCommodityEtf, Contract.UsStock("UNG", "ARCA")),
        new("DBA  —  Agriculture ETF",    CatCommodityEtf, Contract.UsStock("DBA", "ARCA")),
        new("DBB  —  Base Metals ETF",    CatCommodityEtf, Contract.UsStock("DBB", "ARCA")),
        new("XLE  —  Energy Sector ETF",  CatCommodityEtf, Contract.UsStock("XLE", "ARCA")),
        new("XLF  —  Financials ETF",     CatCommodityEtf, Contract.UsStock("XLF", "ARCA")),
        new("XLK  —  Technology ETF",     CatCommodityEtf, Contract.UsStock("XLK", "ARCA")),

        // Equity Index Futures (continuous)
        new("ES  —  E-mini S&P 500",     CatFutEquity, ContFut("ES", "CME")),
        new("NQ  —  E-mini Nasdaq 100",  CatFutEquity, ContFut("NQ", "CME")),
        new("YM  —  E-mini Dow",         CatFutEquity, ContFut("YM", "CBOT")),
        new("RTY —  E-mini Russell 2000",CatFutEquity, ContFut("RTY","CME")),
        new("MES —  Micro E-mini S&P",   CatFutEquity, ContFut("MES","CME")),
        new("MNQ —  Micro E-mini Nasdaq",CatFutEquity, ContFut("MNQ","CME")),

        // Energy Futures (continuous)
        new("CL  —  Crude Oil (WTI)",    CatFutEnergy, ContFut("CL", "NYMEX")),
        new("NG  —  Natural Gas",        CatFutEnergy, ContFut("NG", "NYMEX")),
        new("RB  —  RBOB Gasoline",      CatFutEnergy, ContFut("RB", "NYMEX")),
        new("HO  —  Heating Oil",        CatFutEnergy, ContFut("HO", "NYMEX")),
        new("BZ  —  Brent Crude",        CatFutEnergy, ContFut("BZ", "NYMEX")),

        // Metals Futures (continuous)
        new("GC  —  Gold",               CatFutMetal, ContFut("GC", "COMEX")),
        new("SI  —  Silver",             CatFutMetal, ContFut("SI", "COMEX")),
        new("HG  —  Copper",             CatFutMetal, ContFut("HG", "COMEX")),
        new("PL  —  Platinum",           CatFutMetal, ContFut("PL", "NYMEX")),
        new("PA  —  Palladium",          CatFutMetal, ContFut("PA", "NYMEX")),

        // Agricultural Futures (continuous)
        new("ZC  —  Corn",               CatFutAgri, ContFut("ZC", "CBOT")),
        new("ZW  —  Wheat",              CatFutAgri, ContFut("ZW", "CBOT")),
        new("ZS  —  Soybeans",           CatFutAgri, ContFut("ZS", "CBOT")),
        new("ZL  —  Soybean Oil",        CatFutAgri, ContFut("ZL", "CBOT")),
        new("KC  —  Coffee",             CatFutAgri, ContFut("KC", "NYBOT")),
        new("SB  —  Sugar #11",          CatFutAgri, ContFut("SB", "NYBOT")),
        new("CC  —  Cocoa",              CatFutAgri, ContFut("CC", "NYBOT")),
        new("CT  —  Cotton",             CatFutAgri, ContFut("CT", "NYBOT")),

        // Interest-Rate Futures (continuous)
        new("ZB  —  30-Yr Treasury Bond",CatFutRates, ContFut("ZB", "CBOT")),
        new("ZN  —  10-Yr Treasury Note",CatFutRates, ContFut("ZN", "CBOT")),
        new("ZF  —  5-Yr Treasury Note", CatFutRates, ContFut("ZF", "CBOT")),
        new("ZT  —  2-Yr Treasury Note", CatFutRates, ContFut("ZT", "CBOT")),

        // FX Futures (continuous)
        new("6E  —  Euro FX",            CatFutFx, ContFut("6E", "CME")),
        new("6J  —  Japanese Yen",       CatFutFx, ContFut("6J", "CME")),
        new("6B  —  British Pound",      CatFutFx, ContFut("6B", "CME")),
        new("6A  —  Australian Dollar",  CatFutFx, ContFut("6A", "CME")),
        new("6C  —  Canadian Dollar",    CatFutFx, ContFut("6C", "CME")),
        new("6S  —  Swiss Franc",        CatFutFx, ContFut("6S", "CME")),

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

        // Cash Indices
        new("SPX —  S&P 500 Index",       CatIndex, Index("SPX",  "CBOE")),
        new("NDX —  Nasdaq 100 Index",    CatIndex, Index("NDX",  "NASDAQ")),
        new("DJI —  Dow Jones Industrial",CatIndex, Index("INDU", "CME")),
        new("RUT —  Russell 2000 Index",  CatIndex, Index("RUT",  "RUSSELL")),
        new("VIX —  Volatility Index",    CatIndex, Index("VIX",  "CBOE")),
    };

    private static Contract ContFut(string symbol, string exchange) =>
        new(symbol, "CONTFUT", exchange, "USD", PrimaryExchange: string.Empty);

    private static Contract Cash(string baseCcy, string quoteCcy) =>
        new(baseCcy, "CASH", "IDEALPRO", quoteCcy, PrimaryExchange: string.Empty);

    private static Contract Index(string symbol, string exchange) =>
        new(symbol, "IND", exchange, "USD", PrimaryExchange: string.Empty);
}
