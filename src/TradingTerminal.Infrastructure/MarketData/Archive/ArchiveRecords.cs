namespace TradingTerminal.Infrastructure.MarketData.Archive;

/// <summary>Parquet wire format for an archived quote. Timestamps are epoch microseconds UTC so
/// the file is timezone-agnostic and survives DateTimeKind round-trips.</summary>
internal sealed class QuoteParquetRow
{
    public long InstrumentId { get; set; }
    public long EventTimeMicros { get; set; }
    public long IngestTimeMicros { get; set; }
    public double Bid { get; set; }
    public double Ask { get; set; }
    public long BidSize { get; set; }
    public long AskSize { get; set; }
    public int Source { get; set; }
    public long Sequence { get; set; }
    public bool EventTimeApproximate { get; set; }
}

/// <summary>Parquet wire format for an archived OHLCV bar.</summary>
internal sealed class BarParquetRow
{
    public long InstrumentId { get; set; }
    public int BarSize { get; set; }
    public long OpenTimeMicros { get; set; }
    public double Open { get; set; }
    public double High { get; set; }
    public double Low { get; set; }
    public double Close { get; set; }
    public long Volume { get; set; }
    public int Source { get; set; }
    public bool IsFinal { get; set; }
}

/// <summary>Parquet wire format for an archived trade print.</summary>
internal sealed class TradeParquetRow
{
    public long InstrumentId { get; set; }
    public long EventTimeMicros { get; set; }
    public long IngestTimeMicros { get; set; }
    public double Price { get; set; }
    public long Size { get; set; }
    public int Aggressor { get; set; }
    public int Source { get; set; }
    public long Sequence { get; set; }
    public bool EventTimeApproximate { get; set; }
}

/// <summary>JSON manifest dropped inside each archive zip. Lists every parquet file and the
/// range it covers so the restorer doesn't need to enumerate the zip blindly.</summary>
internal sealed class BundleManifest
{
    public int Version { get; set; } = 1;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public long RowsQuotes { get; set; }
    public long RowsBars { get; set; }
    public long RowsTrades { get; set; }
    public List<BundleFile> Files { get; set; } = new();
}

internal sealed class BundleFile
{
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;     // "quotes" | "bars" | "trades"
    public long InstrumentId { get; set; }
    public int BarSize { get; set; }                       // only meaningful for bars
    public long Rows { get; set; }
}
