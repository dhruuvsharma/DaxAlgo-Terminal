using DaxAlgo.Sdk.Quant;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Core.Domain;
using Xunit;

namespace TradingTerminal.Sandbox.Tests;

/// <summary>
/// The maths vocabulary an authored strategy composes from.
///
/// <para>These are the checks the verification ladder <b>cannot</b> make. A strategy that smooths its
/// RSI with an EMA compiles, instantiates, draws and trades — every rung passes, and the oscillator is
/// simply not an RSI. So the correctness of each estimator has to be pinned here, once, rather than
/// re-rolled by a model on every generation.</para>
/// </summary>
public sealed class QuantEstimatorTests
{
    private const double Tolerance = 1e-9d;

    // ── Guards ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DividingByAClosedSpreadReturnsTheFallback_RatherThanAnInfinity()
    {
        // The failure this prevents is silent: an infinity compares false against every threshold, so
        // the strategy stops trading and never says why.
        Assert.Equal(0d, Num.SafeDiv(1d, 0d));
        Assert.Equal(-1d, Num.SafeDiv(1d, 0d, -1d));
        Assert.Equal(0d, Num.SafeDiv(double.NaN, 2d));
        Assert.Equal(0.5d, Num.SafeDiv(1d, 2d));
    }

    [Fact]
    public void ClampSwapsInvertedBounds_RatherThanThrowingLikeMathClamp()
    {
        Assert.Equal(5d, Num.Clamp(5d, 10d, 0d));
        Assert.Equal(0d, Num.Clamp(-1d, 0d, 10d));
        Assert.Equal(0d, Num.Clamp(double.NaN, 0d, 10d));
    }

    [Fact]
    public void RoundingToAnUnknownTickLeavesThePriceAlone_RatherThanLosingTheLevel()
    {
        Assert.Equal(100.25d, Num.RoundToTick(100.26d, 0.25d), 9);
        Assert.Equal(100.26d, Num.RoundToTick(100.26d, 0d), 9);
    }

    // ── Smoothers ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnEmaSeedsOnItsFirstSample_NotOnZero()
    {
        // Seeding at zero biases the whole series toward zero for several multiples of the period,
        // which on a price series means an average wrong by the price itself.
        var ema = new Ema(10);

        Assert.Equal(4000d, ema.Update(4000d));
    }

    [Fact]
    public void AnEmaIsNotReadyUntilItHasSeenItsPeriod()
    {
        var ema = new Ema(5);
        for (var i = 0; i < 4; i++) ema.Update(i);

        Assert.False(ema.IsReady);
        ema.Update(5d);
        Assert.True(ema.IsReady);
    }

    [Fact]
    public void WilderSmoothingIsSlowerThanAnEmaOfTheSamePeriod()
    {
        // The substitution nothing downstream can detect. Wilder's alpha is 1/n against the EMA's
        // 2/(n+1), so an RSI or ATR smoothed the wrong way is roughly twice as fast and crosses its
        // thresholds at different times.
        var ema = new Ema(14);
        var wilder = new Wilder(14);

        ema.Update(100d);
        wilder.Update(100d);
        for (var i = 0; i < 20; i++)
        {
            ema.Update(200d);
            wilder.Update(200d);
        }

        Assert.True(ema.Value > wilder.Value);
    }

    [Fact]
    public void ASmootherIgnoresANonFiniteSample_RatherThanBeingPoisonedForever()
    {
        // One NaN through the recursion makes every subsequent value NaN, for the life of the object.
        var ema = new Ema(5);
        ema.Update(10d);
        ema.Update(double.NaN);

        Assert.Equal(10d, ema.Value);
    }

    [Fact]
    public void AnSmaIsNotReadyUntilItsWindowIsFull()
    {
        var sma = new Sma(4);
        sma.Update(1d);
        sma.Update(3d);

        Assert.False(sma.IsReady);
        Assert.Equal(2d, sma.Value, 9);

        sma.Update(5d);
        sma.Update(7d);
        Assert.True(sma.IsReady);
        Assert.Equal(4d, sma.Value, 9);
    }

