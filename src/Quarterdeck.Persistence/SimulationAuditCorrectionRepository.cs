using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence;

internal sealed record AppendAuditCorrectionCommand(
    string CorrectionId,
    string AuditEventId,
    SyntheticActor Author,
    long CorrectedUtc,
    string ReasonCode,
    string? ReplacementReasonCode,
    string? ReplacementResultCode,
    string? ReplacementSourceReference);

internal readonly record struct AuditCorrectionResult(bool Recorded, string SafeResultCode);

internal interface IAuditCorrectionFault
{
    void BeforeCommit();
}

internal sealed class NoAuditCorrectionFault : IAuditCorrectionFault
{
    internal static readonly NoAuditCorrectionFault Instance = new();
    private NoAuditCorrectionFault() { }
    public void BeforeCommit() { }
}

/// <summary>
/// The only supported correction writer. Original audit rows remain immutable;
/// a current IT issuer may append a bounded correction referencing one event.
/// Failed authorization never creates a compensating or denial audit row.
/// </summary>
internal sealed class SimulationAuditCorrectionRepository
{
    private readonly SimulationSqliteConnections _connections;
    private readonly SyntheticIdentityDirectory _directory;

    internal SimulationAuditCorrectionRepository(
        SimulationSqliteConnections connections,
        SyntheticIdentityDirectory directory)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
    }

    internal AuditCorrectionResult Append(
        AppendAuditCorrectionCommand value,
        IAuditCorrectionFault? fault = null)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!Guid.TryParseExact(value.CorrectionId, "D", out _)
                || !Guid.TryParseExact(value.AuditEventId, "D", out _)
                || value.CorrectedUtc < 0
                || !Bounded(value.ReasonCode, 80)
                || !OptionalBounded(value.ReplacementReasonCode, 80)
                || !OptionalBounded(value.ReplacementResultCode, 80)
                || !OptionalBounded(value.ReplacementSourceReference, 160)
                || value.ReplacementReasonCode is null
                    && value.ReplacementResultCode is null
                    && value.ReplacementSourceReference is null)
                return new(false, SimulationSafeResultCodes.AuditCorrectionInvalid);

            SyntheticActor current;
            try { current = _directory.Resolve(value.Author.AccountId); }
            catch { return new(false, SimulationSafeResultCodes.AuditCorrectionUnauthorized); }
            if (!ReferenceEquals(_directory, value.Author.Directory)
                || !current.AccountEnabled
                || !current.HasRole(SyntheticWorkflowRole.ItIssuer)
                || current.PersonId != value.Author.PersonId
                || !string.Equals(current.DisplayLabel, value.Author.DisplayLabel,
                    StringComparison.Ordinal))
                return new(false, SimulationSafeResultCodes.AuditCorrectionUnauthorized);

            using var db = _connections.OpenWorkflow();
            Control(db, "BEGIN IMMEDIATE");
            try
            {
                using var target = Command(db,
                    "SELECT occurred_utc FROM audit_events WHERE audit_event_id=$event",
                    ("$event", value.AuditEventId));
                var occurred = target.ExecuteScalar();
                if (occurred is null || occurred is DBNull)
                {
                    Control(db, "ROLLBACK");
                    return new(false, SimulationSafeResultCodes.AuditCorrectionTargetMissing);
                }
                if (value.CorrectedUtc < Convert.ToInt64(occurred))
                {
                    Control(db, "ROLLBACK");
                    return new(false, SimulationSafeResultCodes.AuditCorrectionInvalid);
                }

                using var insert = Command(db, """
INSERT INTO audit_corrections(correction_id,audit_event_id,author_person_id,
 author_account_id,author_display_label,author_role,reason_code,corrected_utc,
 replacement_reason_code,replacement_result_code,replacement_source_reference)
VALUES($id,$event,$person,$account,$label,'ItIssuer',$reason,$utc,$replacementReason,
 $replacementResult,$replacementReference)
""", ("$id", value.CorrectionId), ("$event", value.AuditEventId),
                    ("$person", current.PersonId.Value), ("$account", current.AccountId.Value),
                    ("$label", current.DisplayLabel), ("$reason", value.ReasonCode),
                    ("$utc", value.CorrectedUtc),
                    ("$replacementReason", value.ReplacementReasonCode),
                    ("$replacementResult", value.ReplacementResultCode),
                    ("$replacementReference", value.ReplacementSourceReference));
                insert.ExecuteNonQuery();
                (fault ?? NoAuditCorrectionFault.Instance).BeforeCommit();
                Control(db, "COMMIT");
                return new(true, SimulationSafeResultCodes.AuditCorrectionRecorded);
            }
            catch
            {
                Rollback(db);
                return new(false, SimulationSafeResultCodes.AuditCorrectionStoreUnavailable);
            }
        }
        catch (Exception exception)
        {
            return new(false, SimulationSafeResultCodes.Map(
                exception, SimulationDiagnosticBoundary.AuditCorrection));
        }
    }

    private static bool Bounded(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool OptionalBounded(string? value, int maximum) =>
        value is null || Bounded(value, maximum);
    private static SqliteCommand Command(SqliteConnection db, string sql,
        params (string Name, object? Value)[] values)
    {
        var command = db.CreateCommand(); command.CommandText = sql;
        foreach (var value in values)
            command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
        return command;
    }
    private static void Control(SqliteConnection db, string sql)
    { using var command = Command(db, sql); command.ExecuteNonQuery(); }
    private static void Rollback(SqliteConnection db)
    { try { Control(db, "ROLLBACK"); } catch (SqliteException) { } }
}
