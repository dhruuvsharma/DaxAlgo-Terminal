using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Infrastructure.Ib;

/// <summary>
/// Comprehensive hand-curated instrument catalog for Interactive Brokers. TWS API has no
/// "list all" endpoint — <c>reqMatchingSymbols</c> is search-only (max 16 hits per call) and
/// the full universe (~2.6M contracts) is not enumerable. This catalog covers what a typical
/// retail IB account can subscribe to without additional permissions: Dow 30, NASDAQ-100,
/// S&amp;P-500 top names, sector/commodity/bond/international ETFs, 28 spot FX pairs,
/// all equity-index/treasury/energy/metal/grain futures, and crypto futures.
///
/// <para>Users hitting symbols not in this list can still subscribe — strategies that resolve
/// by ticker will create the contract on demand via <see cref="Contract.UsStock"/> /
/// <c>ContFut</c> / <c>Cash</c>. The catalog drives the picker dropdown; it isn't an
/// allowlist.</para>
///
/// <para>Each entry stamps <see cref="TradableInstrument.Broker"/> with
/// <see cref="BrokerKind.InteractiveBrokers"/> so the multi-broker picker can group by source
/// when the same ticker exists on cTrader / Alpaca / NinjaTrader.</para>
/// </summary>
internal static class IbCuratedCatalog
{
    // Categories — kept short so the grouped dropdown headers fit.
    private const string CatDow = "Dow 30";
    private const string CatNasdaq = "NASDAQ-100";
    private const string CatSp500 = "S&P 500";
    private const string CatSectorEtf = "Sector ETFs";
    private const string CatBroadEtf = "Broad Market ETFs";
    private const string CatBondEtf = "Bond ETFs";
    private const string CatCommodityEtf = "Commodity ETFs";
    private const string CatLeveragedEtf = "Leveraged / Inverse ETFs";
    private const string CatVolEtf = "Volatility ETFs";
    private const string CatIntlEtf = "International ETFs";
    private const string CatEqIdxFut = "Equity Index Futures";
    private const string CatTreasFut = "Treasury Futures";
    private const string CatEnergyFut = "Energy Futures";
    private const string CatMetalFut = "Metal Futures";
    private const string CatAgFut = "Agricultural Futures";
    private const string CatFxFut = "Currency Futures";
    private const string CatCryptoFut = "Crypto Futures";
    private const string CatForexMajor = "FX Majors";
    private const string CatForexMinor = "FX Minors / Crosses";
    private const string CatForexExotic = "FX Exotics";

    public static IReadOnlyList<TradableInstrument> All { get; } = Build();

    private static IReadOnlyList<TradableInstrument> Build()
    {
        var list = new List<TradableInstrument>(500);
        list.AddRange(Dow30());
        list.AddRange(Nasdaq100());
        list.AddRange(Sp500Heavyweights());
        list.AddRange(SectorEtfs());
        list.AddRange(BroadMarketEtfs());
        list.AddRange(BondEtfs());
        list.AddRange(CommodityEtfs());
        list.AddRange(LeveragedAndInverseEtfs());
        list.AddRange(VolatilityEtfs());
        list.AddRange(InternationalEtfs());
        list.AddRange(EquityIndexFutures());
        list.AddRange(TreasuryFutures());
        list.AddRange(EnergyFutures());
        list.AddRange(MetalFutures());
        list.AddRange(AgriculturalFutures());
        list.AddRange(CurrencyFutures());
        list.AddRange(CryptoFutures());
        list.AddRange(ForexMajors());
        list.AddRange(ForexMinors());
        list.AddRange(ForexExotics());
        return list;
    }

    // ── Equity helpers ───────────────────────────────────────────────────────────────

    private static TradableInstrument Stk(string symbol, string name, string category, string exchange = "NASDAQ") =>
        new($"{symbol}  —  {name}", category, Contract.UsStock(symbol, exchange), BrokerKind.InteractiveBrokers);

    private static TradableInstrument Etf(string symbol, string name, string category, string exchange = "ARCA") =>
        Stk(symbol, name, category, exchange);

    // ── Futures helpers ──────────────────────────────────────────────────────────────

    private static Contract ContFut(string symbol, string exchange, string currency = "USD") =>
        new(symbol, "CONTFUT", exchange, currency, PrimaryExchange: string.Empty);

    private static TradableInstrument Fut(string symbol, string name, string category, string exchange, string currency = "USD") =>
        new($"{symbol}  —  {name}", category, ContFut(symbol, exchange, currency), BrokerKind.InteractiveBrokers);

    // ── FX helpers ───────────────────────────────────────────────────────────────────

    private static Contract Cash(string baseCcy, string quoteCcy) =>
        new(baseCcy, "CASH", "IDEALPRO", quoteCcy, PrimaryExchange: string.Empty);