    // ── Rolling window ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheWindowIsIndexedNewestFirst_SoTheLastCloseIsAlwaysZero()
    {
        var window = new RollingWindow(3);
        window.Update(1d);
        window.Update(2d);
        window.Update(3d);

        Assert.Equal(3d, window[0]);
        Assert.Equal(2d, window[1]);
        Assert.Equal(1d, window[2]);
        Assert.Equal(3d, window.Newest);
        Assert.Equal(1d, window.Oldest);
    }

    [Fact]
    public void TheWindowEvictsTheOldestOnceFull()
    {
        var window = new RollingWindow(3);
        for (var i = 1; i <= 5; i++) window.Update(i);

        Assert.Equal(3, window.Count);
        Assert.Equal(5d, window[0]);
        Assert.Equal(3d, window[2]);
        Assert.Equal(12d, window.Sum);
    }

    [Fact]
    public void TheWindowVarianceSurvivesLargeOffsets()
    {
        // The one-pass sum-of-squares form returns a NEGATIVE variance here, and a NaN standard
        // deviation from it. This is what the two-pass computation exists for.
        var window = new RollingWindow(5);
        foreach (var value in new[] { 1e9d, 1e9d + 1d, 1e9d + 2d, 1e9d + 3d, 1e9d + 4d })
            window.Update(value);

        Assert.Equal(2.5d, window.Variance, 6);
        Assert.True(window.StandardDeviation > 0d);
    }

    [Fact]
    public void TheWindowRejectsANonFiniteSample_BecauseOneWouldPoisonEveryStatistic()
    {
        var window = new RollingWindow(4);
        window.Update(1d);
        window.Update(double.NaN);
        window.Update(3d);

        Assert.Equal(2, window.Count);
        Assert.Equal(2d, window.Mean, 9);
    }

    [Fact]
    public void TheWindowInterpolatesItsQuantiles()
    {
        var window = new RollingWindow(5);
        foreach (var value in new[] { 1d, 2d, 3d, 4d, 5d }) window.Update(value);

        Assert.Equal(1d, window.Quantile(0d), 9);
        Assert.Equal(3d, window.Median(), 9);
        Assert.Equal(5d, window.Quantile(1d), 9);
        Assert.Equal(2.5d, window.Quantile(0.375d), 9);
    }

    [Fact]
    public void PositionWithinTheRangeIsTheStochasticNormalisation()
    {
        var window = new RollingWindow(3);
        window.Update(10d);
        window.Update(20d);
        window.Update(30d);

        Assert.Equal(0d, window.PositionOf(10d), 9);
        Assert.Equal(0.5d, window.PositionOf(20d), 9);
        Assert.Equal(1d, window.PositionOf(30d), 9);
        Assert.Equal(1d, window.PositionOf(99d), 9);
    }

    // ── Dispersion ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WelfordMatchesATwoPassVarianceOnOffsetData()
    {
        var welford = new Welford();
        var samples = new[] { 1e9d, 1e9d + 1d, 1e9d + 2d, 1e9d + 3d, 1e9d + 4d };
        foreach (var sample in samples) welford.Update(sample);

        var mean = samples.Average();
        var expected = samples.Sum(s => (s - mean) * (s - mean)) / (samples.Length - 1);

        Assert.Equal(expected, welford.Variance, 6);
        Assert.Equal(5L, welford.Count);
    }

    [Fact]
    public void AZScoreIsNotReadyBeforeItsMinimumSampleCount()
    {
        // A z-score over eight samples reads near zero exactly when the series is most unusual,
        // because with that little data the extremes ARE the sample.
        var score = new ZScore(200);

        for (var i = 0; i < 29; i++) score.Update(i);
        Assert.False(score.IsReady);

        score.Update(29d);
        Assert.True(score.IsReady);
        Assert.Equal(30, score.MinimumSamples);
    }

    [Fact]
    public void AZScoreOfAConstantSeriesIsZero_NotAnInfinity()
    {
        var score = new ZScore(10, minimumSamples: 2);
        for (var i = 0; i < 10; i++) score.Update(5d);

        Assert.Equal(0d, score.Value);
    }

