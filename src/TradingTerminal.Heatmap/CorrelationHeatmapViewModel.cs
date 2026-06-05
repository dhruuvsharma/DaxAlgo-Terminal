using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Analytics;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Heatmap;

/// <summary>
/// Rolling correlation heatmap: an NxN grid of the selected instruments coloured by live pairwise
/// Pearson correlation of mid log-returns (diverging palette, red = move together, blue = move apart),
/// recomputed every sample. Shares its math with the Live Correlation Matrix; this is the ScottPlot
/// heatmap rendering of the same idea. See <see cref="SampledHeatmapViewModelBase"/> for the sampler.
/// </summary>
public sealed class CorrelationHeatmapViewModel : SampledHeatmapViewModelBase
{
    private readonly Dictionary<InstrumentId, Queue<double>> _closes = new();

    public CorrelationHeatmapViewModel(
        IMarketDataRepository repository,
        IBrokerSelector selector,
        IMarketDataIngest ingest,
        IMarketDataHub hub,
        ILogger<CorrelationHeatmapViewModel> logger)
        : base(repository, selector, ingest, hub, logger)
    {
        StatusMessage = "Tick at least two instruments and press Start.";
    }

    protected override void OnStart()
    {
        _closes.Clear();
        foreach (var a in Active) _closes[a.Id] = new Queue<double>();
    }

    protected override void OnSample()
    {
        foreach (var a in Active)
        {
            if (LatestMid.TryGetValue(a.Id, out var mid) && mid > 0)
            {
                var closes = _closes[a.Id];
                closes.Enqueue(mid);
                while (closes.Count > WindowSize) closes.Dequeue();
            }
        }
    }

    protected override HeatmapFrame? BuildFrame()
    {
        var ready = Active.Where(a => _closes.TryGetValue(a.Id, out var q) && q.Count >= 3).ToList();
        if (ready.Count < 2) return null;

        var returns = ready.Select(a => CorrelationCalculator.LogReturns(_closes[a.Id].ToArray())).ToList();
        int common = returns.Min(r => r.Count);
        if (common < 2) return null;

        var trimmed = returns
            .Select(r => (IReadOnlyList<double>)r.Skip(r.Count - common).ToArray())
            .ToList();

        var matrix = CorrelationCalculator.PearsonMatrix(trimmed); // double[n, n]
        var labels = LabelFor(ready.Select(a => a.Instrument).ToList());

        int n = ready.Count;
        return new HeatmapFrame(matrix, 0, n, 0, n, HeatmapPalette.Diverging, Overlay: null, RowLabels: labels);
    }
}
