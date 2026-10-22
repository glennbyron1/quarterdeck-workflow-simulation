using Quarterdeck.Core.Simulation;
using Quarterdeck.Persistence;
using Quarterdeck.Presentation;
using System.Runtime.CompilerServices;

namespace Quarterdeck.Application;

public sealed record ResolveWaitingForItRequest(
    string CaseId,
    long ExpectedCaseRevision,
    string ExactAccountId,
    string ActorAccountId,
    string? CorrectionReason);

public sealed record RegisterSimulationAccountRequest(
    string CaseId,
    long ExpectedCaseRevision,
    string ExactAccountId,
    string ActorAccountId);

public sealed record CreateSyntheticIntakeRequest(
    string CaseId,
    string PersonId,
    string SiteId,
    string ActorAccountId,
    IReadOnlyList<string> CandidateAccountIds,
    string? SyntheticDisplayName = null,
    string? DepartmentCode = null,
    string? OtherDepartmentLabel = null,
    string? JobTitle = null,
    string? EmployeeReference = null);

public sealed record DepartmentOption(string Code, string DisplayName);

public sealed record HrCorrectionRequest(string CaseId, long ExpectedRevision,
    string ActorAccountId, string Comment, string? ProposedUsername = null);
public sealed record HrCorrectionView(long Sequence, string Action, string Actor,
    DateTimeOffset OccurredAt, string Comment, string? ProposedUsername);

public sealed record NameChangeRequest(string CaseId, long ExpectedRevision,
    string RequestedName, string? Comment, string ActorAccountId);
public sealed record NameChangeView(string RequestId, string CaseId, string PersonId,
    string SiteId, string Username, string CurrentName, string RequestedName,
    string? Comment, string RequestedBy, DateTimeOffset RequestedAt,
    string? CompletedBy, DateTimeOffset? CompletedAt, string? CompletionComment);

public sealed record SimulationCommandResult(
    bool Succeeded,
    string SafeResultCode,
    string CaseId);

public sealed record DeniedCommandProjection(
    long Sequence, DateTimeOffset OccurredAt, string ActorReference,
    string CommandCode, string SafeResultCode);

public sealed record VerifyIdentityRequest(
    string CaseId, long ExpectedCaseRevision, long FormalVersion,
    string ActorAccountId);

public sealed record SealFormalRequest(
    string CaseId, long ExpectedCaseRevision, string ActorAccountId,
    FormalRequestFieldInput Fields);

public sealed record ObserveMandatoryGateRequest(
    string CaseId, long ExpectedCaseRevision, long FormalVersion,
    string ActorAccountId, string GateCode);

public sealed record FormalRequestFieldInput(
    string SiteId, string PersonId, string AccountId, string IntendedUse,
    string FakeDeviceId, string Slot, string SyntheticPublicRequestDigest,
    string FakeAuthority, string Template, string PolicyOptionsVersion);

public sealed record ApproveExactVersionRequest(
    string CaseId, long ExpectedCaseRevision, long FormalVersion,
    string ActorAccountId, FormalRequestFieldInput ObservedFields);

public sealed record FormalVersionDifference(
    string Label, string? PreviousValue, string CurrentValue, bool Changed);

public sealed record FormalVersionReviewProjection(
    string CaseId, long CaseRevision, long Version, long? PreviousVersion,
    string RequesterReference,
    IReadOnlyList<PresentationFact> BoundFields,
    IReadOnlyList<FormalVersionDifference> Differences,
    bool IsCurrent, string SafeStopCode);

public sealed record PolicySelectionProjection(string OptionCode, string Classification);
public sealed record MandatoryGateProjection(
    string GateCode, string Status, string SafeResultCode, bool Satisfied);
public sealed record PolicyApprovalProjection(
    string CaseId, long FormalVersion, string PolicyAuthorityId,
    string PolicyVersion, string PolicyContentDigest,
    IReadOnlyList<PolicySelectionProjection> Selections,
    string ApprovalStatus, string? ApprovalId, DateTimeOffset? ApprovedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? ObservedAt,
    long? RemainingSeconds, string ClockStatus,
    IReadOnlyList<MandatoryGateProjection> MandatoryGates,
    bool PolicyMigrationLocked, bool DispatchAuthorityAvailable,
    string SafeStopCode);

public sealed record StartFakeIssuanceRequest(
    string CaseId, long ExpectedCaseRevision, long FormalVersion,
    string ActorAccountId, string SyntheticPublicRequestReference);

public sealed record ObserveFakeIssuanceRequest(
    string CaseId, string OperationId, string ActorAccountId);

public sealed record ReconcileFakeIssuanceRequest(
    string CaseId, string FindingId, string OperationId,
    string ActorAccountId, string ReasonCode);

public sealed record FakeIssuanceProjection(
    string CaseId, string? OperationId, string IntentStatus,
    string DispatchStatus, string ResultStatus, bool RetryAllowed,
    string SafeResultCode, string? RecoveryFindingId);

public sealed record AuditEventProjection(
    string EventId, long Sequence, DateTimeOffset OccurredAt,
    string ActorPersonId, string ActorAccountId, string ActorDisplayLabel,
    string ActorRole, string Action, string AuthorizationDecision,
    string Outcome, long? FormalVersion, string? ApprovalId,
    string? OperationId, string? PolicyAuthorityId, string? PolicyVersion,
    string? PolicyDigest, string SafeReason, string SafeResult,
    string SourceReference, AuditStatePresentation State);

public sealed record SimulationCounterProjection(long FakeDispatchCalls);

public interface ISimulationUseCases
{
    SimulationCommandResult ReturnToHr(HrCorrectionRequest request);
    SimulationCommandResult ResubmitToIt(HrCorrectionRequest request);
    IReadOnlyList<HrCorrectionView> ReadHrCorrections(string caseId);
    IReadOnlyList<NameChangeView> ReadNameChanges();
    SimulationCommandResult RequestNameChange(NameChangeRequest request);
    SimulationCommandResult CompleteNameChange(string requestId, string actorAccountId, string comment);
    IReadOnlyList<DepartmentOption> ReadDepartments();
    IReadOnlyList<SimulationWorkflowProjection> ReadWorkQueue(WorkQueueFilter filter);
    SimulationWorkflowProjection? ReadCase(string caseId);
    FormalVersionReviewProjection? ReadVersionReview(string caseId, long version);
    PolicyApprovalProjection? ReadPolicyApproval(string caseId);
    SimulationCommandResult CreateIntake(CreateSyntheticIntakeRequest request);
    SimulationCommandResult RegisterSimulationAccount(RegisterSimulationAccountRequest request);
    SimulationCommandResult ResolveWaitingForIt(ResolveWaitingForItRequest request);
    SimulationCommandResult SealFormalRequest(SealFormalRequest request);
    SimulationCommandResult ObserveMandatoryGate(ObserveMandatoryGateRequest request);
    SimulationCommandResult VerifyIdentity(VerifyIdentityRequest request);
    SimulationCommandResult ApproveExactVersion(ApproveExactVersionRequest request);
    SimulationCommandResult StartFakeIssuance(StartFakeIssuanceRequest request);
    SimulationCommandResult ObserveFakeIssuance(ObserveFakeIssuanceRequest request);
    SimulationCommandResult ReconcileFakeIssuance(ReconcileFakeIssuanceRequest request);
    FakeIssuanceProjection? ReadFakeIssuance(string caseId);
    IReadOnlyList<AuditEventProjection> ReadAuditTrail(string caseId);
    IReadOnlyList<DeniedCommandProjection> ReadDeniedCommands(string caseId);
    SimulationCounterProjection ReadSimulationCounters();
}