    [Fact]
    public void EwmaVarianceForgetsTheOldRegime()
    {
        // The whole reason it exists: a session-wide variance would still be carrying the calm.
        var ewma = new EwmaVariance(0.9d);
        for (var i = 0; i < 200; i++) ewma.Update(100d);
        var calm = ewma.StandardDeviation;

        for (var i = 0; i < 40; i++) ewma.Update(100d + (i % 2 == 0 ? 5d : -5d));

        Assert.True(ewma.StandardDeviation > calm);
    }

    // ── Indicators ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnUnbrokenRunOfGainsPinsTheRsiAtOneHundred()
    {
        var rsi = new Rsi(14);
        for (var i = 0; i < 40; i++) rsi.Update(100d + i);

        Assert.Equal(100d, rsi.Value, 6);
    }

    [Fact]
    public void AnUnbrokenRunOfLossesPinsTheRsiAtZero()
    {
        var rsi = new Rsi(14);
        for (var i = 0; i < 40; i++) rsi.Update(100d - i);

        Assert.Equal(0d, rsi.Value, 6);
    }

    [Fact]
    public void TheRsiStartsNeutralRatherThanAtAnExtreme()
    {
        var rsi = new Rsi(14);

        Assert.Equal(50d, rsi.Value);
        Assert.False(rsi.IsReady);
    }

    [Fact]
    public void TheRsiAveragesLossesOverEveryBar_NotOnlyOverTheDownBars()
    {
        // The common shortcut averages each side only over the bars where that side moved, which
        // holds the index too high through a downtrend. Nine up-ticks and one large down-tick must
        // not still read as strongly overbought.
        var rsi = new Rsi(10);
        rsi.Update(100d);
        for (var i = 0; i < 9; i++) rsi.Update(101d + i);
        rsi.Update(100d);

        Assert.InRange(rsi.Value, 0d, 90d);
    }

    [Fact]
    public void TrueRangeCoversAnOvernightGap_WhichHighMinusLowMisses()
    {
        // Previous close 100, then a bar that gaps down and trades 90-92. The bar's own span is 2;
        // the true range is 10.
        Assert.Equal(10d, Atr.TrueRange(high: 92d, low: 90d, previousClose: 100d), 9);
    }

    [Fact]
    public void TheAtrSeedsFromTheFirstBarsSpan_BecauseThereIsNoPreviousClose()
    {
        var atr = new Atr(14);
        atr.Update(Bar(open: 100d, high: 105d, low: 95d, close: 100d));

        Assert.Equal(10d, atr.LastTrueRange, 9);
        Assert.Equal(10d, atr.Value, 9);
    }

    [Fact]
    public void TheMacdHistogramIsTheLineMinusItsSignal()
    {
        var macd = new Macd();
        for (var i = 0; i < 100; i++) macd.Update(100d + (i * 0.5d));

        Assert.Equal(macd.Line - macd.Signal, macd.Histogram, 9);
        Assert.True(macd.IsReady);
    }

    [Fact]
    public void PercentBIsOneAtTheUpperBandAndZeroAtTheLower()
    {
        var bands = new BollingerBands(20, 2d);
        var random = new Random(1);
        for (var i = 0; i < 100; i++) bands.Update(100d + random.NextDouble());

        Assert.True(bands.IsReady);
        Assert.Equal(1d, Num.SafeDiv(bands.Upper - bands.Lower, bands.Upper - bands.Lower), 9);
        Assert.InRange(bands.PercentB, -1d, 2d);
        Assert.True(bands.Upper > bands.Middle && bands.Middle > bands.Lower);
    }

    [Fact]
    public void TheVwapIsWeightedByVolume_NotByPrintCount()
    {
        var vwap = new Vwap();
        vwap.Update(price: 100d, volume: 1d);
        vwap.Update(price: 200d, volume: 9d);

        Assert.Equal(190d, vwap.Value, 9);
    }

    [Fact]
    public void ResettingTheVwapStartsANewSession()
    {
        var vwap = new Vwap();
        vwap.Update(100d, 1000d);
        vwap.Reset();
        vwap.Update(200d, 1d);

        Assert.Equal(200d, vwap.Value, 9);
        Assert.Equal(1d, vwap.Volume, 9);
    }

