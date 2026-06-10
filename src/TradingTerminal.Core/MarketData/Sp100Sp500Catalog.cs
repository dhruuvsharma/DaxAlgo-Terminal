using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData;

/// <summary>A single index constituent: its US-stock ticker and company name.</summary>
public readonly record struct SpSymbol(string Symbol, string Name);

/// <summary>
/// Static reference data: the S&amp;P 100 and S&amp;P 500 ticker universes as US-stock
/// <see cref="Contract"/>s, for the "1-Minute Order Flow Pressure Map" tool.
///
/// <para><b>This is point-in-time, hand-maintained reference data.</b> Index membership drifts
/// as the committees rebalance (adds, deletes, ticker changes, mergers), so this list will go
/// stale and is expected to be corrected over time. It prioritises symbol correctness for the
/// names it includes over hitting an exact constituent count, and the <c>Sp500</c> set covers
/// the largest / most-liquid members thoroughly rather than every name. The S&amp;P 100
/// (<see cref="Sp100"/>) is a strict subset of the S&amp;P 500 (<see cref="Sp500"/>).</para>
///
/// Pure Core data — only <see cref="Contract"/> from the domain, no broker or UI types.
/// </summary>
public static class Sp100Sp500Catalog
{
    /// <summary>S&amp;P 100 constituents (symbol + company name), ordered alphabetically by symbol.</summary>
    public static IReadOnlyList<SpSymbol> Sp100 { get; } = BuildSp100();

    /// <summary>S&amp;P 500 constituents (symbol + company name), ordered alphabetically by symbol.
    /// Superset of <see cref="Sp100"/>.</summary>
    public static IReadOnlyList<SpSymbol> Sp500 { get; } = BuildSp500();

    /// <summary>The S&amp;P 100 mapped to US-stock <see cref="Contract"/>s.</summary>
    public static IReadOnlyList<Contract> Sp100Contracts { get; } = Map(Sp100);

    /// <summary>The S&amp;P 500 mapped to US-stock <see cref="Contract"/>s.</summary>
    public static IReadOnlyList<Contract> Sp500Contracts { get; } = Map(Sp500);

    /// <summary>Map a constituent to a US-stock <see cref="Contract"/> via the canonical factory.
    /// Class-share dots (e.g. <c>BRK.B</c>) are kept as-is — IB accepts the dotted symbol.</summary>
    public static Contract ToContract(SpSymbol s) => Contract.UsStock(s.Symbol);

    private static IReadOnlyList<Contract> Map(IReadOnlyList<SpSymbol> set)
    {
        var list = new List<Contract>(set.Count);
        foreach (var s in set)
            list.Add(ToContract(s));
        return list;
    }

