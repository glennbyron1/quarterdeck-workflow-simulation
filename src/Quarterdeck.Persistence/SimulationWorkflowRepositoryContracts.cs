namespace Quarterdeck.Persistence;

internal sealed record HrCorrectionSnapshot(long Sequence, string Action, string Actor,
    long OccurredUtc, string Comment, string? ProposedUsername);

internal sealed record NameChangeSnapshot(string RequestId, string CaseId,
    string PersonId, string SiteId, string AccountId, string OldName,
    string RequestedName, string? Comment, string ActorAccount, long RequestedUtc,
    string? CompletedBy = null, long? CompletedUtc = null, string? CompletionComment = null);

internal static class SimulationWorkflowAuditCodes
{
    internal const string IntakeCreated = "intake_created";
    internal const string AccountResolutionAppended = "account_resolution_appended";
    internal const string FormalVersionAppended = "formal_version_appended";
    internal const string FormalVersionSuperseded = "formal_version_superseded";
    internal const string GateObserved = "gate_observed";
    internal const string IdentityVerified = "identity_verified";
    internal const string ApprovalRecorded = "approval_recorded";
    internal const string IntentCommitted = "intent_committed";

    internal const string Authorized = "authorized";
    internal const string Recorded = "recorded";
}

internal enum IntentCommitStatus
{
    Committed,
    CommittedAfterReadback,
    CommittedDispatchBlocked,
    NotCommitted,
    Uncertain
}

internal enum IntentCommitFaultPoint
{
    BeforeWrites,
    BeforeCommit,
    AfterCommit
}

internal interface IIntentCommitFault
{
    void Observe(IntentCommitFaultPoint point);
}

internal sealed class NoIntentCommitFault : IIntentCommitFault
{
    internal static readonly NoIntentCommitFault Instance = new();
    private NoIntentCommitFault() { }
    public void Observe(IntentCommitFaultPoint point) { }
}