    private static TradableInstrument Fx(string baseCcy, string quoteCcy, string name, string category) =>
        new($"{baseCcy}.{quoteCcy}  —  {name}", category, Cash(baseCcy, quoteCcy), BrokerKind.InteractiveBrokers);

    // ── Dow 30 ───────────────────────────────────────────────────────────────────────
    private static IEnumerable<TradableInstrument> Dow30() => new[]
    {
        Stk("AAPL",  "Apple",                        CatDow),
        Stk("AMGN",  "Amgen",                        CatDow),
        Stk("AMZN",  "Amazon",                       CatDow),
        Stk("AXP",   "American Express",             CatDow, "NYSE"),
        Stk("BA",    "Boeing",                       CatDow, "NYSE"),
        Stk("CAT",   "Caterpillar",                  CatDow, "NYSE"),
        Stk("CRM",   "Salesforce",                   CatDow, "NYSE"),
        Stk("CSCO",  "Cisco Systems",                CatDow),
        Stk("CVX",   "Chevron",                      CatDow, "NYSE"),
        Stk("DIS",   "Walt Disney",                  CatDow, "NYSE"),
        Stk("DOW",   "Dow Inc",                      CatDow, "NYSE"),
        Stk("GS",    "Goldman Sachs",                CatDow, "NYSE"),
        Stk("HD",    "Home Depot",                   CatDow, "NYSE"),
        Stk("HON",   "Honeywell",                    CatDow),
        Stk("IBM",   "International Business Mach.", CatDow, "NYSE"),
        Stk("JNJ",   "Johnson & Johnson",            CatDow, "NYSE"),
        Stk("JPM",   "JPMorgan Chase",               CatDow, "NYSE"),
        Stk("KO",    "Coca-Cola",                    CatDow, "NYSE"),
        Stk("MCD",   "McDonald's",                   CatDow, "NYSE"),
        Stk("MMM",   "3M",                           CatDow, "NYSE"),
        Stk("MRK",   "Merck",                        CatDow, "NYSE"),
        Stk("MSFT",  "Microsoft",                    CatDow),
        Stk("NKE",   "Nike",                         CatDow, "NYSE"),
        Stk("NVDA",  "NVIDIA",                       CatDow),
        Stk("PG",    "Procter & Gamble",             CatDow, "NYSE"),
        Stk("TRV",   "Travelers",                    CatDow, "NYSE"),
        Stk("UNH",   "UnitedHealth Group",           CatDow, "NYSE"),
        Stk("V",     "Visa",                         CatDow, "NYSE"),
        Stk("VZ",    "Verizon",                      CatDow, "NYSE"),
        Stk("WMT",   "Walmart",                      CatDow, "NYSE"),
    };

