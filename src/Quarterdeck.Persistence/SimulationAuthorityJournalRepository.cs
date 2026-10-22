using System.Globalization;
using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence;

/// <summary>
/// Independent fake-authority journal. A dispatch call can record only a
/// durable rejection or a synchronized fake acceptance; it cannot perform an
/// external effect or confer permission to perform one.
/// </summary>
internal sealed class SimulationAuthorityJournalRepository
{
    private readonly SimulationSqliteConnections _connections;
    private readonly AuthorityCallCounters _counters;
    private readonly bool _completeAtDispatch;

    internal object StoreSetToken => _connections.StoreSetToken;

    internal SimulationAuthorityJournalRepository(
        SimulationSqliteConnections connections,
        AuthorityCallCounters counters,
        bool completeAtDispatch = false)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _counters = counters ?? throw new ArgumentNullException(nameof(counters));
        _completeAtDispatch = completeAtDispatch;
    }

    internal long AcceptanceCount
    {
        get
        {
            using var db = _connections.OpenJournal();
            return Convert.ToInt64(Scalar(db, """
SELECT count(DISTINCT operation_id) FROM authority_events
WHERE event_code IN ('AcceptedPending','AcceptedTerminal')
"""), CultureInfo.InvariantCulture);
        }
    }

    internal AuthorityDispatchResult Dispatch(
        AcceptAuthorityOperationCommand command,
        SimulationRecoveryCoordinator.DispatchAuthorization authorization,
        IAuthorityJournalFault? fault = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(authorization);
        authorization.Require(this, command);
        _counters.RecordDispatchCall();
        fault ??= NoAuthorityJournalFault.Instance;

        try
        {
            RequireCommand(command);
            var existing = ReadExisting(command);
            if (existing.State is not AuthorityOperationState.Absent)
            {
                return ExistingResult(existing);
            }

            using var db = _connections.OpenJournal();
            Control(db, "BEGIN IMMEDIATE");
            var committed = false;
            try
            {
                var raced = ReadExisting(db, command);
                if (raced.State is not AuthorityOperationState.Absent)
                {
                    Control(db, "ROLLBACK");
                    return ExistingResult(raced);
                }

                if (Exists(db,
                    "SELECT 1 FROM approval_consumptions WHERE approval_id=$value",
                    ("$value", command.ApprovalId)))
                {
                    Control(db, "ROLLBACK");
                    return new(AuthorityDispatchStatus.AlreadyConsumed,
                        "authority_approval_already_consumed", null);
                }

                var observedUtc = command.ObservedUtc;
                InsertConsumption(db, command, observedUtc);
                var conflict = HasResourceConflict(db, command);

                fault.Observe(AuthorityJournalFaultPoint.AfterPreparedBeforeAcceptance);
                if (conflict)
                {
                    var receipt = CommitDecision(db, command,
                        AuthorityOperationState.RejectedBeforeAcceptance,
                        "authority_resource_conflict", observedUtc);
                    committed = true;
                    return new(AuthorityDispatchStatus.RejectedBeforeAcceptance,
                        "authority_resource_conflict", receipt);
                }

                var committer = new JournalAcceptanceCommitter(db, command,
                    _completeAtDispatch
                        ? AuthorityOperationState.AcceptedTerminal
                        : AuthorityOperationState.AcceptedPending);
                try
                {
                    command.Admission.Authority.CommitSynchronizedAcceptance(
                        command.Admission, committer);
                    committed = true;
                }
                catch (InvalidOperationException) when (!committer.AttemptedCommit)
                {
                    var receipt = CommitDecision(db, command,
                        AuthorityOperationState.RejectedBeforeAcceptance,
                        "authority_current_state_rejected", observedUtc);
                    committed = true;
                    return new(AuthorityDispatchStatus.RejectedBeforeAcceptance,
                        "authority_current_state_rejected", receipt);
                }

                fault.Observe(AuthorityJournalFaultPoint.AfterCommit);
                return committer.Receipt?.State == AuthorityOperationState.AcceptedTerminal
                    ? new(AuthorityDispatchStatus.AcceptedTerminal,
                        "authority_accepted_terminal", committer.Receipt)
                    : new(AuthorityDispatchStatus.AcceptedPending,
                        "authority_accepted_pending", committer.Receipt);
            }
            catch
            {
                if (!committed)
                {
                    try { Control(db, "ROLLBACK"); }
                    catch (SqliteException) { }
                }
                throw;
            }
        }
        catch
        {
            var observed = SafeReadExisting(command);
            if (observed.State is AuthorityOperationState.AcceptedPending
                or AuthorityOperationState.AcceptedTerminal)
            {
                return new(AuthorityDispatchStatus.AcceptedPendingAfterReadback,
                    "authority_commit_uncertain", observed.Receipt);
            }
            return observed.State switch
            {
                AuthorityOperationState.Absent => new(
                    AuthorityDispatchStatus.NotAccepted,
                    "authority_not_accepted", null),
                AuthorityOperationState.Mismatch => new(
                    AuthorityDispatchStatus.ExistingOperationMismatch,
                    observed.SafeResultCode, null),
                _ => new(AuthorityDispatchStatus.Uncertain,
                    SimulationSafeResultCodes.AuthorityJournalUnavailable, observed.Receipt)
            };
        }
    }

    internal AuthorityObservation Observe(ObserveAuthorityOperationCommand command)
    {
        try
        {
            RequireGuid(command.OperationId, "authority_operation_id_invalid");
            RequireDigest(command.BindingDigest);
            using var db = _connections.OpenJournal();
            return ReadObservation(db, command.OperationId, command.BindingDigest);
        }
        catch
        {
            return new(AuthorityOperationState.Unreadable,
                SimulationSafeResultCodes.AuthorityObservationUnreadable, null);
        }
    }

    internal JournalRecoveryBatch EnumerateForRecovery()
    {
        using var db = _connections.OpenJournal();
        Control(db, "BEGIN");
        var journalId = Convert.ToString(Scalar(db,
            "SELECT database_instance_id FROM journal_metadata WHERE singleton_id=1"),
            CultureInfo.InvariantCulture)!;
        var sequenceStart = Convert.ToInt64(Scalar(db,
            "SELECT journal_sequence FROM journal_metadata WHERE singleton_id=1"),
            CultureInfo.InvariantCulture);
        using var command = CreateCommand(db, """
SELECT m.database_instance_id,e.journal_sequence,o.operation_id,o.approval_id,
 o.binding_digest,o.admission_identity_digest,c.workflow_database_instance_id,c.workflow_commit_sequence,
 o.target_site_id,o.target_account_id,o.intended_use,
 o.fake_device_id,o.slot,e.event_code,e.safe_result_code,e.safe_reference
FROM authority_operations o
JOIN approval_consumptions c ON c.operation_id=o.operation_id
JOIN journal_metadata m ON m.singleton_id=1
JOIN authority_events e ON e.operation_id=o.operation_id
JOIN (SELECT operation_id,max(journal_sequence) AS latest_sequence
      FROM authority_events GROUP BY operation_id) latest
 ON latest.operation_id=e.operation_id AND latest.latest_sequence=e.journal_sequence
ORDER BY e.journal_sequence,o.operation_id
""");
        using var reader = command.ExecuteReader();
        var values = new List<JournalRecoveryProjection>();
        while (reader.Read())
        {
            values.Add(new(reader.GetString(0), reader.GetInt64(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetInt64(7), reader.GetString(8),
                reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), ParseState(reader.GetString(13)), reader.GetString(14),
                reader.IsDBNull(15) ? null : reader.GetString(15)));
        }
        reader.Close();
        var sequenceEnd = Convert.ToInt64(Scalar(db,
            "SELECT journal_sequence FROM journal_metadata WHERE singleton_id=1"),
            CultureInfo.InvariantCulture);
        Control(db, "COMMIT");
        if (sequenceStart != sequenceEnd)
            throw new InvalidOperationException("authority_recovery_snapshot_changed");
        return new(journalId, sequenceStart, sequenceEnd, values.AsReadOnly());
    }

    internal JournalWatermark ReadWatermark()
    {
        using var db = _connections.OpenJournal();
        using var command = CreateCommand(db, """
SELECT database_instance_id,journal_sequence FROM journal_metadata WHERE singleton_id=1
""");
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("authority_journal_metadata_missing");
        var result = new JournalWatermark(reader.GetString(0), reader.GetInt64(1));
        if (reader.Read()) throw new InvalidOperationException("authority_journal_metadata_ambiguous");
        return result;
    }

    private AuthorityObservation SafeReadExisting(AcceptAuthorityOperationCommand command)
    {
        try { return ReadExisting(command); }
        catch
        {
            return new(AuthorityOperationState.Unreadable,
                SimulationSafeResultCodes.AuthorityObservationUnreadable, null);
        }
    }

    private static AuthorityDispatchResult ExistingResult(AuthorityObservation observation) =>
        observation.State switch
        {
            AuthorityOperationState.AcceptedPending => new(
                AuthorityDispatchStatus.AcceptedPendingAfterReadback,
                "authority_exact_operation_already_accepted", observation.Receipt),
            AuthorityOperationState.AcceptedTerminal => new(
                AuthorityDispatchStatus.AlreadyConsumed,
                "authority_exact_operation_terminal", observation.Receipt),
            AuthorityOperationState.RejectedBeforeAcceptance => new(
                AuthorityDispatchStatus.AlreadyConsumed,
                "authority_exact_operation_rejected", observation.Receipt),
            AuthorityOperationState.Mismatch => new(
                AuthorityDispatchStatus.ExistingOperationMismatch,
                "authority_operation_binding_mismatch", null),
            _ => new(AuthorityDispatchStatus.Uncertain,
                "authority_observation_unreadable", observation.Receipt)
        };

    private sealed class JournalAcceptanceCommitter(
        SqliteConnection db,
        AcceptAuthorityOperationCommand command,
        AuthorityOperationState acceptedState) : ISyntheticDispatchAcceptanceCommitter
    {
        internal bool AttemptedCommit { get; private set; }
        internal AuthorityOperationReceipt? Receipt { get; private set; }

        public void CommitAccepted(SyntheticDispatchAcceptanceContext context)
        {
            AttemptedCommit = true;
            InsertOperation(db, command, acceptedState,
                context.ObservedAt.ToUnixTimeMilliseconds());
            InsertReservations(db, command, context.ObservedAt.ToUnixTimeMilliseconds());
            Receipt = CommitEventsAndTransaction(db, command,
                acceptedState,
                acceptedState == AuthorityOperationState.AcceptedTerminal
                    ? "authority_accepted_terminal" : "authority_accepted_pending",
                context.ObservedAt.ToUnixTimeMilliseconds());
        }
    }

    private static AuthorityOperationReceipt CommitDecision(
        SqliteConnection db,
        AcceptAuthorityOperationCommand command,
        AuthorityOperationState state,
        string safeCode,
        long observedUtc)
    {
        InsertOperation(db, command, state, observedUtc);
        return CommitEventsAndTransaction(db, command, state, safeCode, observedUtc);
    }

    private static void InsertOperation(
        SqliteConnection db,
        AcceptAuthorityOperationCommand command,
        AuthorityOperationState state,
        long observedUtc)
    {
        var stateText = state.ToString();
        Execute(db, """
INSERT INTO authority_operations(operation_id,approval_id,synthetic_public_request_reference,binding_digest,admission_identity_digest,case_id,formal_version,attempt_number,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,state,result_reference,created_utc,updated_utc)
VALUES($operation,$approval,$reference,$digest,$admissionDigest,$case,$version,$attempt,$site,$account,$use,$device,$slot,$authority,$template,$state,NULL,$utc,$utc)
""", ("$operation", command.OperationId), ("$approval", command.ApprovalId),
            ("$reference", command.SyntheticPublicRequestReference),
            ("$digest", command.BindingDigest),
            ("$admissionDigest", command.Admission.IdentityDigest), ("$case", command.CaseId),
            ("$version", command.FormalVersion), ("$attempt", command.AttemptNumber),
            ("$site", command.TargetSiteId), ("$account", command.TargetAccountId),
            ("$use", command.IntendedUse), ("$device", command.FakeDeviceId),
            ("$slot", command.Slot), ("$authority", command.AuthorityId),
            ("$template", command.Template), ("$state", stateText), ("$utc", observedUtc));
    }

    private static AuthorityOperationReceipt CommitEventsAndTransaction(
        SqliteConnection db,
        AcceptAuthorityOperationCommand command,
        AuthorityOperationState state,
        string safeCode,
        long observedUtc)
    {
        var stateText = state.ToString();
        var firstSequence = NextSequence(db);
        var finalSequence = checked(firstSequence + 1);
        var journalId = Convert.ToString(Scalar(db,
            "SELECT database_instance_id FROM journal_metadata WHERE singleton_id=1"),
            CultureInfo.InvariantCulture)!;
        InsertEvent(db, command.OperationId, firstSequence, "CallObserved",
            "authority_call_observed", observedUtc);
        InsertEvent(db, command.OperationId, finalSequence, stateText, safeCode, observedUtc);
        SetSequence(db, finalSequence);
        Control(db, "COMMIT");
        return new(command.OperationId, command.ApprovalId, command.BindingDigest,
            journalId, finalSequence, state, null);
    }

    private static void InsertConsumption(
        SqliteConnection db,
        AcceptAuthorityOperationCommand command,
        long observedUtc) => Execute(db, """
INSERT INTO approval_consumptions(approval_id,operation_id,binding_digest,consumed_utc,workflow_database_instance_id,workflow_commit_sequence)
VALUES($approval,$operation,$digest,$utc,$workflow,$sequence)
""", ("$approval", command.ApprovalId), ("$operation", command.OperationId),
        ("$digest", command.BindingDigest), ("$utc", observedUtc),
        ("$workflow", command.WorkflowDatabaseInstanceId),
        ("$sequence", command.WorkflowCommitSequence));

    private static bool HasResourceConflict(SqliteConnection db, AcceptAuthorityOperationCommand command) =>
        Exists(db, """
SELECT 1 FROM authority_resource_reservations
WHERE released_utc IS NULL AND site_id=$site AND
 ((resource_kind='AccountUse' AND account_id=$account AND intended_use=$use) OR
  (resource_kind='DeviceSlot' AND fake_device_id=$device AND slot=$slot))
LIMIT 1
""", ("$site", command.TargetSiteId), ("$account", command.TargetAccountId),
            ("$use", command.IntendedUse), ("$device", command.FakeDeviceId),
            ("$slot", command.Slot));

    private static void InsertReservations(SqliteConnection db, AcceptAuthorityOperationCommand command, long utc)
    {
        Execute(db, """
INSERT INTO authority_resource_reservations(operation_id,resource_kind,site_id,account_id,intended_use,fake_device_id,slot,reserved_utc)
VALUES($operation,'AccountUse',$site,$account,$use,NULL,NULL,$utc)
""", ("$operation", command.OperationId), ("$site", command.TargetSiteId),
            ("$account", command.TargetAccountId), ("$use", command.IntendedUse), ("$utc", utc));
        Execute(db, """
INSERT INTO authority_resource_reservations(operation_id,resource_kind,site_id,account_id,intended_use,fake_device_id,slot,reserved_utc)
VALUES($operation,'DeviceSlot',$site,NULL,NULL,$device,$slot,$utc)
""", ("$operation", command.OperationId), ("$site", command.TargetSiteId),
            ("$device", command.FakeDeviceId), ("$slot", command.Slot), ("$utc", utc));
    }

    private static AuthorityObservation ReadObservation(SqliteConnection db, string operationId, string digest)
    {
        using var command = CreateCommand(db, """
SELECT o.approval_id,o.binding_digest,m.database_instance_id,e.journal_sequence,
 e.event_code,e.safe_result_code,e.safe_reference
FROM authority_operations o
JOIN journal_metadata m ON m.singleton_id=1
JOIN authority_events e ON e.operation_id=o.operation_id
WHERE o.operation_id=$operation
ORDER BY e.journal_sequence DESC LIMIT 1
""", ("$operation", operationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new(AuthorityOperationState.Absent, "authority_operation_absent", null);
        var actualDigest = reader.GetString(1);
        if (!FixedEquals(actualDigest, digest))
            return new(AuthorityOperationState.Mismatch,
                "authority_operation_binding_mismatch", null);
        var state = ParseState(reader.GetString(4));
        var receipt = new AuthorityOperationReceipt(operationId, reader.GetString(0),
            actualDigest, reader.GetString(2), reader.GetInt64(3), state,
            reader.IsDBNull(6) ? null : reader.GetString(6));
        return new(state, reader.GetString(5), receipt);
    }

    private AuthorityObservation ReadExisting(AcceptAuthorityOperationCommand command)
    {
        using var db = _connections.OpenJournal();
        return ReadExisting(db, command);
    }

    private static AuthorityObservation ReadExisting(
        SqliteConnection db,
        AcceptAuthorityOperationCommand value)
    {
        using var command = CreateCommand(db, """
SELECT o.binding_digest,o.admission_identity_digest,o.approval_id,o.case_id,o.formal_version,o.attempt_number,
 o.synthetic_public_request_reference,o.target_site_id,o.target_account_id,o.intended_use,
 o.fake_device_id,o.slot,o.authority_id,o.template,
 c.workflow_database_instance_id,c.workflow_commit_sequence
FROM authority_operations o
JOIN approval_consumptions c ON c.operation_id=o.operation_id
WHERE o.operation_id=$operation
""", ("$operation", value.OperationId));
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new(AuthorityOperationState.Absent, "authority_operation_absent", null);
        var exact = FixedEquals(reader.GetString(0), value.BindingDigest)
            && FixedEquals(reader.GetString(1), value.Admission.IdentityDigest)
            && string.Equals(reader.GetString(2), value.ApprovalId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(3), value.CaseId, StringComparison.Ordinal)
            && reader.GetInt64(4) == value.FormalVersion
            && reader.GetInt64(5) == value.AttemptNumber
            && string.Equals(reader.GetString(6), value.SyntheticPublicRequestReference, StringComparison.Ordinal)
            && string.Equals(reader.GetString(7), value.TargetSiteId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(8), value.TargetAccountId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(9), value.IntendedUse, StringComparison.Ordinal)
            && string.Equals(reader.GetString(10), value.FakeDeviceId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(11), value.Slot, StringComparison.Ordinal)
            && string.Equals(reader.GetString(12), value.AuthorityId, StringComparison.Ordinal)
            && string.Equals(reader.GetString(13), value.Template, StringComparison.Ordinal)
            && string.Equals(reader.GetString(14), value.WorkflowDatabaseInstanceId, StringComparison.Ordinal)
            && reader.GetInt64(15) == value.WorkflowCommitSequence;
        reader.Close();
        return exact
            ? ReadObservation(db, value.OperationId, value.BindingDigest)
            : new(AuthorityOperationState.Mismatch,
                "authority_operation_identity_mismatch", null);
    }

    private static AuthorityOperationState ParseState(string value) => value switch
    {
        "AcceptedPending" => AuthorityOperationState.AcceptedPending,
        "AcceptedTerminal" => AuthorityOperationState.AcceptedTerminal,
        "RejectedBeforeAcceptance" => AuthorityOperationState.RejectedBeforeAcceptance,
        _ => AuthorityOperationState.Unreadable
    };

    private static void InsertEvent(SqliteConnection db, string operationId, long sequence,
        string code, string safeCode, long utc) => Execute(db, """
INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
VALUES($event,$sequence,$operation,$code,$safe,NULL,$utc)
""", ("$event", Guid.NewGuid().ToString("D")), ("$sequence", sequence),
        ("$operation", operationId), ("$code", code), ("$safe", safeCode), ("$utc", utc));

    private static long NextSequence(SqliteConnection db) => checked(Convert.ToInt64(
        Scalar(db, "SELECT journal_sequence FROM journal_metadata WHERE singleton_id=1"),
        CultureInfo.InvariantCulture) + 1);

    private static void SetSequence(SqliteConnection db, long sequence) => Execute(db,
        "UPDATE journal_metadata SET journal_sequence=$sequence WHERE singleton_id=1",
        ("$sequence", sequence));

    private static bool Exists(SqliteConnection db, string sql, params (string, object?)[] values) =>
        Scalar(db, sql, values) is not null;

    private static void RequireCommand(AcceptAuthorityOperationCommand command)
    {
        RequireGuid(command.OperationId, "authority_operation_id_invalid");
        RequireGuid(command.ApprovalId, "authority_approval_id_invalid");
        RequireGuid(command.WorkflowDatabaseInstanceId, "authority_workflow_instance_invalid");
        RequireDigest(command.BindingDigest);
        RequireToken(command.CaseId, 80, "authority_case_id_invalid");
        RequireToken(command.SyntheticPublicRequestReference, 160, "authority_request_reference_invalid");
        if (command.FormalVersion <= 0 || command.AttemptNumber <= 0
            || command.WorkflowCommitSequence <= 0 || command.ObservedUtc < 0)
            throw new InvalidOperationException("authority_operation_identity_invalid");
    }

    private static void RequireGuid(string value, string code)
    {
        if (!Guid.TryParseExact(value, "D", out _)) throw new ArgumentException(code);
    }

    private static void RequireDigest(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)
            || value.Skip(7).Any(character =>
                !(char.IsAsciiDigit(character) || character is >= 'a' and <= 'f')))
            throw new ArgumentException("authority_binding_digest_invalid");
    }

    private static void RequireToken(string value, int maximumLength, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException(code);
    }

    private static bool FixedEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(left),
            System.Text.Encoding.ASCII.GetBytes(right));

    private static int Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        using var command = CreateCommand(db, sql, values);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        using var command = CreateCommand(db, sql, values);
        return command.ExecuteScalar();
    }

    private static SqliteCommand CreateCommand(SqliteConnection db, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static void Control(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