    [Fact]
    public void RealisedVolatilityIsMeasuredAboutZero_NotAboutTheSampleDrift()
    {
        // A steadily rising series has real per-sample volatility. Measured about its own mean it
        // would report almost none, which understates risk exactly where a trend is running.
        var volatility = new RealizedVolatility(20);
        for (var i = 0; i < 60; i++) volatility.Update(100d * Math.Pow(1.01d, i));

        Assert.True(volatility.IsReady);
        Assert.Equal(Math.Log(1.01d), volatility.Value, 6);
    }

    [Fact]
    public void AnnualisingScalesByTheSquareRootOfTheSampleCount()
    {
        var volatility = new RealizedVolatility(10);
        for (var i = 0; i < 30; i++) volatility.Update(100d * Math.Pow(1.01d, i));

        Assert.Equal(volatility.Value * Math.Sqrt(252d), volatility.Annualized(252d), 9);
    }

    // ── Regression and mean reversion ───────────────────────────────────────────────────────────

    [Fact]
    public void TheRegressionRecoversAKnownLine()
    {
        var fit = new OnlineRegression(50);
        for (var i = 0; i < 50; i++) fit.Update(i, 3d + (2d * i));

        Assert.True(fit.IsReady);
        Assert.Equal(2d, fit.Slope, 6);
        Assert.Equal(3d, fit.Intercept, 6);
        Assert.Equal(1d, fit.RSquared, 6);
        Assert.Equal(0d, fit.StandardError, 6);
    }

    [Fact]
    public void TheRegressionReportsNoFitWhenThereIsNoRelationship()
    {
        // A slope through a cloud is still a slope, and it is confidently wrong. RSquared is what
        // separates a hedge ratio from a random number with two decimal places.
        var fit = new OnlineRegression(200);
        var random = new Random(7);
        for (var i = 0; i < 200; i++) fit.Update(random.NextDouble(), random.NextDouble());

        Assert.True(fit.RSquared < 0.1d);
    }

    [Fact]
    public void AnExactFitHasNoResidualStandardError_AndTheZScoreDegradesToZero()
    {
        var fit = new OnlineRegression(30);
        for (var i = 0; i < 30; i++) fit.Update(i, 5d * i);

        Assert.Equal(0d, fit.ResidualZScore(10d, 50d), 9);
    }

    [Fact]
    public void RandomWalksAreRejectedAtRoughlyTheNominalRate()
    {
        // The failure this prevents is a strategy that adds to a loser forever, on the belief that a
        // walk must come back.
        //
        // Twenty seeds rather than one, because a single walk proves nothing either way: the test is
        // sized at 5%, so the honest property is the RATE. An earlier gate here was "phi < 1 and the
        // regression explains something", which flagged all twenty — least squares is biased downward
        // under a unit root, so phi < 1 is the EXPECTED reading on a walk, and R-squared is near one
        // on a walk precisely because the best predictor of the next level is the current one.
        var flagged = 0;
        for (var seed = 0; seed < 20; seed++)
        {
            var process = new OrnsteinUhlenbeck(200);
            var random = new Random(seed);
            var level = 100d;
            for (var i = 0; i < 400; i++)
            {
                level += random.NextDouble() - 0.5d;
                process.Update(level);
            }

            if (process.IsMeanReverting) flagged++;
        }

        Assert.InRange(flagged, 0, 3);
    }

    [Fact]
    public void AMeanRevertingSeriesIsRecognisedAndItsHalfLifeRecovered()
    {
        // phi = 0.5 has a half-life of exactly one sample: ln(2) / -ln(0.5) == 1.
        var process = Reverting(0.5d, seed: 13);

        Assert.True(process.IsMeanReverting);
        Assert.Equal(1d, process.HalfLife, 0);
        Assert.InRange(process.Mean, -0.5d, 0.5d);
        Assert.True(process.UnitRootStatistic < OrnsteinUhlenbeck.CriticalValue);
    }

