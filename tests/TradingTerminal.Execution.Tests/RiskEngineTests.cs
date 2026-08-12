using TradingTerminal.Core.Domain;
using TradingTerminal.Execution;

namespace TradingTerminal.Execution.Tests;

public sealed class RiskEngineTests
{
    private static readonly InstrumentId Instrument = new(501);
    private static readonly DateOnly RiskDay = new(2026, 8, 4);

    [Fact]
    public void WithinLimits_AcceptsAndRecordsVersionedExposure()
    {
        var engine = CreateEngine(
            "risk-main",
            "7",
            maximumOrderQuantity: 3,
            maximumOrderNotional: 300,
            maximumPosition: 5,
            maximumGrossExposure: 500,
            dailyLossLimit: 100);
        var input = Snapshot(target: 5, current: 2, grossBefore: 200, realizedPnl: -50, markToMarketPnl: -49);

        var decision = engine.Evaluate(input);

        Assert.True(decision.IsAccepted);
        Assert.Equal(RiskReasonCode.None, decision.ReasonCodes);
        Assert.Equal("risk-main", decision.PolicyId);
        Assert.Equal("7", decision.PolicyVersion);
        Assert.Equal(64, decision.PolicyHash.Length);
        Assert.Equal(ScaledQuantity.FromWhole(3), decision.SignedOrderQuantity);
        Assert.Equal(new ScaledMoney(300, 0), decision.OrderNotional);
        Assert.Equal(
            new RiskExposureSnapshot(ScaledQuantity.FromWhole(2), new ScaledMoney(200, 0), new ScaledMoney(200, 0)),
            decision.ExposureBefore);
        Assert.Equal(
            new RiskExposureSnapshot(ScaledQuantity.FromWhole(5), new ScaledMoney(500, 0), new ScaledMoney(500, 0)),
            decision.ExposureAfter);
        Assert.Equal(input, decision.Input);
        Assert.Equal(decision, Assert.Single(engine.Decisions));
    }

    [Fact]
    public void OrderQuantityCap_RejectsReversalWithoutClamping()
    {
        var engine = CreateEngine(maximumOrderQuantity: 9, maximumPosition: 5);
        var input = Snapshot(target: -5, current: 5, grossBefore: 500);

        var decision = engine.Evaluate(input);

        Assert.False(decision.IsAccepted);
        Assert.Equal(RiskReasonCode.MaximumOrderQuantityExceeded, decision.ReasonCodes);
        Assert.Equal(ScaledQuantity.FromWhole(-10), decision.SignedOrderQuantity);
        Assert.Equal(ScaledQuantity.FromWhole(-5), decision.Input.Intent.SignedUnits);
        Assert.Equal(ScaledQuantity.FromWhole(-5), decision.ExposureAfter.Position);
        Assert.Equal(decision, Assert.Single(engine.Decisions));
    }

    [Fact]
    public void OrderNotionalCap_RejectsWithoutClamping()
    {
        var engine = CreateEngine(maximumOrderNotional: 199);
        var input = Snapshot(target: 2);

        var decision = engine.Evaluate(input);

        Assert.Equal(RiskDecisionOutcome.Rejected, decision.Outcome);
        Assert.Equal(RiskReasonCode.MaximumOrderNotionalExceeded, decision.ReasonCodes);
        Assert.Equal(new ScaledMoney(200, 0), decision.OrderNotional);
        Assert.Equal(ScaledQuantity.FromWhole(2), decision.Input.Intent.SignedUnits);
        Assert.False(decision.IsAccepted);
        Assert.Equal(decision, Assert.Single(engine.Decisions));
    }

    [Fact]
    public void AbsolutePositionCap_RejectsProjectedPosition()
    {
        var engine = CreateEngine(maximumPosition: 5);
        var input = Snapshot(target: 6, current: 4, grossBefore: 400);

        var decision = engine.Evaluate(input);

        Assert.Equal(RiskDecisionOutcome.Rejected, decision.Outcome);
        Assert.False(decision.IsAccepted);
        Assert.Equal(RiskReasonCode.MaximumAbsolutePositionExceeded, decision.ReasonCodes);
        Assert.Equal(ScaledQuantity.FromWhole(2), decision.SignedOrderQuantity);
        Assert.Equal(ScaledQuantity.FromWhole(6), decision.ExposureAfter.Position);
        Assert.Equal(decision, Assert.Single(engine.Decisions));
    }

    [Fact]
    public void GrossExposureCap_ReplacesInstrumentExposureBeforeComparison()
    {
        var engine = CreateEngine(maximumGrossExposure: 599);
        var input = Snapshot(target: 3, current: 2, grossBefore: 500);

        var decision = engine.Evaluate(input);

        Assert.Equal(RiskDecisionOutcome.Rejected, decision.Outcome);
        Assert.False(decision.IsAccepted);
        Assert.Equal(RiskReasonCode.MaximumGrossExposureExceeded, decision.ReasonCodes);
        Assert.Equal(new ScaledMoney(200, 0), decision.ExposureBefore.InstrumentExposure);
        Assert.Equal(new ScaledMoney(500, 0), decision.ExposureBefore.GrossExposure);
        Assert.Equal(new ScaledMoney(300, 0), decision.ExposureAfter.InstrumentExposure);
        Assert.Equal(new ScaledMoney(600, 0), decision.ExposureAfter.GrossExposure);
        Assert.Equal(decision, Assert.Single(engine.Decisions));
    }

