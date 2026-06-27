using TradingTerminal.Core.Domain;

namespace TradingTerminal.Core.MarketData.AdvancedRegime;

/// <summary>
/// Pure calculator that turns per-timeframe bar series into an <see cref="AdvancedRegimeSnapshot"/>.
/// No I/O, no repository references — the Infrastructure provider pulls bars, aggregates per column
/// and hands the result here. Each of the 18 indicator rows is classified exactly as the original
/// Pine-script dashboard does (see the row-by-row comments); insufficient data yields a neutral
/// "—" cell rather than throwing.
/// </summary>
public static class AdvancedRegimeCalculator
{
    private const string Em = "—";

    public static AdvancedRegimeSnapshot Compute(
        string symbol,
        IReadOnlyList<(AdvancedTimeframe Timeframe, IReadOnlyList<Bar> Bars)> columns,
        AdvancedRegimeSettings settings,
        DateTime nowUtc)
    {
        settings ??= AdvancedRegimeSettings.Default;
        var resultColumns = new List<AdvancedRegimeColumn>(columns?.Count ?? 0);

        if (columns is not null)
        {
            foreach (var (tf, bars) in columns)
            {
                var (cells, trendScore, needle) = ComputeColumn(bars ?? Array.Empty<Bar>(), settings);
                resultColumns.Add(new AdvancedRegimeColumn(tf, cells, trendScore, needle));
            }
        }

        return new AdvancedRegimeSnapshot(symbol ?? string.Empty, resultColumns, nowUtc, Unavailable: false);
    }

