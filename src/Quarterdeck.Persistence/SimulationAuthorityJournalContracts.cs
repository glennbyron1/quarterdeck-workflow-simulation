namespace Quarterdeck.Persistence;

internal enum AuthorityJournalFaultPoint
{
    AfterPreparedBeforeAcceptance,
    AfterCommit
}

internal interface IAuthorityJournalFault
{
    void Observe(AuthorityJournalFaultPoint point);
}

internal sealed class NoAuthorityJournalFault : IAuthorityJournalFault
{
    internal static readonly NoAuthorityJournalFault Instance = new();
    private NoAuthorityJournalFault() { }
    public void Observe(AuthorityJournalFaultPoint point) { }
}

/// <summary>
/// Process-local evidence only. Durable acceptance truth remains in the
/// independent authority journal.
/// </summary>
internal sealed class AuthorityCallCounters
{
    private long _dispatchCalls;

    internal long DispatchCalls => Interlocked.Read(ref _dispatchCalls);
    internal void RecordDispatchCall() => Interlocked.Increment(ref _dispatchCalls);
}

internal sealed class AcceptAuthorityOperationCommand
{
    internal AcceptAuthorityOperationCommand(IntentCommitReceipt intent)
    {
        Intent = intent;
        Admission = intent.Admission
            ?? throw new ArgumentException("intent_admission_missing", nameof(intent));
    }

    internal IntentCommitReceipt Intent { get; }
    internal Quarterdeck.Core.Simulation.SyntheticDispatchAdmission Admission { get; }
    internal string OperationId => Intent.OperationId;
    internal string BindingDigest => Intent.OperationBindingDigest;
    internal string WorkflowDatabaseInstanceId => Intent.WorkflowDatabaseInstanceId;
    internal long WorkflowCommitSequence => Intent.IntentCommitSequence;
    internal string CaseId => Admission.CaseId;
    internal long FormalVersion => Admission.FormalVersion;
    internal string ApprovalId => Admission.ApprovalId;
    internal long AttemptNumber => Admission.AttemptNumber;
    internal string SyntheticPublicRequestReference => Admission.SyntheticPublicRequestReference;
    internal string TargetSiteId => Admission.Fields.SiteId.Value;
    internal string TargetAccountId => Admission.Fields.AccountId.Value;
    internal string IntendedUse => Admission.Fields.IntendedUse;
    internal string FakeDeviceId => Admission.Fields.FakeDeviceId;
    internal string Slot => Admission.Fields.Slot;
    internal string AuthorityId => Admission.Fields.FakeAuthority;
    internal string Template => Admission.Fields.Template;
    internal long ObservedUtc => Admission.ObservedAt.ToUnixTimeMilliseconds();
}

internal readonly record struct ObserveAuthorityOperationCommand(
    string OperationId,
    string BindingDigest);

internal enum AuthorityDispatchStatus
{
    AcceptedPending,
    AcceptedTerminal,
    AcceptedPendingAfterReadback,
    RejectedBeforeAcceptance,
    AlreadyConsumed,
    ExistingOperationMismatch,
    NotAccepted,
    Uncertain
}

internal enum AuthorityOperationState
{
    Absent,
    AcceptedPending,
    AcceptedTerminal,
    RejectedBeforeAcceptance,
    Mismatch,
    Unreadable
}

internal readonly record struct AuthorityOperationReceipt(
    string OperationId,
    string ApprovalId,
    string BindingDigest,
    string JournalDatabaseInstanceId,
    long JournalSequence,
    AuthorityOperationState State,
    string? ResultReference);

internal readonly record struct AuthorityDispatchResult(
    AuthorityDispatchStatus Status,
    string SafeResultCode,
    AuthorityOperationReceipt? Receipt)
{
    internal bool IsAccepted => Status is AuthorityDispatchStatus.AcceptedPending
        or AuthorityDispatchStatus.AcceptedTerminal
        or AuthorityDispatchStatus.AcceptedPendingAfterReadback;
}

internal readonly record struct AuthorityObservation(
    AuthorityOperationState State,
    string SafeResultCode,
    AuthorityOperationReceipt? Receipt);
