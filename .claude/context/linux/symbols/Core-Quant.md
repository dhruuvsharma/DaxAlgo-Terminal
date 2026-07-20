# TradingTerminal.Core / Quant — public API surface (Linux/Avalonia)

Generated from public repository revision `b73fd6a72ae2`. Declaration lines only;
multi-line signatures show their first line. `[ObservableProperty]` generated properties are not listed.

## src/linux/Core/TradingTerminal.Core/Quant/CurveFitting.cs
```cs
    5: public enum CurveFitKind
   27: public static class CurveFitting
   30: public static int MinPoints(CurveFitKind kind) => kind switch
   43: public static double[]? FitEvaluate(
```

## src/linux/Core/TradingTerminal.Core/Quant/DeflatedSharpe.cs
```cs
    9: public sealed record DeflatedSharpeResult(double Dsr, double ExpectedMaxSharpe, int Trials);
   29: public static class DeflatedSharpe
   39: public static DeflatedSharpeResult Compute(
   81: public static double NormCdf(double x) => 0.5 * Erfc(-x / Math.Sqrt(2.0));
   87: public static double NormInv(double p)
```

## src/linux/Core/TradingTerminal.Core/Quant/EwRegression.cs
```cs
   13: public sealed record EwRegressionResult(
   23: public double Predict(double x) => Intercept + Slope * x;
   44: public static class EwRegression
   52: public static EwRegressionResult Fit(
  121: public static double[] Residuals(IReadOnlyList<double> x, IReadOnlyList<double> y, EwRegressionResult fit)
  137: public sealed class Accumulator
  143: public Accumulator(double delta = 0.9)
  150: public int Count => _n;
  153: public void Add(double x, double y, double weight = 1.0)
  168: public EwRegressionResult Result()
```

## src/linux/Core/TradingTerminal.Core/Quant/FirstPassage.cs
```cs
   14: public static class FirstPassage
   30: public static double WinProbability(double stop, double target, double mu, double sigma, double gapPenalty = 0.0)
   75: public static double ExpectedValue(double winProbability, double target, double stop, double roundTripCosts = 0.0)
```

## src/linux/Core/TradingTerminal.Core/Quant/HawkesProcess.cs
```cs
   28: public sealed class HawkesProcess
   42: public HawkesProcess(double baselineMu, double alpha, double beta)
   50: public double Excitation => _excitation;
   53: public void Reset()
   64: public void Add(double timeSeconds)
   84: public double Intensity(double nowSeconds)
   97: public static double IntensityAt(IReadOnlyList<double> eventTimes, double now, double mu, double alpha, double beta)
```

## src/linux/Core/TradingTerminal.Core/Quant/InformationCoefficient.cs
```cs
    9: public sealed record IcResult(double Pearson, double Spearman, int SampleSize);
   18: public static class InformationCoefficient
   24: public static IcResult Compute(IReadOnlyList<double> scores, IReadOnlyList<double> forwardReturns)
   50: public static IcResult[] Compute(double[,] scoreColumns, IReadOnlyList<double> forwardReturns)
```

## src/linux/Core/TradingTerminal.Core/Quant/IsotonicCalibration.cs
```cs
   12: public sealed record IsotonicMap(double[] X, double[] Y, int[] Counts, int TotalSamples)
   15: public double Evaluate(double c)
   42: public static class IsotonicCalibration
   49: public static IsotonicMap Fit(IReadOnlyList<double> composites, IReadOnlyList<double> forwardReturns, int minSamples = 100)
   96: public static IsotonicMap BinnedMean(IReadOnlyList<double> composites, IReadOnlyList<double> forwardReturns, int bins = 10)
```

## src/linux/Core/TradingTerminal.Core/Quant/KalmanPocPredictor.cs
```cs
   29: public sealed class KalmanPocPredictor
   42: public bool IsInitialized { get; private set; }
   45: public double Price => _p;
   48: public double Velocity => _v;
   57: public KalmanPocPredictor(double processNoise = 1e-4, double measurementNoise = 1e-2)
   65: public void Reset()
   77: public void Update(double observation)
  131: public (double Price, double Variance) Forecast(int stepsAhead)
```

