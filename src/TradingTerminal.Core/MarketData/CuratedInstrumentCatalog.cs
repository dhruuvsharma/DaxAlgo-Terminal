using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>
/// Curated instrument lists for brokers whose APIs can't cleanly enumerate a tradable
/// universe. Interactive Brokers' universe is millions of contracts behind a search API,
/// and NinjaTrader's NTDirect surface has no list call — so those clients return one of
/// these hand-picked sets from <c>ListInstrumentsAsync</c> instead of a live query.
///
/// Brokers that <em>can</em> enumerate (Alpaca, cTrader) ignore this and return live lists.
/// </summary>
public static class CuratedInstrumentCatalog
{
    private const string CatEtf = "Index ETFs";
    private const string CatStock = "US Stocks";
    private const string CatFut = "Futures (continuous)";
    private const string CatForex = "Spot Forex";

    /// <summary>ETFs, large-cap stocks, continuous futures and FX — a broad starter set for IB.</summary>
    public static IReadOnlyList<TradableInstrument> ForInteractiveBrokers { get; } = new TradableInstrument[]
    {
        new("SPY  —  S&P 500 ETF",          CatEtf,   Contract.UsStock("SPY", "ARCA")),
        new("QQQ  —  Nasdaq 100 ETF",       CatEtf,   Contract.UsStock("QQQ", "NASDAQ")),
        new("IWM  —  Russell 2000 ETF",     CatEtf,   Contract.UsStock("IWM", "ARCA")),
        new("GLD  —  Gold ETF",             CatEtf,   Contract.UsStock("GLD", "ARCA")),
        new("USO  —  Crude Oil ETF",        CatEtf,   Contract.UsStock("USO", "ARCA")),

        new("AAPL  —  Apple",               CatStock, Contract.UsStock("AAPL")),
        new("MSFT  —  Microsoft",           CatStock, Contract.UsStock("MSFT")),
        new("NVDA  —  NVIDIA",              CatStock, Contract.UsStock("NVDA")),
        new("TSLA  —  Tesla",               CatStock, Contract.UsStock("TSLA")),
        new("META  —  Meta Platforms",      CatStock, Contract.UsStock("META")),

        new("ES  —  E-mini S&P 500",        CatFut,   ContFut("ES",  "CME")),
        new("NQ  —  E-mini Nasdaq 100",     CatFut,   ContFut("NQ",  "CME")),
        new("CL  —  Crude Oil (WTI)",       CatFut,   ContFut("CL",  "NYMEX")),
        new("GC  —  Gold",                  CatFut,   ContFut("GC",  "COMEX")),
        new("MES —  Micro E-mini S&P",      CatFut,   ContFut("MES", "CME")),

        new("EUR.USD —  Euro / US Dollar",  CatForex, Cash("EUR", "USD")),
        new("GBP.USD —  Pound / US Dollar", CatForex, Cash("GBP", "USD")),
        new("USD.JPY —  US Dollar / Yen",   CatForex, Cash("USD", "JPY")),
        new("AUD.USD —  Aussie / USD",      CatForex, Cash("AUD", "USD")),
        new("USD.CAD —  USD / Loonie",      CatForex, Cash("USD", "CAD")),
    };

    /// <summary>Continuous futures only — what a default NinjaTrader Sim101 account trades.</summary>
    public static IReadOnlyList<TradableInstrument> Futures { get; } = new TradableInstrument[]
    {
        new("ES  —  E-mini S&P 500",        CatFut, ContFut("ES",  "CME")),
        new("NQ  —  E-mini Nasdaq 100",     CatFut, ContFut("NQ",  "CME")),
        new("YM  —  E-mini Dow",            CatFut, ContFut("YM",  "CBOT")),
        new("RTY —  E-mini Russell 2000",   CatFut, ContFut("RTY", "CME")),
        new("CL  —  Crude Oil (WTI)",       CatFut, ContFut("CL",  "NYMEX")),
        new("GC  —  Gold",                  CatFut, ContFut("GC",  "COMEX")),
        new("MES —  Micro E-mini S&P",      CatFut, ContFut("MES", "CME")),
        new("MNQ —  Micro E-mini Nasdaq",   CatFut, ContFut("MNQ", "CME")),
    };

    private static Contract ContFut(string symbol, string exchange) =>
        new(symbol, "CONTFUT", exchange, "USD", PrimaryExchange: string.Empty);

    private static Contract Cash(string baseCcy, string quoteCcy) =>
        new(baseCcy, "CASH", "IDEALPRO", quoteCcy, PrimaryExchange: string.Empty);
}