    [Theory]
    [InlineData(-100L, 0L)]
    [InlineData(0L, -100L)]
    public void DailyLossCap_RejectsRealizedOrMarkToMarketLoss(long realizedPnl, long markToMarketPnl)
    {
        var engine = CreateEngine(dailyLossLimit: 100);

        var first = engine.Evaluate(Snapshot(
            target: 1,
            realizedPnl: realizedPnl,
            markToMarketPnl: markToMarketPnl));
        var afterRecovery = engine.Evaluate(Snapshot(
            target: 1,
            realizedPnl: 1_000,
            markToMarketPnl: 1_000));

        Assert.Equal(RiskDecisionOutcome.Rejected, first.Outcome);
        Assert.False(first.IsAccepted);
        Assert.Equal(RiskReasonCode.DailyLossLimitExceeded, first.ReasonCodes);
        Assert.Equal(RiskDecisionOutcome.Rejected, afterRecovery.Outcome);
        Assert.False(afterRecovery.IsAccepted);
        Assert.Equal(RiskReasonCode.DailyLossLimitExceeded, afterRecovery.ReasonCodes);
        Assert.True(engine.IsDailyLossLimitTripped);
        Assert.Equal(first, engine.Decisions[0]);
        Assert.Equal(afterRecovery, engine.Decisions[1]);
    }

    [Fact]
    public void KillSwitch_RejectsEveryLaterIntentWithoutFlattening()
    {
        var engine = CreateEngine();
        var admitted = engine.Evaluate(Snapshot(target: 1));

        engine.TripKillSwitch();
        var flat = engine.Evaluate(Snapshot(target: 0, current: 1, grossBefore: 100));
        var newEntry = engine.Evaluate(Snapshot(target: 2, current: 1, grossBefore: 100));

        Assert.True(admitted.IsAccepted);
        Assert.True(engine.IsKillSwitchTripped);
        Assert.Equal(RiskDecisionOutcome.Rejected, flat.Outcome);
        Assert.False(flat.IsAccepted);
        Assert.Equal(RiskReasonCode.KillSwitchTripped, flat.ReasonCodes);
        Assert.Equal(ScaledQuantity.Zero, flat.Input.Intent.SignedUnits);
        Assert.Equal(ScaledQuantity.Zero, flat.ExposureAfter.Position);
        Assert.Equal(RiskDecisionOutcome.Rejected, newEntry.Outcome);
        Assert.False(newEntry.IsAccepted);
        Assert.Equal(RiskReasonCode.KillSwitchTripped, newEntry.ReasonCodes);
        Assert.Equal(3, engine.Decisions.Count);
    }

    [Fact]
    public void PolicyUpdate_DoesNotAlterPriorDecisionRecord()
    {
        var engine = CreateEngine("risk-a", "1", maximumOrderQuantity: 10, dailyLossLimit: 100);
        var input = Snapshot(target: 2, realizedPnl: -100);
        var first = engine.Evaluate(input);
        var retained = engine.Decisions[0];
        var replacement = CreatePolicy(
            "risk-b",
            "2",
            maximumOrderQuantity: 1,
            dailyLossLimit: 1_000);

        engine.ReplacePolicy(replacement);
        var second = engine.Evaluate(input);

        Assert.Equal(retained, engine.Decisions[0]);
        Assert.Equal(first.PolicyHash, engine.Decisions[0].PolicyHash);
        Assert.Equal("risk-a", engine.Decisions[0].PolicyId);
        Assert.Equal(RiskReasonCode.DailyLossLimitExceeded, engine.Decisions[0].ReasonCodes);
        Assert.Equal("risk-b", second.PolicyId);
        Assert.NotEqual(first.PolicyHash, second.PolicyHash);
        Assert.Equal(RiskReasonCode.MaximumOrderQuantityExceeded, second.ReasonCodes);
    }

    private static RiskInputSnapshot Snapshot(
        long target,
        long current = 0,
        long grossBefore = 0,
        long realizedPnl = 0,
        long markToMarketPnl = 0) =>
        new(
            Intent(target),
            ScaledQuantity.FromWhole(current),
            new ScaledPrice(100, 0),
            new ScaledRatio(1, 0),
            new ScaledMoney(grossBefore, 0),
            new ScaledMoney(realizedPnl, 0),
            new ScaledMoney(markToMarketPnl, 0),
            RiskDay);

    private static TradeIntent Intent(long target) =>
        new(
            Instrument,
            TradeIntentQuantityMode.TargetPosition,
            ScaledQuantity.FromWhole(target),
            null,
            null,
            ScaledMoney.Zero,
            "risk-test.strategy",
            44,
            "signal-policy-v1");

    private static RiskEngine CreateEngine(
        string policyId = "risk-test",
        string policyVersion = "1",
        long maximumOrderQuantity = 100,
        long maximumOrderNotional = 100_000,
        long maximumPosition = 100,
        long maximumGrossExposure = 100_000,
        long dailyLossLimit = 10_000) =>
        new(CreatePolicy(
            policyId,
            policyVersion,
            maximumOrderQuantity,
            maximumOrderNotional,
            maximumPosition,
            maximumGrossExposure,
            dailyLossLimit));

    private static RiskPolicy CreatePolicy(
        string policyId,
        string policyVersion,
        long maximumOrderQuantity = 100,
        long maximumOrderNotional = 100_000,
        long maximumPosition = 100,
        long maximumGrossExposure = 100_000,
        long dailyLossLimit = 10_000)
    {
        var limits = new RiskLimits(
            ScaledQuantity.FromWhole(maximumOrderQuantity),
            new ScaledMoney(maximumOrderNotional, 0),
            ScaledQuantity.FromWhole(maximumPosition),
            new ScaledMoney(maximumGrossExposure, 0),
            new ScaledMoney(dailyLossLimit, 0));
        var fault = RiskPolicy.TryCreate(policyId, policyVersion, limits, out var policy);
        Assert.Equal(RiskPolicyFault.None, fault);
        return Assert.IsType<RiskPolicy>(policy);
    }
}