    // ── NASDAQ-100 (excluding overlap with Dow already listed) ───────────────────────
    private static IEnumerable<TradableInstrument> Nasdaq100() => new[]
    {
        Stk("ABNB",  "Airbnb",                       CatNasdaq),
        Stk("ADBE",  "Adobe",                        CatNasdaq),
        Stk("ADI",   "Analog Devices",               CatNasdaq),
        Stk("ADP",   "Automatic Data Processing",    CatNasdaq),
        Stk("ADSK",  "Autodesk",                     CatNasdaq),
        Stk("AEP",   "American Electric Power",      CatNasdaq),
        Stk("AMAT",  "Applied Materials",            CatNasdaq),
        Stk("AMD",   "Advanced Micro Devices",       CatNasdaq),
        Stk("ANSS",  "ANSYS",                        CatNasdaq),
        Stk("ARM",   "Arm Holdings",                 CatNasdaq),
        Stk("ASML",  "ASML Holding",                 CatNasdaq),
        Stk("AVGO",  "Broadcom",                     CatNasdaq),
        Stk("AZN",   "AstraZeneca",                  CatNasdaq),
        Stk("BIIB",  "Biogen",                       CatNasdaq),
        Stk("BKNG",  "Booking Holdings",             CatNasdaq),
        Stk("BKR",   "Baker Hughes",                 CatNasdaq),
        Stk("CDNS",  "Cadence Design Systems",       CatNasdaq),
        Stk("CDW",   "CDW Corp",                     CatNasdaq),
        Stk("CEG",   "Constellation Energy",         CatNasdaq),
        Stk("CHTR",  "Charter Communications",       CatNasdaq),
        Stk("CMCSA", "Comcast",                      CatNasdaq),
        Stk("COST",  "Costco",                       CatNasdaq),
        Stk("CPRT",  "Copart",                       CatNasdaq),
        Stk("CRWD",  "CrowdStrike",                  CatNasdaq),
        Stk("CSGP",  "CoStar Group",                 CatNasdaq),
        Stk("CSX",   "CSX Corp",                     CatNasdaq),
        Stk("CTAS",  "Cintas",                       CatNasdaq),
        Stk("CTSH",  "Cognizant",                    CatNasdaq),
        Stk("DASH",  "DoorDash",                     CatNasdaq),
        Stk("DDOG",  "Datadog",                      CatNasdaq),
        Stk("DLTR",  "Dollar Tree",                  CatNasdaq),
        Stk("DXCM",  "DexCom",                       CatNasdaq),
        Stk("EA",    "Electronic Arts",              CatNasdaq),
        Stk("EXC",   "Exelon",                       CatNasdaq),
        Stk("FANG",  "Diamondback Energy",           CatNasdaq),
        Stk("FAST",  "Fastenal",                     CatNasdaq),
        Stk("FTNT",  "Fortinet",                     CatNasdaq),
        Stk("GEHC",  "GE HealthCare",                CatNasdaq),
        Stk("GFS",   "GlobalFoundries",              CatNasdaq),
        Stk("GILD",  "Gilead Sciences",              CatNasdaq),
        Stk("GOOG",  "Alphabet (Class C)",           CatNasdaq),
        Stk("GOOGL", "Alphabet (Class A)",           CatNasdaq),
        Stk("IDXX",  "IDEXX Laboratories",           CatNasdaq),
        Stk("ILMN",  "Illumina",                     CatNasdaq),
        Stk("INTC",  "Intel",                        CatNasdaq),
        Stk("INTU",  "Intuit",                       CatNasdaq),
        Stk("ISRG",  "Intuitive Surgical",           CatNasdaq),
        Stk("KDP",   "Keurig Dr Pepper",             CatNasdaq),
        Stk("KHC",   "Kraft Heinz",                  CatNasdaq),
        Stk("KLAC",  "KLA Corp",                     CatNasdaq),
        Stk("LIN",   "Linde",                        CatNasdaq),
        Stk("LRCX",  "Lam Research",                 CatNasdaq),
        Stk("LULU",  "Lululemon Athletica",          CatNasdaq),
        Stk("MAR",   "Marriott",                     CatNasdaq),
        Stk("MCHP",  "Microchip Technology",         CatNasdaq),
        Stk("MDB",   "MongoDB",                      CatNasdaq),
        Stk("MDLZ",  "Mondelez",                     CatNasdaq),
        Stk("MELI",  "MercadoLibre",                 CatNasdaq),
        Stk("META",  "Meta Platforms",               CatNasdaq),
        Stk("MNST",  "Monster Beverage",             CatNasdaq),
        Stk("MRNA",  "Moderna",                      CatNasdaq),
        Stk("MRVL",  "Marvell Technology",           CatNasdaq),
        Stk("MU",    "Micron Technology",            CatNasdaq),
        Stk("NFLX",  "Netflix",                      CatNasdaq),
        Stk("NXPI",  "NXP Semiconductors",           CatNasdaq),
        Stk("ODFL",  "Old Dominion Freight Line",    CatNasdaq),
        Stk("ON",    "ON Semiconductor",             CatNasdaq),
        Stk("ORLY",  "O'Reilly Automotive",          CatNasdaq),
        Stk("PANW",  "Palo Alto Networks",           CatNasdaq),
        Stk("PAYX",  "Paychex",                      CatNasdaq),
        Stk("PCAR",  "PACCAR",                       CatNasdaq),
        Stk("PDD",   "PDD Holdings",                 CatNasdaq),
        Stk("PEP",   "PepsiCo",                      CatNasdaq),
        Stk("PYPL",  "PayPal",                       CatNasdaq),
        Stk("QCOM",  "Qualcomm",                     CatNasdaq),
        Stk("REGN",  "Regeneron Pharmaceuticals",    CatNasdaq),
        Stk("ROP",   "Roper Technologies",           CatNasdaq),
        Stk("ROST",  "Ross Stores",                  CatNasdaq),
        Stk("SBUX",  "Starbucks",                    CatNasdaq),
        Stk("SMCI",  "Super Micro Computer",         CatNasdaq),
        Stk("SNPS",  "Synopsys",                     CatNasdaq),
        Stk("TEAM",  "Atlassian",                    CatNasdaq),
        Stk("TMUS",  "T-Mobile US",                  CatNasdaq),
        Stk("TSLA",  "Tesla",                        CatNasdaq),
        Stk("TTD",   "The Trade Desk",               CatNasdaq),
        Stk("TXN",   "Texas Instruments",            CatNasdaq),
        Stk("VRSK",  "Verisk Analytics",             CatNasdaq),
        Stk("VRTX",  "Vertex Pharmaceuticals",       CatNasdaq),
        Stk("WBA",   "Walgreens Boots Alliance",     CatNasdaq),
        Stk("WBD",   "Warner Bros. Discovery",       CatNasdaq),
        Stk("WDAY",  "Workday",                      CatNasdaq),
        Stk("XEL",   "Xcel Energy",                  CatNasdaq),
        Stk("ZS",    "Zscaler",                      CatNasdaq),
    };

