using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.IndexKScore;

/// <summary>A named index universe: its display metadata and weighted constituents.</summary>
public sealed record IndexFamily(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<IndexComponent> Components);

/// <summary>
/// Hand-curated, weighted constituent catalogs for the major US indices. Weights are approximate and
/// slow-moving; users adjust them in a setup form if they need exact numbers. Symbols use the
/// standard US-equity contract shape so they resolve uniformly across IB / Alpaca / NinjaTrader.
/// cTrader is FX-only and won't have these instruments — host VMs warn when no connected broker can
/// supply the family. Pure Core data — only <see cref="Contract"/> from the domain.
///
/// <para><b>Point-in-time reference data.</b> Index membership and weights drift as committees
/// rebalance, so these lists go stale and are expected to be corrected over time.</para>
/// </summary>
public static class IndexComponentCatalog
{
    private static Contract Stock(string sym, string exch = "NASDAQ") => Contract.UsStock(sym, exch);

    public static IReadOnlyList<IndexFamily> All { get; } = new IndexFamily[]
    {
        // ── Dow Jones Industrial Average (DJIA / US30) — price-weighted, 30 names. ──────
        // Weights are derived from current prices ÷ index divisor; they drift as prices move.
        // Snapshot ≈ 2026-01 levels.
        new IndexFamily(
            Id: "us30",
            DisplayName: "Dow Jones Industrial Average (US30)",
            Description: "30 large-cap US blue chips, price-weighted.",
            Components: new IndexComponent[]
            {
                new("UNH",  "UnitedHealth",        0.082, Stock("UNH",  "NYSE")),
                new("GS",   "Goldman Sachs",       0.075, Stock("GS",   "NYSE")),
                new("MSFT", "Microsoft",           0.067, Stock("MSFT")),
                new("HD",   "Home Depot",          0.063, Stock("HD",   "NYSE")),
                new("CAT",  "Caterpillar",         0.058, Stock("CAT",  "NYSE")),
                new("CRM",  "Salesforce",          0.052, Stock("CRM",  "NYSE")),
                new("MCD",  "McDonald's",          0.048, Stock("MCD",  "NYSE")),
                new("V",    "Visa",                0.046, Stock("V",    "NYSE")),
                new("AMGN", "Amgen",               0.045, Stock("AMGN")),
                new("AXP",  "American Express",    0.044, Stock("AXP",  "NYSE")),
                new("AAPL", "Apple",               0.041, Stock("AAPL")),
                new("TRV",  "Travelers",           0.038, Stock("TRV",  "NYSE")),
                new("BA",   "Boeing",              0.036, Stock("BA",   "NYSE")),
                new("HON",  "Honeywell",           0.034, Stock("HON")),
                new("JPM",  "JPMorgan Chase",      0.032, Stock("JPM",  "NYSE")),
                new("IBM",  "IBM",                 0.030, Stock("IBM",  "NYSE")),
                new("PG",   "Procter & Gamble",    0.028, Stock("PG",   "NYSE")),
                new("JNJ",  "Johnson & Johnson",   0.026, Stock("JNJ",  "NYSE")),
                new("CVX",  "Chevron",             0.024, Stock("CVX",  "NYSE")),
                new("MMM",  "3M",                  0.022, Stock("MMM",  "NYSE")),
                new("DIS",  "Disney",              0.020, Stock("DIS",  "NYSE")),
                new("WMT",  "Walmart",             0.018, Stock("WMT",  "NYSE")),
                new("MRK",  "Merck",               0.016, Stock("MRK",  "NYSE")),
                new("NKE",  "Nike",                0.014, Stock("NKE",  "NYSE")),
                new("KO",   "Coca-Cola",           0.012, Stock("KO",   "NYSE")),
                new("CSCO", "Cisco",               0.010, Stock("CSCO")),
                new("INTC", "Intel",               0.008, Stock("INTC")),
                new("VZ",   "Verizon",             0.006, Stock("VZ",   "NYSE")),
                new("WBA",  "Walgreens Boots",     0.004, Stock("WBA")),
                new("DOW",  "Dow Inc",             0.003, Stock("DOW",  "NYSE")),
            }),

        // ── S&P 500 top 30 — capitalization-weighted (approximate). ─────────────────────
        // Heavy tech bias reflects current index composition.
        new IndexFamily(
            Id: "sp500-top30",
            DisplayName: "S&P 500 Top 30 (cap-weighted slice)",
            Description: "30 largest S&P 500 constituents by market cap (covers ~50% of index weight).",
            Components: new IndexComponent[]
            {
                new("AAPL", "Apple",               0.072, Stock("AAPL")),
                new("MSFT", "Microsoft",           0.069, Stock("MSFT")),
                new("NVDA", "NVIDIA",              0.067, Stock("NVDA")),
                new("AMZN", "Amazon",              0.036, Stock("AMZN")),
                new("META", "Meta Platforms",      0.025, Stock("META")),
                new("GOOGL","Alphabet (Class A)",  0.022, Stock("GOOGL")),
                new("GOOG", "Alphabet (Class C)",  0.018, Stock("GOOG")),
                new("BRK.B","Berkshire Hathaway B",0.017, Stock("BRK B", "NYSE")),
                new("LLY",  "Eli Lilly",           0.015, Stock("LLY",  "NYSE")),
                new("AVGO", "Broadcom",            0.014, Stock("AVGO")),
                new("TSLA", "Tesla",               0.013, Stock("TSLA")),
                new("JPM",  "JPMorgan Chase",      0.012, Stock("JPM",  "NYSE")),
                new("UNH",  "UnitedHealth",        0.011, Stock("UNH",  "NYSE")),
                new("XOM",  "Exxon Mobil",         0.010, Stock("XOM",  "NYSE")),
                new("V",    "Visa",                0.009, Stock("V",    "NYSE")),
                new("PG",   "Procter & Gamble",    0.008, Stock("PG",   "NYSE")),
                new("MA",   "Mastercard",          0.008, Stock("MA",   "NYSE")),
                new("JNJ",  "Johnson & Johnson",   0.007, Stock("JNJ",  "NYSE")),
                new("HD",   "Home Depot",          0.007, Stock("HD",   "NYSE")),
                new("COST", "Costco",              0.007, Stock("COST")),
                new("ORCL", "Oracle",              0.006, Stock("ORCL", "NYSE")),
                new("ABBV", "AbbVie",              0.006, Stock("ABBV", "NYSE")),
                new("BAC",  "Bank of America",     0.006, Stock("BAC",  "NYSE")),
                new("KO",   "Coca-Cola",           0.005, Stock("KO",   "NYSE")),
                new("CVX",  "Chevron",             0.005, Stock("CVX",  "NYSE")),
                new("MRK",  "Merck",               0.005, Stock("MRK",  "NYSE")),
                new("WMT",  "Walmart",             0.005, Stock("WMT",  "NYSE")),
                new("CRM",  "Salesforce",          0.004, Stock("CRM",  "NYSE")),
                new("AMD",  "Advanced Micro Devices",0.004, Stock("AMD")),
                new("ADBE", "Adobe",               0.003, Stock("ADBE")),
            }),

        // ── Mini-test set (5 names) — fast smoke test without hammering broker subscriptions.
        new IndexFamily(
            Id: "mini5",
            DisplayName: "Mini test (5 large-caps)",
            Description: "Smoke-test universe: AAPL, MSFT, NVDA, AMZN, META. Use for development.",
            Components: new IndexComponent[]
            {
                new("AAPL", "Apple",     0.30, Stock("AAPL")),
                new("MSFT", "Microsoft", 0.25, Stock("MSFT")),
                new("NVDA", "NVIDIA",    0.20, Stock("NVDA")),
                new("AMZN", "Amazon",    0.15, Stock("AMZN")),
                new("META", "Meta",      0.10, Stock("META")),
            }),
    };
}
