namespace TradingTerminal.Core.Research;

/// <summary>
/// Lifecycle of a reproduction job, in the order it normally advances. Terminal states are
/// <see cref="Succeeded"/>, <see cref="Failed"/>, and <see cref="Cancelled"/>; everything else is
/// "unfinished" and is requeued by the orchestrator on restart.
/// </summary>
public enum ReproStatus
{
    /// <summary>Accepted and waiting for a sandbox slot.</summary>
    Queued,

    /// <summary>Resolving the paper → repo + environment (sidecar).</summary>
    Resolving,

    /// <summary>Building the sandbox image / installing the resolved environment.</summary>
    Building,

    /// <summary>Running the minimal reproduction inside the sandbox.</summary>
    RunningMinimal,

    /// <summary>Running the full (budget-gated) reproduction inside the sandbox.</summary>
    RunningFull,

    /// <summary>Mapping the reproduced outputs onto InstrumentId-keyed signals.</summary>
    Bridged,

    /// <summary>Completed with a usable result.</summary>
    Succeeded,

    /// <summary>Terminated with an error folded into the result.</summary>
    Failed,

    /// <summary>Cancelled by the user before completion.</summary>
    Cancelled,
}
