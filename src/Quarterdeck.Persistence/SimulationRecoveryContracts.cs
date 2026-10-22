namespace Quarterdeck.Persistence;

internal enum WorkflowResultFaultPoint
{
    BeforeCommit,
    AfterCommit
}

internal interface IWorkflowResultFault
{
    void Observe(WorkflowResultFaultPoint point);
}

internal sealed class NoWorkflowResultFault : IWorkflowResultFault
{
    internal static readonly NoWorkflowResultFault Instance = new();
    private NoWorkflowResultFault() { }
    public void Observe(WorkflowResultFaultPoint point) { }
}

internal sealed class PersistAuthorityObservationCommand
{
    internal PersistAuthorityObservationCommand(string eventId, string operationId,
        string bindingDigest, long observedUtc)
    {
        EventId = eventId; OperationId = operationId;
        BindingDigest = bindingDigest; ObservedUtc = observedUtc;
    }
    internal string EventId { get; }
    internal string OperationId { get; }
    internal string BindingDigest { get; }
    internal long ObservedUtc { get; }
}

internal enum WorkflowResultPersistenceStatus
{
    Persisted,
    PersistedAfterReadback,
    Rejected,
    Mismatch,
    Unknown
}

internal readonly record struct WorkflowResultPersistenceResult(
    WorkflowResultPersistenceStatus Status,
    string SafeResultCode);

internal readonly record struct JournalRecoveryProjection(
    string JournalDatabaseInstanceId,
    long JournalSequence,
    string OperationId,
    string ApprovalId,
    string BindingDigest,
    string AdmissionIdentityDigest,
    string WorkflowDatabaseInstanceId,
    long WorkflowCommitSequence,
    string SiteId,
    string AccountId,
    string IntendedUse,
    string FakeDeviceId,
    string Slot,
    AuthorityOperationState State,
    string SafeResultCode,
    string? ResultReference);

internal readonly record struct JournalRecoveryBatch(
    string JournalDatabaseInstanceId,
    long SequenceStart,
    long SequenceEnd,
    IReadOnlyList<JournalRecoveryProjection> Operations);

internal readonly record struct JournalWatermark(
    string JournalDatabaseInstanceId,
    long JournalSequence);

internal enum RecoveryDiscoveryFaultPoint
{
    AfterWorkflowCommitBeforeJournalReadback
}

internal interface IRecoveryDiscoveryFault
{
    void Observe(RecoveryDiscoveryFaultPoint point);
}

internal sealed class NoRecoveryDiscoveryFault : IRecoveryDiscoveryFault
{
    internal static readonly NoRecoveryDiscoveryFault Instance = new();
    private NoRecoveryDiscoveryFault() { }
    public void Observe(RecoveryDiscoveryFaultPoint point) { }
}

internal enum RecoveryMatchState
{
    AbsentLocal,
    Pending,
    Terminal,
    Mismatch,
    Ambiguous,
    Unknown
}

internal readonly record struct RecoveryFinding(
    string FindingId,
    string OperationId,
    RecoveryMatchState MatchState,
    string SafeResultCode,
    bool BlocksDispatch,
    string AccountResourceReference,
    string DeviceResourceReference);

internal readonly record struct RecoveryDiscoveryResult(
    string CycleId,
    string SafeResultCode,
    IReadOnlyList<RecoveryFinding> Findings);

internal sealed class ReconcileRecoveryCommand
{
    internal ReconcileRecoveryCommand(string actionId, string findingId,
        string operationId, Quarterdeck.Core.Simulation.SyntheticReconciliationAuthorization authorization,
        long observedUtc, string reasonCode)
    {
        ActionId = actionId; FindingId = findingId; OperationId = operationId;
        Authorization = authorization; ObservedUtc = observedUtc;
        ReasonCode = reasonCode;
    }
    internal string ActionId { get; }
    internal string FindingId { get; }
    internal string OperationId { get; }
    internal Quarterdeck.Core.Simulation.SyntheticReconciliationAuthorization Authorization { get; }
    internal long ObservedUtc { get; }
    internal string ReasonCode { get; }
}

internal readonly record struct RecoveryActionResult(bool Applied, string SafeResultCode);