    // ── S&P 500 heavyweights (top 60 by market cap, excluding NASDAQ/Dow overlap) ───
    private static IEnumerable<TradableInstrument> Sp500Heavyweights() => new[]
    {
        Stk("ABBV",  "AbbVie",                       CatSp500, "NYSE"),
        Stk("ABT",   "Abbott Laboratories",          CatSp500, "NYSE"),
        Stk("ACN",   "Accenture",                    CatSp500, "NYSE"),
        Stk("AIG",   "AIG",                          CatSp500, "NYSE"),
        Stk("ALL",   "Allstate",                     CatSp500, "NYSE"),
        Stk("BAC",   "Bank of America",              CatSp500, "NYSE"),
        Stk("BLK",   "BlackRock",                    CatSp500, "NYSE"),
        Stk("BMY",   "Bristol-Myers Squibb",         CatSp500, "NYSE"),
        Stk("BRK B", "Berkshire Hathaway (Cl B)",    CatSp500, "NYSE"),
        Stk("C",     "Citigroup",                    CatSp500, "NYSE"),
        Stk("COF",   "Capital One",                  CatSp500, "NYSE"),
        Stk("COP",   "ConocoPhillips",               CatSp500, "NYSE"),
        Stk("DE",    "Deere & Co",                   CatSp500, "NYSE"),
        Stk("DHR",   "Danaher",                      CatSp500, "NYSE"),
        Stk("DUK",   "Duke Energy",                  CatSp500, "NYSE"),
        Stk("EMR",   "Emerson Electric",             CatSp500, "NYSE"),
        Stk("EOG",   "EOG Resources",                CatSp500, "NYSE"),
        Stk("F",     "Ford Motor",                   CatSp500, "NYSE"),
        Stk("FDX",   "FedEx",                        CatSp500, "NYSE"),
        Stk("GE",    "GE Aerospace",                 CatSp500, "NYSE"),
        Stk("GM",    "General Motors",               CatSp500, "NYSE"),
        Stk("LLY",   "Eli Lilly",                    CatSp500, "NYSE"),
        Stk("LMT",   "Lockheed Martin",              CatSp500, "NYSE"),
        Stk("LOW",   "Lowe's",                       CatSp500, "NYSE"),
        Stk("MA",    "Mastercard",                   CatSp500, "NYSE"),
        Stk("MET",   "MetLife",                      CatSp500, "NYSE"),
        Stk("MO",    "Altria Group",                 CatSp500, "NYSE"),
        Stk("MS",    "Morgan Stanley",               CatSp500, "NYSE"),
        Stk("NEE",   "NextEra Energy",               CatSp500, "NYSE"),
        Stk("NOC",   "Northrop Grumman",             CatSp500, "NYSE"),
        Stk("NOW",   "ServiceNow",                   CatSp500, "NYSE"),
        Stk("ORCL",  "Oracle",                       CatSp500, "NYSE"),
        Stk("PFE",   "Pfizer",                       CatSp500, "NYSE"),
        Stk("PM",    "Philip Morris International",  CatSp500, "NYSE"),
        Stk("PNC",   "PNC Financial",                CatSp500, "NYSE"),
        Stk("RTX",   "RTX Corp",                     CatSp500, "NYSE"),
        Stk("SCHW",  "Charles Schwab",               CatSp500, "NYSE"),
        Stk("SHOP",  "Shopify",                      CatSp500, "NYSE"),
        Stk("SLB",   "Schlumberger",                 CatSp500, "NYSE"),
        Stk("SO",    "Southern Co",                  CatSp500, "NYSE"),
        Stk("SPGI",  "S&P Global",                   CatSp500, "NYSE"),
        Stk("T",     "AT&T",                         CatSp500, "NYSE"),
        Stk("TGT",   "Target",                       CatSp500, "NYSE"),
        Stk("TMO",   "Thermo Fisher Scientific",     CatSp500, "NYSE"),
        Stk("UBER",  "Uber Technologies",            CatSp500, "NYSE"),
        Stk("UNP",   "Union Pacific",                CatSp500, "NYSE"),
        Stk("UPS",   "United Parcel Service",        CatSp500, "NYSE"),
        Stk("USB",   "U.S. Bancorp",                 CatSp500, "NYSE"),
        Stk("WFC",   "Wells Fargo",                  CatSp500, "NYSE"),
        Stk("XOM",   "Exxon Mobil",                  CatSp500, "NYSE"),
    };

