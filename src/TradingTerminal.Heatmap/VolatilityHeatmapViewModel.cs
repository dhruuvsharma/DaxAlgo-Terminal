using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Cross-asset volatility heatmap: instruments on Y, time on X, colour = each instrument's rolling
/// realised volatility (std of recent mid log-returns) at that moment. A live "what's moving" grid —
/// hot rows are the instruments currently churning. See <see cref="SampledHeatmapViewModelBase"/> for
/// the multi-select picker + sampler.
/// </summary>
public sealed class VolatilityHeatmapViewModel : SampledHeatmapViewModelBase
{
    /// <summary>How many returns feed each realised-vol estimate.</summary>
    private const int VolWindow = 20;

    /// <summary>How many time columns scroll across.</summary>
    private const int MaxColumns = 240;

    private readonly Dictionary<InstrumentId, Queue<double>> _closes = new();
    private readonly Dictionary<InstrumentId, Queue<double>> _vol = new();

    public VolatilityHeatmapViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        ILogger<VolatilityHeatmapViewModel> logger)
        : base(repository, selector, ingest, hub, logger)
    {
        StatusMessage = "Tick instruments and press Start to map their live volatility.";
    }

    protected override void OnStart()
    {
        _closes.Clear();
        _vol.Clear();
        foreach (var a in Active) { _closes[a.Id] = new Queue<double>(); _vol[a.Id] = new Queue<double>(); }
    }

    protected override void OnSample()
    {
        foreach (var a in Active)
        {
            if (!LatestMid.TryGetValue(a.Id, out var mid) || mid <= 0) continue;

            var closes = _closes[a.Id];
            closes.Enqueue(mid);
            while (closes.Count > VolWindow + 1) closes.Dequeue();

            var vol = _vol[a.Id];
            vol.Enqueue(RealizedVol(closes));
            while (vol.Count > MaxColumns) vol.Dequeue();
        }
    }

    protected override HeatmapFrame? BuildFrame()
    {
        var ready = Active.Where(a => _vol.TryGetValue(a.Id, out var q) && q.Count >= 1).ToList();
        if (ready.Count < 1) return null;

        int cols = ready.Min(a => _vol[a.Id].Count);
        if (cols < 1) return null;

        int rows = ready.Count;
        var cells = new double[rows, cols];
        for (int r = 0; r < rows; r++)
        {
            var arr = _vol[ready[r].Id].ToArray();
            int off = arr.Length - cols;
            for (int c = 0; c < cols; c++) cells[r, c] = arr[off + c];
        }

        var labels = LabelFor(ready.Select(a => a.Instrument).ToList());
        return new HeatmapFrame(cells, 0, cols, 0, rows, HeatmapPalette.Sequential, Overlay: null, RowLabels: labels);
    }

    private static double RealizedVol(Queue<double> closes)
    {
        if (closes.Count < 2) return 0;
        var returns = CorrelationCalculator.LogReturns(closes.ToArray());
        if (returns.Count < 1) return 0;

        double mean = 0;
        foreach (var r in returns) mean += r;
        mean /= returns.Count;

        double variance = 0;
        foreach (var r in returns) { double d = r - mean; variance += d * d; }
        variance /= returns.Count;

        return Math.Sqrt(variance);
    }
}
