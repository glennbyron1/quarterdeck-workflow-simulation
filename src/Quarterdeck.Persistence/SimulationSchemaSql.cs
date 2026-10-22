namespace Quarterdeck.Persistence;

internal static class SimulationSchemaSql
{
    internal static readonly string[] WorkflowTables =
    [
        "name_change_requests", "name_change_completions",
        "schema_migrations", "workflow_metadata", "case_number_sequence", "department_catalog", "case_intake_profiles", "cases", "case_candidate_accounts", "account_resolutions",
        "formal_request_versions", "credential_subject_bindings", "policy_selections", "policy_migrations", "formal_gate_requirements", "gate_observations",
        "identity_verifications", "approvals", "operations", "operation_events",
        "workflow_resource_reservations", "audit_events", "audit_event_fields", "command_denial_events",
        "audit_corrections", "recovery_findings", "recovery_finding_observations", "workflow_recovery_findings",
        "reconciliation_actions", "reconciliation_cycles"
    ];

    internal static readonly string[] WorkflowIndexes =
    [
        "ux_approvals_current", "ux_operations_approval", "ux_operations_attempt", "ux_workflow_active_account",
        "ux_workflow_active_device", "ux_audit_case_sequence", "ux_operation_event_sequence",
        "ux_operation_event_journal_anchor", "ux_recovery_journal_event", "ux_recovery_source_event", "ux_workflow_recovery_anchor"
    ];

    internal static readonly string[] WorkflowTriggers =
    [
        "no_update_name_change_requests", "no_delete_name_change_requests",
        "no_update_name_change_completions", "no_delete_name_change_completions",
        "no_update_schema_migrations", "no_delete_schema_migrations", "guard_workflow_metadata",
        "no_delete_department_catalog", "no_update_case_intake_profiles", "no_delete_case_intake_profiles",
        "guard_case_projection", "no_delete_cases", "no_update_candidate_accounts", "no_delete_candidate_accounts",
        "no_update_account_resolutions", "no_delete_account_resolutions", "no_update_policy_selections", "no_delete_policy_selections",
        "no_update_policy_migrations", "no_delete_policy_migrations", "no_update_formal_gate_requirements", "no_delete_formal_gate_requirements",
        "reject_required_gate_not_applicable", "no_update_gate_observations", "no_delete_gate_observations",
        "guard_formal_supersession", "no_delete_formal_versions", "no_update_credential_subject_bindings", "no_delete_credential_subject_bindings", "no_update_identity_verifications", "no_delete_identity_verifications",
        "guard_approval_projection", "reject_unusable_replacement_approval", "no_delete_approvals", "reject_unusable_approval_operation",
        "no_update_operations", "no_delete_operations", "guard_operation_event_source_insert", "no_update_operation_events", "no_delete_operation_events",
        "no_update_audit_events", "no_delete_audit_events", "no_update_audit_fields", "no_delete_audit_fields",
        "no_update_command_denial_events", "no_delete_command_denial_events",
        "no_update_audit_corrections", "no_delete_audit_corrections", "guard_recovery_source_insert", "no_update_recovery_findings", "no_delete_recovery_findings",
        "no_update_recovery_observations", "no_delete_recovery_observations", "no_update_workflow_recovery_findings", "no_delete_workflow_recovery_findings", "no_update_reconciliation_cycles",
        "no_delete_reconciliation_cycles", "no_update_reconciliation_actions", "no_delete_reconciliation_actions",
        "guard_workflow_release", "no_delete_workflow_reservations"
    ];

    internal static readonly string[] JournalTables =
    [
        "journal_schema_migrations", "journal_metadata", "approval_consumptions",
        "authority_operations", "authority_events", "authority_resource_reservations"
    ];

    internal static readonly string[] JournalIndexes =
    [
        "ux_authority_event_sequence", "ux_journal_active_account", "ux_journal_active_device"
    ];

    internal static readonly string[] JournalTriggers =
    [
        "guard_journal_metadata", "no_update_journal_migrations", "no_delete_journal_migrations",
        "no_update_approval_consumptions", "no_delete_approval_consumptions",
        "no_update_authority_operations", "no_delete_authority_operations",
        "no_update_authority_events", "no_delete_authority_events",
        "guard_journal_release", "no_delete_journal_reservations"
    ];