    [Fact]
    public void TheAutoregressiveCoefficientIsUnbiasedAcrossSeeds()
    {
        // Across seeds, not on one: the estimate has a standard error near 0.04 on a 400-sample
        // window, so pinning a single run to two digits would be pinning that run's noise. Averaging
        // twenty is what actually tests the estimator rather than the sample.
        var total = 0d;
        for (var seed = 0; seed < 20; seed++) total += Reverting(0.5d, seed).Phi;

        Assert.Equal(0.5d, total / 20d, 1);
    }

    private static OrnsteinUhlenbeck Reverting(double phi, int seed)
    {
        var process = new OrnsteinUhlenbeck(400);
        var random = new Random(seed);
        var level = 0d;
        for (var i = 0; i < 800; i++)
        {
            level = (phi * level) + (random.NextDouble() - 0.5d);
            process.Update(level);
        }

        return process;
    }

    [Fact]
    public void ADeviationIsMeasuredInStationarySigma_NotInOneStepsWorth()
    {
        // A slow-reverting series takes many steps to cross its own distribution, so dividing by the
        // residual standard deviation reports an ordinary level as a twenty-sigma event.
        var process = new OrnsteinUhlenbeck(400);
        var random = new Random(29);
        var level = 0d;
        for (var i = 0; i < 1200; i++)
        {
            level = (0.97d * level) + (random.NextDouble() - 0.5d);
            process.Update(level);
        }

        Assert.True(process.IsMeanReverting);
        Assert.True(process.StationaryDeviation > process.HalfLife * 0d);
        Assert.InRange(Math.Abs(process.Deviation), 0d, 4d);
    }

    // ── Kalman ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheKalmanLevelConvergesOnAConstantThroughNoise()
    {
        var filter = new KalmanLevel(processNoise: 1e-6d, measurementNoise: 1d);
        var random = new Random(17);
        for (var i = 0; i < 500; i++) filter.Update(50d + (random.NextDouble() - 0.5d));

        Assert.True(filter.IsReady);
        Assert.Equal(50d, filter.Value, 1);
        Assert.InRange(filter.Gain, 0d, 1d);
    }

    [Fact]
    public void TheKalmanLevelSeedsOnItsFirstMeasurement()
    {
        var filter = new KalmanLevel();

        Assert.Equal(4000d, filter.Update(4000d));
    }

    [Fact]
    public void TheHedgeRatioFilterRecoversAKnownRatio()
    {
        var filter = new KalmanHedgeRatio(processNoise: 1e-7d, measurementNoise: 1e-4d);
        var random = new Random(19);
        for (var i = 0; i < 2000; i++)
        {
            var x = 100d + (random.NextDouble() * 10d);
            filter.Update(x, (2.5d * x) + 4d + ((random.NextDouble() - 0.5d) * 0.01d));
        }

        Assert.True(filter.IsReady);
        Assert.Equal(2.5d, filter.HedgeRatio, 1);
        Assert.True(Math.Abs(filter.Spread) < 0.1d);
    }

    [Fact]
    public void TheHedgeRatioFilterSeedsSoTheFirstSpreadIsNotTheSizeOfThePrice()
    {
        // A zero seed makes the first spreads the size of the price rather than of a residual, and a
        // strategy watching for an extreme spread sees one immediately.
        var filter = new KalmanHedgeRatio();
        filter.Update(4000d, 4010d);

        Assert.Equal(0d, filter.Spread, 9);
    }

    // ── Order flow ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheQuoteRuleClassifiesAtAndThroughTheTouch()
    {
        Assert.Equal(TradeSide.Buy, TradeClassifier.QuoteRule(101d, 99d, 101d));
        Assert.Equal(TradeSide.Sell, TradeClassifier.QuoteRule(99d, 99d, 101d));
        Assert.Equal(TradeSide.Buy, TradeClassifier.QuoteRule(100.5d, 99d, 101d));
        Assert.Equal(TradeSide.Sell, TradeClassifier.QuoteRule(99.5d, 99d, 101d));
        Assert.Equal(TradeSide.Unknown, TradeClassifier.QuoteRule(100d, 99d, 101d));
    }

