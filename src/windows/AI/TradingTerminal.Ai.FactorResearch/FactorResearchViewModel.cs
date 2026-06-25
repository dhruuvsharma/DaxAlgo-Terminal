using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Ml;
using TradingTerminal.Infrastructure.Backtest.Persistence;
using TradingTerminal.UI;

namespace TradingTerminal.Ai.FactorResearch;

/// <summary>
/// Factor research tab. Loads a parquet tick file (from the live recorder or a synth
/// file), aggregates into N-tick bars, computes a standard microstructure feature
/// panel, displays the pairwise correlation matrix, and runs a forward-return decile
/// sort on a user-chosen feature. The point: gauge whether a feature has predictive
/// power BEFORE bolting it onto a strategy.
/// </summary>
public sealed partial class FactorResearchViewModel : ViewModelBase
{
    private readonly ILogger<FactorResearchViewModel> _logger;
    private IReadOnlyList<FactorComputation.FeatureBar> _bars = Array.Empty<FactorComputation.FeatureBar>();

    public FactorResearchViewModel(ILogger<FactorResearchViewModel> logger)
    {
        _logger = logger;
        FeatureChoices = new[] { "LogReturn", "RollingVol", "MicropriceDev", "QueueImbalance", "Spread" };
        SelectedFeature = "QueueImbalance";
    }

    public IReadOnlyList<string> FeatureChoices { get; }
    public ObservableCollection<CorrelationRow> Correlations { get; } = new();
    public ObservableCollection<FactorComputation.DecileRow> Deciles { get; } = new();

    [ObservableProperty] private string _dataPath = "";
    [ObservableProperty] private int _barTicks = 100;
    [ObservableProperty] private int _volWindow = 20;
    [ObservableProperty] private int _forwardBars = 5;
    [ObservableProperty] private string _selectedFeature = "QueueImbalance";
    [ObservableProperty] private bool _isComputing;
    [ObservableProperty] private string _status = "Pick a parquet tick file and press Compute.";
    [ObservableProperty] private int _barsComputed;
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

        IsComputing = true;
        Status = "Loading ticks…";
        Correlations.Clear();
        Deciles.Clear();

        try
        {
            var ticks = new List<Tick>();
            await foreach (var t in ParquetTickReader.ReadAsync(DataPath, ct: CancellationToken.None))
                ticks.Add(t);

            Status = $"Computing features on {ticks.Count} ticks…";
            _bars = await Task.Run(() => FactorComputation.ComputeBars(ticks, BarTicks, VolWindow));
            BarsComputed = _bars.Count;

            var corr = FactorComputation.Correlations(_bars);
            for (var i = 0; i < corr.FeatureNames.Count; i++)
            {
                var row = new CorrelationRow { Feature = corr.FeatureNames[i] };
                for (var j = 0; j < corr.FeatureNames.Count; j++)
                    row.Cells.Add(new CorrelationCell(corr.FeatureNames[j], corr.Values[i, j]));
                Correlations.Add(row);
            }

            RunDecileSort();
            Status = $"Done. {BarsComputed} bars. Pick a feature to re-run the decile sort.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Factor compute failed");
            Status = $"Failed: {ex.Message}";
        }
        finally
        {
            IsComputing = false;
        }
    }

    [RelayCommand]
    private void RunDecileSort()
    {
        if (_bars.Count == 0) return;
        Deciles.Clear();
        var result = FactorComputation.DecileSort(_bars, SelectedFeature, ForwardBars);
        foreach (var row in result.Rows) Deciles.Add(row);
    }

    partial void OnSelectedFeatureChanged(string value) => RunDecileSort();
    partial void OnForwardBarsChanged(int value) => RunDecileSort();
}

public sealed class CorrelationRow
{
    public string Feature { get; set; } = "";
    public ObservableCollection<CorrelationCell> Cells { get; } = new();
}

public sealed record CorrelationCell(string Other, double Value)
{
    public string Display => Value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
}