internal sealed class CommitIntentCommand
{
    internal CommitIntentCommand(
        string operationId,
        string eventId,
        string auditEventId,
        Quarterdeck.Core.Simulation.SyntheticDispatchAdmission admission)
    {
        OperationId = operationId;
        EventId = eventId;
        AuditEventId = auditEventId;
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    internal string OperationId { get; }
    internal string EventId { get; }
    internal string AuditEventId { get; }
    internal Quarterdeck.Core.Simulation.SyntheticDispatchAdmission Admission { get; }
    internal string CaseId => Admission.CaseId;
    internal long ExpectedCaseRevision => Admission.ExpectedCaseRevision;
    internal long FormalVersion => Admission.FormalVersion;
    internal string ApprovalId => Admission.ApprovalId;
    internal long AttemptNumber => Admission.AttemptNumber;
    internal string SyntheticPublicRequestReference => Admission.SyntheticPublicRequestReference;
    internal PersistedActor Actor => new(
        Admission.Issuer.PersonId.Value,
        Admission.Issuer.AccountId.Value,
        Admission.Issuer.DisplayLabel,
        Quarterdeck.Core.Simulation.SyntheticWorkflowRole.ItIssuer.ToString());
    internal long ObservedUtc => Admission.ObservedAt.ToUnixTimeMilliseconds();
    internal long ApprovedUtc => Admission.ApprovedAt.ToUnixTimeMilliseconds();
    internal long ExpiresUtc => Admission.ExpiresAt.ToUnixTimeMilliseconds();

    internal CommitIntentCommand Reissue(
        string operationId,
        string eventId,
        string auditEventId,
        long expectedCaseRevision,
        long attemptNumber) =>
        new(operationId, eventId, auditEventId,
            Admission.Reissue(expectedCaseRevision, attemptNumber));
}

internal static class SimulationIntentTransition
{
    internal const string SourceStep = "Intent";
    internal const string SourceAction = "prepare";
    internal const string DestinationStep = "Dispatch";
    internal const string DestinationAction = "dispatch_fake";
}

internal sealed record IntentCommitReceipt
{
    internal IntentCommitReceipt(string operationId, string operationBindingDigest,
        string workflowDatabaseInstanceId, long intentCommitSequence,
        Quarterdeck.Core.Simulation.SyntheticDispatchAdmission admission)
    {
        OperationId = operationId;
        OperationBindingDigest = operationBindingDigest;
        WorkflowDatabaseInstanceId = workflowDatabaseInstanceId;
        IntentCommitSequence = intentCommitSequence;
        Admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    internal string OperationId { get; }
    internal string OperationBindingDigest { get; }
    internal string WorkflowDatabaseInstanceId { get; }
    internal long IntentCommitSequence { get; }
    internal Quarterdeck.Core.Simulation.SyntheticDispatchAdmission Admission { get; }
}

internal readonly record struct IntentCommitResult(
    IntentCommitStatus Status,
    string SafeResultCode,
    IntentCommitReceipt? Receipt)
{
    internal bool IsDurablyCommitted => Status is IntentCommitStatus.Committed
        or IntentCommitStatus.CommittedAfterReadback
        or IntentCommitStatus.CommittedDispatchBlocked;
}

internal enum IntentReadbackState
{
    Exact,
    Absent,
    Mismatch,
    Unreadable
}

internal readonly record struct IntentReadback(
    IntentReadbackState State,
    IntentCommitReceipt? Receipt,
    string SafeResultCode);

internal readonly record struct PersistedActor(
    string PersonId,
    string AccountId,
    string DisplayLabel,
    string Role);

internal readonly record struct PersistedIdentity(
    string PersonId,
    string AccountId,
    string DisplayLabel);

internal readonly record struct CandidateAccountSnapshot(
    string AccountId,
    string EligibilityObservationCode,
    long FixtureRevision);

internal readonly record struct DepartmentCatalogueSnapshot(
    string Code,
    string DisplayName,
    int SortOrder,
    bool IsActive);

internal readonly record struct IntakeProfileSnapshot(
    string SyntheticDisplayName,
    string DepartmentCode,
    string DepartmentLabel,
    string? JobTitle,
    string? EmployeeReference,
    bool OtherDepartmentReviewRequired);

internal readonly record struct PolicySelectionSnapshot(
    string OptionCode,
    string Classification);

internal sealed class CreateIntakeCommand
{
    internal CreateIntakeCommand(
        string caseId,
        string personId,
        string siteId,
        PersistedActor intakeActor,
        long intakeUtc,
        string currentStep,
        string requiredNextActionCode,
        IEnumerable<CandidateAccountSnapshot> candidateAccounts,
        string auditEventId,
        IntakeProfileSnapshot? intakeProfile = null)
    {
        CaseId = caseId;
        PersonId = personId;
        SiteId = siteId;
        IntakeActor = intakeActor;
        IntakeUtc = intakeUtc;
        CurrentStep = currentStep;
        RequiredNextActionCode = requiredNextActionCode;
        CandidateAccounts = Array.AsReadOnly(candidateAccounts.ToArray());
        AuditEventId = auditEventId;
        IntakeProfile = intakeProfile;
    }

    internal string CaseId { get; }
    internal string PersonId { get; }
    internal string SiteId { get; }
    internal PersistedActor IntakeActor { get; }
    internal long IntakeUtc { get; }
    internal string CurrentStep { get; }
    internal string RequiredNextActionCode { get; }
    internal IReadOnlyList<CandidateAccountSnapshot> CandidateAccounts { get; }
    internal string AuditEventId { get; }
    internal IntakeProfileSnapshot? IntakeProfile { get; }
}

internal readonly record struct AppendResolutionCommand(
    string CaseId,
    long ExpectedCaseRevision,
    long ResolutionSequence,
    string AccountId,
    PersistedActor Resolver,
    long DirectoryRevision,
    bool SubmittedCandidateMatched,
    string? CorrectionReason,
    long ResolvedUtc,
    string NextStep,
    string RequiredNextActionCode,
    string AuditEventId);

internal readonly record struct FormalRequestSnapshot(
    string SiteId,
    string PersonId,
    string AccountId,
    string IntendedUse,
    string FakeDeviceId,
    string Slot,
    string SyntheticPublicRequestDigest,
    string FakeAuthority,
    string Template,
    string PolicyOptionsVersion);

internal sealed class PolicySnapshot
{
    internal PolicySnapshot(
        string authorityId,
        string version,
        string contentDigest,
        IEnumerable<PolicySelectionSnapshot> selections)
    {
        AuthorityId = authorityId;
        Version = version;
        ContentDigest = contentDigest;
        Selections = Array.AsReadOnly(selections.ToArray());
    }

