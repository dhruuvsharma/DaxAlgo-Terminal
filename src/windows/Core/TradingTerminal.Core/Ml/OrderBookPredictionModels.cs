using TradingTerminal.Core.Domain;
using TradingTerminal.Core.MarketData;

namespace TradingTerminal.Core.Ml;

/// <summary>
/// One fixed-cadence sample of the order book plus the trade flow that arrived since the previous
/// sample — the input the <see cref="OrderBookMicroPredictor"/> steps on. All prices are raw
/// (tick-free) units; the predictor normalizes with its own tick estimate. Built through a single
/// path (<see cref="From"/>) shared by the live capture tick and the warm-start resampler so
/// historical and live steps are exactly comparable.
/// </summary>
/// <param name="TimestampUtc">Step time (the capture tick / resample boundary, not the exchange event time).</param>
/// <param name="BestBid">Best bid price (0 when the side is empty).</param>
/// <param name="BestAsk">Best ask price (0 when the side is empty).</param>
/// <param name="BestBidSize">Resting size at the best bid.</param>
/// <param name="BestAskSize">Resting size at the best ask.</param>
/// <param name="Microprice">Size-weighted L1 fair price (<see cref="Microstructure.Microprice(double,double,long,long)"/>).</param>
/// <param name="WeightedMid">Size-weighted mid across the stats depth.</param>
/// <param name="ImbalanceL1">Top-of-book queue imbalance in [-1, 1].</param>
/// <param name="ImbalanceCum">Cumulative top-N queue imbalance in [-1, 1].</param>
/// <param name="BidDepthTop3">Cumulative bid size across the top 3 levels.</param>
/// <param name="AskDepthTop3">Cumulative ask size across the top 3 levels.</param>
/// <param name="BidDepthTopN">Cumulative bid size across the stats depth.</param>
/// <param name="AskDepthTopN">Cumulative ask size across the stats depth.</param>
/// <param name="LargestGapBid">Largest price gap between consecutive bid levels.</param>
/// <param name="LargestGapAsk">Largest price gap between consecutive ask levels.</param>
/// <param name="MinLevelGap">Smallest positive gap between adjacent levels on either side — the
/// predictor's tick-size estimator input. 0 when unknown (fewer than 2 levels per side).</param>
/// <param name="SweepCostBuy">Slippage to fill the sweep size through the asks.</param>
/// <param name="SweepCostSell">Slippage to fill the sweep size through the bids.</param>
/// <param name="SignedTradeFlow">Signed traded volume (buy − sell) since the previous step.</param>
/// <param name="TradeCount">Trade prints since the previous step.</param>
/// <param name="TradeFlowValid">False when flow wasn't observable (warm-start steps replay depth
/// only) — surfaced as a feature so the model learns to discount the flow inputs.</param>
public sealed record OrderBookStepSummary(
    DateTime TimestampUtc,
    double BestBid,
    double BestAsk,
    long BestBidSize,
    long BestAskSize,
    double Microprice,
    double WeightedMid,
    double ImbalanceL1,
    double ImbalanceCum,
    long BidDepthTop3,
    long AskDepthTop3,
    long BidDepthTopN,
    long AskDepthTopN,
    double LargestGapBid,
    double LargestGapAsk,
    double MinLevelGap,
    double SweepCostBuy,
    double SweepCostSell,
    long SignedTradeFlow,
    int TradeCount,
    bool TradeFlowValid)
{
    private const int MaxGapScanLevels = 30;

    /// <summary>Projects one depth snapshot (plus the accumulated flow) into the step shape,
    /// reusing the <see cref="Microstructure"/> helpers throughout. <paramref name="timestampUtc"/>
    /// overrides the snapshot's own time — the live path passes the capture-tick time, the
    /// warm-start resampler passes the 250 ms boundary.</summary>
    public static OrderBookStepSummary From(
        DepthSnapshot snapshot,
        int statsDepth,
        long sweepSize,
        long signedTradeFlow,
        int tradeCount,
        bool tradeFlowValid,
        DateTime? timestampUtc = null)
    {
        var bids = snapshot.Bids;
        var asks = snapshot.Asks;
        var bestBidSize = bids.Count > 0 ? bids[0].Size : 0;
        var bestAskSize = asks.Count > 0 ? asks[0].Size : 0;

        return new OrderBookStepSummary(
            TimestampUtc: timestampUtc ?? snapshot.TimestampUtc,
            BestBid: snapshot.BestBid,
            BestAsk: snapshot.BestAsk,
            BestBidSize: bestBidSize,
            BestAskSize: bestAskSize,
            Microprice: Microstructure.Microprice(snapshot.BestBid, snapshot.BestAsk, bestBidSize, bestAskSize),
            WeightedMid: Microstructure.WeightedMidPrice(snapshot, statsDepth),
            ImbalanceL1: Microstructure.QueueImbalance(bestBidSize, bestAskSize),
            ImbalanceCum: Microstructure.CumulativeImbalance(snapshot, statsDepth),
            BidDepthTop3: Microstructure.SideDepth(bids, 3),
            AskDepthTop3: Microstructure.SideDepth(asks, 3),
            BidDepthTopN: Microstructure.SideDepth(bids, statsDepth),
            AskDepthTopN: Microstructure.SideDepth(asks, statsDepth),
            LargestGapBid: Microstructure.LargestLevelGap(bids),
            LargestGapAsk: Microstructure.LargestLevelGap(asks),
            MinLevelGap: MinAdjacentGap(bids, asks),
            SweepCostBuy: Microstructure.EstimatedSlippage(asks, sweepSize, out _),
            SweepCostSell: Microstructure.EstimatedSlippage(bids, sweepSize, out _),
            SignedTradeFlow: signedTradeFlow,
            TradeCount: tradeCount,
            TradeFlowValid: tradeFlowValid);
    }

    private static double MinAdjacentGap(IReadOnlyList<DepthLevel> bids, IReadOnlyList<DepthLevel> asks)
    {
        var min = double.MaxValue;
        Scan(bids);
        Scan(asks);
        return min == double.MaxValue ? 0 : min;

        void Scan(IReadOnlyList<DepthLevel> side)
        {
            var n = Math.Min(side.Count, MaxGapScanLevels);
            for (var i = 1; i < n; i++)
            {
                var gap = Math.Abs(side[i].Price - side[i - 1].Price);
                if (gap > 0 && gap < min) min = gap;
            }
        }
    }
}

