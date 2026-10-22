using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence.Tests;

public sealed class SchemaInitializerTests
{
    [Fact]
    public void CleanStoresCreateCompleteIndependentStrictSchemasIdempotently()
    {
        var fixture = NewFixture();
        var first = fixture.Initializer.Initialize("test-revision-1");
        var second = fixture.Initializer.Initialize("test-revision-1");

        Assert.Equal(6, first.Workflow.SchemaVersion);
        Assert.Equal(6, first.Journal.SchemaVersion);
        Assert.NotEqual(first.Workflow.ApplicationId, first.Journal.ApplicationId);
        Assert.NotEqual(first.Workflow.DatabaseInstanceId, first.Journal.DatabaseInstanceId);
        Assert.Equal(first.Workflow.DatabaseInstanceId, second.Workflow.DatabaseInstanceId);
        Assert.Equal(first.Journal.DatabaseInstanceId, second.Journal.DatabaseInstanceId);
        Assert.Equal(0, first.Workflow.Sequence);
        Assert.Equal(0, first.Journal.Sequence);
        Assert.Equal(30, first.Workflow.Tables.Count);
        Assert.Equal(6, first.Journal.Tables.Count);
        Assert.Equal(6, Scalar(fixture.Workflow, "SELECT count(*) FROM schema_migrations"));
        Assert.Equal(6, Scalar(fixture.Journal, "SELECT count(*) FROM journal_schema_migrations"));
        AssertStrict(fixture.Workflow);
        AssertStrict(fixture.Journal);
        AssertNoForbiddenStorage(fixture.Workflow);
        AssertNoForbiddenStorage(fixture.Journal);
    }

