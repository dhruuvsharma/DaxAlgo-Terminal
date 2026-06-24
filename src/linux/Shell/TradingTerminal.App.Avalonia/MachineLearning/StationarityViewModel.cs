using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Quant.TimeSeries;

namespace TradingTerminal.App.Avalonia.MachineLearning;

/// <summary>
/// Avalonia "Stationarity &amp; Differencing" window VM. Runs the broker-neutral Core time-series
/// toolkit (<see cref="StationarityTests"/> ADF + KPSS over <see cref="SeriesTransforms"/>) on a
/// price series and reports the verdicts + the transformed series for plotting. Uses a synthetic
/// random-walk price series for now; wiring it to historical store bars is the next step.
/// </summary>
public sealed partial class StationarityViewModel : ObservableObject
{
    private readonly double[] _prices;

    public StationarityViewModel()
    {
        // Synthetic non-stationary price series (random walk with mild drift).
        var rng = new Random(7);
        _prices = new double[500];
        double p = 100;
        for (int i = 0; i < _prices.Length; i++) { p += (rng.NextDouble() - 0.48) * 0.5; _prices[i] = p; }
        Transforms = Enum.GetValues<SeriesTransform>();
        SelectedTransform = SeriesTransform.FirstDifference;
        Run();
    }

    public SeriesTransform[] Transforms { get; }

    [ObservableProperty] private SeriesTransform _selectedTransform;
    [ObservableProperty] private IReadOnlyList<double> _series = Array.Empty<double>();
    [ObservableProperty] private string _adf = "—";
    [ObservableProperty] private string _adfVerdict = "—";
    [ObservableProperty] private string _kpss = "—";
    [ObservableProperty] private string _kpssVerdict = "—";
    [ObservableProperty] private string _status = "";

    partial void OnSelectedTransformChanged(SeriesTransform value) => Run();

    [RelayCommand]
    private void Run()
    {
        var (series, _) = SeriesTransforms.Apply(_prices, SelectedTransform);
        Series = series;

        var adf = StationarityTests.Adf(series);
        var kpss = StationarityTests.Kpss(series);
        if (adf is not null)
        {
            Adf = adf.Statistic.ToString("0.000");
            AdfVerdict = adf.IsStationary ? "STATIONARY (reject unit root)" : "non-stationary";
        }
        if (kpss is not null)
        {
            Kpss = kpss.Statistic.ToString("0.000");
            KpssVerdict = kpss.IsStationary ? "STATIONARY" : "non-stationary (reject level)";
        }
        Status = $"{SelectedTransform} · {series.Length} obs · ADF {Adf} / KPSS {Kpss}";
    }
}
