using System.Security.Cryptography;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationSqliteBackupTests
{
    [Fact]
    public void WorkflowAndJournalBackupsAreSeparatelyIdentifiedAndVerified()
    {
        var fixture = CreateFixture();
        var workflowPath = Path.Combine(fixture.Root, "backups", "workflow.db");
        var journalPath = Path.Combine(fixture.Root, "backups", "journal.db");

        var workflow = fixture.Backup.CreateWorkflowBackup(workflowPath);
        var journal = fixture.Backup.CreateJournalBackup(journalPath);

        Assert.Equal(SimulationSqliteBackupKind.Workflow, workflow.Kind);
        Assert.Equal(SimulationSqliteBackupKind.Journal, journal.Kind);
        Assert.Equal(Path.GetFullPath(workflowPath), workflow.Path);
        Assert.Equal(Path.GetFullPath(journalPath), journal.Path);
        Assert.Equal(fixture.Schema.Workflow.DatabaseInstanceId, workflow.DatabaseInstanceId);
        Assert.Equal(fixture.Schema.Journal.DatabaseInstanceId, journal.DatabaseInstanceId);
        Assert.Equal(fixture.Schema.Workflow.Sequence, workflow.Sequence);
        Assert.Equal(fixture.Schema.Journal.Sequence, journal.Sequence);
        Assert.Equal(Hash(workflowPath), workflow.Sha256);
        Assert.Equal(Hash(journalPath), journal.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", workflow.Sha256);
        Assert.Matches("^[0-9a-f]{64}$", journal.Sha256);
        Assert.True((File.GetAttributes(workflowPath) & FileAttributes.ReadOnly) != 0);
        Assert.True((File.GetAttributes(journalPath) & FileAttributes.ReadOnly) != 0);
        Assert.Equal("wal", fixture.Connections.InspectWorkflow().JournalMode);
        Assert.Equal("wal", fixture.Connections.InspectJournal().JournalMode);
    }

    [Fact]
    public void BackupRequiresNewAbsoluteDestinationOutsideStoresAndSource()
    {
        var fixture = CreateFixture();
        var existing = Path.Combine(fixture.Root, "existing.db");
        File.WriteAllText(existing, "owned");

        Assert.Throws<ArgumentException>(() =>
            fixture.Backup.CreateWorkflowBackup("relative.db"));
        Assert.Throws<ArgumentException>(() =>
            fixture.Backup.CreateWorkflowBackup(existing));
        Assert.Throws<ArgumentException>(() =>
            fixture.Backup.CreateWorkflowBackup(fixture.Paths.Workflow));
        Assert.Throws<ArgumentException>(() =>
            fixture.Backup.CreateWorkflowBackup(ChangeCase(fixture.Paths.Workflow)));
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
        {
            Assert.Throws<ArgumentException>(() =>
                fixture.Backup.CreateJournalBackup(fixture.Paths.Journal + suffix));
        }
        Assert.Throws<ArgumentException>(() => fixture.Backup.CreateWorkflowBackup(
            Path.Combine(Path.GetDirectoryName(fixture.Paths.Workflow)!, "backup.db")));
        Assert.Throws<ArgumentException>(() => fixture.Backup.CreateWorkflowBackup(
            Path.Combine(Path.GetDirectoryName(fixture.Paths.Workflow)!, "nested", "backup.db")));
        Assert.Throws<ArgumentException>(() =>
            fixture.Backup.CreateWorkflowBackup(Path.Combine(fixture.Paths.SourceRoot, "backup.db")));
        Assert.Equal("owned", File.ReadAllText(existing));
    }

    [Fact]
    public void BackupRejectsDestinationBelowExistingReparsePoint()
    {
        if (OperatingSystem.IsWindows()) return;
        var fixture = CreateFixture();
        var target = Path.Combine(fixture.Root, "target");
        var link = Path.Combine(fixture.Root, "linked");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        var error = Assert.Throws<ArgumentException>(() =>
            fixture.Backup.CreateJournalBackup(Path.Combine(link, "journal.db")));

        Assert.Contains("simulation_backup_reparse_path", error.Message,
            StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(target, "journal.db")));
    }

    private static Fixture CreateFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "quarterdeck-backup-" + Guid.NewGuid().ToString("N"));
        var paths = SimulationStorePaths.Create(
            Path.Combine(root, "workflow", "data.db"),
            Path.Combine(root, "journal", "data.db"),
            Path.Combine(root, "source"));
        Directory.CreateDirectory(paths.SourceRoot);
        var connections = new SimulationSqliteConnections(paths);
        var schema = new SimulationSchemaInitializer(connections).Initialize("backup-tests");
        return new(root, paths, connections, schema, new SimulationSqliteBackup(connections));
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ChangeCase(string value)
    {
        var index = value.IndexOf("workflow", StringComparison.OrdinalIgnoreCase);
        Assert.True(index >= 0);
        return string.Concat(value.AsSpan(0, index), "WORKFLOW",
            value.AsSpan(index + "workflow".Length));
    }

    private sealed record Fixture(string Root, SimulationStorePaths Paths,
        SimulationSqliteConnections Connections, SimulationSchemaObservation Schema,
        SimulationSqliteBackup Backup);
}