    internal const string Workflow = """
CREATE TABLE schema_migrations(
 version INTEGER PRIMARY KEY CHECK(version>0), migration_name TEXT NOT NULL CHECK(length(migration_name) BETWEEN 1 AND 80),
 applied_utc TEXT NOT NULL CHECK(length(applied_utc) BETWEEN 20 AND 40), source_revision TEXT NOT NULL CHECK(length(source_revision) BETWEEN 1 AND 80)) STRICT;
CREATE TABLE workflow_metadata(
 singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1), schema_name TEXT NOT NULL CHECK(length(schema_name) BETWEEN 1 AND 80),
 database_instance_id TEXT NOT NULL UNIQUE CHECK(length(database_instance_id)=36), commit_sequence INTEGER NOT NULL CHECK(commit_sequence>=0)) STRICT;
CREATE TRIGGER guard_workflow_metadata BEFORE UPDATE ON workflow_metadata WHEN NEW.singleton_id!=OLD.singleton_id OR NEW.schema_name!=OLD.schema_name OR NEW.database_instance_id!=OLD.database_instance_id OR NEW.commit_sequence<OLD.commit_sequence BEGIN SELECT RAISE(ABORT,'immutable_identity'); END;
CREATE TABLE case_number_sequence(singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1), last_value INTEGER NOT NULL CHECK(last_value>=0)) STRICT;
INSERT INTO case_number_sequence(singleton_id,last_value) VALUES(1,0);
CREATE TABLE department_catalog(
 department_code TEXT PRIMARY KEY CHECK(length(department_code) BETWEEN 1 AND 40 AND department_code NOT GLOB '*[^A-Z0-9_-]*'), display_name TEXT NOT NULL CHECK(length(trim(display_name)) BETWEEN 1 AND 80), sort_order INTEGER NOT NULL CHECK(sort_order>=0), is_active INTEGER NOT NULL CHECK(is_active IN (0,1))) STRICT;
INSERT INTO department_catalog(department_code,display_name,sort_order,is_active) VALUES
 ('ADMINISTRATION','Administration',10,1),('FINANCE','Finance',20,1),('FIRE','Fire',30,1),('EMS','EMS',40,1),('POLICE','Police',50,1),('PUBLIC_WORKS','Public Works',60,1),('HR','Human Resources',70,1),('IT','Information Technology',80,1),('SCHOOLS','Schools',90,1),('UTILITIES','Utilities',100,1),('OTHER','Other',110,1);
CREATE TRIGGER no_delete_department_catalog BEFORE DELETE ON department_catalog BEGIN SELECT RAISE(ABORT,'deactivate_instead_of_delete'); END;
CREATE TABLE cases(
 case_id TEXT PRIMARY KEY CHECK(length(case_id) BETWEEN 1 AND 80 AND case_id NOT GLOB '*[^A-Za-z0-9_-]*'), person_id TEXT NOT NULL CHECK(length(person_id) BETWEEN 1 AND 128), site_id TEXT NOT NULL CHECK(length(site_id) BETWEEN 1 AND 80),
 intake_actor_person_id TEXT NOT NULL CHECK(length(intake_actor_person_id) BETWEEN 1 AND 128), intake_actor_account_id TEXT NOT NULL CHECK(length(intake_actor_account_id) BETWEEN 1 AND 128),
 intake_actor_display_label TEXT NOT NULL CHECK(length(intake_actor_display_label) BETWEEN 1 AND 160), intake_utc INTEGER NOT NULL CHECK(intake_utc>=0),
 current_step TEXT NOT NULL CHECK(current_step IN ('HrIntake','WaitingForIt','FormalRequest','IdentityVerification','Approval','Intent','Dispatch','Delivery','Closed')),
 required_next_action_code TEXT NOT NULL CHECK(length(required_next_action_code) BETWEEN 1 AND 80), current_formal_version INTEGER CHECK(current_formal_version IS NULL OR current_formal_version>0),
 row_revision INTEGER NOT NULL CHECK(row_revision>=0), UNIQUE(case_id,person_id,site_id), FOREIGN KEY(case_id,current_formal_version) REFERENCES formal_request_versions(case_id,version)) STRICT;
CREATE TABLE case_intake_profiles(
 case_id TEXT PRIMARY KEY, synthetic_display_name TEXT NOT NULL CHECK(length(trim(synthetic_display_name)) BETWEEN 1 AND 160), department_code TEXT NOT NULL CHECK(length(department_code) BETWEEN 1 AND 40), department_label TEXT NOT NULL CHECK(length(trim(department_label)) BETWEEN 1 AND 80), job_title TEXT CHECK(job_title IS NULL OR length(trim(job_title)) BETWEEN 1 AND 100), employee_reference TEXT CHECK(employee_reference IS NULL OR length(trim(employee_reference)) BETWEEN 1 AND 80), other_department_review_required INTEGER NOT NULL CHECK(other_department_review_required IN (0,1)),
 FOREIGN KEY(case_id) REFERENCES cases(case_id)) STRICT;
CREATE TRIGGER no_update_case_intake_profiles BEFORE UPDATE ON case_intake_profiles BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_case_intake_profiles BEFORE DELETE ON case_intake_profiles BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TABLE case_candidate_accounts(
 case_id TEXT NOT NULL, account_id TEXT NOT NULL CHECK(length(account_id) BETWEEN 1 AND 128), eligibility_observation_code TEXT NOT NULL CHECK(length(eligibility_observation_code) BETWEEN 1 AND 80),
 fixture_revision INTEGER NOT NULL CHECK(fixture_revision>=0), PRIMARY KEY(case_id,account_id), FOREIGN KEY(case_id) REFERENCES cases(case_id)) STRICT;
CREATE TABLE account_resolutions(
 case_id TEXT NOT NULL, resolution_sequence INTEGER NOT NULL CHECK(resolution_sequence>0), account_id TEXT NOT NULL CHECK(length(account_id) BETWEEN 1 AND 128),
 resolver_person_id TEXT NOT NULL CHECK(length(resolver_person_id) BETWEEN 1 AND 128), resolver_account_id TEXT NOT NULL CHECK(length(resolver_account_id) BETWEEN 1 AND 128), resolver_display_label TEXT NOT NULL CHECK(length(resolver_display_label) BETWEEN 1 AND 160),
 directory_revision INTEGER NOT NULL CHECK(directory_revision>=0), submitted_candidate_matched INTEGER NOT NULL CHECK(submitted_candidate_matched IN (0,1)), correction_reason TEXT CHECK(correction_reason IS NULL OR length(correction_reason) BETWEEN 1 AND 160),
 resolved_utc INTEGER NOT NULL CHECK(resolved_utc>=0), CHECK((submitted_candidate_matched=1 AND correction_reason IS NULL) OR (submitted_candidate_matched=0 AND correction_reason IS NOT NULL AND length(trim(correction_reason)) BETWEEN 1 AND 160 AND length(correction_reason)<=160)), PRIMARY KEY(case_id,resolution_sequence), FOREIGN KEY(case_id) REFERENCES cases(case_id)) STRICT;
CREATE TABLE formal_request_versions(
 case_id TEXT NOT NULL, version INTEGER NOT NULL CHECK(version>0), site_id TEXT NOT NULL CHECK(length(site_id) BETWEEN 1 AND 80), person_id TEXT NOT NULL CHECK(length(person_id) BETWEEN 1 AND 128),
 account_id TEXT NOT NULL CHECK(length(account_id) BETWEEN 1 AND 128), intended_use TEXT NOT NULL CHECK(intended_use='PIV-authentication-simulation'),
 fake_device_id TEXT NOT NULL CHECK(length(fake_device_id) BETWEEN 1 AND 160), slot TEXT NOT NULL CHECK(slot='9a'), synthetic_public_request_digest TEXT NOT NULL CHECK(length(synthetic_public_request_digest)=64 AND synthetic_public_request_digest NOT GLOB '*[^0-9a-f]*'),
 fake_authority TEXT NOT NULL CHECK(length(fake_authority) BETWEEN 1 AND 160), template TEXT NOT NULL CHECK(length(template) BETWEEN 1 AND 160), policy_options_version TEXT NOT NULL CHECK(length(policy_options_version) BETWEEN 1 AND 160),
 requester_person_id TEXT NOT NULL CHECK(length(requester_person_id) BETWEEN 1 AND 128), requester_account_id TEXT NOT NULL CHECK(length(requester_account_id) BETWEEN 1 AND 128), requester_display_label TEXT NOT NULL CHECK(length(requester_display_label) BETWEEN 1 AND 160),
 created_utc INTEGER NOT NULL CHECK(created_utc>=0), policy_authority_id TEXT NOT NULL CHECK(length(policy_authority_id) BETWEEN 1 AND 160), policy_version TEXT NOT NULL CHECK(length(policy_version) BETWEEN 1 AND 160),
 policy_content_digest TEXT NOT NULL CHECK(length(policy_content_digest)=64 AND policy_content_digest NOT GLOB '*[^0-9a-f]*'), operation_binding_input_revision INTEGER NOT NULL CHECK(operation_binding_input_revision>0),
 sealed_request_digest TEXT NOT NULL CHECK(length(sealed_request_digest)=71 AND substr(sealed_request_digest,1,7)='sha256:' AND substr(sealed_request_digest,8) NOT GLOB '*[^0-9a-f]*'), superseded_utc INTEGER, superseded_reason_code TEXT,
 CHECK((superseded_utc IS NULL AND superseded_reason_code IS NULL) OR (superseded_utc>=0 AND length(superseded_reason_code) BETWEEN 1 AND 80)),
 PRIMARY KEY(case_id,version), UNIQUE(case_id,version,policy_authority_id,policy_version,policy_content_digest), UNIQUE(case_id,version,sealed_request_digest,policy_authority_id,policy_version,policy_content_digest),
 FOREIGN KEY(case_id,person_id,site_id) REFERENCES cases(case_id,person_id,site_id)) STRICT;
CREATE TABLE policy_selections(
 case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, option_code TEXT NOT NULL CHECK(length(option_code) BETWEEN 1 AND 80),
 classification TEXT NOT NULL CHECK(classification IN ('Required','OptionalSelected','NotSelected')), policy_authority_id TEXT NOT NULL CHECK(length(policy_authority_id) BETWEEN 1 AND 160),
 policy_version TEXT NOT NULL CHECK(length(policy_version) BETWEEN 1 AND 160), policy_content_digest TEXT NOT NULL CHECK(length(policy_content_digest)=64 AND policy_content_digest NOT GLOB '*[^0-9a-f]*'),
 PRIMARY KEY(case_id,formal_version,option_code),
 FOREIGN KEY(case_id,formal_version,policy_authority_id,policy_version,policy_content_digest) REFERENCES formal_request_versions(case_id,version,policy_authority_id,policy_version,policy_content_digest)) STRICT;
CREATE TABLE policy_migrations(
 migration_id TEXT PRIMARY KEY CHECK(length(migration_id)=36), case_id TEXT NOT NULL,
 source_formal_version INTEGER NOT NULL CHECK(source_formal_version>0), replacement_formal_version INTEGER NOT NULL CHECK(replacement_formal_version>source_formal_version),
 source_policy_authority_id TEXT NOT NULL CHECK(length(source_policy_authority_id) BETWEEN 1 AND 160), source_policy_version TEXT NOT NULL CHECK(length(source_policy_version) BETWEEN 1 AND 160), source_policy_content_digest TEXT NOT NULL CHECK(length(source_policy_content_digest)=64 AND source_policy_content_digest NOT GLOB '*[^0-9a-f]*'),
 replacement_policy_authority_id TEXT NOT NULL CHECK(length(replacement_policy_authority_id) BETWEEN 1 AND 160), replacement_policy_version TEXT NOT NULL CHECK(length(replacement_policy_version) BETWEEN 1 AND 160), replacement_policy_content_digest TEXT NOT NULL CHECK(length(replacement_policy_content_digest)=64 AND replacement_policy_content_digest NOT GLOB '*[^0-9a-f]*'),
 actor_person_id TEXT NOT NULL CHECK(length(actor_person_id) BETWEEN 1 AND 128), actor_account_id TEXT NOT NULL CHECK(length(actor_account_id) BETWEEN 1 AND 128), actor_display_label TEXT NOT NULL CHECK(length(actor_display_label) BETWEEN 1 AND 160), actor_role TEXT NOT NULL CHECK(length(actor_role) BETWEEN 1 AND 80),
 migrated_utc INTEGER NOT NULL CHECK(migrated_utc>=0), reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80),
 UNIQUE(case_id,source_formal_version,replacement_formal_version),
 FOREIGN KEY(case_id,source_formal_version,source_policy_authority_id,source_policy_version,source_policy_content_digest) REFERENCES formal_request_versions(case_id,version,policy_authority_id,policy_version,policy_content_digest),
 FOREIGN KEY(case_id,replacement_formal_version,replacement_policy_authority_id,replacement_policy_version,replacement_policy_content_digest) REFERENCES formal_request_versions(case_id,version,policy_authority_id,policy_version,policy_content_digest)) STRICT;
CREATE TABLE formal_gate_requirements(
 case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, gate_code TEXT NOT NULL CHECK(length(gate_code) BETWEEN 1 AND 80),
 classification TEXT NOT NULL CHECK(classification='Required'), catalogue_version TEXT NOT NULL CHECK(length(catalogue_version) BETWEEN 1 AND 80),
 policy_authority_id TEXT NOT NULL CHECK(length(policy_authority_id) BETWEEN 1 AND 160), policy_version TEXT NOT NULL CHECK(length(policy_version) BETWEEN 1 AND 160),
 policy_content_digest TEXT NOT NULL CHECK(length(policy_content_digest)=64 AND policy_content_digest NOT GLOB '*[^0-9a-f]*'),
 PRIMARY KEY(case_id,formal_version,gate_code),
 UNIQUE(case_id,formal_version,gate_code,catalogue_version,policy_authority_id,policy_version,policy_content_digest),
 FOREIGN KEY(case_id,formal_version,policy_authority_id,policy_version,policy_content_digest) REFERENCES formal_request_versions(case_id,version,policy_authority_id,policy_version,policy_content_digest)) STRICT;
CREATE TABLE gate_observations(
 observation_id TEXT PRIMARY KEY CHECK(length(observation_id)=36), observation_sequence INTEGER NOT NULL CHECK(observation_sequence>0), case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, gate_code TEXT NOT NULL CHECK(length(gate_code) BETWEEN 1 AND 80),
 catalogue_version TEXT NOT NULL CHECK(length(catalogue_version) BETWEEN 1 AND 80), policy_authority_id TEXT NOT NULL CHECK(length(policy_authority_id) BETWEEN 1 AND 160), policy_version TEXT NOT NULL CHECK(length(policy_version) BETWEEN 1 AND 160), policy_content_digest TEXT NOT NULL CHECK(length(policy_content_digest)=64 AND policy_content_digest NOT GLOB '*[^0-9a-f]*'),
 observed_state TEXT NOT NULL CHECK(observed_state IN ('Satisfied','NotSatisfied','Unavailable','Unknown','NotApplicable')), safe_result_code TEXT NOT NULL CHECK(length(safe_result_code) BETWEEN 1 AND 80),
 actor_person_id TEXT NOT NULL CHECK(length(actor_person_id) BETWEEN 1 AND 128), actor_account_id TEXT NOT NULL CHECK(length(actor_account_id) BETWEEN 1 AND 128), actor_display_label TEXT NOT NULL CHECK(length(actor_display_label) BETWEEN 1 AND 160),
 observed_utc INTEGER NOT NULL CHECK(observed_utc>=0), UNIQUE(case_id,observation_sequence),
 FOREIGN KEY(case_id,formal_version,gate_code,catalogue_version,policy_authority_id,policy_version,policy_content_digest) REFERENCES formal_gate_requirements(case_id,formal_version,gate_code,catalogue_version,policy_authority_id,policy_version,policy_content_digest)) STRICT;
CREATE TABLE identity_verifications(
 attestation_id TEXT PRIMARY KEY CHECK(length(attestation_id)=36), case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, subject_person_id TEXT NOT NULL CHECK(length(subject_person_id) BETWEEN 1 AND 128),
 verifier_person_id TEXT NOT NULL CHECK(length(verifier_person_id) BETWEEN 1 AND 128), verifier_account_id TEXT NOT NULL CHECK(length(verifier_account_id) BETWEEN 1 AND 128), verifier_display_label TEXT NOT NULL CHECK(length(verifier_display_label) BETWEEN 1 AND 160),
 verifier_role TEXT NOT NULL CHECK(length(verifier_role) BETWEEN 1 AND 80), observed_utc INTEGER NOT NULL CHECK(observed_utc>=0), fixture_revision INTEGER NOT NULL CHECK(fixture_revision>=0),
 FOREIGN KEY(case_id,formal_version) REFERENCES formal_request_versions(case_id,version)) STRICT;
CREATE TABLE approvals(
 approval_id TEXT PRIMARY KEY CHECK(length(approval_id)=36), case_id TEXT NOT NULL, formal_version INTEGER NOT NULL,
 approver_person_id TEXT NOT NULL CHECK(length(approver_person_id) BETWEEN 1 AND 128), approver_account_id TEXT NOT NULL CHECK(length(approver_account_id) BETWEEN 1 AND 128), approver_display_label TEXT NOT NULL CHECK(length(approver_display_label) BETWEEN 1 AND 160), approver_role TEXT NOT NULL CHECK(length(approver_role) BETWEEN 1 AND 80),
 approved_utc INTEGER NOT NULL CHECK(approved_utc>=0), expires_utc INTEGER NOT NULL CHECK(expires_utc>=approved_utc), approved_request_digest TEXT NOT NULL CHECK(length(approved_request_digest)=71 AND substr(approved_request_digest,1,7)='sha256:' AND substr(approved_request_digest,8) NOT GLOB '*[^0-9a-f]*'),
 policy_authority_id TEXT NOT NULL CHECK(length(policy_authority_id) BETWEEN 1 AND 160), policy_version TEXT NOT NULL CHECK(length(policy_version) BETWEEN 1 AND 160), policy_content_digest TEXT NOT NULL CHECK(length(policy_content_digest)=64 AND policy_content_digest NOT GLOB '*[^0-9a-f]*'),
 invalidated_utc INTEGER, invalidation_reason_code TEXT, replaced_by_approval_id TEXT UNIQUE, revoked_utc INTEGER, revocation_reason_code TEXT, consumed_by_operation_id TEXT UNIQUE, consumed_utc INTEGER,
 CHECK((invalidated_utc IS NULL AND invalidation_reason_code IS NULL AND replaced_by_approval_id IS NULL) OR (invalidated_utc>=approved_utc AND length(invalidation_reason_code) BETWEEN 1 AND 80)),
 CHECK(invalidated_utc IS NULL OR (revoked_utc IS NULL AND consumed_by_operation_id IS NULL)),
 CHECK((revoked_utc IS NULL AND revocation_reason_code IS NULL) OR (revoked_utc>=0 AND length(revocation_reason_code) BETWEEN 1 AND 80)),
 CHECK((consumed_by_operation_id IS NULL AND consumed_utc IS NULL) OR (length(consumed_by_operation_id)=36 AND consumed_utc>=0)),
 UNIQUE(approval_id,case_id,formal_version), UNIQUE(approval_id,case_id,formal_version,approved_request_digest,policy_authority_id,policy_version,policy_content_digest), FOREIGN KEY(case_id,formal_version) REFERENCES formal_request_versions(case_id,version),
 FOREIGN KEY(case_id,formal_version,approved_request_digest,policy_authority_id,policy_version,policy_content_digest) REFERENCES formal_request_versions(case_id,version,sealed_request_digest,policy_authority_id,policy_version,policy_content_digest),
 FOREIGN KEY(replaced_by_approval_id,case_id,formal_version,approved_request_digest,policy_authority_id,policy_version,policy_content_digest) REFERENCES approvals(approval_id,case_id,formal_version,approved_request_digest,policy_authority_id,policy_version,policy_content_digest)) STRICT;
CREATE UNIQUE INDEX ux_approvals_current ON approvals(case_id,formal_version) WHERE invalidated_utc IS NULL AND revoked_utc IS NULL;
CREATE TABLE operations(
 operation_id TEXT PRIMARY KEY CHECK(length(operation_id)=36), case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, approval_id TEXT NOT NULL, attempt_number INTEGER NOT NULL CHECK(attempt_number>0),
 synthetic_public_request_reference TEXT NOT NULL CHECK(length(synthetic_public_request_reference) BETWEEN 1 AND 160), operation_binding_digest TEXT NOT NULL CHECK(length(operation_binding_digest)=71 AND substr(operation_binding_digest,1,7)='sha256:' AND substr(operation_binding_digest,8) NOT GLOB '*[^0-9a-f]*'),
 admission_identity_digest TEXT CHECK(admission_identity_digest IS NULL OR (length(admission_identity_digest)=71 AND substr(admission_identity_digest,1,7)='sha256:')),
 target_site_id TEXT NOT NULL CHECK(length(target_site_id) BETWEEN 1 AND 80), target_account_id TEXT NOT NULL CHECK(length(target_account_id) BETWEEN 1 AND 128), intended_use TEXT NOT NULL CHECK(intended_use='PIV-authentication-simulation'),
 fake_device_id TEXT NOT NULL CHECK(length(fake_device_id) BETWEEN 1 AND 160), slot TEXT NOT NULL CHECK(slot='9a'), authority_id TEXT NOT NULL CHECK(length(authority_id) BETWEEN 1 AND 160), template TEXT NOT NULL CHECK(length(template) BETWEEN 1 AND 160),
 workflow_database_instance_id TEXT NOT NULL CHECK(length(workflow_database_instance_id)=36), intent_commit_sequence INTEGER NOT NULL CHECK(intent_commit_sequence>0),
 state TEXT NOT NULL CHECK(state IN ('IntentCommitted','DispatchObserved','Pending','AcceptedTerminal','RejectedBeforeAcceptance','Unknown','Reconciled')),
 created_utc INTEGER NOT NULL CHECK(created_utc>=0), updated_utc INTEGER NOT NULL CHECK(updated_utc>=created_utc), row_revision INTEGER NOT NULL CHECK(row_revision>=0),
 FOREIGN KEY(case_id,formal_version) REFERENCES formal_request_versions(case_id,version),
 UNIQUE(operation_id,case_id,formal_version,approval_id), FOREIGN KEY(approval_id,case_id,formal_version) REFERENCES approvals(approval_id,case_id,formal_version)) STRICT;
CREATE UNIQUE INDEX ux_operations_approval ON operations(approval_id);
CREATE UNIQUE INDEX ux_operations_attempt ON operations(case_id,formal_version,attempt_number);
CREATE TABLE operation_events(
 event_id TEXT PRIMARY KEY CHECK(length(event_id)=36), operation_id TEXT NOT NULL, event_sequence INTEGER NOT NULL CHECK(event_sequence>0),
 state_code TEXT NOT NULL CHECK(state_code IN ('IntentCommitted','DispatchObserved','Pending','AcceptedTerminal','RejectedBeforeAcceptance','Unknown','Reconciled')),
 event_code TEXT NOT NULL CHECK(length(event_code) BETWEEN 1 AND 80), safe_result_code TEXT NOT NULL CHECK(length(safe_result_code) BETWEEN 1 AND 80), safe_reference TEXT CHECK(safe_reference IS NULL OR length(safe_reference) BETWEEN 1 AND 160),
 observed_utc INTEGER NOT NULL CHECK(observed_utc>=0), source_kind TEXT NOT NULL CHECK(source_kind IN ('Workflow','Journal','Adapter','Recovery')),
 source_database_instance_id TEXT, source_sequence INTEGER,
 CHECK((source_database_instance_id IS NULL AND source_sequence IS NULL) OR (length(source_database_instance_id)=36 AND source_sequence>0)),
 UNIQUE(operation_id,event_sequence), FOREIGN KEY(operation_id) REFERENCES operations(operation_id)) STRICT;
CREATE UNIQUE INDEX ux_operation_event_sequence ON operation_events(operation_id,event_sequence);
CREATE UNIQUE INDEX ux_operation_event_journal_anchor ON operation_events(source_database_instance_id,source_sequence) WHERE source_database_instance_id IS NOT NULL;
CREATE TRIGGER guard_operation_event_source_insert BEFORE INSERT ON operation_events WHEN (NEW.source_database_instance_id IS NULL)!=(NEW.source_sequence IS NULL) BEGIN SELECT RAISE(ABORT,'source_anchor_incomplete'); END;
CREATE TABLE workflow_resource_reservations(
 operation_id TEXT NOT NULL, resource_kind TEXT NOT NULL CHECK(resource_kind IN ('AccountUse','DeviceSlot')), site_id TEXT NOT NULL CHECK(length(site_id) BETWEEN 1 AND 80),
 account_id TEXT, intended_use TEXT, fake_device_id TEXT, slot TEXT, reserved_utc INTEGER NOT NULL CHECK(reserved_utc>=0), released_utc INTEGER, release_reason_code TEXT,
 CHECK((resource_kind='AccountUse' AND length(account_id) BETWEEN 1 AND 128 AND intended_use='PIV-authentication-simulation' AND fake_device_id IS NULL AND slot IS NULL) OR (resource_kind='DeviceSlot' AND account_id IS NULL AND intended_use IS NULL AND length(fake_device_id) BETWEEN 1 AND 160 AND slot='9a')),
 CHECK((released_utc IS NULL AND release_reason_code IS NULL) OR (released_utc>=reserved_utc AND length(release_reason_code) BETWEEN 1 AND 80)),
 PRIMARY KEY(operation_id,resource_kind), FOREIGN KEY(operation_id) REFERENCES operations(operation_id)) STRICT;
CREATE UNIQUE INDEX ux_workflow_active_account ON workflow_resource_reservations(site_id,account_id,intended_use) WHERE released_utc IS NULL AND resource_kind='AccountUse';
CREATE UNIQUE INDEX ux_workflow_active_device ON workflow_resource_reservations(site_id,fake_device_id,slot) WHERE released_utc IS NULL AND resource_kind='DeviceSlot';
CREATE TABLE audit_events(
 audit_event_id TEXT PRIMARY KEY CHECK(length(audit_event_id)=36), event_sequence INTEGER NOT NULL UNIQUE CHECK(event_sequence>0), actor_person_id TEXT, actor_account_id TEXT, actor_display_label TEXT, actor_role TEXT,
 action_code TEXT NOT NULL CHECK(length(action_code) BETWEEN 1 AND 80), authorization_decision_code TEXT NOT NULL CHECK(length(authorization_decision_code) BETWEEN 1 AND 80), outcome_code TEXT NOT NULL CHECK(length(outcome_code) BETWEEN 1 AND 80), occurred_utc INTEGER NOT NULL CHECK(occurred_utc>=0),
 case_id TEXT, formal_version INTEGER, approval_id TEXT, operation_id TEXT, policy_authority_id TEXT, policy_version TEXT, policy_content_digest TEXT,
 safe_reason_code TEXT CHECK(safe_reason_code IS NULL OR length(safe_reason_code) BETWEEN 1 AND 80), safe_result_code TEXT CHECK(safe_result_code IS NULL OR length(safe_result_code) BETWEEN 1 AND 80), safe_source_reference TEXT CHECK(safe_source_reference IS NULL OR length(safe_source_reference) BETWEEN 1 AND 160),
 CHECK((actor_person_id IS NULL AND actor_account_id IS NULL AND actor_display_label IS NULL AND actor_role IS NULL) OR (actor_person_id IS NOT NULL AND actor_account_id IS NOT NULL AND actor_display_label IS NOT NULL AND actor_role IS NOT NULL AND length(actor_person_id) BETWEEN 1 AND 128 AND length(actor_account_id) BETWEEN 1 AND 128 AND length(actor_display_label) BETWEEN 1 AND 160 AND length(actor_role) BETWEEN 1 AND 80)),
 CHECK((policy_authority_id IS NULL AND policy_version IS NULL AND policy_content_digest IS NULL) OR (policy_authority_id IS NOT NULL AND policy_version IS NOT NULL AND policy_content_digest IS NOT NULL AND length(policy_authority_id) BETWEEN 1 AND 160 AND length(policy_version) BETWEEN 1 AND 160 AND length(policy_content_digest)=64 AND policy_content_digest NOT GLOB '*[^0-9a-f]*')),
 CHECK(formal_version IS NULL OR case_id IS NOT NULL),
 CHECK(operation_id IS NULL OR (case_id IS NOT NULL AND formal_version IS NOT NULL AND approval_id IS NOT NULL)),
 CHECK(approval_id IS NULL OR (case_id IS NOT NULL AND formal_version IS NOT NULL)),
 FOREIGN KEY(case_id,formal_version) REFERENCES formal_request_versions(case_id,version),
 FOREIGN KEY(approval_id,case_id,formal_version) REFERENCES approvals(approval_id,case_id,formal_version),
 FOREIGN KEY(operation_id,case_id,formal_version,approval_id) REFERENCES operations(operation_id,case_id,formal_version,approval_id)) STRICT;
CREATE UNIQUE INDEX ux_audit_case_sequence ON audit_events(case_id,event_sequence);
CREATE TABLE audit_event_fields(
 audit_event_id TEXT NOT NULL, field_name TEXT NOT NULL CHECK(length(field_name) BETWEEN 1 AND 80), field_value TEXT NOT NULL CHECK(length(field_value) BETWEEN 1 AND 512),
 PRIMARY KEY(audit_event_id,field_name), FOREIGN KEY(audit_event_id) REFERENCES audit_events(audit_event_id)) STRICT;
CREATE TABLE command_denial_events(
 denial_sequence INTEGER PRIMARY KEY AUTOINCREMENT, denial_event_id TEXT NOT NULL UNIQUE CHECK(length(denial_event_id)=36),
 actor_person_id TEXT, actor_account_id TEXT, actor_display_label TEXT, actor_role TEXT,
 command_code TEXT NOT NULL CHECK(length(command_code) BETWEEN 1 AND 80), safe_result_code TEXT NOT NULL CHECK(length(safe_result_code) BETWEEN 1 AND 80),
 occurred_utc INTEGER NOT NULL CHECK(occurred_utc>=0), case_id TEXT,
 CHECK((actor_person_id IS NULL AND actor_account_id IS NULL AND actor_display_label IS NULL AND actor_role IS NULL) OR (actor_person_id IS NOT NULL AND actor_account_id IS NOT NULL AND actor_display_label IS NOT NULL AND actor_role='AttemptedCommandActor' AND length(actor_person_id) BETWEEN 1 AND 128 AND length(actor_account_id) BETWEEN 1 AND 128 AND length(actor_display_label) BETWEEN 1 AND 160)),
 FOREIGN KEY(case_id) REFERENCES cases(case_id)) STRICT;
CREATE TABLE audit_corrections(
 correction_id TEXT PRIMARY KEY CHECK(length(correction_id)=36), audit_event_id TEXT NOT NULL, author_person_id TEXT NOT NULL CHECK(length(author_person_id) BETWEEN 1 AND 128), author_account_id TEXT NOT NULL CHECK(length(author_account_id) BETWEEN 1 AND 128),
 author_display_label TEXT NOT NULL CHECK(length(author_display_label) BETWEEN 1 AND 160), author_role TEXT NOT NULL CHECK(author_role='ItIssuer'), reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80), corrected_utc INTEGER NOT NULL CHECK(corrected_utc>=0),
 replacement_reason_code TEXT CHECK(replacement_reason_code IS NULL OR length(replacement_reason_code) BETWEEN 1 AND 80), replacement_result_code TEXT CHECK(replacement_result_code IS NULL OR length(replacement_result_code) BETWEEN 1 AND 80), replacement_source_reference TEXT CHECK(replacement_source_reference IS NULL OR length(replacement_source_reference) BETWEEN 1 AND 160), FOREIGN KEY(audit_event_id) REFERENCES audit_events(audit_event_id)) STRICT;
CREATE TABLE recovery_findings(
 finding_id TEXT PRIMARY KEY CHECK(length(finding_id)=36), first_cycle_id TEXT NOT NULL,
 source_kind TEXT NOT NULL CHECK(source_kind='Journal'), source_database_instance_id TEXT NOT NULL CHECK(length(source_database_instance_id)=36), source_sequence INTEGER NOT NULL CHECK(source_sequence>0),
 journal_database_instance_id TEXT NOT NULL CHECK(length(journal_database_instance_id)=36), journal_sequence INTEGER NOT NULL CHECK(journal_sequence>0),
 journal_operation_id TEXT NOT NULL CHECK(length(journal_operation_id)=36), journal_approval_id TEXT NOT NULL CHECK(length(journal_approval_id)=36), binding_digest TEXT NOT NULL CHECK(length(binding_digest)=71 AND substr(binding_digest,1,7)='sha256:' AND substr(binding_digest,8) NOT GLOB '*[^0-9a-f]*'),
 account_resource_reference TEXT NOT NULL CHECK(length(account_resource_reference) BETWEEN 1 AND 160), device_resource_reference TEXT NOT NULL CHECK(length(device_resource_reference) BETWEEN 1 AND 160), result_reference TEXT CHECK(result_reference IS NULL OR length(result_reference) BETWEEN 1 AND 160),
 match_state TEXT NOT NULL CHECK(match_state IN ('MissingLocalIntent','Matched','Conflict','SequenceGap','Unknown')), first_observed_utc INTEGER NOT NULL CHECK(first_observed_utc>=0),
 affected_state TEXT NOT NULL CHECK(affected_state IN ('Affected','Unaffected','Unknown')), blocking_state TEXT NOT NULL CHECK(blocking_state IN ('Blocking','NonBlocking')),
 UNIQUE(finding_id,source_sequence), UNIQUE(source_kind,source_database_instance_id,source_sequence), FOREIGN KEY(first_cycle_id) REFERENCES reconciliation_cycles(cycle_id)) STRICT;
CREATE UNIQUE INDEX ux_recovery_journal_event ON recovery_findings(journal_database_instance_id,journal_sequence);
CREATE UNIQUE INDEX ux_recovery_source_event ON recovery_findings(source_kind,source_database_instance_id,source_sequence);
CREATE TRIGGER guard_recovery_source_insert BEFORE INSERT ON recovery_findings WHEN NEW.source_kind!='Journal' OR NEW.source_database_instance_id IS NULL OR length(NEW.source_database_instance_id)!=36 OR NEW.source_sequence IS NULL OR NEW.source_sequence<=0 BEGIN SELECT RAISE(ABORT,'recovery_source_anchor_invalid'); END;
CREATE TABLE recovery_finding_observations(
 observation_id TEXT PRIMARY KEY CHECK(length(observation_id)=36), finding_id TEXT NOT NULL, cycle_id TEXT NOT NULL, observed_utc INTEGER NOT NULL CHECK(observed_utc>=0),
 journal_sequence INTEGER NOT NULL CHECK(journal_sequence>0), source_sequence INTEGER NOT NULL CHECK(source_sequence>0), match_code TEXT NOT NULL CHECK(length(match_code) BETWEEN 1 AND 80), FOREIGN KEY(finding_id,source_sequence) REFERENCES recovery_findings(finding_id,source_sequence), FOREIGN KEY(cycle_id) REFERENCES reconciliation_cycles(cycle_id)) STRICT;
CREATE TABLE workflow_recovery_findings(
 finding_id TEXT PRIMARY KEY CHECK(length(finding_id)=36), first_cycle_id TEXT NOT NULL, workflow_database_instance_id TEXT NOT NULL CHECK(length(workflow_database_instance_id)=36), workflow_commit_sequence INTEGER NOT NULL CHECK(workflow_commit_sequence>0), operation_id TEXT NOT NULL CHECK(length(operation_id)=36), approval_id TEXT NOT NULL CHECK(length(approval_id)=36), binding_digest TEXT NOT NULL CHECK(length(binding_digest)=71), account_resource_reference TEXT NOT NULL CHECK(length(account_resource_reference) BETWEEN 1 AND 160), device_resource_reference TEXT NOT NULL CHECK(length(device_resource_reference) BETWEEN 1 AND 160), match_code TEXT NOT NULL CHECK(length(match_code) BETWEEN 1 AND 80), first_observed_utc INTEGER NOT NULL CHECK(first_observed_utc>=0), blocking_state TEXT NOT NULL CHECK(blocking_state='Blocking'), UNIQUE(workflow_database_instance_id,workflow_commit_sequence), FOREIGN KEY(first_cycle_id) REFERENCES reconciliation_cycles(cycle_id)) STRICT;
CREATE UNIQUE INDEX ux_workflow_recovery_anchor ON workflow_recovery_findings(workflow_database_instance_id,workflow_commit_sequence);
CREATE TABLE reconciliation_cycles(
 cycle_id TEXT PRIMARY KEY CHECK(length(cycle_id)=36), workflow_database_instance_id TEXT NOT NULL CHECK(length(workflow_database_instance_id)=36), journal_database_instance_id TEXT NOT NULL CHECK(length(journal_database_instance_id)=36),
 workflow_sequence_start INTEGER NOT NULL CHECK(workflow_sequence_start>=0), workflow_sequence_end INTEGER CHECK(workflow_sequence_end IS NULL OR workflow_sequence_end>=workflow_sequence_start),
 journal_sequence_start INTEGER NOT NULL CHECK(journal_sequence_start>=0), journal_sequence_end INTEGER CHECK(journal_sequence_end IS NULL OR journal_sequence_end>=journal_sequence_start),
 started_utc INTEGER NOT NULL CHECK(started_utc>=0), completed_utc INTEGER CHECK(completed_utc IS NULL OR completed_utc>=started_utc), result_code TEXT NOT NULL CHECK(length(result_code) BETWEEN 1 AND 80)) STRICT;
CREATE TABLE reconciliation_actions(
 action_id TEXT PRIMARY KEY CHECK(length(action_id)=36), finding_id TEXT NOT NULL, operation_id TEXT CHECK(operation_id IS NULL OR length(operation_id)=36), source_reference TEXT NOT NULL CHECK(length(source_reference) BETWEEN 1 AND 160),
 issuer_person_id TEXT NOT NULL CHECK(length(issuer_person_id) BETWEEN 1 AND 128), issuer_account_id TEXT NOT NULL CHECK(length(issuer_account_id) BETWEEN 1 AND 128), issuer_display_label TEXT NOT NULL CHECK(length(issuer_display_label) BETWEEN 1 AND 160), issuer_role TEXT NOT NULL CHECK(issuer_role='ItIssuer'),
 reason_code TEXT NOT NULL CHECK(length(reason_code) BETWEEN 1 AND 80), observed_utc INTEGER NOT NULL CHECK(observed_utc>=0), resolution_code TEXT NOT NULL CHECK(length(resolution_code) BETWEEN 1 AND 80),
 FOREIGN KEY(finding_id) REFERENCES recovery_findings(finding_id), FOREIGN KEY(operation_id) REFERENCES operations(operation_id)) STRICT;
CREATE TRIGGER no_update_schema_migrations BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_schema_migrations BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER guard_case_projection BEFORE UPDATE ON cases WHEN NEW.case_id IS NOT OLD.case_id OR NEW.person_id IS NOT OLD.person_id OR NEW.site_id IS NOT OLD.site_id OR NEW.intake_actor_person_id IS NOT OLD.intake_actor_person_id OR NEW.intake_actor_account_id IS NOT OLD.intake_actor_account_id OR NEW.intake_actor_display_label IS NOT OLD.intake_actor_display_label OR NEW.intake_utc IS NOT OLD.intake_utc OR NEW.row_revision!=OLD.row_revision+1 BEGIN SELECT RAISE(ABORT,'immutable_history'); END;
CREATE TRIGGER no_delete_cases BEFORE DELETE ON cases BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_candidate_accounts BEFORE UPDATE ON case_candidate_accounts BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_candidate_accounts BEFORE DELETE ON case_candidate_accounts BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_account_resolutions BEFORE UPDATE ON account_resolutions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_account_resolutions BEFORE DELETE ON account_resolutions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_policy_selections BEFORE UPDATE ON policy_selections BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_policy_selections BEFORE DELETE ON policy_selections BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_policy_migrations BEFORE UPDATE ON policy_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_policy_migrations BEFORE DELETE ON policy_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_formal_gate_requirements BEFORE UPDATE ON formal_gate_requirements BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_formal_gate_requirements BEFORE DELETE ON formal_gate_requirements BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER reject_required_gate_not_applicable BEFORE INSERT ON gate_observations WHEN NEW.observed_state='NotApplicable' AND EXISTS(SELECT 1 FROM formal_gate_requirements AS requirement WHERE requirement.case_id=NEW.case_id AND requirement.formal_version=NEW.formal_version AND requirement.gate_code=NEW.gate_code AND requirement.classification='Required') BEGIN SELECT RAISE(ABORT,'required_gate_not_applicable'); END;
CREATE TRIGGER no_update_gate_observations BEFORE UPDATE ON gate_observations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_gate_observations BEFORE DELETE ON gate_observations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER guard_formal_supersession BEFORE UPDATE ON formal_request_versions WHEN NEW.case_id IS NOT OLD.case_id OR NEW.version IS NOT OLD.version OR NEW.site_id IS NOT OLD.site_id OR NEW.person_id IS NOT OLD.person_id OR NEW.account_id IS NOT OLD.account_id OR NEW.intended_use IS NOT OLD.intended_use OR NEW.fake_device_id IS NOT OLD.fake_device_id OR NEW.slot IS NOT OLD.slot OR NEW.synthetic_public_request_digest IS NOT OLD.synthetic_public_request_digest OR NEW.fake_authority IS NOT OLD.fake_authority OR NEW.template IS NOT OLD.template OR NEW.policy_options_version IS NOT OLD.policy_options_version OR NEW.requester_person_id IS NOT OLD.requester_person_id OR NEW.requester_account_id IS NOT OLD.requester_account_id OR NEW.requester_display_label IS NOT OLD.requester_display_label OR NEW.created_utc IS NOT OLD.created_utc OR NEW.policy_authority_id IS NOT OLD.policy_authority_id OR NEW.policy_version IS NOT OLD.policy_version OR NEW.policy_content_digest IS NOT OLD.policy_content_digest OR NEW.operation_binding_input_revision IS NOT OLD.operation_binding_input_revision OR NEW.sealed_request_digest IS NOT OLD.sealed_request_digest OR OLD.superseded_utc IS NOT NULL OR NEW.superseded_utc IS NULL OR NEW.superseded_reason_code IS NULL BEGIN SELECT RAISE(ABORT,'immutable_history'); END;
CREATE TRIGGER no_delete_formal_versions BEFORE DELETE ON formal_request_versions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TABLE credential_subject_bindings(
 case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, subject_person_external_id TEXT NOT NULL CHECK(length(subject_person_external_id) BETWEEN 1 AND 184),
 subject_account_sid TEXT NOT NULL CHECK(length(subject_account_sid) BETWEEN 5 AND 184 AND substr(subject_account_sid,1,4)='S-1-'), subject_directory_object_id TEXT NOT NULL CHECK(length(subject_directory_object_id) BETWEEN 1 AND 184),
 subject_account_label TEXT NOT NULL CHECK(length(subject_account_label) BETWEEN 1 AND 160), site_id TEXT NOT NULL CHECK(length(site_id) BETWEEN 1 AND 80),
 subject_authority_revision TEXT NOT NULL CHECK(length(subject_authority_revision) BETWEEN 1 AND 184), subject_observed_utc INTEGER NOT NULL CHECK(subject_observed_utc>=0),
 confirmer_person_external_id TEXT NOT NULL CHECK(length(confirmer_person_external_id) BETWEEN 1 AND 184), confirmer_account_sid TEXT NOT NULL CHECK(length(confirmer_account_sid) BETWEEN 5 AND 184 AND substr(confirmer_account_sid,1,4)='S-1-'),
 confirmer_directory_object_id TEXT NOT NULL CHECK(length(confirmer_directory_object_id) BETWEEN 1 AND 184), confirmer_authority_revision TEXT NOT NULL CHECK(length(confirmer_authority_revision) BETWEEN 1 AND 184),
 confirmer_observed_utc INTEGER NOT NULL CHECK(confirmer_observed_utc>=0), confirmed_utc INTEGER NOT NULL CHECK(confirmed_utc>=subject_observed_utc AND confirmed_utc>=confirmer_observed_utc),
 PRIMARY KEY(case_id,formal_version), UNIQUE(subject_account_sid,case_id,formal_version),
 FOREIGN KEY(case_id,formal_version) REFERENCES formal_request_versions(case_id,version)) STRICT;
CREATE TRIGGER no_update_credential_subject_bindings BEFORE UPDATE ON credential_subject_bindings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_credential_subject_bindings BEFORE DELETE ON credential_subject_bindings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_identity_verifications BEFORE UPDATE ON identity_verifications BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_identity_verifications BEFORE DELETE ON identity_verifications BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER guard_approval_projection BEFORE UPDATE ON approvals WHEN NEW.approval_id IS NOT OLD.approval_id OR NEW.case_id IS NOT OLD.case_id OR NEW.formal_version IS NOT OLD.formal_version OR NEW.approver_person_id IS NOT OLD.approver_person_id OR NEW.approver_account_id IS NOT OLD.approver_account_id OR NEW.approver_display_label IS NOT OLD.approver_display_label OR NEW.approver_role IS NOT OLD.approver_role OR NEW.approved_utc IS NOT OLD.approved_utc OR NEW.expires_utc IS NOT OLD.expires_utc OR NEW.approved_request_digest IS NOT OLD.approved_request_digest OR NEW.policy_authority_id IS NOT OLD.policy_authority_id OR NEW.policy_version IS NOT OLD.policy_version OR NEW.policy_content_digest IS NOT OLD.policy_content_digest OR (OLD.invalidated_utc IS NOT NULL AND (NEW.invalidated_utc IS NOT OLD.invalidated_utc OR NEW.invalidation_reason_code IS NOT OLD.invalidation_reason_code OR NEW.revoked_utc IS NOT NULL OR NEW.consumed_by_operation_id IS NOT NULL)) OR (OLD.invalidated_utc IS NULL AND ((NEW.invalidated_utc IS NULL)!=(NEW.invalidation_reason_code IS NULL))) OR (OLD.replaced_by_approval_id IS NOT NULL AND NEW.replaced_by_approval_id IS NOT OLD.replaced_by_approval_id) OR (OLD.replaced_by_approval_id IS NULL AND NEW.replaced_by_approval_id IS NOT NULL AND OLD.invalidated_utc IS NULL) OR (OLD.revoked_utc IS NOT NULL AND (NEW.revoked_utc IS NOT OLD.revoked_utc OR NEW.revocation_reason_code IS NOT OLD.revocation_reason_code OR NEW.invalidated_utc IS NOT NULL OR (OLD.consumed_by_operation_id IS NULL AND NEW.consumed_by_operation_id IS NOT NULL))) OR (OLD.consumed_by_operation_id IS NOT NULL AND (NEW.consumed_by_operation_id IS NOT OLD.consumed_by_operation_id OR NEW.consumed_utc IS NOT OLD.consumed_utc OR NEW.invalidated_utc IS NOT NULL)) OR (OLD.revoked_utc IS NULL AND ((NEW.revoked_utc IS NULL)!=(NEW.revocation_reason_code IS NULL))) OR (OLD.consumed_by_operation_id IS NULL AND ((NEW.consumed_by_operation_id IS NULL)!=(NEW.consumed_utc IS NULL))) OR (OLD.revoked_utc IS NULL AND OLD.consumed_by_operation_id IS NULL AND NEW.revoked_utc IS NOT NULL AND NEW.consumed_by_operation_id IS NOT NULL) BEGIN SELECT RAISE(ABORT,'immutable_history'); END;
CREATE TRIGGER reject_unusable_replacement_approval BEFORE UPDATE OF replaced_by_approval_id ON approvals WHEN NEW.replaced_by_approval_id IS NOT NULL AND OLD.replaced_by_approval_id IS NULL AND NOT EXISTS(SELECT 1 FROM approvals AS replacement WHERE replacement.approval_id=NEW.replaced_by_approval_id AND replacement.case_id=NEW.case_id AND replacement.formal_version=NEW.formal_version AND replacement.approved_request_digest=NEW.approved_request_digest AND replacement.policy_authority_id=NEW.policy_authority_id AND replacement.policy_version=NEW.policy_version AND replacement.policy_content_digest=NEW.policy_content_digest AND replacement.invalidated_utc IS NULL AND replacement.revoked_utc IS NULL AND replacement.consumed_by_operation_id IS NULL AND replacement.approved_utc>=NEW.invalidated_utc) BEGIN SELECT RAISE(ABORT,'replacement_approval_unusable'); END;
CREATE TRIGGER no_delete_approvals BEFORE DELETE ON approvals BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER reject_unusable_approval_operation BEFORE INSERT ON operations WHEN EXISTS(SELECT 1 FROM approvals WHERE approval_id=NEW.approval_id AND (revoked_utc IS NOT NULL OR invalidated_utc IS NOT NULL)) BEGIN SELECT RAISE(ABORT,'approval_unusable'); END;
CREATE TRIGGER no_update_operations BEFORE UPDATE ON operations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_operations BEFORE DELETE ON operations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_operation_events BEFORE UPDATE ON operation_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_operation_events BEFORE DELETE ON operation_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_audit_events BEFORE UPDATE ON audit_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_audit_events BEFORE DELETE ON audit_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_audit_fields BEFORE UPDATE ON audit_event_fields BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_audit_fields BEFORE DELETE ON audit_event_fields BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_command_denial_events BEFORE UPDATE ON command_denial_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_command_denial_events BEFORE DELETE ON command_denial_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_audit_corrections BEFORE UPDATE ON audit_corrections BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_audit_corrections BEFORE DELETE ON audit_corrections BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_recovery_findings BEFORE UPDATE ON recovery_findings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_recovery_findings BEFORE DELETE ON recovery_findings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_recovery_observations BEFORE UPDATE ON recovery_finding_observations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_recovery_observations BEFORE DELETE ON recovery_finding_observations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_workflow_recovery_findings BEFORE UPDATE ON workflow_recovery_findings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_workflow_recovery_findings BEFORE DELETE ON workflow_recovery_findings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_reconciliation_cycles BEFORE UPDATE ON reconciliation_cycles BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_reconciliation_cycles BEFORE DELETE ON reconciliation_cycles BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_reconciliation_actions BEFORE UPDATE ON reconciliation_actions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_reconciliation_actions BEFORE DELETE ON reconciliation_actions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER guard_workflow_release BEFORE UPDATE ON workflow_resource_reservations WHEN NEW.operation_id!=OLD.operation_id OR NEW.resource_kind!=OLD.resource_kind OR NEW.site_id!=OLD.site_id OR NEW.account_id IS NOT OLD.account_id OR NEW.intended_use IS NOT OLD.intended_use OR NEW.fake_device_id IS NOT OLD.fake_device_id OR NEW.slot IS NOT OLD.slot OR OLD.released_utc IS NOT NULL OR NEW.released_utc IS NULL OR NEW.release_reason_code IS NULL BEGIN SELECT RAISE(ABORT,'immutable_history'); END;
CREATE TRIGGER no_delete_workflow_reservations BEFORE DELETE ON workflow_resource_reservations BEGIN SELECT RAISE(ABORT,'append_only'); END;
""" + NameChanges;