    [Theory]
    [InlineData("PRAGMA application_id=7", "simulation_schema_application_mismatch")]
    [InlineData("PRAGMA user_version=7", "simulation_schema_newer_version")]
    public void IdentityMismatchAndNewerVersionAreRejected(string mutation, string message)
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, mutation);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Initializer.Initialize("test-revision-1"));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void UnrecognizedNonemptyDatabaseIsRejected()
    {
        var fixture = NewFixture();
        Execute(fixture.Workflow, "CREATE TABLE foreign_data(value TEXT) STRICT");
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Initializer.Initialize("test-revision-1"));
        Assert.Equal("simulation_schema_unrecognized_database", error.Message);
    }

    [Fact]
    public void Existing_version_two_stores_gain_the_denial_ledger_transactionally()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, """
DROP TRIGGER no_update_command_denial_events;
DROP TRIGGER no_delete_command_denial_events;
DROP TABLE command_denial_events;
DROP TRIGGER no_delete_department_catalog;
DROP TRIGGER no_update_case_intake_profiles;
DROP TRIGGER no_delete_case_intake_profiles;
DROP TABLE case_intake_profiles;
DROP TABLE department_catalog;
DROP TABLE case_number_sequence;
DROP TRIGGER no_delete_schema_migrations;
DROP TABLE name_change_completions;
DROP TABLE name_change_requests;
DROP TRIGGER no_update_credential_subject_bindings;
DROP TRIGGER no_delete_credential_subject_bindings;
DROP TABLE credential_subject_bindings;
DELETE FROM schema_migrations WHERE version=6;
DELETE FROM schema_migrations WHERE version=5;
DELETE FROM schema_migrations WHERE version=4;
DELETE FROM schema_migrations WHERE version=3;
CREATE TRIGGER no_delete_schema_migrations BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
PRAGMA user_version=2;
""");
        Execute(fixture.Journal, """
DROP TRIGGER no_delete_journal_migrations;
DELETE FROM journal_schema_migrations WHERE version=6;
DELETE FROM journal_schema_migrations WHERE version=5;
DELETE FROM journal_schema_migrations WHERE version=4;
DELETE FROM journal_schema_migrations WHERE version=3;
CREATE TRIGGER no_delete_journal_migrations BEFORE DELETE ON journal_schema_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
PRAGMA user_version=2;
""");

        var migrated = fixture.Initializer.Initialize("test-revision-2");

        Assert.Equal(6, migrated.Workflow.SchemaVersion);
        Assert.Equal(6, migrated.Journal.SchemaVersion);
        Assert.Equal(1L, Convert.ToInt64(Scalar(fixture.Workflow,
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='command_denial_events'"),
            System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1L, Convert.ToInt64(Scalar(fixture.Workflow,
            "SELECT count(*) FROM schema_migrations WHERE version=3 AND source_revision='test-revision-2'"),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("DROP TABLE audit_corrections", "simulation_schema_required_table_missing")]
    [InlineData("DROP INDEX ux_audit_case_sequence", "simulation_schema_required_index_missing")]
    [InlineData("DROP TRIGGER reject_required_gate_not_applicable", "simulation_schema_required_trigger_missing")]
    public void MissingRequiredObjectIsRejected(string mutation, string message)
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, mutation);
        var error = Assert.Throws<InvalidOperationException>(() => fixture.Initializer.Initialize("test-revision-1"));
        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void MigrationAndHistoricRowsAreAppendOnlyAndForeignKeysAreEnforced()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, "UPDATE schema_migrations SET migration_name='changed' WHERE version=1"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Journal, "DELETE FROM journal_schema_migrations WHERE version=1"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow,
            "INSERT INTO formal_request_versions(case_id,version,site_id,person_id,account_id,intended_use,fake_device_id,slot,synthetic_public_request_digest,fake_authority,template,policy_options_version,requester_person_id,requester_account_id,requester_display_label,created_utc,policy_authority_id,policy_version,policy_content_digest,operation_binding_input_revision,sealed_request_digest) VALUES('missing-case',1,'s','p','a','PIV-authentication-simulation','d','9a','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','ca','t','v1','rp','ra','Requester',1788652800000,'pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',1,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc')"));
    }

    [Fact]
    public void FailedInitialMigrationLeavesNoPartialIdentityOrObjects()
    {
        var fixture = NewFixture();
        using var connection = Open(fixture.Workflow);
        Assert.Throws<SqliteException>(() => SimulationSchemaInitializer.ApplyInitialSchema(
            connection,
            0x51445731,
            SimulationSchemaSql.Workflow + "\nTHIS IS NOT SQL;",
            "workflow",
            "test-revision-1"));

        Assert.Equal(0, Convert.ToInt64(Scalar(connection, "PRAGMA application_id"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, Convert.ToInt64(Scalar(connection, "PRAGMA user_version"), System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0, Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'"), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void OperationCannotRebindApprovalToAnotherRequest()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseA = "10000000-0000-0000-0000-000000000001";
        var caseB = "10000000-0000-0000-0000-000000000002";
        Execute(fixture.Workflow, CaseAndFormal(caseA, "p1", 'a') + CaseAndFormal(caseB, "p2", 'b') + $"""
INSERT INTO approvals(approval_id,case_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest)
VALUES('20000000-0000-0000-0000-000000000001','{caseA}',1,'hrp','hra','HR','HrVerifier',1788652800000,1788653700000,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO operations(operation_id,case_id,formal_version,approval_id,attempt_number,synthetic_public_request_reference,operation_binding_digest,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,workflow_database_instance_id,intent_commit_sequence,state,created_utc,updated_utc,row_revision)
VALUES('30000000-0000-0000-0000-000000000001','{caseB}',1,'20000000-0000-0000-0000-000000000001',1,'request-1','sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','s','a','PIV-authentication-simulation','d','9a','ca','t','40000000-0000-0000-0000-000000000001',1,'IntentCommitted',1788652800000,1788652800000,0);
"""));
    }

    [Fact]
    public void CaseCurrentVersionMustNameAnExistingFormalRequestVersion()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, """
INSERT INTO cases(case_id,person_id,site_id,intake_actor_person_id,intake_actor_account_id,intake_actor_display_label,intake_utc,current_step,required_next_action_code,current_formal_version,row_revision)
VALUES('case-current-version','p','s','hp','ha','HR',1788652800000,'HrIntake','it_validate',NULL,0);
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow,
            "UPDATE cases SET current_formal_version=1 WHERE case_id='case-current-version'"));
    }

    [Fact]
    public void FormalHistoryApprovalFactsAndRecoveryFindingsCannotBeRewritten()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "50000000-0000-0000-0000-000000000001";
        Execute(fixture.Workflow, CaseAndFormal(caseId, "p", 'a'));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"UPDATE formal_request_versions SET account_id='other' WHERE case_id='{caseId}' AND version=1"));
    }

    [Fact]
    public void ApprovalMustBindExactFormalDigestAndPolicyIdentity()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "60000000-0000-0000-0000-000000000001";
        Execute(fixture.Workflow, CaseAndFormal(caseId, "p", 'a'));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO approvals(approval_id,case_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest)
VALUES('60000000-0000-0000-0000-000000000002','{caseId}',1,'hp','ha','HR','HrVerifier',1788652800000,1788653700000,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','wrong','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
"""));
    }

    [Fact]
    public void ApprovalDigestIsRequestBindingWhileOperationDigestIsASeparateBinding()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "61000000-0000-0000-0000-000000000001";
        var approvalId = "61000000-0000-0000-0000-000000000002";
        Execute(fixture.Workflow, CaseAndFormal(caseId, "p", 'a'));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO approvals(approval_id,case_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest)
