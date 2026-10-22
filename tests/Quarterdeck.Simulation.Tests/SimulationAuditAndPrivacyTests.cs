using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationAuditAndPrivacyTests
{
    [Fact]
    public void ExactCurrentItIssuerAppendsCorrectionWithoutChangingOriginal()
    {
        using var fixture = Fixture.Create();
        var before = Text(fixture.Workflow, "SELECT outcome_code FROM audit_events");
        var result = fixture.Repository.Append(new(Id(2), Id(1), fixture.Issuer,
            2000, "operator_correction", null, "corrected_result", "case:synthetic"));

        Assert.True(result.Recorded);
        Assert.Equal(SimulationSafeResultCodes.AuditCorrectionRecorded, result.SafeResultCode);
        Assert.Equal(before, Text(fixture.Workflow, "SELECT outcome_code FROM audit_events"));
        Assert.Equal("ItIssuer", Text(fixture.Workflow, "SELECT author_role FROM audit_corrections"));
        Assert.Equal(Id(1), Text(fixture.Workflow, "SELECT audit_event_id FROM audit_corrections"));
    }

    [Fact]
    public void UnauthorizedInvalidAndMissingTargetAttemptsFabricateNoAudit()
    {
        using var fixture = Fixture.Create();
        var auditBefore = Scalar(fixture.Workflow, "SELECT count(*) FROM audit_events");
        Assert.Equal(SimulationSafeResultCodes.AuditCorrectionUnauthorized,
            fixture.Repository.Append(new(Id(3), Id(1), fixture.Hr, 2000,
                "operator_correction", "replacement", null, null)).SafeResultCode);
        Assert.Equal(SimulationSafeResultCodes.AuditCorrectionTargetMissing,
            fixture.Repository.Append(new(Id(4), Id(99), fixture.Issuer, 2000,
                "operator_correction", "replacement", null, null)).SafeResultCode);
        Assert.Equal(SimulationSafeResultCodes.AuditCorrectionInvalid,
            fixture.Repository.Append(new(Id(5), Id(1), fixture.Issuer, 999,
                "operator_correction", "replacement", null, null)).SafeResultCode);
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM audit_corrections"));
        Assert.Equal(auditBefore, Scalar(fixture.Workflow, "SELECT count(*) FROM audit_events"));
    }

    [Fact]
    public void LookalikeActorFromAnotherFixtureCannotAuthorizeCorrection()
    {
        using var fixture = Fixture.Create();
        var other = SyntheticIdentityDirectory.Create([
            new SyntheticIdentityBinding(new("issuer"), new("issuer-account"), "Issuer",
                new("site"), new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.ItIssuer })]);
        var result = fixture.Repository.Append(new(Id(10), Id(1),
            other.Resolve(new("issuer-account")), 2000, "operator_correction",
            null, "replacement", null));
        Assert.Equal(SimulationSafeResultCodes.AuditCorrectionUnauthorized,
            result.SafeResultCode);
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM audit_corrections"));
        Assert.Equal(1, Scalar(fixture.Workflow, "SELECT count(*) FROM audit_events"));
    }

    [Fact]
    public void AppendOnlyCorrectionAndOriginalForeignKeyAreDatabaseEnforced()
    {
        using var fixture = Fixture.Create();
        Assert.True(fixture.Repository.Append(new(Id(6), Id(1), fixture.Issuer,
            2000, "operator_correction", "replacement", null, null)).Recorded);
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow,
            "UPDATE audit_corrections SET reason_code='changed'"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow,
            "DELETE FROM audit_corrections"));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO audit_corrections(correction_id,audit_event_id,author_person_id,author_account_id,
 author_display_label,author_role,reason_code,corrected_utc,replacement_result_code)
VALUES('{Id(7)}','{Id(99)}','issuer','issuer-account','Issuer','ItIssuer','reason',2000,'result')
"""));
        Assert.Throws<SqliteException>(() => Execute(fixture.Workflow, $"""
INSERT INTO audit_corrections(correction_id,audit_event_id,author_person_id,author_account_id,
 author_display_label,author_role,reason_code,corrected_utc,replacement_result_code)
