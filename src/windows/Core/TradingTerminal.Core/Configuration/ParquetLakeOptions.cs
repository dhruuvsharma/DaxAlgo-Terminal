using TradingTerminal.Core.MarketData.Archive;

namespace TradingTerminal.Core.Configuration;

/// <summary>
/// Knobs for the local Parquet lake — a persistent, on-disk Parquet mirror of the canonical
/// store's closed periods, laid out for direct DuckDB querying. Distinct from the Telegram
/// archive offloader: the offloader bundles → uploads → prunes local rows, leaving nothing on
/// disk to query, whereas the lake keeps the Parquet files locally so the research/backtest path
/// can read history through <c>IParquetQueryService</c>. The two are independent and can run
/// together. Bound from the <c>MarketDataParquetLake</c> section of appsettings.
/// </summary>
public sealed class ParquetLakeOptions
{
    public const string SectionName = "MarketDataParquetLake";

    /// <summary>Master switch. When false the export service idles (one cheap timer tick / 15 min).</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Root directory of the lake. Falls back to
    /// %LocalAppData%/DaxAlgo Terminal/parquet-lake when null.</summary>
    public string? RootDirectory { get; set; }

    /// <summary>How often a period closes and gets exported. Monthly by default — fewer, larger
    /// files are friendlier to DuckDB scans than many tiny weekly ones.</summary>
    public ArchivePeriod Period { get; set; } = ArchivePeriod.Monthly;

    /// <summary>Tables to export (bit flag). Default is Quotes + Bars + Trades.</summary>
    public ArchiveTables Tables { get; set; } = ArchiveTables.Quotes | ArchiveTables.Bars | ArchiveTables.Trades;

    /// <summary>UTC hour of day at which the service checks for a closed period (0–23). Offset
    /// from the archive offloader's default hour so the two don't hammer the store at once.</summary>
    public int DailyCheckHourUtc { get; set; } = 4;
}