    private static (IReadOnlyList<AdvancedRegimeCell> Cells, int TrendScore, int Needle) ComputeColumn(
        IReadOnlyList<Bar> bars, AdvancedRegimeSettings s)
    {
        var cells = new List<AdvancedRegimeCell>(18);

        var n = bars.Count;
        var closes = AdvancedRegimeBarIndicators.Closes(bars);
        var lastClose = n > 0 ? bars[^1].Close : double.NaN;
        var prevClose = n > 1 ? bars[^2].Close : double.NaN;

        // Shared computations consumed by both individual rows and the Trend composite.
        var rsi = Rsi(closes, s.RsiLength);
        var (macdLine, _, macdHist) = AdvancedRegimeBarIndicators.Macd(closes, s.MacdFast, s.MacdSlow, s.MacdSignal);
        var cci = AdvancedRegimeBarIndicators.Cci(bars, s.CciLength);
        var ma9 = AdvancedRegimeBarIndicators.SmaTail(closes, s.Ma9Length);
        var ma21 = AdvancedRegimeBarIndicators.SmaTail(closes, s.Ma21Length);
        var ma50 = AdvancedRegimeBarIndicators.SmaTail(closes, s.Ma50Length);
        var vwap = AdvancedRegimeBarIndicators.SessionVwap(bars);
        var (stLine, stBull) = AdvancedRegimeBarIndicators.SuperTrend(bars, s.SuperTrendFactor, s.SuperTrendAtrLength);

        // ---- Row 1: RSI ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.Rsi))
        {
            if (double.IsNaN(rsi)) cells.Add(EmptyCell(AdvancedIndicatorRow.Rsi));
            else if (rsi > s.RsiOverbought) cells.Add(new(AdvancedIndicatorRow.Rsi, "▼ OB", rsi, CellSignal.StrongDown));
            else if (rsi < s.RsiOversold) cells.Add(new(AdvancedIndicatorRow.Rsi, "▲ OS", rsi, CellSignal.StrongUp));
            else if (rsi > 50) cells.Add(new(AdvancedIndicatorRow.Rsi, "▲", rsi, CellSignal.Up));
            else cells.Add(new(AdvancedIndicatorRow.Rsi, "▼", rsi, CellSignal.Down));
        }

        // ---- Row 2: MACD (direction by histogram sign; value = MACD line) ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.Macd))
        {
            if (double.IsNaN(macdHist) || double.IsNaN(macdLine)) cells.Add(EmptyCell(AdvancedIndicatorRow.Macd));
            else if (macdHist > 0) cells.Add(new(AdvancedIndicatorRow.Macd, "▲", macdLine, CellSignal.Up));
            else if (macdHist < 0) cells.Add(new(AdvancedIndicatorRow.Macd, "▼", macdLine, CellSignal.Down));
            else cells.Add(new(AdvancedIndicatorRow.Macd, "─", macdLine, CellSignal.Neutral));
        }

        // ---- Row 3: CCI ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.Cci))
        {
            if (double.IsNaN(cci)) cells.Add(EmptyCell(AdvancedIndicatorRow.Cci));
            else if (cci > 100) cells.Add(new(AdvancedIndicatorRow.Cci, "▲▲ Strong", cci, CellSignal.StrongUp));
            else if (cci > 0) cells.Add(new(AdvancedIndicatorRow.Cci, "▲", cci, CellSignal.Up));
            else if (cci < -100) cells.Add(new(AdvancedIndicatorRow.Cci, "▼▼ Strong", cci, CellSignal.StrongDown));
            else cells.Add(new(AdvancedIndicatorRow.Cci, "▼", cci, CellSignal.Down));
        }

        // ---- Rows 4-6: MA9 / MA21 / MA50 (close vs MA) ----
        AddMaRow(cells, s, AdvancedIndicatorRow.Ma9, lastClose, ma9);
        AddMaRow(cells, s, AdvancedIndicatorRow.Ma21, lastClose, ma21);
        AddMaRow(cells, s, AdvancedIndicatorRow.Ma50, lastClose, ma50);

        // ---- Row 7: Triple MA stack (value = (fast-slow)/slow*100 %) ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.TripleMa))
        {
            var tFast = AdvancedRegimeBarIndicators.SmaTail(closes, s.TripleMaFast);
            var tMid = AdvancedRegimeBarIndicators.SmaTail(closes, s.TripleMaMid);
            var tSlow = AdvancedRegimeBarIndicators.SmaTail(closes, s.TripleMaSlow);
            if (double.IsNaN(tFast) || double.IsNaN(tMid) || double.IsNaN(tSlow) || tSlow == 0)
                cells.Add(EmptyCell(AdvancedIndicatorRow.TripleMa, "%"));
            else
            {
                var spreadPct = (tFast - tSlow) / tSlow * 100.0;
                if (tFast > tMid && tMid > tSlow) cells.Add(new(AdvancedIndicatorRow.TripleMa, "▲▲ Stack", spreadPct, CellSignal.StrongUp, "%"));
                else if (tFast < tMid && tMid < tSlow) cells.Add(new(AdvancedIndicatorRow.TripleMa, "▼▼ Stack", spreadPct, CellSignal.StrongDown, "%"));
                else cells.Add(new(AdvancedIndicatorRow.TripleMa, "─ Mix", spreadPct, CellSignal.Neutral, "%"));
            }
        }

        // ---- Row 8: VWAP (close vs VWAP) ----
        AddMaRow(cells, s, AdvancedIndicatorRow.Vwap, lastClose, vwap);

        // ---- Row 9: SuperTrend ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.SuperTrend))
        {
            if (double.IsNaN(stLine)) cells.Add(EmptyCell(AdvancedIndicatorRow.SuperTrend));
            else if (stBull) cells.Add(new(AdvancedIndicatorRow.SuperTrend, "▲ Bull", stLine, CellSignal.Up));
            else cells.Add(new(AdvancedIndicatorRow.SuperTrend, "▼ Bear", stLine, CellSignal.Down));
        }

        // ---- Rows 10-11: ATR + ATR regression ----
        var atrSeries = AdvancedRegimeBarIndicators.TrueRangeAtr(bars, s.AtrLength);
        var atrLast = n > 0 ? atrSeries[^1] : double.NaN;
        var atrReg = SmaOfSeriesTail(atrSeries, s.AtrRegressionLength);

        if (s.IsRowEnabled(AdvancedIndicatorRow.Atr))
        {
            if (double.IsNaN(atrLast) || double.IsNaN(atrReg) || double.IsNaN(prevClose))
                cells.Add(EmptyCell(AdvancedIndicatorRow.Atr));
            else
            {
                var expanding = atrLast > atrReg;
                var up = lastClose > prevClose;
                if (expanding && up) cells.Add(new(AdvancedIndicatorRow.Atr, "▲▲ Brk↑", atrLast, CellSignal.StrongUp));
                else if (expanding) cells.Add(new(AdvancedIndicatorRow.Atr, "▼▼ Brk↓", atrLast, CellSignal.StrongDown));
                else if (up) cells.Add(new(AdvancedIndicatorRow.Atr, "▲ Cnt", atrLast, CellSignal.Up));
                else cells.Add(new(AdvancedIndicatorRow.Atr, "▼ Cnt", atrLast, CellSignal.Down));
            }
        }

        if (s.IsRowEnabled(AdvancedIndicatorRow.AtrRegression))
        {
            if (double.IsNaN(atrReg) || double.IsNaN(atrLast)) cells.Add(EmptyCell(AdvancedIndicatorRow.AtrRegression));
            else if (atrLast > atrReg) cells.Add(new(AdvancedIndicatorRow.AtrRegression, "▲ Exp", atrReg, CellSignal.Up));
            else cells.Add(new(AdvancedIndicatorRow.AtrRegression, "▼ Cnt", atrReg, CellSignal.Down));
        }

        // ---- Row 12: Std (stdev of close) ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.Std))
        {
            var stdSeries = StdevSeries(closes, s.StdLength);
            var stdLast = stdSeries.Length > 0 ? stdSeries[^1] : double.NaN;
            var stdSma = SmaOfSeriesTail(stdSeries, s.StdLength);
            if (double.IsNaN(stdLast) || double.IsNaN(stdSma) || double.IsNaN(prevClose))
                cells.Add(EmptyCell(AdvancedIndicatorRow.Std));
            else
            {
                var hi = stdLast > stdSma;
                var up = lastClose > prevClose;
                if (hi && up) cells.Add(new(AdvancedIndicatorRow.Std, "▲▲ HiVol↑", stdLast, CellSignal.StrongUp));
                else if (hi) cells.Add(new(AdvancedIndicatorRow.Std, "▼▼ HiVol↓", stdLast, CellSignal.StrongDown));
                else if (up) cells.Add(new(AdvancedIndicatorRow.Std, "▲ LoVol", stdLast, CellSignal.Up));
                else cells.Add(new(AdvancedIndicatorRow.Std, "▼ LoVol", stdLast, CellSignal.Down));
            }
        }

        // ---- Row 13: POC position (close vs POC) ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.PocPosition))
        {
            var poc = AdvancedRegimeBarIndicators.PocApprox(bars, s.PocLookback);
            AddMaCell(cells, AdvancedIndicatorRow.PocPosition, lastClose, poc);
        }

        // ---- Row 14: Trend/range (range position % + 3-bar bias) ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.TrendRange))
        {
            var pos = AdvancedRegimeBarIndicators.RangePosition(bars, s.TrendRangeLength);
            if (double.IsNaN(pos) || n < 3) cells.Add(EmptyCell(AdvancedIndicatorRow.TrendRange, "%"));
            else
            {
                var bias = 0;
                for (int i = n - 3; i < n; i++) bias += bars[i].Close > bars[i].Open ? 1 : -1;
                if (pos > 70 && bias > 0) cells.Add(new(AdvancedIndicatorRow.TrendRange, "▲▲ Top", pos, CellSignal.StrongUp, "%"));
                else if (pos < 30 && bias < 0) cells.Add(new(AdvancedIndicatorRow.TrendRange, "▼▼ Bot", pos, CellSignal.StrongDown, "%"));
                else if (bias > 0) cells.Add(new(AdvancedIndicatorRow.TrendRange, "▲", pos, CellSignal.Up, "%"));
                else if (bias < 0) cells.Add(new(AdvancedIndicatorRow.TrendRange, "▼", pos, CellSignal.Down, "%"));
                else cells.Add(new(AdvancedIndicatorRow.TrendRange, "─", pos, CellSignal.Neutral, "%"));
            }
        }

        // ---- Row 15: Delta (last bar) ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.Delta))
        {
            if (n == 0) cells.Add(EmptyCell(AdvancedIndicatorRow.Delta));
            else
            {
                var d = AdvancedRegimeBarIndicators.BarDelta(bars[^1]);
                cells.Add(SignCell(AdvancedIndicatorRow.Delta, d));
            }
        }

        // ---- Row 16: Cumulative delta over lookback ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.CumulativeDelta))
        {
            if (n == 0) cells.Add(EmptyCell(AdvancedIndicatorRow.CumulativeDelta));
            else
            {
                var start = Math.Max(0, n - s.DeltaLookback);
                double cum = 0;
                for (int i = start; i < n; i++) cum += AdvancedRegimeBarIndicators.BarDelta(bars[i]);
                cells.Add(SignCell(AdvancedIndicatorRow.CumulativeDelta, cum));
            }
        }

        // ---- Row 17: Volume buy/sell ratio over lookback ----
        if (s.IsRowEnabled(AdvancedIndicatorRow.VolumeBuySell))
        {
            if (n == 0) cells.Add(EmptyCell(AdvancedIndicatorRow.VolumeBuySell));
            else
            {
                var start = Math.Max(0, n - s.DeltaLookback);
                long buyVol = 0, sellVol = 0;
                for (int i = start; i < n; i++)
                {
                    if (bars[i].Close > bars[i].Open) buyVol += bars[i].Volume;
                    else if (bars[i].Close < bars[i].Open) sellVol += bars[i].Volume;
                }
                double? ratio = sellVol == 0 ? null : (double)buyVol / sellVol;
                if (buyVol > sellVol) cells.Add(new(AdvancedIndicatorRow.VolumeBuySell, "▲ Buy", ratio, CellSignal.Up));
                else if (buyVol < sellVol) cells.Add(new(AdvancedIndicatorRow.VolumeBuySell, "▼ Sell", ratio, CellSignal.Down));
                else cells.Add(new(AdvancedIndicatorRow.VolumeBuySell, "─", ratio, CellSignal.Neutral));
            }
        }

        // ---- Row 18: Trend composite (score in -8..+8) ----
        int trendScore = 0;
        int needle = 0;
        if (s.IsRowEnabled(AdvancedIndicatorRow.Trend))
        {
            // Need every component finite to compute the composite.
            var ready = !double.IsNaN(ma9) && !double.IsNaN(ma21) && !double.IsNaN(ma50)
                && !double.IsNaN(vwap) && !double.IsNaN(macdLine) && !double.IsNaN(rsi)
                && !double.IsNaN(cci) && !double.IsNaN(stLine) && n > 0;
            if (!ready)
            {
                cells.Add(EmptyCell(AdvancedIndicatorRow.Trend, "/8"));
            }
            else
            {
                int score = 0;
                score += lastClose > ma9 ? 1 : -1;
                score += lastClose > ma21 ? 1 : -1;
                score += lastClose > ma50 ? 1 : -1;
                score += lastClose > vwap ? 1 : -1;
                score += macdLine > 0 ? 1 : -1;
                score += rsi > 50 ? 1 : -1;
                score += cci > 0 ? 1 : -1;
                score += stBull ? 1 : -1;

                trendScore = score;
                var pct = score / 8.0;
                var angle = (int)Math.Round(Math.Clamp(pct, -1, 1) * 90);
                needle = angle;

                string glyph;
                CellSignal signal;
                if (pct >= 0.75) { glyph = $"▲▲▲ +{angle}°"; signal = CellSignal.StrongUp; }
                else if (pct >= 0.40) { glyph = $"▲▲ +{angle}°"; signal = CellSignal.Up; }
                else if (pct >= 0.10) { glyph = $"▲ +{angle}°"; signal = CellSignal.Up; }
                else if (pct > -0.10) { glyph = $"─ {angle}°"; signal = CellSignal.Neutral; }
                else if (pct > -0.40) { glyph = $"▼ {angle}°"; signal = CellSignal.Down; }
                else if (pct > -0.75) { glyph = $"▼▼ {angle}°"; signal = CellSignal.Down; }
                else { glyph = $"▼▼▼ {angle}°"; signal = CellSignal.StrongDown; }

                cells.Add(new(AdvancedIndicatorRow.Trend, glyph, score, signal, "/8"));
            }
        }

        return (cells, trendScore, needle);
    }