VALUES('{Id(9)}','{Id(1)}','hr','hr-account','HR','HrVerifier','reason',2000,'result')
"""));
    }

    [Fact]
    public void FaultMessageIsMappedAndSyntheticMarkerIsAbsentFromInspectedSinks()
    {
        using var fixture = Fixture.Create();
        var marker = "QD3-SENSITIVE-" + Guid.NewGuid().ToString("N");
        var result = fixture.Repository.Append(new(Id(8), Id(1), fixture.Issuer,
            2000, "operator_correction", null, "replacement", null),
            new ThrowMarker(marker));
        Assert.Equal(SimulationSafeResultCodes.AuditCorrectionStoreUnavailable,
            result.SafeResultCode);
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM audit_corrections"));

        var evidence = Path.Combine(fixture.Root, "public-evidence.txt");
        File.WriteAllText(evidence, "safe_code=" + result.SafeResultCode);
        var scan = SimulationDiagnosticPrivacyScanner.Scan(marker,
            [fixture.Workflow, fixture.Journal],
            [("result", result.SafeResultCode), ("console", "tests passed")], [evidence]);
        Assert.True(scan.MarkerAbsent);
        Assert.Contains("process-memory", scan.UninspectedSinks);
        Assert.Contains("external-ci-logs", scan.UninspectedSinks);
    }

    [Fact]
    public void PrivacyScannerPositiveControlFindsMarkerInEveryInspectedSinkKind()
    {
        using var fixture = Fixture.Create();
        var marker = "QD3-SENSITIVE-" + Guid.NewGuid().ToString("N");
        Execute(fixture.Workflow, $"""
