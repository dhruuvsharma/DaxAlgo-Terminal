using CommunityToolkit.Mvvm.ComponentModel;
using TradingTerminal.Core.IndexRegime;
using TradingTerminal.Core.MarketData.AdvancedRegime;

namespace TradingTerminal.Strategies.IndexRegimeGraph;

/// <summary>Per-constituent data-load phase, surfaced as a spinner/glyph beside each row so the user
/// can see exactly which names are still being fetched and analysed during a refresh cycle.</summary>
public enum AssetLoadState
{
    /// <summary>Queued for this cycle, analysis not yet started.</summary>
    Pending,
    /// <summary>History is being fetched / the regime is being computed right now.</summary>
    Loading,
    /// <summary>Analysed from real broker history.</summary>
    Ready,
    /// <summary>Analysed, but from synthetic fallback history (no live/stored data).</summary>
    Synthetic,
    /// <summary>No data at all — the row is blank.</summary>
    NoData,
}

/// <summary>Band → hex colours (bipolar green→red) for the regime heatmap. Plain strings so the
/// view binds through the shared <c>StringToBrushConverter</c> and the models stay free of WPF
/// brush types.</summary>
internal static class BandColors
{
    /// <summary>Solid band colour — used for text and direction chips.</summary>
    public static string Hex(CellSignal band) => band switch
    {
        CellSignal.StrongUp => "#16C784",
        CellSignal.Up => "#3FB37A",
        CellSignal.Down => "#E0555A",
        CellSignal.StrongDown => "#E6383C",
        _ => "#8A93A3",
    };

    /// <summary>Low-alpha tint of the band colour — used as a heatmap cell / chip background.</summary>
    public static string Tint(CellSignal band) => band switch
    {
        CellSignal.StrongUp => "#3916C784",
        CellSignal.Up => "#243FB37A",
        CellSignal.Down => "#24E0555A",
        CellSignal.StrongDown => "#3AE6383C",
        _ => "#10FFFFFF",
    };

    public static string DirectionText(CellSignal band) => band switch
    {
        CellSignal.StrongUp => "STRONG UP",
        CellSignal.Up => "UP",
        CellSignal.Down => "DOWN",
        CellSignal.StrongDown => "STRONG DOWN",
        _ => "NEUTRAL",
    };

    /// <summary>Compact directional glyph for a single indicator cell.</summary>
    public static string Arrow(CellSignal band) => band switch
    {
        CellSignal.StrongUp => "▲▲",
        CellSignal.Up => "▲",
        CellSignal.Down => "▼",
        CellSignal.StrongDown => "▼▼",
        _ => "·",
    };
}

/// <summary>Short display labels for the 18 Advanced-Regime indicator rows.</summary>
internal static class IndicatorLabels
{
    public static string Short(AdvancedIndicatorRow row) => row switch
    {
        AdvancedIndicatorRow.Rsi => "RSI",
        AdvancedIndicatorRow.Macd => "MACD",
        AdvancedIndicatorRow.Cci => "CCI",
        AdvancedIndicatorRow.Ma9 => "MA9",
        AdvancedIndicatorRow.Ma21 => "MA21",
        AdvancedIndicatorRow.Ma50 => "MA50",
        AdvancedIndicatorRow.TripleMa => "3MA",
        AdvancedIndicatorRow.Vwap => "VWAP",
        AdvancedIndicatorRow.SuperTrend => "SuperTrend",
        AdvancedIndicatorRow.Atr => "ATR",
        AdvancedIndicatorRow.AtrRegression => "ATR reg",
        AdvancedIndicatorRow.Std => "Std dev",
        AdvancedIndicatorRow.PocPosition => "POC",
        AdvancedIndicatorRow.TrendRange => "Trend/Range",
        AdvancedIndicatorRow.Delta => "Delta",
        AdvancedIndicatorRow.CumulativeDelta => "Cum Delta",
        AdvancedIndicatorRow.VolumeBuySell => "Vol B/S",
        AdvancedIndicatorRow.Trend => "Trend",
        _ => row.ToString(),
    };
}

