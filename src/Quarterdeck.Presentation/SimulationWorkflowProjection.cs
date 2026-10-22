namespace Quarterdeck.Presentation;

public enum PresentationFactSource
{
    Recorded,
    FakeAuthorityObserved,
    ActorAttested,
    Derived
}

public enum WorkflowPresentationCondition
{
    WaitingForIt,
    ReadyForItValidation,
    AwaitingHrVerification,
    AwaitingExactApproval,
    ApprovalExpired,
    ApprovalRevoked,
    ApprovalSuperseded,
    ClockUnavailable,
    ClockMovedBackward,
    MandatoryGateFailed,
    ReadyForSimulatedIssuance,
    FakeIssuancePending,
    OutcomeUnresolved,
    EvidenceMissing,
    EvidenceMismatch,
    EvidenceAmbiguous,
    FakeAuthorityUnavailable,
    SimulationJourneyComplete,
    NeedsHrCorrection
}

public enum WorkflowQueueGroup
{
    HrIntake,
    WaitingForIt,
    HrVerificationAndApproval,
    ItSimulatedIssuance,
    NeedsAttention,
    Complete
}

public enum WorkflowNextRole
{
    HrRequester,
    ItFormalRequester,
    HrVerifier,
    HrApprover,
    ItIssuer,
    None
}

public enum AuditStateKind
{
    Attempted,
    Denied,
    Failed,
    Interrupted,
    Pending,
    Unknown,
    Successful
}

public sealed record AuditStatePresentation(
    AuditStateKind Kind, string Label, string CssClass, string SafeCode);

public static class AuditStatePresentationMapper
{
    public static AuditStatePresentation Map(string authorizationDecision,
        string outcome, string safeResult)
    {
        var combined = $"{authorizationDecision}|{outcome}|{safeResult}";
        if (Has(combined, "denied") || Has(combined, "unauthorized"))
            return State(AuditStateKind.Denied, "Denied", "audit-denied", "denied");
        if (Has(combined, "interrupted") || Has(combined, "cancelled"))
            return State(AuditStateKind.Interrupted, "Interrupted", "audit-interrupted", "interrupted");
        if (Has(combined, "pending"))
            return State(AuditStateKind.Pending, "Pending", "audit-pending", "pending");
        if (Has(combined, "failed") || Has(combined, "rejected")
            || Has(combined, "unavailable"))
            return State(AuditStateKind.Failed, "Failed", "audit-failed", "failed");
        if (Has(combined, "unknown") || Has(combined, "uncertain"))
            return State(AuditStateKind.Unknown, "Unknown", "audit-unknown", "unknown");
        if (Has(combined, "attempted") || Has(combined, "started"))
            return State(AuditStateKind.Attempted, "Attempted", "audit-attempted", "attempted");
        if (Has(combined, "authorized") || Has(combined, "recorded")
            || Has(combined, "succeeded") || Has(combined, "complete"))
            return State(AuditStateKind.Successful, "Successful", "audit-successful", "successful");
        return State(AuditStateKind.Unknown, "Unknown", "audit-unknown", "unknown");
    }

    private static bool Has(string value, string token) =>
        value.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static AuditStatePresentation State(AuditStateKind kind,
        string label, string css, string code) => new(kind, label, css, code);
}

public sealed record PresentationFact(
    string Label,
    string Value,
    PresentationFactSource Source);

public sealed record FormalVersionProjection(
    long? Version,
    string Status,
    PresentationFactSource Source,
    IReadOnlyList<PresentationFact> BoundFields);

public sealed record ApprovalProjection(
    string Status,
    string? ApprovalId,
    DateTimeOffset? ApprovedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? ObservedAt,
    PresentationFactSource Source);

public sealed record OperationProjection(
    string Status,
    string? OperationId,
    int DispatchCount,
    int AcceptanceCount,
    string? AuthorityReference,
    PresentationFactSource Source);

public sealed record TimelineProjection(
    long Sequence,
    DateTimeOffset OccurredAt,
    string Event,
    string ActorReference,
    string Role,
    string Outcome,
    PresentationFactSource Source);

public sealed record NextActionProjection(
    WorkflowNextRole Role,
    string Action,
    string SafeStopCode,
    bool ActionAvailable,
    PresentationFactSource Source);

public sealed record SimulationWorkflowProjection(
    string CaseId,
    string DisplayLabel,
    string SiteId,
    long CaseRevision,
    WorkflowPresentationCondition Condition,
    WorkflowQueueGroup QueueGroup,
    string Headline,
    PresentationFactSource StateSource,
    FormalVersionProjection FormalVersion,
    ApprovalProjection Approval,
    OperationProjection Operation,
    IReadOnlyList<TimelineProjection> Timeline,
    NextActionProjection NextAction,
    string DepartmentLabel = "Unspecified",
    string PersonReference = "Unspecified",
    string? ResolvedAccountId = null);

