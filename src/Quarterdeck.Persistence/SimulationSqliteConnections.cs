using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence;

public sealed record SqliteConnectionObservation(
    string EngineVersion,
    long ForeignKeys,
    string JournalMode,
    long Synchronous,
    long BusyTimeoutMilliseconds);

/// <summary>
/// Adapts V2 SqliteFakeLifecycleStore.Open's private-cache, bounded-timeout,
/// pooling-disabled connection. No arbitrary SQL or raw connection is public.
/// Schema and workflow repositories are the next bounded slice.
/// </summary>
public sealed class SimulationSqliteConnections(SimulationStorePaths paths)
{
    private readonly SimulationStorePaths _paths = paths
        ?? throw new ArgumentNullException(nameof(paths));
    private readonly SimulationStoreSetRuntimeState _runtimeState =
        SimulationStoreSetRuntimeRegistry.Get(paths ?? throw new ArgumentNullException(nameof(paths)));

    internal object StoreSetToken => _runtimeState;
    internal SimulationStoreSetRuntimeState RuntimeState => _runtimeState;
    internal string WorkflowPath => _paths.Workflow;
    internal string JournalPath => _paths.Journal;
    internal string SourceRoot => _paths.SourceRoot;

    public SqliteConnectionObservation InspectWorkflow()
    {
        using var connection = OpenWorkflow();
        return Observe(connection);
    }

    public SqliteConnectionObservation InspectJournal()
    {
        using var connection = OpenJournal();
        return Observe(connection);
    }

    internal SqliteConnection OpenWorkflow() => Open(_paths.Workflow);
    internal SqliteConnection OpenJournal() => Open(_paths.Journal);

    private SqliteConnection Open(string path)
    {
        _paths.Revalidate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _paths.Revalidate();
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            DefaultTimeout = 5,
            Pooling = false
        };
        var connection = new SqliteConnection(options.ToString());
        try
        {
            connection.Open();
            using var configure = connection.CreateCommand();
            configure.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;";
            configure.ExecuteNonQuery();
            var observation = Observe(connection);
            if (!Version.TryParse(observation.EngineVersion, out var engine)
                || engine < new Version(3, 37, 0)
                || observation.ForeignKeys != 1
                || observation.JournalMode != "wal"
                || observation.Synchronous != 2
                || observation.BusyTimeoutMilliseconds != 5000)
            {
                throw new InvalidOperationException("simulation_sqlite_profile_unsupported");
            }
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static SqliteConnectionObservation Observe(SqliteConnection connection) => new(
        Convert.ToString(Scalar(connection, "SELECT sqlite_version()"), System.Globalization.CultureInfo.InvariantCulture)!,
        Convert.ToInt64(Scalar(connection, "PRAGMA foreign_keys"), System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToString(Scalar(connection, "PRAGMA journal_mode"), System.Globalization.CultureInfo.InvariantCulture)!,
        Convert.ToInt64(Scalar(connection, "PRAGMA synchronous"), System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt64(Scalar(connection, "PRAGMA busy_timeout"), System.Globalization.CultureInfo.InvariantCulture));

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

internal sealed class SimulationStoreSetRuntimeState
{
    internal SimulationStoreSetRuntimeState(string identity) => Identity = identity;
    internal string Identity { get; }
    internal object DispatchSync { get; } = new();
    internal int GlobalDiscoveryBlocked = 1;
}

internal static class SimulationStoreSetRuntimeRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, WeakReference<SimulationStoreSetRuntimeState>> States =
        new(StringComparer.Ordinal);

    internal static SimulationStoreSetRuntimeState Get(SimulationStorePaths paths)
    {
        lock (Sync)
        {
            var identity = paths.StoreSetIdentity;
            var foldedIdentity = paths.StoreSetFoldedIdentity;
            if (States.TryGetValue(foldedIdentity, out var reference)
                && reference.TryGetTarget(out var existing))
            {
                if (!string.Equals(existing.Identity, identity, StringComparison.Ordinal))
                    throw new ArgumentException("simulation_store_case_alias_ambiguous", nameof(paths));
                return existing;
            }

            var created = new SimulationStoreSetRuntimeState(identity);
            States[foldedIdentity] = new(created);
            if (States.Count > 256)
            {
                foreach (var stale in States
                    .Where(item => !item.Value.TryGetTarget(out _))
                    .Select(item => item.Key)
                    .ToArray())
                {
                    States.Remove(stale);
                }
            }
            return created;
        }
    }
}
