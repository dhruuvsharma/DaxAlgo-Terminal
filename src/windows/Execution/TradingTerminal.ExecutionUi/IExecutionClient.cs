namespace TradingTerminal.ExecutionUi;

/// <summary>
/// UI-facing execution read model and operator-command seam. The default registration is the
/// in-process, mode-gated implementation; a named-pipe implementation can replace it later without
/// changing the view-model. The backend alone authorizes and constructs LIVE routes. Intake pause is
/// the local admission flag, while reconciliation and the confirm-gated kill switch remain callable.
/// </summary>
public interface IExecutionClient : IDisposable
{
    event EventHandler? SnapshotInvalidated;

    ExecutionConsoleSnapshot GetSnapshot();

    ValueTask<ExecutionCommandResult> SetIntakePausedAsync(
        string bookId,
        bool paused,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> ReconcileAsync(
        string bookId,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> KillAsync(
        string bookId,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> SetExecutionModeAsync(
        ExecutionModeChangeRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
        string adapterId,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> ConnectAdapterAsync(
        ExecutionAdapterConnectRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> DisconnectAdapterAsync(
        string adapterId,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> CreateBookAsync(
        ExecutionBookCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> SubmitTestOrderAsync(
        ExecutionTestOrderRequest request,
        CancellationToken cancellationToken = default);
}
