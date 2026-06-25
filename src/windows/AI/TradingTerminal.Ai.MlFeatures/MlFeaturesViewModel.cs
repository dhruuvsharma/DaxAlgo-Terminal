using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Ml;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.UI;

namespace TradingTerminal.Ai.MlFeatures;

/// <summary>
/// ML Features tab. Loads a parquet tick file, aggregates into N-tick bars with the
/// standard microstructure feature panel, then runs López de Prado triple-barrier
/// labelling forward over each candidate entry. Displays the +1 / 0 / -1 label
/// distribution and the first N labelled rows; export-to-CSV produces the same shape
/// the CLI's `features` subcommand emits, so downstream ML pipelines stay portable.
/// </summary>
public sealed partial class MlFeaturesViewModel : ViewModelBase
{
    private readonly ILogger<MlFeaturesViewModel> _logger;
    private IReadOnlyList<TripleBarrierLabeler.LabelledBar<(FactorComputation.FeatureBar Bar, int Index)>> _labelled = Array.Empty<TripleBarrierLabeler.LabelledBar<(FactorComputation.FeatureBar, int)>>();

    public MlFeaturesViewModel(ILogger<MlFeaturesViewModel> logger)
    {
        _logger = logger;
    }

    public ObservableCollection<LabelPreviewRow> Preview { get; } = new();

    [ObservableProperty] private string _dataPath = "";
    [ObservableProperty] private int _barTicks = 100;
    [ObservableProperty] private int _volWindow = 20;
    [ObservableProperty] private double _upperBarrier = 0.10;
    [ObservableProperty] private double _lowerBarrier = 0.10;
    [ObservableProperty] private int _timeoutBars = 20;
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string _status = "Pick a parquet tick file and press Compute.";
    [ObservableProperty] private int _barsComputed;
    [ObservableProperty] private int _positiveCount;
    [ObservableProperty] private int _neutralCount;
    [ObservableProperty] private int _negativeCount;
    [ObservableProperty] private string? _validationError;

    [RelayCommand]
    private async Task BrowseData()
    {
        var path = await UiFile.OpenAsync("Parquet files", new[] { "parquet" });
        if (path is not null) DataPath = path;
    }

    [RelayCommand]
    private async Task ComputeAsync()
    {
        ValidationError = null;
        if (string.IsNullOrWhiteSpace(DataPath) || !File.Exists(DataPath))
        {
            ValidationError = "Pick a valid parquet file first.";
            return;
        }
        if (BarTicks < 2) { ValidationError = "BarTicks must be ≥ 2."; return; }
        if (VolWindow < 2) { ValidationError = "VolWindow must be ≥ 2."; return; }
        if (UpperBarrier <= 0 || LowerBarrier <= 0) { ValidationError = "Barriers must be > 0."; return; }
        if (TimeoutBars <= 0) { ValidationError = "TimeoutBars must be > 0."; return; }

        IsComputing = true;
        Status = "Loading ticks…";
        Preview.Clear();
        BarsComputed = PositiveCount = NeutralCount = NegativeCount = 0;

        try
        {
            var ticks = new List<Tick>();
            await foreach (var t in ParquetTickReader.ReadAsync(DataPath, ct: CancellationToken.None))
                ticks.Add(t);

            Status = $"Aggregating {ticks.Count} ticks → bars…";
            var bars = await Task.Run(() => FactorComputation.ComputeBars(ticks, BarTicks, VolWindow));
            BarsComputed = bars.Count;

            Status = "Computing high/low per bar…";
            var highs = new double[bars.Count];
            var lows = new double[bars.Count];
            await Task.Run(() =>
            {
                for (var b = 0; b < bars.Count; b++)
                {
                    double hi = double.MinValue, lo = double.MaxValue;
                    for (var i = b * BarTicks; i < (b + 1) * BarTicks; i++)
                    {
                        var mid = (ticks[i].Bid + ticks[i].Ask) * 0.5;
                        if (mid > hi) hi = mid;
                        if (mid < lo) lo = mid;
                    }
                    highs[b] = hi; lows[b] = lo;
                }
            });

            Status = "Labelling (triple-barrier)…";
            var indexed = bars.Select((b, i) => (Bar: b, Index: i)).ToArray();
            _labelled = await Task.Run(() => TripleBarrierLabeler.Apply(
                indexed,
                close: x => x.Bar.Close,
                high: x => highs[x.Index],
                low: x => lows[x.Index],
                upperBarrier: UpperBarrier,
                lowerBarrier: LowerBarrier,
                timeoutBars: TimeoutBars));

            PositiveCount = _labelled.Count(l => l.Label == TripleBarrierLabeler.Label.Positive);
            NegativeCount = _labelled.Count(l => l.Label == TripleBarrierLabeler.Label.Negative);
            NeutralCount = _labelled.Count - PositiveCount - NegativeCount;

            foreach (var lb in _labelled.Take(500))
            {
                var b = lb.Bar.Bar;
                Preview.Add(new LabelPreviewRow(
                    b.TimestampUtc, b.Close, b.LogReturn, b.RollingVol, b.QueueImbalance,
                    (int)lb.Label, lb.BarsToOutcome));
            }

            Status = $"Done. {BarsComputed} bars · +1 {PositiveCount} · 0 {NeutralCount} · -1 {NegativeCount}. Showing first {Preview.Count} rows.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ML features compute failed");
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsComputing = false;
        }
    }

    [RelayCommand]
    private async Task ExportCsvAsync()
    {
        if (_labelled.Count == 0)
        {
            ValidationError = "Compute first.";
            return;
        }
        var savePath = await UiFile.SaveAsync("CSV", new[] { "csv" }, "features.csv");
        if (savePath is null) return;

        try
        {
            var ic = CultureInfo.InvariantCulture;
            var lines = new List<string>(_labelled.Count + 1)
            {
                "timestamp_utc,close,log_return,rolling_vol,microprice_dev,queue_imbalance,spread,label,bars_to_outcome"
            };
            foreach (var lb in _labelled)
            {
                var b = lb.Bar.Bar;
                lines.Add(string.Join(",", new[]
                {
                    b.TimestampUtc.ToString("O", ic),
                    b.Close.ToString("F6", ic),
                    b.LogReturn.ToString("F8", ic),
                    b.RollingVol.ToString("F8", ic),
                    b.MicropriceDeviation.ToString("F8", ic),
                    b.QueueImbalance.ToString("F6", ic),
                    b.Spread.ToString("F8", ic),
                    ((int)lb.Label).ToString(ic),
                    lb.BarsToOutcome.ToString(ic),
                }));
            }
            await File.WriteAllLinesAsync(savePath, lines);
            Status = $"Wrote {_labelled.Count} rows to {savePath}.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Export failed");
            Status = $"Export failed: {ex.Message}";
        }
    }
}

public sealed record LabelPreviewRow(
    DateTime TimestampUtc,
    double Close,
    double LogReturn,
    double RollingVol,
    double QueueImbalance,
    int Label,
    int BarsToOutcome);