/// <summary>
/// Sole Phase 1 application boundary between the web host and qualified SQLite
/// repositories. Browser values identify a requested action; the current
/// synthetic directory and persisted revision remain authoritative.
/// </summary>
public sealed class SimulationUseCases : ISimulationUseCases
{
    public IReadOnlyList<HrCorrectionView> ReadHrCorrections(string caseId) => !ValidToken(caseId,80) ? [] :
        _workflow.LoadHrCorrections(caseId).Select(r=>new HrCorrectionView(r.Sequence,r.Action,r.Actor,
            FromUnix(r.OccurredUtc),r.Comment,r.ProposedUsername)).ToArray();
    public SimulationCommandResult ReturnToHr(HrCorrectionRequest request) => RecordHrCorrection(request,true);
    public SimulationCommandResult ResubmitToIt(HrCorrectionRequest request) => RecordHrCorrection(request,false);
    private SimulationCommandResult RecordHrCorrection(HrCorrectionRequest request, bool returning)
    {
        if (!ValidToken(request.CaseId,80) || !NameChangeText(request.Comment,500)
            || (returning && request.ProposedUsername is not null))
            return Failed(request.CaseId,"command_input_invalid",request.ActorAccountId);
        try
        {
            if(request.ProposedUsername is not null) _ = new SyntheticAccountId(request.ProposedUsername);
            var snapshot=_workflow.Load(request.CaseId);
            if(snapshot is null) return Failed(request.CaseId,"case_not_found",request.ActorAccountId);
            var role=returning ? SyntheticWorkflowRole.ItFormalRequester : SyntheticWorkflowRole.HrIntake;
            var actor=_directory.CaptureCurrentRoleActor(_directory.Resolve(new(request.ActorAccountId)),role,new(snapshot.SiteId));
            if(snapshot.RowRevision!=request.ExpectedRevision) return Failed(request.CaseId,"case_revision_stale",request.ActorAccountId);
            if(snapshot.FormalVersions.Count>0 || (returning
                ? snapshot.CurrentStep is not ("WaitingForIt" or "FormalRequest")
                : snapshot.CurrentStep!="HrIntake"))
                return Failed(request.CaseId,"workflow_step_mismatch",request.ActorAccountId);
            _workflow.RecordHrCorrection(request.CaseId,request.ExpectedRevision,
                new(actor.PersonId.Value,actor.AccountId.Value,actor.DisplayLabel,role.ToString()),
                _clock().ToUnixTimeMilliseconds(),request.Comment.Trim(),request.ProposedUsername,returning);
            return new(true,returning ? "returned_to_hr" : "hr_resubmitted",request.CaseId);
        }
        catch(InvalidOperationException error) { return Failed(request.CaseId,SafeCode(error.Message),request.ActorAccountId); }
        catch { return Failed(request.CaseId,"command_rejected",request.ActorAccountId); }
    }
    public IReadOnlyList<NameChangeView> ReadNameChanges() => _workflow.LoadNameChanges()
        .Select(r => new NameChangeView(r.RequestId,r.CaseId,r.PersonId,r.SiteId,r.AccountId,
            r.OldName,r.RequestedName,r.Comment,r.ActorAccount,FromUnix(r.RequestedUtc),
            r.CompletedBy,r.CompletedUtc is null ? null : FromUnix(r.CompletedUtc.Value),r.CompletionComment)).ToArray();

    public SimulationCommandResult RequestNameChange(NameChangeRequest request)
    {
        if (!ValidToken(request.CaseId,80) || !NameChangeText(request.RequestedName,160)
            || (request.Comment is not null && !NameChangeText(request.Comment,500)))
            return Failed(request.CaseId,"command_input_invalid",request.ActorAccountId);
        try
        {
            var source = _workflow.Load(request.CaseId) ?? throw new InvalidOperationException("case_not_found");
            if (source.RowRevision != request.ExpectedRevision) throw new InvalidOperationException("simulation_workflow_concurrency_conflict");
            if (source.Resolutions.Count == 0) throw new InvalidOperationException("exact_subject_site_account_unresolved");
            var actor = _directory.CaptureCurrentRoleActor(_directory.Resolve(new(request.ActorAccountId)),
                SyntheticWorkflowRole.HrIntake,new(source.SiteId));
            var account = source.Resolutions[^1].AccountId;
            var subject = _directory.Resolve(new(account));
            if (subject.PersonId.Value != source.PersonId || subject.SiteId?.Value != source.SiteId)
                throw new InvalidOperationException("exact_subject_site_account_unresolved");
            var oldName = source.IntakeProfile?.SyntheticDisplayName ?? source.PersonId;
            if (string.Equals(oldName,request.RequestedName.Trim(),StringComparison.Ordinal))
                throw new ArgumentException("name_unchanged");
            _workflow.CreateNameChange(new(Guid.NewGuid().ToString("D"),source.CaseId,source.PersonId,
                source.SiteId,account,oldName,request.RequestedName.Trim(),request.Comment?.Trim(),
                actor.AccountId.Value,_clock().ToUnixTimeMilliseconds()),request.ExpectedRevision,
                new(actor.PersonId.Value,actor.AccountId.Value,actor.DisplayLabel,SyntheticWorkflowRole.HrIntake.ToString()));
            return new(true,"name_change_requested",source.CaseId);
        }
        catch (InvalidOperationException error) { return Failed(request.CaseId,SafeCode(error.Message),request.ActorAccountId); }
        catch { return Failed(request.CaseId,"command_rejected",request.ActorAccountId); }
    }

    public SimulationCommandResult CompleteNameChange(string requestId, string actorAccountId, string comment)
    {
        if (!Guid.TryParseExact(requestId,"D",out _) || !NameChangeText(comment,500))
            return Failed(string.Empty,"command_input_invalid",actorAccountId);
        string caseId = string.Empty;
        try
        {
            var row = _workflow.LoadNameChanges().Single(r => r.RequestId == requestId);
            caseId = row.CaseId;
            if (row.CompletedBy is not null) return Failed(caseId,"name_change_already_completed",actorAccountId);
            var actor = _directory.CaptureCurrentRoleActor(_directory.Resolve(new(actorAccountId)),
                SyntheticWorkflowRole.ItFormalRequester,new(row.SiteId));
            _workflow.CompleteNameChange(row,new(actor.PersonId.Value,actor.AccountId.Value,
                actor.DisplayLabel,SyntheticWorkflowRole.ItFormalRequester.ToString()),
                _clock().ToUnixTimeMilliseconds(),comment.Trim());
            return new(true,"name_change_simulation_completed",caseId);
        }
        catch (InvalidOperationException error) { return Failed(caseId,SafeCode(error.Message),actorAccountId); }
        catch { return Failed(caseId,"command_rejected",actorAccountId); }
    }

    private static bool NameChangeText(string? value, int max) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= max && !value.Any(char.IsControl);
    private readonly SimulationWorkflowRepository _workflow;
    private readonly SyntheticIdentityDirectory _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SimulationRuntime _runtime;
    private readonly SyntheticSimulationApprovalClock _approvalClock;
    private readonly SyntheticDispatchAdmissionAuthority _dispatchAuthority;
    private readonly IReadOnlyList<RecoveryFinding> _startupRecoveryFindings;
    private readonly bool _deferResultPersistence;

    private SimulationUseCases(
        SimulationRuntime runtime,
        SyntheticIdentityDirectory directory,
        Func<DateTimeOffset> clock,
        SyntheticSimulationApprovalClock approvalClock,
        SyntheticDispatchAdmissionAuthority dispatchAuthority,
        IReadOnlyList<RecoveryFinding> startupRecoveryFindings,
        bool deferResultPersistence)
    {
        _runtime = runtime;
        _workflow = runtime.Workflow;
        _directory = directory;
        _clock = clock;
        _approvalClock = approvalClock;
        _dispatchAuthority = dispatchAuthority;
        _startupRecoveryFindings = startupRecoveryFindings;
        _deferResultPersistence = deferResultPersistence;
    }