    [Fact]
    public void TheVenuesOwnAggressorWinsOverAnyRule()
    {
        var trade = new TradePrint(
            new InstrumentId(1), DateTime.UnixEpoch, DateTime.UnixEpoch,
            Price: 99d, Size: 1L, AggressorSide.Buy, BrokerKind.Binance, 1L, false);

        // The quote rule would call this a sell; the venue said otherwise and the venue knows.
        Assert.Equal(TradeSide.Buy, TradeClassifier.Classify(trade, Quote(99d, 101d)));
    }

    [Fact]
    public void ImbalanceIsNormalisedSoAThresholdTransfersBetweenInstruments()
    {
        var flow = new OrderFlowImbalance(4);
        flow.Update(10d, TradeSide.Buy);
        flow.Update(10d, TradeSide.Buy);
        flow.Update(10d, TradeSide.Sell);
        flow.Update(10d, TradeSide.Sell);

        Assert.True(flow.IsReady);
        Assert.Equal(0d, flow.Value, 9);
        Assert.Equal(0d, flow.Cumulative, 9);

        flow.Reset();
        flow.Update(1_000_000d, TradeSide.Buy);
        Assert.Equal(1d, flow.Value, 9);
    }

    [Fact]
    public void OneSidedFlowDrivesVpinToOne()
    {
        var vpin = new Vpin(bucketVolume: 100d, buckets: 5);
        for (var i = 0; i < 20; i++) vpin.Update(50d, TradeSide.Buy);

        Assert.True(vpin.IsReady);
        Assert.Equal(1d, vpin.Value, 9);
    }

    [Fact]
    public void BalancedFlowDrivesVpinToZero()
    {
        var vpin = new Vpin(bucketVolume: 100d, buckets: 5);
        for (var i = 0; i < 20; i++)
        {
            vpin.Update(50d, TradeSide.Buy);
            vpin.Update(50d, TradeSide.Sell);
        }

        Assert.Equal(0d, vpin.Value, 9);
    }

    [Fact]
    public void OneBlockPrintClosesEveryBucketItFills()
    {
        // A while loop, not an if: on a thin instrument a single print is several buckets' worth, and
        // an if would silently discard the rest.
        var vpin = new Vpin(bucketVolume: 10d, buckets: 100);
        vpin.Update(95d, TradeSide.Buy);

        Assert.Equal(9, vpin.BucketCount);
        Assert.Equal(0.5d, vpin.BucketProgress, 9);
    }

    [Fact]
    public void KyleLambdaRecoversAKnownPriceImpact()
    {
        var lambda = new KyleLambda(60);
        var price = 100d;
        lambda.Close(price);

        var random = new Random(23);
        for (var i = 0; i < 200; i++)
        {
            var units = (random.NextDouble() - 0.5d) * 100d;
            lambda.Record(Math.Abs(units), units >= 0d ? TradeSide.Buy : TradeSide.Sell);
            price += 0.02d * units;
            lambda.Close(price);
        }

        Assert.True(lambda.IsReady);
        Assert.Equal(0.02d, lambda.Value, 6);
        Assert.Equal(0.2d, lambda.ImpactOf(10d), 6);
    }

    // ── Book ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void TheMicropriceLeansTowardTheThinSide()
    {
        // A thousand bid against ten offered means the offer is about to be taken, so fair value sits
        // near the ask — which the plain mid never says.
        var micro = Book.Microprice(bidPrice: 99d, bidSize: 1000d, askPrice: 101d, askSize: 10d);

        Assert.True(micro > 100d);
        Assert.InRange(micro, 100d, 101d);
    }

    [Fact]
    public void TheMicropriceDegradesToTheMidWhenTheBookIsEmpty()
    {
        Assert.Equal(100d, Book.Microprice(99d, 0d, 101d, 0d), 9);
    }

    [Fact]
    public void QueueImbalanceIsSignedTowardTheLargerSide()
    {
        Assert.Equal(1d, Book.Imbalance(100d, 0d), 9);
        Assert.Equal(-1d, Book.Imbalance(0d, 100d), 9);
        Assert.Equal(0d, Book.Imbalance(50d, 50d), 9);
        Assert.Equal(0d, Book.Imbalance(0d, 0d), 9);
    }