    private static void AddMaRow(List<AdvancedRegimeCell> cells, AdvancedRegimeSettings s,
        AdvancedIndicatorRow row, double close, double ma)
    {
        if (!s.IsRowEnabled(row)) return;
        AddMaCell(cells, row, close, ma);
    }

    private static void AddMaCell(List<AdvancedRegimeCell> cells, AdvancedIndicatorRow row, double close, double ma)
    {
        if (double.IsNaN(ma) || double.IsNaN(close)) { cells.Add(EmptyCell(row)); return; }
        if (close > ma) cells.Add(new(row, "▲", ma, CellSignal.Up));
        else if (close < ma) cells.Add(new(row, "▼", ma, CellSignal.Down));
        else cells.Add(new(row, "─", ma, CellSignal.Neutral));
    }

    private static AdvancedRegimeCell SignCell(AdvancedIndicatorRow row, double value)
    {
        if (value > 0) return new(row, "▲", value, CellSignal.Up);
        if (value < 0) return new(row, "▼", value, CellSignal.Down);
        return new(row, "─", value, CellSignal.Neutral);
    }

    private static AdvancedRegimeCell EmptyCell(AdvancedIndicatorRow row, string? suffix = null) =>
        new(row, Em, null, CellSignal.Neutral, suffix);