/// <summary>One timeframe cell in a constituent's collapsed heatmap row: the signed regime score for
/// that timeframe, coloured by its band.</summary>
public sealed record HeatCell(string Label, double Score, CellSignal Band, bool HasData)
{
    public string TintHex => HasData ? BandColors.Tint(Band) : "#0AFFFFFF";
    public string TextHex => HasData ? BandColors.Hex(Band) : "#5A6473";
    public string Text => !HasData ? "·" : $"{Score * 100:+0;-0;0}";
}

/// <summary>One indicator's result on one timeframe (a cell of the expanded drill-down grid).</summary>
public sealed record IndicatorCell(CellSignal Band, string ToolTip, bool HasData)
{
    public string Arrow => HasData ? BandColors.Arrow(Band) : "·";
    public string TintHex => HasData ? BandColors.Tint(Band) : "#0AFFFFFF";
    public string TextHex => HasData ? BandColors.Hex(Band) : "#5A6473";
}

/// <summary>One indicator row of the expanded drill-down: its label plus one <see cref="IndicatorCell"/>
/// per timeframe column (in header order).</summary>
public sealed record IndicatorRow(string Label, IReadOnlyList<IndicatorCell> Cells);

/// <summary>
/// One row of the regime heatmap — a constituent's blended direction and its per-timeframe cells,
/// plus the full indicator×timeframe drill-down exposed when the row is expanded. Mutable only in
/// <see cref="IsExpanded"/>; the data fields are rebuilt wholesale on each refresh. All colours are
/// hex strings so the view binds through the shared converters without the model touching WPF types.
/// </summary>
public sealed partial class ConstituentRow : ObservableObject
{
    public required string Symbol { get; init; }
    public required double IndexWeight { get; init; }
    public required double StockScore { get; init; }
    public required double Contribution { get; init; }
    public required CellSignal Band { get; init; }
    public required bool HasData { get; init; }
    public required IReadOnlyList<HeatCell> Cells { get; init; }
    public required IReadOnlyList<IndicatorRow> IndicatorMatrix { get; init; }

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Live data-load phase for this constituent — drives the per-row spinner/status.</summary>
    [ObservableProperty] private AssetLoadState _loadState;

