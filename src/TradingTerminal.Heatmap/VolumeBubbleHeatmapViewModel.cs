using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;
using TradingTerminal.UI;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Volume bubble heatmap: every trade print is drawn as a bubble at (time, price), sized by trade
/// volume and coloured by aggressor side (green = buy, red = sell, grey = unknown). Unlike the
/// gridded volume-at-price heatmap this keeps each print distinct, so blocks and sweeps stand out.
/// Needs the trade tape (IB-only); brokers without it leave it empty. See
/// <see cref="SingleInstrumentHeatmapViewModelBase"/> for the picker + streaming plumbing.
/// </summary>
public sealed partial class VolumeBubbleHeatmapViewModel : SingleInstrumentHeatmapViewModelBase
{
    /// <summary>How many recent trades stay on screen.</summary>
    private const int MaxBubbles = 600;

    /// <summary>Min/max bubble diameter in pixels; trade size maps onto this by √(size/max).</summary>
    private const float MinPx = 4f;
    private const float SpanPx = 22f;

    private readonly Queue<TradeMark> _trades = new();

    public VolumeBubbleHeatmapViewModel(
        IMarketDataRepository repository,
        IMarketDataHub hub,
        IMarketDataIngest ingest,
        IBrokerSelector selector,
        ILogger<VolumeBubbleHeatmapViewModel> logger)
        : base(repository, hub, ingest, selector, logger)
    {
        Status = "Pick an instrument to stream its volume bubbles (needs trade tape — IB only).";
    }

    [ObservableProperty] private double? _lastPrice;
    [ObservableProperty] private double _totalVolume;
    [ObservableProperty] private int _bubbleCount;

    protected override void StartPumps(SignalInstrument instrument, BrokerKind broker, InstrumentId id, CancellationToken ct)
    {
        AddStreamHandle(Ingest.Subscribe(instrument.Contract, broker));      // L1 aids aggressor inference
        AddStreamHandle(Ingest.SubscribeTrades(instrument.Contract, broker)); // no-op handle on brokers without a tape
        PumpTrades(id, ct, OnTrade);
    }

    private void OnTrade(TradePrint trade)
    {
        var side = trade.Aggressor switch
        {
            AggressorSide.Buy => BubbleSide.Buy,
            AggressorSide.Sell => BubbleSide.Sell,
            _ => BubbleSide.Unknown,
        };

        _trades.Enqueue(new TradeMark(trade.EventTimeUtc, trade.Price, trade.Size, side));
        while (_trades.Count > MaxBubbles) _trades.Dequeue();

        LastPrice = trade.Price;
        TotalVolume += trade.Size;
        BubbleCount = _trades.Count;
    }

    protected override void ResetBuffers()
    {
        _trades.Clear();
        LastPrice = null;
        TotalVolume = 0;
        BubbleCount = 0;
    }

    protected override IHeatmapFrame? BuildFrame()
    {
        if (_trades.Count == 0) return null;

        var arr = _trades.ToArray();
        double pMin = double.MaxValue, pMax = double.MinValue;
        double xMin = double.MaxValue, xMax = double.MinValue;
        long maxSize = 1;
        foreach (var t in arr)
        {
            if (t.Price < pMin) pMin = t.Price;
            if (t.Price > pMax) pMax = t.Price;
            if (t.Size > maxSize) maxSize = t.Size;
            double x = t.Time.ToOADate();
            if (x < xMin) xMin = x;
            if (x > xMax) xMax = x;
        }

        var bubbles = new HeatBubble[arr.Length];
        for (int i = 0; i < arr.Length; i++)
        {
            var t = arr[i];
            float px = MinPx + SpanPx * (float)Math.Sqrt((double)t.Size / maxSize);
            bubbles[i] = new HeatBubble(t.Time.ToOADate(), t.Price, px, t.Side);
        }

        double pPad = (pMax - pMin) * 0.04;
        if (pPad <= 0) pPad = Math.Abs(pMax) * 0.001 + 1e-6;
        double xPad = (xMax - xMin) * 0.02;
        if (xPad <= 0) xPad = 1.0 / 86400.0; // ~1 second in OADate units

        return new BubbleFrame(bubbles, xMin - xPad, xMax + xPad, pMin - pPad, pMax + pPad);
    }

    /// <summary>One trade retained for the bubble view.</summary>
    private readonly record struct TradeMark(DateTime Time, double Price, long Size, BubbleSide Side);
}
