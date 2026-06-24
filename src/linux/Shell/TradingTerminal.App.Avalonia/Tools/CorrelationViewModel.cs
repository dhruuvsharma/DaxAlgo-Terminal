using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TradingTerminal.Core.Analytics;

namespace TradingTerminal.App.Avalonia.Tools;

/// <summary>
/// Avalonia Correlation-matrix window VM. Computes a Pearson correlation matrix via the broker-neutral
/// Core <see cref="CorrelationCalculator"/> over a set of return series and exposes it as a
/// <see cref="CorrelationMatrix"/> for the heatmap. Uses synthetic, partly-correlated return series
/// for now (real store/hub series wiring is the next step).
/// </summary>
public sealed partial class CorrelationViewModel : ObservableObject
{
    private static readonly string[] Names = { "SPY", "QQQ", "IWM", "GLD", "TLT", "BTC", "ETH", "VIX" };

    public CorrelationViewModel() => Run();

    [ObservableProperty] private CorrelationMatrix? _matrix;
    [ObservableProperty] private int _observations = 250;
    [ObservableProperty] private string _status = "";

    [RelayCommand]
    private void Run()
    {
        var rng = new Random(13);
        int n = Names.Length, m = Math.Max(30, Observations);

        // Two latent factors → realistic positive/negative cross-correlations.
        var f1 = new double[m];
        var f2 = new double[m];
        for (int t = 0; t < m; t++) { f1[t] = rng.NextGaussian(); f2[t] = rng.NextGaussian(); }

        var series = new List<IReadOnlyList<double>>(n);
        for (int i = 0; i < n; i++)
        {
            double b1 = Math.Cos(i), b2 = Math.Sin(i * 1.7);
            var r = new double[m];
            for (int t = 0; t < m; t++) r[t] = b1 * f1[t] + b2 * f2[t] + rng.NextGaussian() * 0.6;
            series.Add(r);
        }

        var values = CorrelationCalculator.PearsonMatrix(series);
        Matrix = new CorrelationMatrix(Names, values, m);
        Status = $"{n} instruments · {m} observations · Pearson";
    }
}

internal static class RandomGaussianExtensions
{
    public static double NextGaussian(this Random r)
    {
        double u1 = 1.0 - r.NextDouble(), u2 = 1.0 - r.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
    }
}
