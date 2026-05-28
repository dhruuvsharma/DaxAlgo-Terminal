using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;

namespace TradingTerminal.Strategies.OrderFlowSurfaceSpike;

public sealed record TradeableInstrument(
    string DisplayName,
    string Category,
    Contract Contract,
    BrokerKind? Broker = null);

/// <summary>
/// Static fallback catalog used until the connected broker's list resolves. Focused on
/// instruments that have a meaningful centralized trade tape under typical IB market-data
/// subscriptions (US equities, ETFs, equity-index futures).
/// </summary>
internal static class InstrumentCatalog
{
    private const string CatEtf = "Index ETFs";
    private const string CatStock = "US Stocks";
    private const string CatFutEquity = "Equity Index Futures";

    public static IReadOnlyList<TradeableInstrument> All { get; } = new TradeableInstrument[]
    {
        new("SPY  —  S&P 500 ETF",           CatEtf,    Contract.UsStock("SPY", "ARCA")),
        new("QQQ  —  Nasdaq-100 ETF",        CatEtf,    Contract.UsStock("QQQ", "NASDAQ")),
        new("IWM  —  Russell 2000 ETF",      CatEtf,    Contract.UsStock("IWM", "ARCA")),
        new("DIA  —  Dow Jones ETF",         CatEtf,    Contract.UsStock("DIA", "ARCA")),

        new("AAPL —  Apple",                 CatStock,  Contract.UsStock("AAPL", "NASDAQ")),
        new("MSFT —  Microsoft",             CatStock,  Contract.UsStock("MSFT", "NASDAQ")),
        new("NVDA —  NVIDIA",                CatStock,  Contract.UsStock("NVDA", "NASDAQ")),
        new("AMZN —  Amazon",                CatStock,  Contract.UsStock("AMZN", "NASDAQ")),
        new("TSLA —  Tesla",                 CatStock,  Contract.UsStock("TSLA", "NASDAQ")),

        new("ES   —  E-mini S&P 500",        CatFutEquity, ContFut("ES",  "CME")),
        new("NQ   —  E-mini Nasdaq 100",     CatFutEquity, ContFut("NQ",  "CME")),
        new("RTY  —  E-mini Russell 2000",   CatFutEquity, ContFut("RTY", "CME")),
        new("YM   —  E-mini Dow",            CatFutEquity, ContFut("YM",  "CBOT")),
        new("MES  —  Micro E-mini S&P",      CatFutEquity, ContFut("MES", "CME")),
        new("MNQ  —  Micro E-mini Nasdaq",   CatFutEquity, ContFut("MNQ", "CME")),
    };

    private static Contract ContFut(string symbol, string exchange) =>
        new(symbol, "CONTFUT", exchange, "USD", PrimaryExchange: string.Empty);
}
