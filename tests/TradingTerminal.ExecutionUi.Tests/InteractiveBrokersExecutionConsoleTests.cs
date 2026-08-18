using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.UI;

namespace TradingTerminal.ExecutionUi.Tests;

[Collection("Execution client")]
public sealed class InteractiveBrokersExecutionConsoleTests
{
    [Fact]
    public void DefaultClient_DoesNotSurfaceInteractiveBrokersWithoutOptIn()
    {
        using var client = new InProcessExecutionClient();

        Assert.DoesNotContain(
            client.GetSnapshot().Adapters,
            adapter => adapter.Id.Contains("interactive-brokers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LiveMode_MapsTwsPortAndForwardsExactInteractiveBrokersIdentity()
    {
        var client = new FakeExecutionClient(Snapshot(Adapter()));
        var confirmation = new FakeConfirmationService();

        await WithViewModelAsync(client, confirmation, async viewModel =>
        {
            var loginForm = Assert.IsType<IbLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
            loginForm.Host = "127.0.0.1";
            loginForm.Port = 7497;
            loginForm.ClientId = 2;
            Assert.Equal("DU123456", viewModel.InteractiveBrokersAccountId);

            await viewModel.SetExecutionModeCommand.ExecuteAsync(viewModel.Adapters.Single());

            var request = Assert.Single(client.ModeChanges);
            Assert.Equal("interactive-brokers-paper", request.AdapterId);
            Assert.Equal("DU123456", request.AccountId);
            Assert.Equal(ExecutionMode.Live, request.Mode);
            Assert.Equal("LIVE", request.TypedConfirmation);
            Assert.Equal("127.0.0.1", request.Host);
            Assert.Equal(7496, request.Port);
            Assert.Equal(2, request.ClientId);
            Assert.Equal(7496, loginForm.Port);
        });
    }

    [Fact]
    public async Task Connect_ForwardsInMemoryConnectionSettingsWithoutCredentials()
    {
        var client = new FakeExecutionClient(Snapshot(Adapter()));
        var confirmation = new FakeConfirmationService();

        await WithViewModelAsync(client, confirmation, async viewModel =>
        {
            var loginForm = Assert.IsType<IbLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
            loginForm.Host = "127.0.0.1";
            loginForm.Port = 4002;
            loginForm.ClientId = 17;
            viewModel.InteractiveBrokersAccountId = "DU654321";

            await viewModel.ConnectAdapterCommand.ExecuteAsync("interactive-brokers-paper");

            var request = Assert.Single(client.ConnectRequests);
            Assert.Equal("interactive-brokers-paper", request.AdapterId);
            Assert.Equal("127.0.0.1", request.Host);
            Assert.Equal(4002, request.Port);
            Assert.Equal(17, request.ClientId);
            Assert.Equal("DU654321", request.AccountId);
            Assert.Equal(string.Empty, request.KeyId);
            Assert.Equal(string.Empty, request.SecretKey);
        });
    }

    private static async Task WithViewModelAsync(
        FakeExecutionClient client,
        FakeConfirmationService confirmation,
        Func<ExecutionConsoleViewModel, Task> test)
    {
        var originalTimerFactory = UiThread.CreateRenderTimer;
        var timer = new TrackingDisposable();
        UiThread.CreateRenderTimer = (_, _) => timer;
        try
        {
            var viewModel = new ExecutionConsoleViewModel(
                client,
                confirmation,
                TestBrokerLoginFormFactory.InteractiveBrokers());
            try
            {
                await test(viewModel);
            }
            finally
            {
                viewModel.Dispose();
            }
        }
        finally
        {
            UiThread.CreateRenderTimer = originalTimerFactory;
        }

        Assert.True(timer.IsDisposed);
        // The engine is app-lifetime and shared with the header books chip, so closing the console
        // must LEAVE it running - disposing it here is what used to delete every book when the
        // window was closed.
        Assert.False(client.IsDisposed);
    }

    private static ExecutionAdapterReadModel Adapter() => new(
        "interactive-brokers-paper",
        "Interactive Brokers PAPER",
        "Account DU123456",
        ExecutionConnectionStatus.NotConfigured,
        "Not connected",
        "Configure the local TWS or IB Gateway endpoint.",
        ExecutionTone.Neutral,
        IsRegistered: true,
        CanConnect: true,
        CanDisconnect: false,
        CanCreateBook: false,
        IsDemoOnly: false,
        "Local socket settings",
        "TWS or IB Gateway",
        Array.Empty<string>(),
        EnvironmentLabel: "PAPER",
        Mode: ExecutionMode.Paper,
        BrokerAccountId: "DU123456",
        LoginBroker: BrokerKind.InteractiveBrokers);

    private static ExecutionConsoleSnapshot Snapshot(ExecutionAdapterReadModel adapter) => new(
        Array.AsReadOnly([adapter]),
        Array.Empty<ExecutionBookReadModel>(),
        new ExecutionPortfolioAnalyticsReadModel(
            Array.AsReadOnly(
            [
                new ExecutionPeriodAnalyticsReadModel(
                    ExecutionTimeRange.ThirtyDays,
                    "30D",
                    new ExecutionMetricResult(0m, 0m, 0m, 0d, 0m, 0m, 0, 0m, 0, 0),
                    Array.Empty<ExecutionEquityPointReadModel>(),
                    Array.Empty<ExecutionDailyPnlPointReadModel>()),
            ]),
            Array.Empty<ExecutionExposureReadModel>(),
            new ExecutionQualityReadModel(0, 0, 0, 0, 0, 0, 0, 0d, 0, 0d)),
        DateTime.UtcNow,
        null);

    private sealed class FakeConfirmationService : IExecutionConfirmationService
    {
        public ValueTask<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<ExecutionTypedConfirmationResult> ConfirmTypedAsync(
            string title,
            string message,
            string requiredText,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ExecutionTypedConfirmationResult(true, "LIVE"));
    }

    private sealed class FakeExecutionClient(ExecutionConsoleSnapshot snapshot) : IExecutionClient
    {
        internal List<ExecutionModeChangeRequest> ModeChanges { get; } = [];

        internal List<ExecutionAdapterConnectRequest> ConnectRequests { get; } = [];

        internal bool IsDisposed { get; private set; }

        public event EventHandler? SnapshotInvalidated;

        public ExecutionConsoleSnapshot GetSnapshot() => snapshot;

        public ValueTask<ExecutionCommandResult> SetIntakePausedAsync(
            string bookId,
            bool paused,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("intake changed"));

        public ValueTask<ExecutionCommandResult> ReconcileAsync(
            string bookId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("reconciled"));

        public ValueTask<ExecutionCommandResult> KillAsync(
            string bookId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("killed"));

        public ValueTask<ExecutionCommandResult> SetExecutionModeAsync(
            ExecutionModeChangeRequest request,
            CancellationToken cancellationToken = default)
        {
            ModeChanges.Add(request);
            SnapshotInvalidated?.Invoke(this, EventArgs.Empty);
            return ValueTask.FromResult(ExecutionCommandResult.Success("mode changed"));
        }

        public ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
            string adapterId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Failure("structured request required"));

        public ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
            ExecutionAdapterConnectRequest request,
            CancellationToken cancellationToken = default)
        {
            ConnectRequests.Add(request);
            return ValueTask.FromResult(ExecutionCommandResult.Success("connected"));
        }

        public ValueTask<ExecutionCommandResult> DisconnectAdapterAsync(
            string adapterId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("disconnected"));

        public ValueTask<ExecutionCommandResult> CreateBookAsync(
            ExecutionBookCreateRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("book created"));

        public ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(
            ExecutionManualOrderRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("submitted"));

        public void Dispose() => IsDisposed = true;
    }

    private sealed class TrackingDisposable : IDisposable
    {
        internal bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