public sealed record SimulationWorkflowProjectionInput(
    string CaseId,
    string DisplayLabel,
    string SiteId,
    long CaseRevision,
    WorkflowPresentationCondition Condition,
    FormalVersionProjection FormalVersion,
    ApprovalProjection Approval,
    OperationProjection Operation,
    IReadOnlyList<TimelineProjection> Timeline,
    string DepartmentLabel = "Unspecified",
    string PersonReference = "Unspecified",
    string? ResolvedAccountId = null);

public static class SimulationWorkflowProjectionMapper
{
    public static SimulationWorkflowProjection Map(SimulationWorkflowProjectionInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        RequireToken(input.CaseId, nameof(input.CaseId));
        RequireToken(input.DisplayLabel, nameof(input.DisplayLabel));
        RequireToken(input.SiteId, nameof(input.SiteId));
        ArgumentNullException.ThrowIfNull(input.FormalVersion);
        ArgumentNullException.ThrowIfNull(input.Approval);
        ArgumentNullException.ThrowIfNull(input.Operation);
        ArgumentNullException.ThrowIfNull(input.Timeline);

        var state = Describe(input.Condition);
        return new(
            input.CaseId,
            input.DisplayLabel,
            input.SiteId,
            input.CaseRevision,
            input.Condition,
            state.Group,
            state.Headline,
            state.Source,
            input.FormalVersion,
            input.Approval,
            input.Operation,
            Array.AsReadOnly(input.Timeline.ToArray()),
            new(state.Role, state.Action, state.SafeStopCode, state.ActionAvailable, state.Source),
            input.DepartmentLabel,
            input.PersonReference,
            input.ResolvedAccountId);
    }

    public static IReadOnlyList<WorkflowPresentationCondition> SupportedConditions { get; } =
        Array.AsReadOnly(Enum.GetValues<WorkflowPresentationCondition>());

    private static PresentationStateDescription Describe(WorkflowPresentationCondition condition) => condition switch
    {
        WorkflowPresentationCondition.NeedsHrCorrection => new(WorkflowQueueGroup.HrIntake,
            "Needs HR correction",WorkflowNextRole.HrRequester,
            "Review IT's reason, add your correction and resubmit to IT.","hr_correction_required",true,PresentationFactSource.Recorded),
        WorkflowPresentationCondition.WaitingForIt => new(WorkflowQueueGroup.WaitingForIt,
            "Waiting for IT", WorkflowNextRole.ItFormalRequester,
            "Resolve and validate the exact synthetic site and account.", "account_resolution_required", true,
            PresentationFactSource.Recorded),
        WorkflowPresentationCondition.ReadyForItValidation => new(WorkflowQueueGroup.WaitingForIt,
            "Ready for IT validation", WorkflowNextRole.ItFormalRequester,
            "Review the account and seal the formal request.", "formal_version_required", true,
            PresentationFactSource.Derived),
        WorkflowPresentationCondition.AwaitingHrVerification => new(WorkflowQueueGroup.HrVerificationAndApproval,
            "Awaiting HR verification", WorkflowNextRole.HrVerifier,
            "Verify the intended synthetic person for this exact version.", "identity_verification_required", true,
            PresentationFactSource.Derived),
        WorkflowPresentationCondition.AwaitingExactApproval => new(WorkflowQueueGroup.HrVerificationAndApproval,
            "Awaiting exact approval", WorkflowNextRole.HrApprover,
            "Review and approve this exact formal version.", "exact_approval_required", true,
            PresentationFactSource.Derived),
        WorkflowPresentationCondition.ApprovalExpired => Block("Approval expired", "Start a new approval workflow; the old approval cannot be extended.", "approval_expired", WorkflowNextRole.HrApprover),
        WorkflowPresentationCondition.ApprovalRevoked => Block("Approval revoked", "Review the current request and obtain a new exact approval.", "approval_revoked", WorkflowNextRole.HrApprover),
        WorkflowPresentationCondition.ApprovalSuperseded => Block("Approval superseded", "Review and approve the current formal version.", "approval_superseded", WorkflowNextRole.HrApprover),
        WorkflowPresentationCondition.ClockUnavailable => Block("Clock unavailable", "Restore the authoritative simulated clock before continuing.", "clock_unavailable", WorkflowNextRole.ItIssuer),
        WorkflowPresentationCondition.ClockMovedBackward => Block("Clock moved backward", "Resolve the authoritative simulated-clock fault; do not dispatch.", "clock_moved_backward", WorkflowNextRole.ItIssuer),
        WorkflowPresentationCondition.MandatoryGateFailed => Block("Mandatory gate failed", "Resolve the named mandatory prerequisite; there is no bypass.", "mandatory_gate_failed", WorkflowNextRole.ItIssuer),
        WorkflowPresentationCondition.ReadyForSimulatedIssuance => new(WorkflowQueueGroup.ItSimulatedIssuance,
            "Ready for simulated issuance", WorkflowNextRole.ItIssuer,
            "Submit one bounded fake issuance attempt.", "ready", true, PresentationFactSource.Derived),
        WorkflowPresentationCondition.FakeIssuancePending => Attention("Fake issuance pending",
            "Observe the same fake request; do not resubmit.", "fake_result_pending"),
        WorkflowPresentationCondition.OutcomeUnresolved => Attention("Outcome unresolved",
            "Perform read-only observation and exact-evidence reconciliation.", "outcome_unresolved"),
        WorkflowPresentationCondition.EvidenceMissing => Attention("Recovery evidence missing",
            "Leave the case unresolved or supply exact evidence for the same operation.", "recovery_evidence_missing"),
        WorkflowPresentationCondition.EvidenceMismatch => Attention("Recovery evidence does not match",
            "Do not retry; investigate the exact operation binding.", "recovery_evidence_mismatch"),
        WorkflowPresentationCondition.EvidenceAmbiguous => Attention("Recovery evidence is ambiguous",
            "Leave the case unresolved until one exact result can be established.", "recovery_evidence_ambiguous"),
        WorkflowPresentationCondition.FakeAuthorityUnavailable => Attention("Fake authority unavailable",
            "Restore observation capability; do not infer or resubmit the result.", "fake_authority_unavailable"),
        WorkflowPresentationCondition.SimulationJourneyComplete => new(WorkflowQueueGroup.Complete,
            "Simulation journey complete", WorkflowNextRole.None,
            "No further simulation action. This does not prove physical delivery or Windows logon.", "complete", false,
            PresentationFactSource.Derived),
        _ => throw new ArgumentOutOfRangeException(nameof(condition), condition,
            "presentation_condition_unmapped")
    };