    internal const string NameChanges = """
CREATE TABLE name_change_requests(
 request_id TEXT PRIMARY KEY, case_id TEXT NOT NULL REFERENCES cases(case_id), person_id TEXT NOT NULL,
 site_id TEXT NOT NULL, account_id TEXT NOT NULL, old_name TEXT NOT NULL,
 requested_name TEXT NOT NULL CHECK(length(trim(requested_name)) BETWEEN 1 AND 160),
 comment TEXT CHECK(comment IS NULL OR length(comment)<=500), actor_account TEXT NOT NULL,
 requested_utc INTEGER NOT NULL CHECK(requested_utc>=0)) STRICT;
CREATE TABLE name_change_completions(
 request_id TEXT PRIMARY KEY REFERENCES name_change_requests(request_id), actor_account TEXT NOT NULL,
 completed_utc INTEGER NOT NULL CHECK(completed_utc>=0),
 comment TEXT NOT NULL CHECK(length(trim(comment)) BETWEEN 1 AND 500)) STRICT;
CREATE TRIGGER no_update_name_change_requests BEFORE UPDATE ON name_change_requests BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_name_change_requests BEFORE DELETE ON name_change_requests BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_name_change_completions BEFORE UPDATE ON name_change_completions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_name_change_completions BEFORE DELETE ON name_change_completions BEGIN SELECT RAISE(ABORT,'append_only'); END;
""";