    /// <summary>Final-bar Wilder RSI via the shared streaming primitive; NaN if not warmed up.</summary>
    private static double Rsi(IReadOnlyList<double> closes, int period)
    {
        if (closes.Count <= period) return double.NaN;
        var rsi = new Indicators.RelativeStrengthIndex(period);
        foreach (var c in closes) rsi.Push(c);
        return rsi.IsReady ? rsi.Value : double.NaN;
    }

    /// <summary>Rolling sample-stdev of <paramref name="closes"/> at each index, NaN until the window
    /// fills (aligned to the input).</summary>
    private static double[] StdevSeries(IReadOnlyList<double> closes, int period)
    {
        var n = closes.Count;
        var outp = new double[n];
        for (int i = 0; i < n; i++) outp[i] = double.NaN;
        if (period <= 0) return outp;
        var sd = new Indicators.RollingStdev(period);
        for (int i = 0; i < n; i++)
        {
            sd.Push(closes[i]);
            if (sd.IsReady) outp[i] = sd.Value;
        }
        return outp;
    }

    /// <summary>SMA over the final <paramref name="length"/> finite values of a possibly-NaN-prefixed
    /// series. Skips the NaN warmup prefix; NaN if fewer than <paramref name="length"/> finite values.</summary>
    private static double SmaOfSeriesTail(double[] series, int length)
    {
        if (series is null || length <= 0) return double.NaN;
        double sum = 0;
        int count = 0;
        for (int i = series.Length - 1; i >= 0 && count < length; i--)
        {
            if (double.IsNaN(series[i])) break;
            sum += series[i];
            count++;
        }
        return count >= length ? sum / length : double.NaN;
    }
}