    internal string AuthorityId { get; }
    internal string Version { get; }
    internal string ContentDigest { get; }
    internal IReadOnlyList<PolicySelectionSnapshot> Selections { get; }
}

internal readonly record struct AppendFormalVersionCommand(
    string CaseId,
    long ExpectedCaseRevision,
    long Version,
    FormalRequestSnapshot Request,
    PersistedActor Requester,
    long CreatedUtc,
    PolicySnapshot Policy,
    long OperationBindingInputRevision,
    string SealedRequestDigest,
    long? SupersedesVersion,
    long? SupersededUtc,
    string? SupersededReasonCode,
    PolicyMigrationSnapshot? PolicyMigration,
    string NextStep,
    string RequiredNextActionCode,
    string AuditEventId);

internal readonly record struct PolicyMigrationSnapshot(
    string MigrationId,
    long SourceFormalVersion,
    long ReplacementFormalVersion,
    string SourcePolicyAuthorityId,
    string SourcePolicyVersion,
    string SourcePolicyContentDigest,
    string ReplacementPolicyAuthorityId,
    string ReplacementPolicyVersion,
    string ReplacementPolicyContentDigest,
    PersistedActor Actor,
    long MigratedUtc,
    string ReasonCode);

internal readonly record struct AppendGateObservationCommand(
    string ObservationId,
    string CaseId,
    long ExpectedCaseRevision,
    long FormalVersion,
    string GateCode,
    string ObservedState,
    string SafeResultCode,
    PersistedActor Actor,
    long ObservedUtc,
    string NextStep,
    string RequiredNextActionCode,
    string AuditEventId);

internal readonly record struct AppendIdentityVerificationCommand(
    string AttestationId,
    string CaseId,
    long ExpectedCaseRevision,
    long FormalVersion,
    string SubjectPersonId,
    PersistedActor Verifier,
    long ObservedUtc,
    long FixtureRevision,
    string NextStep,
    string RequiredNextActionCode,
    string AuditEventId);

internal readonly record struct AppendApprovalCommand(
    string ApprovalId,
    string CaseId,
    long ExpectedCaseRevision,
    long FormalVersion,
    PersistedActor Approver,
    long ApprovedUtc,
    long ExpiresUtc,
    string ApprovedRequestDigest,
    string PolicyAuthorityId,
    string PolicyVersion,
    string PolicyContentDigest,
    string? InvalidatesApprovalId,
    long? InvalidatedUtc,
    string? InvalidationReasonCode,
    string NextStep,
    string RequiredNextActionCode,
    string AuditEventId);

internal readonly record struct ResolutionSnapshot(
    long Sequence,
    string AccountId,
    PersistedIdentity Resolver,
    long DirectoryRevision,
    bool SubmittedCandidateMatched,
    string? CorrectionReason,
    long ResolvedUtc);

internal readonly record struct FormalVersionSnapshot(
    long Version,
    FormalRequestSnapshot Request,
    PersistedIdentity Requester,
    long CreatedUtc,
    PolicySnapshot Policy,
    long OperationBindingInputRevision,
    string SealedRequestDigest,
    long? SupersededUtc,
    string? SupersededReasonCode);

internal readonly record struct GateObservationSnapshot(
    string ObservationId,
    long ObservationSequence,
    long FormalVersion,
    string GateCode,
    string ObservedState,
    string SafeResultCode,
    PersistedIdentity Actor,
    long ObservedUtc);

internal readonly record struct GateRequirementSnapshot(
    string GateCode, string Classification, string CatalogueVersion,
    string PolicyAuthorityId, string PolicyVersion, string PolicyContentDigest);

internal readonly record struct IdentityVerificationSnapshot(
    string AttestationId,
    long FormalVersion,
    string SubjectPersonId,
    PersistedActor Verifier,
    long ObservedUtc,
    long FixtureRevision);

internal readonly record struct ApprovalSnapshot(
    string ApprovalId,
    long FormalVersion,
    PersistedActor Approver,
    long ApprovedUtc,
    long ExpiresUtc,
    string ApprovedRequestDigest,
    string PolicyAuthorityId,
    string PolicyVersion,
    string PolicyContentDigest,
    long? InvalidatedUtc,
    string? InvalidationReasonCode,
    string? ReplacedByApprovalId,
    long? RevokedUtc,
    string? RevocationReasonCode,
    string? ConsumedByOperationId,
    long? ConsumedUtc)
{
    internal bool IsExpiredAt(long observedUtc) => observedUtc >= ExpiresUtc;
}

internal readonly record struct OperationSnapshot(
    string OperationId, string CaseId, long FormalVersion, string ApprovalId,
    string OperationBindingDigest, string State, long RowRevision);

internal readonly record struct AuditEventSnapshot(
    string AuditEventId, long Sequence, string? ActorPersonId,
    string? ActorAccountId, string? ActorDisplayLabel, string? ActorRole,
    string ActionCode, string AuthorizationDecisionCode, string OutcomeCode,
    long OccurredUtc, long? FormalVersion, string? ApprovalId,
    string? OperationId, string? PolicyAuthorityId, string? PolicyVersion,
    string? PolicyContentDigest, string? SafeReasonCode,
    string? SafeResultCode, string? SafeSourceReference);

internal readonly record struct CommandDenialSnapshot(
    long Sequence, string? ActorPersonId, string? ActorAccountId,
    string? ActorDisplayLabel, string CommandCode, string SafeResultCode,
    long OccurredUtc);

internal sealed class WorkflowCaseSnapshot
{
    internal WorkflowCaseSnapshot(
        string caseId,
        string personId,
        string siteId,
        PersistedIdentity intakeActor,
        long intakeUtc,
        string currentStep,
        string requiredNextActionCode,
        long? currentFormalVersion,
        long rowRevision,
        IEnumerable<CandidateAccountSnapshot> candidates,
        IEnumerable<ResolutionSnapshot> resolutions,
        IEnumerable<FormalVersionSnapshot> formalVersions,
        IEnumerable<PolicyMigrationSnapshot> policyMigrations,
        IEnumerable<GateObservationSnapshot> gateObservations,
        IEnumerable<IdentityVerificationSnapshot> verifications,
        IEnumerable<ApprovalSnapshot> approvals,
        IntakeProfileSnapshot? intakeProfile = null)
    {
        CaseId = caseId;
        PersonId = personId;
        SiteId = siteId;
        IntakeActor = intakeActor;
        IntakeUtc = intakeUtc;
        CurrentStep = currentStep;
        RequiredNextActionCode = requiredNextActionCode;
        CurrentFormalVersion = currentFormalVersion;
        RowRevision = rowRevision;
        CandidateAccounts = Array.AsReadOnly(candidates.ToArray());
        Resolutions = Array.AsReadOnly(resolutions.ToArray());
        FormalVersions = Array.AsReadOnly(formalVersions.ToArray());
        PolicyMigrations = Array.AsReadOnly(policyMigrations.ToArray());
        GateObservations = Array.AsReadOnly(gateObservations.ToArray());
        Verifications = Array.AsReadOnly(verifications.ToArray());
        Approvals = Array.AsReadOnly(approvals.ToArray());
        IntakeProfile = intakeProfile;
    }

    internal string CaseId { get; }
    internal string PersonId { get; }
    internal string SiteId { get; }
    internal PersistedIdentity IntakeActor { get; }
    internal long IntakeUtc { get; }
    internal string CurrentStep { get; }
    internal string RequiredNextActionCode { get; }
    internal long? CurrentFormalVersion { get; }
    internal long RowRevision { get; }
    internal IReadOnlyList<CandidateAccountSnapshot> CandidateAccounts { get; }
    internal IReadOnlyList<ResolutionSnapshot> Resolutions { get; }
    internal IReadOnlyList<FormalVersionSnapshot> FormalVersions { get; }
    internal IReadOnlyList<PolicyMigrationSnapshot> PolicyMigrations { get; }
    internal IReadOnlyList<GateObservationSnapshot> GateObservations { get; }
    internal IReadOnlyList<IdentityVerificationSnapshot> Verifications { get; }
    internal IReadOnlyList<ApprovalSnapshot> Approvals { get; }
    internal IntakeProfileSnapshot? IntakeProfile { get; }
}