    // ── Sector SPDR ETFs (all 11) ────────────────────────────────────────────────────
    private static IEnumerable<TradableInstrument> SectorEtfs() => new[]
    {
        Etf("XLB",  "Materials Select Sector",       CatSectorEtf),
        Etf("XLC",  "Communication Services Sector", CatSectorEtf),
        Etf("XLE",  "Energy Select Sector",          CatSectorEtf),
        Etf("XLF",  "Financial Select Sector",       CatSectorEtf),
        Etf("XLI",  "Industrial Select Sector",      CatSectorEtf),
        Etf("XLK",  "Technology Select Sector",      CatSectorEtf),
        Etf("XLP",  "Consumer Staples Sector",       CatSectorEtf),
        Etf("XLRE", "Real Estate Select Sector",     CatSectorEtf),
        Etf("XLU",  "Utilities Select Sector",       CatSectorEtf),
        Etf("XLV",  "Health Care Select Sector",     CatSectorEtf),
        Etf("XLY",  "Consumer Discretionary Sector", CatSectorEtf),
    };

    private static IEnumerable<TradableInstrument> BroadMarketEtfs() => new[]
    {
        Etf("SPY",  "SPDR S&P 500 ETF",              CatBroadEtf),
        Etf("QQQ",  "Invesco QQQ (Nasdaq-100)",      CatBroadEtf, "NASDAQ"),
        Etf("IWM",  "iShares Russell 2000",          CatBroadEtf),
        Etf("DIA",  "SPDR Dow Jones Industrial",     CatBroadEtf),
        Etf("VOO",  "Vanguard S&P 500",              CatBroadEtf),
        Etf("VTI",  "Vanguard Total Stock Market",   CatBroadEtf),
        Etf("VEA",  "Vanguard FTSE Developed Mkts",  CatBroadEtf),
        Etf("VWO",  "Vanguard FTSE Emerging Mkts",   CatBroadEtf),
        Etf("IVV",  "iShares Core S&P 500",          CatBroadEtf),
        Etf("RSP",  "Invesco S&P 500 Equal Weight",  CatBroadEtf),
        Etf("MDY",  "SPDR S&P MidCap 400",           CatBroadEtf),
        Etf("IJR",  "iShares Core S&P Small-Cap",    CatBroadEtf),
    };

    private static IEnumerable<TradableInstrument> BondEtfs() => new[]
    {
        Etf("TLT",  "iShares 20+ Year Treasury",     CatBondEtf, "NASDAQ"),
        Etf("IEF",  "iShares 7-10 Year Treasury",    CatBondEtf, "NASDAQ"),
        Etf("SHY",  "iShares 1-3 Year Treasury",     CatBondEtf, "NASDAQ"),
        Etf("LQD",  "iShares Investment Grade Corp", CatBondEtf),
        Etf("HYG",  "iShares High Yield Corp",       CatBondEtf),
        Etf("AGG",  "iShares Core US Aggregate",     CatBondEtf),
        Etf("BND",  "Vanguard Total Bond Market",    CatBondEtf, "NASDAQ"),
        Etf("EMB",  "iShares JPM USD EM Bond",       CatBondEtf, "NASDAQ"),
        Etf("MUB",  "iShares National Muni Bond",    CatBondEtf),
        Etf("TIP",  "iShares TIPS Bond",             CatBondEtf),
        Etf("SHV",  "iShares Short-Term Treasury",   CatBondEtf, "NASDAQ"),
    };

    private static IEnumerable<TradableInstrument> CommodityEtfs() => new[]
    {
        Etf("GLD",  "SPDR Gold Shares",              CatCommodityEtf),
        Etf("IAU",  "iShares Gold Trust",            CatCommodityEtf),
        Etf("SLV",  "iShares Silver Trust",          CatCommodityEtf),
        Etf("PPLT", "abrdn Platinum",                CatCommodityEtf),
        Etf("PALL", "abrdn Palladium",               CatCommodityEtf),
        Etf("USO",  "US Oil Fund",                   CatCommodityEtf),
        Etf("BNO",  "US Brent Oil Fund",             CatCommodityEtf),
        Etf("UNG",  "US Natural Gas Fund",           CatCommodityEtf),
        Etf("DBC",  "Invesco DB Commodity Index",    CatCommodityEtf),
        Etf("DBA",  "Invesco DB Agriculture",        CatCommodityEtf),
        Etf("CORN", "Teucrium Corn Fund",            CatCommodityEtf),
        Etf("WEAT", "Teucrium Wheat Fund",           CatCommodityEtf),
        Etf("SOYB", "Teucrium Soybean Fund",         CatCommodityEtf),
        Etf("URA",  "Global X Uranium ETF",          CatCommodityEtf),
        Etf("COPX", "Global X Copper Miners",        CatCommodityEtf),
    };

