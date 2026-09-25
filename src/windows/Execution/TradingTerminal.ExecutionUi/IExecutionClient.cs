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

    /// <summary>
    /// Reads the account a routed broker's LIVE credentials reach, enabling nothing, so the typed LIVE
    /// confirmation can name the real account. Default: no routed brokers.
    /// </summary>
    ValueTask<ExecutionLiveAccountProbe> ProbeLiveAccountAsync(string adapterId, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(new ExecutionLiveAccountProbe(false, string.Empty, "This execution client has no routed brokers."));

    /// <summary>
    /// Recreates the books remembered from the last run.
    ///
    /// <para>Default is to restore nothing, so a host with no book store — and every test double —
    /// behaves exactly as before.</para>
    /// </summary>
    ValueTask<ExecutionCommandResult> RestoreBooksAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(ExecutionCommandResult.Success("No books to restore."));

    ValueTask<ExecutionCommandResult> CreateBookAsync(
        ExecutionBookCreateRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<ExecutionCommandResult> SubmitManualOrderAsync(
        ExecutionManualOrderRequest request,
        CancellationToken cancellationToken = default);
}