    internal const string Journal = """
CREATE TABLE journal_schema_migrations(version INTEGER PRIMARY KEY CHECK(version>0), migration_name TEXT NOT NULL CHECK(length(migration_name) BETWEEN 1 AND 80), applied_utc TEXT NOT NULL CHECK(length(applied_utc) BETWEEN 20 AND 40), source_revision TEXT NOT NULL CHECK(length(source_revision) BETWEEN 1 AND 80)) STRICT;
CREATE TABLE journal_metadata(singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1), schema_name TEXT NOT NULL CHECK(length(schema_name) BETWEEN 1 AND 80), database_instance_id TEXT NOT NULL UNIQUE CHECK(length(database_instance_id)=36), journal_sequence INTEGER NOT NULL CHECK(journal_sequence>=0)) STRICT;
CREATE TRIGGER guard_journal_metadata BEFORE UPDATE ON journal_metadata WHEN NEW.singleton_id!=OLD.singleton_id OR NEW.schema_name!=OLD.schema_name OR NEW.database_instance_id!=OLD.database_instance_id OR NEW.journal_sequence<OLD.journal_sequence BEGIN SELECT RAISE(ABORT,'immutable_identity'); END;
CREATE TABLE approval_consumptions(
 approval_id TEXT PRIMARY KEY CHECK(length(approval_id)=36), operation_id TEXT NOT NULL UNIQUE CHECK(length(operation_id)=36), binding_digest TEXT NOT NULL CHECK(length(binding_digest)=71 AND substr(binding_digest,1,7)='sha256:' AND substr(binding_digest,8) NOT GLOB '*[^0-9a-f]*'), consumed_utc INTEGER NOT NULL CHECK(consumed_utc>=0), workflow_database_instance_id TEXT NOT NULL CHECK(length(workflow_database_instance_id)=36), workflow_commit_sequence INTEGER NOT NULL CHECK(workflow_commit_sequence>0),
 UNIQUE(approval_id,operation_id,binding_digest)) STRICT;
CREATE TABLE authority_operations(
 operation_id TEXT PRIMARY KEY CHECK(length(operation_id)=36), approval_id TEXT NOT NULL UNIQUE, synthetic_public_request_reference TEXT NOT NULL CHECK(length(synthetic_public_request_reference) BETWEEN 1 AND 160), binding_digest TEXT NOT NULL CHECK(length(binding_digest)=71 AND substr(binding_digest,1,7)='sha256:' AND substr(binding_digest,8) NOT GLOB '*[^0-9a-f]*'), admission_identity_digest TEXT NOT NULL CHECK(length(admission_identity_digest)=71 AND substr(admission_identity_digest,1,7)='sha256:' AND substr(admission_identity_digest,8) NOT GLOB '*[^0-9a-f]*'),
 case_id TEXT NOT NULL CHECK(length(case_id) BETWEEN 1 AND 80 AND case_id NOT GLOB '*[^A-Za-z0-9_-]*'), formal_version INTEGER NOT NULL CHECK(formal_version>0), attempt_number INTEGER NOT NULL CHECK(attempt_number>0), target_site_id TEXT NOT NULL CHECK(length(target_site_id) BETWEEN 1 AND 80), target_account_id TEXT NOT NULL CHECK(length(target_account_id) BETWEEN 1 AND 128), intended_use TEXT NOT NULL CHECK(intended_use='PIV-authentication-simulation'),
 fake_device_id TEXT NOT NULL CHECK(length(fake_device_id) BETWEEN 1 AND 160), slot TEXT NOT NULL CHECK(slot='9a'), authority_id TEXT NOT NULL CHECK(length(authority_id) BETWEEN 1 AND 160), template TEXT NOT NULL CHECK(length(template) BETWEEN 1 AND 160),
 state TEXT NOT NULL CHECK(state IN ('CallObserved','RejectedBeforeAcceptance','AcceptedPending','AcceptedTerminal','Observed')), result_reference TEXT CHECK(result_reference IS NULL OR length(result_reference) BETWEEN 1 AND 160), created_utc INTEGER NOT NULL CHECK(created_utc>=0), updated_utc INTEGER NOT NULL CHECK(updated_utc>=created_utc),
 FOREIGN KEY(approval_id,operation_id,binding_digest) REFERENCES approval_consumptions(approval_id,operation_id,binding_digest)) STRICT;
CREATE TABLE authority_events(
 event_id TEXT PRIMARY KEY CHECK(length(event_id)=36), journal_sequence INTEGER NOT NULL UNIQUE CHECK(journal_sequence>0), operation_id TEXT NOT NULL,
 event_code TEXT NOT NULL CHECK(event_code IN ('CallObserved','RejectedBeforeAcceptance','AcceptedPending','AcceptedTerminal','Observed')), safe_result_code TEXT NOT NULL CHECK(length(safe_result_code) BETWEEN 1 AND 80), safe_reference TEXT CHECK(safe_reference IS NULL OR length(safe_reference) BETWEEN 1 AND 160),
 observed_utc INTEGER NOT NULL CHECK(observed_utc>=0), FOREIGN KEY(operation_id) REFERENCES authority_operations(operation_id)) STRICT;
CREATE UNIQUE INDEX ux_authority_event_sequence ON authority_events(operation_id,journal_sequence);
CREATE TABLE authority_resource_reservations(
 operation_id TEXT NOT NULL, resource_kind TEXT NOT NULL CHECK(resource_kind IN ('AccountUse','DeviceSlot')), site_id TEXT NOT NULL CHECK(length(site_id) BETWEEN 1 AND 80), account_id TEXT, intended_use TEXT, fake_device_id TEXT, slot TEXT,
 reserved_utc INTEGER NOT NULL CHECK(reserved_utc>=0), released_utc INTEGER, release_reason_code TEXT,
 CHECK((resource_kind='AccountUse' AND length(account_id) BETWEEN 1 AND 128 AND intended_use='PIV-authentication-simulation' AND fake_device_id IS NULL AND slot IS NULL) OR (resource_kind='DeviceSlot' AND account_id IS NULL AND intended_use IS NULL AND length(fake_device_id) BETWEEN 1 AND 160 AND slot='9a')),
 CHECK((released_utc IS NULL AND release_reason_code IS NULL) OR (released_utc>=reserved_utc AND length(release_reason_code) BETWEEN 1 AND 80)),
 PRIMARY KEY(operation_id,resource_kind), FOREIGN KEY(operation_id) REFERENCES authority_operations(operation_id)) STRICT;
CREATE UNIQUE INDEX ux_journal_active_account ON authority_resource_reservations(site_id,account_id,intended_use) WHERE released_utc IS NULL AND resource_kind='AccountUse';
CREATE UNIQUE INDEX ux_journal_active_device ON authority_resource_reservations(site_id,fake_device_id,slot) WHERE released_utc IS NULL AND resource_kind='DeviceSlot';
CREATE TRIGGER no_update_journal_migrations BEFORE UPDATE ON journal_schema_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_journal_migrations BEFORE DELETE ON journal_schema_migrations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_approval_consumptions BEFORE UPDATE ON approval_consumptions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_approval_consumptions BEFORE DELETE ON approval_consumptions BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_authority_operations BEFORE UPDATE ON authority_operations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_authority_operations BEFORE DELETE ON authority_operations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_authority_events BEFORE UPDATE ON authority_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_authority_events BEFORE DELETE ON authority_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER guard_journal_release BEFORE UPDATE ON authority_resource_reservations WHEN NEW.operation_id!=OLD.operation_id OR NEW.resource_kind!=OLD.resource_kind OR NEW.site_id!=OLD.site_id OR NEW.account_id IS NOT OLD.account_id OR NEW.intended_use IS NOT OLD.intended_use OR NEW.fake_device_id IS NOT OLD.fake_device_id OR NEW.slot IS NOT OLD.slot OR OLD.released_utc IS NOT NULL OR NEW.released_utc IS NULL OR NEW.release_reason_code IS NULL BEGIN SELECT RAISE(ABORT,'immutable_history'); END;
CREATE TRIGGER no_delete_journal_reservations BEFORE DELETE ON authority_resource_reservations BEGIN SELECT RAISE(ABORT,'append_only'); END;
""";

