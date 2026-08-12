using TradingTerminal.App.Login.Forms;
using TradingTerminal.Core.Brokers;
using TradingTerminal.Execution.Oms;
using TradingTerminal.ExecutionUi;
using TradingTerminal.UI;

namespace TradingTerminal.ExecutionUi.Tests;

[Collection("Execution client")]
public sealed class ExecutionConsoleViewModelTests
{
    [Fact]
    public void ReadModels_DefaultToPaper_AndSnapshotDerivesLiveFromEveryAdapter()
    {
        var paper = Adapter(ExecutionMode.Paper);
        var live = Adapter(ExecutionMode.Live);

        Assert.False(paper.IsLive);
        Assert.Equal("PAPER", paper.ModeLabel);
        Assert.False(Snapshot(paper).HasLiveExecution);
        Assert.True(live.IsLive);
        Assert.Equal("LIVE", live.ModeLabel);
        Assert.True(Snapshot(live).HasLiveExecution);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "live")]
    [InlineData(true, "LIVE ")]
    public async Task LiveMode_CancelOrNonOrdinalAcknowledgement_NeverCallsBackend(
        bool confirmed,
        string enteredText)
    {
        var client = new FakeExecutionClient(Snapshot(Adapter(ExecutionMode.Paper)));
        var confirmation = new FakeConfirmationService
        {
            TypedResult = new ExecutionTypedConfirmationResult(confirmed, enteredText),
        };

        await WithViewModelAsync(client, confirmation, async viewModel =>
        {
            var loginForm = Assert.IsType<AlpacaLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
            viewModel.AlpacaLiveAccountId = "LIVE-ACCOUNT-42";
            loginForm.ApiKey = "live-key";
            loginForm.ApiSecret = "live-secret";

            await viewModel.SetExecutionModeCommand.ExecuteAsync(viewModel.Adapters.Single());

            Assert.Empty(client.ModeChanges);
            Assert.False(viewModel.HasLiveExecution);
            Assert.Contains("remains PAPER", viewModel.OperationMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LiveMode_AlpacaWithoutExpectedLiveAccountId_DoesNotAskOrCallBackend()
    {
        var client = new FakeExecutionClient(Snapshot(Adapter(ExecutionMode.Paper)));
        var confirmation = new FakeConfirmationService { ThrowOnTypedConfirmation = true };

        await WithViewModelAsync(client, confirmation, async viewModel =>
        {
            var loginForm = Assert.IsType<AlpacaLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
            loginForm.ApiKey = "live-key";
            loginForm.ApiSecret = "live-secret";

            await viewModel.SetExecutionModeCommand.ExecuteAsync(viewModel.Adapters.Single());

            Assert.Empty(client.ModeChanges);
            Assert.Equal(0, confirmation.TypedCallCount);
            Assert.Contains("expected Alpaca LIVE account ID", viewModel.OperationMessage, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task LiveMode_ExactAcknowledgement_ForwardsExpectedAccountAndCredentials_WithoutLocalMutation()
    {
        var client = new FakeExecutionClient(Snapshot(Adapter(ExecutionMode.Paper)))
        {
            SetModeResult = ExecutionCommandResult.Failure("authorization refused safely"),
        };
        var confirmation = new FakeConfirmationService
        {
            TypedResult = new ExecutionTypedConfirmationResult(true, "LIVE"),
        };

        await WithViewModelAsync(client, confirmation, async viewModel =>
        {
            var loginForm = Assert.IsType<AlpacaLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
            viewModel.AlpacaLiveAccountId = "LIVE-ACCOUNT-42";
            loginForm.ApiKey = "live-key";
            loginForm.ApiSecret = "live-secret";

            await viewModel.SetExecutionModeCommand.ExecuteAsync(viewModel.Adapters.Single());

            var request = Assert.Single(client.ModeChanges);
            Assert.Equal("alpaca", request.AdapterId);
            Assert.Equal("LIVE-ACCOUNT-42", request.AccountId);
            Assert.Equal(ExecutionMode.Live, request.Mode);
            Assert.Equal("LIVE", request.TypedConfirmation);
            Assert.Equal("live-key", request.KeyId);
            Assert.Equal("live-secret", request.SecretKey);
            Assert.Equal("alpaca|LIVE-ACCOUNT-42|Live", request.ToString());
            Assert.DoesNotContain("live-secret", request.ToString(), StringComparison.Ordinal);
            Assert.False(viewModel.HasLiveExecution);
            Assert.Equal("authorization refused safely", viewModel.OperationMessage);
            Assert.Equal(string.Empty, loginForm.ApiSecret);
        });
    }

    [Fact]
    public async Task Connect_CTraderLoginCredentialsUseSeparateExecutionAccountBinding()
    {
        var client = new FakeExecutionClient(Snapshot(CTraderAdapter()));
        var confirmation = new FakeConfirmationService();

        await WithViewModelAsync(
            client,
            confirmation,
            async viewModel =>
            {
                var loginForm = Assert.IsType<CTraderLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
                loginForm.Username = "not-an-account-id";
                loginForm.ClientId = "oauth-client";
                loginForm.ClientSecret = "oauth-secret";
                loginForm.AccessToken = "oauth-token";
                loginForm.AccountId = 999999;
                viewModel.CTraderExecutionAccountId = "123456";

                await viewModel.ConnectAdapterCommand.ExecuteAsync("ctrader-openapi|42");

                var request = Assert.Single(client.ConnectRequests);
                Assert.Equal("123456", request.AccountId);
                Assert.NotEqual(loginForm.Username, request.AccountId);
                Assert.NotEqual("999999", request.AccountId);
                Assert.Equal("oauth-client", request.OAuthClientId);
                Assert.Equal("oauth-secret", request.OAuthClientSecret);
                Assert.Equal("oauth-token", request.OAuthAccessToken);
                Assert.DoesNotContain("oauth-secret", request.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("oauth-token", request.ToString(), StringComparison.Ordinal);
                Assert.Equal(string.Empty, loginForm.ClientSecret);
                Assert.Equal(string.Empty, loginForm.AccessToken);
            },
            loginFormFactory: TestBrokerLoginFormFactory.CTrader());
    }

    [Fact]
    public async Task Dispose_DisposesHeldLoginForm()
    {
        var form = new TrackingBrokerLoginForm(BrokerKind.Alpaca);
        var client = new FakeExecutionClient(Snapshot(Adapter(ExecutionMode.Paper)));

        await WithViewModelAsync(
            client,
            new FakeConfirmationService(),
            _ => Task.CompletedTask,
            loginFormFactory: new TestBrokerLoginFormFactory(form));

        Assert.True(form.IsDisposed);
    }

    [Fact]
    public async Task PaperMode_DoesNotRequestTypedConfirmation()
    {
        var client = new FakeExecutionClient(Snapshot(Adapter(ExecutionMode.Live)));
        var confirmation = new FakeConfirmationService { ThrowOnTypedConfirmation = true };

        await WithViewModelAsync(client, confirmation, async viewModel =>
        {
            var loginForm = Assert.IsType<AlpacaLoginFormViewModel>(viewModel.Adapters.Single().LoginForm);
            loginForm.ApiKey = "paper-key";
            loginForm.ApiSecret = "paper-secret";
            Assert.True(viewModel.HasLiveExecution);

            await viewModel.SetExecutionModeCommand.ExecuteAsync(viewModel.Adapters.Single());

            var request = Assert.Single(client.ModeChanges);
            Assert.Equal(ExecutionMode.Paper, request.Mode);
            Assert.Equal(string.Empty, request.TypedConfirmation);
            Assert.Equal(0, confirmation.TypedCallCount);
        });
    }

    [Fact]
    public async Task SharedModeProjection_DrivesConsoleBannerWithoutSnapshotDivergence()
    {
        var client = new FakeExecutionClient(Snapshot(Adapter(ExecutionMode.Paper)));
        var confirmation = new FakeConfirmationService();
        using var projection = new ExecutionModeStatusProjection();
        using var publisher = projection.CreatePublisher();
        publisher.Publish(true);

        await WithViewModelAsync(client, confirmation, viewModel =>
        {
            Assert.True(viewModel.HasLiveExecution);
            Assert.StartsWith("LIVE", viewModel.ExecutionModeBannerLabel, StringComparison.Ordinal);

            publisher.Publish(false);

            Assert.False(viewModel.HasLiveExecution);
            Assert.StartsWith("PAPER", viewModel.ExecutionModeBannerLabel, StringComparison.Ordinal);
            return Task.CompletedTask;
        }, projection);
    }

    private static async Task WithViewModelAsync(
        FakeExecutionClient client,
        FakeConfirmationService confirmation,
        Func<ExecutionConsoleViewModel, Task> test,
        ExecutionModeStatusProjection? executionModeStatus = null,
        IBrokerLoginFormFactory? loginFormFactory = null)
    {
        var originalTimerFactory = UiThread.CreateRenderTimer;
        var timer = new TrackingDisposable();
        UiThread.CreateRenderTimer = (_, _) => timer;
        try
        {
            var viewModel = new ExecutionConsoleViewModel(
                client,
                confirmation,
                loginFormFactory ?? TestBrokerLoginFormFactory.Alpaca(),
                executionModeStatus);
            try
            {
                await test(viewModel);
            }
            finally
            {
                viewModel.Dispose();
            }

            Assert.True(timer.IsDisposed);
            Assert.True(client.IsDisposed);
        }
        finally
        {
            UiThread.CreateRenderTimer = originalTimerFactory;
        }
    }

    private static ExecutionAdapterReadModel Adapter(ExecutionMode mode) => new(
        "alpaca",
        "Alpaca",
        "Account LIVE-ACCOUNT-42",
        ExecutionConnectionStatus.NotConfigured,
        "Not connected",
        "Mock transport only.",
        ExecutionTone.Neutral,
        IsRegistered: true,
        CanConnect: true,
        CanDisconnect: false,
        CanCreateBook: true,
        IsDemoOnly: false,
        "Runtime credentials",
        "Test credentials",
        Array.Empty<string>(),
        EnvironmentLabel: mode == ExecutionMode.Live ? "LIVE" : "PAPER",
        Mode: mode,
        BrokerAccountId: "LIVE-ACCOUNT-42",
        LoginBroker: BrokerKind.Alpaca);

    private static ExecutionAdapterReadModel CTraderAdapter() => new(
        "ctrader-openapi|42",
        "cTrader DEMO",
        "42",
        ExecutionConnectionStatus.NotConfigured,
        "Not connected",
        "Mock transport only.",
        ExecutionTone.Neutral,
        IsRegistered: true,
        CanConnect: true,
        CanDisconnect: false,
        CanCreateBook: false,
        IsDemoOnly: true,
        "Shared Login credentials",
        "Test credentials",
        Array.Empty<string>(),
        EnvironmentLabel: "DEMO",
        Mode: ExecutionMode.Paper,
        BrokerAccountId: "42",
        LoginBroker: BrokerKind.CTrader);

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
        internal ExecutionTypedConfirmationResult TypedResult { get; init; } =
            ExecutionTypedConfirmationResult.Cancelled;

        internal bool ThrowOnTypedConfirmation { get; init; }

        internal int TypedCallCount { get; private set; }

        public ValueTask<bool> ConfirmAsync(
            string title,
            string message,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(true);

        public ValueTask<ExecutionTypedConfirmationResult> ConfirmTypedAsync(
            string title,
            string message,
            string requiredText,
            CancellationToken cancellationToken = default)
        {
            if (ThrowOnTypedConfirmation)
                throw new InvalidOperationException("Typed confirmation must not be requested.");

            TypedCallCount++;
            Assert.Equal("LIVE", requiredText);
            return ValueTask.FromResult(TypedResult);
        }
    }

    private sealed class FakeExecutionClient(ExecutionConsoleSnapshot snapshot) : IExecutionClient
    {
        internal List<ExecutionModeChangeRequest> ModeChanges { get; } = [];

        internal List<ExecutionAdapterConnectRequest> ConnectRequests { get; } = [];

        internal ExecutionCommandResult SetModeResult { get; init; } =
            ExecutionCommandResult.Success("mode changed");

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
            return ValueTask.FromResult(SetModeResult);
        }

        public ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
            string adapterId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ExecutionCommandResult.Success("connected"));

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

        public ValueTask<ExecutionCommandResult> SubmitTestOrderAsync(
            ExecutionTestOrderRequest request,
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
