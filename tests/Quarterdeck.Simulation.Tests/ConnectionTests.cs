using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence.Tests;

public sealed class ConnectionTests
{
    [Fact]
    public void EveryConnectionReestablishesRequiredProfile()
    {
        var root = NewRoot();
        var workflow = Path.Combine(root, "workflow", "data.db");
        var journal = Path.Combine(root, "journal", "data.db");
        var connections = new SimulationSqliteConnections(
            SimulationStorePaths.Create(workflow, journal, Path.Combine(root, "source")));
        AssertProfile(connections.InspectWorkflow());
        AssertProfile(connections.InspectJournal());
        AssertProfile(connections.InspectWorkflow());
        using var raw = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = workflow, Pooling = false
        }.ToString());
        raw.Open();
        using var command = raw.CreateCommand();
        command.CommandText = "CREATE TABLE qualification(value INTEGER NOT NULL) STRICT;";
        command.ExecuteNonQuery();
    }

    [Fact]
    public void SemicolonFilenameCannotInjectConnectionOptions()
    {
        var root = NewRoot();
        var workflow = Path.Combine(root, "workflow", "data;Mode=Memory;Cache=Shared.db");
        var journal = Path.Combine(root, "journal", "data.db");
        var connections = new SimulationSqliteConnections(
            SimulationStorePaths.Create(workflow, journal, Path.Combine(root, "source")));
        AssertProfile(connections.InspectWorkflow());
        Assert.True(File.Exists(workflow));
    }

    [Fact]
    public void RelativeNetworkSameAndSourceLocationsAreRejected()
    {
        var root = NewRoot();
        var workflow = Path.Combine(root, "workflow", "data.db");
        var journal = Path.Combine(root, "journal", "data.db");
        var source = Path.Combine(root, "source");
        Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create("relative.db", journal, source));
        Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create("\\\\server\\share\\data.db", journal, source));
        Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(workflow, workflow, source));
        Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(workflow, Path.Combine(root, "workflow", "other.db"), source));
        Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(Path.Combine(source, "data.db"), journal, source));
        Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(workflow, journal, "relative-source"));
    }

    [Fact]
    public void FilesystemRootSourceBoundaryRejectsEveryDescendant()
    {
        var root = NewRoot();
        var error = Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(
            Path.Combine(root, "workflow", "data.db"),
            Path.Combine(root, "journal", "data.db"),
            Path.GetPathRoot(root)!));
        Assert.Equal("simulation_store_under_source", error.Message);
    }

    [Fact]
    public void SimilarNamedSiblingIsNotInsideSource()
    {
        var root = NewRoot();
        var paths = SimulationStorePaths.Create(
            Path.Combine(root, "source-other", "data.db"),
            Path.Combine(root, "journal", "data.db"),
            Path.Combine(root, "source"));
        Assert.NotNull(paths);
    }

    [Fact]
    public void LiveCaseOnlyStoreAliasIsRejectedFailClosed()
    {
        var root = NewRoot();
        var source = Path.Combine(root, "source");
        var first = new SimulationSqliteConnections(SimulationStorePaths.Create(
            Path.Combine(root, "Workflow", "data.db"),
            Path.Combine(root, "Journal", "data.db"), source));
        Assert.NotNull(first);

        var error = Assert.Throws<ArgumentException>(() =>
            new SimulationSqliteConnections(SimulationStorePaths.Create(
                Path.Combine(root, "workflow", "data.db"),
                Path.Combine(root, "journal", "data.db"), source)));

        Assert.Contains("simulation_store_case_alias_ambiguous", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingDirectoryLinkIsRejected()
    {
        var root = NewRoot();
        var target = Path.Combine(root, "actual");
        Directory.CreateDirectory(target);
        var link = Path.Combine(root, "linked");
        // The Windows qualification runner must support symlink creation. A
        // missing capability fails this check rather than silently claiming pass.
        Directory.CreateSymbolicLink(link, target);
        var error = Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(
            Path.Combine(link, "data.db"),
            Path.Combine(root, "journal", "data.db"),
            Path.Combine(root, "source")));
        Assert.Equal("simulation_store_reparse_path", error.Message);
    }

    [Fact]
    public void WindowsAliasesCannotChangeTheValidatedLocation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = NewRoot();
        var journal = Path.Combine(root, "journal", "data.db");
        var source = Path.Combine(root, "source");
        foreach (var component in new[] { "workflow.", "workflow ", "workflow:stream" })
        {
            Assert.Throws<ArgumentException>(() => SimulationStorePaths.Create(
                Path.Combine(root, component, "data.db"), journal, source));
        }
    }

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "quarterdeck-connection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Retain these tiny synthetic fixtures for failed-run inspection.
        return root;
    }

    private static void AssertProfile(SqliteConnectionObservation value)
    {
        Assert.True(Version.Parse(value.EngineVersion) >= new Version(3, 37, 0));
        Assert.Equal(1, value.ForeignKeys);
        Assert.Equal("wal", value.JournalMode);
        Assert.Equal(2, value.Synchronous);
        Assert.Equal(5000, value.BusyTimeoutMilliseconds);
    }
}
