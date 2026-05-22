using System.Globalization;

namespace TradingTerminal.Core.Regime;

/// <summary>
/// Pure, deterministic scoring of the ten regime sub-signals into a 0–100 composite. This is
/// a faithful C# port of the upstream <c>worldmonitor</c> <c>seed-fear-greed.mjs</c> algorithm
/// (weights, normalisation ranges, and degradation rules preserved). No I/O — feed it a fully
/// populated <see cref="RegimeInputs"/> and it returns a <see cref="MarketRegimeSnapshot"/>.
/// </summary>
public static class MarketRegimeCalculator
{
    /// <summary>Composite weights — sum to 1.0. Matches the upstream WEIGHTS table.</summary>
    public static double Weight(RegimeCategory c) => c switch
    {
        RegimeCategory.Sentiment => 0.10,
        RegimeCategory.Volatility => 0.10,
        RegimeCategory.Positioning => 0.15,
        RegimeCategory.Trend => 0.10,
        RegimeCategory.Breadth => 0.10,
        RegimeCategory.Momentum => 0.10,
        RegimeCategory.Liquidity => 0.15,
        RegimeCategory.Credit => 0.10,
        RegimeCategory.Macro => 0.05,
        RegimeCategory.CrossAsset => 0.05,
        _ => 0.0,
    };

    public static MarketRegimeSnapshot Compute(RegimeInputs i, double? previousScore, DateTime nowUtc)
    {
        var cats = new[]
        {
            ScoreSentiment(i),
            ScoreVolatility(i),
            ScorePositioning(i),
            ScoreTrend(i),
            ScoreBreadth(i),
            ScoreMomentum(i),
            ScoreLiquidity(i),
            ScoreCredit(i),
            ScoreMacro(i),
            ScoreCrossAsset(i),
        };

        // Composite = Σ score×weight, rounded to 0.1 (matches upstream).
        var composite = Math.Round(cats.Sum(c => c.Score * c.Weight) * 10) / 10;

        return new MarketRegimeSnapshot(
            CompositeScore: composite,
            State: RegimeStateExtensions.FromScore(composite),
            PreviousScore: previousScore,
            Categories: cats,
            Header: BuildHeader(i),
            GeneratedAtUtc: nowUtc,
            Unavailable: false);
    }

    // ---- Per-category scoring (ported 1:1) ----

    private static RegimeCategoryScore ScoreSentiment(RegimeInputs i)
    {
        var cnn = i.CnnFearGreed;
        var degraded = i.AaiiBull is null || i.AaiiBear is null;
        double score;
        if (!degraded)
        {
            var bullPct = Clamp(i.AaiiBull!.Value, 0, 100);
            var bearPct = Clamp(i.AaiiBear!.Value, 0, 100);
            var bullPercentile = Clamp(bullPct / 60 * 100, 0, 100);
            var bearPercentile = Clamp(bearPct / 55 * 100, 0, 100);
            score = cnn != null
                ? cnn.Value * 0.4 + bullPercentile * 0.3 + (100 - bearPercentile) * 0.3
                : bullPercentile * 0.5 + (100 - bearPercentile) * 0.5;
        }
        else if (cnn != null) score = cnn.Value;
        else { score = 50; }

        var detail = cnn != null
            ? $"CNN {cnn}" + (degraded ? "" : $" · AAII {i.AaiiBull:F0}/{i.AaiiBear:F0}")
            : (degraded ? "no sentiment data" : $"AAII {i.AaiiBull:F0}/{i.AaiiBear:F0}");
        return Build(RegimeCategory.Sentiment, score, degraded, detail);
    }

    private static RegimeCategoryScore ScoreVolatility(RegimeInputs i)
    {
        if (i.Vix is null) return Build(RegimeCategory.Volatility, 50, true, "no VIX");
        var vix = i.Vix.Value;
        // VIX range 12–35: low VIX → high (greedy) score.
        var vixScore = Clamp(100 - (vix - 12) / 23 * 100, 0, 100);
        var hasTerm = i.Vix3m != null;
        var contango = hasTerm && vix / i.Vix3m!.Value < 1;
        var termScore = hasTerm ? (contango ? 70 : 30) : 50;
        var term = hasTerm ? (contango ? "contango" : "backwardation") : "term n/a";
        return Build(RegimeCategory.Volatility, vixScore * 0.7 + termScore * 0.3, false,
            $"VIX {vix:F1} · {term}");
    }

