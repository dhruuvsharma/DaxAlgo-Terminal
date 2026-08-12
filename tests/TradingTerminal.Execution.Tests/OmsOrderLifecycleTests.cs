using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class OmsOrderLifecycleTests
{
    [Fact]
    public void EveryDeclaredTransition_IsLegal()
    {
        foreach (var transition in OrderLifecycle.LegalTransitions)
            Assert.True(OrderLifecycle.CanTransition(transition.From, transition.To));
    }

    [Fact]
    public void EveryDeclaredTransition_HasAtLeastOnePermittedSemanticEvent()
    {
        var eventKinds = Enum.GetValues<OrderEventKind>();
        foreach (var transition in OrderLifecycle.LegalTransitions)
        {
            Assert.Contains(
                eventKinds,
                kind => OrderLifecycle.CanApplyEvent(kind, transition.From, transition.To));
        }
    }

    [Fact]
    public void EveryUndeclaredTransition_IsRejected()
    {
        var legal = OrderLifecycle.LegalTransitions
            .Select(static transition => (transition.From, transition.To))
            .ToHashSet();

        foreach (var from in Enum.GetValues<OrderLifecycleState>())
        {
            foreach (var to in Enum.GetValues<OrderLifecycleState>())
            {
                Assert.Equal(legal.Contains((from, to)), OrderLifecycle.CanTransition(from, to));
            }
        }
    }

    [Fact]
    public void UnknownAndReconciling_BlockRetry_AndAreNotRejection()
    {
        Assert.True(OrderLifecycle.BlocksRetry(OrderLifecycleState.Unknown));
        Assert.True(OrderLifecycle.BlocksRetry(OrderLifecycleState.Reconciling));
        Assert.False(OrderLifecycle.BlocksRetry(OrderLifecycleState.Rejected));
        Assert.NotEqual(OrderLifecycleState.Rejected, OrderLifecycleState.Unknown);
    }

    [Fact]
    public void FinalEconomicOutcomesAndReconciled_AreTerminal()
    {
        foreach (var state in Enum.GetValues<OrderLifecycleState>())
        {
            var expected = state is OrderLifecycleState.Filled or
                OrderLifecycleState.Cancelled or
                OrderLifecycleState.Rejected or
                OrderLifecycleState.Expired or
                OrderLifecycleState.Reconciled;
            Assert.Equal(expected, OrderLifecycle.IsTerminal(state));
        }
    }
}
