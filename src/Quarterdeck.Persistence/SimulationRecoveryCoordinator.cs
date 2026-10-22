using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence;

/// <summary>
/// Persists journal observations independently of intent creation and performs
/// fail-closed startup comparison. It never calls the adapter and never infers
/// an external outcome from a local intent.
/// </summary>
internal sealed class SimulationRecoveryCoordinator
{
    private readonly SimulationSqliteConnections _connections;
    private readonly SimulationAuthorityJournalRepository _journal;
    private readonly SimulationStoreSetRuntimeState _runtime;

    internal object StoreSetToken => _connections.StoreSetToken;

    internal SimulationRecoveryCoordinator(
        SimulationSqliteConnections connections,
        SimulationAuthorityJournalRepository journal)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _runtime = _connections.RuntimeState;
        if (!ReferenceEquals(_connections.StoreSetToken, _journal.StoreSetToken))
            throw new ArgumentException("journal_store_set_mismatch", nameof(journal));
    }

    internal WorkflowResultPersistenceResult PersistObservation(
        PersistAuthorityObservationCommand value,
        IWorkflowResultFault? fault = null)
    {
        fault ??= NoWorkflowResultFault.Instance;
        try
        {
            RequireGuid(value.EventId, "result_event_id_invalid");
            RequireGuid(value.OperationId, "result_operation_id_invalid");
            RequireDigest(value.BindingDigest);
            if (value.ObservedUtc < 0) throw new ArgumentException("result_time_invalid");
            var batch = _journal.EnumerateForRecovery();
            var matches = batch.Operations.Where(item =>
                string.Equals(item.OperationId, value.OperationId, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1 || !FixedEquals(matches[0].BindingDigest, value.BindingDigest))
                return new(WorkflowResultPersistenceStatus.Rejected,
                    "result_non_authoritative_observation");
            var journalResult = matches[0];
            var observation = new AuthorityObservation(journalResult.State,
                journalResult.SafeResultCode, new AuthorityOperationReceipt(
                    journalResult.OperationId, journalResult.ApprovalId,
                    journalResult.BindingDigest, journalResult.JournalDatabaseInstanceId,
                    journalResult.JournalSequence, journalResult.State,
                    journalResult.ResultReference));

            using var db = _connections.OpenWorkflow();
            Control(db, "BEGIN IMMEDIATE");
            try
            {
                var existing = ReadResultEvent(db, value.EventId, value.OperationId,
                    value.BindingDigest, observation);
                if (existing is { } exact)
                {
                    Control(db, "ROLLBACK");
                    return exact;
                }

                using var operation = Command(db, """
SELECT approval_id,operation_binding_digest,admission_identity_digest,workflow_database_instance_id,intent_commit_sequence,
 target_site_id,target_account_id,intended_use,fake_device_id,slot
FROM operations WHERE operation_id=$operation
""", ("$operation", value.OperationId));
                using var operationReader = operation.ExecuteReader();
                var exactLocal = operationReader.Read()
                    && string.Equals(operationReader.GetString(0), journalResult.ApprovalId, StringComparison.Ordinal)
                    && FixedEquals(operationReader.GetString(1), value.BindingDigest)
                    && !operationReader.IsDBNull(2)
                    && FixedEquals(operationReader.GetString(2), journalResult.AdmissionIdentityDigest)
                    && string.Equals(operationReader.GetString(3), journalResult.WorkflowDatabaseInstanceId, StringComparison.Ordinal)
                    && operationReader.GetInt64(4) == journalResult.WorkflowCommitSequence
                    && string.Equals(operationReader.GetString(5), journalResult.SiteId, StringComparison.Ordinal)
                    && string.Equals(operationReader.GetString(6), journalResult.AccountId, StringComparison.Ordinal)
                    && string.Equals(operationReader.GetString(7), journalResult.IntendedUse, StringComparison.Ordinal)
                    && string.Equals(operationReader.GetString(8), journalResult.FakeDeviceId, StringComparison.Ordinal)
                    && string.Equals(operationReader.GetString(9), journalResult.Slot, StringComparison.Ordinal)
                    && !operationReader.Read();
                operationReader.Close();
                if (!exactLocal)
                {
                    Control(db, "ROLLBACK");
                    return new(WorkflowResultPersistenceStatus.Mismatch,
                        "result_local_intent_mismatch");
                }
                if (observation.Receipt is not { } receipt
                    || !string.Equals(receipt.OperationId, value.OperationId, StringComparison.Ordinal)
                    || !FixedEquals(receipt.BindingDigest, value.BindingDigest))
                {
                    Control(db, "ROLLBACK");
                    return new(WorkflowResultPersistenceStatus.Mismatch,
                        "result_journal_receipt_mismatch");
                }

                var state = MapState(observation.State);
                var sequence = Convert.ToInt64(Scalar(db,
                    "SELECT coalesce(max(event_sequence),0)+1 FROM operation_events WHERE operation_id=$operation",
                    ("$operation", value.OperationId)), CultureInfo.InvariantCulture);
                Execute(db, """
INSERT INTO operation_events(event_id,operation_id,event_sequence,state_code,event_code,
 safe_result_code,safe_reference,observed_utc,source_kind,source_database_instance_id,source_sequence)
VALUES($event,$operation,$sequence,$state,'authority_observation',$safe,$reference,$utc,'Adapter',$sourceDatabase,$sourceSequence)
""", ("$event", value.EventId), ("$operation", value.OperationId),
                    ("$sequence", sequence), ("$state", state),
                    ("$safe", observation.SafeResultCode),
                    ("$reference", CanonicalSourceReference(receipt)), ("$utc", value.ObservedUtc),
                    ("$sourceDatabase", receipt.JournalDatabaseInstanceId),
                    ("$sourceSequence", receipt.JournalSequence));
                if (observation.State is AuthorityOperationState.AcceptedTerminal
                    or AuthorityOperationState.RejectedBeforeAcceptance)
                {
                    var released = Execute(db, """
UPDATE workflow_resource_reservations
SET released_utc=$utc,release_reason_code='exact_terminal_authority_result'
WHERE operation_id=$operation AND released_utc IS NULL
""", ("$utc", value.ObservedUtc), ("$operation", value.OperationId));
                    if (released != 2)
                        throw new InvalidOperationException("result_exact_reservations_missing");
                }
                var commitSequence = Convert.ToInt64(Scalar(db,
                    "SELECT commit_sequence+1 FROM workflow_metadata WHERE singleton_id=1"),
                    CultureInfo.InvariantCulture);
                Execute(db, "UPDATE workflow_metadata SET commit_sequence=$value WHERE singleton_id=1",
                    ("$value", commitSequence));
                fault.Observe(WorkflowResultFaultPoint.BeforeCommit);
                Control(db, "COMMIT");
                try
                {
                    fault.Observe(WorkflowResultFaultPoint.AfterCommit);
                    return new(WorkflowResultPersistenceStatus.Persisted,
                        "result_observation_persisted");
                }
                catch
                {
                    return ReadResultEventOutside(value, observation) ?? new(
                        WorkflowResultPersistenceStatus.Unknown,
                        "result_commit_unknown");
                }
            }
            catch
            {
                Rollback(db);
                return ReadResultEventOutside(value, observation) ?? new(
                    WorkflowResultPersistenceStatus.Unknown,
                    "result_persistence_unknown");
            }
        }
        catch (ArgumentException)
        {
            return new(WorkflowResultPersistenceStatus.Rejected,
                "result_command_invalid");
        }
        catch
        {
            SetGlobalDiscoveryBlocked(true);
            return new(WorkflowResultPersistenceStatus.Unknown,
                "result_authority_or_store_unavailable");
        }
    }

    internal RecoveryDiscoveryResult DiscoverAtStartup(long observedUtc,
        IRecoveryDiscoveryFault? fault = null)
    {
        lock (_runtime.DispatchSync)
            return DiscoverAtStartupLocked(observedUtc, fault);
    }

    private RecoveryDiscoveryResult DiscoverAtStartupLocked(long observedUtc,
        IRecoveryDiscoveryFault? fault)
    {
        if (observedUtc < 0) throw new ArgumentOutOfRangeException(nameof(observedUtc));
        SetGlobalDiscoveryBlocked(true);
        fault ??= NoRecoveryDiscoveryFault.Instance;
        JournalRecoveryBatch batch;
        try { batch = _journal.EnumerateForRecovery(); }
        catch
        {
            SetGlobalDiscoveryBlocked(true);
            return new(Guid.Empty.ToString("D"), "recovery_journal_unavailable",
                new[] { new RecoveryFinding(Guid.Empty.ToString("D"),
                    Guid.Empty.ToString("D"), RecoveryMatchState.Unknown,
                    "recovery_journal_unavailable", true, "unknown", "unknown") });
        }

        using var db = _connections.OpenWorkflow();
        Control(db, "BEGIN IMMEDIATE");
        try
        {
            var cycleId = Guid.NewGuid().ToString("D");
            var workflowId = Convert.ToString(Scalar(db,
                "SELECT database_instance_id FROM workflow_metadata WHERE singleton_id=1"),
                CultureInfo.InvariantCulture)!;
            var workflowStart = Convert.ToInt64(Scalar(db,
                "SELECT commit_sequence FROM workflow_metadata WHERE singleton_id=1"),
                CultureInfo.InvariantCulture);
            var classifiedItems = batch.Operations
                .Select(item => (Item: item, Classified: Classify(db, item)))
                .ToArray();
            Execute(db, """
INSERT INTO reconciliation_cycles(cycle_id,workflow_database_instance_id,journal_database_instance_id,
 workflow_sequence_start,workflow_sequence_end,journal_sequence_start,journal_sequence_end,
 started_utc,completed_utc,result_code)
VALUES($cycle,$workflow,$journal,$workflowStart,$workflowStart,$journalStart,$journalEnd,$utc,$utc,'recovery_discovery_complete')
""", ("$cycle", cycleId), ("$workflow", workflowId),
                ("$journal", batch.JournalDatabaseInstanceId),
                ("$workflowStart", workflowStart), ("$journalStart", batch.SequenceStart),
                ("$journalEnd", batch.SequenceEnd), ("$utc", observedUtc));

            var findings = new List<RecoveryFinding>();
            foreach (var entry in classifiedItems)
            {
                var item = entry.Item;
                var classified = entry.Classified;
                var blocking = classified.State == RecoveryMatchState.Terminal
                    ? "NonBlocking" : "Blocking";
                var persistedMatch = classified.State switch
                {
                    RecoveryMatchState.AbsentLocal => "MissingLocalIntent",
                    RecoveryMatchState.Pending or RecoveryMatchState.Terminal => "Matched",
                    RecoveryMatchState.Mismatch or RecoveryMatchState.Ambiguous => "Conflict",
                    _ => "Unknown"
                };
                var findingId = Guid.NewGuid().ToString("D");
                var account = $"{item.SiteId}|{item.AccountId}|{item.IntendedUse}";
                var device = $"{item.SiteId}|{item.FakeDeviceId}|{item.Slot}";
                Execute(db, """
INSERT INTO recovery_findings(finding_id,first_cycle_id,journal_database_instance_id,
 journal_sequence,source_kind,source_database_instance_id,source_sequence,journal_operation_id,journal_approval_id,binding_digest,
 account_resource_reference,device_resource_reference,result_reference,match_state,
 first_observed_utc,affected_state,blocking_state)
VALUES($finding,$cycle,$journal,$sequence,'Journal',$journal,$sequence,$operation,$approval,$digest,$account,$device,
 $reference,$match,$utc,'Affected',$blocking)
ON CONFLICT(source_kind,source_database_instance_id,source_sequence) DO NOTHING
""", ("$finding", findingId), ("$cycle", cycleId),
                    ("$journal", item.JournalDatabaseInstanceId), ("$sequence", item.JournalSequence),
                    ("$operation", item.OperationId), ("$approval", item.ApprovalId),
                    ("$digest", item.BindingDigest), ("$account", account), ("$device", device),
                    ("$reference", item.ResultReference), ("$match", persistedMatch),
                    ("$blocking", blocking),
                    ("$utc", observedUtc));
                var persistedId = Convert.ToString(Scalar(db, """
SELECT finding_id FROM recovery_findings
WHERE source_kind='Journal' AND source_database_instance_id=$journal AND source_sequence=$sequence
""", ("$journal", item.JournalDatabaseInstanceId), ("$sequence", item.JournalSequence)),
                    CultureInfo.InvariantCulture)!;
                Execute(db, """
INSERT INTO recovery_finding_observations(observation_id,finding_id,cycle_id,observed_utc,journal_sequence,source_sequence,match_code)
VALUES($observation,$finding,$cycle,$utc,$sequence,$sequence,$code)
""", ("$observation", Guid.NewGuid().ToString("D")), ("$finding", persistedId),
                    ("$cycle", cycleId), ("$utc", observedUtc),
                    ("$sequence", item.JournalSequence), ("$code", classified.Code));
                findings.Add(new(persistedId, item.OperationId, classified.State,
                    classified.Code, blocking == "Blocking", account, device));
            }
            foreach (var item in ReadWorkflowOnlyUnresolved(db, batch))
            {
                var findingId = Guid.NewGuid().ToString("D");
                var account = $"{item.SiteId}|{item.AccountId}|{item.IntendedUse}";
                var device = $"{item.SiteId}|{item.FakeDeviceId}|{item.Slot}";
                Execute(db, """
INSERT INTO workflow_recovery_findings(finding_id,first_cycle_id,workflow_database_instance_id,
 workflow_commit_sequence,operation_id,approval_id,binding_digest,account_resource_reference,
 device_resource_reference,match_code,first_observed_utc,blocking_state)
VALUES($finding,$cycle,$workflow,$sequence,$operation,$approval,$digest,$account,$device,
 'recovery_journal_operation_absent',$utc,'Blocking')
ON CONFLICT(workflow_database_instance_id,workflow_commit_sequence) DO NOTHING
""", ("$finding", findingId), ("$cycle", cycleId), ("$workflow", item.WorkflowDatabaseInstanceId),
                    ("$sequence", item.WorkflowCommitSequence), ("$operation", item.OperationId),
                    ("$approval", item.ApprovalId), ("$digest", item.BindingDigest),
                    ("$account", account), ("$device", device), ("$utc", observedUtc));
                var persistedId = Convert.ToString(Scalar(db, """
SELECT finding_id FROM workflow_recovery_findings
WHERE workflow_database_instance_id=$workflow AND workflow_commit_sequence=$sequence
""", ("$workflow", item.WorkflowDatabaseInstanceId),
                    ("$sequence", item.WorkflowCommitSequence)), CultureInfo.InvariantCulture)!;
                findings.Add(new(persistedId, item.OperationId, RecoveryMatchState.Unknown,
                    "recovery_journal_operation_absent", true, account, device));
            }
            Control(db, "COMMIT");
            fault.Observe(RecoveryDiscoveryFaultPoint.AfterWorkflowCommitBeforeJournalReadback);
            var readback = _journal.ReadWatermark();
            if (!string.Equals(readback.JournalDatabaseInstanceId,
                    batch.JournalDatabaseInstanceId, StringComparison.Ordinal)
                || readback.JournalSequence != batch.SequenceEnd)
            {
                SetGlobalDiscoveryBlocked(true);
                return new(cycleId, "recovery_journal_changed_retry_required",
                    new[] { new RecoveryFinding(Guid.Empty.ToString("D"),
                        Guid.Empty.ToString("D"), RecoveryMatchState.Unknown,
                        "recovery_journal_changed_retry_required", true,
                        "unknown", "unknown") });
            }
            SetGlobalDiscoveryBlocked(false);
            return new(cycleId, "recovery_discovery_complete", findings.AsReadOnly());
        }
        catch
        {
            Rollback(db);
            SetGlobalDiscoveryBlocked(true);
            return new(Guid.Empty.ToString("D"), "recovery_discovery_unknown",
                new[] { new RecoveryFinding(Guid.Empty.ToString("D"),
                    Guid.Empty.ToString("D"), RecoveryMatchState.Unknown,
                    "recovery_discovery_unknown", true, "unknown", "unknown") });
        }
    }

    internal bool IsDispatchBlocked(string siteId, string accountId, string intendedUse,
        string fakeDeviceId, string slot)
    {
        if (Volatile.Read(ref _runtime.GlobalDiscoveryBlocked) != 0) return true;
        using var db = _connections.OpenWorkflow();
        var account = $"{siteId}|{accountId}|{intendedUse}";
        var device = $"{siteId}|{fakeDeviceId}|{slot}";
        return Convert.ToInt64(Scalar(db, """
SELECT
(SELECT count(*) FROM recovery_findings f
WHERE f.blocking_state='Blocking'
AND (f.account_resource_reference=$account OR f.device_resource_reference=$device)
AND NOT EXISTS(SELECT 1 FROM reconciliation_actions a WHERE a.finding_id=f.finding_id)
AND NOT EXISTS(SELECT 1 FROM recovery_findings newer
 WHERE newer.source_kind='Journal' AND f.source_kind='Journal'
 AND newer.source_database_instance_id=f.source_database_instance_id
 AND newer.journal_operation_id=f.journal_operation_id
 AND newer.source_sequence>f.source_sequence))
+
(SELECT count(*) FROM workflow_recovery_findings w
 WHERE w.blocking_state='Blocking'
 AND (w.account_resource_reference=$account OR w.device_resource_reference=$device)
 AND NOT EXISTS(SELECT 1 FROM recovery_findings jf
  WHERE jf.journal_operation_id=w.operation_id AND jf.binding_digest=w.binding_digest))
""", ("$account", account), ("$device", device)), CultureInfo.InvariantCulture) > 0;
    }

    internal AuthorityDispatchResult Dispatch(AcceptAuthorityOperationCommand command,
        IAuthorityJournalFault? fault = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        lock (_runtime.DispatchSync)
        {
            try
            {
                var fields = command.Admission.Fields;
                if (IsDispatchBlocked(fields.SiteId.Value, fields.AccountId.Value,
                    fields.IntendedUse, fields.FakeDeviceId, fields.Slot))
                {
                    return new(AuthorityDispatchStatus.NotAccepted,
                        "recovery_finding_blocks_dispatch", null);
                }
                var authorization = new DispatchAuthorization(this, _journal, command);
                return _journal.Dispatch(command, authorization, fault);
            }
            catch
            {
                SetGlobalDiscoveryBlocked(true);
                return new(AuthorityDispatchStatus.NotAccepted,
                    "recovery_gate_unavailable", null);
            }
        }
    }

    internal RecoveryActionResult Reconcile(ReconcileRecoveryCommand value)
    {
        try
        {
            RequireGuid(value.ActionId, "reconciliation_action_id_invalid");
            RequireGuid(value.FindingId, "reconciliation_finding_id_invalid");
            RequireGuid(value.OperationId, "reconciliation_operation_id_invalid");
            using var db = _connections.OpenWorkflow();
            Control(db, "BEGIN IMMEDIATE");
            using var findingCommand = Command(db, """
SELECT f.journal_database_instance_id,f.journal_sequence,f.binding_digest,
 f.account_resource_reference,o.match_code,f.source_kind
FROM recovery_findings f JOIN recovery_finding_observations o ON o.finding_id=f.finding_id
WHERE f.finding_id=$finding AND f.journal_operation_id=$operation
ORDER BY o.observed_utc DESC LIMIT 1
""", ("$finding", value.FindingId), ("$operation", value.OperationId));
            using var reader = findingCommand.ExecuteReader();
            if (!reader.Read())
            {
                Control(db, "ROLLBACK");
                return new(false, "reconciliation_exact_finding_required");
            }
            var journalId = reader.IsDBNull(0) ? null : reader.GetString(0);
            var journalSequence = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
            var digest = reader.GetString(2);
            var accountReference = reader.GetString(3);
            var matchState = reader.GetString(4);
            var sourceKind = reader.GetString(5);
            if (reader.Read() || !string.Equals(matchState, "recovery_terminal", StringComparison.Ordinal)
                || !string.Equals(sourceKind, "Journal", StringComparison.Ordinal))
            {
                Control(db, "ROLLBACK");
                return new(false, "reconciliation_exact_finding_required");
            }
            reader.Close();
            var siteId = accountReference.Split('|', StringSplitOptions.None)[0];
            var actor = value.Authorization.Revalidate();
            if (!string.Equals(value.Authorization.SiteId.Value, siteId, StringComparison.Ordinal))
            {
                Control(db, "ROLLBACK");
                return new(false, "reconciliation_site_scope_mismatch");
            }
            var current = _journal.Observe(new(value.OperationId, digest));
            if (current.State is not AuthorityOperationState.AcceptedTerminal
                and not AuthorityOperationState.RejectedBeforeAcceptance
                || current.Receipt is not { } receipt
                || !string.Equals(receipt.JournalDatabaseInstanceId, journalId, StringComparison.Ordinal)
                || receipt.JournalSequence != journalSequence)
            {
                Control(db, "ROLLBACK");
                return new(false, "reconciliation_journal_state_changed");
            }
            var released = Execute(db, """
UPDATE workflow_resource_reservations
SET released_utc=$utc,release_reason_code='exact_terminal_reconciliation'
WHERE operation_id=$operation AND released_utc IS NULL
""", ("$utc", value.ObservedUtc), ("$operation", value.OperationId));
            if (released != 2)
            {
                Control(db, "ROLLBACK");
                return new(false, "reconciliation_exact_reservations_required");
            }
            Execute(db, """
INSERT INTO reconciliation_actions(action_id,finding_id,operation_id,source_reference,
 issuer_person_id,issuer_account_id,issuer_display_label,issuer_role,reason_code,
 observed_utc,resolution_code)
VALUES($action,$finding,$operation,$source,$person,$account,$label,'ItIssuer',$reason,$utc,
 'exact_journal_observation_acknowledged')
""", ("$action", value.ActionId), ("$finding", value.FindingId),
                ("$operation", value.OperationId), ("$source", CanonicalSourceReference(receipt)),
                ("$person", actor.PersonId.Value), ("$account", actor.AccountId.Value),
                ("$label", actor.DisplayLabel), ("$reason", value.ReasonCode),
                ("$utc", value.ObservedUtc));
            var eventSequence = Convert.ToInt64(Scalar(db,
                "SELECT coalesce(max(event_sequence),0)+1 FROM operation_events WHERE operation_id=$operation",
                ("$operation", value.OperationId)), CultureInfo.InvariantCulture);
            Execute(db, """
INSERT INTO operation_events(event_id,operation_id,event_sequence,state_code,event_code,
 safe_result_code,safe_reference,observed_utc,source_kind,
 source_database_instance_id,source_sequence)
VALUES($event,$operation,$sequence,'Reconciled','exact_reconciliation',
 'reconciliation_recorded',$source,$utc,'Recovery',$sourceDatabase,$sourceSequence)
""", ("$event", value.ActionId), ("$operation", value.OperationId),
                ("$sequence", eventSequence), ("$source", CanonicalSourceReference(receipt)),
                ("$utc", value.ObservedUtc), ("$sourceDatabase", receipt.JournalDatabaseInstanceId),
                ("$sourceSequence", receipt.JournalSequence));
            Control(db, "COMMIT");
            return new(true, "reconciliation_recorded");
        }
        catch
        {
            return new(false, SimulationSafeResultCodes.ReconciliationUnknown);
        }
    }

    internal RecoveryActionResult Cancel(string findingId,
        Quarterdeck.Core.Simulation.SyntheticReconciliationAuthorization authorization,
        long observedUtc) =>
        new(false, "recovery_cancellation_retains_lock");

    private (RecoveryMatchState State, string Code) Classify(
        SqliteConnection db, JournalRecoveryProjection item)
    {
        var workflowDatabaseId = Convert.ToString(Scalar(db,
            "SELECT database_instance_id FROM workflow_metadata WHERE singleton_id=1"),
            CultureInfo.InvariantCulture)!;
        using var command = Command(db, """
SELECT approval_id,operation_binding_digest,admission_identity_digest,workflow_database_instance_id,intent_commit_sequence,
 target_site_id,target_account_id,intended_use,fake_device_id,slot
FROM operations WHERE operation_id=$operation
""", ("$operation", item.OperationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (RecoveryMatchState.AbsentLocal, "recovery_absent_local_intent");
        var exactIdentity = string.Equals(reader.GetString(0), item.ApprovalId, StringComparison.Ordinal)
            && FixedEquals(reader.GetString(1), item.BindingDigest)
            && !reader.IsDBNull(2) && FixedEquals(reader.GetString(2), item.AdmissionIdentityDigest)
            && string.Equals(reader.GetString(3), workflowDatabaseId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), item.WorkflowDatabaseInstanceId, StringComparison.Ordinal)
            && reader.GetInt64(4) == item.WorkflowCommitSequence
            && string.Equals(reader.GetString(5), item.SiteId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(6), item.AccountId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(7), item.IntendedUse, StringComparison.Ordinal)
            && string.Equals(reader.GetString(8), item.FakeDeviceId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(9), item.Slot, StringComparison.Ordinal);
        if (reader.Read()) return (RecoveryMatchState.Ambiguous, "recovery_ambiguous_local_intent");
        if (!exactIdentity)
            return (RecoveryMatchState.Mismatch, "recovery_operation_identity_mismatch");
        return item.State switch
        {
            AuthorityOperationState.AcceptedPending => (RecoveryMatchState.Pending, "recovery_pending"),
            AuthorityOperationState.AcceptedTerminal or AuthorityOperationState.RejectedBeforeAcceptance
                => (RecoveryMatchState.Terminal, "recovery_terminal"),
            _ => (RecoveryMatchState.Unknown, "recovery_unknown")
        };
    }

    private static IReadOnlyList<WorkflowRecoveryProjection> ReadWorkflowOnlyUnresolved(
        SqliteConnection db, JournalRecoveryBatch batch)
    {
        using var command = Command(db, """
SELECT o.workflow_database_instance_id,o.intent_commit_sequence,o.operation_id,o.approval_id,
 o.operation_binding_digest,o.admission_identity_digest,o.target_site_id,o.target_account_id,o.intended_use,o.fake_device_id,o.slot
FROM operations o
JOIN operation_events e ON e.operation_id=o.operation_id
JOIN (SELECT operation_id,max(event_sequence) latest FROM operation_events GROUP BY operation_id) x
 ON x.operation_id=e.operation_id AND x.latest=e.event_sequence
ORDER BY o.intent_commit_sequence,o.operation_id
""");
        using var reader = command.ExecuteReader();
        var results = new List<WorkflowRecoveryProjection>();
        while (reader.Read())
        {
            var item = new WorkflowRecoveryProjection(reader.GetString(0), reader.GetInt64(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10));
            var exactJournal = batch.Operations.Any(journal =>
                string.Equals(journal.WorkflowDatabaseInstanceId,
                    item.WorkflowDatabaseInstanceId, StringComparison.Ordinal)
                && journal.WorkflowCommitSequence == item.WorkflowCommitSequence
                && string.Equals(journal.OperationId, item.OperationId, StringComparison.Ordinal)
                && string.Equals(journal.ApprovalId, item.ApprovalId, StringComparison.Ordinal)
                && FixedEquals(journal.BindingDigest, item.BindingDigest)
                && item.AdmissionIdentityDigest is not null
                && FixedEquals(journal.AdmissionIdentityDigest, item.AdmissionIdentityDigest));
            if (!exactJournal) results.Add(item);
        }
        return results.AsReadOnly();
    }

    private WorkflowResultPersistenceResult? ReadResultEventOutside(
        PersistAuthorityObservationCommand value, AuthorityObservation observation)
    {
        try
        {
            using var db = _connections.OpenWorkflow();
            return ReadResultEvent(db, value.EventId, value.OperationId,
                value.BindingDigest, observation);
        }
        catch { return null; }
    }

    private static WorkflowResultPersistenceResult? ReadResultEvent(SqliteConnection db,
        string eventId, string operationId, string digest, AuthorityObservation observation)
    {
        using var command = Command(db, """
SELECT o.operation_binding_digest,e.state_code,e.safe_result_code,
 e.source_database_instance_id,e.source_sequence,e.safe_reference
FROM operation_events e JOIN operations o ON o.operation_id=e.operation_id
WHERE e.event_id=$event AND e.operation_id=$operation
""", ("$event", eventId), ("$operation", operationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var exact = FixedEquals(reader.GetString(0), digest)
            && string.Equals(reader.GetString(1), MapState(observation.State), StringComparison.Ordinal)
            && string.Equals(reader.GetString(2), observation.SafeResultCode, StringComparison.Ordinal)
            && observation.Receipt is { } receipt
            && string.Equals(reader.GetString(3), receipt.JournalDatabaseInstanceId, StringComparison.Ordinal)
            && reader.GetInt64(4) == receipt.JournalSequence
            && string.Equals(reader.GetString(5), CanonicalSourceReference(receipt), StringComparison.Ordinal);
        return exact
            ? new(WorkflowResultPersistenceStatus.PersistedAfterReadback,
                "result_observation_persisted_after_readback")
            : new(WorkflowResultPersistenceStatus.Mismatch, "result_event_identity_mismatch");
    }

    private static string MapState(AuthorityOperationState state) => state switch
    {
        AuthorityOperationState.AcceptedPending => "Pending",
        AuthorityOperationState.AcceptedTerminal => "AcceptedTerminal",
        AuthorityOperationState.RejectedBeforeAcceptance => "RejectedBeforeAcceptance",
        _ => "Unknown"
    };

    private void SetGlobalDiscoveryBlocked(bool blocked)
    {
        lock (_runtime.DispatchSync)
            Interlocked.Exchange(ref _runtime.GlobalDiscoveryBlocked, blocked ? 1 : 0);
    }

    private static string CanonicalSourceReference(AuthorityOperationReceipt receipt)
    {
        var resultToken = receipt.ResultReference is null
            ? "none"
            : "sha256:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(receipt.ResultReference)))
                .ToLowerInvariant();
        return $"journal:{receipt.JournalDatabaseInstanceId}:{receipt.JournalSequence}:{resultToken}";
    }

    private static void RequireGuid(string value, string code)
    { if (!Guid.TryParseExact(value, "D", out _)) throw new ArgumentException(code); }
    private static void RequireDigest(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Skip(7).Any(c => !(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')))
            throw new ArgumentException("digest_invalid");
    }
    private static bool FixedEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left), System.Text.Encoding.ASCII.GetBytes(right));
    private static SqliteCommand Command(SqliteConnection db, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }
    private static int Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(db, sql, values); return command.ExecuteNonQuery(); }
    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    { using var command = Command(db, sql, values); return command.ExecuteScalar(); }
    private static void Control(SqliteConnection db, string sql) => Execute(db, sql);
    private static void Rollback(SqliteConnection db)
    { try { Control(db, "ROLLBACK"); } catch (SqliteException) { } }

    private readonly record struct WorkflowRecoveryProjection(
        string WorkflowDatabaseInstanceId, long WorkflowCommitSequence,
        string OperationId, string ApprovalId, string BindingDigest,
        string? AdmissionIdentityDigest,
        string SiteId, string AccountId, string IntendedUse,
        string FakeDeviceId, string Slot);

    internal sealed class DispatchAuthorization
    {
        private readonly SimulationRecoveryCoordinator _owner;
        private readonly SimulationAuthorityJournalRepository _journal;
        private readonly string _operationId;
        private readonly string _bindingDigest;

        internal DispatchAuthorization(SimulationRecoveryCoordinator owner,
            SimulationAuthorityJournalRepository journal,
            AcceptAuthorityOperationCommand command)
        {
            _owner = owner;
            _journal = journal;
            _operationId = command.OperationId;
            _bindingDigest = command.BindingDigest;
        }

        internal void Require(SimulationAuthorityJournalRepository journal,
            AcceptAuthorityOperationCommand command)
        {
            if (!Monitor.IsEntered(_owner._runtime.DispatchSync)
                || !ReferenceEquals(_journal, journal)
                || !string.Equals(_operationId, command.OperationId, StringComparison.Ordinal)
                || !FixedEquals(_bindingDigest, command.BindingDigest))
            {
                throw new InvalidOperationException("recovery_dispatch_authorization_invalid");
            }
        }
    }
}