    private static IEnumerable<TradableInstrument> LeveragedAndInverseEtfs() => new[]
    {
        Etf("TQQQ", "ProShares UltraPro QQQ (3×)",   CatLeveragedEtf, "NASDAQ"),
        Etf("SQQQ", "ProShares UltraPro Short QQQ",  CatLeveragedEtf, "NASDAQ"),
        Etf("SPXL", "Direxion Daily S&P 500 Bull 3×",CatLeveragedEtf),
        Etf("SPXS", "Direxion Daily S&P 500 Bear 3×",CatLeveragedEtf),
        Etf("SOXL", "Direxion Daily Semicond Bull 3×",CatLeveragedEtf),
        Etf("SOXS", "Direxion Daily Semicond Bear 3×",CatLeveragedEtf),
        Etf("TNA",  "Direxion Russell 2000 Bull 3×", CatLeveragedEtf),
        Etf("TZA",  "Direxion Russell 2000 Bear 3×", CatLeveragedEtf),
        Etf("UPRO", "ProShares UltraPro S&P 500 3×", CatLeveragedEtf),
        Etf("SPXU", "ProShares UltraPro Short S&P",  CatLeveragedEtf),
        Etf("FAS",  "Direxion Financial Bull 3×",    CatLeveragedEtf),
        Etf("FAZ",  "Direxion Financial Bear 3×",    CatLeveragedEtf),
        Etf("LABU", "Direxion Biotech Bull 3×",      CatLeveragedEtf),
        Etf("LABD", "Direxion Biotech Bear 3×",      CatLeveragedEtf),
        Etf("NUGT", "Direxion Gold Miners Bull 2×",  CatLeveragedEtf),
        Etf("DUST", "Direxion Gold Miners Bear 2×",  CatLeveragedEtf),
    };

    private static IEnumerable<TradableInstrument> VolatilityEtfs() => new[]
    {
        Etf("VXX",  "iPath Series B S&P 500 VIX",    CatVolEtf),
        Etf("UVXY", "ProShares Ultra VIX Short-Term",CatVolEtf),
        Etf("SVXY", "ProShares Short VIX Short-Term",CatVolEtf),
        Etf("VIXY", "ProShares VIX Short-Term",      CatVolEtf),
        Etf("VXZ",  "iPath Series B Mid-Term VIX",   CatVolEtf),
    };

    private static IEnumerable<TradableInstrument> InternationalEtfs() => new[]
    {
        Etf("EFA",  "iShares MSCI EAFE",             CatIntlEtf),
        Etf("EEM",  "iShares MSCI Emerging Markets", CatIntlEtf),
        Etf("FXI",  "iShares China Large-Cap",       CatIntlEtf),
        Etf("MCHI", "iShares MSCI China",            CatIntlEtf, "NASDAQ"),
        Etf("KWEB", "KraneShares CSI China Internet",CatIntlEtf),
        Etf("EWJ",  "iShares MSCI Japan",            CatIntlEtf),
        Etf("EWZ",  "iShares MSCI Brazil",           CatIntlEtf),
        Etf("EWG",  "iShares MSCI Germany",          CatIntlEtf),
        Etf("EWU",  "iShares MSCI United Kingdom",   CatIntlEtf),
        Etf("INDA", "iShares MSCI India",            CatIntlEtf),
        Etf("EWT",  "iShares MSCI Taiwan",           CatIntlEtf),
        Etf("EWY",  "iShares MSCI South Korea",      CatIntlEtf),
        Etf("EWA",  "iShares MSCI Australia",        CatIntlEtf),
        Etf("EWC",  "iShares MSCI Canada",           CatIntlEtf),
        Etf("EWH",  "iShares MSCI Hong Kong",        CatIntlEtf),
        Etf("VEU",  "Vanguard FTSE All-World ex-US", CatIntlEtf),
    };

    // ── Futures ───────────────────────────────────────────────────────────────────────
    private static IEnumerable<TradableInstrument> EquityIndexFutures() => new[]
    {
        Fut("ES",    "E-mini S&P 500",            CatEqIdxFut, "CME"),
        Fut("MES",   "Micro E-mini S&P 500",      CatEqIdxFut, "CME"),
        Fut("NQ",    "E-mini Nasdaq-100",         CatEqIdxFut, "CME"),
        Fut("MNQ",   "Micro E-mini Nasdaq-100",   CatEqIdxFut, "CME"),
        Fut("YM",    "E-mini Dow ($5)",           CatEqIdxFut, "CBOT"),
        Fut("MYM",   "Micro E-mini Dow",          CatEqIdxFut, "CBOT"),
        Fut("RTY",   "E-mini Russell 2000",       CatEqIdxFut, "CME"),
        Fut("M2K",   "Micro E-mini Russell 2000", CatEqIdxFut, "CME"),
        Fut("VX",    "CBOE Volatility Index",     CatEqIdxFut, "CFE"),
        Fut("NKD",   "Nikkei 225 USD",            CatEqIdxFut, "CME"),
        Fut("NIY",   "Nikkei 225 Yen",            CatEqIdxFut, "CME", "JPY"),
        Fut("FDAX",  "DAX 40 Index",              CatEqIdxFut, "EUREX", "EUR"),
        Fut("FESX",  "EURO STOXX 50",             CatEqIdxFut, "EUREX", "EUR"),
        Fut("FSMI",  "Swiss Market Index",        CatEqIdxFut, "EUREX", "CHF"),
        Fut("Z",     "FTSE 100 Index",            CatEqIdxFut, "ICEEU", "GBP"),
        Fut("HSI",   "Hang Seng Index",           CatEqIdxFut, "HKFE",  "HKD"),
        Fut("MHI",   "Mini Hang Seng",            CatEqIdxFut, "HKFE",  "HKD"),
    };

