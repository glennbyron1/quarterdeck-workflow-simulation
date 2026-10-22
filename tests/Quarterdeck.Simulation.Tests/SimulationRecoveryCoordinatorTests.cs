using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationRecoveryCoordinatorTests
{
    [Fact]
    public void ChildProcessCrashBeforeResultCommitIsDiscoveredAfterActualRestart()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        var root = FindRepositoryRoot();
        var probe = Path.Combine(root, "tools", "Quarterdeck.RecoveryCrashProbe", "bin",
            "Release", "net10.0", "Quarterdeck.RecoveryCrashProbe.dll");
        Assert.True(File.Exists(probe), $"Crash probe was not built: {probe}");
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                probe, fixture.Workflow, fixture.JournalPath, fixture.Paths.SourceRoot,
                EventId, OperationId, Digest, "2500"
            }
        })!;
        Assert.True(process.WaitForExit(30_000), "Crash probe did not terminate.");
        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal(1, EventuallyScalar(fixture.Workflow,
            $"SELECT count(*) FROM operation_events WHERE operation_id='{OperationId}'"));

        var restarted = Reopen(fixture);
        var finding = Assert.Single(restarted.DiscoverAtStartup(3000).Findings);
        Assert.Equal(RecoveryMatchState.Pending, finding.MatchState);
        Assert.True(restarted.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
    }

    [Fact]
    public void AdapterObservationCommitsSeparatelyAndPostCommitFaultReadsBackExactly()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        var result = fixture.Recovery.PersistObservation(new(
            EventId, OperationId, Digest, 2000), new ThrowAfterCommit());

        Assert.Equal(WorkflowResultPersistenceStatus.PersistedAfterReadback, result.Status);
        Assert.DoesNotContain(ThrowAfterCommit.Marker, result.SafeResultCode,
            StringComparison.Ordinal);
        Assert.Equal("IntentCommitted", Text(fixture.Workflow,
            $"SELECT state FROM operations WHERE operation_id='{OperationId}'"));
        Assert.Equal("Pending", Text(fixture.Workflow,
            $"SELECT state_code FROM operation_events WHERE event_id='{EventId}'"));
        Assert.Equal(2, Scalar(fixture.Workflow,
            $"SELECT count(*) FROM operation_events WHERE operation_id='{OperationId}'"));
    }

    [Fact]
    public void RestartedCoordinatorDiscoversPendingAndBlocksOnlyAffectedResources()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        var restarted = Reopen(fixture);

        var result = restarted.DiscoverAtStartup(3000);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RecoveryMatchState.Pending, finding.MatchState);
        Assert.True(restarted.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
        Assert.False(restarted.IsDispatchBlocked("site", "other-account",
            "PIV-authentication-simulation", "other-device", "9a"));
    }

    [Fact]
    public void LaterTerminalJournalEventSupersedesEarlierPendingFindingAppendOnly()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        var first = fixture.Recovery.DiscoverAtStartup(3000);
        Assert.Equal(RecoveryMatchState.Pending, Assert.Single(first.Findings).MatchState);
        Assert.True(fixture.Recovery.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));

        AppendTerminal(fixture.JournalPath);
        var second = fixture.Recovery.DiscoverAtStartup(4000);

        Assert.Equal(RecoveryMatchState.Terminal, Assert.Single(second.Findings).MatchState);
        Assert.Equal(2, Scalar(fixture.Workflow, "SELECT count(*) FROM recovery_findings"));
        Assert.False(fixture.Recovery.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
    }

    [Fact]
    public void JournalWatermarkChangeAfterWorkflowCommitKeepsGlobalGateClosedUntilRetry()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);

        var changed = fixture.Recovery.DiscoverAtStartup(3000,
            new ActAtDiscovery(() => AppendTerminal(fixture.JournalPath)));

        Assert.Equal("recovery_journal_changed_retry_required", changed.SafeResultCode);
        Assert.True(fixture.Recovery.IsDispatchBlocked("other-site", "other-account",
            "PIV-authentication-simulation", "other-device", "9a"));
        var retry = fixture.Recovery.DiscoverAtStartup(4000);
        Assert.Equal(RecoveryMatchState.Terminal, Assert.Single(retry.Findings).MatchState);
        Assert.False(fixture.Recovery.IsDispatchBlocked("other-site", "other-account",
            "PIV-authentication-simulation", "other-device", "9a"));
    }

    [Fact]
    public void FreshEmptyReplacementJournalCannotOpenUnresolvedWorkflowGate()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        SqliteConnection.ClearAllPools();
        File.Move(fixture.JournalPath, fixture.JournalPath + ".original");
        var connections = new SimulationSqliteConnections(fixture.Paths);
        new SimulationSchemaInitializer(connections).Initialize("replacement-journal-test");
        var journal = new SimulationAuthorityJournalRepository(connections,
            new AuthorityCallCounters());
        var restarted = new SimulationRecoveryCoordinator(connections, journal);

        var result = restarted.DiscoverAtStartup(3000);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RecoveryMatchState.Unknown, finding.MatchState);
        Assert.Equal("recovery_journal_operation_absent", finding.SafeResultCode);
        Assert.True(restarted.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
    }

    [Fact]
    public void LocallyTerminalOperationMissingFromIndependentJournalRemainsUnknownAndBlocked()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        Execute(fixture.Workflow, $"""
INSERT INTO operation_events(event_id,operation_id,event_sequence,state_code,event_code,safe_result_code,safe_reference,observed_utc,source_kind)
VALUES('{Id(30)}','{OperationId}',2,'AcceptedTerminal','local_terminal','local_terminal',NULL,2000,'Workflow');
""");
        SqliteConnection.ClearAllPools();
        File.Move(fixture.JournalPath, fixture.JournalPath + ".original");
        var connections = new SimulationSqliteConnections(fixture.Paths);
        new SimulationSchemaInitializer(connections).Initialize("replacement-journal-test");
        var journal = new SimulationAuthorityJournalRepository(connections,
            new AuthorityCallCounters());
        var restarted = new SimulationRecoveryCoordinator(connections, journal);

        var finding = Assert.Single(restarted.DiscoverAtStartup(3000).Findings);

        Assert.Equal(RecoveryMatchState.Unknown, finding.MatchState);
        Assert.Equal("recovery_journal_operation_absent", finding.SafeResultCode);
        Assert.True(restarted.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
    }

    [Fact]
    public void MissingLocalIntentIsRecordedWithoutFabricatingIntent()
    {
        var fixture = NewFixture(seedWorkflow: false, terminal: false);

        var result = fixture.Recovery.DiscoverAtStartup(3000);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RecoveryMatchState.AbsentLocal, finding.MatchState);
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM operations"));
        Assert.True(fixture.Recovery.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
    }

    [Fact]
    public void UnavailableJournalPreservesUnknownAndBlocksGlobally()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        SqliteConnection.ClearAllPools();
        File.Move(fixture.JournalPath, fixture.JournalPath + ".unavailable");

        var result = fixture.Recovery.DiscoverAtStartup(3000);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RecoveryMatchState.Unknown, finding.MatchState);
        Assert.Equal("recovery_journal_unavailable", result.SafeResultCode);
        Assert.True(fixture.Recovery.IsDispatchBlocked("other-site", "other-account",
            "PIV-authentication-simulation", "other-device", "9a"));
    }

    [Fact]
    public void OnlyExactItIssuerCanReconcileTerminalFinding()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: true);
        var finding = Assert.Single(fixture.Recovery.DiscoverAtStartup(3000).Findings);
        Assert.Equal(RecoveryMatchState.Terminal, finding.MatchState);
        Assert.Throws<InvalidOperationException>(() => fixture.Directory
            .AuthorizeReconciliation(fixture.Hr, new("site")));
        var issuer = fixture.Directory.AuthorizeReconciliation(fixture.Issuer, new("site"));

        var applied = fixture.Recovery.Reconcile(new(Id(10), finding.FindingId,
            OperationId, issuer, 3200, "reviewed"));

        Assert.True(applied.Applied);
        Assert.Equal(1, Scalar(fixture.Workflow, "SELECT count(*) FROM reconciliation_actions"));
        Assert.Equal(0, Scalar(fixture.Workflow,
            "SELECT count(*) FROM workflow_resource_reservations WHERE released_utc IS NULL"));
        Assert.Equal("journal:", Text(fixture.Workflow,
            "SELECT substr(source_reference,1,8) FROM reconciliation_actions"));
        Assert.False(fixture.Recovery.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
    }

    [Fact]
    public void PendingAndCancellationCannotReleaseRecoveryLock()
    {
        var fixture = NewFixture(seedWorkflow: true, terminal: false);
        var finding = Assert.Single(fixture.Recovery.DiscoverAtStartup(3000).Findings);
        var issuer = fixture.Directory.AuthorizeReconciliation(fixture.Issuer, new("site"));

        Assert.False(fixture.Recovery.Reconcile(new(Id(11), finding.FindingId,
            OperationId, issuer, 3200, "reviewed")).Applied);
        Assert.False(fixture.Recovery.Cancel(finding.FindingId, issuer, 3300).Applied);
        Assert.True(fixture.Recovery.IsDispatchBlocked("site", "account",
            "PIV-authentication-simulation", "device", "9a"));
        Assert.Equal(0, Scalar(fixture.Workflow, "SELECT count(*) FROM reconciliation_actions"));
    }

    private static Fixture NewFixture(bool seedWorkflow, bool terminal)
    {
        var root = Path.Combine(Path.GetTempPath(), "quarterdeck-recovery-" + Guid.NewGuid().ToString("N"));
        var workflow = Path.Combine(root, "workflow", "data.db");
        var journal = Path.Combine(root, "journal", "data.db");
        var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
        var paths = SimulationStorePaths.Create(workflow, journal, source);
        var connections = new SimulationSqliteConnections(paths);
        new SimulationSchemaInitializer(connections).Initialize("recovery-tests");
        var workflowId = Text(workflow, "SELECT database_instance_id FROM workflow_metadata");
        if (seedWorkflow) SeedWorkflow(workflow, workflowId);
        SeedJournal(journal, workflowId, terminal);
        var authority = new SimulationAuthorityJournalRepository(connections,
            new AuthorityCallCounters());
        var directory = SyntheticIdentityDirectory.Create([
            new(new("issuer"), new("issuer-account"), "Issuer", new("site"),
                new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.ItIssuer }),
            new(new("hr"), new("hr-account"), "HR", new("site"),
                new HashSet<SyntheticWorkflowRole> { SyntheticWorkflowRole.HrVerifier })
        ]);
        return new(workflow, journal, paths, authority,
            new SimulationRecoveryCoordinator(connections, authority), directory,
            directory.Resolve(new("issuer-account")), directory.Resolve(new("hr-account")));
    }

    private static SimulationRecoveryCoordinator Reopen(Fixture fixture)
    {
        var connections = new SimulationSqliteConnections(fixture.Paths);
        var journal = new SimulationAuthorityJournalRepository(connections,
            new AuthorityCallCounters());
        return new(connections, journal);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Quarterdeck.Simulation.slnx")))
            current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("repository_root_not_found");
    }

    private static void SeedWorkflow(string path, string workflowId) => Execute(path, $"""
INSERT INTO cases(case_id,person_id,site_id,intake_actor_person_id,intake_actor_account_id,intake_actor_display_label,intake_utc,current_step,required_next_action_code,current_formal_version,row_revision)
VALUES('case','person','site','hr','hr-account','HR',1000,'Dispatch','dispatch_fake',NULL,0);
INSERT INTO formal_request_versions(case_id,version,site_id,person_id,account_id,intended_use,fake_device_id,slot,synthetic_public_request_digest,fake_authority,template,policy_options_version,requester_person_id,requester_account_id,requester_display_label,created_utc,policy_authority_id,policy_version,policy_content_digest,operation_binding_input_revision,sealed_request_digest)
VALUES('case',1,'site','person','account','PIV-authentication-simulation','device','9a','{new string('b', 64)}','authority','template','v1','requester','requester-account','Requester',1000,'policy','v1','{new string('c', 64)}',1,'sha256:{new string('e', 64)}');
UPDATE cases SET current_formal_version=1,row_revision=1 WHERE case_id='case';
INSERT INTO approvals(approval_id,case_id,formal_version,approver_person_id,approver_account_id,approver_display_label,approver_role,approved_utc,expires_utc,approved_request_digest,policy_authority_id,policy_version,policy_content_digest,consumed_by_operation_id,consumed_utc)
VALUES('{ApprovalId}','case',1,'hr','hr-account','HR','HrVerifier',1000,5000,'sha256:{new string('e', 64)}','policy','v1','{new string('c', 64)}','{OperationId}',1500);
INSERT INTO operations(operation_id,case_id,formal_version,approval_id,attempt_number,synthetic_public_request_reference,operation_binding_digest,admission_identity_digest,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,workflow_database_instance_id,intent_commit_sequence,state,created_utc,updated_utc,row_revision)
VALUES('{OperationId}','case',1,'{ApprovalId}',1,'request','{Digest}','sha256:{new string('a', 64)}','site','account','PIV-authentication-simulation','device','9a','authority','template','{workflowId}',1,'IntentCommitted',1500,1500,0);
INSERT INTO operation_events(event_id,operation_id,event_sequence,state_code,event_code,safe_result_code,safe_reference,observed_utc,source_kind)
VALUES('{Id(20)}','{OperationId}',1,'IntentCommitted','intent_committed','intent_committed',NULL,1500,'Workflow');
INSERT INTO workflow_resource_reservations(operation_id,resource_kind,site_id,account_id,intended_use,fake_device_id,slot,reserved_utc)
VALUES('{OperationId}','AccountUse','site','account','PIV-authentication-simulation',NULL,NULL,1500);
INSERT INTO workflow_resource_reservations(operation_id,resource_kind,site_id,account_id,intended_use,fake_device_id,slot,reserved_utc)
VALUES('{OperationId}','DeviceSlot','site',NULL,NULL,'device','9a',1500);
""");

    private static void SeedJournal(string path, string workflowId, bool terminal)
    {
        Execute(path, $"""
INSERT INTO approval_consumptions(approval_id,operation_id,binding_digest,consumed_utc,workflow_database_instance_id,workflow_commit_sequence)
VALUES('{ApprovalId}','{OperationId}','{Digest}',1500,'{workflowId}',1);
INSERT INTO authority_operations(operation_id,approval_id,synthetic_public_request_reference,binding_digest,admission_identity_digest,case_id,formal_version,attempt_number,target_site_id,target_account_id,intended_use,fake_device_id,slot,authority_id,template,state,result_reference,created_utc,updated_utc)
VALUES('{OperationId}','{ApprovalId}','request','{Digest}','sha256:{new string('a', 64)}','case',1,1,'site','account','PIV-authentication-simulation','device','9a','authority','template','AcceptedPending',NULL,1500,1500);
INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
VALUES('{Id(21)}',1,'{OperationId}','CallObserved','authority_call_observed',NULL,1500);
INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
VALUES('{Id(22)}',2,'{OperationId}','AcceptedPending','authority_accepted_pending',NULL,1500);
UPDATE journal_metadata SET journal_sequence=2 WHERE singleton_id=1;
""");
        if (terminal) AppendTerminal(path);
    }

    private static void AppendTerminal(string path) => Execute(path, $"""
INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
VALUES('{Id(23)}',3,'{OperationId}','AcceptedTerminal','authority_accepted_terminal','result-1',2000);
UPDATE journal_metadata SET journal_sequence=3 WHERE singleton_id=1;
""");

    private sealed class ThrowAfterCommit : IWorkflowResultFault
    {
        internal const string Marker = "QD3-SENSITIVE-RESULT-FAULT-18D4";
        public void Observe(WorkflowResultFaultPoint point) { if (point == WorkflowResultFaultPoint.AfterCommit) throw new IOException(Marker); }
    }
    private sealed class ActAtDiscovery(Action action) : IRecoveryDiscoveryFault
    {
        public void Observe(RecoveryDiscoveryFaultPoint point)
        {
            if (point == RecoveryDiscoveryFaultPoint.AfterWorkflowCommitBeforeJournalReadback)
                action();
        }
    }
    private static void Execute(string path, string sql)
    { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    private static object? Query(string path, string sql)
    { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static long Scalar(string path, string sql) => Convert.ToInt64(Query(path, sql));
    private static long EventuallyScalar(string path, string sql)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                return Scalar(path, sql);
            }
            catch (SqliteException exception) when (
                exception.SqliteErrorCode is 5 or 10 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
        }
    }
    private static string Text(string path, string sql) => Convert.ToString(Query(path, sql))!;
    private static string Id(int value) => $"00000000-0000-0000-0000-{value:000000000000}";
    private const string OperationId = "00000000-0000-0000-0000-000000000001";
    private const string ApprovalId = "00000000-0000-0000-0000-000000000002";
    private const string EventId = "00000000-0000-0000-0000-000000000003";
    private const string Digest = "sha256:dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private sealed record Fixture(string Workflow, string JournalPath, SimulationStorePaths Paths,
        SimulationAuthorityJournalRepository Authority, SimulationRecoveryCoordinator Recovery,
        SyntheticIdentityDirectory Directory, SyntheticActor Issuer, SyntheticActor Hr);
}