INSERT INTO schema_migrations(version,migration_name,applied_utc,source_revision)
VALUES(999,'privacy-positive-control','2026-09-07T00:00:00Z','{marker}')
""");
        var evidence = Path.Combine(fixture.Root, "export.txt");
        File.WriteAllText(evidence, marker);
        var scan = SimulationDiagnosticPrivacyScanner.Scan(marker, [fixture.Workflow],
            [("stderr", marker)], [evidence]);
        Assert.False(scan.MarkerAbsent);
        Assert.Contains(scan.Findings, item => item.StartsWith("sqlite:", StringComparison.Ordinal));
        Assert.Contains("captured:stderr", scan.Findings);
        Assert.Contains("evidence:export.txt", scan.Findings);
    }

    [Fact]
    public void GeneratedArtifactClassesContainOnlyClosedDiagnosticsAfterMarkerFault()
    {
        using var fixture = Fixture.Create();
        var marker = "QD3-SENSITIVE-" + Guid.NewGuid().ToString("N");
        var result = fixture.Repository.Append(new(Id(18), Id(1), fixture.Issuer,
            2000, "operator_correction", null, "replacement", null),
            new ThrowMarker(marker));
        var safe = result.SafeResultCode;
        var html = Path.Combine(fixture.Root, "case.html");
        var log = Path.Combine(fixture.Root, "worker.log");
        var screenshotSidecar = Path.Combine(fixture.Root, "case.png.txt");
        var transcript = Path.Combine(fixture.Root, "walkthrough.txt");
        var manifest = Path.Combine(fixture.Root, "manifest.sha256");
        File.WriteAllText(html, $"<output>{safe}</output>");
        File.WriteAllText(log, $"safe_result={safe}");
        File.WriteAllText(screenshotSidecar, $"ocr={safe}");
        File.WriteAllText(transcript, $"narration={safe}");
        File.WriteAllText(manifest, $"fixture  {safe}");

        var scan = SimulationDiagnosticPrivacyScanner.Scan(marker,
            [fixture.Workflow, fixture.Journal],
            [("html", File.ReadAllText(html)), ("log", File.ReadAllText(log)),
             ("screenshot-ocr-sidecar", File.ReadAllText(screenshotSidecar)),
             ("transcript", File.ReadAllText(transcript)),
             ("manifest", File.ReadAllText(manifest))],
            [html, log, screenshotSidecar, transcript, manifest]);

        Assert.True(scan.MarkerAbsent);
        Assert.Contains("captured:html", scan.InspectedSinks);
        Assert.Contains("captured:screenshot-ocr-sidecar", scan.InspectedSinks);
        Assert.Contains("captured:transcript", scan.InspectedSinks);
        Assert.Contains("captured:manifest", scan.InspectedSinks);
    }

    [Theory]
    [InlineData(0, "audit_correction_store_unavailable")]
    [InlineData(1, "reconciliation_unknown")]
    [InlineData(2, "recovery_discovery_unknown")]
    [InlineData(3, "result_persistence_unknown")]
    public void EveryRegisteredFaultBoundaryMapsMarkerBearingExceptionToClosedCode(
        int boundaryValue, string expected)
    {
        var marker = "QD3-SENSITIVE-" + Guid.NewGuid().ToString("N");
        var code = SimulationSafeResultCodes.Map(new IOException(marker),
            (SimulationDiagnosticBoundary)boundaryValue);
        Assert.Equal(expected, code);
        Assert.DoesNotContain(marker, code, StringComparison.Ordinal);
    }

    [Fact]
    public void AllExceptionDerivedPublicResultCodesAreClosedAndMarkerFree()
    {
        var marker = "QD3-SENSITIVE-" + Guid.NewGuid().ToString("N");
        string[] codes =
        [
            SimulationSafeResultCodes.RecoveryGateUnavailable,
            SimulationSafeResultCodes.IntentDispatchAuthorityUnavailable,
            SimulationSafeResultCodes.AuthorityJournalUnavailable,
            SimulationSafeResultCodes.AuthorityObservationUnreadable
        ];
        Assert.All(codes, code =>
        {
            Assert.Matches("^[a-z0-9_]{1,80}$", code);
            Assert.DoesNotContain(marker, code, StringComparison.Ordinal);
        });
    }

    private sealed class ThrowMarker(string marker) : IAuditCorrectionFault
    { public void BeforeCommit() => throw new IOException(marker); }

    private sealed class Fixture : IDisposable
    {
        private Fixture(string root, string workflow, string journal,
            SimulationAuditCorrectionRepository repository, SyntheticActor issuer, SyntheticActor hr)
        { Root = root; Workflow = workflow; Journal = journal; Repository = repository; Issuer = issuer; Hr = hr; }
        internal string Root { get; }
        internal string Workflow { get; }
        internal string Journal { get; }
        internal SimulationAuditCorrectionRepository Repository { get; }
        internal SyntheticActor Issuer { get; }
        internal SyntheticActor Hr { get; }
        internal static Fixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "qd-audit-" + Guid.NewGuid().ToString("N"));
            var workflow = Path.Combine(root, "workflow", "data.db");
            var journal = Path.Combine(root, "journal", "data.db");
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            var connections = new SimulationSqliteConnections(
                SimulationStorePaths.Create(workflow, journal, source));
            new SimulationSchemaInitializer(connections).Initialize("chunk7-tests");
            Execute(workflow, $"""
INSERT INTO audit_events(audit_event_id,event_sequence,action_code,authorization_decision_code,
 outcome_code,occurred_utc,safe_result_code)
VALUES('{Id(1)}',1,'test_event','authorized','recorded',1000,'safe_result')
""");
            var directory = SyntheticIdentityDirectory.Create([
                new(new("issuer"), new("issuer-account"), "Issuer", new("site"),
                    new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.ItIssuer }),
                new(new("hr"), new("hr-account"), "HR", new("site"),
                    new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.HrVerifier })]);
            return new(root, workflow, journal,
                new SimulationAuditCorrectionRepository(connections, directory),
                directory.Resolve(new("issuer-account")), directory.Resolve(new("hr-account")));
        }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private static void Execute(string path, string sql)
    { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Query(string path, string sql)
    { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static long Scalar(string path, string sql) => Convert.ToInt64(Query(path, sql));
    private static string Text(string path, string sql) => Convert.ToString(Query(path, sql))!;
    private static string Id(int value) => $"70000000-0000-0000-0000-{value:000000000000}";
}