    private static PresentationStateDescription Block(
        string headline, string action, string code, WorkflowNextRole role) =>
        new(WorkflowQueueGroup.NeedsAttention, headline, role, action, code, false,
            PresentationFactSource.Derived);

    private static PresentationStateDescription Attention(string headline, string action, string code) =>
        new(WorkflowQueueGroup.NeedsAttention, headline, WorkflowNextRole.ItIssuer, action, code, true,
            PresentationFactSource.Derived);

    private static void RequireToken(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 160)
        {
            throw new ArgumentException("presentation_token_invalid", parameter);
        }
    }

    private sealed record PresentationStateDescription(
        WorkflowQueueGroup Group,
        string Headline,
        WorkflowNextRole Role,
        string Action,
        string SafeStopCode,
        bool ActionAvailable,
        PresentationFactSource Source);
}

public sealed record WorkQueueFilter(
    WorkflowNextRole? Role = null,
    string? SiteId = null,
    WorkflowPresentationCondition? Condition = null);

public static class SimulationWorkQueue
{
    public static IReadOnlyList<SimulationWorkflowProjection> Build(
        IEnumerable<SimulationWorkflowProjection> cases,
        WorkQueueFilter? filter = null)
    {
        ArgumentNullException.ThrowIfNull(cases);
        filter ??= new();
        var query = cases;
        if (filter.Role is not null)
        {
            query = query.Where(item => item.NextAction.Role == filter.Role);
        }
        if (!string.IsNullOrWhiteSpace(filter.SiteId))
        {
            query = query.Where(item => string.Equals(
                item.SiteId, filter.SiteId.Trim(), StringComparison.Ordinal));
        }
        if (filter.Condition is not null)
        {
            query = query.Where(item => item.Condition == filter.Condition);
        }

        return Array.AsReadOnly(query
            .OrderBy(item => Priority(item.QueueGroup))
            .ThenBy(item => item.Timeline.LastOrDefault()?.OccurredAt ?? DateTimeOffset.MinValue)
            .ThenBy(item => item.CaseId, StringComparer.Ordinal)
            .ToArray());
    }

    private static int Priority(WorkflowQueueGroup group) => group switch
    {
        WorkflowQueueGroup.NeedsAttention => 0,
        WorkflowQueueGroup.WaitingForIt => 1,
        WorkflowQueueGroup.HrIntake => 2,
        WorkflowQueueGroup.HrVerificationAndApproval => 3,
        WorkflowQueueGroup.ItSimulatedIssuance => 4,
        WorkflowQueueGroup.Complete => 5,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "queue_group_unmapped")
    };
}