    public static ISimulationUseCases Create(
        SimulationStorePaths paths,
        string sourceRevision,
        IEnumerable<SyntheticIdentityBinding> identities,
        IEnumerable<string>? qualifiedFakeDeviceIds = null,
        IEnumerable<string>? availableFakeAuthorityIds = null,
        IEnumerable<string>? clearSubjectAccountIds = null,
        Func<DateTimeOffset>? clock = null,
        bool completeFakeAuthorityAtDispatch = false,
        bool deferResultPersistence = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(identities);
        var directory = SyntheticIdentityDirectory.Create(identities);
        var approvalClock = new SyntheticSimulationApprovalClock(DateTimeOffset.UnixEpoch);
        var dispatchAuthority = new SyntheticDispatchAdmissionAuthority(directory, approvalClock);
        var runtime = SimulationCompositionRoot.Compose("fake", paths, sourceRevision,
            dispatchAuthority, completeFakeAuthorityAtDispatch);
        foreach (var caseId in runtime.Workflow.LoadCaseIds())
        {
            var snapshot = runtime.Workflow.Load(caseId);
            if (snapshot is not null)
            {
                // Fixture creation is durable provenance, not the current account
                // selection. A later reconfirmation must not erase that provenance.
                foreach (var resolution in snapshot.Resolutions.Where(value =>
                    value.CorrectionReason == "simulation_fixture_created_and_confirmed"))
                    directory.RestoreSimulationSubjectAccount(
                        new(snapshot.PersonId), new(snapshot.SiteId), new(resolution.AccountId));
            }
        }
        var applicationClock = clock ?? (() => DateTimeOffset.UtcNow);
        // The qualified recovery gate starts closed. Complete its fail-closed
        // comparison before this application instance can attempt dispatch.
        RecoveryDiscoveryResult? startupRecovery = null;
        try
        {
            startupRecovery = runtime.Recovery.DiscoverAtStartup(
                applicationClock().ToUnixTimeMilliseconds());
        }
        catch
        {
            // The coordinator remains closed; read-only projections can still
            // report that the authoritative clock or recovery gate is unavailable.
        }
        foreach (var device in qualifiedFakeDeviceIds ?? [])
            directory.SetDeviceQualificationState(device,
                SyntheticDeviceQualificationState.Qualified);
        foreach (var authority in availableFakeAuthorityIds ?? [])
            directory.SetAuthorityAvailabilityState(authority,
                SyntheticAuthorityAvailabilityState.Available);
        foreach (var account in clearSubjectAccountIds ?? [])
            directory.SetContainmentState(new(account), SyntheticContainmentState.Clear);
        return new SimulationUseCases(runtime, directory,
            applicationClock, approvalClock, dispatchAuthority,
            startupRecovery?.Findings ?? [], deferResultPersistence);
    }

    public IReadOnlyList<SimulationWorkflowProjection> ReadWorkQueue(WorkQueueFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var projections = _workflow.LoadCaseIds()
            .Select(id => _workflow.Load(id))
            .Where(snapshot => snapshot is not null)
            .Select(snapshot => Map(snapshot!));
        return SimulationWorkQueue.Build(projections, filter);
    }

    public SimulationWorkflowProjection? ReadCase(string caseId)
    {
        if (!ValidToken(caseId, 80)) return null;
        var snapshot = _workflow.Load(caseId);
        return snapshot is null ? null : Map(snapshot);
    }

    public FormalVersionReviewProjection? ReadVersionReview(string caseId, long version)
    {
        if (!ValidToken(caseId, 80) || version < 1) return null;
        var snapshot = _workflow.Load(caseId);
        var exact = snapshot?.FormalVersions
            .Where(item => item.Version == version)
            .Select(item => (FormalVersionSnapshot?)item)
            .SingleOrDefault();
        if (snapshot is null || exact is null) return null;
        var previous = snapshot.FormalVersions
            .Where(item => item.Version < version)
            .Select(item => (FormalVersionSnapshot?)item)
            .LastOrDefault();
        var current = Fields(exact.Value.Request);
        var old = previous is null ? null : Fields(previous.Value.Request);
        var differences = current.Select((field, index) => new FormalVersionDifference(
            field.Label, old?[index].Value, field.Value,
            old is not null && !string.Equals(old[index].Value, field.Value,
                StringComparison.Ordinal))).ToArray();
        var isCurrent = snapshot.CurrentFormalVersion == version
            && exact.Value.SupersededUtc is null;
        return new(caseId, snapshot.RowRevision, version, previous?.Version,
            $"{exact.Value.Requester.AccountId} · {exact.Value.Requester.DisplayLabel}",
            current, Array.AsReadOnly(differences), isCurrent,
            isCurrent ? "exact_version_current" : "formal_version_superseded");
    }

    public PolicyApprovalProjection? ReadPolicyApproval(string caseId)
    {
        if (!ValidToken(caseId, 80)) return null;
        var snapshot = _workflow.Load(caseId);
        if (snapshot?.CurrentFormalVersion is not { } versionNumber) return null;
        var version = snapshot.FormalVersions.Single(item => item.Version == versionNumber);
        var approval = snapshot.Approvals
            .Where(item => item.FormalVersion == versionNumber)
            .Select(item => (ApprovalSnapshot?)item)
            .LastOrDefault();
        var requirements = _workflow.LoadGateRequirements(caseId, versionNumber)
            .Where(item => string.Equals(item.Classification, "Required", StringComparison.Ordinal));
        var gates = requirements.Select(requirement =>
        {
            var latest = snapshot.GateObservations
                .Where(item => item.FormalVersion == versionNumber
                    && string.Equals(item.GateCode, requirement.GateCode, StringComparison.Ordinal))
                .OrderBy(item => item.ObservationSequence)
                .Select(item => (GateObservationSnapshot?)item)
                .LastOrDefault();
            var satisfied = latest is { } value
                && string.Equals(value.ObservedState, "Satisfied", StringComparison.Ordinal);
            return new MandatoryGateProjection(requirement.GateCode,
                latest?.ObservedState ?? "Not observed",
                latest?.SafeResultCode ?? "mandatory_gate_unresolved", satisfied);
        }).ToArray();
        var observed = ObserveClock(approval?.ApprovedUtc);
        var status = ApprovalStatus(approval, observed, gates);
        var migrationLocked = snapshot.PolicyMigrations.Any(item =>
                item.ReplacementFormalVersion == versionNumber)
            && gates.Any(item => !item.Satisfied);
        return new(caseId, versionNumber, version.Policy.AuthorityId,
            version.Policy.Version, version.Policy.ContentDigest,
            Array.AsReadOnly(version.Policy.Selections.Select(item =>
                new PolicySelectionProjection(item.OptionCode, item.Classification)).ToArray()),
            status.Status, approval?.ApprovalId,
            approval is null ? null : FromUnix(approval.Value.ApprovedUtc),
            approval is null ? null : FromUnix(approval.Value.ExpiresUtc),
            observed.ObservedAt, observed.RemainingSeconds, observed.ClockStatus,
            Array.AsReadOnly(gates), migrationLocked,
            status.SafeStopCode == "ready", status.SafeStopCode);
    }

