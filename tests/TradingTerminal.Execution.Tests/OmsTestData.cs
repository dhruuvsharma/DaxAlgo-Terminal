using TradingTerminal.Core.Domain;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

internal static class OmsTestData
{
    internal static readonly DateTime TimestampUtc =
        new(2026, 8, 5, 9, 30, 0, DateTimeKind.Utc);

    internal static CanonicalOrderInstruction Instruction(
        string clientOrderId = "client-1",
        long target = 2,
        CanonicalOrderType orderType = CanonicalOrderType.Market,
        CanonicalTimeInForce timeInForce = CanonicalTimeInForce.Day,
        ScaledPrice? limitPrice = null,
        ScaledPrice? stopPrice = null)
    {
        var intent = Intent(target);
        var identity = new OrderIdentity(
            new IntentId($"intent-{clientOrderId}"),
            null,
            new LegId($"leg-{clientOrderId}"),
            new ClientOrderId(clientOrderId),
            null,
            null,
            new CorrelationId($"correlation-{clientOrderId}"),
            new CausationId($"cause-{clientOrderId}"),
            new ExecutionLeaseId("lease-test"),
            new FencingToken(7));
        var terms = new CanonicalOrderTerms(
            target >= 0 ? TradingTerminal.Core.Trading.OrderSide.Buy : TradingTerminal.Core.Trading.OrderSide.Sell,
            orderType,
            timeInForce,
            ScaledQuantity.FromWhole(Math.Abs(target)),
            limitPrice,
            stopPrice);
        return new CanonicalOrderInstruction(identity, intent, terms);
    }

    internal static TradeIntent Intent(long target) =>
        new(
            new InstrumentId(9001),
            TradeIntentQuantityMode.TargetPosition,
            ScaledQuantity.FromWhole(target),
            null,
            null,
            ScaledMoney.Zero,
            "oms-test.strategy",
            501,
            "signal-policy-v1");

    internal static RiskInputSnapshot RiskSnapshot(
        long target = 2,
        long current = 0,
        long referencePrice = 100) =>
        new(
            Intent(target),
            ScaledQuantity.FromWhole(current),
            new ScaledPrice(referencePrice, 0),
            new ScaledRatio(1, 0),
            new ScaledMoney(Math.Abs(current) * referencePrice, 0),
            ScaledMoney.Zero,
            ScaledMoney.Zero,
            new DateOnly(2026, 8, 5));

    internal static RiskEngine RiskEngine(
        long maximumOrderQuantity = 100,
        long maximumOrderNotional = 100_000,
        string policyVersion = "1")
    {
        var limits = new RiskLimits(
            ScaledQuantity.FromWhole(maximumOrderQuantity),
            new ScaledMoney(maximumOrderNotional, 0),
            ScaledQuantity.FromWhole(100),
            new ScaledMoney(1_000_000, 0),
            new ScaledMoney(100_000, 0));
        var fault = RiskPolicy.TryCreate("oms-risk", policyVersion, limits, out var policy);
        if (fault != RiskPolicyFault.None || policy is null)
            throw new InvalidOperationException($"Test risk policy failed: {fault}.");
        return new RiskEngine(policy);
    }

    internal static CausationId Causation(string suffix) => new($"cause-{suffix}");

    internal static DeduplicationKey Dedup(string suffix) => new($"dedup-{suffix}");
}