    private static RegimeCategoryScore ScorePositioning(RegimeInputs i)
    {
        var pc = i.PutCallRatio;
        var skew = i.Skew;
        if (pc is null && skew is null)
            return Build(RegimeCategory.Positioning, 50, true, "no options data");
        var pcScore = pc != null ? Clamp(100 - (pc.Value - 0.7) / 0.6 * 100, 0, 100) : 50;
        var skewScore = skew != null ? Clamp(100 - (skew.Value - 100) / 50 * 100, 0, 100) : 50;
        // Upstream weights put/call 0.6 / skew 0.4 when both exist; when only one is present
        // it carries the full weight (we don't have a free put/call feed in v1, so SKEW alone
        // drives this — the upstream's [1,0] fallback would discard SKEW, which we avoid).
        var (w0, w1) = (pc, skew) switch
        {
            (not null, not null) => (0.6, 0.4),
            (not null, null) => (1.0, 0.0),
            _ => (0.0, 1.0),
        };
        var detail = pc != null ? $"P/C {pc:F2}" : $"SKEW {skew:F0}";
        return Build(RegimeCategory.Positioning, pcScore * w0 + skewScore * w1,
            pc is null && skew is null, detail);
    }

    private static RegimeCategoryScore ScoreTrend(RegimeInputs i)
    {
        var prices = i.SpxCloses;
        if (prices.Length == 0) return Build(RegimeCategory.Trend, 50, true, "no SPX");
        var price = prices[^1];
        var s20 = Sma(prices, 20);
        var s50 = Sma(prices, 50);
        var s200 = Sma(prices, 200);
        var aboveCount = new[] { s20, s50, s200 }.Count(s => s != null && price > s);
        var dist200 = s200 != null && s200 != 0 ? (price - s200.Value) / s200.Value : 0;
        var score = (double)aboveCount / 3 * 50 + Clamp(dist200 * 500 + 50, 0, 100) * 0.5;
        return Build(RegimeCategory.Trend, Clamp(score, 0, 100), false, $"{aboveCount}/3 above MAs");
    }

    private static RegimeCategoryScore ScoreBreadth(RegimeInputs i)
    {
        var mmth = i.PctAbove200dma;
        var breadthScore = mmth != null ? Clamp(mmth.Value, 0, 100) : 50;
        double? rspRoc = i.RspCloses.Length > 0 && i.SpyCloses.Length > 0
            ? (Roc(i.RspCloses, 30) ?? 0) - (Roc(i.SpyCloses, 30) ?? 0)
            : null;
        var rspScore = rspRoc != null ? Clamp(rspRoc.Value * 10 + 50, 0, 100) : 50;
        // No advance/decline source here → reweight breadth + RSP (matches upstream's no-A/D path).
        var score = breadthScore * 0.57 + rspScore * 0.43;
        var detail = mmth != null ? $"{mmth:F0}% > 200d" : "RSP vs SPY";
        return Build(RegimeCategory.Breadth, Clamp(score, 0, 100), mmth is null, detail);
    }

    private static RegimeCategoryScore ScoreMomentum(RegimeInputs i)
    {
        double? spxRoc = i.SpxCloses.Length > 0 ? Roc(i.SpxCloses, 20) : null;
        var rocScore = spxRoc != null ? Clamp(spxRoc.Value * 10 + 50, 0, 100) : 50;
        var rsis = i.SectorCloses.Values
            .Where(c => c is { Length: > 0 })
            .Select(c => Rsi(c))
            .ToList();
        var avgRsi = rsis.Count > 0 ? rsis.Average() : 50;
        var rsiScore = Clamp((avgRsi - 30) / 40 * 100, 0, 100);
        return Build(RegimeCategory.Momentum, rsiScore * 0.5 + rocScore * 0.5, false,
            $"sector RSI {avgRsi:F0}");
    }

