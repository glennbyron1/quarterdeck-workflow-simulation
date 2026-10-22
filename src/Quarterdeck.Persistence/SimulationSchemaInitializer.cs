using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence;

public sealed record SimulationDatabaseSchemaObservation(
    string Kind,
    int ApplicationId,
    int SchemaVersion,
    Guid DatabaseInstanceId,
    long Sequence,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Indexes);

public sealed record SimulationSchemaObservation(
    SimulationDatabaseSchemaObservation Workflow,
    SimulationDatabaseSchemaObservation Journal);

/// <summary>Creates or validates the independent version-1 synthetic stores.</summary>
public sealed class SimulationSchemaInitializer(SimulationSqliteConnections connections)
{
    internal const int WorkflowApplicationId = 0x51445731; // QDW1
    internal const int JournalApplicationId = 0x51444A31; // QDJ1
    internal const int CurrentSchemaVersion = 6;
    private readonly SimulationSqliteConnections _connections = connections
        ?? throw new ArgumentNullException(nameof(connections));

    public SimulationSchemaObservation Initialize(string sourceRevision)
    {
        RequireSourceRevision(sourceRevision);
        using var workflow = _connections.OpenWorkflow();
        var workflowObservation = InitializeOne(
            workflow, "workflow", WorkflowApplicationId,
            SimulationSchemaSql.Workflow, SimulationSchemaSql.WorkflowTables,
            SimulationSchemaSql.WorkflowIndexes, SimulationSchemaSql.WorkflowTriggers, sourceRevision);
        using var journal = _connections.OpenJournal();
        var journalObservation = InitializeOne(
            journal, "journal", JournalApplicationId,
            SimulationSchemaSql.Journal, SimulationSchemaSql.JournalTables,
            SimulationSchemaSql.JournalIndexes, SimulationSchemaSql.JournalTriggers, sourceRevision);
        return new(workflowObservation, journalObservation);
    }

    private static SimulationDatabaseSchemaObservation InitializeOne(
        SqliteConnection connection,
        string kind,
        int applicationId,
        string schema,
        IReadOnlyList<string> requiredTables,
        IReadOnlyList<string> requiredIndexes,
        IReadOnlyList<string> requiredTriggers,
        string sourceRevision)
    {
        var observedApplicationId = ReadInt(connection, "PRAGMA application_id");
        var observedVersion = ReadInt(connection, "PRAGMA user_version");
        if (observedApplicationId == 0 && observedVersion == 0)
        {
            if (HasUserObjects(connection))
            {
                throw new InvalidOperationException("simulation_schema_unrecognized_database");
            }
            ApplyInitialSchema(connection, applicationId, schema, kind, sourceRevision);
        }
        else if (observedApplicationId != applicationId)
        {
            throw new InvalidOperationException("simulation_schema_application_mismatch");
        }
        else if (observedVersion > CurrentSchemaVersion)
        {
            throw new InvalidOperationException("simulation_schema_newer_version");
        }
        else if (observedVersion == 1)
        {
            ApplyVersionTwoMigration(connection, kind, sourceRevision);
            ApplyVersionThreeMigration(connection, kind, sourceRevision);
            ApplyVersionFourMigration(connection, kind, sourceRevision);
        }
        else if (observedVersion == 2)
        {
            ApplyVersionThreeMigration(connection, kind, sourceRevision);
            ApplyVersionFourMigration(connection, kind, sourceRevision);
        }
        else if (observedVersion == 3)
        {
            ApplyVersionFourMigration(connection, kind, sourceRevision);
        }
        else if (observedVersion != 4 && observedVersion != 5 && observedVersion != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("simulation_schema_unsupported_version");
        }

        if (ReadInt(connection, "PRAGMA user_version") == 4)
            ApplyVersionFiveMigration(connection, kind, sourceRevision);
        if (ReadInt(connection, "PRAGMA user_version") == 5)
            ApplyVersionSixMigration(connection, kind, sourceRevision);
        return Validate(connection, kind, applicationId, requiredTables, requiredIndexes, requiredTriggers);
    }