    internal const string WorkflowV2Migration = """
CREATE TRIGGER no_update_operations BEFORE UPDATE ON operations BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER guard_workflow_metadata BEFORE UPDATE ON workflow_metadata WHEN NEW.singleton_id!=OLD.singleton_id OR NEW.schema_name!=OLD.schema_name OR NEW.database_instance_id!=OLD.database_instance_id OR NEW.commit_sequence<OLD.commit_sequence BEGIN SELECT RAISE(ABORT,'immutable_identity'); END;
ALTER TABLE operation_events ADD COLUMN source_database_instance_id TEXT;
ALTER TABLE operation_events ADD COLUMN source_sequence INTEGER;
ALTER TABLE operations ADD COLUMN admission_identity_digest TEXT;
CREATE UNIQUE INDEX ux_operation_event_journal_anchor ON operation_events(source_database_instance_id,source_sequence) WHERE source_database_instance_id IS NOT NULL;
CREATE TRIGGER guard_operation_event_source_insert BEFORE INSERT ON operation_events WHEN (NEW.source_database_instance_id IS NULL)!=(NEW.source_sequence IS NULL) OR (NEW.source_database_instance_id IS NOT NULL AND (length(NEW.source_database_instance_id)!=36 OR NEW.source_sequence<=0)) BEGIN SELECT RAISE(ABORT,'source_anchor_invalid'); END;
ALTER TABLE recovery_findings ADD COLUMN source_kind TEXT NOT NULL DEFAULT 'Journal';
ALTER TABLE recovery_findings ADD COLUMN source_database_instance_id TEXT;
ALTER TABLE recovery_findings ADD COLUMN source_sequence INTEGER;
UPDATE recovery_findings SET source_database_instance_id=journal_database_instance_id,source_sequence=journal_sequence;
ALTER TABLE recovery_finding_observations ADD COLUMN source_sequence INTEGER;
UPDATE recovery_finding_observations SET source_sequence=journal_sequence;
CREATE UNIQUE INDEX ux_recovery_source_event ON recovery_findings(source_kind,source_database_instance_id,source_sequence);
CREATE TRIGGER guard_recovery_source_insert BEFORE INSERT ON recovery_findings WHEN NEW.source_kind!='Journal' OR NEW.source_database_instance_id IS NULL OR length(NEW.source_database_instance_id)!=36 OR NEW.source_sequence IS NULL OR NEW.source_sequence<=0 BEGIN SELECT RAISE(ABORT,'recovery_source_anchor_invalid'); END;
CREATE TABLE workflow_recovery_findings(
 finding_id TEXT PRIMARY KEY CHECK(length(finding_id)=36), first_cycle_id TEXT NOT NULL, workflow_database_instance_id TEXT NOT NULL CHECK(length(workflow_database_instance_id)=36), workflow_commit_sequence INTEGER NOT NULL CHECK(workflow_commit_sequence>0), operation_id TEXT NOT NULL CHECK(length(operation_id)=36), approval_id TEXT NOT NULL CHECK(length(approval_id)=36), binding_digest TEXT NOT NULL CHECK(length(binding_digest)=71), account_resource_reference TEXT NOT NULL CHECK(length(account_resource_reference) BETWEEN 1 AND 160), device_resource_reference TEXT NOT NULL CHECK(length(device_resource_reference) BETWEEN 1 AND 160), match_code TEXT NOT NULL CHECK(length(match_code) BETWEEN 1 AND 80), first_observed_utc INTEGER NOT NULL CHECK(first_observed_utc>=0), blocking_state TEXT NOT NULL CHECK(blocking_state='Blocking'), UNIQUE(workflow_database_instance_id,workflow_commit_sequence), FOREIGN KEY(first_cycle_id) REFERENCES reconciliation_cycles(cycle_id)) STRICT;
CREATE UNIQUE INDEX ux_workflow_recovery_anchor ON workflow_recovery_findings(workflow_database_instance_id,workflow_commit_sequence);
CREATE TRIGGER no_update_workflow_recovery_findings BEFORE UPDATE ON workflow_recovery_findings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_workflow_recovery_findings BEFORE DELETE ON workflow_recovery_findings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_update_reconciliation_cycles BEFORE UPDATE ON reconciliation_cycles BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_reconciliation_cycles BEFORE DELETE ON reconciliation_cycles BEGIN SELECT RAISE(ABORT,'append_only'); END;
""";

