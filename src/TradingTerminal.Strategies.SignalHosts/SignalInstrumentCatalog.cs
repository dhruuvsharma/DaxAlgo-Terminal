using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.SignalHosts;

/// <summary>User-facing instrument label paired with the broker contract it resolves to.</summary>
public sealed record SignalInstrument(string DisplayName, string Category, Contract Contract);

/// <summary>
/// Curated catalog of instruments the live signal generator can stream. Trimmed to one
/// representative ticker per asset class — users edit this file to add their own. (The
/// RSI strategy has a wider catalog of its own; intentionally not shared so each
/// strategy assembly stays self-contained.)
/// </summary>
public static class SignalInstrumentCatalog
{
    private const string CatEtf = "Index ETFs";
    private const string CatStock = "US Stocks";
    private const string CatFut = "Futures (continuous)";
    private const string CatForex = "Spot Forex";

    public static IReadOnlyList<SignalInstrument> All { get; } = new SignalInstrument[]
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

    private static Contract ContFut(string symbol, string exchange) =>
        new(symbol, "CONTFUT", exchange, "USD", PrimaryExchange: string.Empty);

    private static Contract Cash(string baseCcy, string quoteCcy) =>
        new(baseCcy, "CASH", "IDEALPRO", quoteCcy, PrimaryExchange: string.Empty);
}
