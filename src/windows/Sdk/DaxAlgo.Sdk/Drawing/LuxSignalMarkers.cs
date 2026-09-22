using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Domain;

namespace DaxAlgo.Sdk.Drawing;

/// <summary>
/// Bridges <see cref="LuxStyleSignals"/> into the drawing stack: <see cref="Signal"/> markers for
/// <see cref="PriceChart"/> / <see cref="Signals.Draw"/>, plus an optional trail overlay series.
/// Authored units should prefer this path over the Charts WebView markers.
/// </summary>
public static class LuxSignalMarkers
{
    /// <summary>
    /// Runs <see cref="LuxStyleSignals"/> over <paramref name="bars"/> and returns chart markers
    /// (bar index + price). Strong signals use the same Buy/Sell kinds with a "+" label.
    /// </summary>
    public static (IReadOnlyList<Signal> Markers, IReadOnlyList<double> Trail) FromBars(
        IReadOnlyList<OhlcvBar>? bars,
        int fast = 8,
        int slow = 21,
        int atrPeriod = 14)
    {
        if (bars is null || bars.Count == 0)
            return (Array.Empty<Signal>(), Array.Empty<double>());

        var eng = new LuxStyleSignals(fast, slow, atrPeriod);
        var markers = new List<Signal>();
        var trail = new double[bars.Count];
        for (var i = 0; i < bars.Count; i++)
        {
            var b = bars[i];
            var s = eng.Update(b.High, b.Low, b.Close);
            trail[i] = s.Trail > 0 && double.IsFinite(s.Trail) ? s.Trail : double.NaN;
            if (s.Kind == LuxStyleSignals.Kind.None) continue;

            var (kind, label) = s.Kind switch
            {
                LuxStyleSignals.Kind.ConfirmBuy => (SignalKind.Buy, "Buy"),
                LuxStyleSignals.Kind.StrongBuy => (SignalKind.Buy, "Buy+"),
                LuxStyleSignals.Kind.ConfirmSell => (SignalKind.Sell, "Sell"),
                LuxStyleSignals.Kind.StrongSell => (SignalKind.Sell, "Sell+"),
                LuxStyleSignals.Kind.ContraBuy => (SignalKind.Note, "C↑"),
                LuxStyleSignals.Kind.ContraSell => (SignalKind.Note, "C↓"),
                _ => (SignalKind.Note, null),
            };
            markers.Add(new Signal(i, s.Price, kind, label));
        }

        return (markers, trail);
    }
}