    public SimulationCommandResult CreateIntake(CreateSyntheticIntakeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var automaticCaseId = string.IsNullOrWhiteSpace(request.CaseId);
        if ((!automaticCaseId && !ValidToken(request.CaseId, 80))
            || !ValidToken(request.PersonId, 160)
            || !ValidToken(request.SiteId, 160)
            || !ValidToken(request.ActorAccountId, 160)
            || request.CandidateAccountIds is null
            || request.CandidateAccountIds.Any(value => !ValidToken(value, 160))
            || (request.SyntheticDisplayName is not null && !ValidToken(request.SyntheticDisplayName, 160))
            || (request.DepartmentCode is not null && !ValidToken(request.DepartmentCode, 40))
            || (request.OtherDepartmentLabel is not null && !ValidToken(request.OtherDepartmentLabel, 80))
            || (request.JobTitle is not null && !ValidToken(request.JobTitle, 100))
            || (request.EmployeeReference is not null && !ValidToken(request.EmployeeReference, 80)))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var caseId = automaticCaseId ? _workflow.AllocateCaseId() : request.CaseId;
            var actor = _directory.Resolve(new(request.ActorAccountId));
            var candidates = request.CandidateAccountIds.Select(value =>
                new SyntheticAccountId(value)).ToArray();
            IntakeProfileSnapshot? profile = null;
            if (request.DepartmentCode is not null || request.SyntheticDisplayName is not null)
            {
                if (!ValidToken(request.SyntheticDisplayName, 160)
                    || !ValidToken(request.DepartmentCode, 40))
                    throw new InvalidOperationException("intake_profile_incomplete");
                var department = _workflow.LoadDepartments()
                    .SingleOrDefault(value => string.Equals(value.Code,
                        request.DepartmentCode, StringComparison.Ordinal));
                if (string.IsNullOrEmpty(department.Code))
                    throw new InvalidOperationException("department_unavailable");
                var isOther = string.Equals(department.Code, "OTHER", StringComparison.Ordinal);
                if (isOther != (request.OtherDepartmentLabel is not null))
                    throw new InvalidOperationException("other_department_label_invalid");
                profile = new(request.SyntheticDisplayName!.Trim(), department.Code,
                    isOther ? request.OtherDepartmentLabel!.Trim() : department.DisplayName,
                    request.JobTitle?.Trim(), request.EmployeeReference?.Trim(), isOther);
            }
            var intake = HrIntake.Create(caseId, actor, _directory,
                new(request.PersonId), new(request.SiteId), candidates);
            var now = _clock().ToUnixTimeMilliseconds();
            _workflow.CreateIntake(new(
                intake.CaseId,
                intake.PersonId.Value,
                intake.SiteId.Value,
                new(intake.IntakeActorPersonId.Value, intake.IntakeActorAccountId.Value,
                    intake.IntakeActorDisplayLabel, SyntheticWorkflowRole.HrIntake.ToString()),
                now,
                intake.State.ToString(),
                intake.State == IntakeState.WaitingForIt ? "it_resolve" : "it_formalize",
                candidates.Select(value => new CandidateAccountSnapshot(value.Value,
                    "submitted_candidate", intake.DirectoryRevision)),
                Guid.NewGuid().ToString("D"), profile));
            return new(true, "intake_recorded", caseId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException)
        {
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        }
        catch
        {
            return Failed(request.CaseId, "command_rejected", request.ActorAccountId);
        }
    }

    public IReadOnlyList<DepartmentOption> ReadDepartments() =>
        Array.AsReadOnly(_workflow.LoadDepartments()
            .Select(value => new DepartmentOption(value.Code, value.DisplayName)).ToArray());