## src/linux/Core/TradingTerminal.Core/Quant/KyleResidual.cs
```cs
   22: public sealed record KyleResidualResult(
   56: public static class KyleResidual
   63: public static KyleResidualResult Fit(IReadOnlyList<double> returns, IReadOnlyList<double> signedFlow)
```

## src/linux/Core/TradingTerminal.Core/Quant/LedoitWolf.cs
```cs
    9: public sealed record LedoitWolfResult(double[,] Covariance, double ShrinkageIntensity, int Dimension);
   25: public static class LedoitWolf
   31: public static LedoitWolfResult Estimate(double[,] observations)
  109: public static double[,] ToCorrelation(double[,] covariance)
  131: public static double[,] SafeInverse(double[,] matrix, double ridge = 1e-8)
```

## src/linux/Core/TradingTerminal.Core/Quant/NeweyWest.cs
```cs
   10: public sealed record NeweyWestResult(double StandardError, double TStat, int Lag);
   26: public static class NeweyWest
   29: public static int AutoLag(int n) => n <= 1 ? 0 : Math.Max(0, (int)Math.Floor(4.0 * Math.Pow(n / 100.0, 2.0 / 9.0)));
   37: public static NeweyWestResult SlopeStandardError(
```

## src/linux/Core/TradingTerminal.Core/Quant/SignalWeights.cs
```cs
   15: public static class SignalWeights
   24: public static double[] Solve(double[,] signalCovariance, IReadOnlyList<double> informationCoefficients)
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/ArimaModel.cs
```cs
    4: public sealed record ArimaFit(
   16: public sealed record ArimaForecastPoint(double Mean, double Lower95, double Upper95);
   31: public static class ArimaModel
   33: public const int MaxOrder = 5;
   36: public static int MinObservations(int p, int q) => Math.Max(40, 6 * (p + q + 1));
   42: public static ArimaFit? Fit(IReadOnlyList<double> series, int p, int d, int q)
  105: public static ArimaForecastPoint[] Forecast(ArimaFit fit, IReadOnlyList<double> series, int horizon)
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/GarchModel.cs
```cs
    8: public sealed record GarchFit(
   23: public double ForecastVariance(int h)
   38: public static class GarchModel
   40: public const int MinObservations = 60;
   43: public static GarchFit? Fit(IReadOnlyList<double> returns)
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/KalmanFilters.cs
```cs
    9: public sealed record KalmanResult(
   32: public static class KalmanFilters
   34: public const int MinObservations = 10;
   37: public static KalmanResult? LocalLevel(IReadOnlyList<double> y, double qOverR)
   79: public static KalmanResult? LocalLinearTrend(IReadOnlyList<double> y, double qOverR)
  136: public static KalmanResult? DynamicRegression(IReadOnlyList<double> y, IReadOnlyList<double> x, double qOverR)
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/NelderMead.cs
```cs
    9: public static class NelderMead
   11: public static (double[] X, double F)? Minimize(
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/Ols.cs
```cs
    7: public sealed record OlsResult(
   16: public double TStat(int j) => StandardErrors[j] > 1e-300 ? Beta[j] / StandardErrors[j] : 0.0;
   24: public static class Ols
   30: public static OlsResult? Fit(double[][] x, double[] y)
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/SeriesTransforms.cs
```cs
    4: public enum SeriesTransform
   31: public static class SeriesTransforms
   38: public static (double[] Series, int Consumed) Apply(
   86: public static (double[] Series, int Consumed) FracDiff(
  118: public static (double[] Mean, double[] Std) RollingMeanStd(IReadOnlyList<double> series, int window)
```

## src/linux/Core/TradingTerminal.Core/Quant/TimeSeries/StationarityTests.cs
```cs
   10: public sealed record StationarityTestResult(
   23: public static class StationarityTests
   43: public static StationarityTestResult? Adf(IReadOnlyList<double> series, int lags = -1)
  119: public static StationarityTestResult? Kpss(IReadOnlyList<double> series, int lags = -1)
  161: public static double[] Acf(IReadOnlyList<double> series, int maxLag)
  185: public static double AcfConfidenceBand(int n) => n > 0 ? 1.96 / Math.Sqrt(n) : 0.0;
```
