using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Execution;

namespace TradingTerminal.Execution.Tests;

public sealed class SignalExecutionPolicyTests
{
    private static readonly InstrumentId Instrument = new(17);

    [Fact]
    public void FixedContracts_ProducesExactTargetAndVersionedProvenance()
    {
        var policy = CreatePolicy(MaxUnits(20));

        var decision = policy.Evaluate(
            "strategy.alpha",
            new StrategySignal(StrategySignalKind.Long, 1d, 42),
            UnitDefinition.FixedContracts(7),
            Inputs());

        Assert.True(decision.IsAccepted);
        var intent = decision.Intent!.Value;
        Assert.Equal(ScaledQuantity.FromWhole(7), intent.SignedUnits);
        Assert.Equal(TradeIntentQuantityMode.TargetPosition, intent.QuantityMode);
        Assert.Equal("strategy.alpha", intent.StrategyId);
        Assert.Equal(42, intent.StrategyNoteId);
        Assert.Equal("signal-policy-v1", intent.PolicyVersion);
        Assert.Null(intent.ProtectiveStopPrice);
        Assert.Null(intent.ProfitTargetPrice);
    }

    [Fact]
    public void PercentOfEquityAtRisk_ProducesExpectedIntent()
    {
        var policy = CreatePolicy(MaxUnits(200));
        var unit = UnitDefinition.PercentOfEquityAtRisk(
            basisPoints: 100,
            riskDistance: Price(10));

        var decision = policy.Evaluate(
            "strategy.percent",
            new StrategySignal(StrategySignalKind.Long, 1d),
            unit,
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(100), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void FixedCashRisk_ProducesExpectedIntent()
    {
        var policy = CreatePolicy(MaxUnits(100));
        var unit = UnitDefinition.FixedCashRisk(Money(500), Price(10));

        var decision = policy.Evaluate(
            "strategy.cash",
            new StrategySignal(StrategySignalKind.Short, 1d),
            unit,
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(-50), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void RiskSizing_AppliesStrengthBeforeWholeContractFloor()
    {
        var policy = CreatePolicy(MaxUnits(10));

        var decision = policy.Evaluate(
            "strategy.fractional-risk",
            new StrategySignal(StrategySignalKind.Long, 0.8d),
            UnitDefinition.FixedCashRisk(Money(19), Price(10)),
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(1), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void RiskSizing_IsInvariantToEquivalentHighScaleEncoding()
    {
        const long nineAtScale18 = 9_000_000_000_000_000_000;
        const long oneAtScale18 = 1_000_000_000_000_000_000;
        var policy = CreatePolicy(MaxUnits(10));
        var inputs = Inputs() with { ContractMultiplier = new ScaledRatio(oneAtScale18, 18) };

        var decision = policy.Evaluate(
            "strategy.high-scale",
            new StrategySignal(StrategySignalKind.Long, 1d),
            UnitDefinition.FixedCashRisk(
                new ScaledMoney(nineAtScale18, 18),
                new ScaledPrice(nineAtScale18, 18)),
            inputs);

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(1), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void VolatilityScaled_ProducesExpectedIntent()
    {
        var policy = CreatePolicy(MaxUnits(200));
        var unit = UnitDefinition.VolatilityScaled(
            cashRisk: Money(600),
            volatility: Price(2),
            volatilityMultipleBasisPoints: 30_000);

        var decision = policy.Evaluate(
            "strategy.volatility",
            new StrategySignal(StrategySignalKind.Long, 1d),
            unit,
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(100), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void VolatilityScaled_PreservesFractionalBasisPointProductExactly()
    {
        var policy = CreatePolicy(MaxUnits(10));
        var unit = UnitDefinition.VolatilityScaled(
            cashRisk: Money(10),
            volatility: new ScaledPrice(5, 0),
            volatilityMultipleBasisPoints: 5_000);

        var decision = policy.Evaluate(
            "strategy.volatility-fraction",
            new StrategySignal(StrategySignalKind.Long, 1d),
            unit,
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(4), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void CostAssumptions_AreIncludedInSizingAndIntent()
    {
        var costs = new SignalCostAssumptions(Money(0.25m), Money(0.25m), Money(0.50m));
        var policy = CreatePolicy(MaxUnits(100), costs);
        var unit = UnitDefinition.FixedCashRisk(Money(110), Price(10));

        var decision = policy.Evaluate(
            "strategy.costed",
            new StrategySignal(StrategySignalKind.Long, 1d),
            unit,
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(10), decision.Intent!.Value.SignedUnits);
        Assert.Equal(new ScaledMoney(1, 0), decision.Intent.Value.EstimatedRoundTripCostPerUnit);
    }

    [Fact]
    public void CostSum_IsInvariantToEquivalentHighScaleEncoding()
    {
        const long nineAtScale18 = 9_000_000_000_000_000_000;
        var costs = new SignalCostAssumptions(
            new ScaledMoney(nineAtScale18, 18),
            new ScaledMoney(nineAtScale18, 18),
            ScaledMoney.Zero);
        var policy = CreatePolicy(MaxUnits(1), costs);

        var decision = policy.Evaluate(
            "strategy.high-scale-cost",
            new StrategySignal(StrategySignalKind.Long, 1d),
            UnitDefinition.FixedContracts(1),
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(new ScaledMoney(18, 0), decision.Intent!.Value.EstimatedRoundTripCostPerUnit);
    }

    [Theory]
    [InlineData(StrategySignalKind.Long, 90, 120)]
    [InlineData(StrategySignalKind.Short, 110, 80)]
    public void OptionalProtectivePrices_AreExactPriceTerms(
        StrategySignalKind kind,
        int expectedStop,
        int expectedTarget)
    {
        var options = new SignalExecutionPolicyOptions(
            SignalCostAssumptions.Zero,
            MaxUnits(20),
            AttachSizingRiskAsProtectiveStop: true,
            ProfitTargetMultipleBasisPoints: 20_000);
        var policy = CreatePolicy(options);

        var decision = policy.Evaluate(
            "strategy.protected",
            new StrategySignal(kind, 1d),
            UnitDefinition.FixedCashRisk(Money(100), Price(10)),
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(new ScaledPrice(expectedStop, 0), decision.Intent!.Value.ProtectiveStopPrice);
        Assert.Equal(new ScaledPrice(expectedTarget, 0), decision.Intent.Value.ProfitTargetPrice);
    }

    [Fact]
    public void ProtectivePrice_IsInvariantToEquivalentHighScaleEncoding()
    {
        const long nineAtScale18 = 9_000_000_000_000_000_000;
        var options = new SignalExecutionPolicyOptions(
            SignalCostAssumptions.Zero,
            MaxUnits(1),
            AttachSizingRiskAsProtectiveStop: true);
        var policy = CreatePolicy(options);
        var inputs = Inputs() with { ReferencePrice = new ScaledPrice(nineAtScale18, 18) };

        var decision = policy.Evaluate(
            "strategy.high-scale-protection",
            new StrategySignal(StrategySignalKind.Short, 1d),
            UnitDefinition.FixedCashRisk(
                new ScaledMoney(nineAtScale18, 18),
                new ScaledPrice(nineAtScale18, 18)),
            inputs);

        Assert.True(decision.IsAccepted);
        Assert.Equal(new ScaledPrice(18, 0), decision.Intent!.Value.ProtectiveStopPrice);
    }

    [Fact]
    public void FractionalSignalThatFloorsToZero_ProducesUnprotectedFlatTarget()
    {
        var options = new SignalExecutionPolicyOptions(
            SignalCostAssumptions.Zero,
            MaxUnits(1),
            AttachSizingRiskAsProtectiveStop: true,
            ProfitTargetMultipleBasisPoints: 20_000);
        var policy = CreatePolicy(options);

        var decision = policy.Evaluate(
            "strategy.floored-flat",
            new StrategySignal(StrategySignalKind.Long, 0.5d),
            UnitDefinition.FixedContracts(1),
            Inputs());

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.Zero, decision.Intent!.Value.SignedUnits);
        Assert.Null(decision.Intent.Value.ProtectiveStopPrice);
        Assert.Null(decision.Intent.Value.ProfitTargetPrice);
    }

    [Fact]
    public void AbsoluteUnitCap_RejectsFullCandidateWithoutClamping()
    {
        var policy = CreatePolicy(MaxUnits(5));

        var decision = policy.Evaluate(
            "strategy.capped",
            new StrategySignal(StrategySignalKind.Long, 1d),
            UnitDefinition.FixedContracts(10),
            Inputs());

        Assert.False(decision.IsAccepted);
        Assert.Equal(SignalExecutionFault.BuyerUnitCapExceeded, decision.Fault);
        Assert.Equal(ScaledQuantity.FromWhole(10), decision.CandidateTargetUnits);
        Assert.Null(decision.Intent);
    }

    [Fact]
    public void NotionalCap_RejectsFullCandidateWithoutClamping()
    {
        var caps = new BuyerExecutionCaps(
            ScaledQuantity.FromWhole(10),
            MaximumNotional: Money(150));
        var policy = CreatePolicy(caps);

        var decision = policy.Evaluate(
            "strategy.notional",
            new StrategySignal(StrategySignalKind.Long, 1d),
            UnitDefinition.FixedContracts(2),
            Inputs());

        Assert.Equal(SignalExecutionFault.BuyerNotionalCapExceeded, decision.Fault);
        Assert.Equal(ScaledQuantity.FromWhole(2), decision.CandidateTargetUnits);
        Assert.Null(decision.Intent);
    }

    [Fact]
    public void MoneyCaps_AcceptEquivalentHighScaleNotionalAndRisk()
    {
        const long nineAtScale18 = 9_000_000_000_000_000_000;
        const long highPrecisionAlmostNine = nineAtScale18 - 1;
        var caps = new BuyerExecutionCaps(
            ScaledQuantity.FromWhole(1),
            MaximumNotional: new ScaledMoney(200, 0),
            MaximumCashRisk: new ScaledMoney(100, 0));
        var policy = CreatePolicy(caps);
        var inputs = Inputs() with
        {
            ReferencePrice = new ScaledPrice(highPrecisionAlmostNine, 18),
            ContractMultiplier = new ScaledRatio(highPrecisionAlmostNine, 18),
        };

        var decision = policy.Evaluate(
            "strategy.high-scale-caps",
            new StrategySignal(StrategySignalKind.Long, 1d),
            UnitDefinition.FixedCashRisk(
                new ScaledMoney(100, 0),
                new ScaledPrice(highPrecisionAlmostNine, 18)),
            inputs);

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.FromWhole(1), decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void CashRiskCap_RejectsFullCandidateWithoutClamping()
    {
        var caps = new BuyerExecutionCaps(
            ScaledQuantity.FromWhole(20),
            MaximumCashRisk: Money(50));
        var policy = CreatePolicy(caps);

        var decision = policy.Evaluate(
            "strategy.cash-cap",
            new StrategySignal(StrategySignalKind.Long, 1d),
            UnitDefinition.FixedCashRisk(Money(100), Price(10)),
            Inputs());

        Assert.Equal(SignalExecutionFault.BuyerCashRiskCapExceeded, decision.Fault);
        Assert.Equal(ScaledQuantity.FromWhole(10), decision.CandidateTargetUnits);
        Assert.Null(decision.Intent);
    }

    [Fact]
    public void FlatSignal_BypassesStaleEntrySizingAndProducesExitTarget()
    {
        var policy = CreatePolicy(MaxUnits(5));
        var invalidInputs = Inputs() with
        {
            ReferencePrice = default,
            Equity = default,
            ContractMultiplier = default,
            CurrentPosition = ScaledQuantity.FromWhole(3),
        };

        var decision = policy.Evaluate(
            "strategy.exit",
            new StrategySignal(StrategySignalKind.Flat, 1d),
            default,
            invalidInputs);

        Assert.True(decision.IsAccepted);
        Assert.Equal(ScaledQuantity.Zero, decision.Intent!.Value.SignedUnits);
    }

    [Fact]
    public void RepeatedIdenticalInput_IsBitForBitDeterministicAndAllocationFree()
    {
        var policy = CreatePolicy(MaxUnits(200));
        var signal = new StrategySignal(StrategySignalKind.Short, 0.875d, 9);
        var unit = UnitDefinition.FixedCashRisk(Money(1_000), Price(8));
        var inputs = Inputs();
        var expected = policy.Evaluate("strategy.repeat", signal, unit, inputs);

        // Best-of-N allocation measurement. A genuinely allocation-free path yields a zero delta once
        // fully warmed; a one-time tiered-JIT recompile or GC on the measurement thread can perturb an
        // individual round, so we take the minimum steady-state delta across several rounds. A path
        // that actually allocated per iteration would make every round non-zero.
        long minDelta = long.MaxValue;
        var allEqual = true;
        var measurementThread = new Thread(() =>
        {
            for (var index = 0; index < 20_000; index++)
                _ = expected.Equals(policy.Evaluate("strategy.repeat", signal, unit, inputs));

            for (var round = 0; round < 8 && minDelta != 0; round++)
            {
                _ = GC.GetAllocatedBytesForCurrentThread();
                var before = GC.GetAllocatedBytesForCurrentThread();
                for (var index = 0; index < 10_000; index++)
                    allEqual &= expected.Equals(policy.Evaluate("strategy.repeat", signal, unit, inputs));
                var after = GC.GetAllocatedBytesForCurrentThread();
                minDelta = Math.Min(minDelta, after - before);
            }
        });
        measurementThread.Start();
        measurementThread.Join();

        Assert.True(allEqual);
        Assert.Equal(0, minDelta);
    }

    [Fact]
    public void EconomicFields_AreExactScaledTypes()
    {
        var properties = typeof(TradeIntent).GetProperties().ToDictionary(p => p.Name, p => p.PropertyType);

        Assert.Equal(typeof(ScaledQuantity), properties[nameof(TradeIntent.SignedUnits)]);
        Assert.Equal(typeof(ScaledPrice?), properties[nameof(TradeIntent.ProtectiveStopPrice)]);
        Assert.Equal(typeof(ScaledPrice?), properties[nameof(TradeIntent.ProfitTargetPrice)]);
        Assert.Equal(typeof(ScaledMoney), properties[nameof(TradeIntent.EstimatedRoundTripCostPerUnit)]);
        Assert.DoesNotContain(properties.Values, type => type == typeof(double) || type == typeof(float));
    }

    private static SignalExecutionInputs Inputs() => new(
        Instrument,
        Price(100),
        Money(100_000),
        ScaledQuantity.Zero,
        new ScaledRatio(100, 2));

    private static BuyerExecutionCaps MaxUnits(long units) =>
        new(ScaledQuantity.FromWhole(units));

    private static SignalExecutionPolicy CreatePolicy(
        BuyerExecutionCaps caps,
        SignalCostAssumptions? costs = null) =>
        CreatePolicy(new SignalExecutionPolicyOptions(costs ?? SignalCostAssumptions.Zero, caps));

    private static SignalExecutionPolicy CreatePolicy(SignalExecutionPolicyOptions options)
    {
        var fault = SignalExecutionPolicy.TryCreate("signal-policy-v1", options, out var policy);
        Assert.Equal(SignalExecutionFault.None, fault);
        return Assert.IsType<SignalExecutionPolicy>(policy);
    }

    private static ScaledPrice Price(decimal value) =>
        new(decimal.ToInt64(value * 100m), 2);

    private static ScaledMoney Money(decimal value) =>
        new(decimal.ToInt64(value * 100m), 2);
}
