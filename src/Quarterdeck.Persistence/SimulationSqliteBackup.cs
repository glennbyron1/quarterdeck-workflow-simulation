using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence;

internal enum SimulationSqliteBackupKind
{
    Workflow,
    Journal
}

internal sealed record SimulationSqliteBackupManifest(
    SimulationSqliteBackupKind Kind,
    string Path,
    Guid DatabaseInstanceId,
    long Sequence,
    string Sha256);

/// <summary>
/// Creates independently identified test-fixture backups through SQLite's online
/// backup API. Restore policy and mutation deliberately remain outside this type.
/// </summary>
internal sealed class SimulationSqliteBackup
{
    private readonly SimulationSqliteConnections _connections;

    internal SimulationSqliteBackup(SimulationSqliteConnections connections) =>
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));

    internal SimulationSqliteBackupManifest CreateWorkflowBackup(string destinationPath) =>
        Create(destinationPath, SimulationSqliteBackupKind.Workflow,
            _connections.WorkflowPath, _connections.OpenWorkflow);

    internal SimulationSqliteBackupManifest CreateJournalBackup(string destinationPath) =>
        Create(destinationPath, SimulationSqliteBackupKind.Journal,
            _connections.JournalPath, _connections.OpenJournal);

    internal static void VerifyArtifact(
        SimulationSqliteBackupManifest manifest, string candidatePath)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        var firstIdentity = Verify(path, manifest.Kind);
        var firstHash = Hash(path);
        var secondIdentity = Verify(path, manifest.Kind);
        var secondHash = Hash(path);
        if (firstIdentity != secondIdentity
            || firstIdentity.DatabaseInstanceId != manifest.DatabaseInstanceId
            || firstIdentity.Sequence != manifest.Sequence
            || !string.Equals(firstHash, secondHash, StringComparison.Ordinal)
            || !string.Equals(secondHash, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("simulation_backup_manifest_mismatch");
        }
    }

    private SimulationSqliteBackupManifest Create(string destinationPath,
        SimulationSqliteBackupKind kind, string livePath, Func<SqliteConnection> openSource)
    {
        var destination = ValidateDestination(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None)) { }
            using (var source = openSource())
            using (var target = Open(destination, SqliteOpenMode.ReadWrite))
            {
                source.BackupDatabase(target);
            }

            File.SetAttributes(destination,
                File.GetAttributes(destination) | FileAttributes.ReadOnly);
            var firstIdentity = Verify(destination, kind);
            var firstHash = Hash(destination);
            var secondIdentity = Verify(destination, kind);
            var secondHash = Hash(destination);
            if (firstIdentity != secondIdentity
                || !string.Equals(firstHash, secondHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("simulation_backup_changed_during_verification");
            }
            return new(kind, destination, secondIdentity.DatabaseInstanceId,
                secondIdentity.Sequence, secondHash);
        }
        catch
        {
            // The destination was required not to exist and is owned by this attempt.
            try
            {
                if (File.Exists(destination))
                    File.SetAttributes(destination, FileAttributes.Normal);
                File.Delete(destination);
            }
            catch { }
            throw;
        }
    }

    private string ValidateDestination(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.StartsWith("\\\\", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("simulation_backup_path_invalid", nameof(value));
        }

        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (File.Exists(path) || Directory.Exists(path))
            throw new ArgumentException("simulation_backup_destination_exists", nameof(value));
        if (Same(path, _connections.WorkflowPath) || Same(path, _connections.JournalPath))
            throw new ArgumentException("simulation_backup_live_store", nameof(value));
        if (IsLiveSidecar(path, _connections.WorkflowPath)
            || IsLiveSidecar(path, _connections.JournalPath)
            || IsAtOrBelow(path, Path.GetDirectoryName(_connections.WorkflowPath)!)
            || IsAtOrBelow(path, Path.GetDirectoryName(_connections.JournalPath)!))
        {
            throw new ArgumentException("simulation_backup_live_store_boundary", nameof(value));
        }

        var sourcePrefix = Path.EndsInDirectorySeparator(_connections.SourceRoot)
            ? _connections.SourceRoot
            : _connections.SourceRoot + Path.DirectorySeparatorChar;
        if (Same(path, _connections.SourceRoot)
            || path.StartsWith(sourcePrefix, Comparison))
        {
            throw new ArgumentException("simulation_backup_under_source", nameof(value));
        }

        if (OperatingSystem.IsWindows()
            && (value.AsSpan(2).Contains(':')
                || value.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(segment => segment.EndsWith('.') || segment.EndsWith(' '))))
        {
            throw new ArgumentException("simulation_backup_path_invalid", nameof(value));
        }

        for (var current = Path.GetDirectoryName(path); current is not null;
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException("simulation_backup_reparse_path", nameof(value));
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }

        if (OperatingSystem.IsWindows()
            && new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
        {
            throw new ArgumentException("simulation_backup_fixed_disk_required", nameof(value));
        }
        return path;
    }

    private static (Guid DatabaseInstanceId, long Sequence) Verify(
        string path, SimulationSqliteBackupKind kind)
    {
        using var db = Open(path, SqliteOpenMode.ReadOnly);
        if (!string.Equals(Convert.ToString(Scalar(db, "PRAGMA integrity_check"),
                CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("simulation_backup_integrity_failure");
        using (var foreignKeys = db.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_key_check";
            using var reader = foreignKeys.ExecuteReader();
            if (reader.Read())
                throw new InvalidOperationException("simulation_backup_foreign_key_failure");
        }

        var expectedApplicationId = kind == SimulationSqliteBackupKind.Workflow
            ? SimulationSchemaInitializer.WorkflowApplicationId
            : SimulationSchemaInitializer.JournalApplicationId;
        if (Convert.ToInt32(Scalar(db, "PRAGMA application_id"), CultureInfo.InvariantCulture)
                != expectedApplicationId)
            throw new InvalidOperationException("simulation_backup_application_mismatch");
        if (Convert.ToInt32(Scalar(db, "PRAGMA user_version"), CultureInfo.InvariantCulture)
                != SimulationSchemaInitializer.CurrentSchemaVersion)
            throw new InvalidOperationException("simulation_backup_schema_version_mismatch");

        var metadataTable = kind == SimulationSqliteBackupKind.Workflow
            ? "workflow_metadata" : "journal_metadata";
        var sequenceColumn = kind == SimulationSqliteBackupKind.Workflow
            ? "commit_sequence" : "journal_sequence";
        using var metadata = db.CreateCommand();
        metadata.CommandText = $"SELECT schema_name,database_instance_id,{sequenceColumn} FROM {metadataTable} WHERE singleton_id=1";
        using var metadataReader = metadata.ExecuteReader();
        var expectedName = "quarterdeck-v3-simulation-" + kind.ToString().ToLowerInvariant();
        if (!metadataReader.Read()
            || !string.Equals(metadataReader.GetString(0), expectedName, StringComparison.Ordinal)
            || !Guid.TryParseExact(metadataReader.GetString(1), "D", out var instanceId))
            throw new InvalidOperationException("simulation_backup_metadata_invalid");
        var sequence = metadataReader.GetInt64(2);
        if (sequence < 0 || metadataReader.Read())
            throw new InvalidOperationException("simulation_backup_metadata_invalid");
        return (instanceId, sequence);
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static string Hash(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool IsLiveSidecar(string path, string livePath) =>
        Same(path, livePath + "-wal")
        || Same(path, livePath + "-shm")
        || Same(path, livePath + "-journal");

    private static bool IsAtOrBelow(string path, string directory)
    {
        var prefix = Path.EndsInDirectorySeparator(directory)
            ? directory : directory + Path.DirectorySeparatorChar;
        return Same(path, directory) || path.StartsWith(prefix, Comparison);
    }

    // Conservatively reject case-only aliases on every platform. This can deny
    // distinct paths on a case-sensitive volume, but cannot redirect a backup
    // into a live store on a case-insensitive mount.
    private static StringComparison Comparison => StringComparison.OrdinalIgnoreCase;

    private static bool Same(string first, string second) =>
        string.Equals(first, second, Comparison);
}