VALUES('{approvalId}','{caseId}',1,'hp','ha','HR','HrVerifier',1788652800000,1788653700000,'sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
"""));

        Execute(fixture.Workflow, Approval(caseId, approvalId));
        Execute(fixture.Workflow, Operation(caseId, approvalId, "61000000-0000-0000-0000-000000000003", 'd'));
        Assert.Equal("sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            ScalarText(fixture.Workflow, "SELECT operation_binding_digest FROM operations"));
    }

    [Fact]
    public void InvalidationIsOneWayAndAllowsOneCoherentReplacementApproval()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "62000000-0000-0000-0000-000000000001";
        var first = "62000000-0000-0000-0000-000000000002";
        var replacement = "62000000-0000-0000-0000-000000000003";
        Execute(fixture.Workflow, CaseAndFormal(caseId, "p", 'a') + Approval(caseId, first));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Approval(caseId, replacement)));

        Execute(fixture.Workflow, $"UPDATE approvals SET invalidated_utc=1788652860000,invalidation_reason_code='formal_input_changed' WHERE approval_id='{first}'");
        Execute(fixture.Workflow, Approval(caseId, replacement).Replace(
            "1788652800000,1788653700000",
            "1788652920000,1788653820000",
            StringComparison.Ordinal));
        Execute(fixture.Workflow, $"UPDATE approvals SET replaced_by_approval_id='{replacement}' WHERE approval_id='{first}'");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"UPDATE approvals SET invalidated_utc=NULL,invalidation_reason_code=NULL WHERE approval_id='{first}'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Operation(caseId, first, "62000000-0000-0000-0000-000000000004")));
        Execute(fixture.Workflow, Operation(caseId, replacement, "62000000-0000-0000-0000-000000000005"));
    }

    [Fact]
    public void PolicyMigrationBindsExactVersionsAndPoliciesAndIsAppendOnly()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "63000000-0000-0000-0000-000000000001";
        Execute(fixture.Workflow, CaseAndFormal(caseId, "p", 'a') + $"""
INSERT INTO formal_request_versions(case_id,version,site_id,person_id,account_id,intended_use,fake_device_id,slot,synthetic_public_request_digest,fake_authority,template,policy_options_version,requester_person_id,requester_account_id,requester_display_label,created_utc,policy_authority_id,policy_version,policy_content_digest,operation_binding_input_revision,sealed_request_digest)
VALUES('{caseId}',2,'s','p','a','PIV-authentication-simulation','d','9a','aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','ca','t','v2','rp','ra','Requester',1788652860000,'pa','v2','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd',2,'sha256:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee');
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO policy_migrations(migration_id,case_id,source_formal_version,replacement_formal_version,source_policy_authority_id,source_policy_version,source_policy_content_digest,replacement_policy_authority_id,replacement_policy_version,replacement_policy_content_digest,actor_person_id,actor_account_id,actor_display_label,actor_role,migrated_utc,reason_code)
VALUES('63000000-0000-0000-0000-000000000002','{caseId}',1,2,'pa','wrong','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','pa','v2','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','ip','ia','Issuer','ItIssuer',1788652920000,'policy_superseded');
"""));
        Execute(fixture.Workflow, $"""
INSERT INTO policy_migrations(migration_id,case_id,source_formal_version,replacement_formal_version,source_policy_authority_id,source_policy_version,source_policy_content_digest,replacement_policy_authority_id,replacement_policy_version,replacement_policy_content_digest,actor_person_id,actor_account_id,actor_display_label,actor_role,migrated_utc,reason_code)
VALUES('63000000-0000-0000-0000-000000000003','{caseId}',1,2,'pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','pa','v2','dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd','ip','ia','Issuer','ItIssuer',1788652920000,'policy_superseded');
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, "UPDATE policy_migrations SET reason_code='changed'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, "DELETE FROM policy_migrations"));
    }

    [Fact]
    public void TypedReservationsRejectWrongShapeAndDuplicateActiveHolder()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, "INSERT INTO workflow_resource_reservations(operation_id,resource_kind,site_id,reserved_utc) VALUES('00000000-0000-0000-0000-000000000001','AccountUse','s',1788652800000)"));
    }

    [Fact]
    public void UnmatchedResolutionCannotPersistNullOrEmptyCorrectionReason()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, Case("correction-case", "p", "s"));
        foreach (var reason in new[] { "NULL", "''", "' '" })
        {
            Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO account_resolutions(case_id,resolution_sequence,account_id,resolver_person_id,resolver_account_id,resolver_display_label,directory_revision,submitted_candidate_matched,correction_reason,resolved_utc)
VALUES('correction-case',1,'account','rp','ra','Resolver',1,0,{reason},1000);
"""));
        }
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM account_resolutions"));
    }

    [Fact]
    public void JournalCannotRebindConsumptionDigestOrRewriteAcceptedFacts()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Journal, """
INSERT INTO approval_consumptions(approval_id,operation_id,binding_digest,consumed_utc,workflow_database_instance_id,workflow_commit_sequence)
VALUES('70000000-0000-0000-0000-000000000001','70000000-0000-0000-0000-000000000002','sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1788652800000,'70000000-0000-0000-0000-000000000003',1);
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Journal, """
INSERT INTO authority_operations(operation_id,approval_id,synthetic_public_request_reference,binding_digest,admission_identity_digest,case_id,formal_version,attempt_number,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,state,created_utc,updated_utc)
VALUES('70000000-0000-0000-0000-000000000002','70000000-0000-0000-0000-000000000001','r','sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','case-journal',1,1,'s','a','PIV-authentication-simulation','d','9a','ca','t','CallObserved',1788652800000,1788652800000);
"""));
        Assert.Throws<SqliteException>(() => Execute(fixture.Journal, "UPDATE approval_consumptions SET workflow_commit_sequence=2"));
    }

    [Fact]
    public void CaseProjectionPreservesIntakeHistoryAndRequiresNextRevision()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "80000000-0000-0000-0000-000000000001";
        Execute(fixture.Workflow, CaseAndFormal(caseId, "p", 'a'));

        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"UPDATE cases SET person_id='other',row_revision=1 WHERE case_id='{caseId}'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"UPDATE cases SET current_step='Approval' WHERE case_id='{caseId}'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"DELETE FROM cases WHERE case_id='{caseId}'"));
        Execute(fixture.Workflow, $"UPDATE cases SET current_step='IdentityVerification',required_next_action_code='verify_identity',current_formal_version=1,row_revision=1 WHERE case_id='{caseId}'");
        Assert.Equal(1, Scalar(fixture.Workflow, $"SELECT row_revision FROM cases WHERE case_id='{caseId}'"));
    }

    [Fact]
    public void SafeReferencesAreBoundedAndAuditActorIsAtomic()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var oversized = new string('x', 161);
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO audit_events(audit_event_id,event_sequence,actor_person_id,action_code,authorization_decision_code,outcome_code,occurred_utc,safe_source_reference)
VALUES('81000000-0000-0000-0000-000000000001',1,'partial','a','allow','ok',1788652800000,'safe');
"""));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO audit_events(audit_event_id,event_sequence,action_code,authorization_decision_code,outcome_code,occurred_utc,safe_source_reference)
VALUES('81000000-0000-0000-0000-000000000002',2,'a','allow','ok',1788652800000,'{oversized}');
"""));
        Execute(fixture.Workflow, $"""
INSERT INTO audit_events(audit_event_id,event_sequence,action_code,authorization_decision_code,outcome_code,occurred_utc,safe_source_reference)
VALUES('81000000-0000-0000-0000-000000000003',3,'a','allow','ok',1788652800000,'{new string('s', 160)}');
""");
    }

    [Fact]
    public void RevokedApprovalCannotBeConsumedOrUsedButConsumedApprovalMayLaterBeRevoked()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var revokedCase = "82000000-0000-0000-0000-000000000001";
        var consumedCase = "82000000-0000-0000-0000-000000000002";
        var revokedApproval = "82000000-0000-0000-0000-000000000003";
        var consumedApproval = "82000000-0000-0000-0000-000000000004";
        Execute(fixture.Workflow, CaseAndFormal(revokedCase, "p1", 'a') + Approval(revokedCase, revokedApproval));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"UPDATE approvals SET revoked_utc=1788652860000,revocation_reason_code='withdrawn',consumed_by_operation_id='82000000-0000-0000-0000-000000000008',consumed_utc=1788652920000 WHERE approval_id='{revokedApproval}'"));
        Execute(fixture.Workflow, $"UPDATE approvals SET revoked_utc=1788652860000,revocation_reason_code='withdrawn' WHERE approval_id='{revokedApproval}'");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"UPDATE approvals SET consumed_by_operation_id='82000000-0000-0000-0000-000000000005',consumed_utc=1788652920000 WHERE approval_id='{revokedApproval}'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Operation(revokedCase, revokedApproval, "82000000-0000-0000-0000-000000000005")));

        Execute(fixture.Workflow, CaseAndFormal(consumedCase, "p2", 'b') + Approval(consumedCase, consumedApproval));
        Execute(fixture.Workflow, Operation(consumedCase, consumedApproval, "82000000-0000-0000-0000-000000000006"));
        Execute(fixture.Workflow, $"UPDATE approvals SET consumed_by_operation_id='82000000-0000-0000-0000-000000000006',consumed_utc=1788652920000 WHERE approval_id='{consumedApproval}'");
        Execute(fixture.Workflow, $"UPDATE approvals SET revoked_utc=1788652980000,revocation_reason_code='after_use' WHERE approval_id='{consumedApproval}'");
    }

    [Fact]
    public void FormalVersionMustMatchImmutableCaseSubjectAndSite()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseId = "83000000-0000-0000-0000-000000000001";
        Execute(fixture.Workflow, Case(caseId, "p1", "s1"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Formal(caseId, "p2", "s2", 'a')));
    }

    [Fact]
    public void AuditAndRecoveryReferencesCannotBeCrossWired()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        var caseA = "84000000-0000-0000-0000-000000000001";
        var caseB = "84000000-0000-0000-0000-000000000002";
        var approvalA = "84000000-0000-0000-0000-000000000003";
        var approvalB = "84000000-0000-0000-0000-000000000004";
        var operationB = "84000000-0000-0000-0000-000000000005";
        Execute(fixture.Workflow, CaseAndFormal(caseA, "p1", 'a') + Approval(caseA, approvalA));
        Execute(fixture.Workflow, CaseAndFormal(caseB, "p2", 'b') + Approval(caseB, approvalB) + Operation(caseB, approvalB, operationB));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO audit_events(audit_event_id,event_sequence,action_code,authorization_decision_code,outcome_code,occurred_utc,case_id,formal_version,approval_id,operation_id)
