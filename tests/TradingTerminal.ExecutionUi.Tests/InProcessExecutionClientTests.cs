using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

[Collection("Execution client")]
public sealed class InProcessExecutionClientTests
{
    [Theory]
    [InlineData(ExecutionTimeRange.SevenDays)]
    [InlineData(ExecutionTimeRange.ThirtyDays)]
    [InlineData(ExecutionTimeRange.NinetyDays)]
    [InlineData(ExecutionTimeRange.YearToDate)]
    public void PortfolioAnalytics_AnswerForEveryRange_EvenWithNoBooks(ExecutionTimeRange range)
    {
        // A portfolio with no books still has to answer for every range. It used to return an analytics
        // model with an EMPTY period list, so Period(range) threw "Sequence contains no matching
        // element" - which took the console view-model's constructor down and left the window silently
        // unopened. Seeded demo books hid it, because the book count was never zero.
        //
        // The console's own test snapshot is hand-built with a single period, so it could never catch
        // this; only the real client can.
        using var client = new InProcessExecutionClient();

        var analytics = client.GetSnapshot().PortfolioAnalytics;

        var period = analytics.Period(range);
        Assert.Equal(range, period.Range);
        Assert.Equal(0m, period.Metrics.NetProfitAndLoss);
    }

    [Fact]
    public void InitialSnapshot_StartsWithNoBooksAndNoFabricatedState()
    {
        using var client = new InProcessExecutionClient();

        var snapshot = client.GetSnapshot();

        // This replaced a test that asserted on two seeded demo books' invented orders, positions,
        // divergences and reconciliation cases. All of that was fabricated by SeedAlpha/SeedBeta and
        // was removed on 2026-08-18; the console starts empty and shows real state or nothing.
        Assert.Empty(snapshot.Books);

        // Adapter cards still describe what is registered - that part was never fabricated.
        Assert.Contains(snapshot.Adapters, adapter =>
            adapter.Id == "ctrader-openapi-demo" && adapter.IsUnavailable);
    }

    [Fact]
    public async Task NewBook_ReconcilesAndKillsCleanlyWithNothingOutstanding()
    {
        using var client = new InProcessExecutionClient();
        var created = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Live Book", "paper", Array.AsReadOnly(["Test strategy"])));
        Assert.True(created.IsSuccess, created.Message);
        var bookId = Assert.Single(client.GetSnapshot().Books).Id;

        var reconciled = await client.ReconcileAsync(bookId);
        Assert.True(reconciled.IsSuccess, reconciled.Message);
        Assert.All(
            Assert.Single(client.GetSnapshot().Books).ReconciliationCases,
            item => Assert.Equal("Resolved", item.Status));

        var killed = await client.KillAsync(bookId);
        Assert.True(killed.IsSuccess, killed.Message);

        var afterKill = Assert.Single(client.GetSnapshot().Books);
        Assert.Equal(0, afterKill.OpenRealPositionCount);
        Assert.True(afterKill.IsIntakePaused);
    }

    // NOTE: the fail-closed "cannot kill before reconciling" gate is NOT covered here any more.
    // The test that covered it leaned on the seeded demo book's fabricated open positions and
    // divergence; a genuinely empty book has nothing outstanding, so kill succeeds immediately and
    // the gate never engages. Covering it honestly means driving real orders through a book, which
    // needs the order-execution work tracked in Pro issue #19. The gate itself is unchanged and is
    // still covered at the OMS level in TradingTerminal.Execution.Tests.

    [Fact]
    public async Task NewBookAffordance_IsConfigurationOnlyAndBounded()
    {
        using var client = new InProcessExecutionClient();

        for (var index = 0; index < 20; index++)
        {
            _ = await client.CreateBookAsync(new ExecutionBookCreateRequest(
                $"Book {index + 4}",
                "paper",
                Array.AsReadOnly(["Test strategy"])));
        }

        var snapshot = client.GetSnapshot();
        Assert.Equal(12, snapshot.Books.Count);
        var created = Assert.Single(snapshot.Books, book => book.Name == "Book 4");
        Assert.True(created.Lease.IsHeld);
        Assert.Equal("paper", created.AdapterId);
        Assert.Empty(created.Orders);
        Assert.Empty(created.LedgerEvents);
    }

    [Fact]
    public async Task NewBook_RequiresUniqueNameAndRegisteredAdapter_ButNotAStrategy()
    {
        using var client = new InProcessExecutionClient();

        // The duplicate has to be a book this test created - there are no seeded books to collide with.
        Assert.True((await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Alpha", "paper", Array.AsReadOnly(["Strategy"])))).IsSuccess);
        var duplicate = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Alpha",
            "paper",
            Array.AsReadOnly(["Strategy"])));
        var unavailable = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Delta",
            "ctrader-openapi-demo",
            Array.AsReadOnly(["Strategy"])));
        // A book with NO strategy is valid on purpose: the catalog is empty on a fresh install, so
        // requiring one made the first book impossible to create.
        var unbound = await client.CreateBookAsync(new ExecutionBookCreateRequest(
            "Delta",
            "paper",
            Array.Empty<string>()));

        Assert.False(duplicate.IsSuccess);
        Assert.False(unavailable.IsSuccess);
        Assert.True(unbound.IsSuccess, unbound.Message);
        Assert.Equal(2, client.GetSnapshot().Books.Count);
    }

    [Fact]
    public async Task Dispose_RejectsFurtherCommandsAndSnapshots()
    {
        var client = new InProcessExecutionClient();

        client.Dispose();

        Assert.Throws<ObjectDisposedException>(client.GetSnapshot);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await client.ConnectAdapterAsync("paper"));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await client.CreateBookAsync(new ExecutionBookCreateRequest(
                "Closed",
                "paper",
                Array.AsReadOnly(["Strategy"]))));
    }
}