    internal const string JournalV2Migration = """
CREATE TRIGGER guard_journal_metadata BEFORE UPDATE ON journal_metadata WHEN NEW.singleton_id!=OLD.singleton_id OR NEW.schema_name!=OLD.schema_name OR NEW.database_instance_id!=OLD.database_instance_id OR NEW.journal_sequence<OLD.journal_sequence BEGIN SELECT RAISE(ABORT,'immutable_identity'); END;
""";

    internal const string WorkflowV3Migration = """
CREATE TABLE command_denial_events(
 denial_sequence INTEGER PRIMARY KEY AUTOINCREMENT, denial_event_id TEXT NOT NULL UNIQUE CHECK(length(denial_event_id)=36),
 actor_person_id TEXT, actor_account_id TEXT, actor_display_label TEXT, actor_role TEXT,
 command_code TEXT NOT NULL CHECK(length(command_code) BETWEEN 1 AND 80), safe_result_code TEXT NOT NULL CHECK(length(safe_result_code) BETWEEN 1 AND 80),
 occurred_utc INTEGER NOT NULL CHECK(occurred_utc>=0), case_id TEXT,
 CHECK((actor_person_id IS NULL AND actor_account_id IS NULL AND actor_display_label IS NULL AND actor_role IS NULL) OR (actor_person_id IS NOT NULL AND actor_account_id IS NOT NULL AND actor_display_label IS NOT NULL AND actor_role='AttemptedCommandActor' AND length(actor_person_id) BETWEEN 1 AND 128 AND length(actor_account_id) BETWEEN 1 AND 128 AND length(actor_display_label) BETWEEN 1 AND 160)),
 FOREIGN KEY(case_id) REFERENCES cases(case_id)) STRICT;
CREATE TRIGGER no_update_command_denial_events BEFORE UPDATE ON command_denial_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_command_denial_events BEFORE DELETE ON command_denial_events BEGIN SELECT RAISE(ABORT,'append_only'); END;
""";

