using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

[Collection("Execution client")]
public sealed class InProcessExecutionClientTests
{
    [Fact]
    public void InitialSnapshot_UsesSimulationRuntimeAndBoundedReadModels()
    {
        using var client = new InProcessExecutionClient();

        var snapshot = client.GetSnapshot();
        var alpha = Assert.Single(snapshot.Books, book => book.Id == "alpha");

        Assert.Contains("Simulated", alpha.ServiceStatus, StringComparison.Ordinal);
        Assert.Equal("simulated", alpha.AdapterId);
        Assert.Contains(snapshot.Adapters, adapter => adapter.Id == "simulated" && adapter.IsConnected);
        Assert.Contains(snapshot.Adapters, adapter =>
            adapter.Id == "ctrader-openapi-demo" && adapter.IsUnavailable);
        Assert.True(alpha.Lease.IsHeld);
        Assert.False(alpha.AdmissionOpen);
        Assert.True(alpha.OpenRealPositionCount > 0);
        Assert.Contains(alpha.Positions, position => position.HasDivergence);
        Assert.Contains(alpha.Orders, order => order.State == "Draft");
        Assert.Contains(alpha.Orders, order => order.State == "Validated");
        Assert.Contains(alpha.Orders, order => order.State == "Armed");
        Assert.Contains(alpha.Orders, order => order.State == "Working");
        Assert.Contains(alpha.Orders, order => order.State == "Filled");
        Assert.Contains(alpha.Orders, order => order.State == "Rejected");
        Assert.Contains(alpha.ReconciliationCases, item => item.Type == "Quantity Mismatch");
        Assert.Contains(alpha.ReconciliationCases, item => item.Type == "Broker Missing");
        Assert.InRange(alpha.LedgerEvents.Count, 1, 96);
        Assert.InRange(alpha.History.Count, 1, 500);
        Assert.All(alpha.LedgerEvents, item => Assert.EndsWith("…", item.Hash, StringComparison.Ordinal));
        var analytics = alpha.Analytics.Period(ExecutionTimeRange.ThirtyDays);
        Assert.InRange(analytics.EquitySeries.Count, 1, 370);
        Assert.InRange(analytics.DailyProfitAndLossSeries.Count, 1, 370);
        Assert.True(analytics.Metrics.TradeCount > 0);
        Assert.NotEmpty(alpha.History);
    }

    [Fact]
    public async Task ReconcileThenKill_RespectsFailClosedGateAndUsesSimulatedPositions()
    {
        using var client = new InProcessExecutionClient();

        var blocked = await client.KillAsync("alpha");
        Assert.False(blocked.IsSuccess);
        Assert.Contains("Reconcile", blocked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Assert.Single(client.GetSnapshot().Books, book => book.Id == "alpha").IsIntakePaused);

        var reconciled = await client.ReconcileAsync("alpha");
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        var afterReconcile = Assert.Single(client.GetSnapshot().Books, book => book.Id == "alpha");
        Assert.False(afterReconcile.AdmissionOpen);
        Assert.True(afterReconcile.IsIntakePaused);
        Assert.All(afterReconcile.ReconciliationCases, item => Assert.Equal("Resolved", item.Status));

        var resumed = await client.SetIntakePausedAsync("alpha", paused: false);
        Assert.True(resumed.IsSuccess, resumed.Message);
        var killed = await client.KillAsync("alpha");
        Assert.True(killed.IsSuccess, killed.Message);
        Assert.Contains("Verified", killed.Message, StringComparison.Ordinal);
        var afterKill = Assert.Single(client.GetSnapshot().Books, book => book.Id == "alpha");
        Assert.Equal(0, afterKill.OpenRealPositionCount);
        Assert.True(afterKill.IsIntakePaused);
        Assert.All(afterKill.Positions, position => Assert.Equal("0", position.RealQuantity));
        Assert.InRange(afterKill.LedgerEvents.Count, 1, 96);
    }

    [Fact]
    public async Task NewBookAffordance_IsConfigurationOnlyAndBounded()
    {
        using var client = new InProcessExecutionClient();

        for (var index = 0; index < 20; index++)
        {
            _ = await client.CreateBookAsync(new ExecutionBookCreateRequest(
                $"Book {index + 4}",
                "simulated",
                Array.AsReadOnly(["Test strategy"])));
        }

        var snapshot = client.GetSnapshot();
        Assert.Equal(12, snapshot.Books.Count);
        var created = Assert.Single(snapshot.Books, book => book.Name == "Book 4");
        Assert.True(created.Lease.IsHeld);
        Assert.Equal("simulated", created.AdapterId);
        Assert.Empty(created.Orders);
        Assert.Empty(created.LedgerEvents);
    }

    [Fact]
    public async Task NewBook_RequiresUniqueNameRegisteredAdapterAndStrategyBinding()
    {
        using var client = new InProcessExecutionClient();

        var duplicate = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Alpha",
            "simulated",
            Array.AsReadOnly(["Strategy"])));
        var unavailable = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Delta",
            "ctrader-openapi-demo",
            Array.AsReadOnly(["Strategy"])));
        var unbound = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Delta",
            "simulated",
            Array.Empty<string>()));

        Assert.False(duplicate.IsSuccess);
        Assert.False(unavailable.IsSuccess);
        Assert.False(unbound.IsSuccess);
        Assert.Equal(3, client.GetSnapshot().Books.Count);
    }

    [Fact]
    public async Task Dispose_RejectsFurtherCommandsAndSnapshots()
    {
        var client = new InProcessExecutionClient();

        client.Dispose();

        Assert.Throws<ObjectDisposedException>(client.GetSnapshot);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await client.ConnectAdapterAsync("simulated"));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await client.CreateBookAsync(new ExecutionBookCreateRequest(
                "Closed",
                "simulated",
                Array.AsReadOnly(["Strategy"]))));
    }
}
