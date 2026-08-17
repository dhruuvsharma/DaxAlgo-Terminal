using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Trading;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class SimulatedVenueTests
{
    [Fact]
    public void EveryCanonicalOrderType_MapsAcrossPublicBoundaryWithoutDowngrade()
    {
        var mappings = new[]
        {
            (OrderType.Market, CanonicalOrderType.Market),
            (OrderType.Limit, CanonicalOrderType.Limit),
            (OrderType.Stop, CanonicalOrderType.Stop),
            (OrderType.StopLimit, CanonicalOrderType.StopLimit),
        };
        foreach (var (publicType, canonicalType) in mappings)
        {
            Assert.True(PublicOrderSeamMapper.TryFromPublicOrderType(publicType, out var mapped, out var fromFault));
            Assert.Equal(PublicOrderMappingFault.None, fromFault);
            Assert.Equal(canonicalType, mapped);
            Assert.True(PublicOrderSeamMapper.TryToPublicOrderType(mapped, out var roundTrip, out var toFault));
            Assert.Equal(PublicOrderMappingFault.None, toFault);
            Assert.Equal(publicType, roundTrip);
        }
    }

    [Fact]
    public void EveryCanonicalTimeInForce_MapsAcrossPublicBoundaryWithoutDowngrade()
    {
        var mappings = new[]
        {
            (TimeInForce.Day, CanonicalTimeInForce.Day),
            (TimeInForce.Gtc, CanonicalTimeInForce.GoodTillCancelled),
            (TimeInForce.Ioc, CanonicalTimeInForce.ImmediateOrCancel),
            (TimeInForce.Fok, CanonicalTimeInForce.FillOrKill),
        };
        foreach (var (publicValue, canonicalValue) in mappings)
        {
            Assert.True(PublicOrderSeamMapper.TryFromPublicTimeInForce(publicValue, out var mapped, out var fromFault));
            Assert.Equal(PublicOrderMappingFault.None, fromFault);
            Assert.Equal(canonicalValue, mapped);
            Assert.True(PublicOrderSeamMapper.TryToPublicTimeInForce(mapped, out var roundTrip, out var toFault));
            Assert.Equal(PublicOrderMappingFault.None, toFault);
            Assert.Equal(publicValue, roundTrip);
        }
    }

    [Fact]
    public void PublicStopLimitRequest_QuantizesOnlyAtExplicitBoundary_AndRoundTrips()
    {
        var request = new OrderRequest(
            "public-order-1",
            Contract.UsStock("TEST"),
            OrderSide.Buy,
            OrderType.StopLimit,
            3,
            100.25,
            99.75,
            TimeInForce.Gtc);

        Assert.True(PublicOrderSeamMapper.TryFromPublicRequest(
            request,
            2,
            out var clientOrderId,
            out var terms,
            out var inboundFault));
        Assert.Equal(PublicOrderMappingFault.None, inboundFault);
        Assert.Equal(new ScaledPrice(10_025, 2), terms.LimitPrice);
        Assert.Equal(new ScaledPrice(9_975, 2), terms.StopPrice);

        Assert.True(PublicOrderSeamMapper.TryToPublicRequest(
            clientOrderId,
            request.Contract,
            terms,
            out var roundTrip,
            out var outboundFault));
        Assert.Equal(PublicOrderMappingFault.None, outboundFault);
        Assert.Equal(request, roundTrip);
    }

    [Fact]
    public void PublicOrderEvent_MapsThroughTypedVenueBoundaryWithExactFillEconomics()
    {
        var publicEvent = new TradingTerminal.Core.Trading.OrderEvent(
            OmsTestData.TimestampUtc,
            "public-fill-1",
            "sim-broker-1",
            OrderSide.Buy,
            OrderState.PartiallyFilled,
            1,
            100.25,
            1,
            100.25,
            Liquidity: LiquidityFlag.Maker);

        Assert.True(PublicOrderSeamMapper.TryFromPublicOrderEvent(
            publicEvent,
            OmsTestData.Causation("public-fill"),
            OmsTestData.Dedup("public-fill"),
            2,
            new ScaledMoney(5, 2),
            out var venueEvent,
            out var inboundFault));
        Assert.Equal(PublicOrderMappingFault.None, inboundFault);
        Assert.Equal(VenueEventKind.Fill, venueEvent!.Kind);
        Assert.Equal(new ScaledPrice(10_025, 2), venueEvent.Fill!.Value.Price);
        Assert.Equal(new ScaledMoney(5, 2), venueEvent.Fill.Value.Fee);

        Assert.True(PublicOrderSeamMapper.TryToPublicOrderEvent(
            venueEvent,
            OrderSide.Buy,
            OrderLifecycleState.PartiallyFilled,
            ScaledQuantity.FromWhole(1),
            new ScaledPrice(10_025, 2),
            out var roundTrip,
            out var outboundFault));
        Assert.Equal(PublicOrderMappingFault.None, outboundFault);
        Assert.Equal(publicEvent, roundTrip);
    }

    [Fact]
    public void RichStateWithoutFaithfulPublicMeaning_IsRejectedExplicitly()
    {
        var states = new[]
        {
            OrderLifecycleState.Draft,
            OrderLifecycleState.Validated,
            OrderLifecycleState.Prepared,
            OrderLifecycleState.Armed,
            OrderLifecycleState.Unknown,
            OrderLifecycleState.Reconciling,
            OrderLifecycleState.PendingCancel,
            OrderLifecycleState.PendingReplace,
            OrderLifecycleState.Expired,
            OrderLifecycleState.Reconciled,
        };
        foreach (var state in states)
        {
            var mapped = PublicOrderSeamMapper.TryToPublicOrderState(state, out var publicState, out var fault);

            Assert.False(mapped);
            Assert.Equal(PublicOrderMappingFault.UnsupportedLifecycleState, fault);
            if (state == OrderLifecycleState.Unknown)
                Assert.NotEqual(OrderState.Rejected, publicState);
        }
    }

    [Fact]
    public void PartialFillPlan_ForFillOrKill_IsRejectedWithoutSilentDowngrade()
    {
        var instruction = OmsTestData.Instruction(
            timeInForce: CanonicalTimeInForce.FillOrKill);
        var plan = new VenueSubmitPlan(
            instruction.Identity.ClientOrderId,
            VenueSubmitOutcome.Accepted,
            [
                new FillExecution(
                    ScaledQuantity.FromWhole(1),
                    new ScaledPrice(100, 0),
                    ScaledMoney.Zero,
                    LiquidityFlag.Taker),
            ]);
        var venue = Venue([plan]);

        var result = venue.Submit(instruction, OmsTestData.Causation("fok"));

        Assert.Equal(VenueCommandStatus.Rejected, result.EffectiveStatus);
        Assert.Single(result.Events);
        Assert.Equal(VenueEventKind.Rejected, result.Events[0].Kind);
        Assert.DoesNotContain(result.Events, item => item.Kind == VenueEventKind.Fill);
    }

    [Fact]
    public void UndefinedCanonicalEnums_AreRejectedFailClosed()
    {
        var instruction = OmsTestData.Instruction();
        var invalidSide = instruction with
        {
            Terms = instruction.Terms with { Side = (OrderSide)byte.MaxValue },
        };
        var invalidMode = instruction with
        {
            TradeIntent = instruction.TradeIntent with
            {
                QuantityMode = (TradeIntentQuantityMode)byte.MaxValue,
            },
        };
        var invalidLiquidity = new FillExecution(
            ScaledQuantity.FromWhole(1),
            new ScaledPrice(100, 0),
            ScaledMoney.Zero,
            (LiquidityFlag)byte.MaxValue);

        Assert.Equal(OrderDomainFault.InvalidClassification, invalidSide.Validate());
        Assert.Equal(OrderDomainFault.InvalidTradeIntent, invalidMode.Validate());
        Assert.False(invalidLiquidity.IsValid);
        Assert.Equal(
            VenueCommandFault.InvalidInstruction,
            Venue().Submit(invalidSide, OmsTestData.Causation("invalid-side")).Fault);
    }

    [Fact]
    public void ConfiguredLimitFills_MustHonorSideAwareExactLimit()
    {
        var cases = new[]
        {
            (Instruction: OmsTestData.Instruction(
                "buy-limit",
                orderType: CanonicalOrderType.Limit,
                limitPrice: new ScaledPrice(100, 0)), FillPrice: new ScaledPrice(101, 0)),
            (Instruction: OmsTestData.Instruction(
                "sell-limit",
                target: -2,
                orderType: CanonicalOrderType.Limit,
                limitPrice: new ScaledPrice(100, 0)), FillPrice: new ScaledPrice(99, 0)),
        };

        foreach (var item in cases)
        {
            var plan = new VenueSubmitPlan(
                item.Instruction.Identity.ClientOrderId,
                VenueSubmitOutcome.Accepted,
                [new FillExecution(
                    ScaledQuantity.FromWhole(1),
                    item.FillPrice,
                    ScaledMoney.Zero,
                    LiquidityFlag.Taker)]);

            var result = Venue([plan]).Submit(
                item.Instruction,
                OmsTestData.Causation($"bad-limit-{item.Instruction.Identity.ClientOrderId.Value}"));

            Assert.Equal(VenueCommandStatus.Rejected, result.Status);
            Assert.Equal(VenueCommandFault.InvalidPlan, result.Fault);
            Assert.Empty(result.Events);
        }
    }

    [Fact]
    public void AcceptedSubmit_IsIdempotentlyReplayed_AndChangedRequestConflicts()
    {
        var instruction = OmsTestData.Instruction();
        var venue = Venue();

        var first = venue.Submit(instruction, OmsTestData.Causation("first"));
        var replay = venue.Submit(instruction, OmsTestData.Causation("replay"));
        var changed = instruction with
        {
            Terms = instruction.Terms with { Quantity = ScaledQuantity.FromWhole(3) },
        };
        var conflict = venue.Submit(changed, OmsTestData.Causation("conflict"));

        Assert.Equal(VenueCommandStatus.Accepted, first.Status);
        Assert.Equal(VenueCommandStatus.IdempotentReplay, replay.Status);
        Assert.Equal(first.Events, replay.Events);
        Assert.Equal(VenueCommandStatus.Conflict, conflict.Status);
        Assert.Equal(VenueCommandFault.IdempotencyConflict, conflict.Fault);
        Assert.Equal(instruction.Identity.ClientOrderId, conflict.Order!.Instruction.Identity.ClientOrderId);
    }

    private static DeterministicSimulatedVenue Venue(IEnumerable<VenueSubmitPlan>? plans = null)
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        return new DeterministicSimulatedVenue(clock, plans);
    }
}
