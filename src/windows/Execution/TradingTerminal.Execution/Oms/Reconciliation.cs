namespace TradingTerminal.Execution.Oms;

/// <summary>Exact comparison result required by roadmap section 10.3.</summary>
public enum ReconciliationCaseKind : byte
{
    /// <summary>Local and simulated-adapter evidence agrees exactly.</summary>
    Matched = 0,

    /// <summary>The simulated adapter reports a subject absent from the local ledger.</summary>
    LocallyMissing = 1,

    /// <summary>The local ledger reports a subject absent from the simulated adapter.</summary>
    BrokerMissing = 2,

    /// <summary>Exact quantity, filled quantity, position, or cash values disagree.</summary>
    QuantityMismatch = 3,

    /// <summary>Exact limit or stop prices disagree.</summary>
    PriceMismatch = 4,

    /// <summary>Terminal/open classification or terminal outcome disagrees.</summary>
    TerminalStateMismatch = 5,

    /// <summary>More than one candidate has the same canonical matching identity.</summary>
    DuplicateCandidate = 6,

    /// <summary>The evidence cannot be compared safely without operator judgment.</summary>
    ManualException = 7,
}

/// <summary>Kind of local and simulated-adapter subject compared by a cycle.</summary>
public enum ReconciliationSubjectKind : byte
{
    /// <summary>One canonical client order.</summary>
    Order = 0,

    /// <summary>One exact instrument position.</summary>
    Position = 1,

    /// <summary>One exact currency cash balance.</summary>
    Cash = 2,

    /// <summary>An account-level comparison failure.</summary>
    Account = 3,
}

/// <summary>Lifecycle of an append-only durable reconciliation case.</summary>
public enum ReconciliationCaseStatus : byte
{
    /// <summary>The discrepancy is unresolved.</summary>
    Open = 0,

    /// <summary>Later evidence confirmed that operator investigation is still required.</summary>
    Investigating = 1,

    /// <summary>A later immutable fact records explicit resolution evidence.</summary>
    Resolved = 2,
}

/// <summary>
/// One immutable reconciliation-case fact. A resolution repeats the immutable observation fields
/// and adds resolver/time/evidence; the original open fact remains unchanged in storage.
/// </summary>
public sealed record ReconciliationCase(
    ReconciliationCaseId CaseId,
    BrokerExecutionAccount Account,
    ReconciliationSubjectKind SubjectKind,
    string SubjectKey,
    ClientOrderId? ClientOrderId,
    ReconciliationCaseKind Kind,
    ReconciliationCaseStatus Status,
    string LocalEvidence,
    string BrokerEvidence,
    DateTime OpenedAtUtc,
    DateTime? ResolvedAtUtc,
    string? ResolvedBy,
    string? ResolutionEvidence)
{
    /// <summary>All non-matched classifications fail closed while unresolved.</summary>
    public bool IsMaterial => Kind != ReconciliationCaseKind.Matched;

    /// <summary>Gets whether every identity, timestamp, and evidence field is internally consistent.</summary>
    public bool IsValid
    {
        get
        {
            if (!CaseId.IsValid ||
                !Account.IsValid ||
                !Enum.IsDefined(SubjectKind) ||
                string.IsNullOrWhiteSpace(SubjectKey) ||
                SubjectKey.Length > 512 ||
                !Enum.IsDefined(Kind) ||
                !Enum.IsDefined(Status) ||
                string.IsNullOrWhiteSpace(LocalEvidence) ||
                string.IsNullOrWhiteSpace(BrokerEvidence) ||
                OpenedAtUtc.Kind != DateTimeKind.Utc ||
                (SubjectKind == ReconciliationSubjectKind.Order) != ClientOrderId.HasValue ||
                ClientOrderId is { IsValid: false })
            {
                return false;
            }

            if (Status == ReconciliationCaseStatus.Resolved)
            {
                return ResolvedAtUtc is { Kind: DateTimeKind.Utc } resolvedAt &&
                       resolvedAt >= OpenedAtUtc &&
                       !string.IsNullOrWhiteSpace(ResolvedBy) &&
                       !string.IsNullOrWhiteSpace(ResolutionEvidence);
            }

            return Kind != ReconciliationCaseKind.Matched &&
                   !ResolvedAtUtc.HasValue &&
                   ResolvedBy is null &&
                   ResolutionEvidence is null;
        }
    }
}