    private static IReadOnlyList<SpSymbol> BuildSp100() =>
    [
        new("AAPL", "Apple Inc."),
        new("ABBV", "AbbVie Inc."),
        new("ABT", "Abbott Laboratories"),
        new("ACN", "Accenture plc"),
        new("ADBE", "Adobe Inc."),
        new("AIG", "American International Group"),
        new("AMD", "Advanced Micro Devices"),
        new("AMGN", "Amgen Inc."),
        new("AMT", "American Tower Corporation"),
        new("AMZN", "Amazon.com Inc."),
        new("AVGO", "Broadcom Inc."),
        new("AXP", "American Express Company"),
        new("BA", "Boeing Company"),
        new("BAC", "Bank of America Corp."),
        new("BK", "Bank of New York Mellon"),
        new("BKNG", "Booking Holdings Inc."),
        new("BLK", "BlackRock Inc."),
        new("BMY", "Bristol-Myers Squibb"),
        new("BRK.B", "Berkshire Hathaway Inc. Cl B"),
        new("C", "Citigroup Inc."),
        new("CAT", "Caterpillar Inc."),
        new("CHTR", "Charter Communications"),
        new("CL", "Colgate-Palmolive Company"),
        new("CMCSA", "Comcast Corporation"),
        new("COF", "Capital One Financial"),
        new("COP", "ConocoPhillips"),
        new("COST", "Costco Wholesale Corp."),
        new("CRM", "Salesforce Inc."),
        new("CSCO", "Cisco Systems Inc."),
        new("CVS", "CVS Health Corporation"),
        new("CVX", "Chevron Corporation"),
        new("DHR", "Danaher Corporation"),
        new("DIS", "Walt Disney Company"),
        new("DUK", "Duke Energy Corporation"),
        new("EMR", "Emerson Electric Co."),
        new("F", "Ford Motor Company"),
        new("FDX", "FedEx Corporation"),
        new("GD", "General Dynamics Corp."),
        new("GE", "General Electric Company"),
        new("GILD", "Gilead Sciences Inc."),
        new("GM", "General Motors Company"),
        new("GOOG", "Alphabet Inc. Cl C"),
        new("GOOGL", "Alphabet Inc. Cl A"),
        new("GS", "Goldman Sachs Group"),
        new("HD", "Home Depot Inc."),
        new("HON", "Honeywell International"),
        new("IBM", "International Business Machines"),
        new("INTC", "Intel Corporation"),
        new("JNJ", "Johnson & Johnson"),
        new("JPM", "JPMorgan Chase & Co."),
        new("KO", "Coca-Cola Company"),
        new("LIN", "Linde plc"),
        new("LLY", "Eli Lilly and Company"),
        new("LMT", "Lockheed Martin Corp."),
        new("LOW", "Lowe's Companies Inc."),
        new("MA", "Mastercard Incorporated"),
        new("MCD", "McDonald's Corporation"),
        new("MDLZ", "Mondelez International"),
        new("MDT", "Medtronic plc"),
        new("MET", "MetLife Inc."),
        new("META", "Meta Platforms Inc."),
        new("MMM", "3M Company"),
        new("MO", "Altria Group Inc."),
        new("MRK", "Merck & Co. Inc."),
        new("MS", "Morgan Stanley"),
        new("MSFT", "Microsoft Corporation"),
        new("NEE", "NextEra Energy Inc."),
        new("NFLX", "Netflix Inc."),
        new("NKE", "Nike Inc."),
        new("NVDA", "NVIDIA Corporation"),
        new("ORCL", "Oracle Corporation"),
        new("PEP", "PepsiCo Inc."),
        new("PFE", "Pfizer Inc."),
        new("PG", "Procter & Gamble Co."),
        new("PM", "Philip Morris International"),
        new("PYPL", "PayPal Holdings Inc."),
        new("QCOM", "QUALCOMM Incorporated"),
        new("RTX", "RTX Corporation"),
        new("SBUX", "Starbucks Corporation"),
        new("SCHW", "Charles Schwab Corp."),
        new("SO", "Southern Company"),
        new("SPG", "Simon Property Group"),
        new("T", "AT&T Inc."),
        new("TGT", "Target Corporation"),
        new("TMO", "Thermo Fisher Scientific"),
        new("TMUS", "T-Mobile US Inc."),
        new("TSLA", "Tesla Inc."),
        new("TXN", "Texas Instruments Inc."),
        new("UNH", "UnitedHealth Group Inc."),
        new("UNP", "Union Pacific Corporation"),
        new("UPS", "United Parcel Service"),
        new("USB", "U.S. Bancorp"),
        new("V", "Visa Inc."),
        new("VZ", "Verizon Communications"),
        new("WFC", "Wells Fargo & Company"),
        new("WMT", "Walmart Inc."),
        new("XOM", "Exxon Mobil Corporation"),
    ];

