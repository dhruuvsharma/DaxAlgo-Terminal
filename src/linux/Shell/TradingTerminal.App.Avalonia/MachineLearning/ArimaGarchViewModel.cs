using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Quant.TimeSeries;

namespace TradingTerminal.App.Avalonia.MachineLearning;

/// <summary>
/// Avalonia "ARIMA &amp; GARCH" window VM. Fits the broker-neutral Core <see cref="ArimaModel"/>
/// (mean) and <see cref="GarchModel"/> (volatility) to a price series and reports the coefficients,
/// plotting the series + the ARIMA forecast tail. Synthetic series for now (store-bars wiring next).
/// </summary>
public sealed partial class ArimaGarchViewModel : ObservableObject
{
    private readonly double[] _prices;
    private readonly double[] _returns;

    public ArimaGarchViewModel()
    {
        var rng = new Random(11);
        _prices = new double[400];
        double p = 100;
        for (int i = 0; i < _prices.Length; i++) { p += (rng.NextDouble() - 0.5) * 0.6; _prices[i] = p; }
        _returns = new double[_prices.Length - 1];
        for (int i = 1; i < _prices.Length; i++) _returns[i - 1] = _prices[i] - _prices[i - 1];
        Run();
    }

    [ObservableProperty] private int _p = 1;
    [ObservableProperty] private int _d = 1;
    [ObservableProperty] private int _q = 1;
    [ObservableProperty] private IReadOnlyList<double> _series = Array.Empty<double>();
    [ObservableProperty] private IReadOnlyList<double> _forecast = Array.Empty<double>();
    [ObservableProperty] private string _arima = "—";
    [ObservableProperty] private string _aic = "—";
    [ObservableProperty] private string _garch = "—";
    [ObservableProperty] private string _persistence = "—";
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    private void Run()
    {
        Series = _prices;
        var fit = ArimaModel.Fit(_prices, P, D, Q);
        if (fit is not null)
        {
            Arima = $"ARIMA({fit.P},{fit.D},{fit.Q})  σ²={fit.SigmaSquared:0.0000}";
            Aic = fit.Aic.ToString("0.0");
            var fc = ArimaModel.Forecast(fit, _prices, 40);
            var tail = new List<double>(_prices);
            tail.AddRange(fc.Select(x => x.Mean));
            Forecast = tail; // price followed by forecast means → the cyan tail is the forecast
        }
        else { Arima = "fit failed (need more observations)"; Forecast = _prices; }

        var g = GarchModel.Fit(_returns);
        if (g is not null)
        {
            Garch = $"ω={g.Omega:0.000e0} α={g.Alpha:0.000} β={g.Beta:0.000}";
            Persistence = g.Persistence.ToString("0.0000");
        }
        Status = $"{_prices.Length} bars · ARIMA AIC {Aic} · GARCH persistence {Persistence}";
    }
}