/// <summary>Persistence seam for append-only reconciliation-case facts.</summary>
public interface IReconciliationCaseStore
{
    /// <summary>Appends an opening, investigation, or resolution fact.</summary>
    bool TryAppend(ReconciliationCase reconciliationCase);

    /// <summary>Reads facts attached to one canonical order.</summary>
    IReadOnlyList<ReconciliationCase> Read(ClientOrderId clientOrderId);

    /// <summary>Reads every explicit case fact for one independently gated adapter/account.</summary>
    IReadOnlyList<ReconciliationCase> Read(BrokerExecutionAccount account);

    /// <summary>Reads the complete append-only fact sequence for one case.</summary>
    IReadOnlyList<ReconciliationCase> Read(ReconciliationCaseId caseId);
}

/// <summary>Deterministic in-process case store used by engine tests and non-durable simulations.</summary>
public sealed class InMemoryReconciliationCaseStore : IReconciliationCaseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<ReconciliationCaseId, List<ReconciliationCase>> _facts = [];

    /// <inheritdoc />
    public bool TryAppend(ReconciliationCase reconciliationCase)
    {
        if (reconciliationCase is null || !reconciliationCase.IsValid)
            return false;

        lock (_gate)
        {
            if (!_facts.TryGetValue(reconciliationCase.CaseId, out var facts))
            {
                if (!CanStartFactSequence(reconciliationCase))
                    return false;
                facts = [];
                _facts.Add(reconciliationCase.CaseId, facts);
            }
            else
            {
                var latest = facts[^1];
                if (latest == reconciliationCase)
                    return true;
                if (!HasSameObservation(latest, reconciliationCase) ||
                    reconciliationCase.Status <= latest.Status)
                {
                    return false;
                }
            }

            facts.Add(reconciliationCase);
            return true;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ReconciliationCase> Read(ClientOrderId clientOrderId) =>
        ReadWhere(item => item.ClientOrderId == clientOrderId);

    /// <inheritdoc />
    public IReadOnlyList<ReconciliationCase> Read(BrokerExecutionAccount account) =>
        ReadWhere(item => item.Account == account);

    /// <inheritdoc />
    public IReadOnlyList<ReconciliationCase> Read(ReconciliationCaseId caseId)
    {
        lock (_gate)
        {
            return _facts.TryGetValue(caseId, out var facts)
                ? Array.AsReadOnly(facts.ToArray())
                : Array.Empty<ReconciliationCase>();
        }
    }

    internal static bool HasSameObservation(ReconciliationCase left, ReconciliationCase right) =>
        left.CaseId == right.CaseId &&
        left.Account == right.Account &&
        left.SubjectKind == right.SubjectKind &&
        string.Equals(left.SubjectKey, right.SubjectKey, StringComparison.Ordinal) &&
        left.ClientOrderId == right.ClientOrderId &&
        left.Kind == right.Kind &&
        string.Equals(left.LocalEvidence, right.LocalEvidence, StringComparison.Ordinal) &&
        string.Equals(left.BrokerEvidence, right.BrokerEvidence, StringComparison.Ordinal) &&
        left.OpenedAtUtc == right.OpenedAtUtc;

    internal static bool CanStartFactSequence(ReconciliationCase fact) =>
        fact.Kind == ReconciliationCaseKind.Matched
            ? fact.Status == ReconciliationCaseStatus.Resolved
            : fact.Status == ReconciliationCaseStatus.Open;

    private IReadOnlyList<ReconciliationCase> ReadWhere(Func<ReconciliationCase, bool> predicate)
    {
        lock (_gate)
        {
            var result = _facts
                .OrderBy(item => item.Key.Value, StringComparer.Ordinal)
                .SelectMany(item => item.Value)
                .Where(predicate)
                .ToArray();
            return result.Length == 0 ? Array.Empty<ReconciliationCase>() : Array.AsReadOnly(result);
        }
    }
}

/// <summary>Explicit terminal evidence that completes an OMS Unknown reconciliation.</summary>
public readonly record struct ReconciliationResolution(
    ReconciliationCaseId CaseId,
    OrderLifecycleState ObservedState,
    string Evidence)
{
    /// <summary>Gets whether identity, terminal observed state, and evidence are valid.</summary>
    public bool IsValid =>
        CaseId.IsValid &&
        (ObservedState is OrderLifecycleState.Filled or
            OrderLifecycleState.Cancelled or
            OrderLifecycleState.Rejected or
            OrderLifecycleState.Expired) &&
        !string.IsNullOrWhiteSpace(Evidence);
}