    internal static void ApplyInitialSchema(
        SqliteConnection connection,
        int applicationId,
        string schema,
        string kind,
        string sourceRevision)
    {
        ExecuteControl(connection, "BEGIN EXCLUSIVE");
        try
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = schema;
                command.ExecuteNonQuery();
            }
            var instanceId = Guid.NewGuid();
            using (var metadata = connection.CreateCommand())
            {
                metadata.CommandText = kind == "workflow"
                    ? "INSERT INTO workflow_metadata(singleton_id, schema_name, database_instance_id, commit_sequence) VALUES(1, $name, $instance, 0)"
                    : "INSERT INTO journal_metadata(singleton_id, schema_name, database_instance_id, journal_sequence) VALUES(1, $name, $instance, 0)";
                metadata.Parameters.AddWithValue("$name", "quarterdeck-v3-simulation-" + kind);
                metadata.Parameters.AddWithValue("$instance", instanceId.ToString("D"));
                metadata.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(1, 'initial-typed-schema', $utc, $source)"
                    : "INSERT INTO journal_schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(1, 'initial-typed-schema', $utc, $source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(2, 'result-recovery-anchors', $utc, $source)"
                    : "INSERT INTO journal_schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(2, 'result-recovery-anchors', $utc, $source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(3, 'command-denial-ledger', $utc, $source)"
                    : "INSERT INTO journal_schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(3, 'command-denial-ledger-compatibility', $utc, $source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(4, 'generated-case-and-department-catalogue', $utc, $source)"
                    : "INSERT INTO journal_schema_migrations(version, migration_name, applied_utc, source_revision) VALUES(4, 'generated-case-and-department-catalogue-compatibility', $utc, $source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations VALUES(5,'name-change-requests',$utc,$source)"
                    : "INSERT INTO journal_schema_migrations VALUES(5,'name-change-compatibility',$utc,$source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations VALUES(6,'credential-subject-bindings',$utc,$source)"
                    : "INSERT INTO journal_schema_migrations VALUES(6,'credential-subject-binding-compatibility',$utc,$source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            using (var identity = connection.CreateCommand())
            {
                identity.CommandText = $"PRAGMA application_id={applicationId}; PRAGMA user_version={CurrentSchemaVersion};";
                identity.ExecuteNonQuery();
            }
            ExecuteControl(connection, "COMMIT");
        }
        catch
        {
            try { ExecuteControl(connection, "ROLLBACK"); }
            catch (SqliteException) { /* Preserve the initiating failure. */ }
            throw;
        }
    }

    private static void ApplyVersionFiveMigration(SqliteConnection connection, string kind, string sourceRevision)
    {
        ExecuteControl(connection, "BEGIN EXCLUSIVE");
        try
        {
            if (kind == "workflow") ExecuteControl(connection, SimulationSchemaSql.NameChanges);
            using var command = connection.CreateCommand();
            command.CommandText = kind == "workflow"
                ? "INSERT INTO schema_migrations VALUES(5,'name-change-requests',$utc,$source)"
                : "INSERT INTO journal_schema_migrations VALUES(5,'name-change-compatibility',$utc,$source)";
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$source", sourceRevision);
            command.ExecuteNonQuery();
            ExecuteControl(connection, "PRAGMA user_version=5");
            ExecuteControl(connection, "COMMIT");
        }
        catch { try { ExecuteControl(connection, "ROLLBACK"); } catch (SqliteException) { } throw; }
    }

    private static void ApplyVersionSixMigration(SqliteConnection connection, string kind, string sourceRevision)
    {
        ExecuteControl(connection, "BEGIN EXCLUSIVE");
        try
        {
            if (kind == "workflow") ExecuteControl(connection, SimulationSchemaSql.WorkflowV6Migration);
            using var command = connection.CreateCommand();
            command.CommandText = kind == "workflow"
                ? "INSERT INTO schema_migrations VALUES(6,'credential-subject-bindings',$utc,$source)"
                : "INSERT INTO journal_schema_migrations VALUES(6,'credential-subject-binding-compatibility',$utc,$source)";
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$source", sourceRevision);
            command.ExecuteNonQuery();
            ExecuteControl(connection, "PRAGMA user_version=6");
            ExecuteControl(connection, "COMMIT");
        }
        catch { try { ExecuteControl(connection, "ROLLBACK"); } catch (SqliteException) { } throw; }
    }

    private static void ApplyVersionFourMigration(
        SqliteConnection connection, string kind, string sourceRevision)
    {
        ExecuteControl(connection, "BEGIN EXCLUSIVE");
        try
        {
            if (kind == "workflow")
            {
                using var schema = connection.CreateCommand();
                schema.CommandText = SimulationSchemaSql.WorkflowV4Migration;
                schema.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version,migration_name,applied_utc,source_revision) VALUES(4,'generated-case-and-department-catalogue',$utc,$source)"
                    : "INSERT INTO journal_schema_migrations(version,migration_name,applied_utc,source_revision) VALUES(4,'generated-case-and-department-catalogue-compatibility',$utc,$source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            ExecuteControl(connection, "PRAGMA user_version=4");
            ExecuteControl(connection, "COMMIT");
        }
        catch
        {
            try { ExecuteControl(connection, "ROLLBACK"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static void ApplyVersionTwoMigration(
        SqliteConnection connection, string kind, string sourceRevision)
    {
        ExecuteControl(connection, "BEGIN EXCLUSIVE");
        try
        {
            using (var schema = connection.CreateCommand())
            {
                schema.CommandText = kind == "workflow"
                    ? SimulationSchemaSql.WorkflowV2Migration
                    : SimulationSchemaSql.JournalV2Migration;
                schema.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version,migration_name,applied_utc,source_revision) VALUES(2,'result-recovery-anchors',$utc,$source)"
                    : "INSERT INTO journal_schema_migrations(version,migration_name,applied_utc,source_revision) VALUES(2,'result-recovery-anchors',$utc,$source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            ExecuteControl(connection, "PRAGMA user_version=2");
            ExecuteControl(connection, "COMMIT");
        }
        catch
        {
            try { ExecuteControl(connection, "ROLLBACK"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static void ApplyVersionThreeMigration(
        SqliteConnection connection, string kind, string sourceRevision)
    {
        ExecuteControl(connection, "BEGIN EXCLUSIVE");
        try
        {
            if (kind == "workflow")
            {
                using var schema = connection.CreateCommand();
                schema.CommandText = SimulationSchemaSql.WorkflowV3Migration;
                schema.ExecuteNonQuery();
            }
            using (var migration = connection.CreateCommand())
            {
                migration.CommandText = kind == "workflow"
                    ? "INSERT INTO schema_migrations(version,migration_name,applied_utc,source_revision) VALUES(3,'command-denial-ledger',$utc,$source)"
                    : "INSERT INTO journal_schema_migrations(version,migration_name,applied_utc,source_revision) VALUES(3,'command-denial-ledger-compatibility',$utc,$source)";
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.Parameters.AddWithValue("$source", sourceRevision);
                migration.ExecuteNonQuery();
            }
            ExecuteControl(connection, "PRAGMA user_version=3");
            ExecuteControl(connection, "COMMIT");
        }
        catch
        {
            try { ExecuteControl(connection, "ROLLBACK"); }
            catch (SqliteException) { }
            throw;
        }
    }

    private static SimulationDatabaseSchemaObservation Validate(
        SqliteConnection connection,
        string kind,
        int applicationId,
        IReadOnlyList<string> requiredTables,
        IReadOnlyList<string> requiredIndexes,
        IReadOnlyList<string> requiredTriggers)
    {
        if (ReadInt(connection, "PRAGMA application_id") != applicationId)
        {
            throw new InvalidOperationException("simulation_schema_application_mismatch");
        }
        var version = ReadInt(connection, "PRAGMA user_version");
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException("simulation_schema_newer_version");
        }
        if (version != CurrentSchemaVersion)
        {
            throw new InvalidOperationException("simulation_schema_unsupported_version");
        }

        var tables = ReadNames(connection, "table");
        var indexes = ReadNames(connection, "index");
        var triggers = ReadNames(connection, "trigger");
        if (requiredTables.Except(tables, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("simulation_schema_required_table_missing");
        }
        if (requiredIndexes.Except(indexes, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("simulation_schema_required_index_missing");
        }
        if (requiredTriggers.Except(triggers, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException("simulation_schema_required_trigger_missing");
        }
        using (var foreignKeys = connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read())
            {
                throw new InvalidOperationException("simulation_schema_foreign_key_failure");
            }
        }

        var metadataTable = kind == "workflow" ? "workflow_metadata" : "journal_metadata";
        var sequenceColumn = kind == "workflow" ? "commit_sequence" : "journal_sequence";
        Guid instanceId;
        long sequence;
        using (var metadata = connection.CreateCommand())
        {
            metadata.CommandText = $"SELECT database_instance_id, {sequenceColumn} FROM {metadataTable} WHERE singleton_id=1";
            using var metadataReader = metadata.ExecuteReader();
            if (!metadataReader.Read()
                || !Guid.TryParseExact(metadataReader.GetString(0), "D", out instanceId))
            {
                throw new InvalidOperationException("simulation_schema_metadata_invalid");
            }
            sequence = metadataReader.GetInt64(1);
            if (metadataReader.Read())
            {
                throw new InvalidOperationException("simulation_schema_metadata_invalid");
            }
        }

        var migrationTable = kind == "workflow" ? "schema_migrations" : "journal_schema_migrations";
        using var migration = connection.CreateCommand();
        migration.CommandText = $"SELECT COUNT(*) FROM {migrationTable} WHERE version IN (1,2,3) AND length(source_revision)>0";
        if (Convert.ToInt64(migration.ExecuteScalar(), CultureInfo.InvariantCulture) != 3)
        {
            throw new InvalidOperationException("simulation_schema_migration_identity_invalid");
        }
        return new(
            kind,
            applicationId,
            version,
            instanceId,
            sequence,
            new ReadOnlyCollection<string>(tables),
            new ReadOnlyCollection<string>(indexes));
    }

    private static List<string> ReadNames(SqliteConnection connection, string type)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type=$type AND name NOT LIKE 'sqlite_%' ORDER BY name";
        command.Parameters.AddWithValue("$type", type);
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static bool HasUserObjects(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' LIMIT 1";
        return command.ExecuteScalar() is not null;
    }

    private static int ReadInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ExecuteControl(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void RequireSourceRevision(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 80
            || value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new ArgumentException("simulation_schema_source_revision_invalid", nameof(value));
        }
    }
}