    public SimulationCommandResult RegisterSimulationAccount(
        RegisterSimulationAccountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80)
            || !ValidToken(request.ExactAccountId, 160)
            || !ValidToken(request.ActorAccountId, 160))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);

        try
        {
            var snapshot = _workflow.Load(request.CaseId);
            if (snapshot is null) return Failed(request.CaseId, "case_not_found", request.ActorAccountId);
            if (snapshot.RowRevision != request.ExpectedCaseRevision)
                return Failed(request.CaseId, "case_revision_stale", request.ActorAccountId);
            if (!string.Equals(snapshot.CurrentStep, "WaitingForIt", StringComparison.Ordinal))
                return Failed(request.CaseId, "account_resolution_not_required", request.ActorAccountId);

            var actor = _directory.Resolve(new(request.ActorAccountId));
            return _directory.RegisterSimulationSubjectAccount(actor,
                new(snapshot.PersonId), new(snapshot.SiteId), new(request.ExactAccountId),
                snapshot.IntakeProfile?.SyntheticDisplayName ?? snapshot.PersonId,
                () =>
                {
                    var result = ResolveWaitingForItCore(new(request.CaseId, request.ExpectedCaseRevision,
                request.ExactAccountId, request.ActorAccountId,
                "simulation_fixture_created_and_confirmed"), true);
                    if (!result.Succeeded) throw new InvalidOperationException(result.SafeResultCode);
                    return result;
                });
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException)
        {
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        }
        catch
        {
            return Failed(request.CaseId, "command_rejected", request.ActorAccountId);
        }
    }

    public SimulationCommandResult ResolveWaitingForIt(ResolveWaitingForItRequest request)
        => ResolveWaitingForItCore(request, false);

    private SimulationCommandResult ResolveWaitingForItCore(ResolveWaitingForItRequest request, bool fixture)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80)
            || !ValidToken(request.ExactAccountId, 160)
            || !ValidToken(request.ActorAccountId, 160))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);

        try
        {
            if (!fixture && request.CorrectionReason?.Trim() == "simulation_fixture_created_and_confirmed")
                return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
            var snapshot = _workflow.Load(request.CaseId);
            if (snapshot is null) return Failed(request.CaseId, "case_not_found", request.ActorAccountId);
            if (snapshot.RowRevision != request.ExpectedCaseRevision)
                return Failed(request.CaseId, "case_revision_stale", request.ActorAccountId);
            if (!string.Equals(snapshot.CurrentStep, "WaitingForIt", StringComparison.Ordinal))
                return Failed(request.CaseId, "account_resolution_not_required", request.ActorAccountId);

            var actor = _directory.Resolve(new(request.ActorAccountId));
            var authority = _directory.ResolveExactEnabledAccount(
                actor, new(snapshot.PersonId), new(snapshot.SiteId),
                new(request.ExactAccountId));
            var matched = !fixture && snapshot.CandidateAccounts.Any(candidate =>
                string.Equals(candidate.AccountId, request.ExactAccountId,
                    StringComparison.Ordinal));
            var reason = NormalizeReason(request.CorrectionReason, matched);
            var now = _clock().ToUnixTimeMilliseconds();
            _workflow.AppendResolution(new(
                request.CaseId,
                request.ExpectedCaseRevision,
                snapshot.Resolutions.Count + 1L,
                authority.Subject.AccountId.Value,
                new(authority.Resolver.PersonId.Value,
                    authority.Resolver.AccountId.Value,
                    authority.Resolver.DisplayLabel,
                    SyntheticWorkflowRole.ItFormalRequester.ToString()),
                authority.DirectoryRevision,
                matched,
                reason,
                now,
                "FormalRequest",
                "it_formalize",
                Guid.NewGuid().ToString("D")));
            return new(true, "account_resolution_recorded", request.CaseId);
        }
        catch (InvalidOperationException error)
        {
            if (fixture) throw;
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException)
        {
            if (fixture) throw;
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        }
        catch
        {
            if (fixture) throw;
            return Failed(request.CaseId, "command_rejected", request.ActorAccountId);
        }
    }

    public SimulationCommandResult SealFormalRequest(SealFormalRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80)
            || !ValidToken(request.ActorAccountId, 160)
            || request.Fields is null)
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);

        try
        {
            var snapshot = _workflow.Load(request.CaseId);
            if (snapshot is null) return Failed(request.CaseId, "case_not_found", request.ActorAccountId);
            if (snapshot.RowRevision != request.ExpectedCaseRevision)
                return Failed(request.CaseId, "case_revision_stale", request.ActorAccountId);
            if (snapshot.CurrentFormalVersion is not null)
                return Failed(request.CaseId, "formal_version_already_exists", request.ActorAccountId);
            if (!string.Equals(snapshot.CurrentStep, "FormalRequest", StringComparison.Ordinal)
                && !string.Equals(snapshot.CurrentStep, "ReadyForItValidation", StringComparison.Ordinal))
                return Failed(request.CaseId, "workflow_step_mismatch", request.ActorAccountId);

            var actor = _directory.CaptureCurrentRoleActor(
                _directory.Resolve(new(request.ActorAccountId)),
                SyntheticWorkflowRole.ItFormalRequester, new(snapshot.SiteId));
            var exactAccountId = snapshot.Resolutions.Count > 0
                ? snapshot.Resolutions[^1].AccountId
                : snapshot.CandidateAccounts.Count == 1
                    ? snapshot.CandidateAccounts[0].AccountId
                    : null;
            if (exactAccountId is null
                || !string.Equals(request.Fields.SiteId, snapshot.SiteId, StringComparison.Ordinal)
                || !string.Equals(request.Fields.PersonId, snapshot.PersonId, StringComparison.Ordinal)
                || !string.Equals(request.Fields.AccountId, exactAccountId, StringComparison.Ordinal))
                return Failed(request.CaseId, "bound_fields_changed", request.ActorAccountId);
            var authority = _directory.ResolveExactEnabledAccount(actor,
                new(snapshot.PersonId), new(snapshot.SiteId), new(exactAccountId));

            var fields = ToCore(request.Fields);
            fields.Validate();
            var policy = SimulationPolicySnapshot.CreateClassified(
                "quarterdeck-simulation-policy", request.Fields.PolicyOptionsVersion,
                [new("directory-ready", SimulationPolicyOptionClassification.Required)]);
            var bindingRevision = authority.DirectoryRevision;
            var sealedDigest = SealedRequestBinding.Compute(fields, policy, bindingRevision);
            var now = ObserveUtc().ToUnixTimeMilliseconds();
            _workflow.AppendFormalVersion(new(
                request.CaseId, request.ExpectedCaseRevision, 1,
                new(request.Fields.SiteId, request.Fields.PersonId,
                    request.Fields.AccountId, request.Fields.IntendedUse,
                    request.Fields.FakeDeviceId, request.Fields.Slot,
                    request.Fields.SyntheticPublicRequestDigest,
                    request.Fields.FakeAuthority, request.Fields.Template,
                    request.Fields.PolicyOptionsVersion),
                new(actor.PersonId.Value, actor.AccountId.Value, actor.DisplayLabel,
                    SyntheticWorkflowRole.ItFormalRequester.ToString()),
                now,
                new(policy.AuthorityId, policy.Version, policy.ContentDigest,
                    policy.Selections.Select(item => new PolicySelectionSnapshot(
                        item.OptionCode, item.Classification.ToString()))),
                bindingRevision, sealedDigest, null, null, null, null,
                "IdentityVerification", "verify_identity", Guid.NewGuid().ToString("D")));
            return new(true, "formal_version_sealed", request.CaseId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException)
        {
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        }
        catch
        {
            return Failed(request.CaseId, "command_rejected", request.ActorAccountId);
        }
    }

    public SimulationCommandResult ObserveMandatoryGate(ObserveMandatoryGateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80) || request.FormalVersion < 1
            || !ValidToken(request.ActorAccountId, 160)
            || !string.Equals(request.GateCode, "directory_ready", StringComparison.Ordinal))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var snapshot = RequireExactCurrent(request.CaseId,
                request.ExpectedCaseRevision, request.FormalVersion,
                "IdentityVerification", "Approval", "Intent");
            var actor = _directory.CaptureCurrentRoleActor(
                _directory.Resolve(new(request.ActorAccountId)),
                SyntheticWorkflowRole.ItFormalRequester, new(snapshot.SiteId));
            var version = snapshot.FormalVersions.Single(item =>
                item.Version == request.FormalVersion);
            _directory.ResolveExactEnabledAccount(actor, new(version.Request.PersonId),
                new(version.Request.SiteId), new(version.Request.AccountId));
            var now = ObserveUtc().ToUnixTimeMilliseconds();
            _workflow.AppendGateObservation(new(Guid.NewGuid().ToString("D"),
                request.CaseId, request.ExpectedCaseRevision, request.FormalVersion,
                request.GateCode, "Satisfied", "ready",
                new(actor.PersonId.Value, actor.AccountId.Value, actor.DisplayLabel,
                    SyntheticWorkflowRole.ItFormalRequester.ToString()), now,
                snapshot.CurrentStep, snapshot.RequiredNextActionCode,
                Guid.NewGuid().ToString("D")));
            return new(true, "mandatory_gate_observed", request.CaseId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException)
        {
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        }
        catch
        {
            return Failed(request.CaseId, "command_rejected", request.ActorAccountId);
        }
    }

    public SimulationCommandResult VerifyIdentity(VerifyIdentityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80) || request.FormalVersion < 1
            || !ValidToken(request.ActorAccountId, 160))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var snapshot = RequireExactCurrent(request.CaseId,
                request.ExpectedCaseRevision, request.FormalVersion,
                "IdentityVerification");
            var version = snapshot.FormalVersions.Single(item =>
                item.Version == request.FormalVersion);
            var verifier = _directory.CaptureCurrentRoleActor(
                _directory.Resolve(new(request.ActorAccountId)),
                SyntheticWorkflowRole.HrVerifier, new(version.Request.SiteId));
            if (string.Equals(verifier.PersonId.Value, version.Requester.PersonId,
                    StringComparison.Ordinal)
                || string.Equals(verifier.PersonId.Value, version.Request.PersonId,
                    StringComparison.Ordinal))
                return Failed(request.CaseId, "person_separation_required", request.ActorAccountId);
            var now = ObserveUtc().ToUnixTimeMilliseconds();
            _workflow.AppendIdentityVerification(new(
                Guid.NewGuid().ToString("D"), request.CaseId,
                request.ExpectedCaseRevision, request.FormalVersion,
                version.Request.PersonId,
                new(verifier.PersonId.Value, verifier.AccountId.Value,
                    verifier.DisplayLabel, SyntheticWorkflowRole.HrVerifier.ToString()),
                now, _directory.Revision, "Approval", "approve",
                Guid.NewGuid().ToString("D")));
            return new(true, "identity_verification_recorded", request.CaseId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException) { return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId); }
        catch { return Failed(request.CaseId, "command_rejected", request.ActorAccountId); }
    }

    public SimulationCommandResult ApproveExactVersion(ApproveExactVersionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80) || request.FormalVersion < 1
            || !ValidToken(request.ActorAccountId, 160)
            || request.ObservedFields is null)
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var snapshot = RequireExactCurrent(request.CaseId,
                request.ExpectedCaseRevision, request.FormalVersion, "Approval", "Intent");
            var version = snapshot.FormalVersions.Single(item =>
                item.Version == request.FormalVersion);
            var observed = ToCore(request.ObservedFields);
            observed.Validate();
            var persisted = ToCore(version.Request);
            if (observed != persisted)
                return Failed(request.CaseId, "bound_fields_changed", request.ActorAccountId);
            var verification = snapshot.Verifications.LastOrDefault(item =>
                item.FormalVersion == request.FormalVersion
                && string.Equals(item.SubjectPersonId, version.Request.PersonId,
                    StringComparison.Ordinal));
            if (string.IsNullOrEmpty(verification.AttestationId))
                return Failed(request.CaseId, "identity_verification_required", request.ActorAccountId);
            var prior = snapshot.Approvals
                .Where(item => item.FormalVersion == request.FormalVersion)
                .Select(item => (ApprovalSnapshot?)item)
                .LastOrDefault();
            var approver = _directory.Resolve(new(request.ActorAccountId));
            var subject = _directory.Resolve(new(version.Request.AccountId));
            var approvedAt = ObserveUtc();
            if (string.Equals(snapshot.CurrentStep, "Intent", StringComparison.Ordinal)
                && prior is { } existing
                && existing.InvalidatedUtc is null && existing.RevokedUtc is null
                && approvedAt.ToUnixTimeMilliseconds() < existing.ExpiresUtc)
                return Failed(request.CaseId, "approval_not_required", request.ActorAccountId);
            var approvalId = Guid.NewGuid().ToString("D");
            _directory.CommitApproval(
                new(version.Requester.AccountId), new(version.Requester.PersonId),
                new(verification.Verifier.AccountId), new(verification.Verifier.PersonId),
                approver, subject, persisted, authority =>
                {
                    _workflow.AppendApproval(new(approvalId, request.CaseId,
                        request.ExpectedCaseRevision, request.FormalVersion,
                        new(authority.Approver.PersonId.Value,
                            authority.Approver.AccountId.Value,
                            authority.Approver.DisplayLabel,
                            SyntheticWorkflowRole.HrApprover.ToString()),
                        approvedAt.ToUnixTimeMilliseconds(),
                        approvedAt.Add(FormalRequestLifecycle.ApprovalLifetime)
                            .ToUnixTimeMilliseconds(),
                        version.SealedRequestDigest, version.Policy.AuthorityId,
                        version.Policy.Version, version.Policy.ContentDigest,
                        prior is { } replaceable && replaceable.RevokedUtc is null
                            && replaceable.InvalidatedUtc is null ? replaceable.ApprovalId : null,
                        prior is { } invalidated && invalidated.RevokedUtc is null
                            && invalidated.InvalidatedUtc is null
                                ? approvedAt.ToUnixTimeMilliseconds() : null,
                        prior is { } priorApproval && priorApproval.RevokedUtc is null
                            && priorApproval.InvalidatedUtc is null ? "approval_replaced" : null,
                        "Intent", SimulationIntentTransition.SourceAction,
                        Guid.NewGuid().ToString("D")));
                    return true;
                });
            return new(true, "exact_approval_recorded", request.CaseId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException) { return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId); }
        catch { return Failed(request.CaseId, "command_rejected", request.ActorAccountId); }
    }

    public SimulationCommandResult StartFakeIssuance(StartFakeIssuanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80) || request.FormalVersion < 1
            || !ValidToken(request.ActorAccountId, 160)
            || !ValidToken(request.SyntheticPublicRequestReference, 160))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var snapshot = RequireExactCurrent(request.CaseId,
                request.ExpectedCaseRevision, request.FormalVersion, "Intent");
            var version = snapshot.FormalVersions.Single(item =>
                item.Version == request.FormalVersion);
            var approval = snapshot.Approvals.LastOrDefault(item =>
                item.FormalVersion == request.FormalVersion
                && item.InvalidatedUtc is null && item.RevokedUtc is null
                && item.ConsumedByOperationId is null);
            if (string.IsNullOrEmpty(approval.ApprovalId))
                return Failed(request.CaseId, "exact_approval_required", request.ActorAccountId);
            var verification = snapshot.Verifications.LastOrDefault(item =>
                item.FormalVersion == request.FormalVersion);
            if (string.IsNullOrEmpty(verification.AttestationId))
                return Failed(request.CaseId, "identity_verification_required", request.ActorAccountId);

            var now = ObserveUtc();
            _approvalClock.SetUtc(now);
            var admission = _dispatchAuthority.Capture(request.CaseId,
                request.FormalVersion, approval.ApprovalId,
                request.ExpectedCaseRevision, 1,
                request.SyntheticPublicRequestReference,
                _directory.Resolve(new(version.Requester.AccountId)),
                _directory.Resolve(new(verification.Verifier.AccountId)),
                _directory.Resolve(new(approval.Approver.AccountId)),
                _directory.Resolve(new(request.ActorAccountId)),
                ToCore(version.Request), FromUnix(approval.ApprovedUtc),
                FromUnix(approval.ExpiresUtc));
            var command = new CommitIntentCommand(Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), admission);
            var intent = _runtime.Intent.Commit(command);
            if (!intent.IsDurablyCommitted || intent.Receipt is not { } intentReceipt)
                return Failed(request.CaseId, intent.SafeResultCode, request.ActorAccountId);
            if (intent.Status == IntentCommitStatus.CommittedDispatchBlocked)
                return Failed(request.CaseId, intent.SafeResultCode, request.ActorAccountId);

            var dispatch = _runtime.Recovery.Dispatch(
                new AcceptAuthorityOperationCommand(intentReceipt));
            if (dispatch.Receipt is { } authorityReceipt && !_deferResultPersistence)
            {
                _runtime.Recovery.PersistObservation(new(
                    Guid.NewGuid().ToString("D"), authorityReceipt.OperationId,
                    authorityReceipt.BindingDigest, now.ToUnixTimeMilliseconds()));
            }
            return dispatch.IsAccepted
                ? new(true, dispatch.SafeResultCode, request.CaseId)
                : Failed(request.CaseId, dispatch.SafeResultCode, request.ActorAccountId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch (ArgumentException) { return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId); }
        catch { return Failed(request.CaseId, "command_rejected", request.ActorAccountId); }
    }

    public SimulationCommandResult ObserveFakeIssuance(ObserveFakeIssuanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80) || !Guid.TryParse(request.OperationId, out _)
            || !ValidToken(request.ActorAccountId, 160))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var operation = _workflow.LoadOperation(request.OperationId);
            if (operation is null || !string.Equals(operation.Value.CaseId,
                    request.CaseId, StringComparison.Ordinal))
                return Failed(request.CaseId, "operation_not_found", request.ActorAccountId);
            var snapshot = _workflow.Load(request.CaseId)!;
            _directory.CaptureCurrentRoleActor(
                _directory.Resolve(new(request.ActorAccountId)),
                SyntheticWorkflowRole.ItIssuer, new(snapshot.SiteId));
            if (!string.Equals(operation.Value.State, "IntentCommitted",
                    StringComparison.Ordinal))
                return new(true, "result_observation_already_persisted", request.CaseId);
            var result = _runtime.Recovery.PersistObservation(new(
                Guid.NewGuid().ToString("D"), request.OperationId,
                operation.Value.OperationBindingDigest,
                ObserveUtc().ToUnixTimeMilliseconds()));
            return result.Status is WorkflowResultPersistenceStatus.Persisted
                    or WorkflowResultPersistenceStatus.PersistedAfterReadback
                ? new(true, result.SafeResultCode, request.CaseId)
                : Failed(request.CaseId, result.SafeResultCode, request.ActorAccountId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch { return Failed(request.CaseId, "command_rejected", request.ActorAccountId); }
    }

    public SimulationCommandResult ReconcileFakeIssuance(
        ReconcileFakeIssuanceRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidToken(request.CaseId, 80)
            || !Guid.TryParse(request.FindingId, out _)
            || !Guid.TryParse(request.OperationId, out _)
            || !ValidToken(request.ActorAccountId, 160)
            || !ValidToken(request.ReasonCode, 80))
            return Failed(request.CaseId, "command_input_invalid", request.ActorAccountId);
        try
        {
            var snapshot = _workflow.Load(request.CaseId);
            var operation = _workflow.LoadOperation(request.OperationId);
            if (snapshot is null || operation is null
                || !string.Equals(operation.Value.CaseId, request.CaseId,
                    StringComparison.Ordinal))
                return Failed(request.CaseId, "operation_not_found", request.ActorAccountId);
            var authorization = _directory.AuthorizeReconciliation(
                _directory.Resolve(new(request.ActorAccountId)),
                new(snapshot.SiteId));
            var result = _runtime.Recovery.Reconcile(new(
                Guid.NewGuid().ToString("D"), request.FindingId,
                request.OperationId, authorization,
                ObserveUtc().ToUnixTimeMilliseconds(), request.ReasonCode));
            return result.Applied
                ? new(true, result.SafeResultCode, request.CaseId)
                : Failed(request.CaseId, result.SafeResultCode, request.ActorAccountId);
        }
        catch (InvalidOperationException error)
        {
            return Failed(request.CaseId, SafeCode(error.Message), request.ActorAccountId);
        }
        catch { return Failed(request.CaseId, "command_rejected", request.ActorAccountId); }
    }

    public FakeIssuanceProjection? ReadFakeIssuance(string caseId)
    {
        if (!ValidToken(caseId, 80)) return null;
        var snapshot = _workflow.Load(caseId);
        var operationId = snapshot?.Approvals.LastOrDefault().ConsumedByOperationId;
        if (snapshot is null || operationId is null)
            return snapshot is null ? null : new(caseId, null, "Not committed",
                "Not dispatched", "No result", true, "intent_required", null);
        var operation = _workflow.LoadOperation(operationId);
        if (operation is null)
            return new(caseId, operationId, "Uncertain", "Unknown", "Unknown",
                false, "intent_readback_unreadable", null);
        var state = operation.Value.State;
        var terminal = state is "AcceptedTerminal" or "RejectedBeforeAcceptance" or "Reconciled";
        var finding = _startupRecoveryFindings.SingleOrDefault(item =>
            string.Equals(item.OperationId, operationId, StringComparison.Ordinal));
        return new(caseId, operationId, "Durably committed",
            state is "IntentCommitted" ? "Not yet accepted" : "Dispatch observed",
            terminal ? state : "Pending observation", false,
            terminal ? "terminal_result_recorded" : "same_operation_observation_required",
            finding == default ? null : finding.FindingId);
    }

    public IReadOnlyList<AuditEventProjection> ReadAuditTrail(string caseId)
    {
        if (!ValidToken(caseId, 80)) return [];
        return _workflow.LoadAuditEvents(caseId).Select(item => new AuditEventProjection(
            item.AuditEventId, item.Sequence, FromUnix(item.OccurredUtc),
            item.ActorPersonId ?? "system", item.ActorAccountId ?? "system",
            item.ActorDisplayLabel ?? "System", item.ActorRole ?? "System",
            item.ActionCode, item.AuthorizationDecisionCode, item.OutcomeCode,
            item.FormalVersion, item.ApprovalId, item.OperationId,
            item.PolicyAuthorityId, item.PolicyVersion, item.PolicyContentDigest,
            item.SafeReasonCode ?? "none", item.SafeResultCode ?? "none",
            item.SafeSourceReference ?? "workflow",
            AuditStatePresentationMapper.Map(item.AuthorizationDecisionCode,
                item.OutcomeCode, item.SafeResultCode ?? "none"))).ToArray();
    }

    public IReadOnlyList<DeniedCommandProjection> ReadDeniedCommands(string caseId)
    {
        if (!ValidToken(caseId, 80)) return [];
        return _workflow.LoadCommandDenials(caseId).Select(item =>
            new DeniedCommandProjection(item.Sequence, FromUnix(item.OccurredUtc),
                item.ActorAccountId ?? "unresolved", item.CommandCode,
                item.SafeResultCode)).ToArray();
    }

    public SimulationCounterProjection ReadSimulationCounters() =>
        new(_runtime.Counters.DispatchCalls);

    private WorkflowCaseSnapshot RequireExactCurrent(string caseId, long expectedRevision,
        long version, params string[] steps)
    {
        var snapshot = _workflow.Load(caseId)
            ?? throw new InvalidOperationException("case_not_found");
        if (snapshot.RowRevision != expectedRevision)
            throw new InvalidOperationException("case_revision_stale");
        if (snapshot.CurrentFormalVersion != version
            || snapshot.FormalVersions.All(item => item.Version != version
                || item.SupersededUtc is not null))
            throw new InvalidOperationException("formal_version_superseded");
        if (!steps.Contains(snapshot.CurrentStep, StringComparer.Ordinal))
            throw new InvalidOperationException("workflow_step_mismatch");
        return snapshot;
    }

    private DateTimeOffset ObserveUtc()
    {
        var value = _clock();
        if (value.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("clock_unavailable");
        // Persistence and dispatch authority use Unix-millisecond timestamps.
        // Canonicalize once at the application boundary so a normal host clock's
        // sub-millisecond ticks cannot make an otherwise valid dispatch fail.
        return DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
    }

    private ClockProjection ObserveClock(long? approvedUtc)
    {
        try
        {
            var observed = ObserveUtc();
            if (approvedUtc is { } floor && observed.ToUnixTimeMilliseconds() < floor)
                return new("Moved backward", observed, null);
            return new("Available", observed, null);
        }
        catch
        {
            return new("Unavailable", null, null);
        }
    }

    private static ApprovalState ApprovalStatus(ApprovalSnapshot? approval,
        ClockProjection observed, IReadOnlyList<MandatoryGateProjection> gates)
    {
        if (approval is null) return new("Not approved", "exact_approval_required");
        if (approval.Value.ConsumedByOperationId is not null)
            return new("Consumed by recorded operation", "operation_observation_only");
        if (approval.Value.RevokedUtc is not null) return new("Revoked", "approval_revoked");
        if (approval.Value.InvalidatedUtc is not null) return new("Superseded", "approval_superseded");
        if (observed.ClockStatus == "Unavailable") return new("Clock unavailable", "clock_unavailable");
        if (observed.ClockStatus == "Moved backward") return new("Clock moved backward", "clock_moved_backward");
        var remaining = (approval.Value.ExpiresUtc
            - observed.ObservedAt!.Value.ToUnixTimeMilliseconds()) / 1000;
        observed.RemainingSeconds = Math.Max(0, remaining);
        if (remaining <= 0) return new("Expired", "approval_expired");
        if (gates.Any(item => !item.Satisfied))
            return new("Mandatory gate unresolved", "mandatory_gate_failed");
        return new("Valid", "ready");
    }

    private static FormalRequestFields ToCore(FormalRequestFieldInput value) => new(
        new(value.SiteId), new(value.PersonId), new(value.AccountId), value.IntendedUse,
        value.FakeDeviceId, value.Slot, value.SyntheticPublicRequestDigest,
        value.FakeAuthority, value.Template, value.PolicyOptionsVersion);

    private static FormalRequestFields ToCore(FormalRequestSnapshot value) => new(
        new(value.SiteId), new(value.PersonId), new(value.AccountId), value.IntendedUse,
        value.FakeDeviceId, value.Slot, value.SyntheticPublicRequestDigest,
        value.FakeAuthority, value.Template, value.PolicyOptionsVersion);

    private SimulationCommandResult Failed(string caseId, string code,
        string? actorAccountId = null,
        [CallerMemberName] string caller = "")
    {
        var safeCaseId = ValidToken(caseId, 80) ? caseId : string.Empty;
        if (string.Equals(code, "command_store_unavailable", StringComparison.Ordinal))
            return new(false, code, safeCaseId);
        var safeResultCode = DenialResultCode(code);
        try
        {
            PersistedActor? actor = null;
            if (ValidToken(actorAccountId, 160))
            {
                try
                {
                    var resolved = _directory.Resolve(new(actorAccountId!));
                    actor = new(resolved.PersonId.Value, resolved.AccountId.Value,
                        resolved.DisplayLabel, "AttemptedCommandActor");
                }
                catch
                {
                    // Actor lookup is optional attribution enrichment. Any
                    // unresolved or malformed identity remains anonymous; the
                    // caller-controlled account text is never copied to storage.
                }
            }
            _workflow.AppendCommandDenial(Guid.NewGuid().ToString("D"), actor,
                DenialCommandCode(caller), safeResultCode,
                _clock().ToUnixTimeMilliseconds(),
                safeCaseId.Length == 0 ? null : safeCaseId);
            return new(false, safeResultCode, safeCaseId);
        }
        catch
        {
            // A rejected command is not represented as durably audited when the
            // independent denial ledger could not be appended.
            return new(false, "command_store_unavailable", safeCaseId);
        }
    }

    private static string DenialCommandCode(string caller) => caller switch
    {
        nameof(CreateIntake) => "create_intake",
        nameof(RecordHrCorrection) => "hr_correction",
        nameof(RequestNameChange) => "request_name_change",
        nameof(CompleteNameChange) => "complete_name_change",
        nameof(ResolveWaitingForIt) => "resolve_waiting_for_it",
        nameof(ResolveWaitingForItCore) => "resolve_waiting_for_it",
        nameof(RegisterSimulationAccount) => "register_simulation_account",
        nameof(SealFormalRequest) => "seal_formal_request",
        nameof(ObserveMandatoryGate) => "observe_mandatory_gate",
        nameof(VerifyIdentity) => "verify_identity",
        nameof(ApproveExactVersion) => "approve_exact_version",
        nameof(StartFakeIssuance) => "start_fake_issuance",
        nameof(ObserveFakeIssuance) => "observe_fake_issuance",
        nameof(ReconcileFakeIssuance) => "reconcile_fake_issuance",
        _ => "unknown_application_command"
    };

    private static string DenialResultCode(string code) => code switch
    {
        "command_input_invalid" or "case_not_found" or "case_revision_stale"
            or "account_resolution_not_required" or "formal_version_already_exists"
            or "workflow_step_mismatch" or "bound_fields_changed"
            or "person_separation_required" or "identity_verification_required"
            or "approval_not_required" or "exact_approval_required"
            or "operation_not_found" or "actor_not_authorized"
            or "exact_account_unresolved" or "formal_version_superseded"
            or "clock_unavailable" or "command_rejected"
            or "reconciliation_exact_finding_required"
            or "intent_not_committed" or "intent_commit_uncertain"
            or "intent_committed_dispatch_blocked" => code,
        _ => "command_rejected"
    };

    private static string NormalizeReason(string? value, bool matched)
    {
        if (matched) return null!;
        var normalized = value?.Trim();
        if (!ValidToken(normalized, 160))
            throw new ArgumentException("account_correction_reason_invalid");
        return normalized!;
    }

    private static string SafeCode(string value) => value switch
    {
        "name_change_pending" => "name_change_pending",
        "required_role_missing" => "actor_not_authorized",
        "actor_account_disabled" => "actor_not_authorized",
        "actor_account_state_ambiguous" => "actor_not_authorized",
        "actor_account_state_unknown" => "actor_not_authorized",
        "exact_subject_site_account_unresolved" => "exact_account_unresolved",
        "simulation_workflow_concurrency_conflict" => "case_revision_stale",
        "case_revision_stale" => "case_revision_stale",
        "case_not_found" => "case_not_found",
        "formal_version_superseded" => "formal_version_superseded",
        "workflow_step_mismatch" => "workflow_step_mismatch",
        "bound_fields_changed" => "bound_fields_changed",
        "identity_verification_required" => "identity_verification_required",
        "requester_approver_same_person" => "person_separation_required",
        "subject_disabled" => "exact_account_unresolved",
        "clock_unavailable" => "clock_unavailable",
        _ => "command_rejected"
    };

    private static bool ValidToken(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;

    private SimulationWorkflowProjection Map(WorkflowCaseSnapshot snapshot)
    {
        var current = snapshot.CurrentFormalVersion is { } version
            ? snapshot.FormalVersions.Single(item => item.Version == version)
            : (FormalVersionSnapshot?)null;
        ApprovalSnapshot? approval = current is null
            ? null
            : snapshot.Approvals
                .Where(item => item.FormalVersion == current.Value.Version)
                .Select(item => (ApprovalSnapshot?)item)
                .LastOrDefault();
        var policyApproval = current is null ? null : ReadPolicyApproval(snapshot.CaseId);
        var condition = Condition(snapshot, current, approval, policyApproval);
        var fields = current is null ? Array.Empty<PresentationFact>() : Fields(current.Value.Request);
        var timeline = new List<TimelineProjection>
        {
            new(1, FromUnix(snapshot.IntakeUtc), "Intake recorded",
                snapshot.IntakeActor.AccountId, "HR requester", "Recorded",
                PresentationFactSource.Recorded)
        };
        timeline.AddRange(snapshot.Resolutions.Select((item, index) => new TimelineProjection(
            index + 2L, FromUnix(item.ResolvedUtc), "Account resolution recorded",
            item.Resolver.AccountId, "IT formal requester", "Recorded",
            PresentationFactSource.Recorded)));
        return SimulationWorkflowProjectionMapper.Map(new(
            snapshot.CaseId,
            snapshot.IntakeProfile?.SyntheticDisplayName ?? snapshot.PersonId,
            snapshot.SiteId,
            snapshot.RowRevision,
            condition,
            new(current?.Version, current is null ? "Not sealed" : "Current",
                PresentationFactSource.Recorded, fields),
            new(policyApproval?.ApprovalStatus ?? "Not approved",
                approval?.ApprovalId,
                approval is null ? null : FromUnix(approval.Value.ApprovedUtc),
                approval is null ? null : FromUnix(approval.Value.ExpiresUtc),
                policyApproval?.ObservedAt, PresentationFactSource.Recorded),
            new("Not started", null, 0, 0, null, PresentationFactSource.Recorded),
            timeline,
            snapshot.IntakeProfile?.DepartmentLabel ?? "Unspecified",
            snapshot.PersonId,
            snapshot.Resolutions.Count == 0 ? null : snapshot.Resolutions[^1].AccountId));
    }

    private WorkflowPresentationCondition Condition(
        WorkflowCaseSnapshot snapshot,
        FormalVersionSnapshot? current,
        ApprovalSnapshot? approval,
        PolicyApprovalProjection? policy)
    {
        if (snapshot.CurrentStep == "HrIntake") return WorkflowPresentationCondition.NeedsHrCorrection;
        if (string.Equals(snapshot.CurrentStep, "WaitingForIt", StringComparison.Ordinal))
            return WorkflowPresentationCondition.WaitingForIt;
        if (current is null) return WorkflowPresentationCondition.ReadyForItValidation;
        if (string.Equals(snapshot.CurrentStep, "Dispatch", StringComparison.Ordinal))
        {
            var operationId = approval?.ConsumedByOperationId;
            var operation = operationId is null ? null : _workflow.LoadOperation(operationId);
            return operation?.State is "AcceptedTerminal" or "RejectedBeforeAcceptance" or "Reconciled"
                ? WorkflowPresentationCondition.SimulationJourneyComplete
                : WorkflowPresentationCondition.FakeIssuancePending;
        }
        if (!snapshot.Verifications.Any(item => item.FormalVersion == current.Value.Version))
            return WorkflowPresentationCondition.AwaitingHrVerification;
        if (approval is null) return WorkflowPresentationCondition.AwaitingExactApproval;
        return policy!.SafeStopCode switch
        {
            "approval_expired" => WorkflowPresentationCondition.ApprovalExpired,
            "approval_revoked" => WorkflowPresentationCondition.ApprovalRevoked,
            "approval_superseded" => WorkflowPresentationCondition.ApprovalSuperseded,
            "clock_unavailable" => WorkflowPresentationCondition.ClockUnavailable,
            "clock_moved_backward" => WorkflowPresentationCondition.ClockMovedBackward,
            "mandatory_gate_failed" => WorkflowPresentationCondition.MandatoryGateFailed,
            _ => WorkflowPresentationCondition.ReadyForSimulatedIssuance
        };
    }

    private static PresentationFact[] Fields(FormalRequestSnapshot value) =>
    [
        Fact("Site ID", value.SiteId), Fact("Person ID", value.PersonId),
        Fact("Account ID", value.AccountId), Fact("Intended use", value.IntendedUse),
        Fact("Fake device ID", value.FakeDeviceId), Fact("Slot", value.Slot),
        Fact("Synthetic public-request digest", value.SyntheticPublicRequestDigest),
        Fact("Fake authority", value.FakeAuthority), Fact("Template", value.Template),
        Fact("Policy/options version", value.PolicyOptionsVersion)
    ];

    private static PresentationFact Fact(string label, string value) =>
        new(label, value, PresentationFactSource.Recorded);

    private static DateTimeOffset FromUnix(long value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value);

    private sealed record ApprovalState(string Status, string SafeStopCode);
    private sealed record ClockProjection(
        string ClockStatus, DateTimeOffset? ObservedAt, long? InitialRemainingSeconds)
    {
        internal long? RemainingSeconds { get; set; } = InitialRemainingSeconds;
    }
}