    internal const string WorkflowV4Migration = """
CREATE TABLE case_number_sequence(singleton_id INTEGER PRIMARY KEY CHECK(singleton_id=1), last_value INTEGER NOT NULL CHECK(last_value>=0)) STRICT;
INSERT INTO case_number_sequence(singleton_id,last_value) VALUES(1,0);
UPDATE case_number_sequence SET last_value=COALESCE((SELECT max(CAST(substr(case_id,8) AS INTEGER)) FROM cases WHERE case_id GLOB 'SIM-QD-[0-9]*'),0) WHERE singleton_id=1;
CREATE TABLE department_catalog(
 department_code TEXT PRIMARY KEY CHECK(length(department_code) BETWEEN 1 AND 40 AND department_code NOT GLOB '*[^A-Z0-9_-]*'), display_name TEXT NOT NULL CHECK(length(trim(display_name)) BETWEEN 1 AND 80), sort_order INTEGER NOT NULL CHECK(sort_order>=0), is_active INTEGER NOT NULL CHECK(is_active IN (0,1))) STRICT;
INSERT INTO department_catalog(department_code,display_name,sort_order,is_active) VALUES
 ('ADMINISTRATION','Administration',10,1),('FINANCE','Finance',20,1),('FIRE','Fire',30,1),('EMS','EMS',40,1),('POLICE','Police',50,1),('PUBLIC_WORKS','Public Works',60,1),('HR','Human Resources',70,1),('IT','Information Technology',80,1),('SCHOOLS','Schools',90,1),('UTILITIES','Utilities',100,1),('OTHER','Other',110,1);
CREATE TRIGGER no_delete_department_catalog BEFORE DELETE ON department_catalog BEGIN SELECT RAISE(ABORT,'deactivate_instead_of_delete'); END;
CREATE TABLE case_intake_profiles(
 case_id TEXT PRIMARY KEY, synthetic_display_name TEXT NOT NULL CHECK(length(trim(synthetic_display_name)) BETWEEN 1 AND 160), department_code TEXT NOT NULL CHECK(length(department_code) BETWEEN 1 AND 40), department_label TEXT NOT NULL CHECK(length(trim(department_label)) BETWEEN 1 AND 80), job_title TEXT CHECK(job_title IS NULL OR length(trim(job_title)) BETWEEN 1 AND 100), employee_reference TEXT CHECK(employee_reference IS NULL OR length(trim(employee_reference)) BETWEEN 1 AND 80), other_department_review_required INTEGER NOT NULL CHECK(other_department_review_required IN (0,1)),
 FOREIGN KEY(case_id) REFERENCES cases(case_id)) STRICT;
CREATE TRIGGER no_update_case_intake_profiles BEFORE UPDATE ON case_intake_profiles BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_case_intake_profiles BEFORE DELETE ON case_intake_profiles BEGIN SELECT RAISE(ABORT,'append_only'); END;
""";