    // The S&P 500 set: the S&P 100 above plus a broad cross-section of additional members,
    // merged and re-sorted alphabetically at construction time.
    private static IReadOnlyList<SpSymbol> BuildSp500()
    {
        var extra = new SpSymbol[]
        {
            new("A", "Agilent Technologies"),
            new("ADI", "Analog Devices Inc."),
            new("ADM", "Archer-Daniels-Midland"),
            new("ADP", "Automatic Data Processing"),
            new("ADSK", "Autodesk Inc."),
            new("AEP", "American Electric Power"),
            new("AFL", "Aflac Incorporated"),
            new("AJG", "Arthur J. Gallagher & Co."),
            new("ALL", "Allstate Corporation"),
            new("AME", "AMETEK Inc."),
            new("AMP", "Ameriprise Financial"),
            new("ANET", "Arista Networks Inc."),
            new("AON", "Aon plc"),
            new("APD", "Air Products and Chemicals"),
            new("APH", "Amphenol Corporation"),
            new("ATO", "Atmos Energy Corporation"),
            new("AZO", "AutoZone Inc."),
            new("BBY", "Best Buy Co. Inc."),
            new("BDX", "Becton Dickinson and Co."),
            new("BKR", "Baker Hughes Company"),
            new("BSX", "Boston Scientific Corp."),
            new("CB", "Chubb Limited"),
            new("CBRE", "CBRE Group Inc."),
            new("CCI", "Crown Castle Inc."),
            new("CDNS", "Cadence Design Systems"),
            new("CDW", "CDW Corporation"),
            new("CEG", "Constellation Energy"),
            new("CI", "Cigna Group"),
            new("CINF", "Cincinnati Financial Corp."),
            new("CLX", "Clorox Company"),
            new("CME", "CME Group Inc."),
            new("CMG", "Chipotle Mexican Grill"),
            new("CMI", "Cummins Inc."),
            new("CNC", "Centene Corporation"),
            new("COR", "Cencora Inc."),
            new("CPRT", "Copart Inc."),
            new("CSX", "CSX Corporation"),
            new("CTAS", "Cintas Corporation"),
            new("CTSH", "Cognizant Technology"),
            new("CTVA", "Corteva Inc."),
            new("D", "Dominion Energy Inc."),
            new("DAL", "Delta Air Lines Inc."),
            new("DD", "DuPont de Nemours Inc."),
            new("DE", "Deere & Company"),
            new("DELL", "Dell Technologies Inc."),
            new("DG", "Dollar General Corporation"),
            new("DLR", "Digital Realty Trust"),
            new("DOW", "Dow Inc."),
            new("DXCM", "DexCom Inc."),
            new("EA", "Electronic Arts Inc."),
            new("ECL", "Ecolab Inc."),
            new("EL", "Estee Lauder Companies"),
            new("ELV", "Elevance Health Inc."),
            new("EOG", "EOG Resources Inc."),
            new("EQIX", "Equinix Inc."),
            new("EQT", "EQT Corporation"),
            new("ETN", "Eaton Corporation plc"),
            new("EW", "Edwards Lifesciences"),
            new("EXC", "Exelon Corporation"),
            new("FANG", "Diamondback Energy Inc."),
            new("FAST", "Fastenal Company"),
            new("FCX", "Freeport-McMoRan Inc."),
            new("FI", "Fiserv Inc."),
            new("FICO", "Fair Isaac Corporation"),
            new("FIS", "Fidelity National Information"),
            new("FTNT", "Fortinet Inc."),
            new("GEHC", "GE HealthCare Technologies"),
            new("GEV", "GE Vernova Inc."),
            new("GIS", "General Mills Inc."),
            new("GLW", "Corning Incorporated"),
            new("GWW", "W.W. Grainger Inc."),
            new("HAL", "Halliburton Company"),
            new("HCA", "HCA Healthcare Inc."),
            new("HES", "Hess Corporation"),
            new("HLT", "Hilton Worldwide Holdings"),
            new("HPQ", "HP Inc."),
            new("HUM", "Humana Inc."),
            new("ICE", "Intercontinental Exchange"),
            new("IDXX", "IDEXX Laboratories Inc."),
            new("ITW", "Illinois Tool Works Inc."),
            new("JCI", "Johnson Controls International"),
            new("KDP", "Keurig Dr Pepper Inc."),
            new("KHC", "Kraft Heinz Company"),
            new("KKR", "KKR & Co. Inc."),
            new("KLAC", "KLA Corporation"),
            new("KMB", "Kimberly-Clark Corporation"),
            new("KMI", "Kinder Morgan Inc."),
            new("KR", "Kroger Co."),
            new("KVUE", "Kenvue Inc."),
            new("LHX", "L3Harris Technologies"),
            new("LRCX", "Lam Research Corporation"),
            new("LULU", "Lululemon Athletica Inc."),
            new("LVS", "Las Vegas Sands Corp."),
            new("MAR", "Marriott International"),
            new("MCHP", "Microchip Technology Inc."),
            new("MCK", "McKesson Corporation"),
            new("MCO", "Moody's Corporation"),
            new("MMC", "Marsh & McLennan Companies"),
            new("MNST", "Monster Beverage Corp."),
            new("MPC", "Marathon Petroleum Corp."),
            new("MPWR", "Monolithic Power Systems"),
            new("MRVL", "Marvell Technology Inc."),
            new("MSI", "Motorola Solutions Inc."),
            new("MU", "Micron Technology Inc."),
            new("NDAQ", "Nasdaq Inc."),
            new("NEM", "Newmont Corporation"),
            new("NOC", "Northrop Grumman Corp."),
            new("NOW", "ServiceNow Inc."),
            new("NSC", "Norfolk Southern Corp."),
            new("NXPI", "NXP Semiconductors"),
            new("O", "Realty Income Corporation"),
            new("ODFL", "Old Dominion Freight Line"),
            new("OKE", "ONEOK Inc."),
            new("OXY", "Occidental Petroleum Corp."),
            new("PANW", "Palo Alto Networks Inc."),
            new("PAYX", "Paychex Inc."),
            new("PCAR", "PACCAR Inc."),
            new("PCG", "PG&E Corporation"),
            new("PEG", "Public Service Enterprise"),
            new("PGR", "Progressive Corporation"),
            new("PH", "Parker-Hannifin Corp."),
            new("PLD", "Prologis Inc."),
            new("PNC", "PNC Financial Services"),
            new("PSA", "Public Storage"),
            new("PSX", "Phillips 66"),
            new("PWR", "Quanta Services Inc."),
            new("REGN", "Regeneron Pharmaceuticals"),
            new("ROK", "Rockwell Automation Inc."),
            new("ROP", "Roper Technologies Inc."),
            new("ROST", "Ross Stores Inc."),
            new("SLB", "Schlumberger Limited"),
            new("SNPS", "Synopsys Inc."),
            new("SPGI", "S&P Global Inc."),
            new("SRE", "Sempra"),
            new("STZ", "Constellation Brands Inc."),
            new("SYK", "Stryker Corporation"),
            new("SYY", "Sysco Corporation"),
            new("TDG", "TransDigm Group Inc."),
            new("TEL", "TE Connectivity Ltd."),
            new("TFC", "Truist Financial Corp."),
            new("TJX", "TJX Companies Inc."),
            new("TRV", "Travelers Companies Inc."),
            new("TT", "Trane Technologies plc"),
            new("TTD", "Trade Desk Inc."),
            new("TTWO", "Take-Two Interactive"),
            new("TXT", "Textron Inc."),
            new("URI", "United Rentals Inc."),
            new("VLO", "Valero Energy Corporation"),
            new("VRSK", "Verisk Analytics Inc."),
            new("VRTX", "Vertex Pharmaceuticals"),
            new("VST", "Vistra Corp."),
            new("WBD", "Warner Bros. Discovery"),
            new("WDAY", "Workday Inc."),
            new("WDC", "Western Digital Corp."),
            new("WEC", "WEC Energy Group Inc."),
            new("WELL", "Welltower Inc."),
            new("WM", "Waste Management Inc."),
            new("WMB", "Williams Companies Inc."),
            new("XEL", "Xcel Energy Inc."),
            new("YUM", "Yum! Brands Inc."),
            new("ZTS", "Zoetis Inc."),
        };

        var merged = new Dictionary<string, SpSymbol>(StringComparer.Ordinal);
        foreach (var s in Sp100)
            merged[s.Symbol] = s;
        foreach (var s in extra)
            merged[s.Symbol] = s;

        var list = new List<SpSymbol>(merged.Values);
        list.Sort(static (a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
        return list;
    }
}
