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

/// <summary>Parquet wire format for one archived L2 depth level. Depth is stored flattened — one
/// row per (snapshot, side, level) — matching the QuestDB <c>depth</c> table; the restorer regroups
/// rows sharing an event time back into a <see cref="TradingTerminal.Core.Domain.DepthSnapshot"/>.</summary>
internal sealed class DepthParquetRow
{
    public long InstrumentId { get; set; }
    public long EventTimeMicros { get; set; }
    public long IngestTimeMicros { get; set; }
    public string Side { get; set; } = string.Empty;       // "B" (bid) | "A" (ask)
    public int Level { get; set; }                          // 0 = best
    public double Price { get; set; }
    public long Size { get; set; }
    public int Source { get; set; }
}

/// <summary>
/// Keys the archiver stamps into each uploaded blob's <c>Metadata</c> so the per-document restore
/// path can group a document's parts, order them, and re-import the reassembled parquet — without an
/// inner manifest. <see cref="Format"/> = <see cref="PerDocument"/> distinguishes this layout from
/// the legacy single-zip bundle (whose parts carry none of these keys).
/// </summary>
internal static class ArchiveDocMeta
{
    public const string Format = "format";
    public const string PerDocument = "perdoc";
    public const string Kind = "kind";            // quotes | bars | trades | depth
    public const string DocKey = "doc";           // groups the parts of one logical document
    public const string PartIndex = "part";       // 1-based slice index within the document
    public const string PartCount = "parts";      // total slices in the document
    public const string InstrumentId = "instrument_id";
    public const string Symbol = "symbol";
    public const string Exchange = "exchange";
    public const string Broker = "broker";
    public const string BarSize = "bar_size";
    public const string Rows = "rows";
}

/// <summary>JSON manifest dropped inside a legacy archive zip. Lists every parquet file and the
/// range it covers so the restorer doesn't need to enumerate the zip blindly. Retained for restoring
/// archives produced before the per-document layout.</summary>
internal sealed class BundleManifest
{
    public int Version { get; set; } = 1;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }
    public long RowsQuotes { get; set; }
    public long RowsBars { get; set; }
    public long RowsTrades { get; set; }
    public long RowsDepth { get; set; }
    public List<BundleFile> Files { get; set; } = new();
}

internal sealed class BundleFile
{
    public string Path { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;     // "quotes" | "bars" | "trades" | "depth"
    public long InstrumentId { get; set; }
    public int BarSize { get; set; }                       // only meaningful for bars
    public long Rows { get; set; }
}