    private static RegimeCategoryScore ScoreLiquidity(RegimeInputs i)
    {
        // M2SL weekly: 52 obs back ≈ true YoY. WALCL weekly: 4 obs back ≈ 1 month.
        var m2Yoy = PctChange(i.M2, 52);
        var fedBsMom = PctChange(i.FedBalanceSheet, 4);
        var degraded = m2Yoy is null && fedBsMom is null && i.Sofr is null;
        var m2Score = m2Yoy != null ? Clamp(m2Yoy.Value * 5 + 50, 0, 100) : 50;
        var fedScore = fedBsMom != null ? Clamp(fedBsMom.Value * 20 + 50, 0, 100) : 50;
        var sofrScore = i.Sofr != null ? Clamp(100 - i.Sofr.Value * 15, 0, 100) : 50;
        var detail = m2Yoy != null ? $"M2 YoY {m2Yoy:F1}%" : "macro liquidity n/a";
        return Build(RegimeCategory.Liquidity, m2Score * 0.4 + fedScore * 0.3 + sofrScore * 0.3,
            degraded, detail);
    }

    private static RegimeCategoryScore ScoreCredit(RegimeInputs i)
    {
        var hy = Latest(i.HighYieldOas);
        var ig = Latest(i.InvGradeOas);
        var degraded = hy is null && ig is null;
        // HY OAS 2.0–10.0%, IG OAS 0.4–3.0% (tighter = greedier).
        var hyScore = hy != null ? Clamp(100 - (hy.Value - 2.0) / 8.0 * 100, 0, 100) : 50;
        var igScore = ig != null ? Clamp(100 - (ig.Value - 0.4) / 2.6 * 100, 0, 100) : 50;
        var hyPrev = NTradingDaysAgo(i.HighYieldOas, 20);
        var trend = hy != null && hyPrev != null
            ? (hy < hyPrev ? "narrowing" : hy > hyPrev ? "widening" : "stable")
            : "stable";
        var trendScore = trend == "narrowing" ? 70 : trend == "widening" ? 30 : 50;
        var detail = hy != null ? $"HY {hy:F2}% · {trend}" : "credit n/a";
        return Build(RegimeCategory.Credit, hyScore * 0.4 + igScore * 0.3 + trendScore * 0.3,
            degraded, detail);
    }

    private static RegimeCategoryScore ScoreMacro(RegimeInputs i)
    {
        var fed = Latest(i.FedFunds);
        var curve = Latest(i.Curve10y2y);
        var unrate = Latest(i.Unemployment);
        var degraded = fed is null && curve is null && unrate is null;
        var rateScore = fed != null ? Clamp(100 - fed.Value * 15, 0, 100) : 50;
        var curveScore = curve != null
            ? (curve > 0 ? Clamp(60 + curve.Value * 20, 0, 100) : Clamp(40 + curve.Value * 40, 0, 100))
            : 50;
        var unempScore = unrate != null ? Clamp(100 - (unrate.Value - 3.5) * 20, 0, 100) : 50;
        var detail = curve != null ? $"10y-2y {curve:F2}" : "macro n/a";
        return Build(RegimeCategory.Macro, rateScore * 0.3 + curveScore * 0.4 + unempScore * 0.3,
            degraded, detail);
    }

    private static RegimeCategoryScore ScoreCrossAsset(RegimeInputs i)
    {
        var goldRoc = i.GldCloses.Length > 0 ? Roc(i.GldCloses, 30) : null;
        var tltRoc = i.TltCloses.Length > 0 ? Roc(i.TltCloses, 30) : null;
        var spyRoc = i.SpyCloses.Length > 0 ? Roc(i.SpyCloses, 30) : null;
        var dxyRoc = i.DxyCloses.Length > 0 ? Roc(i.DxyCloses, 30) : null;
        var degraded = goldRoc is null && tltRoc is null && spyRoc is null && dxyRoc is null;
        // Defensive assets outperforming equities = risk-off (low score).
        var goldSignal = goldRoc != null && spyRoc != null ? (goldRoc > spyRoc ? 30 : 70) : 50;
        var bondSignal = tltRoc != null && spyRoc != null ? (tltRoc > spyRoc ? 30 : 70) : 50;
        var dxySignal = dxyRoc != null ? (dxyRoc > 0 ? 40 : 60) : 50;
        return Build(RegimeCategory.CrossAsset, (goldSignal + bondSignal + dxySignal) / 3.0,
            degraded, "gold/bonds/USD vs SPY");
    }

