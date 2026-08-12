using TradingTerminal.Execution.Oms;

namespace TradingTerminal.Execution.Service;

/// <summary>Versioned local IPC contract shared by the desktop control plane and execution service.</summary>
public static class ExecutionServiceProtocol
{
    /// <summary>The only protocol version accepted by this slice.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Maximum bounded request identity length.</summary>
    public const int MaximumRequestIdLength = 128;
}

/// <summary>Commands admitted by the local execution-service contract.</summary>
public enum ExecutionServiceRequestKind : byte
{
    /// <summary>Reads the current service account and lease generation.</summary>
    Status = 0,

    /// <summary>Creates, validates, prepares, arms, and submits one canonical instruction.</summary>
    Submit = 1,

    /// <summary>Requests cancellation of one existing order.</summary>
    Cancel = 2,

    /// <summary>Requests an exact, freshly risk-validated replacement.</summary>
    Replace = 3,

    /// <summary>Runs one explicit account reconciliation cycle.</summary>
    Reconcile = 4,

    /// <summary>Reads durable ledger events after an exclusive outbox cursor.</summary>
    Resync = 5,
}

/// <summary>One submission payload; all economic values remain exact coefficient/scale values.</summary>
public sealed record ExecutionSubmitRequest(
    CanonicalOrderInstruction Instruction,
    RiskInputSnapshot RiskInput);

/// <summary>One cancellation payload.</summary>
public sealed record ExecutionCancelRequest(ClientOrderId ClientOrderId);

/// <summary>One exact replacement payload.</summary>
public sealed record ExecutionReplaceRequest(
    ClientOrderId ClientOrderId,
    CanonicalOrderTerms Terms,
    RiskInputSnapshot RiskInput);

/// <summary>
/// One authenticated control-plane request. Mutating requests must present the current lease id and
/// fencing token; read-only status/resync requests deliberately do not require writer authority.
/// </summary>
public sealed record ExecutionServiceRequest(
    int ProtocolVersion,
    string RequestId,
    ExecutionServiceRequestKind Kind,
    BrokerExecutionAccount Account,
    ExecutionLeaseId ExecutionLeaseId,
    FencingToken FencingToken,
    long AfterOutboxSequence = 0,
    ExecutionSubmitRequest? Submit = null,
    ExecutionCancelRequest? Cancel = null,
    ExecutionReplaceRequest? Replace = null,
    ReconciliationTrigger? ReconciliationTrigger = null)
{
    /// <summary>Gets whether the common envelope is structurally valid.</summary>
    public bool HasValidEnvelope =>
        ProtocolVersion > 0 &&
        !string.IsNullOrWhiteSpace(RequestId) &&
        RequestId.Length <= ExecutionServiceProtocol.MaximumRequestIdLength &&
        Enum.IsDefined(Kind) &&
        Account.IsValid &&
        AfterOutboxSequence >= 0;
}

/// <summary>Stable service-layer failure categories returned without throwing across IPC.</summary>
public enum ExecutionServiceFault : byte
{
    /// <summary>The request completed successfully.</summary>
    None = 0,

    /// <summary>The request envelope or kind-specific payload is invalid.</summary>
    InvalidRequest = 1,

    /// <summary>The authenticated request repeats a protocol version the service does not support.</summary>
    ProtocolVersionMismatch = 2,

    /// <summary>The request names a different simulated execution account.</summary>
    InvalidAccount = 3,

    /// <summary>No active same-machine writer lease is available.</summary>
    LeaseUnavailable = 4,

    /// <summary>The service instance has lost its writer lease.</summary>
    LeaseLost = 5,

    /// <summary>The request carries an older or otherwise non-current fencing token.</summary>
    StaleFencingToken = 6,

    /// <summary>The durable lease token could not be verified.</summary>
    LeasePersistenceFailure = 7,

    /// <summary>The OMS rejected the requested state transition.</summary>
    OmsRejected = 8,

    /// <summary>The simulation adapter/coordinator rejected the command.</summary>
    AdapterRejected = 9,

    /// <summary>The reconciliation cycle failed closed.</summary>
    ReconciliationFailed = 10,

    /// <summary>An exception was contained at the service boundary.</summary>
    InternalFailure = 11,
}

/// <summary>One request-correlated service result followed by exactly <see cref="EventCount"/> events.</summary>
public sealed record ExecutionServiceResponse(
    int ProtocolVersion,
    string RequestId,
    ExecutionServiceFault Fault,
    BrokerExecutionAccount Account,
    ExecutionLeaseId ExecutionLeaseId,
    FencingToken FencingToken,
    OrderLifecycleState? State,
    long LastOutboxSequence,
    int EventCount,
    string? Reason = null)
{
    /// <summary>Gets whether the service accepted and completed the request.</summary>
    public bool IsSuccess => Fault == ExecutionServiceFault.None;
}

/// <summary>One durable lifecycle/fill/ledger fact streamed after a request response.</summary>
public sealed record ExecutionServiceEvent(long OutboxSequence, OrderEvent Event);

/// <summary>Client-side aggregate of one response and its bounded event batch.</summary>
public sealed record ExecutionServiceExchange(
    ExecutionServiceResponse Response,
    IReadOnlyList<ExecutionServiceEvent> Events);