    private static IEnumerable<TradableInstrument> TreasuryFutures() => new[]
    {
        Fut("ZT",  "2-Year T-Note",       CatTreasFut, "CBOT"),
        Fut("ZF",  "5-Year T-Note",       CatTreasFut, "CBOT"),
        Fut("ZN",  "10-Year T-Note",      CatTreasFut, "CBOT"),
        Fut("ZB",  "30-Year T-Bond",      CatTreasFut, "CBOT"),
        Fut("UB",  "Ultra T-Bond",        CatTreasFut, "CBOT"),
        Fut("TN",  "Ultra 10-Year",       CatTreasFut, "CBOT"),
        Fut("SR3", "3-Month SOFR",        CatTreasFut, "CME"),
        Fut("SR1", "1-Month SOFR",        CatTreasFut, "CME"),
    };

    private static IEnumerable<TradableInstrument> EnergyFutures() => new[]
    {
        Fut("CL",  "Crude Oil (WTI)",     CatEnergyFut, "NYMEX"),
        Fut("MCL", "Micro Crude Oil",     CatEnergyFut, "NYMEX"),
        Fut("QM",  "E-mini Crude Oil",    CatEnergyFut, "NYMEX"),
        Fut("BZ",  "Brent Crude",         CatEnergyFut, "NYMEX"),
        Fut("NG",  "Natural Gas",         CatEnergyFut, "NYMEX"),
        Fut("MNG", "Micro Natural Gas",   CatEnergyFut, "NYMEX"),
        Fut("RB",  "RBOB Gasoline",       CatEnergyFut, "NYMEX"),
        Fut("HO",  "NY Harbor ULSD",      CatEnergyFut, "NYMEX"),
    };

    private static IEnumerable<TradableInstrument> MetalFutures() => new[]
    {
        Fut("GC",  "Gold (100 oz)",       CatMetalFut, "COMEX"),
        Fut("MGC", "Micro Gold (10 oz)",  CatMetalFut, "COMEX"),
        Fut("SI",  "Silver (5000 oz)",    CatMetalFut, "COMEX"),
        Fut("SIL", "Micro Silver",        CatMetalFut, "COMEX"),
        Fut("HG",  "Copper",              CatMetalFut, "COMEX"),
        Fut("MHG", "Micro Copper",        CatMetalFut, "COMEX"),
        Fut("PL",  "Platinum",            CatMetalFut, "NYMEX"),
        Fut("PA",  "Palladium",           CatMetalFut, "NYMEX"),
    };

    private static IEnumerable<TradableInstrument> AgriculturalFutures() => new[]
    {
        Fut("ZC",  "Corn",                CatAgFut, "CBOT"),
        Fut("ZS",  "Soybeans",            CatAgFut, "CBOT"),
        Fut("ZW",  "Chicago Wheat",       CatAgFut, "CBOT"),
        Fut("ZL",  "Soybean Oil",         CatAgFut, "CBOT"),
        Fut("ZM",  "Soybean Meal",        CatAgFut, "CBOT"),
        Fut("ZO",  "Oats",                CatAgFut, "CBOT"),
        Fut("ZR",  "Rough Rice",          CatAgFut, "CBOT"),
        Fut("KC",  "Coffee",              CatAgFut, "NYBOT"),
        Fut("SB",  "Sugar #11",           CatAgFut, "NYBOT"),
        Fut("CC",  "Cocoa",               CatAgFut, "NYBOT"),
        Fut("CT",  "Cotton",              CatAgFut, "NYBOT"),
        Fut("OJ",  "Orange Juice",        CatAgFut, "NYBOT"),
        Fut("LE",  "Live Cattle",         CatAgFut, "CME"),
        Fut("GF",  "Feeder Cattle",       CatAgFut, "CME"),
        Fut("HE",  "Lean Hogs",           CatAgFut, "CME"),
    };

    private static IEnumerable<TradableInstrument> CurrencyFutures() => new[]
    {
        Fut("6E", "Euro FX",          CatFxFut, "CME"),
        Fut("6B", "British Pound",    CatFxFut, "CME"),
        Fut("6J", "Japanese Yen",     CatFxFut, "CME"),
        Fut("6A", "Australian Dollar",CatFxFut, "CME"),
        Fut("6C", "Canadian Dollar",  CatFxFut, "CME"),
        Fut("6S", "Swiss Franc",      CatFxFut, "CME"),
        Fut("6N", "New Zealand $",    CatFxFut, "CME"),
        Fut("6M", "Mexican Peso",     CatFxFut, "CME"),
        Fut("DX", "US Dollar Index",  CatFxFut, "ICEUS"),
    };