    private static RegimeHeaderMetrics BuildHeader(RegimeInputs i)
    {
        var vix = i.Vix;
        var hy = Latest(i.HighYieldOas);
        double? fsi = null;
        string? fsiLabel = null;
        if (i.HygPrice is > 0 && i.TltPrice is > 0 && vix is > 0 && hy is > 0)
        {
            fsi = Math.Round(i.HygPrice.Value / i.TltPrice.Value / (vix.Value * hy.Value / 100) * 10000) / 10000;
            fsiLabel = fsi switch
            {
                >= 1.5 => "Low Stress",
                >= 0.8 => "Moderate Stress",
                >= 0.3 => "Elevated Stress",
                _ => "High Stress",
            };
        }

        return new RegimeHeaderMetrics(
            Vix: vix,
            PutCallRatio: i.PutCallRatio,
            HighYieldSpread: hy,
            PctAbove200dma: i.PctAbove200dma,
            Yield10y: Latest(i.Yield10y),
            FedFundsRate: Latest(i.FedFunds),
            CnnFearGreed: i.CnnFearGreed,
            FinancialStressIndex: fsi,
            FinancialStressLabel: fsiLabel);
    }

    // ---- Helpers ----

    private static RegimeCategoryScore Build(RegimeCategory cat, double rawScore, bool degraded, string detail)
    {
        var score = (int)Math.Round(Clamp(rawScore, 0, 100));
        var weight = Weight(cat);
        return new RegimeCategoryScore(cat, score, weight,
            Math.Round(score * weight * 10) / 10, degraded, detail);
    }

    private static double Clamp(double v, double lo, double hi) => Math.Max(lo, Math.Min(hi, v));

    private static double? Sma(double[] prices, int period)
    {
        if (prices.Length < period) return null;
        double sum = 0;
        for (var k = prices.Length - period; k < prices.Length; k++) sum += prices[k];
        return sum / period;
    }

    private static double? Roc(double[] prices, int period)
    {
        if (prices.Length < period + 1) return null;
        var prev = prices[^(period + 1)];
        var curr = prices[^1];
        return prev != 0 ? (curr - prev) / prev * 100 : null;
    }

    private static double Rsi(double[] prices, int period = 14)
    {
        if (prices.Length < period + 1) return 50;
        double gains = 0, losses = 0;
        for (var k = prices.Length - period; k < prices.Length; k++)
        {
            var d = prices[k] - prices[k - 1];
            if (d > 0) gains += d; else losses += Math.Abs(d);
        }
        if (losses == 0) return 100;
        var rs = gains / period / (losses / period);
        return 100 - 100 / (1 + rs);
    }

    private static double? Latest(double[] obs) => obs.Length > 0 ? obs[^1] : null;

    private static double? NTradingDaysAgo(double[] obs, int days)
    {
        var idx = obs.Length - 1 - days;
        return idx >= 0 ? obs[idx] : null;
    }

    private static double? PctChange(double[] obs, int back)
    {
        var latest = Latest(obs);
        var idx = obs.Length - 1 - back;
        if (latest is null || idx < 0) return null;
        var ago = obs[idx];
        return ago != 0 ? (latest.Value - ago) / ago * 100 : null;
    }

    /// <summary>Standard 0–100 band label, exposed for callers that only have a score.</summary>
    public static string LabelFromScore(double score) =>
        RegimeStateExtensions.FromScore(score).Label();

    internal static string Fmt(double? v, int digits = 2) =>
        v?.ToString("F" + digits, CultureInfo.InvariantCulture) ?? "N/A";
}