VALUES('84000000-0000-0000-0000-000000000006',1,'a','allow','ok',1788652800000,'{caseA}',1,'{approvalB}','{operationB}');
"""));

        Execute(fixture.Workflow, """
INSERT INTO reconciliation_cycles(cycle_id,workflow_database_instance_id,journal_database_instance_id,workflow_sequence_start,journal_sequence_start,started_utc,result_code)
VALUES('84000000-0000-0000-0000-000000000007','84000000-0000-0000-0000-000000000008','84000000-0000-0000-0000-000000000009',0,0,1788652800000,'running');
INSERT INTO recovery_findings(finding_id,first_cycle_id,source_kind,source_database_instance_id,source_sequence,journal_database_instance_id,journal_sequence,journal_operation_id,journal_approval_id,binding_digest,account_resource_reference,device_resource_reference,match_state,first_observed_utc,affected_state,blocking_state)
VALUES('84000000-0000-0000-0000-000000000010','84000000-0000-0000-0000-000000000007','Journal','84000000-0000-0000-0000-000000000009',4,'84000000-0000-0000-0000-000000000009',4,'84000000-0000-0000-0000-000000000011','84000000-0000-0000-0000-000000000012','sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','account','device','MissingLocalIntent',1788652800000,'Affected','Blocking');
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, """
INSERT INTO recovery_finding_observations(observation_id,finding_id,cycle_id,observed_utc,journal_sequence,source_sequence,match_code)
VALUES('84000000-0000-0000-0000-000000000013','84000000-0000-0000-0000-000000000010','84000000-0000-0000-0000-000000000007',1788652860000,5,5,'same');
"""));
    }

    [Fact]
    public void LifecycleTimesAreUnixMillisecondsWhileMigrationEvidenceRemainsText()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");

        Assert.Equal("INTEGER", ScalarText(fixture.Workflow, "SELECT type FROM pragma_table_info('cases') WHERE name='intake_utc'"));
        Assert.Equal("INTEGER", ScalarText(fixture.Workflow, "SELECT type FROM pragma_table_info('approvals') WHERE name='approved_utc'"));
        Assert.Equal("INTEGER", ScalarText(fixture.Journal, "SELECT type FROM pragma_table_info('authority_events') WHERE name='observed_utc'"));
        Assert.Equal("TEXT", ScalarText(fixture.Workflow, "SELECT type FROM pragma_table_info('schema_migrations') WHERE name='applied_utc'"));

        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow,
            "INSERT INTO cases(case_id,person_id,site_id,intake_actor_person_id,intake_actor_account_id,intake_actor_display_label,intake_utc,current_step,required_next_action_code,row_revision) VALUES('bad-time','p','s','hp','ha','HR','2026-09-06T00:00:00Z','HrIntake','it_validate',0)"));
    }

    [Theory]
    [InlineData("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")]
    [InlineData("sha256:CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC")]
    [InlineData("sha256:gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void BindingDigestsRequireCanonicalPrefixedLowercaseSha256(string digest)
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, Case("digest-case", "p", "s"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Formal("digest-case", "p", "s", 'a').Replace(
            "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            digest,
            StringComparison.Ordinal)));
    }

    [Fact]
    public void FormalVersionRequiresPositiveOperationBindingInputRevision()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, Case("revision-case", "p", "s"));

        Assert.Throws<SqliteException>(() => Execute(
            fixture.Workflow,
            Formal("revision-case", "p", "s", 'a').Replace(
                ",1,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'",
                ",0,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc'",
                StringComparison.Ordinal)));
    }

    [Fact]
    public void GateObservationMustBindExactFormalRequirementAndRequiredCannotBeNotApplicable()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, CaseAndFormal("gate-schema-case", "p", 'a') + """
INSERT INTO formal_gate_requirements(case_id,formal_version,gate_code,classification,catalogue_version,policy_authority_id,policy_version,policy_content_digest)
VALUES('gate-schema-case',1,'directory_ready','Required','quarterdeck-v3-simulation-gates-v1','pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
""");

        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, """
INSERT INTO gate_observations(observation_id,observation_sequence,case_id,formal_version,gate_code,catalogue_version,policy_authority_id,policy_version,policy_content_digest,observed_state,safe_result_code,actor_person_id,actor_account_id,actor_display_label,observed_utc)
VALUES('85000000-0000-0000-0000-000000000001',1,'gate-schema-case',1,'directory_ready','quarterdeck-v3-simulation-gates-v1','wrong','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','Satisfied','ready','p','a','Actor',1788652800000);
"""));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, """
INSERT INTO gate_observations(observation_id,observation_sequence,case_id,formal_version,gate_code,catalogue_version,policy_authority_id,policy_version,policy_content_digest,observed_state,safe_result_code,actor_person_id,actor_account_id,actor_display_label,observed_utc)
VALUES('85000000-0000-0000-0000-000000000002',1,'gate-schema-case',1,'directory_ready','quarterdeck-v3-simulation-gates-v1','pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb','NotApplicable','not_applicable','p','a','Actor',1788652800000);
"""));
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM gate_observations"));
    }

    [Fact]
    public void PolicySelectionMustBindExactFormalPolicyIdentity()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, CaseAndFormal("policy-selection-case", "p", 'a'));

        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, """
INSERT INTO policy_selections(case_id,formal_version,option_code,classification,policy_authority_id,policy_version,policy_content_digest)
VALUES('policy-selection-case',1,'proof-required','Required','wrong-authority','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
"""));
    }

    [Fact]
    public void CaseIdsAreBoundedSafeTokensAndAccountResolutionHistoryIsAppendOnly()
    {
        var fixture = NewFixture();
        fixture.Initializer.Initialize("test-revision-1");
        Execute(fixture.Workflow, Case("case-ready-1_site_a", "p", "s"));
        Execute(fixture.Workflow, """
INSERT INTO account_resolutions(case_id,resolution_sequence,account_id,resolver_person_id,resolver_account_id,resolver_display_label,directory_revision,submitted_candidate_matched,correction_reason,resolved_utc)
VALUES('case-ready-1_site_a',1,'account','rp','ra','Resolver',2,0,'corrected_candidate',1788652800000);
""");
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, "UPDATE account_resolutions SET account_id='other'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, "DELETE FROM account_resolutions"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Case("case id with spaces", "p", "s")));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Case("case.with.dot", "p", "s")));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Case("case:with:colon", "p", "s")));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, Case(new string('x', 81), "p", "s")));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, """
INSERT INTO account_resolutions(case_id,resolution_sequence,account_id,resolver_person_id,resolver_account_id,resolver_display_label,directory_revision,submitted_candidate_matched,correction_reason,resolved_utc)
VALUES('case-ready-1_site_a',2,'account','rp','ra','Resolver',2,1,'not_allowed',1788652800000);
"""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("revision with spaces")]
    [InlineData("revision/with/slash")]
    public void InvalidSourceRevisionIsRejected(string revision)
    {
        var fixture = NewFixture();
        Assert.Throws<ArgumentException>(() => fixture.Initializer.Initialize(revision));
    }

    private static Fixture NewFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "quarterdeck-schema-" + Guid.NewGuid().ToString("N"));
        var workflow = Path.Combine(root, "workflow", "workflow.db");
        var journal = Path.Combine(root, "journal", "journal.db");
        var paths = SimulationStorePaths.Create(workflow, journal, Path.Combine(root, "source"));
        return new(workflow, journal, new SimulationSchemaInitializer(new SimulationSqliteConnections(paths)));
    }

    private static string CaseAndFormal(string caseId, string personId, char digestCharacter) =>
        Case(caseId, personId, "s") + Formal(caseId, personId, "s", digestCharacter);

    private static string Case(string caseId, string personId, string siteId) => $"""
INSERT INTO cases(case_id,person_id,site_id,intake_actor_person_id,intake_actor_account_id,intake_actor_display_label,intake_utc,current_step,required_next_action_code,current_formal_version,row_revision)
VALUES('{caseId}','{personId}','{siteId}','hp','ha','HR',1788652800000,'FormalRequest','identity_verify',NULL,0);
""";

    private static string Formal(string caseId, string personId, string siteId, char digestCharacter)
    {
        var requestDigest = new string(digestCharacter, 64);
        return $"""
INSERT INTO formal_request_versions(case_id,version,site_id,person_id,account_id,intended_use,fake_device_id,slot,synthetic_public_request_digest,fake_authority,template,policy_options_version,requester_person_id,requester_account_id,requester_display_label,created_utc,policy_authority_id,policy_version,policy_content_digest,operation_binding_input_revision,sealed_request_digest)
VALUES('{caseId}',1,'{siteId}','{personId}','a','PIV-authentication-simulation','d','9a','{requestDigest}','ca','t','v1','rp','ra','Requester',1788652800000,'pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb',1,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc');
""";
    }

    private static string Approval(string caseId, string approvalId) => $"""
INSERT INTO approvals(approval_id,case_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest)
VALUES('{approvalId}','{caseId}',1,'hp','ha','HR','HrVerifier',1788652800000,1788653700000,'sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc','pa','v1','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
""";

    private static string Operation(string caseId, string approvalId, string operationId, char digestCharacter = 'c') => $"""
INSERT INTO operations(operation_id,case_id,formal_version,approval_id,attempt_number,synthetic_public_request_reference,operation_binding_digest,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,workflow_database_instance_id,intent_commit_sequence,state,created_utc,updated_utc,row_revision)
VALUES('{operationId}','{caseId}',1,'{approvalId}',1,'request','sha256:{new string(digestCharacter, 64)}','s','a','PIV-authentication-simulation','d','9a','ca','t','82000000-0000-0000-0000-000000000009',1,'IntentCommitted',1788652800000,1788652800000,0);
""";

    private static void AssertStrict(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name, strict FROM pragma_table_list WHERE name NOT LIKE 'sqlite_%'";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            count++;
            Assert.Equal(1, reader.GetInt64(1));
        }
        Assert.True(count > 0);
    }

    private static void AssertNoForbiddenStorage(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT p.name, p.type FROM sqlite_schema AS s JOIN pragma_table_info(s.name) AS p WHERE s.type='table' AND (lower(p.name) LIKE '%json%' OR lower(p.name) LIKE '%password%' OR lower(p.name) LIKE '%private_key%' OR lower(p.name) IN ('pin','puk') OR upper(p.type)='BLOB')";
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string ScalarText(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var foreignKeys = connection.CreateCommand();
        foreignKeys.CommandText = "PRAGMA foreign_keys=ON";
        foreignKeys.ExecuteNonQuery();
        return connection;
    }

    private sealed record Fixture(string Workflow, string Journal, SimulationSchemaInitializer Initializer);
}