    private static IEnumerable<TradableInstrument> CryptoFutures() => new[]
    {
        Fut("BTC", "Bitcoin",         CatCryptoFut, "CME"),
        Fut("MBT", "Micro Bitcoin",   CatCryptoFut, "CME"),
        Fut("ETH", "Ether",           CatCryptoFut, "CME"),
        Fut("MET", "Micro Ether",     CatCryptoFut, "CME"),
    };

    // ── FX (IDEALPRO) ────────────────────────────────────────────────────────────────
    private static IEnumerable<TradableInstrument> ForexMajors() => new[]
    {
        Fx("EUR", "USD", "Euro / US Dollar",          CatForexMajor),
        Fx("GBP", "USD", "Pound / US Dollar",         CatForexMajor),
        Fx("USD", "JPY", "US Dollar / Japanese Yen",  CatForexMajor),
        Fx("USD", "CHF", "US Dollar / Swiss Franc",   CatForexMajor),
        Fx("AUD", "USD", "Australian / US Dollar",    CatForexMajor),
        Fx("USD", "CAD", "US Dollar / Can. Dollar",   CatForexMajor),
        Fx("NZD", "USD", "New Zealand / US Dollar",   CatForexMajor),
    };

    private static IEnumerable<TradableInstrument> ForexMinors() => new[]
    {
        Fx("EUR", "GBP", "Euro / Pound",              CatForexMinor),
        Fx("EUR", "JPY", "Euro / Japanese Yen",       CatForexMinor),
        Fx("EUR", "CHF", "Euro / Swiss Franc",        CatForexMinor),
        Fx("EUR", "AUD", "Euro / Australian Dollar",  CatForexMinor),
        Fx("EUR", "CAD", "Euro / Canadian Dollar",    CatForexMinor),
        Fx("EUR", "NZD", "Euro / NZ Dollar",          CatForexMinor),
        Fx("GBP", "JPY", "Pound / Japanese Yen",      CatForexMinor),
        Fx("GBP", "CHF", "Pound / Swiss Franc",       CatForexMinor),
        Fx("GBP", "AUD", "Pound / Australian Dollar", CatForexMinor),
        Fx("GBP", "CAD", "Pound / Canadian Dollar",   CatForexMinor),
        Fx("GBP", "NZD", "Pound / NZ Dollar",         CatForexMinor),
        Fx("AUD", "JPY", "Australian / Japanese Yen", CatForexMinor),
        Fx("AUD", "CHF", "Australian / Swiss Franc",  CatForexMinor),
        Fx("AUD", "NZD", "Australian / NZ Dollar",    CatForexMinor),
        Fx("AUD", "CAD", "Australian / Can. Dollar",  CatForexMinor),
        Fx("NZD", "JPY", "NZ Dollar / Japanese Yen",  CatForexMinor),
        Fx("NZD", "CHF", "NZ Dollar / Swiss Franc",   CatForexMinor),
        Fx("NZD", "CAD", "NZ Dollar / Can. Dollar",   CatForexMinor),
        Fx("CAD", "JPY", "Can. Dollar / Japanese Yen",CatForexMinor),
        Fx("CAD", "CHF", "Can. Dollar / Swiss Franc", CatForexMinor),
        Fx("CHF", "JPY", "Swiss Franc / Japanese Yen",CatForexMinor),
    };

    private static IEnumerable<TradableInstrument> ForexExotics() => new[]
    {
        Fx("USD", "MXN", "US Dollar / Mexican Peso",  CatForexExotic),
        Fx("USD", "ZAR", "US Dollar / S. African Rand",CatForexExotic),
        Fx("USD", "SGD", "US Dollar / Singapore $",   CatForexExotic),
        Fx("USD", "HKD", "US Dollar / Hong Kong $",   CatForexExotic),
        Fx("USD", "NOK", "US Dollar / Norwegian Krone",CatForexExotic),
        Fx("USD", "SEK", "US Dollar / Swedish Krona", CatForexExotic),
        Fx("USD", "DKK", "US Dollar / Danish Krone",  CatForexExotic),
        Fx("USD", "PLN", "US Dollar / Polish Zloty",  CatForexExotic),
        Fx("USD", "TRY", "US Dollar / Turkish Lira",  CatForexExotic),
        Fx("USD", "CNH", "US Dollar / CNH (offshore)",CatForexExotic),
        Fx("EUR", "NOK", "Euro / Norwegian Krone",    CatForexExotic),
        Fx("EUR", "SEK", "Euro / Swedish Krona",      CatForexExotic),
        Fx("EUR", "PLN", "Euro / Polish Zloty",       CatForexExotic),
        Fx("EUR", "TRY", "Euro / Turkish Lira",       CatForexExotic),
    };
}
