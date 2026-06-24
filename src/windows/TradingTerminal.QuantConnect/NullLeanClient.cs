using TradingTerminal.Core.QuantConnect;

namespace TradingTerminal.QuantConnect;

/// <summary>
/// No-op client used when an engine mode isn't wired yet (currently <see cref="LeanEngineMode.Cloud"/>).
/// Reports unavailable and fails calls cleanly so the UI shows a clear "not wired" status instead of
/// throwing. Swap this slot for a real cloud REST client later.
/// </summary>
public sealed class NullLeanClient : ILeanClient
{
    public LeanEngineMode Mode { get; }

    private readonly string _reason;

    public NullLeanClient(LeanEngineMode mode = LeanEngineMode.Cloud,
        string reason = "QuantConnect cloud mode is not wired yet — use the local LEAN CLI.")
    {
        Mode = mode;
        _reason = reason;
    }

    public Task<LeanAvailability> CheckAvailabilityAsync(CancellationToken ct = default) =>
        Task.FromResult(LeanAvailability.Unavailable(_reason));

    public Task<IReadOnlyList<LeanProject>> ListProjectsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LeanProject>>(Array.Empty<LeanProject>());

    public Task<LeanBacktestResult> RunBacktestAsync(
        LeanBacktestRequest request, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(LeanBacktestResult.Failed(_reason));

    public Task<LeanDataResult> DownloadDataAsync(
        LeanDataDownloadRequest request, IProgress<string>? progress = null, CancellationToken ct = default) =>
        Task.FromResult(LeanDataResult.Failed(_reason));
}
