using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Volume-at-price heatmap — a time-evolving volume profile. Trade prints are bucketed into
/// fixed time columns; within each column volume is summed per price bucket, so hot cells mark where
/// the most size traded in that interval (sequential colour). The per-column last trade is overlaid
/// as a price track. Needs the trade tape (IB-only); brokers without it leave the grid empty.
/// See <see cref="SingleInstrumentHeatmapViewModelBase"/> for the picker + streaming plumbing.
/// </summary>
public sealed partial class VolumeProfileHeatmapViewModel : SingleInstrumentHeatmapViewModelBase
{
    private const int PriceRows = 120;
    private const int MaxColumns = 240;

    /// <summary>Wall-clock width of one heatmap column.</summary>
    private static readonly TimeSpan BucketInterval = TimeSpan.FromSeconds(2);

    private readonly List<TradeColumn> _cols = new();

    public VolumeProfileHeatmapViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<VolumeProfileHeatmapViewModel> logger)
        : base(repository, hub, ingest, selector, logger)
    {
        Status = "Pick an instrument to stream its volume profile (needs trade tape — IB only).";
    }

    [ObservableProperty] private int _columnsFilled;
    [ObservableProperty] private double? _lastPrice;
    [ObservableProperty] private double _totalVolume;

    protected override void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct)
    {
        // L1 helps the ingest layer's aggressor inference; the trade tape is the actual source.
        AddStreamHandle(Ingest.Subscribe(instrument.Contract, broker));
        AddStreamHandle(Ingest.SubscribeTrades(instrument.Contract, broker)); // no-op handle on brokers without a tape
        PumpTrades(id, ct, OnTrade);
    }

    private void OnTrade(TradePrint trade)
    {
        var bucket = new DateTime(trade.EventTimeUtc.Ticks - (trade.EventTimeUtc.Ticks % BucketInterval.Ticks), DateTimeKind.Utc);
        if (_cols.Count == 0 || _cols[^1].Start != bucket)
        {
            _cols.Add(new TradeColumn(bucket));
            while (_cols.Count > MaxColumns) _cols.RemoveAt(0);
        }

        var col = _cols[^1];
        col.VolumeByPrice.TryGetValue(trade.Price, out var v);
        col.VolumeByPrice[trade.Price] = v + trade.Size;
        col.Last = trade.Price;

        LastPrice = trade.Price;
        TotalVolume += trade.Size;
        ColumnsFilled = _cols.Count;
    }

    protected override void ResetBuffers()
    {
        _cols.Clear();
        ColumnsFilled = 0;
        LastPrice = null;
        TotalVolume = 0;
    }

    protected override HeatmapFrame? BuildFrame()
    {
        if (_cols.Count == 0) return null;

        double pMin = double.MaxValue, pMax = double.MinValue;
        foreach (var col in _cols)
            foreach (var price in col.VolumeByPrice.Keys)
            {
                if (price < pMin) pMin = price;
                if (price > pMax) pMax = price;
            }
        if (pMin > pMax) return null;

        double span = pMax - pMin;
        if (span <= 0) { pMax = pMin + 1; span = 1; }

        int cols = _cols.Count;
        var cells = new double[PriceRows, cols];
        var lastLine = new double[cols];

        for (int c = 0; c < cols; c++)
        {
            var col = _cols[c];
            foreach (var (price, vol) in col.VolumeByPrice)
                cells[RowOf(price, pMax, span), c] += vol;
            lastLine[c] = col.Last > 0 ? col.Last : double.NaN;
        }

        return new HeatmapFrame(cells, 0, cols, pMin, pMax, HeatmapPalette.Sequential, lastLine);
    }

    private static int RowOf(double price, double pMax, double span)
    {
        int r = (int)Math.Round((pMax - price) / span * (PriceRows - 1));
        return Math.Clamp(r, 0, PriceRows - 1);
    }

    /// <summary>One time column: traded volume keyed by price, plus the last trade in the bucket.</summary>
    private sealed class TradeColumn(DateTime start)
    {
        public DateTime Start { get; } = start;
        public Dictionary<double, double> VolumeByPrice { get; } = new();
        public double Last { get; set; }
    }
}
