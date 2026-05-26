using TradingTerminal.Core.Brokers;
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
    public static IReadOnlyList<TradableInstrument> ForInteractiveBrokers { get; } = BuildFor(BrokerKind.InteractiveBrokers, includeEquities: true);

    /// <summary>Continuous futures only — what a default NinjaTrader Sim101 account trades.</summary>
    public static IReadOnlyList<TradableInstrument> Futures { get; } = BuildFor(BrokerKind.NinjaTrader, includeEquities: false);

    /// <summary>Builder used by the per-broker static lists above. <paramref name="broker"/> stamps
    /// every row's <see cref="TradableInstrument.Broker"/> so the dropdown can show "ES — IB" vs
    /// "ES — NinjaTrader" when the same ticker exists on both backends.</summary>
    public static IReadOnlyList<TradableInstrument> BuildFor(BrokerKind broker, bool includeEquities)
    {
        var list = new List<TradableInstrument>();
        if (includeEquities)
        {
            list.AddRange(new[]
            {
                new TradableInstrument("SPY  —  S&P 500 ETF",      CatEtf,   Contract.UsStock("SPY", "ARCA"),    broker),
                new TradableInstrument("QQQ  —  Nasdaq 100 ETF",   CatEtf,   Contract.UsStock("QQQ", "NASDAQ"),  broker),
                new TradableInstrument("IWM  —  Russell 2000 ETF", CatEtf,   Contract.UsStock("IWM", "ARCA"),    broker),
                new TradableInstrument("GLD  —  Gold ETF",         CatEtf,   Contract.UsStock("GLD", "ARCA"),    broker),
                new TradableInstrument("USO  —  Crude Oil ETF",    CatEtf,   Contract.UsStock("USO", "ARCA"),    broker),

                new TradableInstrument("AAPL  —  Apple",           CatStock, Contract.UsStock("AAPL"),           broker),
                new TradableInstrument("MSFT  —  Microsoft",       CatStock, Contract.UsStock("MSFT"),           broker),
                new TradableInstrument("NVDA  —  NVIDIA",          CatStock, Contract.UsStock("NVDA"),           broker),
                new TradableInstrument("TSLA  —  Tesla",           CatStock, Contract.UsStock("TSLA"),           broker),
                new TradableInstrument("META  —  Meta Platforms",  CatStock, Contract.UsStock("META"),           broker),
            });
        }

        list.AddRange(new[]
        {
            new TradableInstrument("ES  —  E-mini S&P 500",      CatFut,   ContFut("ES",  "CME"),     broker),
            new TradableInstrument("NQ  —  E-mini Nasdaq 100",   CatFut,   ContFut("NQ",  "CME"),     broker),
            new TradableInstrument("YM  —  E-mini Dow",          CatFut,   ContFut("YM",  "CBOT"),    broker),
            new TradableInstrument("RTY —  E-mini Russell 2000", CatFut,   ContFut("RTY", "CME"),     broker),
            new TradableInstrument("CL  —  Crude Oil (WTI)",     CatFut,   ContFut("CL",  "NYMEX"),   broker),
            new TradableInstrument("GC  —  Gold",                CatFut,   ContFut("GC",  "COMEX"),   broker),
            new TradableInstrument("MES —  Micro E-mini S&P",    CatFut,   ContFut("MES", "CME"),     broker),
            new TradableInstrument("MNQ —  Micro E-mini Nasdaq", CatFut,   ContFut("MNQ", "CME"),     broker),
        });

        if (includeEquities)
        {
            list.AddRange(new[]
            {
                new TradableInstrument("EUR.USD —  Euro / US Dollar",  CatForex, Cash("EUR", "USD"), broker),
                new TradableInstrument("GBP.USD —  Pound / US Dollar", CatForex, Cash("GBP", "USD"), broker),
                new TradableInstrument("USD.JPY —  US Dollar / Yen",   CatForex, Cash("USD", "JPY"), broker),
                new TradableInstrument("AUD.USD —  Aussie / USD",      CatForex, Cash("AUD", "USD"), broker),
                new TradableInstrument("USD.CAD —  USD / Loonie",      CatForex, Cash("USD", "CAD"), broker),
            });
        }

        return list;
    }

    private static Contract ContFut(string symbol, string exchange) =>
        new(symbol, "CONTFUT", exchange, "USD", PrimaryExchange: string.Empty);

    private static Contract Cash(string baseCcy, string quoteCcy) =>
        new(baseCcy, "CASH", "IDEALPRO", quoteCcy, PrimaryExchange: string.Empty);
}
