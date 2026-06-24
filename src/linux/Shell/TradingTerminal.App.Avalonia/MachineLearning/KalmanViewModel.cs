using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Quant.TimeSeries;

namespace TradingTerminal.App.Avalonia.MachineLearning;

/// <summary>
/// Avalonia "Kalman Filter" window VM. Runs the broker-neutral Core <see cref="KalmanFilters"/>
/// local-level / local-linear-trend filter over a noisy series and overlays the filtered state on
/// the observations. The single q/r knob (process/observation noise ratio) drives tracking speed.
/// Synthetic observations for now (store-bars wiring next).
/// </summary>
public sealed partial class KalmanViewModel : ObservableObject
{
    private readonly double[] _observed;

    public KalmanViewModel()
    {
        // Smooth latent level + observation noise → something worth filtering.
        var rng = new Random(5);
        _observed = new double[400];
        double level = 100;
        for (int i = 0; i < _observed.Length; i++)
        {
            level += Math.Sin(i / 30.0) * 0.15 + (rng.NextDouble() - 0.5) * 0.1;
            _observed[i] = level + (rng.NextDouble() - 0.5) * 2.0; // + obs noise
        }
        Run();
    }

    [ObservableProperty] private double _qOverR = 0.05;
    [ObservableProperty] private bool _useTrend;
    [ObservableProperty] private IReadOnlyList<double> _series = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _filtered = Array.Empty<double>();
    [ObservableProperty] private string _logLik = "—";
    [ObservableProperty] private string _obsNoise = "—";
    [ObservableProperty] private string _procNoise = "—";
    [ObservableProperty] private string _status = "";

    partial void OnQOverRChanged(double value) => Run();
    partial void OnUseTrendChanged(bool value) => Run();

    [RelayCommand]
    private void Run()
    {
        Series = _observed;
        var res = UseTrend
            ? KalmanFilters.LocalLinearTrend(_observed, QOverR)
            : KalmanFilters.LocalLevel(_observed, QOverR);
        if (res is not null)
        {
            Filtered = res.Level;
            LogLik = res.LogLikelihood.ToString("0.0");
            ObsNoise = res.ObservationNoise.ToString("0.0000");
            ProcNoise = res.ProcessNoise.ToString("0.000000");
            Status = $"{(UseTrend ? "local linear trend" : "local level")} · q/r={QOverR:0.###} · logL {LogLik}";
        }
        else { Filtered = Array.Empty<double>(); Status = "filter failed"; }
    }
}
