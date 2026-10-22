using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence;

/// <summary>Typed workflow persistence with no dispatch or generic SQL surface.</summary>
internal sealed class SimulationWorkflowRepository
{
    private readonly SimulationSqliteConnections _connections;
    private readonly SyntheticDispatchAdmissionAuthority? _dispatchAuthority;

    internal object StoreSetToken => _connections.StoreSetToken;

    internal SimulationWorkflowRepository(
        SimulationSqliteConnections connections,
        SyntheticDispatchAdmissionAuthority? dispatchAuthority = null)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _dispatchAuthority = dispatchAuthority;
    }

    internal string AllocateCaseId()
    {
        using var db = _connections.OpenWorkflow();
        Control(db, "BEGIN IMMEDIATE");
        try
        {
            var next = Convert.ToInt64(Scalar(db,
                "SELECT last_value+1 FROM case_number_sequence WHERE singleton_id=1"),
                System.Globalization.CultureInfo.InvariantCulture);
            RequireOne(Execute(db,
                "UPDATE case_number_sequence SET last_value=$next WHERE singleton_id=1",
                ("$next", next)));
            Control(db, "COMMIT");
            return $"SIM-QD-{next:000000}";
        }
        catch { Rollback(db); throw; }
    }

    internal void RecordHrCorrection(string caseId, long expectedRevision, PersistedActor actor,
        long utc, string comment, string? proposedUsername, bool returning) => Write((db,sequence) =>
    {
        var changed = Execute(db, """
UPDATE cases SET current_step=$next, required_next_action_code=$action,row_revision=row_revision+1
WHERE case_id=$case AND row_revision=$revision AND current_formal_version IS NULL
AND NOT EXISTS(SELECT 1 FROM formal_request_versions WHERE case_id=$case)
AND (($returning=1 AND current_step IN ('WaitingForIt','FormalRequest')) OR ($returning=0 AND current_step='HrIntake'))
""", ("$next",returning ? "HrIntake" : "WaitingForIt"),
            ("$action",returning ? "hr_correction_required" : "it_resolve_account"),
            ("$case",caseId),("$revision",expectedRevision),("$returning",returning ? 1 : 0));
        if (changed != 1) throw new InvalidOperationException("simulation_workflow_concurrency_conflict");
        var eventId = Guid.NewGuid().ToString("D");
        AppendAudit(db,sequence,eventId,actor,returning ? "returned_to_hr" : "hr_resubmitted",utc,caseId);
        Execute(db,"INSERT INTO audit_event_fields VALUES($id,'hr_correction_comment',$comment)",
            ("$id",eventId),("$comment",comment));
        if (proposedUsername is not null)
            Execute(db,"INSERT INTO audit_event_fields VALUES($id,'proposed_username',$username)",
                ("$id",eventId),("$username",proposedUsername));
    });

    internal IReadOnlyList<HrCorrectionSnapshot> LoadHrCorrections(string caseId)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db,"""
SELECT a.event_sequence,a.action_code,a.actor_account_id,a.occurred_utc,f.field_value,p.field_value
FROM audit_events a JOIN audit_event_fields f ON f.audit_event_id=a.audit_event_id AND f.field_name='hr_correction_comment'
LEFT JOIN audit_event_fields p ON p.audit_event_id=a.audit_event_id AND p.field_name='proposed_username'
WHERE a.case_id=$case AND a.action_code IN ('returned_to_hr','hr_resubmitted') ORDER BY a.event_sequence
""",("$case",caseId));
        using var reader=command.ExecuteReader();
        var rows=new List<HrCorrectionSnapshot>();
        while(reader.Read()) rows.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
            reader.GetInt64(3),reader.GetString(4),TextOrNull(reader,5)));
        return rows.AsReadOnly();
    }

    internal IReadOnlyList<NameChangeSnapshot> LoadNameChanges()
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, """
SELECT r.request_id,r.case_id,r.person_id,r.site_id,r.account_id,r.old_name,r.requested_name,r.comment,r.actor_account,r.requested_utc,c.actor_account,c.completed_utc,c.comment
FROM name_change_requests r LEFT JOIN name_change_completions c ON c.request_id=r.request_id ORDER BY r.requested_utc,r.request_id
""");
        using var reader = command.ExecuteReader();
        var rows = new List<NameChangeSnapshot>();
        while (reader.Read()) rows.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),
            reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),
            TextOrNull(reader,7),reader.GetString(8),reader.GetInt64(9),TextOrNull(reader,10),
            reader.IsDBNull(11) ? null : reader.GetInt64(11),TextOrNull(reader,12)));
        return rows.AsReadOnly();
    }

    internal void CreateNameChange(NameChangeSnapshot value, long expectedRevision, PersistedActor actor) => Write((db, sequence) =>
    {
        if (Convert.ToInt64(Scalar(db, "SELECT COUNT(*) FROM cases WHERE case_id=$case AND row_revision=$revision",
            ("$case",value.CaseId),("$revision",expectedRevision))) != 1)
            throw new InvalidOperationException("simulation_workflow_concurrency_conflict");
        if (Convert.ToInt64(Scalar(db, """
SELECT COUNT(*) FROM name_change_requests r LEFT JOIN name_change_completions c ON c.request_id=r.request_id
WHERE r.person_id=$person AND r.site_id=$site AND c.request_id IS NULL
""", ("$person",value.PersonId),("$site",value.SiteId))) != 0)
            throw new InvalidOperationException("name_change_pending");
        Execute(db, """
INSERT INTO name_change_requests VALUES($id,$case,$person,$site,$account,$old,$new,$comment,$actor,$utc)
""", ("$id",value.RequestId),("$case",value.CaseId),("$person",value.PersonId),("$site",value.SiteId),
            ("$account",value.AccountId),("$old",value.OldName),("$new",value.RequestedName),
            ("$comment",value.Comment),("$actor",actor.AccountId),("$utc",value.RequestedUtc));
        AppendAudit(db,sequence,Guid.NewGuid().ToString("D"),actor,"name_change_requested",value.RequestedUtc,value.CaseId);
    });

    internal void CompleteNameChange(NameChangeSnapshot value, PersistedActor actor, long utc, string comment) => Write((db,sequence) =>
    {
        Execute(db,"INSERT INTO name_change_completions VALUES($id,$actor,$utc,$comment)",
            ("$id",value.RequestId),("$actor",actor.AccountId),("$utc",utc),("$comment",comment));
        AppendAudit(db,sequence,Guid.NewGuid().ToString("D"),actor,"name_change_simulation_completed",utc,value.CaseId);
    });

    internal IReadOnlyList<DepartmentCatalogueSnapshot> LoadDepartments(bool activeOnly = true)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, activeOnly
            ? "SELECT department_code,display_name,sort_order,is_active FROM department_catalog WHERE is_active=1 ORDER BY sort_order,department_code"
            : "SELECT department_code,display_name,sort_order,is_active FROM department_catalog ORDER BY sort_order,department_code");
        using var reader = command.ExecuteReader();
        var values = new List<DepartmentCatalogueSnapshot>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2), reader.GetInt64(3) == 1));
        return Array.AsReadOnly(values.ToArray());
    }

    internal void CreateIntake(CreateIntakeCommand value) => Write((db, sequence) =>
    {
        Execute(db, """
INSERT INTO cases(case_id,person_id,site_id,intake_actor_person_id,intake_actor_account_id,intake_actor_display_label,intake_utc,current_step,required_next_action_code,current_formal_version,row_revision)
VALUES($case,$person,$site,$actorPerson,$actorAccount,$actorDisplay,$utc,$step,$next,NULL,0)
""", ("$case", value.CaseId), ("$person", value.PersonId), ("$site", value.SiteId),
            ("$actorPerson", value.IntakeActor.PersonId), ("$actorAccount", value.IntakeActor.AccountId),
            ("$actorDisplay", value.IntakeActor.DisplayLabel), ("$utc", value.IntakeUtc),
            ("$step", value.CurrentStep), ("$next", value.RequiredNextActionCode));
        foreach (var candidate in value.CandidateAccounts)
        {
            Execute(db, """
INSERT INTO case_candidate_accounts(case_id,account_id,eligibility_observation_code,fixture_revision)
VALUES($case,$account,$code,$revision)
""", ("$case", value.CaseId), ("$account", candidate.AccountId),
                ("$code", candidate.EligibilityObservationCode), ("$revision", candidate.FixtureRevision));
        }
        if (value.IntakeProfile is { } profile)
        {
            Execute(db, """
INSERT INTO case_intake_profiles(case_id,synthetic_display_name,department_code,department_label,job_title,employee_reference,other_department_review_required)
VALUES($case,$name,$department,$label,$title,$employee,$review)
""", ("$case", value.CaseId), ("$name", profile.SyntheticDisplayName),
                ("$department", profile.DepartmentCode), ("$label", profile.DepartmentLabel),
                ("$title", profile.JobTitle), ("$employee", profile.EmployeeReference),
                ("$review", profile.OtherDepartmentReviewRequired ? 1 : 0));
        }
        AppendAudit(db, sequence, value.AuditEventId, value.IntakeActor,
            SimulationWorkflowAuditCodes.IntakeCreated, value.IntakeUtc, value.CaseId);
    });

    internal void AppendCommandDenial(string eventId, PersistedActor? actor,
        string commandCode, string safeResultCode, long occurredUtc, string? candidateCaseId)
    {
        using var db = _connections.OpenWorkflow();
        Control(db, "BEGIN IMMEDIATE");
        try
        {
            var persistedCase = candidateCaseId is null ? null : Scalar(db,
                "SELECT case_id FROM cases WHERE case_id=$case", ("$case", candidateCaseId));
            var caseId = persistedCase is null or DBNull
                ? null
                : Convert.ToString(persistedCase,
                    System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrEmpty(caseId)) caseId = null;
            Execute(db, """
INSERT INTO command_denial_events(denial_event_id,actor_person_id,actor_account_id,actor_display_label,actor_role,command_code,safe_result_code,occurred_utc,case_id)
VALUES($id,$person,$account,$display,$role,$command,$result,$utc,$case)
""", ("$id", eventId), ("$person", actor?.PersonId), ("$account", actor?.AccountId),
                ("$display", actor?.DisplayLabel), ("$role", actor?.Role),
                ("$command", commandCode), ("$result", safeResultCode),
                ("$utc", occurredUtc), ("$case", caseId));
            Control(db, "COMMIT");
        }
        catch { Rollback(db); throw; }
    }

    internal IReadOnlyList<CommandDenialSnapshot> LoadCommandDenials(string caseId)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, """
SELECT denial_sequence,actor_person_id,actor_account_id,actor_display_label,command_code,safe_result_code,occurred_utc
FROM command_denial_events WHERE case_id=$case ORDER BY denial_sequence
""", ("$case", caseId));
        using var reader = command.ExecuteReader();
        var values = new List<CommandDenialSnapshot>();
        while (reader.Read()) values.Add(new(reader.GetInt64(0), TextOrNull(reader, 1),
            TextOrNull(reader, 2), TextOrNull(reader, 3), reader.GetString(4),
            reader.GetString(5), reader.GetInt64(6)));
        return Array.AsReadOnly(values.ToArray());
    }

    internal void AppendResolution(AppendResolutionCommand value) => Write((db, sequence) =>
    {
        if (!value.SubmittedCandidateMatched)
            RequireReason(value.CorrectionReason, 160, "account_correction_reason_invalid");
        Execute(db, """
INSERT INTO account_resolutions(case_id,resolution_sequence,account_id,resolver_person_id,resolver_account_id,resolver_display_label,directory_revision,submitted_candidate_matched,correction_reason,resolved_utc)
VALUES($case,$sequence,$account,$person,$actorAccount,$display,$revision,$matched,$reason,$utc)
""", ("$case", value.CaseId), ("$sequence", value.ResolutionSequence),
            ("$account", value.AccountId), ("$person", value.Resolver.PersonId),
            ("$actorAccount", value.Resolver.AccountId), ("$display", value.Resolver.DisplayLabel),
            ("$revision", value.DirectoryRevision), ("$matched", value.SubmittedCandidateMatched ? 1 : 0),
            ("$reason", value.CorrectionReason), ("$utc", value.ResolvedUtc));
        AdvanceCase(db, value.CaseId, value.ExpectedCaseRevision, value.NextStep,
            value.RequiredNextActionCode, null, false);
        AppendAudit(db, sequence, value.AuditEventId, value.Resolver,
            SimulationWorkflowAuditCodes.AccountResolutionAppended, value.ResolvedUtc, value.CaseId);
    });

    internal void AppendFormalVersion(AppendFormalVersionCommand value)
    {
        ValidateSealedDigest(value);
        Write((db, sequence) =>
        {
            var current = Scalar(db, "SELECT current_formal_version FROM cases WHERE case_id=$case",
                ("$case", value.CaseId));
            long? currentVersion = current is null or DBNull ? null : Convert.ToInt64(current,
                System.Globalization.CultureInfo.InvariantCulture);
            if (currentVersion != value.SupersedesVersion)
                throw new InvalidOperationException("formal_supersession_current_version_mismatch");
            if (value.SupersedesVersion is not null)
            {
                RequireCurrentFormalVersion(db, value.CaseId, value.SupersedesVersion.Value);
                RequireReason(value.SupersededReasonCode, 80, "formal_supersession_reason_invalid");
                if (value.SupersededUtc is null or < 0 || value.Version <= value.SupersedesVersion.Value)
                    throw new InvalidOperationException("formal_supersession_invalid");
                RequireOne(Execute(db, """
UPDATE formal_request_versions SET superseded_utc=$utc,superseded_reason_code=$reason
WHERE case_id=$case AND version=$version AND superseded_utc IS NULL AND created_utc<=$utc
""", ("$utc", value.SupersededUtc), ("$reason", value.SupersededReasonCode),
                    ("$case", value.CaseId), ("$version", value.SupersedesVersion)));
                Execute(db, """
UPDATE approvals SET invalidated_utc=$utc,invalidation_reason_code=$reason
WHERE case_id=$case AND formal_version=$version
AND invalidated_utc IS NULL AND revoked_utc IS NULL AND consumed_by_operation_id IS NULL
""", ("$utc", value.SupersededUtc), ("$reason", value.SupersededReasonCode),
                    ("$case", value.CaseId), ("$version", value.SupersedesVersion));
            }
            InsertFormalVersion(db, value);
            foreach (var selection in value.Policy.Selections)
            {
                Execute(db, """
INSERT INTO policy_selections(case_id,formal_version,option_code,classification,policy_authority_id,policy_version,policy_content_digest)
VALUES($case,$version,$option,$classification,$authority,$policyVersion,$digest)
""", ("$case", value.CaseId), ("$version", value.Version), ("$option", selection.OptionCode),
                    ("$classification", selection.Classification), ("$authority", value.Policy.AuthorityId),
                    ("$policyVersion", value.Policy.Version), ("$digest", value.Policy.ContentDigest));
            }
            foreach (var requirement in SimulationGateCatalogue.Requirements)
            {
                Execute(db, """
INSERT INTO formal_gate_requirements(case_id,formal_version,gate_code,classification,catalogue_version,policy_authority_id,policy_version,policy_content_digest)
VALUES($case,$version,$gate,$classification,$catalogue,$authority,$policyVersion,$digest)
""", ("$case", value.CaseId), ("$version", value.Version),
                    ("$gate", requirement.GateCode), ("$classification", requirement.Classification.ToString()),
                    ("$catalogue", SimulationGateCatalogue.Version), ("$authority", value.Policy.AuthorityId),
                    ("$policyVersion", value.Policy.Version), ("$digest", value.Policy.ContentDigest));
            }
            if (value.PolicyMigration is { } migration) InsertPolicyMigration(db, value.CaseId, migration);
            AdvanceCase(db, value.CaseId, value.ExpectedCaseRevision, value.NextStep,
                value.RequiredNextActionCode, value.Version, true);
            AppendAudit(db, sequence, value.AuditEventId, value.Requester,
                value.SupersedesVersion is null
                    ? SimulationWorkflowAuditCodes.FormalVersionAppended
                    : SimulationWorkflowAuditCodes.FormalVersionSuperseded,
                value.CreatedUtc, value.CaseId, value.Version, value.Policy);
        });
    }

    internal void AppendGateObservation(AppendGateObservationCommand value) => Write((db, sequence) =>
    {
        RequireCurrentFormalVersion(db, value.CaseId, value.FormalVersion);
        var requirement = ReadGateRequirement(db, value.CaseId, value.FormalVersion, value.GateCode);
        if (requirement.Classification == "Required" && value.ObservedState == "NotApplicable")
            throw new InvalidOperationException("required_gate_not_applicable");
        Execute(db, """
INSERT INTO gate_observations(observation_id,observation_sequence,case_id,formal_version,gate_code,catalogue_version,policy_authority_id,policy_version,policy_content_digest,observed_state,safe_result_code,actor_person_id,actor_account_id,actor_display_label,observed_utc)
VALUES($id,$sequence,$case,$version,$gate,$catalogue,$authority,$policyVersion,$digest,$state,$result,$person,$account,$display,$utc)
""", ("$id", value.ObservationId), ("$sequence", sequence), ("$case", value.CaseId), ("$version", value.FormalVersion),
            ("$gate", value.GateCode), ("$state", value.ObservedState), ("$result", value.SafeResultCode),
            ("$catalogue", requirement.CatalogueVersion),
            ("$authority", requirement.PolicyAuthorityId), ("$policyVersion", requirement.PolicyVersion),
            ("$digest", requirement.PolicyContentDigest),
            ("$person", value.Actor.PersonId), ("$account", value.Actor.AccountId),
            ("$display", value.Actor.DisplayLabel), ("$utc", value.ObservedUtc));
        AdvanceCase(db, value.CaseId, value.ExpectedCaseRevision, value.NextStep,
            value.RequiredNextActionCode, null, false);
        AppendAudit(db, sequence, value.AuditEventId, value.Actor,
            SimulationWorkflowAuditCodes.GateObserved, value.ObservedUtc, value.CaseId,
            value.FormalVersion, safeResultCode: value.SafeResultCode);
    });

    internal void AppendIdentityVerification(AppendIdentityVerificationCommand value) => Write((db, sequence) =>
    {
        RequireCurrentFormalVersion(db, value.CaseId, value.FormalVersion);
        Execute(db, """
INSERT INTO identity_verifications(attestation_id,case_id,formal_version,subject_person_id,verifier_person_id,verifier_account_id,verifier_display_label,verifier_role,observed_utc,fixture_revision)
VALUES($id,$case,$version,$subject,$person,$account,$display,$role,$utc,$revision)
""", ("$id", value.AttestationId), ("$case", value.CaseId), ("$version", value.FormalVersion),
            ("$subject", value.SubjectPersonId), ("$person", value.Verifier.PersonId),
            ("$account", value.Verifier.AccountId), ("$display", value.Verifier.DisplayLabel),
            ("$role", value.Verifier.Role), ("$utc", value.ObservedUtc), ("$revision", value.FixtureRevision));
        AdvanceCase(db, value.CaseId, value.ExpectedCaseRevision, value.NextStep,
            value.RequiredNextActionCode, null, false);
        AppendAudit(db, sequence, value.AuditEventId, value.Verifier,
            SimulationWorkflowAuditCodes.IdentityVerified, value.ObservedUtc, value.CaseId, value.FormalVersion);
    });

    internal void AppendApproval(AppendApprovalCommand value)
    {
        Write((db, sequence) =>
        {
            RequireCurrentFormalVersion(db, value.CaseId, value.FormalVersion);
            var persistedDigest = Convert.ToString(Scalar(db,
                "SELECT sealed_request_digest FROM formal_request_versions WHERE case_id=$case AND version=$version",
                ("$case", value.CaseId), ("$version", value.FormalVersion)),
                System.Globalization.CultureInfo.InvariantCulture);
            if (!FixedEquals(persistedDigest, value.ApprovedRequestDigest))
                throw new InvalidOperationException("approved_request_digest_mismatch");
            if (value.InvalidatesApprovalId is not null)
            {
                RequireOne(Execute(db, """
UPDATE approvals SET invalidated_utc=$utc,invalidation_reason_code=$reason
WHERE approval_id=$id AND case_id=$case AND formal_version=$version
AND invalidated_utc IS NULL AND revoked_utc IS NULL AND consumed_by_operation_id IS NULL
""", ("$utc", value.InvalidatedUtc), ("$reason", value.InvalidationReasonCode),
                    ("$id", value.InvalidatesApprovalId), ("$case", value.CaseId),
                    ("$version", value.FormalVersion)));
            }
            Execute(db, """
INSERT INTO approvals(approval_id,case_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest)
VALUES($id,$case,$version,$person,$account,$display,$role,$utc,$expires,$requestDigest,$authority,$policyVersion,$policyDigest)
""", ("$id", value.ApprovalId), ("$case", value.CaseId), ("$version", value.FormalVersion),
                ("$person", value.Approver.PersonId), ("$account", value.Approver.AccountId),
                ("$display", value.Approver.DisplayLabel), ("$role", value.Approver.Role),
                ("$utc", value.ApprovedUtc), ("$expires", value.ExpiresUtc),
                ("$requestDigest", value.ApprovedRequestDigest), ("$authority", value.PolicyAuthorityId),
                ("$policyVersion", value.PolicyVersion), ("$policyDigest", value.PolicyContentDigest));
            if (value.InvalidatesApprovalId is not null)
            {
                RequireOne(Execute(db,
                    "UPDATE approvals SET replaced_by_approval_id=$replacement WHERE approval_id=$old",
                    ("$replacement", value.ApprovalId), ("$old", value.InvalidatesApprovalId)));
            }
            AdvanceCase(db, value.CaseId, value.ExpectedCaseRevision, value.NextStep,
                value.RequiredNextActionCode, null, false);
            AppendAudit(db, sequence, value.AuditEventId, value.Approver,
                SimulationWorkflowAuditCodes.ApprovalRecorded, value.ApprovedUtc,
                value.CaseId, value.FormalVersion,
                new(value.PolicyAuthorityId, value.PolicyVersion, value.PolicyContentDigest, []),
                value.ApprovalId);
        });
    }

    /// <summary>Commits complete fake-dispatch intent before an adapter may run.</summary>
    internal IntentCommitResult CommitIntent(CommitIntentCommand value, IIntentCommitFault? fault = null)
    {
        try
        {
            if (_dispatchAuthority is null
                || !ReferenceEquals(value.Admission.Authority, _dispatchAuthority))
                throw new InvalidOperationException("dispatch_admission_authority_mismatch");
            var existing = ResolveExistingIntent(value);
            if (existing is { } durable)
            {
                try
                {
                    value.Admission.Revalidate();
                    return durable;
                }
                catch
                {
                    return new(IntentCommitStatus.CommittedDispatchBlocked,
                        "intent_already_committed_dispatch_authority_unavailable",
                        durable.Receipt);
                }
            }
            value.Admission.Revalidate();
            return CommitIntentCore(value, fault);
        }
        catch (InvalidOperationException)
        {
            return new(IntentCommitStatus.NotCommitted, "intent_authority_unavailable", null);
        }
        catch { return new(IntentCommitStatus.Uncertain, "intent_store_unreadable", null); }
    }

    private IntentCommitResult CommitIntentCore(CommitIntentCommand value, IIntentCommitFault? fault)
    {
        fault ??= NoIntentCommitFault.Instance;
        IntentCommitReceipt? expected = null;
        using var db = _connections.OpenWorkflow();
        Control(db, "BEGIN IMMEDIATE");
        try
        {
            var sequence = Convert.ToInt64(Scalar(db,
                "SELECT commit_sequence+1 FROM workflow_metadata WHERE singleton_id=1"),
                System.Globalization.CultureInfo.InvariantCulture);
            var databaseId = Convert.ToString(Scalar(db,
                "SELECT database_instance_id FROM workflow_metadata WHERE singleton_id=1"),
                System.Globalization.CultureInfo.InvariantCulture)!;
            fault.Observe(IntentCommitFaultPoint.BeforeWrites);
            var authority = ReadIntentAuthority(db, value);
            var fields = new FormalRequestFields(new(authority.Request.SiteId),
                new(authority.Request.PersonId), new(authority.Request.AccountId),
                authority.Request.IntendedUse, authority.Request.FakeDeviceId,
                authority.Request.Slot, authority.Request.SyntheticPublicRequestDigest,
                authority.Request.FakeAuthority, authority.Request.Template,
                authority.Request.PolicyOptionsVersion);
            var policy = SimulationPolicySnapshot.CreateClassified(authority.Policy.AuthorityId,
                authority.Policy.Version, authority.Policy.Selections.Select(selection =>
                    new SimulationPolicySelection(selection.OptionCode,
                        ParsePolicyClassification(selection.Classification))));
            if (!FixedEquals(policy.ContentDigest, authority.Policy.ContentDigest))
                throw new InvalidOperationException("policy_content_digest_mismatch");
            var digest = OperationBinding.Compute(new(value.CaseId, value.FormalVersion,
                value.ApprovalId, value.AttemptNumber, fields,
                value.SyntheticPublicRequestReference, policy, []));
            expected = new(value.OperationId, digest, databaseId, sequence, value.Admission);

            Execute(db, """
INSERT INTO operations(operation_id,case_id,formal_version,approval_id,attempt_number,synthetic_public_request_reference,operation_binding_digest,admission_identity_digest,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,workflow_database_instance_id,intent_commit_sequence,state,created_utc,updated_utc,row_revision)
VALUES($operation,$case,$version,$approval,$attempt,$reference,$digest,$admissionDigest,$site,$account,$use,$device,$slot,$authority,$template,$database,$sequence,'IntentCommitted',$utc,$utc,0)
""", ("$operation", value.OperationId), ("$case", value.CaseId),
                ("$version", value.FormalVersion), ("$approval", value.ApprovalId),
                ("$attempt", value.AttemptNumber), ("$reference", value.SyntheticPublicRequestReference),
                ("$digest", digest), ("$admissionDigest", value.Admission.IdentityDigest),
                ("$site", authority.Request.SiteId),
                ("$account", authority.Request.AccountId), ("$use", authority.Request.IntendedUse),
                ("$device", authority.Request.FakeDeviceId), ("$slot", authority.Request.Slot),
                ("$authority", authority.Request.FakeAuthority), ("$template", authority.Request.Template),
                ("$database", databaseId), ("$sequence", sequence), ("$utc", value.ObservedUtc));
            Execute(db, """
INSERT INTO operation_events(event_id,operation_id,event_sequence,state_code,event_code,safe_result_code,safe_reference,observed_utc,source_kind)
VALUES($event,$operation,1,'IntentCommitted','intent_committed','intent_committed',NULL,$utc,'Workflow')
""", ("$event", value.EventId), ("$operation", value.OperationId), ("$utc", value.ObservedUtc));
            Execute(db, """
INSERT INTO workflow_resource_reservations(operation_id,resource_kind,site_id,account_id,intended_use,fake_device_id,slot,reserved_utc)
VALUES($operation,'AccountUse',$site,$account,$use,NULL,NULL,$utc)
""", ("$operation", value.OperationId), ("$site", authority.Request.SiteId),
                ("$account", authority.Request.AccountId), ("$use", authority.Request.IntendedUse),
                ("$utc", value.ObservedUtc));
            Execute(db, """
INSERT INTO workflow_resource_reservations(operation_id,resource_kind,site_id,account_id,intended_use,fake_device_id,slot,reserved_utc)
VALUES($operation,'DeviceSlot',$site,NULL,NULL,$device,$slot,$utc)
""", ("$operation", value.OperationId), ("$site", authority.Request.SiteId),
                ("$device", authority.Request.FakeDeviceId), ("$slot", authority.Request.Slot),
                ("$utc", value.ObservedUtc));
            RequireOne(Execute(db, """
UPDATE approvals SET consumed_by_operation_id=$operation,consumed_utc=$utc
WHERE approval_id=$approval AND case_id=$case AND formal_version=$version
AND approved_utc<=$utc AND $utc<expires_utc AND invalidated_utc IS NULL
AND revoked_utc IS NULL AND consumed_by_operation_id IS NULL
""", ("$operation", value.OperationId), ("$utc", value.ObservedUtc),
                ("$approval", value.ApprovalId), ("$case", value.CaseId),
                ("$version", value.FormalVersion)));
            AdvanceCase(db, value.CaseId, value.ExpectedCaseRevision,
                SimulationIntentTransition.DestinationStep,
                SimulationIntentTransition.DestinationAction, null, false);
            AppendAudit(db, sequence, value.AuditEventId, value.Actor,
                SimulationWorkflowAuditCodes.IntentCommitted, value.ObservedUtc,
                value.CaseId, value.FormalVersion, authority.Policy, value.ApprovalId,
                "intent_committed", value.OperationId);
            RequireOne(Execute(db,
                "UPDATE workflow_metadata SET commit_sequence=$sequence WHERE singleton_id=1",
                ("$sequence", sequence)));
            fault.Observe(IntentCommitFaultPoint.BeforeCommit);
            Control(db, "COMMIT");
            try
            {
                fault.Observe(IntentCommitFaultPoint.AfterCommit);
                return new(IntentCommitStatus.Committed, "intent_committed", expected);
            }
            catch { return ResolveUncertainIntent(value, expected); }
        }
        catch
        {
            Rollback(db);
            return ResolveUncertainIntent(value, expected);
        }
    }

    internal IntentReadback ReadIntent(CommitIntentCommand value, IntentCommitReceipt expected)
    {
        try
        {
            using var db = _connections.OpenWorkflow();
            Control(db, "BEGIN");
            try
            {
                var actual = ReadIntentState(db, value);
                Control(db, "COMMIT");
                if (actual is null) return new(IntentReadbackState.Absent, null, "intent_absent");
                return IntentStateIsExact(actual.Value, value, expected)
                    ? new(IntentReadbackState.Exact, expected, "intent_exact")
                    : new(IntentReadbackState.Mismatch, null, "intent_readback_mismatch");
            }
            catch { Rollback(db); throw; }
        }
        catch { return new(IntentReadbackState.Unreadable, null, "intent_readback_unreadable"); }
    }

    internal OperationSnapshot? LoadOperation(string operationId)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, """
SELECT operation_id,case_id,formal_version,approval_id,operation_binding_digest,
 coalesce((SELECT e.state_code FROM operation_events e
           WHERE e.operation_id=operations.operation_id
           ORDER BY e.event_sequence DESC LIMIT 1),state),row_revision
FROM operations WHERE operation_id=$operation
""", ("$operation", operationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var value = new OperationSnapshot(reader.GetString(0), reader.GetString(1),
            reader.GetInt64(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), reader.GetInt64(6));
        if (reader.Read()) throw new InvalidOperationException("operation_identity_ambiguous");
        return value;
    }

    internal IReadOnlyList<AuditEventSnapshot> LoadAuditEvents(string caseId)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, """
SELECT audit_event_id,event_sequence,actor_person_id,actor_account_id,
 actor_display_label,actor_role,action_code,authorization_decision_code,
 outcome_code,occurred_utc,formal_version,approval_id,operation_id,
 policy_authority_id,policy_version,policy_content_digest,safe_reason_code,
 safe_result_code,safe_source_reference
FROM audit_events WHERE case_id=$case ORDER BY event_sequence
""", ("$case", caseId));
        using var reader = command.ExecuteReader();
        var values = new List<AuditEventSnapshot>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetInt64(1),
            TextOrNull(reader, 2), TextOrNull(reader, 3), TextOrNull(reader, 4),
            TextOrNull(reader, 5), reader.GetString(6), reader.GetString(7),
            reader.GetString(8), reader.GetInt64(9), LongOrNull(reader, 10),
            TextOrNull(reader, 11), TextOrNull(reader, 12), TextOrNull(reader, 13),
            TextOrNull(reader, 14), TextOrNull(reader, 15), TextOrNull(reader, 16),
            TextOrNull(reader, 17), TextOrNull(reader, 18)));
        return values.AsReadOnly();
    }

    private IntentCommitResult ResolveUncertainIntent(CommitIntentCommand value, IntentCommitReceipt? expected)
    {
        if (expected is null) return new(IntentCommitStatus.NotCommitted, "intent_not_committed", null);
        var readback = ReadIntent(value, expected);
        return readback.State switch
        {
            IntentReadbackState.Exact => new(IntentCommitStatus.CommittedAfterReadback,
                "intent_committed_after_readback", readback.Receipt),
            IntentReadbackState.Absent => new(IntentCommitStatus.NotCommitted, "intent_not_committed", null),
            IntentReadbackState.Mismatch => new(IntentCommitStatus.Uncertain, "intent_readback_mismatch", null),
            _ => new(IntentCommitStatus.Uncertain, "intent_readback_unreadable", null)
        };
    }

    private IntentCommitResult? ResolveExistingIntent(CommitIntentCommand value)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, """
SELECT operation_binding_digest,workflow_database_instance_id,intent_commit_sequence
FROM operations WHERE operation_id=$operation
""", ("$operation", value.OperationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var expected = new IntentCommitReceipt(
            value.OperationId, reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            value.Admission);
        reader.Close();
        var readback = ReadIntent(value, expected);
        return readback.State == IntentReadbackState.Exact
            ? new(IntentCommitStatus.CommittedAfterReadback,
                "intent_already_committed", readback.Receipt)
            : new(IntentCommitStatus.Uncertain, "intent_existing_mismatch", null);
    }

    internal WorkflowCaseSnapshot? Load(string caseId)
    {
        using var db = _connections.OpenWorkflow();
        Control(db, "BEGIN");
        try
        {
            using var command = CreateCommand(db, """
SELECT person_id,site_id,intake_actor_person_id,intake_actor_account_id,intake_actor_display_label,
 intake_utc,current_step,required_next_action_code,current_formal_version,row_revision
FROM cases WHERE case_id=$case
""", ("$case", caseId));
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                Control(db, "COMMIT");
                return null;
            }
            var personId = reader.GetString(0); var siteId = reader.GetString(1);
            var intakeActor = new PersistedIdentity(reader.GetString(2), reader.GetString(3), reader.GetString(4));
            var intakeUtc = reader.GetInt64(5); var step = reader.GetString(6); var next = reader.GetString(7);
            long? currentVersion = reader.IsDBNull(8) ? null : reader.GetInt64(8);
            var revision = reader.GetInt64(9);
            reader.Close();
            var result = new WorkflowCaseSnapshot(caseId, personId, siteId, intakeActor, intakeUtc,
                step, next, currentVersion, revision, ReadCandidates(db, caseId), ReadResolutions(db, caseId),
                ReadFormalVersions(db, caseId), ReadPolicyMigrations(db, caseId), ReadGates(db, caseId),
                ReadVerifications(db, caseId), ReadApprovals(db, caseId), ReadIntakeProfile(db, caseId));
            Control(db, "COMMIT");
            return result;
        }
        catch { Rollback(db); throw; }
    }

    internal IReadOnlyList<string> LoadCaseIds()
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db,
            "SELECT case_id FROM cases ORDER BY case_id");
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(reader.GetString(0));
        return Array.AsReadOnly(values.ToArray());
    }

    private static IntakeProfileSnapshot? ReadIntakeProfile(SqliteConnection db, string caseId)
    {
        using var command = CreateCommand(db, """
SELECT synthetic_display_name,department_code,department_label,job_title,employee_reference,other_department_review_required
FROM case_intake_profiles WHERE case_id=$case
""", ("$case", caseId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            TextOrNull(reader, 3), TextOrNull(reader, 4), reader.GetInt64(5) == 1);
    }

    internal IReadOnlyList<GateRequirementSnapshot> LoadGateRequirements(
        string caseId, long formalVersion)
    {
        using var db = _connections.OpenWorkflow();
        using var command = CreateCommand(db, """
SELECT gate_code,classification,catalogue_version,policy_authority_id,policy_version,policy_content_digest
FROM formal_gate_requirements WHERE case_id=$case AND formal_version=$version ORDER BY gate_code
""", ("$case", caseId), ("$version", formalVersion));
        using var reader = command.ExecuteReader();
        var values = new List<GateRequirementSnapshot>();
        while (reader.Read()) values.Add(new(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5)));
        return Array.AsReadOnly(values.ToArray());
    }

    private void Write(Action<SqliteConnection, long> body)
    {
        using var db = _connections.OpenWorkflow();
        Control(db, "BEGIN IMMEDIATE");
        try
        {
            var sequence = Convert.ToInt64(Scalar(db,
                "SELECT commit_sequence+1 FROM workflow_metadata WHERE singleton_id=1"),
                System.Globalization.CultureInfo.InvariantCulture);
            body(db, sequence);
            RequireOne(Execute(db,
                "UPDATE workflow_metadata SET commit_sequence=$sequence WHERE singleton_id=1",
                ("$sequence", sequence)));
            Control(db, "COMMIT");
        }
        catch { Rollback(db); throw; }
    }

    private static void ValidateSealedDigest(AppendFormalVersionCommand value)
    {
        var fields = new FormalRequestFields(new(value.Request.SiteId), new(value.Request.PersonId),
            new(value.Request.AccountId), value.Request.IntendedUse, value.Request.FakeDeviceId,
            value.Request.Slot, value.Request.SyntheticPublicRequestDigest, value.Request.FakeAuthority,
            value.Request.Template, value.Request.PolicyOptionsVersion);
        var policy = SimulationPolicySnapshot.CreateClassified(
            value.Policy.AuthorityId,
            value.Policy.Version,
            value.Policy.Selections.Select(selection => new SimulationPolicySelection(
                selection.OptionCode,
                ParsePolicyClassification(selection.Classification))));
        if (!FixedEquals(policy.ContentDigest, value.Policy.ContentDigest))
            throw new InvalidOperationException("policy_content_digest_mismatch");
        var computed = SealedRequestBinding.Compute(fields, policy, value.OperationBindingInputRevision);
        if (!string.Equals(computed, value.SealedRequestDigest, StringComparison.Ordinal))
            throw new InvalidOperationException("sealed_request_digest_mismatch");
    }

    private static SimulationPolicyOptionClassification ParsePolicyClassification(string value) => value switch
    {
        "Required" => SimulationPolicyOptionClassification.Required,
        "OptionalSelected" => SimulationPolicyOptionClassification.OptionalSelected,
        "NotSelected" => SimulationPolicyOptionClassification.NotSelected,
        _ => throw new InvalidOperationException("policy_option_classification_invalid")
    };

    private static void RequireReason(string? reason, int maximumLength, string code)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > maximumLength)
            throw new ArgumentException(code);
    }

    private static void RequireCurrentFormalVersion(SqliteConnection db, string caseId, long version)
    {
        var current = Scalar(db, """
SELECT 1 FROM cases AS c JOIN formal_request_versions AS f
ON f.case_id=c.case_id AND f.version=c.current_formal_version
WHERE c.case_id=$case AND f.version=$version AND f.superseded_utc IS NULL
""", ("$case", caseId), ("$version", version));
        if (current is null)
            throw new InvalidOperationException("formal_version_not_current");
    }

    private static bool FixedEquals(string? first, string second)
    {
        if (first is null || first.Length != second.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(first), System.Text.Encoding.ASCII.GetBytes(second));
    }

    private static GateRequirementIdentity ReadGateRequirement(
        SqliteConnection db, string caseId, long formalVersion, string gateCode)
    {
        using var command = CreateCommand(db, """
SELECT classification,catalogue_version,policy_authority_id,policy_version,policy_content_digest
FROM formal_gate_requirements WHERE case_id=$case AND formal_version=$version AND gate_code=$gate
""", ("$case", caseId), ("$version", formalVersion), ("$gate", gateCode));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("formal_gate_requirement_not_found");
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4));
    }

    private readonly record struct GateRequirementIdentity(
        string Classification, string CatalogueVersion, string PolicyAuthorityId,
        string PolicyVersion, string PolicyContentDigest);

    private readonly record struct IntentAuthority(FormalRequestSnapshot Request, PolicySnapshot Policy);

    private static IntentAuthority ReadIntentAuthority(SqliteConnection db, CommitIntentCommand value)
    {
        using var command = CreateCommand(db, """
SELECT f.site_id,f.person_id,f.account_id,f.intended_use,f.fake_device_id,f.slot,
 f.synthetic_public_request_digest,f.fake_authority,f.template,f.policy_options_version,
 f.policy_authority_id,f.policy_version,f.policy_content_digest,f.sealed_request_digest,
 a.approved_request_digest,a.policy_authority_id,a.policy_version,a.policy_content_digest
FROM cases AS c
JOIN formal_request_versions AS f ON f.case_id=c.case_id AND f.version=c.current_formal_version
JOIN approvals AS a ON a.case_id=f.case_id AND a.formal_version=f.version
WHERE c.case_id=$case AND c.row_revision=$revision AND f.version=$version
 AND c.current_step=$sourceStep AND c.required_next_action_code=$sourceAction
 AND f.superseded_utc IS NULL AND a.approval_id=$approval
 AND a.approved_utc<=$utc AND $utc<a.expires_utc
 AND a.approved_utc=$approvedUtc AND a.expires_utc=$expiresUtc
 AND f.requester_person_id=$requesterPerson AND f.requester_account_id=$requesterAccount
 AND a.approver_person_id=$approverPerson AND a.approver_account_id=$approverAccount
 AND EXISTS(SELECT 1 FROM identity_verifications AS verification
   WHERE verification.case_id=f.case_id AND verification.formal_version=f.version
    AND verification.subject_person_id=f.person_id
    AND verification.verifier_person_id=$verifierPerson
    AND verification.verifier_account_id=$verifierAccount)
 AND a.invalidated_utc IS NULL AND a.revoked_utc IS NULL
 AND a.consumed_by_operation_id IS NULL AND a.replaced_by_approval_id IS NULL
""", ("$case", value.CaseId), ("$revision", value.ExpectedCaseRevision),
            ("$version", value.FormalVersion), ("$approval", value.ApprovalId),
            ("$utc", value.ObservedUtc), ("$approvedUtc", value.ApprovedUtc),
            ("$expiresUtc", value.ExpiresUtc),
            ("$sourceStep", SimulationIntentTransition.SourceStep),
            ("$sourceAction", SimulationIntentTransition.SourceAction),
            ("$requesterPerson", value.Admission.Requester.PersonId.Value),
            ("$requesterAccount", value.Admission.Requester.AccountId.Value),
            ("$verifierPerson", value.Admission.Verifier.PersonId.Value),
            ("$verifierAccount", value.Admission.Verifier.AccountId.Value),
            ("$approverPerson", value.Admission.Approver.PersonId.Value),
            ("$approverAccount", value.Admission.Approver.AccountId.Value));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("intent_authority_unavailable");
        var request = new FormalRequestSnapshot(reader.GetString(0), reader.GetString(1),
            reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9));
        var admitted = value.Admission.Fields;
        if (!string.Equals(request.SiteId, admitted.SiteId.Value, StringComparison.Ordinal)
            || !string.Equals(request.PersonId, admitted.PersonId.Value, StringComparison.Ordinal)
            || !string.Equals(request.AccountId, admitted.AccountId.Value, StringComparison.Ordinal)
            || !string.Equals(request.IntendedUse, admitted.IntendedUse, StringComparison.Ordinal)
            || !string.Equals(request.FakeDeviceId, admitted.FakeDeviceId, StringComparison.Ordinal)
            || !string.Equals(request.Slot, admitted.Slot, StringComparison.Ordinal)
            || !FixedEquals(request.SyntheticPublicRequestDigest, admitted.SyntheticPublicRequestDigest)
            || !string.Equals(request.FakeAuthority, admitted.FakeAuthority, StringComparison.Ordinal)
            || !string.Equals(request.Template, admitted.Template, StringComparison.Ordinal)
            || !string.Equals(request.PolicyOptionsVersion, admitted.PolicyOptionsVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("dispatch_admission_request_mismatch");
        var policyAuthority = reader.GetString(10);
        var policyVersion = reader.GetString(11);
        var policyDigest = reader.GetString(12);
        if (!FixedEquals(reader.GetString(13), reader.GetString(14))
            || !string.Equals(policyAuthority, reader.GetString(15), StringComparison.Ordinal)
            || !string.Equals(policyVersion, reader.GetString(16), StringComparison.Ordinal)
            || !FixedEquals(policyDigest, reader.GetString(17)))
            throw new InvalidOperationException("approval_binding_mismatch");
        reader.Close();

        var selections = new List<PolicySelectionSnapshot>();
        using (var selectionCommand = CreateCommand(db, """
SELECT option_code,classification FROM policy_selections
WHERE case_id=$case AND formal_version=$version
 AND policy_authority_id=$authority AND policy_version=$policyVersion
 AND policy_content_digest=$digest ORDER BY option_code
""", ("$case", value.CaseId), ("$version", value.FormalVersion),
            ("$authority", policyAuthority), ("$policyVersion", policyVersion),
            ("$digest", policyDigest)))
        using (var selectionReader = selectionCommand.ExecuteReader())
            while (selectionReader.Read())
                selections.Add(new(selectionReader.GetString(0), selectionReader.GetString(1)));

        var unresolved = Convert.ToInt64(Scalar(db, """
SELECT count(*) FROM formal_gate_requirements AS requirement
WHERE requirement.case_id=$case AND requirement.formal_version=$version
 AND requirement.classification='Required'
 AND NOT EXISTS(
   SELECT 1 FROM gate_observations AS observation
   WHERE observation.case_id=requirement.case_id
    AND observation.formal_version=requirement.formal_version
    AND observation.gate_code=requirement.gate_code
    AND observation.catalogue_version=requirement.catalogue_version
    AND observation.policy_authority_id=requirement.policy_authority_id
    AND observation.policy_version=requirement.policy_version
    AND observation.policy_content_digest=requirement.policy_content_digest
    AND observation.observation_sequence=(
      SELECT max(latest.observation_sequence) FROM gate_observations AS latest
      WHERE latest.case_id=requirement.case_id
       AND latest.formal_version=requirement.formal_version
       AND latest.gate_code=requirement.gate_code
       AND latest.catalogue_version=requirement.catalogue_version
       AND latest.policy_authority_id=requirement.policy_authority_id
       AND latest.policy_version=requirement.policy_version
       AND latest.policy_content_digest=requirement.policy_content_digest)
    AND observation.observed_state='Satisfied')
""", ("$case", value.CaseId), ("$version", value.FormalVersion)),
            System.Globalization.CultureInfo.InvariantCulture);
        if (unresolved != 0) throw new InvalidOperationException("required_gate_unresolved");
        return new(request, new(policyAuthority, policyVersion, policyDigest, selections));
    }

    private readonly record struct StoredIntentState(
        string Digest, string DatabaseId, long Sequence, string CaseId, long FormalVersion,
        string ApprovalId, long Attempt, string Reference, long ApprovalConsumed,
        long AccountReservations, long DeviceReservations, long IntentEvents, long IntentAudits,
        long OperationAuthorityExact, long AdmissionAuthorityExact,
        long CaseAdvancedExact, long MetadataSequence);

    private static StoredIntentState? ReadIntentState(SqliteConnection db, CommitIntentCommand value)
    {
        using var command = CreateCommand(db, """
SELECT o.operation_binding_digest,o.workflow_database_instance_id,o.intent_commit_sequence,
 o.case_id,o.formal_version,o.approval_id,o.attempt_number,o.synthetic_public_request_reference,
 (SELECT count(*) FROM approvals a WHERE a.approval_id=o.approval_id
   AND a.consumed_by_operation_id=o.operation_id AND a.consumed_utc=o.created_utc),
 (SELECT count(*) FROM workflow_resource_reservations r WHERE r.operation_id=o.operation_id
   AND r.resource_kind='AccountUse' AND r.site_id=o.target_site_id
   AND r.account_id=o.target_account_id AND r.intended_use=o.intended_use
   AND r.reserved_utc=o.created_utc AND r.released_utc IS NULL),
 (SELECT count(*) FROM workflow_resource_reservations r WHERE r.operation_id=o.operation_id
   AND r.resource_kind='DeviceSlot' AND r.site_id=o.target_site_id
   AND r.fake_device_id=o.fake_device_id AND r.slot=o.slot
   AND r.reserved_utc=o.created_utc AND r.released_utc IS NULL),
 (SELECT count(*) FROM operation_events e WHERE e.operation_id=o.operation_id
   AND e.event_id=$event AND e.event_sequence=1 AND e.state_code='IntentCommitted'
   AND e.event_code='intent_committed' AND e.safe_result_code='intent_committed'
   AND e.safe_reference IS NULL AND e.observed_utc=o.created_utc AND e.source_kind='Workflow'),
 (SELECT count(*) FROM audit_events ae WHERE ae.operation_id=o.operation_id
   AND ae.audit_event_id=$audit AND ae.event_sequence=o.intent_commit_sequence
   AND ae.actor_person_id=$actorPerson AND ae.actor_account_id=$actorAccount
   AND ae.actor_display_label=$actorDisplay AND ae.actor_role=$actorRole
   AND ae.action_code='intent_committed' AND ae.authorization_decision_code='authorized'
   AND ae.outcome_code='recorded' AND ae.occurred_utc=o.created_utc
   AND ae.case_id=o.case_id AND ae.formal_version=o.formal_version
   AND ae.approval_id=o.approval_id AND ae.policy_authority_id=f.policy_authority_id
   AND ae.policy_version=f.policy_version AND ae.policy_content_digest=f.policy_content_digest
   AND ae.safe_reason_code IS NULL AND ae.safe_result_code='intent_committed'
   AND ae.safe_source_reference IS NULL),
        CASE WHEN o.target_site_id=f.site_id AND o.target_account_id=f.account_id
   AND f.person_id=$subjectPerson
   AND f.synthetic_public_request_digest=$requestDigest
   AND f.policy_options_version=$optionsVersion
   AND o.intended_use=f.intended_use AND o.fake_device_id=f.fake_device_id
   AND o.slot=f.slot AND o.authority_id=f.fake_authority AND o.template=f.template
   AND o.created_utc=$utc AND o.updated_utc=o.created_utc AND o.row_revision=0
   THEN 1 ELSE 0 END,
 CASE WHEN f.requester_person_id=$requesterPerson
   AND f.requester_account_id=$requesterAccount
   AND EXISTS(SELECT 1 FROM identity_verifications verification
     WHERE verification.case_id=o.case_id AND verification.formal_version=o.formal_version
      AND verification.subject_person_id=f.person_id
      AND verification.verifier_person_id=$verifierPerson
      AND verification.verifier_account_id=$verifierAccount)
   AND EXISTS(SELECT 1 FROM approvals originalApproval
     WHERE originalApproval.approval_id=o.approval_id
      AND originalApproval.case_id=o.case_id
      AND originalApproval.formal_version=o.formal_version
      AND originalApproval.approver_person_id=$approverPerson
      AND originalApproval.approver_account_id=$approverAccount
      AND originalApproval.approved_utc=$approvedUtc
      AND originalApproval.expires_utc=$expiresUtc)
   THEN 1 ELSE 0 END,
 (SELECT count(*) FROM cases c WHERE c.case_id=o.case_id
   AND c.current_formal_version=o.formal_version AND c.row_revision=$expectedRevision+1
   AND c.current_step=$step AND c.required_next_action_code=$next),
 (SELECT commit_sequence FROM workflow_metadata WHERE singleton_id=1)
FROM operations o JOIN formal_request_versions f
 ON f.case_id=o.case_id AND f.version=o.formal_version
WHERE o.operation_id=$operation AND o.state='IntentCommitted'
""", ("$operation", value.OperationId), ("$event", value.EventId),
            ("$audit", value.AuditEventId), ("$actorPerson", value.Actor.PersonId),
            ("$actorAccount", value.Actor.AccountId), ("$actorDisplay", value.Actor.DisplayLabel),
            ("$actorRole", value.Actor.Role), ("$utc", value.ObservedUtc),
            ("$requesterPerson", value.Admission.Requester.PersonId.Value),
            ("$requesterAccount", value.Admission.Requester.AccountId.Value),
            ("$verifierPerson", value.Admission.Verifier.PersonId.Value),
            ("$verifierAccount", value.Admission.Verifier.AccountId.Value),
            ("$approverPerson", value.Admission.Approver.PersonId.Value),
            ("$approverAccount", value.Admission.Approver.AccountId.Value),
            ("$subjectPerson", value.Admission.Fields.PersonId.Value),
            ("$requestDigest", value.Admission.Fields.SyntheticPublicRequestDigest),
            ("$optionsVersion", value.Admission.Fields.PolicyOptionsVersion),
            ("$approvedUtc", value.ApprovedUtc), ("$expiresUtc", value.ExpiresUtc),
            ("$expectedRevision", value.ExpectedCaseRevision),
            ("$step", SimulationIntentTransition.DestinationStep),
            ("$next", SimulationIntentTransition.DestinationAction));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2),
            reader.GetString(3), reader.GetInt64(4), reader.GetString(5), reader.GetInt64(6),
            reader.GetString(7), reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10),
            reader.GetInt64(11), reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14),
            reader.GetInt64(15), reader.GetInt64(16));
    }

    private static bool IntentStateIsExact(StoredIntentState actual, CommitIntentCommand command,
        IntentCommitReceipt expected) =>
        string.Equals(expected.OperationId, command.OperationId, StringComparison.Ordinal)
        && FixedEquals(actual.Digest, expected.OperationBindingDigest)
        && string.Equals(actual.DatabaseId, expected.WorkflowDatabaseInstanceId, StringComparison.Ordinal)
        && actual.Sequence == expected.IntentCommitSequence
        && string.Equals(actual.CaseId, command.CaseId, StringComparison.Ordinal)
        && actual.FormalVersion == command.FormalVersion
        && string.Equals(actual.ApprovalId, command.ApprovalId, StringComparison.Ordinal)
        && actual.Attempt == command.AttemptNumber
        && string.Equals(actual.Reference, command.SyntheticPublicRequestReference, StringComparison.Ordinal)
        && actual.ApprovalConsumed == 1 && actual.AccountReservations == 1
        && actual.DeviceReservations == 1 && actual.IntentEvents == 1 && actual.IntentAudits == 1
        && actual.OperationAuthorityExact == 1 && actual.AdmissionAuthorityExact == 1
        && actual.CaseAdvancedExact == 1
        && actual.MetadataSequence >= expected.IntentCommitSequence;

    private static void InsertFormalVersion(SqliteConnection db, AppendFormalVersionCommand value) =>
        Execute(db, """
INSERT INTO formal_request_versions(case_id,version,site_id,person_id,account_id,intended_use,fake_device_id,slot,synthetic_public_request_digest,fake_authority,template,policy_options_version,requester_person_id,requester_account_id,requester_display_label,created_utc,policy_authority_id,policy_version,policy_content_digest,operation_binding_input_revision,sealed_request_digest)
VALUES($case,$version,$site,$person,$account,$use,$device,$slot,$public,$authority,$template,$options,$rp,$ra,$rd,$utc,$pa,$pv,$pd,$revision,$sealed)
""", ("$case", value.CaseId), ("$version", value.Version), ("$site", value.Request.SiteId),
            ("$person", value.Request.PersonId), ("$account", value.Request.AccountId),
            ("$use", value.Request.IntendedUse), ("$device", value.Request.FakeDeviceId),
            ("$slot", value.Request.Slot), ("$public", value.Request.SyntheticPublicRequestDigest),
            ("$authority", value.Request.FakeAuthority), ("$template", value.Request.Template),
            ("$options", value.Request.PolicyOptionsVersion), ("$rp", value.Requester.PersonId),
            ("$ra", value.Requester.AccountId), ("$rd", value.Requester.DisplayLabel),
            ("$utc", value.CreatedUtc), ("$pa", value.Policy.AuthorityId),
            ("$pv", value.Policy.Version), ("$pd", value.Policy.ContentDigest),
            ("$revision", value.OperationBindingInputRevision), ("$sealed", value.SealedRequestDigest));

    private static void InsertPolicyMigration(SqliteConnection db, string caseId, PolicyMigrationSnapshot value) =>
        Execute(db, """
INSERT INTO policy_migrations(migration_id,case_id,source_formal_version,replacement_formal_version,source_policy_authority_id,source_policy_version,source_policy_content_digest,replacement_policy_authority_id,replacement_policy_version,replacement_policy_content_digest,actor_person_id,actor_account_id,actor_display_label,actor_role,migrated_utc,reason_code)
VALUES($id,$case,$source,$replacement,$spa,$spv,$spd,$rpa,$rpv,$rpd,$person,$account,$display,$role,$utc,$reason)
""", ("$id", value.MigrationId), ("$case", caseId), ("$source", value.SourceFormalVersion),
            ("$replacement", value.ReplacementFormalVersion), ("$spa", value.SourcePolicyAuthorityId),
            ("$spv", value.SourcePolicyVersion), ("$spd", value.SourcePolicyContentDigest),
            ("$rpa", value.ReplacementPolicyAuthorityId), ("$rpv", value.ReplacementPolicyVersion),
            ("$rpd", value.ReplacementPolicyContentDigest), ("$person", value.Actor.PersonId),
            ("$account", value.Actor.AccountId), ("$display", value.Actor.DisplayLabel),
            ("$role", value.Actor.Role), ("$utc", value.MigratedUtc), ("$reason", value.ReasonCode));

    private static void AdvanceCase(SqliteConnection db, string caseId, long expectedRevision,
        string step, string next, long? version, bool replaceVersion)
    {
        var sql = replaceVersion
            ? "UPDATE cases SET current_step=$step,required_next_action_code=$next,current_formal_version=$version,row_revision=row_revision+1 WHERE case_id=$case AND row_revision=$expected"
            : "UPDATE cases SET current_step=$step,required_next_action_code=$next,row_revision=row_revision+1 WHERE case_id=$case AND row_revision=$expected";
        RequireOne(Execute(db, sql, ("$step", step), ("$next", next), ("$version", version),
            ("$case", caseId), ("$expected", expectedRevision)));
    }

    private static void AppendAudit(SqliteConnection db, long sequence, string eventId,
        PersistedActor actor, string action, long occurredUtc, string caseReference,
        long? formalVersion = null, PolicySnapshot? policy = null, string? approvalId = null,
        string? safeResultCode = null, string? operationId = null)
    {
        Execute(db, """
INSERT INTO audit_events(audit_event_id,event_sequence,actor_person_id,actor_account_id,actor_display_label,actor_role,action_code,authorization_decision_code,outcome_code,occurred_utc,case_id,formal_version,approval_id,operation_id,policy_authority_id,policy_version,policy_content_digest,safe_result_code,safe_source_reference)
VALUES($id,$sequence,$person,$account,$display,$role,$action,$decision,$outcome,$utc,$case,$version,$approval,$operation,$pa,$pv,$pd,$result,$reference)
""", ("$id", eventId), ("$sequence", sequence), ("$person", actor.PersonId),
            ("$account", actor.AccountId), ("$display", actor.DisplayLabel), ("$role", actor.Role),
            ("$action", action), ("$decision", SimulationWorkflowAuditCodes.Authorized),
            ("$outcome", SimulationWorkflowAuditCodes.Recorded), ("$utc", occurredUtc),
            ("$case", caseReference), ("$version", formalVersion),
            ("$approval", approvalId), ("$operation", operationId),
            ("$pa", policy?.AuthorityId), ("$pv", policy?.Version),
            ("$pd", policy?.ContentDigest), ("$result", safeResultCode),
            ("$reference", null));
    }

    private static List<CandidateAccountSnapshot> ReadCandidates(SqliteConnection db, string caseId) =>
        Read<CandidateAccountSnapshot>(db, "SELECT account_id,eligibility_observation_code,fixture_revision FROM case_candidate_accounts WHERE case_id=$case ORDER BY account_id",
            caseId, r => new(r.GetString(0), r.GetString(1), r.GetInt64(2)));

    private static List<ResolutionSnapshot> ReadResolutions(SqliteConnection db, string caseId) =>
        Read<ResolutionSnapshot>(db, "SELECT resolution_sequence,account_id,resolver_person_id,resolver_account_id,resolver_display_label,directory_revision,submitted_candidate_matched,correction_reason,resolved_utc FROM account_resolutions WHERE case_id=$case ORDER BY resolution_sequence",
            caseId, r => new(r.GetInt64(0), r.GetString(1),
                new(r.GetString(2), r.GetString(3), r.GetString(4)),
                r.GetInt64(5), r.GetInt64(6) == 1, TextOrNull(r, 7), r.GetInt64(8)));

    private static List<FormalVersionSnapshot> ReadFormalVersions(SqliteConnection db, string caseId)
    {
        var selections = Read<(long Version, PolicySelectionSnapshot Selection)>(db,
            "SELECT formal_version,option_code,classification FROM policy_selections WHERE case_id=$case ORDER BY formal_version,option_code",
            caseId, r => (Version: r.GetInt64(0), Selection: new PolicySelectionSnapshot(r.GetString(1), r.GetString(2))))
            .GroupBy(x => x.Version).ToDictionary(x => x.Key, x => (IEnumerable<PolicySelectionSnapshot>)x.Select(y => y.Selection).ToArray());
        return Read<FormalVersionSnapshot>(db, "SELECT version,site_id,person_id,account_id,intended_use,fake_device_id,slot,synthetic_public_request_digest,fake_authority,template,policy_options_version,requester_person_id,requester_account_id,requester_display_label,created_utc,policy_authority_id,policy_version,policy_content_digest,operation_binding_input_revision,sealed_request_digest,superseded_utc,superseded_reason_code FROM formal_request_versions WHERE case_id=$case ORDER BY version",
            caseId, r => new(r.GetInt64(0),
                new(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5),
                    r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10)),
                new(r.GetString(11), r.GetString(12), r.GetString(13)), r.GetInt64(14),
                new(r.GetString(15), r.GetString(16), r.GetString(17), selections.GetValueOrDefault(r.GetInt64(0), [])),
                r.GetInt64(18), r.GetString(19), LongOrNull(r, 20), TextOrNull(r, 21)));
    }

    private static List<PolicyMigrationSnapshot> ReadPolicyMigrations(SqliteConnection db, string caseId) =>
        Read<PolicyMigrationSnapshot>(db, "SELECT migration_id,source_formal_version,replacement_formal_version,source_policy_authority_id,source_policy_version,source_policy_content_digest,replacement_policy_authority_id,replacement_policy_version,replacement_policy_content_digest,actor_person_id,actor_account_id,actor_display_label,actor_role,migrated_utc,reason_code FROM policy_migrations WHERE case_id=$case ORDER BY replacement_formal_version",
            caseId, r => new(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4),
                r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8),
                new(r.GetString(9), r.GetString(10), r.GetString(11), r.GetString(12)), r.GetInt64(13), r.GetString(14)));

    private static List<GateObservationSnapshot> ReadGates(SqliteConnection db, string caseId) =>
        Read<GateObservationSnapshot>(db, "SELECT observation_id,observation_sequence,formal_version,gate_code,observed_state,safe_result_code,actor_person_id,actor_account_id,actor_display_label,observed_utc FROM gate_observations WHERE case_id=$case ORDER BY observation_sequence",
            caseId, r => new(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5),
                new(r.GetString(6), r.GetString(7), r.GetString(8)), r.GetInt64(9)));

    private static List<IdentityVerificationSnapshot> ReadVerifications(SqliteConnection db, string caseId) =>
        Read<IdentityVerificationSnapshot>(db, "SELECT attestation_id,formal_version,subject_person_id,verifier_person_id,verifier_account_id,verifier_display_label,verifier_role,observed_utc,fixture_revision FROM identity_verifications WHERE case_id=$case ORDER BY observed_utc,attestation_id",
            caseId, r => new(r.GetString(0), r.GetInt64(1), r.GetString(2),
                new(r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)), r.GetInt64(7), r.GetInt64(8)));

    private static List<ApprovalSnapshot> ReadApprovals(SqliteConnection db, string caseId) =>
        Read<ApprovalSnapshot>(db, "SELECT approval_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest,invalidated_utc,invalidation_reason_code,replaced_by_approval_id,revoked_utc,revocation_reason_code,consumed_by_operation_id,consumed_utc FROM approvals WHERE case_id=$case ORDER BY approved_utc,approval_id",
            caseId, r => new(r.GetString(0), r.GetInt64(1), new(r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5)),
                r.GetInt64(6), r.GetInt64(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11),
                LongOrNull(r, 12), TextOrNull(r, 13), TextOrNull(r, 14), LongOrNull(r, 15), TextOrNull(r, 16),
                TextOrNull(r, 17), LongOrNull(r, 18)));

    private static List<T> Read<T>(SqliteConnection db, string sql, string caseId, Func<SqliteDataReader, T> map)
    {
        using var command = CreateCommand(db, sql, ("$case", caseId));
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(map(reader));
        return result;
    }

    private static string? TextOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static long? LongOrNull(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static SqliteCommand CreateCommand(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    { using var command = CreateCommand(db, sql, values); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    { using var command = CreateCommand(db, sql, values); return command.ExecuteScalar(); }
    private static void Control(SqliteConnection db, string sql) => Execute(db, sql);
    private static void Rollback(SqliteConnection db) { try { Control(db, "ROLLBACK"); } catch (SqliteException) { } }
    private static void RequireOne(int affected)
    { if (affected != 1) throw new InvalidOperationException("simulation_workflow_concurrency_conflict"); }
}