    partial void OnLoadStateChanged(AssetLoadState value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowLoadStatus));
        OnPropertyChanged(nameof(LoadGlyph));
        OnPropertyChanged(nameof(LoadGlyphHex));
        OnPropertyChanged(nameof(LoadStatusText));
    }

    /// <summary>True while the row is fetching/computing — shows the spinner instead of the caret.</summary>
    public bool IsBusy => LoadState is AssetLoadState.Pending or AssetLoadState.Loading;

    /// <summary>Hide the per-row status caption once the row is fully ready; show it otherwise.</summary>
    public bool ShowLoadStatus => LoadState != AssetLoadState.Ready;

    /// <summary>Glyph shown beside the symbol once loading has finished (●/◷/∅).</summary>
    public string LoadGlyph => LoadState switch
    {
        AssetLoadState.Ready => "●",
        AssetLoadState.Synthetic => "◷",
        AssetLoadState.NoData => "∅",
        _ => string.Empty,
    };

    public string LoadGlyphHex => LoadState switch
    {
        AssetLoadState.Ready => "#16C784",
        AssetLoadState.Synthetic => "#FFB000",
        AssetLoadState.NoData => "#5A6473",
        _ => "#8A93A3",
    };

    public string LoadStatusText => LoadState switch
    {
        AssetLoadState.Pending => "queued",
        AssetLoadState.Loading => "loading…",
        AssetLoadState.Synthetic => "synthetic",
        AssetLoadState.NoData => "no data",
        _ => string.Empty,
    };

    public bool HasMatrix => IndicatorMatrix.Count > 0;

    public string ScoreHex => BandColors.Hex(Band);
    public string TintHex => BandColors.Tint(Band);
    public string DirectionText => BandColors.DirectionText(Band);
    public double RowOpacity => HasData ? 1.0 : 0.45;

    public string WeightText => IndexWeight.ToString("P1");
    public string ScoreText => HasData ? $"{StockScore * 100:+0.0;-0.0;0.0}" : "—";
    public string ContribText => HasData ? $"{Contribution * 100:+0.00;-0.00;0.00}" : "—";

    /// <summary>Projects an aggregated constituent into a heatmap row: the collapsed per-timeframe
    /// cells mapped onto <paramref name="tfLabels"/>, plus the full indicator×timeframe drill-down
    /// transposed from its raw Advanced-Regime columns.</summary>
    public static ConstituentRow From(ConstituentRegimeScore c, IReadOnlyList<string> tfLabels)
    {
        // Collapsed timeframe cells (one signed score per header column).
        var cells = new List<HeatCell>(tfLabels.Count);
        foreach (var label in tfLabels)
        {
            double score = 0;
            foreach (var ts in c.TimeframeScores)
                if (ts.Label == label) { score = ts.Score; break; }
            var band = c.HasData ? IndexRegimeAggregator.BandFor(score) : CellSignal.Neutral;
            cells.Add(new HeatCell(label, score, band, c.HasData));
        }

        return new ConstituentRow
        {
            Symbol = c.Symbol,
            IndexWeight = c.IndexWeight,
            StockScore = c.StockScore,
            Contribution = c.Contribution,
            Band = c.Band,
            HasData = c.HasData,
            Cells = cells,
            IndicatorMatrix = BuildMatrix(c, tfLabels),
        };
    }

    /// <summary>Transposes the raw Advanced-Regime columns (timeframe → indicator cells) into rows of
    /// (indicator → timeframe cells) so the drill-down reads indicators down, timeframes across.</summary>
    private static IReadOnlyList<IndicatorRow> BuildMatrix(ConstituentRegimeScore c, IReadOnlyList<string> tfLabels)
    {
        if (c.Columns.Count == 0) return Array.Empty<IndicatorRow>();

        // Index the raw cells by timeframe for O(1) lookup.
        var byTf = new Dictionary<string, IReadOnlyList<AdvancedRegimeCell>>(c.Columns.Count);
        foreach (var col in c.Columns) byTf[col.Timeframe.Label] = col.Cells;

        var indicators = Enum.GetValues<AdvancedIndicatorRow>();
        var matrix = new List<IndicatorRow>(indicators.Length);
        foreach (var ind in indicators)
        {
            var label = IndicatorLabels.Short(ind);
            var cells = new List<IndicatorCell>(tfLabels.Count);
            foreach (var tf in tfLabels)
            {
                var cell = Lookup(byTf, tf, ind);
                if (cell is { } fc)
                {
                    var suffix = fc.Value is { } v ? $" ({v:0.##}{fc.ValueSuffix})" : "";
                    cells.Add(new IndicatorCell(fc.Signal, $"{label} · {tf}: {fc.Glyph}{suffix}", true));
                }
                else
                {
                    cells.Add(new IndicatorCell(CellSignal.Neutral, $"{label} · {tf}: no data", false));
                }
            }
            matrix.Add(new IndicatorRow(label, cells));
        }
        return matrix;
    }

    private static AdvancedRegimeCell? Lookup(
        IReadOnlyDictionary<string, IReadOnlyList<AdvancedRegimeCell>> byTf, string tf, AdvancedIndicatorRow ind)
    {
        if (!byTf.TryGetValue(tf, out var cells)) return null;
        // Cells are emitted in enum order, so the fast path is a direct index.
        var idx = (int)ind;
        if (idx >= 0 && idx < cells.Count && cells[idx].Row == ind) return cells[idx];
        foreach (var cell in cells)
            if (cell.Row == ind) return cell;
        return null;
    }
}