    [Fact]
    public void SweepingWalksTheBookAndWeightsByTheSizeTakenAtEachLevel()
    {
        var asks = new[] { new DepthLevel(101d, 5L), new DepthLevel(102d, 5L) };

        Assert.Equal(101d, Book.SweepPrice(asks, 5d), 9);
        Assert.Equal(101.5d, Book.SweepPrice(asks, 10d), 9);
    }

    [Fact]
    public void ABookThatCannotFillTheOrderReportsZero_NotACheapFill()
    {
        // "Impossible" and "costly" have to be distinguishable, or a strategy sizing off this number
        // reads a book it cannot trade as the best price available.
        var asks = new[] { new DepthLevel(101d, 5L) };

        Assert.Equal(0d, Book.SweepPrice(asks, 50d));
        Assert.Equal(0d, Book.SweepSlippage(asks, 50d, 100d));
    }

    [Fact]
    public void DepthImbalanceReadsFurtherThanTheTouch()
    {
        var depth = new DepthSnapshot(
            DateTime.UnixEpoch,
            [new DepthLevel(99d, 1L), new DepthLevel(98d, 100L)],
            [new DepthLevel(101d, 100L), new DepthLevel(102d, 1L)]);

        Assert.True(Book.Imbalance(depth, 1) < 0d);
        Assert.Equal(0d, Book.Imbalance(depth, 2), 9);
    }

    [Fact]
    public void AWideSpreadIsMeasuredAgainstTheInstrumentsOwnDistribution()
    {
        var spreads = new SpreadStats(50);
        for (var i = 0; i < 50; i++) spreads.Update(0.01d);

        // Not wide by anybody's tick count, and unmistakably wide for this instrument.
        spreads.Update(0.02d);
        Assert.True(spreads.IsWide(2d));
    }

    [Fact]
    public void ASpreadIsNeverCalledWideBeforeTheWindowHasFilled()
    {
        // Otherwise a strategy refuses to trade its first two hundred quotes of every session.
        var spreads = new SpreadStats(200);
        spreads.Update(0.01d);
        spreads.Update(5d);

        Assert.False(spreads.IsWide());
    }

    // ── Performance ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DrawdownIsMeasuredFromTheRunningPeak_NotFromTheStart()
    {
        var stats = new EquityStats();
        stats.Update(100d);
        stats.Update(130d);
        stats.Update(117d);

        Assert.Equal(130d, stats.Peak, 9);
        Assert.Equal(0.1d, stats.Drawdown, 9);
        Assert.Equal(0.1d, stats.MaximumDrawdown, 9);
    }

    [Fact]
    public void TheWorstDrawdownIsRememberedAfterTheCurveRecovers()
    {
        var stats = new EquityStats();
        stats.Update(100d);
        stats.Update(50d);
        stats.Update(200d);

        Assert.Equal(0d, stats.Drawdown, 9);
        Assert.Equal(0.5d, stats.MaximumDrawdown, 9);
    }

    [Fact]
    public void SortinoIgnoresUpsideDeviation_WhichIsWhatSeparatesItFromSharpe()
    {
        // A curve that only ever rises has no downside deviation at all, so Sortino must not simply
        // reproduce Sharpe on it.
        var stats = new EquityStats();
        for (var i = 0; i < 50; i++) stats.Update(100d + (i * i));

        Assert.True(stats.Sharpe > 0d);
        Assert.Equal(0d, stats.Sortino);
    }

    [Fact]
    public void AFlatCurveHasNoSharpeRatherThanAnInfiniteOne()
    {
        var stats = new EquityStats();
        for (var i = 0; i < 20; i++) stats.Update(100d);

        Assert.Equal(0d, stats.Sharpe);
        Assert.Equal(0d, stats.MaximumDrawdown);
    }

    [Fact]
    public void ProfitFactorAndExpectancyReadTheSameEdgeFromDifferentEnds()
    {
        var trades = new TradeStats();
        trades.Record(30d);
        trades.Record(-10d);
        trades.Record(-10d);
        trades.Record(30d);

        Assert.Equal(0.5d, trades.HitRate, 9);
        Assert.Equal(3d, trades.ProfitFactor, 9);
        Assert.Equal(10d, trades.Expectancy, 9);
        Assert.Equal(3d, trades.PayoffRatio, 9);
    }

