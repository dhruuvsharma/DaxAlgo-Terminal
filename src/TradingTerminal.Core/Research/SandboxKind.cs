namespace TradingTerminal.Core.Research;

/// <summary>
/// The isolation backend used to run untrusted paper code. Selected by <c>SandboxOptions.Kind</c>.
/// Docker is the only fully-implemented backend today; WSL2 is a stub and RemoteWorker is reserved
/// for an off-machine pool (the orchestrator/runner seams already abstract local-vs-remote).
/// </summary>
public enum SandboxKind
{
    /// <summary>A disposable Docker container (deny-by-default isolation).</summary>
    Docker,

    /// <summary>A WSL2 distro / lightweight VM (not yet implemented).</summary>
    Wsl2,

    /// <summary>An off-machine worker pool reached over HTTP (future).</summary>
    RemoteWorker,
}