    internal const string WorkflowV6Migration = """
CREATE TABLE credential_subject_bindings(
 case_id TEXT NOT NULL, formal_version INTEGER NOT NULL, subject_person_external_id TEXT NOT NULL CHECK(length(subject_person_external_id) BETWEEN 1 AND 184),
 subject_account_sid TEXT NOT NULL CHECK(length(subject_account_sid) BETWEEN 5 AND 184 AND substr(subject_account_sid,1,4)='S-1-'), subject_directory_object_id TEXT NOT NULL CHECK(length(subject_directory_object_id) BETWEEN 1 AND 184),
 subject_account_label TEXT NOT NULL CHECK(length(subject_account_label) BETWEEN 1 AND 160), site_id TEXT NOT NULL CHECK(length(site_id) BETWEEN 1 AND 80),
 subject_authority_revision TEXT NOT NULL CHECK(length(subject_authority_revision) BETWEEN 1 AND 184), subject_observed_utc INTEGER NOT NULL CHECK(subject_observed_utc>=0),
 confirmer_person_external_id TEXT NOT NULL CHECK(length(confirmer_person_external_id) BETWEEN 1 AND 184), confirmer_account_sid TEXT NOT NULL CHECK(length(confirmer_account_sid) BETWEEN 5 AND 184 AND substr(confirmer_account_sid,1,4)='S-1-'),
 confirmer_directory_object_id TEXT NOT NULL CHECK(length(confirmer_directory_object_id) BETWEEN 1 AND 184), confirmer_authority_revision TEXT NOT NULL CHECK(length(confirmer_authority_revision) BETWEEN 1 AND 184),
 confirmer_observed_utc INTEGER NOT NULL CHECK(confirmer_observed_utc>=0), confirmed_utc INTEGER NOT NULL CHECK(confirmed_utc>=subject_observed_utc AND confirmed_utc>=confirmer_observed_utc),
 PRIMARY KEY(case_id,formal_version), UNIQUE(subject_account_sid,case_id,formal_version),
 FOREIGN KEY(case_id,formal_version) REFERENCES formal_request_versions(case_id,version)) STRICT;
CREATE TRIGGER no_update_credential_subject_bindings BEFORE UPDATE ON credential_subject_bindings BEGIN SELECT RAISE(ABORT,'append_only'); END;
CREATE TRIGGER no_delete_credential_subject_bindings BEFORE DELETE ON credential_subject_bindings BEGIN SELECT RAISE(ABORT,'append_only'); END;
""";
}
