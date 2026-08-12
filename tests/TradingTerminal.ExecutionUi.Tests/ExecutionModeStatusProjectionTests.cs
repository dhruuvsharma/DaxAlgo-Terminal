using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;

namespace TradingTerminal.ExecutionUi.Tests;

public sealed class ExecutionModeStatusProjectionTests
{
    [Fact]
    public void DisconnectedLiveMode_RemainsConservativelyRedUntilAppLifetimeProjectionIsDisposed()
    {
        var adapter = new ModeOnlyAdapter(ExecutionMode.Live);
        var projection = new ExecutionModeStatusProjection([adapter]);

        Assert.True(projection.HasLiveExecution);
        Assert.StartsWith("LIVE", projection.BannerLabel, StringComparison.Ordinal);
        Assert.Equal(1, adapter.SubscriptionCount);

        projection.Dispose();

        Assert.Equal(0, adapter.SubscriptionCount);
    }

    [Fact]
    public void TransientPublisher_UpdatesSharedTruth_AndRemovalRestoresPaper()
    {
        using var projection = new ExecutionModeStatusProjection();
        var changes = new List<string?>();
        projection.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        var publisher = projection.CreatePublisher();

        publisher.Publish(true);

        Assert.True(projection.HasLiveExecution);
        Assert.StartsWith("LIVE", projection.BannerLabel, StringComparison.Ordinal);
        Assert.Contains(nameof(ExecutionModeStatusProjection.HasLiveExecution), changes);

        publisher.Dispose();

        Assert.False(projection.HasLiveExecution);
        Assert.StartsWith("PAPER", projection.BannerLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void PublisherCapacity_IsBoundedAndRefusesAnotherPublisher()
    {
        using var projection = new ExecutionModeStatusProjection();
        var publishers = Enumerable.Range(0, ExecutionModeStatusProjection.MaximumPublishers)
            .Select(_ => projection.CreatePublisher())
            .ToArray();
        try
        {
            Assert.Throws<InvalidOperationException>(() => projection.CreatePublisher());
        }
        finally
        {
            foreach (var publisher in publishers)
                publisher.Dispose();
        }
    }

    private sealed class ModeOnlyAdapter(ExecutionMode mode) : IBrokerExecutionAdapter
    {
        private Action<BrokerAdapterEvent>? _eventReceived;

        internal int SubscriptionCount { get; private set; }

        public string BrokerId => "mode-status-test";

        public ExecutionMode Mode { get; } = mode;

        public BrokerExecutionAccount Account => default;

        public BrokerExecutionSession Session => null!;

        public BrokerExecutionCapabilities Capabilities => null!;

        public event Action<BrokerAdapterEvent>? EventReceived
        {
            add
            {
                _eventReceived += value;
                SubscriptionCount++;
            }
            remove
            {
                _eventReceived -= value;
                SubscriptionCount--;
            }
        }

        public BrokerAdapterCommandResult Submit(BrokerSubmitCommand command) =>
            throw new NotSupportedException();

        public BrokerAdapterCommandResult Cancel(BrokerCancelCommand command) =>
            throw new NotSupportedException();

        public BrokerAdapterCommandResult Replace(BrokerReplaceCommand command) =>
            throw new NotSupportedException();

        public BrokerOrderQueryResult Query(BrokerOrderQuery query) =>
            throw new NotSupportedException();

        public BrokerReconciliationSnapshot CaptureReconciliationSnapshot() =>
            throw new NotSupportedException();
    }
}