/// <summary>One point of the predicted microprice path: the forecast at
/// <paramref name="HorizonSteps"/> steps (× 250 ms) past the reference step.</summary>
public readonly record struct MicropricePoint(int HorizonSteps, double Microprice);

/// <summary>
/// One order-book forecast: the predicted microprice path across the direction horizons plus the
/// three liquidity-event probabilities over the event window. Prices absolute; probabilities
/// clamped to [0, 1].
/// </summary>
public sealed record OrderBookForecast(
    DateTime ReferenceTimeUtc,
    double ReferenceMicroprice,
    double TickSize,
    IReadOnlyList<MicropricePoint> Path,
    double PSpreadWidens,
    double PDepthDrains,
    double PSweepJumps);

/// <summary>Rolling calibration read-out for one event forecaster: mean squared probability error
/// (<paramref name="Brier"/>, lower is better; 0.25 = always saying 50%), the event's observed
/// <paramref name="BaseRate"/> over the same window (the "climatology" comparison — a useful model
/// beats Brier = baseRate·(1−baseRate)), and the lifetime scored count.</summary>
public readonly record struct EventScore(double Brier, double BaseRate, long ScoredCount);

/// <summary>Tuning for <see cref="OrderBookMicroPredictor"/>. Steps are the 250 ms capture cadence.</summary>
/// <param name="FlagshipHorizon">The horizon (in steps) the ML-vs-baseline scoreboard is scored at.
/// Must be one of <see cref="Horizons"/>.</param>
/// <param name="EventWindow">Lookahead window W (steps) over which the event labels are evaluated.</param>
/// <param name="MetricsWindow">Rolling window for MAE / hit-rate / Brier.</param>
/// <param name="Lambda">RLS exponential-forgetting factor.</param>
/// <param name="MinSamplesReady">Flagship-learner updates required before forecasts are emitted.</param>
/// <param name="HistoryCapacity">Depth of the internal step ring (must cover EventWindow + feature lags).</param>
/// <param name="BaselineDeadBand">|L1 imbalance| below this ⇒ the baseline rule predicts "no move".</param>
/// <param name="SpreadWidenTicks">Spread-widening label threshold, in ticks above the reference spread.</param>
/// <param name="DepthDrainRatio">Depth-drain label: a side's top-3 depth dips to ≤ this fraction of reference.</param>
/// <param name="SweepJumpRatio">Sweep-jump label: worst-side sweep cost reaches ≥ this multiple of reference.</param>
public sealed record OrderBookPredictorOptions(
    int FlagshipHorizon = 4,
    int EventWindow = 8,
    int MetricsWindow = 200,
    double Lambda = 0.995,
    int MinSamplesReady = 40,
    int HistoryCapacity = 64,
    double BaselineDeadBand = 0.05,
    double SpreadWidenTicks = 1.0,
    double DepthDrainRatio = 0.7,
    double SweepJumpRatio = 1.25)
{
    /// <summary>Direction horizons in steps (250 ms each): 250 ms / 500 ms / 1 s / 2 s / 5 s.</summary>
    public IReadOnlyList<int> Horizons { get; init; } = DefaultHorizons;

    public static readonly int[] DefaultHorizons = { 1, 2, 4, 8, 20 };
}