    [Fact]
    public void AScratchBreaksNeitherTheWinCountNorTheLosingStreak()
    {
        var trades = new TradeStats();
        trades.Record(-1d);
        trades.Record(0d);
        trades.Record(-1d);

        Assert.Equal(3, trades.Count);
        Assert.Equal(0, trades.Wins);
        Assert.Equal(2, trades.Losses);
        Assert.Equal(2, trades.LosingStreak);
        Assert.Equal(2, trades.WorstLosingStreak);
    }

    [Fact]
    public void TheWorstLosingStreakSurvivesAWinningRun()
    {
        var trades = new TradeStats();
        for (var i = 0; i < 4; i++) trades.Record(-1d);
        trades.Record(10d);

        Assert.Equal(0, trades.LosingStreak);
        Assert.Equal(4, trades.WorstLosingStreak);
    }

    // ── Contract ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryEstimatorReturnsToItsUnseededStateOnReset()
    {
        // Reset is what a strategy calls at a session boundary or an instrument change. An estimator
        // that half-resets carries yesterday into today, invisibly.
        var estimators = new IEstimator[]
        {
            new Ema(5), new Wilder(5), new Sma(5), new Dema(5),
            new Welford(), new EwmaVariance(), new ZScore(5, 2),
            new Rsi(5), new Atr(5), new Macd(), new BollingerBands(5),
            new Vwap(), new RealizedVolatility(5),
            new RollingCorrelation(5), new OrderFlowImbalance(5),
            new Vpin(10d, 5), new KyleLambda(5), new SpreadStats(5), new KalmanLevel(),
        };

        foreach (var estimator in estimators)
        {
            Drive(estimator);
            estimator.Reset();

            Assert.False(estimator.IsReady);

            // Two estimators reset to a NEUTRAL reading rather than to zero, deliberately: zero is an
            // extreme for both, and a window that opens reporting one is worse than one reporting
            // nothing.
            var expected = estimator switch
            {
                Rsi => 50d,
                BollingerBands => 0.5d,
                _ => 0d,
            };
            Assert.Equal(expected, estimator.Value, Tolerance);
        }
    }

    private static void Drive(IEstimator estimator)
    {
        for (var i = 0; i < 30; i++)
        {
            var value = 100d + i;
            switch (estimator)
            {
                case Rsi rsi: rsi.Update(value); break;
                case Atr atr: atr.Update(Bar(value, value + 1d, value - 1d, value)); break;
                case Macd macd: macd.Update(value); break;
                case BollingerBands bands: bands.Update(value); break;
                case Vwap vwap: vwap.Update(value, 10d); break;
                case RealizedVolatility volatility: volatility.Update(value); break;
                case RollingCorrelation correlation: correlation.Update(i, value); break;
                case OrderFlowImbalance flow: flow.Update(10d, TradeSide.Buy); break;
                case Vpin vpin: vpin.Update(10d, TradeSide.Buy); break;
                case KyleLambda lambda: lambda.Close(value); break;
                case SpreadStats spreads: spreads.Update(0.01d); break;
                case KalmanLevel kalman: kalman.Update(value); break;
                case Ema ema: ema.Update(value); break;
                case Wilder wilder: wilder.Update(value); break;
                case Sma sma: sma.Update(value); break;
                case Dema dema: dema.Update(value); break;
                case Welford welford: welford.Update(value); break;
                case EwmaVariance ewma: ewma.Update(value); break;
                case ZScore score: score.Update(value); break;
            }
        }
    }

    private static OhlcvBar Bar(double open, double high, double low, double close) =>
        new(new InstrumentId(1), BarSize.OneMinute, DateTime.UnixEpoch,
            open, high, low, close, 1L, BrokerKind.Binance, true);

    private static Quote Quote(double bid, double ask) =>
        new(new InstrumentId(1), DateTime.UnixEpoch, DateTime.UnixEpoch,
            bid, ask, 1L, 1L, BrokerKind.Binance, 1L, false);
}
