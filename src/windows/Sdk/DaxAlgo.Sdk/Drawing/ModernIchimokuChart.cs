using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// Bridges <see cref="ModernIchimoku"/> into the drawing stack: cloud band edges, classic lines,
/// and qualified / raw signal markers for <see cref="PriceChart"/>.
/// </summary>
public static class ModernIchimokuChart
{
    public readonly record struct Series(
        IReadOnlyList<double> Tenkan,
        IReadOnlyList<double> Kijun,
        IReadOnlyList<double> SpanA,
        IReadOnlyList<double> SpanB,
        IReadOnlyList<double> Chikou,
        IReadOnlyList<Signal> Markers,
        ModernIchimoku.ThicknessGrade LastGrade,
        double LastThicknessAtr,
        double LastPriceToCloudAtr,
        double FlatKijun,
        double FlatSenkouB);

    /// <summary>Runs Modern Ichimoku over bars and returns aligned series + markers.</summary>
    public static Series FromBars(IReadOnlyList<OhlcvBar>? bars)
    {
        if (bars is null || bars.Count == 0)
        {
            return new Series(
                Array.Empty<double>(), Array.Empty<double>(), Array.Empty<double>(),
                Array.Empty<double>(), Array.Empty<double>(), Array.Empty<Signal>(),
                ModernIchimoku.ThicknessGrade.Unknown, double.NaN, double.NaN,
                double.NaN, double.NaN);
        }

        var eng = new ModernIchimoku();
        var tenkan = new double[bars.Count];
        var kijun = new double[bars.Count];
        var spanA = new double[bars.Count];
        var spanB = new double[bars.Count];
        var chikou = new double[bars.Count];
        var markers = new List<Signal>();
        ModernIchimoku.Snapshot last = default;

        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var s = eng.Update(b.High, b.Low, b.Open, b.Close);
            last = s;
            tenkan[i] = FiniteOrNaN(s.Tenkan);
            kijun[i] = FiniteOrNaN(s.Kijun);
            spanA[i] = FiniteOrNaN(s.SpanA);
            spanB[i] = FiniteOrNaN(s.SpanB);

            if (s.RawSignal == ModernIchimoku.SignalKind.None) continue;

            var (kind, label) = MapSignal(s);
            // Qualified = full markers; raw-only = Note so grey/secondary reads stay visible.
            if (!s.Qualified) kind = SignalKind.Note;
            markers.Add(new Signal(i, s.Qualified ? b.Close : b.Low, kind, label));
        }

        // Chikou = close plotted `displacement` bars back → value at i is close[i+26].
        const int displacement = ModernIchimoku.DefaultDisplacement;
        for (var i = 0; i < bars.Count; i++)
        {
            var src = i + displacement;
            chikou[i] = src < bars.Count ? bars[src].Close : double.NaN;
        }

        return new Series(
            tenkan, kijun, spanA, spanB, chikou, markers,
            last.Grade, last.ThicknessAtr, last.PriceToCloudAtr,
            last.FlatKijun, last.FlatSenkouB);
    }

    private static (SignalKind Kind, string Label) MapSignal(ModernIchimoku.Snapshot s) => s.RawSignal switch
    {
        ModernIchimoku.SignalKind.TkBull => (SignalKind.Buy, s.Qualified ? "TK↑" : "tk"),
        ModernIchimoku.SignalKind.TkBear => (SignalKind.Sell, s.Qualified ? "TK↓" : "tk"),
        ModernIchimoku.SignalKind.KumoBull => (SignalKind.Buy, s.Qualified ? "K↑" : "k"),
        ModernIchimoku.SignalKind.KumoBear => (SignalKind.Sell, s.Qualified ? "K↓" : "k"),
        _ => (SignalKind.Note, ""),
    };

    private static double FiniteOrNaN(double v) => double.IsFinite(v) ? v : double.NaN;
}
