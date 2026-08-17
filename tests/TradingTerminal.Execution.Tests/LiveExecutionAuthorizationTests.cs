using TradingTerminal.Execution.Alpaca;
using TradingTerminal.Execution.CTrader;
using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Tests;

public sealed class LiveExecutionAuthorizationTests
{
    [Fact]
    public void BrokerOptions_DefaultToPaper()
    {
        Assert.Equal(ExecutionMode.Paper, new CTraderExecutionOptions().Mode);
        Assert.Equal(ExecutionMode.Paper, new AlpacaExecutionOptions().Mode);
    }

    [Fact]
    public void SimulatedAdapter_IsAlwaysPaper()
    {
        var clock = new SimClock();
        clock.SetTo(OmsTestData.TimestampUtc);
        var adapter = new SimulatedExecutionAdapter(
            new DeterministicSimulatedVenue(clock),
            clock,
            new ControllableAdapterEventScheduler());

        Assert.Equal("simulated", adapter.BrokerId);
        Assert.Equal(ExecutionMode.Paper, adapter.Mode);
    }

    [Fact]
    public void Confirmation_RequiresExactLiveAcknowledgementAndBoundedUtcIdentity()
    {
        var valid = Confirmation();

        Assert.True(valid.IsValid);
        Assert.False((valid with { Acknowledgement = "live" }).IsValid);
        Assert.False((valid with { ConfirmedAtUtc = DateTime.SpecifyKind(valid.ConfirmedAtUtc, DateTimeKind.Local) }).IsValid);
        Assert.False((valid with { ConfirmedBy = new string('x', LiveExecutionConfirmation.MaximumConfirmingIdentityLength + 1) }).IsValid);
    }

    [Fact]
    public void InMemoryStore_IsExactBoundedAndRevocable()
    {
        var store = new InMemoryLiveExecutionConfirmationStore();
        var confirmation = Confirmation();

        store.Save(confirmation);

        Assert.Equal(confirmation, store.Read(confirmation.BrokerId, confirmation.AccountId));
        Assert.Null(store.Read(confirmation.BrokerId, "different-account"));
        Assert.True(store.Remove(confirmation.BrokerId, confirmation.AccountId));
        Assert.False(store.Remove(confirmation.BrokerId, confirmation.AccountId));
        Assert.Null(store.Read(confirmation.BrokerId, confirmation.AccountId));
        Assert.Throws<ArgumentException>(() => store.Save(confirmation with { Acknowledgement = "Live" }));
    }

    [Fact]
    public void DpapiStore_PersistsExactConfirmationAndRevocationForCurrentUser()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "DaxAlgoExecutionTests",
            "live-confirmations-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "confirmations.dpapi");
        Directory.CreateDirectory(directory);
        try
        {
            var confirmation = Confirmation();
            var first = new DpapiLiveExecutionConfirmationStore(path);
            first.Save(confirmation);

            var reopened = new DpapiLiveExecutionConfirmationStore(path);
            Assert.Equal(confirmation, reopened.Read(confirmation.BrokerId, confirmation.AccountId));
            Assert.True(reopened.Remove(confirmation.BrokerId, confirmation.AccountId));

            var afterRevocation = new DpapiLiveExecutionConfirmationStore(path);
            Assert.Null(afterRevocation.Read(confirmation.BrokerId, confirmation.AccountId));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static LiveExecutionConfirmation Confirmation() => new(
        CTraderExecutionOptions.BrokerId,
        "700001",
        LiveExecutionConfirmation.RequiredAcknowledgement,
        OmsTestData.TimestampUtc,
        "test-owner");
}
