using Microsoft.Data.Sqlite;

namespace Quarterdeck.Persistence;

internal sealed record DiagnosticPrivacyScanResult(
    bool MarkerAbsent,
    IReadOnlyList<string> InspectedSinks,
    IReadOnlyList<string> UninspectedSinks,
    IReadOnlyList<string> Findings);

/// <summary>Bounded synthetic-marker scanner; it makes no claim about uninspected sinks.</summary>
internal static class SimulationDiagnosticPrivacyScanner
{
    private static readonly string[] Uninspected =
    [
        "process-memory", "crash-dumps", "windows-event-log", "etw",
        "antivirus-edr-telemetry", "filesystem-journal", "hypervisor-storage",
        "external-ci-logs", "network-appliance-logs"
    ];

    internal static DiagnosticPrivacyScanResult Scan(
        string marker,
        IEnumerable<string> sqlitePaths,
        IEnumerable<(string Sink, string Content)> capturedText,
        IEnumerable<string> exportedEvidencePaths)
    {
        if (string.IsNullOrWhiteSpace(marker) || marker.Length > 160)
            throw new ArgumentException("diagnostic_marker_invalid", nameof(marker));
        ArgumentNullException.ThrowIfNull(sqlitePaths);
        ArgumentNullException.ThrowIfNull(capturedText);
        ArgumentNullException.ThrowIfNull(exportedEvidencePaths);
        var inspected = new List<string>();
        var findings = new List<string>();

        foreach (var path in sqlitePaths)
        {
            inspected.Add("sqlite:" + Path.GetFileName(path));
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.GetFullPath(path), Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            db.Open();
            using var tables = db.CreateCommand();
            tables.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            var names = new List<string>();
            using (var reader = tables.ExecuteReader())
                while (reader.Read()) names.Add(reader.GetString(0));
            foreach (var table in names)
            {
                using var columns = db.CreateCommand();
                columns.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"")}\")";
                var textColumns = new List<string>();
                using (var reader = columns.ExecuteReader())
                    while (reader.Read())
                        if (reader.GetString(2).Contains("TEXT", StringComparison.OrdinalIgnoreCase))
                            textColumns.Add(reader.GetString(1));
                foreach (var column in textColumns)
                {
                    using var query = db.CreateCommand();
                    query.CommandText = $"SELECT count(*) FROM \"{table.Replace("\"", "\"\"")}\" WHERE instr(CAST(\"{column.Replace("\"", "\"\"")}\" AS TEXT),$marker)>0";
                    query.Parameters.AddWithValue("$marker", marker);
                    if (Convert.ToInt64(query.ExecuteScalar()) > 0)
                        findings.Add($"sqlite:{Path.GetFileName(path)}:{table}.{column}");
                }
            }
        }

        foreach (var captured in capturedText)
        {
            inspected.Add("captured:" + captured.Sink);
            if (captured.Content.Contains(marker, StringComparison.Ordinal))
                findings.Add("captured:" + captured.Sink);
        }
        foreach (var path in exportedEvidencePaths)
        {
            inspected.Add("evidence:" + Path.GetFileName(path));
            if (File.ReadAllText(path).Contains(marker, StringComparison.Ordinal))
                findings.Add("evidence:" + Path.GetFileName(path));
        }
        return new(findings.Count == 0, inspected.AsReadOnly(), Uninspected,
            findings.AsReadOnly());
    }
}
