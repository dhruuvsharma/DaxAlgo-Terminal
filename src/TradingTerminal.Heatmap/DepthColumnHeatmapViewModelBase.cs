using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Shared machinery for the two depth-snapshot heatmaps — raw resting size (depth) and signed
/// side pressure (imbalance). Both scroll a rolling buffer of L2 snapshots as time columns, bucket
/// price into rows, and overlay the mid; they differ only in the per-level cell value and the
/// palette. Subclasses supply <see cref="CellValue"/> and <see cref="Palette"/>.
/// </summary>
public abstract partial class DepthColumnHeatmapViewModelBase : SingleInstrumentHeatmapViewModelBase
{
    private const int PriceRows = 120;
    private const int MaxColumns = 240;

    private readonly List<DepthSnapshot> _columns = new();

    protected DepthColumnHeatmapViewModelBase(
        IMarketDataRepository repository, IMarketDataHub hub, IMarketDataIngest ingest,
        IBrokerSelector selector, ILogger logger)
        : base(repository, hub, ingest, selector, logger) { }

    [ObservableProperty] private double? _bestBid;
    [ObservableProperty] private double? _bestAsk;
    [ObservableProperty] private double? _mid;
    [ObservableProperty] private int _columnsFilled;
    [ObservableProperty] private DateTime? _lastUpdateUtc;

    /// <summary>Diverging for signed pressure, sequential for raw magnitude.</summary>
    protected abstract HeatmapPalette Palette { get; }

    /// <summary>The grid contribution of one book level: e.g. raw <paramref name="size"/> for the depth
    /// heatmap, or <c>+size</c> on the bid side and <c>-size</c> on the ask side for imbalance.</summary>
    protected abstract double CellValue(long size, bool isBid);

    protected override void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct)
    {
        AddStreamHandle(Ingest.Subscribe(instrument.Contract, broker));
        PumpDepth(id, ct, OnSnapshot);
    }

    private void OnSnapshot(DepthSnapshot snapshot)
    {
        _columns.Add(snapshot);
        while (_columns.Count > MaxColumns) _columns.RemoveAt(0);

        ColumnsFilled = _columns.Count;
        BestBid = snapshot.BestBid > 0 ? snapshot.BestBid : null;
        BestAsk = snapshot.BestAsk > 0 ? snapshot.BestAsk : null;
        Mid = BestBid is { } b && BestAsk is { } a ? (a + b) * 0.5 : null;
        LastUpdateUtc = snapshot.TimestampUtc;
    }

    protected override void ResetBuffers()
    {
        _columns.Clear();
        ColumnsFilled = 0;
        BestBid = BestAsk = Mid = null;
        LastUpdateUtc = null;
    }

    protected override HeatmapFrame? BuildFrame()
    {
        if (_columns.Count == 0) return null;

        double pMin = double.MaxValue, pMax = double.MinValue;
        foreach (var snap in _columns)
        {
            foreach (var l in snap.Bids) { if (l.Price < pMin) pMin = l.Price; if (l.Price > pMax) pMax = l.Price; }
            foreach (var l in snap.Asks) { if (l.Price < pMin) pMin = l.Price; if (l.Price > pMax) pMax = l.Price; }
        }
        if (pMin > pMax) return null;

        double span = pMax - pMin;
        if (span <= 0) { pMax = pMin + 1; span = 1; }

        int cols = _columns.Count;
        var cells = new double[PriceRows, cols];
        var midLine = new double[cols];

        for (int c = 0; c < cols; c++)
        {
            var snap = _columns[c];
            foreach (var l in snap.Bids) cells[RowOf(l.Price, pMax, span), c] += CellValue(l.Size, isBid: true);
            foreach (var l in snap.Asks) cells[RowOf(l.Price, pMax, span), c] += CellValue(l.Size, isBid: false);
            midLine[c] = snap.BestBid > 0 && snap.BestAsk > 0 ? (snap.BestBid + snap.BestAsk) * 0.5 : double.NaN;
        }

        return new HeatmapFrame(cells, 0, cols, pMin, pMax, Palette, midLine);
    }

    private static int RowOf(double price, double pMax, double span)
    {
        int r = (int)Math.Round((pMax - price) / span * (PriceRows - 1));
        return Math.Clamp(r, 0, PriceRows - 1);
    }
}
