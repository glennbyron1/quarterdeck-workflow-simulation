using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationIntentCommitTests
{
    [Fact]
    public void RecoveryDependencyCannotBeNull()
    {
        var fixture = Approved();

        Assert.Throws<ArgumentNullException>(() =>
            new SimulationIntentCoordinator(fixture.Repository, null!));
    }

    [Fact]
    public void StartupDiscoveryMustCompleteBeforeCommittedIntentCanDispatch()
    {
        var fixture = Approved(discoverRecovery: false);
        var root = Directory.GetParent(Path.GetDirectoryName(fixture.Path)!)!.FullName;
        var rehydratedConnections = new SimulationSqliteConnections(SimulationStorePaths.Create(
            fixture.Path, fixture.JournalPath, Path.Combine(root, "source")));
        var undiscovered = new SimulationRecoveryCoordinator(rehydratedConnections,
            new SimulationAuthorityJournalRepository(rehydratedConnections,
                new AuthorityCallCounters()));

        var result = new SimulationIntentCoordinator(fixture.Repository, undiscovered)
            .Commit(fixture.Command);

        Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, result.Status);
        Assert.Equal("recovery_finding_blocks_dispatch", result.SafeResultCode);
        Assert.True(result.IsDurablyCommitted);
        Assert.NotNull(result.Receipt);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
        Assert.Equal(2, Scalar(fixture.Path,
            "SELECT count(*) FROM workflow_resource_reservations WHERE released_utc IS NULL"));
    }

    [Fact]
    public void RecoveryFailureOnOneCoordinatorBlocksDispatchThroughAnother()
    {
        var fixture = Approved();
        var intent = Coordinator(fixture).Commit(fixture.Command);
        Assert.Equal(IntentCommitStatus.Committed, intent.Status);
        var root = Directory.GetParent(Path.GetDirectoryName(fixture.Path)!)!.FullName;
        var rehydratedConnections = new SimulationSqliteConnections(SimulationStorePaths.Create(
            fixture.Path, fixture.JournalPath, Path.Combine(root, "source")));
        var secondRecovery = new SimulationRecoveryCoordinator(rehydratedConnections,
            new SimulationAuthorityJournalRepository(rehydratedConnections,
                new AuthorityCallCounters()));

        var failedDiscovery = secondRecovery.DiscoverAtStartup(1600, new ThrowAtDiscovery());
        Assert.Equal("recovery_discovery_unknown", failedDiscovery.SafeResultCode);
        Assert.DoesNotContain(ThrowAtDiscovery.Marker, failedDiscovery.SafeResultCode,
            StringComparison.Ordinal);
        var dispatch = fixture.Recovery.Dispatch(new(intent.Receipt!));

        Assert.Equal(AuthorityDispatchStatus.NotAccepted, dispatch.Status);
        Assert.Equal("recovery_finding_blocks_dispatch", dispatch.SafeResultCode);
        Assert.Equal(0, fixture.Journal.AcceptanceCount);
    }

    [Fact]
    public async Task DiscoveryAndDispatchSerializeAcrossCoordinatorsForOneStoreSet()
    {
        var fixture = Approved();
        var intent = Coordinator(fixture).Commit(fixture.Command);
        Assert.Equal(IntentCommitStatus.Committed, intent.Status);
        var root = Directory.GetParent(Path.GetDirectoryName(fixture.Path)!)!.FullName;
        var rehydratedConnections = new SimulationSqliteConnections(SimulationStorePaths.Create(
            fixture.Path, fixture.JournalPath, Path.Combine(root, "source")));
        var secondRecovery = new SimulationRecoveryCoordinator(rehydratedConnections,
            new SimulationAuthorityJournalRepository(rehydratedConnections,
                new AuthorityCallCounters()));
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var firstDiscovery = Task.Run(() => secondRecovery.DiscoverAtStartup(1600,
            new BarrierDiscovery(reached, release)));
        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
        var secondDiscovery = Task.Run(() => fixture.Recovery.DiscoverAtStartup(1700));
        var dispatch = Task.Run(() => fixture.Recovery.Dispatch(new(intent.Receipt!)));
        await Task.Delay(100);

        Assert.False(secondDiscovery.IsCompleted);
        Assert.False(dispatch.IsCompleted);
        release.Set();
        Assert.Equal("recovery_discovery_unknown", (await firstDiscovery).SafeResultCode);
        Assert.Equal("recovery_discovery_complete", (await secondDiscovery).SafeResultCode);
        _ = await dispatch;
    }

    [Fact]
    public void RecoveryFromAnotherStoreSetIsRejected()
    {
        var fixture = Approved();
        var root = Path.Combine(Path.GetTempPath(),
            "quarterdeck-intent-alternate-" + Guid.NewGuid().ToString("N"));
        var connections = new SimulationSqliteConnections(SimulationStorePaths.Create(
            Path.Combine(root, "workflow", "data.db"),
            Path.Combine(root, "journal", "data.db"), Path.Combine(root, "source")));
        new SimulationSchemaInitializer(connections).Initialize("alternate-store-test");
        var recovery = new SimulationRecoveryCoordinator(connections,
            new SimulationAuthorityJournalRepository(connections, new AuthorityCallCounters()));
        Assert.Equal("recovery_discovery_complete",
            recovery.DiscoverAtStartup(700).SafeResultCode);

        var error = Assert.Throws<ArgumentException>(() =>
            new SimulationIntentCoordinator(fixture.Repository, recovery));

        Assert.Equal("recovery", error.ParamName);
        Assert.Contains("recovery_store_set_mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DispatchRechecksRecoveryAfterReceiptWasReturned()
    {
        var fixture = Approved();
        var intent = Coordinator(fixture).Commit(fixture.Command);
        Assert.Equal(IntentCommitStatus.Committed, intent.Status);
        var blocked = fixture.Recovery.DiscoverAtStartup(1600,
            new ThrowAtDiscovery());
        Assert.Equal("recovery_discovery_unknown", blocked.SafeResultCode);
        Assert.DoesNotContain(ThrowAtDiscovery.Marker, blocked.SafeResultCode,
            StringComparison.Ordinal);

        var dispatch = fixture.Recovery.Dispatch(new(intent.Receipt!));

        Assert.Equal(AuthorityDispatchStatus.NotAccepted, dispatch.Status);
        Assert.Equal("recovery_finding_blocks_dispatch", dispatch.SafeResultCode);
        Assert.Equal(0, fixture.Journal.AcceptanceCount);
    }

    [Fact]
    public void RecoveryStoreFailureAfterCommitRetainsReceiptAndBlocksDispatch()
    {
        var fixture = Approved();
        var fault = new ActAt(IntentCommitFaultPoint.AfterCommit, () =>
            Execute(fixture.Path, "DROP TABLE recovery_findings"));

        var result = Coordinator(fixture).Commit(fixture.Command, fault);

        Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, result.Status);
        Assert.Equal("recovery_gate_unavailable", result.SafeResultCode);
        Assert.True(result.IsDurablyCommitted);
        Assert.NotNull(result.Receipt);
    }

    [Fact]
    public void ExactIntentCommitsAllRowsAndReturnsDurableReceipt()
    {
        var fixture = Approved();
        var result = Coordinator(fixture).Commit(fixture.Command);

        Assert.Equal(IntentCommitStatus.Committed, result.Status);
        Assert.NotNull(result.Receipt);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operation_events"));
        Assert.Equal(2, Scalar(fixture.Path, "SELECT count(*) FROM workflow_resource_reservations"));
        Assert.Equal(fixture.Command.OperationId, Text(fixture.Path,
            "SELECT consumed_by_operation_id FROM approvals"));
        Assert.Equal(7, Scalar(fixture.Path, "SELECT commit_sequence FROM workflow_metadata"));
        Assert.Equal(1, Scalar(fixture.Path,
            "SELECT count(*) FROM audit_events WHERE operation_id IS NOT NULL"));
        Assert.Equal(IntentReadbackState.Exact,
            fixture.Repository.ReadIntent(fixture.Command, result.Receipt!).State);
    }

    [Theory]
    [InlineData((int)IntentCommitFaultPoint.BeforeWrites)]
    [InlineData((int)IntentCommitFaultPoint.BeforeCommit)]
    public void PreCommitFaultRollsBackEveryProjection(
        int pointValue)
    {
        var point = (IntentCommitFaultPoint)pointValue;
        var fixture = Approved();
        var result = Coordinator(fixture)
            .Commit(fixture.Command, new ThrowAt(point));

        Assert.Equal(IntentCommitStatus.NotCommitted, result.Status);
        Assert.DoesNotContain(ThrowAt.Marker, result.SafeResultCode, StringComparison.Ordinal);
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM workflow_resource_reservations"));
        Assert.Equal(6, Scalar(fixture.Path, "SELECT commit_sequence FROM workflow_metadata"));
        Assert.Null(TextOrNull(fixture.Path, "SELECT consumed_by_operation_id FROM approvals"));
    }

    [Fact]
    public void PostCommitFaultUsesExactReadback()
    {
        var fixture = Approved();
        var result = Coordinator(fixture)
            .Commit(fixture.Command, new ThrowAt(IntentCommitFaultPoint.AfterCommit));

        Assert.Equal(IntentCommitStatus.CommittedAfterReadback, result.Status);
        Assert.Equal("intent_committed_after_readback", result.SafeResultCode);
        Assert.DoesNotContain(ThrowAt.Marker, result.SafeResultCode, StringComparison.Ordinal);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public async Task DelayedCommitDoesNotReturnDurableReceiptBeforeCommit()
    {
        var fixture = Approved();
        using var reached = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var task = Task.Run(() => Coordinator(fixture)
            .Commit(fixture.Command, new BarrierAtCommit(reached, release)));

        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(task.IsCompleted);
        release.Set();
        var result = await task;
        Assert.Equal(IntentCommitStatus.Committed, result.Status);
    }

    [Theory]
    [InlineData(999, "before_approval")]
    [InlineData(2000, "at_expiry")]
    public void HalfOpenApprovalWindowCannotMintDispatchAuthority(long observedUtc, string _)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Approved(observedUtc));
        Assert.True(error.Message is "simulation_clock_before_approval" or "approval_expired");
    }

    [Fact]
    public void RevokedApprovalAndUnresolvedLatestGateBothRejectBeforeMutation()
    {
        var revoked = Approved();
        Execute(revoked.Path,
            "UPDATE approvals SET revoked_utc=1200,revocation_reason_code='revoked' WHERE approval_id=$id",
            ("$id", revoked.Command.ApprovalId));
        Assert.Equal(IntentCommitStatus.NotCommitted, revoked.Repository.CommitIntent(revoked.Command).Status);

        var unresolved = Approved();
        unresolved.Repository.AppendGateObservation(new(Id(90), "case-1", 5, 1,
            "directory_ready", "NotSatisfied", "not_ready", Actor("it", "ItFormalRequester"),
            1300, "Intent", "prepare", Id(91)));
        var changed = Copy(unresolved.Command, expectedRevision: 6);
        Assert.Equal(IntentCommitStatus.NotCommitted, unresolved.Repository.CommitIntent(changed).Status);
        Assert.Equal(0, Scalar(unresolved.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public void SupersededFormalVersionCannotCommitIntent()
    {
        var fixture = Approved();
        Execute(fixture.Path, """
UPDATE formal_request_versions SET superseded_utc=1200,superseded_reason_code='inputs_changed'
 WHERE case_id='case-1' AND version=1;
UPDATE approvals SET invalidated_utc=1200,invalidation_reason_code='inputs_changed'
 WHERE approval_id=$id;
UPDATE cases SET current_formal_version=NULL,row_revision=row_revision+1 WHERE case_id='case-1';
""", ("$id", fixture.Command.ApprovalId));
        Assert.Equal(IntentCommitStatus.NotCommitted,
            fixture.Repository.CommitIntent(Copy(fixture.Command, expectedRevision: 6)).Status);
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public async Task ConcurrentApprovalConsumptionAllowsExactlyOneIntent()
    {
        var fixture = Approved();
        var other = Copy(fixture.Command, operationId: Id(25), eventId: Id(26),
            auditId: Id(27), attempt: 2);
        var results = await Task.WhenAll(Task.Run(() => fixture.Repository.CommitIntent(fixture.Command)),
            Task.Run(() => fixture.Repository.CommitIntent(other)));
        Assert.Single(results, result => result.IsDurablyCommitted);
        Assert.Single(results, result => result.Status == IntentCommitStatus.NotCommitted);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public void ExactReadbackRejectsDifferentExpectedDigest()
    {
        var fixture = Approved();
        var committed = fixture.Repository.CommitIntent(fixture.Command);
        var original = committed.Receipt!;
        var receipt = new IntentCommitReceipt(original.OperationId,
            "sha256:" + new string('0', 64), original.WorkflowDatabaseInstanceId,
            original.IntentCommitSequence, original.Admission);
        var readback = fixture.Repository.ReadIntent(fixture.Command, receipt);
        Assert.Equal(IntentReadbackState.Mismatch, readback.State);
        Assert.False(new IntentCommitResult(IntentCommitStatus.Uncertain,
            readback.SafeResultCode, null).IsDurablyCommitted);
    }

    [Fact]
    public void ApprovalReplayFailsClosed()
    {
        var fixture = Approved();
        Assert.True(fixture.Repository.CommitIntent(fixture.Command).IsDurablyCommitted);
        var replay = Copy(fixture.Command, operationId: Id(21), eventId: Id(22), auditId: Id(23),
            expectedRevision: 6, attempt: 2);
        Assert.Equal(IntentCommitStatus.NotCommitted, fixture.Repository.CommitIntent(replay).Status);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));

    }

    [Fact]
    public void ExactOperationRetryReturnsOriginalReceiptWithoutWritingAgain()
    {
        var fixture = Approved();
        var first = fixture.Repository.CommitIntent(fixture.Command);
        var second = fixture.Repository.CommitIntent(fixture.Command);

        Assert.Equal(IntentCommitStatus.Committed, first.Status);
        Assert.Equal(IntentCommitStatus.CommittedAfterReadback, second.Status);
        Assert.Equal("intent_already_committed", second.SafeResultCode);
        Assert.Equal(first.Receipt, second.Receipt);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operation_events"));
        Assert.Equal(1, Scalar(fixture.Path,
            "SELECT count(*) FROM audit_events WHERE operation_id IS NOT NULL"));
    }

    [Fact]
    public void ExpiredExactRetryPreservesCommittedReceiptButBlocksDispatch()
    {
        var fixture = Approved();
        var first = fixture.Repository.CommitIntent(fixture.Command);
        fixture.Clock.SetUtc(DateTimeOffset.FromUnixTimeMilliseconds(2000));

        var retry = fixture.Repository.CommitIntent(fixture.Command);

        Assert.Equal(IntentCommitStatus.Committed, first.Status);
        Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, retry.Status);
        Assert.Equal(first.Receipt, retry.Receipt);
        Assert.True(retry.IsDurablyCommitted);
    }

    [Fact]
    public void WidenedApprovalIntervalCannotMasqueradeAsExactRetry()
    {
        var fixture = Approved();
        Assert.Equal(IntentCommitStatus.Committed,
            fixture.Repository.CommitIntent(fixture.Command).Status);
        fixture.Clock.SetUtc(DateTimeOffset.FromUnixTimeMilliseconds(2000));
        var widened = CaptureAdmission(
            fixture.Environment, "case-1", 10, "subject-account", "fake-device",
            DateTimeOffset.FromUnixTimeMilliseconds(2000),
            DateTimeOffset.FromUnixTimeMilliseconds(3000));
        var retry = new CommitIntentCommand(
            fixture.Command.OperationId, fixture.Command.EventId,
            fixture.Command.AuditEventId, widened);

        var result = fixture.Repository.CommitIntent(retry);

        Assert.Equal(IntentCommitStatus.Uncertain, result.Status);
        Assert.False(result.IsDurablyCommitted);
    }

    [Fact]
    public void DisabledIssuerCannotCommitThroughDirectRepositoryEntry()
    {
        var fixture = Approved();
        SetDirectoryState(fixture.Directory, "SetAccountState",
            new SyntheticAccountId("issuer-account"), SyntheticAccountState.Disabled);

        var result = fixture.Repository.CommitIntent(fixture.Command);

        Assert.Equal(IntentCommitStatus.NotCommitted, result.Status);
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public void BackwardTrustedClockCannotCommitThroughDirectRepositoryEntry()
    {
        var fixture = Approved();
        fixture.Clock.SetUtc(DateTimeOffset.FromUnixTimeMilliseconds(1499));

        var result = fixture.Repository.CommitIntent(fixture.Command);

        Assert.Equal(IntentCommitStatus.NotCommitted, result.Status);
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public void SubMillisecondClockCannotBeTruncatedIntoDispatchAuthority()
    {
        var environment = CreateEnvironment(1500);
        environment.Clock.SetUtc(
            DateTimeOffset.FromUnixTimeMilliseconds(1500).AddTicks(1));

        var error = Assert.Throws<InvalidOperationException>(() => CaptureAdmission(
            environment, "case-1", 10, "subject-account", "fake-device"));

        Assert.Equal("simulation_clock_unavailable", error.Message);
    }

    [Fact]
    public void UnexpectedSourceStateCannotSkipToDispatch()
    {
        var fixture = Approved(sourceStep: "Approval", sourceAction: "approve");

        var result = fixture.Repository.CommitIntent(fixture.Command);

        Assert.Equal(IntentCommitStatus.NotCommitted, result.Status);
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public void LookalikeFixtureCannotAuthorizeBoundRepository()
    {
        var fixture = Approved();
        var alternate = CreateEnvironment(1500);
        var admission = CaptureAdmission(
            alternate, "case-1", 10, "subject-account", "fake-device");
        var forged = new CommitIntentCommand(
            fixture.Command.OperationId,
            fixture.Command.EventId,
            fixture.Command.AuditEventId,
            admission);

        var result = fixture.Repository.CommitIntent(forged);

        Assert.Equal(IntentCommitStatus.NotCommitted, result.Status);
        Assert.Equal(0, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Fact]
    public void AuthorityRemovalAfterCommitBlocksDispatchWithoutRewritingCommittedFact()
    {
        var fixture = Approved();
        var fault = new ActAt(IntentCommitFaultPoint.AfterCommit, () =>
            SetDirectoryState(fixture.Directory, "SetRoleState",
                new SyntheticAccountId("issuer-account"),
                SyntheticWorkflowRole.ItIssuer,
                SyntheticRoleState.Removed));

        var result = Coordinator(fixture)
            .Commit(fixture.Command, fault);

        Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, result.Status);
        Assert.Equal("intent_committed_dispatch_authority_unavailable", result.SafeResultCode);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
    }

    [Theory]
    [InlineData("subject-account", "other-device")]
    [InlineData("other-account", "fake-device")]
    public void AccountAndDeviceConflictsRollBackSecondIntent(string account, string device)
    {
        var fixture = Approved();
        Assert.Equal(IntentCommitStatus.Committed, fixture.Repository.CommitIntent(fixture.Command).Status);
        var second = PrepareApproved(
            fixture.Repository, fixture.Environment, "case-2", 100, account, device).Command;
        Assert.Equal(IntentCommitStatus.NotCommitted, fixture.Repository.CommitIntent(second).Status);
        Assert.Equal(1, Scalar(fixture.Path, "SELECT count(*) FROM operations"));
        Assert.Null(TextOrNull(fixture.Path,
            "SELECT consumed_by_operation_id FROM approvals WHERE approval_id='00000000-0000-0000-0000-000000000101'"));
    }

    [Fact]
    public void ExactTerminalPersistenceReleasesReservationsForNewIntentAndDispatch()
    {
        var fixture = Approved();
        var firstIntent = Coordinator(fixture)
            .Commit(fixture.Command);
        Assert.True(firstIntent.IsDurablyCommitted);
        Assert.True(fixture.Recovery.Dispatch(new(firstIntent.Receipt!)).IsAccepted);
        Execute(fixture.JournalPath, """
BEGIN IMMEDIATE;
INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
VALUES('00000000-0000-0000-0000-000000000099',3,$operation,'AcceptedTerminal','authority_accepted_terminal','result-1',1600);
UPDATE authority_resource_reservations SET released_utc=1600,release_reason_code='authority_terminal'
WHERE operation_id=$operation AND released_utc IS NULL;
UPDATE journal_metadata SET journal_sequence=3 WHERE singleton_id=1;
COMMIT;
""", ("$operation", fixture.Command.OperationId));
        var persisted = fixture.Recovery.PersistObservation(new(Id(90), fixture.Command.OperationId,
            firstIntent.Receipt!.OperationBindingDigest, 1600));
        Assert.Equal(WorkflowResultPersistenceStatus.Persisted, persisted.Status);
        Assert.Equal(0, Scalar(fixture.Path,
            "SELECT count(*) FROM workflow_resource_reservations WHERE released_utc IS NULL"));
        Assert.Equal(fixture.Command.OperationId, Text(fixture.Path,
            "SELECT consumed_by_operation_id FROM approvals WHERE approval_id='00000000-0000-0000-0000-000000000011'"));
        Assert.Equal("journal:", Text(fixture.Path,
            "SELECT substr(safe_reference,1,8) FROM operation_events WHERE event_id='00000000-0000-0000-0000-000000000090'"));
        Assert.Equal("recovery_discovery_complete", fixture.Recovery.DiscoverAtStartup(1700).SafeResultCode);

        var second = PrepareApproved(fixture.Repository, fixture.Environment,
            "case-2", 100, "subject-account", "fake-device").Command;
        var secondIntent = Coordinator(fixture).Commit(second);
        Assert.Equal(IntentCommitStatus.Committed, secondIntent.Status);
        var secondDispatch = fixture.Recovery.Dispatch(new(secondIntent.Receipt!));
        Assert.Equal(AuthorityDispatchStatus.AcceptedPending, secondDispatch.Status);
    }

    [Fact]
    public void AsymmetricRestoreBlocksOriginalAndNewOperationReplayButPermitsUnrelatedResources()
    {
        var scenario = CreateAsymmetricRestoreScenario();
        var restored = OpenAsymmetricRestore(scenario);

        var discovery = restored.Recovery.DiscoverAtStartup(1700);

        Assert.Equal("recovery_discovery_complete", discovery.SafeResultCode);
        Assert.Equal(2, discovery.Findings.Count);
        Assert.All(discovery.Findings, finding =>
        {
            Assert.Equal(RecoveryMatchState.AbsentLocal, finding.MatchState);
            Assert.True(finding.BlocksDispatch);
        });
        Assert.Contains(discovery.Findings,
            finding => finding.OperationId == scenario.Terminal.OperationId);
        Assert.Contains(discovery.Findings,
            finding => finding.OperationId == scenario.Pending.OperationId);
        Assert.Equal("AcceptedTerminal", Text(scenario.JournalBackup.Path, """
            SELECT event_code FROM authority_events WHERE operation_id=$operation
            ORDER BY journal_sequence DESC LIMIT 1
            """, ("$operation", scenario.Terminal.OperationId)));
        Assert.Equal("AcceptedPending", Text(scenario.JournalBackup.Path, """
            SELECT event_code FROM authority_events WHERE operation_id=$operation
            ORDER BY journal_sequence DESC LIMIT 1
            """, ("$operation", scenario.Pending.OperationId)));
        Assert.Equal(0, Scalar(scenario.WorkflowBackup.Path,
            "SELECT count(*) FROM operations"));

        var original = restored.Coordinator.Commit(scenario.Terminal);
        var newCommand = scenario.Pending.Reissue(Id(700), Id(701), Id(702),
            scenario.Pending.ExpectedCaseRevision, 2);
        var reissued = restored.Coordinator.Commit(newCommand);

        Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, original.Status);
        Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, reissued.Status);
        Assert.Equal(AuthorityDispatchStatus.NotAccepted,
            restored.Recovery.Dispatch(new(original.Receipt!)).Status);
        Assert.Equal(AuthorityDispatchStatus.NotAccepted,
            restored.Recovery.Dispatch(new(reissued.Receipt!)).Status);
        Assert.Equal(0, restored.Counters.DispatchCalls);
        Assert.Equal(2, restored.Journal.AcceptanceCount);
        Assert.Equal(scenario.Terminal.OperationId, Text(scenario.JournalBackup.Path,
            "SELECT operation_id FROM approval_consumptions WHERE approval_id=$approval",
            ("$approval", scenario.Terminal.ApprovalId)));
        Assert.Equal(scenario.Pending.OperationId, Text(scenario.JournalBackup.Path,
            "SELECT operation_id FROM approval_consumptions WHERE approval_id=$approval",
            ("$approval", scenario.Pending.ApprovalId)));

        Assert.False(restored.Recovery.IsDispatchBlocked("site-1", "safe-account",
            FormalRequestFields.RequiredIntendedUse, "safe-device", FormalRequestFields.RequiredSlot));
        var safe = restored.Coordinator.Commit(scenario.Safe);
        Assert.Equal(IntentCommitStatus.Committed, safe.Status);
        Assert.Equal(AuthorityDispatchStatus.AcceptedPending,
            restored.Recovery.Dispatch(new(safe.Receipt!)).Status);
        Assert.Equal(1, restored.Counters.DispatchCalls);
        Assert.Equal(3, restored.Journal.AcceptanceCount);
    }

    [Fact]
    public void AsymmetricRestoreWithUnavailableJournalBlocksGloballyWithoutCallsOrAcceptances()
    {
        var scenario = CreateAsymmetricRestoreScenario();
        var retained = scenario.JournalBackup.Path + ".retained";
        File.Move(scenario.JournalBackup.Path, retained);
        var restored = OpenAsymmetricRestore(scenario, includeJournal: false);

        var discovery = restored.Recovery.DiscoverAtStartup(1700);

        Assert.Equal("recovery_journal_unavailable", discovery.SafeResultCode);
        Assert.Equal(RecoveryMatchState.Unknown, Assert.Single(discovery.Findings).MatchState);
        var original = restored.Coordinator.Commit(scenario.Terminal);
        var reissued = restored.Coordinator.Commit(scenario.Pending.Reissue(
            Id(710), Id(711), Id(712), scenario.Pending.ExpectedCaseRevision, 2));
        var safe = restored.Coordinator.Commit(scenario.Safe);
        Assert.All(new[] { original, reissued, safe }, result =>
            Assert.Equal(IntentCommitStatus.CommittedDispatchBlocked, result.Status));
        Assert.Equal(AuthorityDispatchStatus.NotAccepted,
            restored.Recovery.Dispatch(new(original.Receipt!)).Status);
        Assert.Equal(AuthorityDispatchStatus.NotAccepted,
            restored.Recovery.Dispatch(new(reissued.Receipt!)).Status);
        Assert.Equal(AuthorityDispatchStatus.NotAccepted,
            restored.Recovery.Dispatch(new(safe.Receipt!)).Status);
        Assert.Equal(0, restored.Counters.DispatchCalls);
        Assert.Equal(2, Scalar(retained, """
            SELECT count(DISTINCT operation_id) FROM authority_events
            WHERE event_code IN ('AcceptedPending','AcceptedTerminal')
            """));
    }

    private static AsymmetricScenario CreateAsymmetricRestoreScenario()
    {
        var fixture = Approved(discoverRecovery: false);
        var pending = PrepareApproved(fixture.Repository, fixture.Environment,
            "case-pending", 100, "other-account", "other-device").Command;
        var safe = PrepareApproved(fixture.Repository, fixture.Environment,
            "case-safe", 200, "safe-account", "safe-device").Command;
        var root = Directory.GetParent(Path.GetDirectoryName(fixture.Path)!)!.FullName;
        var backup = new SimulationSqliteBackup(fixture.Connections);
        var workflow = backup.CreateWorkflowBackup(
            Path.Combine(root, "workflow-backup", "pre-consumption.db"));
        Assert.Equal("recovery_discovery_complete",
            fixture.Recovery.DiscoverAtStartup(700).SafeResultCode);
        var terminalIntent = Coordinator(fixture).Commit(fixture.Command);
        var pendingIntent = Coordinator(fixture).Commit(pending);
        Assert.True(fixture.Recovery.Dispatch(new(terminalIntent.Receipt!)).IsAccepted);
        Assert.True(fixture.Recovery.Dispatch(new(pendingIntent.Receipt!)).IsAccepted);
        Execute(fixture.JournalPath, """
            INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
            VALUES($event,5,$operation,'AcceptedTerminal','authority_accepted_terminal','result-1',1600);
            UPDATE authority_resource_reservations SET released_utc=1600,release_reason_code='authority_terminal'
            WHERE operation_id=$operation AND released_utc IS NULL;
            UPDATE journal_metadata SET journal_sequence=5 WHERE singleton_id=1;
            """, ("$event", Id(900)), ("$operation", fixture.Command.OperationId));
        var journal = backup.CreateJournalBackup(
            Path.Combine(root, "journal-backup", "newer.db"));
        Assert.Equal(SimulationSqliteBackupKind.Workflow, workflow.Kind);
        Assert.Equal(SimulationSqliteBackupKind.Journal, journal.Kind);
        Assert.Equal(2, fixture.Journal.AcceptanceCount);
        return new(root, workflow, journal, fixture.Environment,
            fixture.Command, pending, safe);
    }

    private static AsymmetricRestored OpenAsymmetricRestore(AsymmetricScenario scenario,
        bool includeJournal = true)
    {
        var workflow = Path.Combine(scenario.Root, "restored-workflow", "data.db");
        var journalPath = Path.Combine(scenario.Root, "restored-journal", "data.db");
        Directory.CreateDirectory(Path.GetDirectoryName(workflow)!);
        Directory.CreateDirectory(Path.GetDirectoryName(journalPath)!);
        File.Copy(scenario.WorkflowBackup.Path, workflow);
        File.SetAttributes(workflow, FileAttributes.Normal);
        SimulationSqliteBackup.VerifyArtifact(scenario.WorkflowBackup, workflow);
        if (includeJournal)
        {
            File.Copy(scenario.JournalBackup.Path, journalPath);
            File.SetAttributes(journalPath, FileAttributes.Normal);
            SimulationSqliteBackup.VerifyArtifact(scenario.JournalBackup, journalPath);
        }
        var paths = SimulationStorePaths.Create(workflow,
            journalPath, Path.Combine(scenario.Root, "restored-source"));
        Directory.CreateDirectory(paths.SourceRoot);
        var connections = new SimulationSqliteConnections(paths);
        var repository = new SimulationWorkflowRepository(connections,
            scenario.Environment.Authority);
        var counters = new AuthorityCallCounters();
        var journal = new SimulationAuthorityJournalRepository(connections, counters);
        var recovery = new SimulationRecoveryCoordinator(connections, journal);
        return new(journal, recovery, counters,
            new SimulationIntentCoordinator(repository, recovery));
    }

    private static Fixture Approved(
        long observedUtc = 1500,
        string caseId = "case-1",
        int operationBase = 10,
        string sourceStep = "Intent",
        string sourceAction = "prepare",
        bool discoverRecovery = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "quarterdeck-intent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workflow", "data.db");
        var journalPath = Path.Combine(root, "journal", "data.db");
        var connections = new SimulationSqliteConnections(SimulationStorePaths.Create(path,
            journalPath, Path.Combine(root, "source")));
        new SimulationSchemaInitializer(connections).Initialize("intent-test");
        var environment = CreateEnvironment(observedUtc);
        var repository = new SimulationWorkflowRepository(connections, environment.Authority);
        var prepared = PrepareApproved(repository, environment, caseId, operationBase,
            "subject-account", "fake-device", sourceStep, sourceAction);
        var journal = new SimulationAuthorityJournalRepository(connections,
            new AuthorityCallCounters());
        var recovery = new SimulationRecoveryCoordinator(connections, journal);
        if (discoverRecovery)
        {
            Assert.Equal("recovery_discovery_complete",
                recovery.DiscoverAtStartup(700).SafeResultCode);
        }
        return new(path, journalPath, connections, repository, journal, recovery,
            prepared.Command, environment);
    }

    private static PreparedIntent PrepareApproved(SimulationWorkflowRepository repository,
        AdmissionEnvironment environment,
        string caseId,
        int operationBase,
        string accountId,
        string deviceId,
        string sourceStep = "Intent",
        string sourceAction = "prepare")
    {
        var hrActor = environment.Hr;
        var verifier = environment.Verifier;
        var approver = environment.Approver;
        var requester = environment.Requester;
        var hr = Persisted(hrActor, SyntheticWorkflowRole.HrIntake);
        var it = Persisted(requester, SyntheticWorkflowRole.ItFormalRequester);
        var request = new FormalRequestSnapshot("site-1", "subject", accountId,
            FormalRequestFields.RequiredIntendedUse, deviceId, FormalRequestFields.RequiredSlot,
            new string('a', 64), "fake-authority", "template", "v1");
        var policy = SimulationPolicySnapshot.CreateClassified("policy-authority", "v1",
            [new("proof-required", SimulationPolicyOptionClassification.Required)]);
        var stored = new PolicySnapshot(policy.AuthorityId, policy.Version, policy.ContentDigest,
            [new("proof-required", "Required")]);
        var digest = SealedRequestBinding.Compute(new(new(request.SiteId), new(request.PersonId),
            new(request.AccountId), request.IntendedUse, request.FakeDeviceId, request.Slot,
            request.SyntheticPublicRequestDigest, request.FakeAuthority, request.Template,
            request.PolicyOptionsVersion), policy, 1);
        repository.CreateIntake(new(caseId, "subject", "site-1", hr, 800, "FormalRequest",
            "formalize", [], Id(operationBase + 50)));
        repository.AppendFormalVersion(new(caseId, 0, 1, request, it, 900, stored, 1, digest,
            null, null, null, null, "IdentityVerification", "gate", Id(operationBase + 51)));
        repository.AppendGateObservation(new(Id(operationBase + 52), caseId, 1, 1,
            "directory_ready", "Satisfied", "ready", it, 950, "IdentityVerification",
            "verify", Id(operationBase + 53)));
        repository.AppendIdentityVerification(new(Id(operationBase + 54), caseId, 2, 1,
            "subject", Persisted(verifier, SyntheticWorkflowRole.HrVerifier), 975, 1, "Approval", "approve",
            Id(operationBase + 55)));
        repository.AppendApproval(new(Id(operationBase + 1), caseId, 3, 1,
            Persisted(approver, SyntheticWorkflowRole.HrApprover), 1000, 2000, digest, policy.AuthorityId,
            policy.Version, policy.ContentDigest, null, null, null, "Intent", "prepare",
            Id(operationBase + 56)));
        // A second satisfied observation proves latest-sequence semantics and makes
        // the expected pre-intent commit sequence/revision six/five.
        repository.AppendGateObservation(new(Id(operationBase + 57), caseId, 4, 1,
            "directory_ready", "Satisfied", "ready-latest", it, 1100, sourceStep, sourceAction,
            Id(operationBase + 58)));
        var admission = CaptureAdmission(environment, caseId, operationBase, accountId, deviceId);
        return new(new CommitIntentCommand(
            Id(operationBase), Id(operationBase + 2), Id(operationBase + 3), admission));
    }

    private static SyntheticDispatchAdmission CaptureAdmission(
        AdmissionEnvironment environment,
        string caseId,
        int operationBase,
        string accountId,
        string deviceId,
        DateTimeOffset? approvedAt = null,
        DateTimeOffset? expiresAt = null)
    {
        var fields = new FormalRequestFields(environment.Site, new("subject"), new(accountId),
            FormalRequestFields.RequiredIntendedUse, deviceId, FormalRequestFields.RequiredSlot,
            new string('a', 64), "fake-authority", "template", "v1");
        return environment.Authority.Capture(
            caseId, 1, Id(operationBase + 1), 5, 1, "request-reference",
            environment.Requester, environment.Verifier, environment.Approver,
            environment.Issuer, fields,
            approvedAt ?? DateTimeOffset.FromUnixTimeMilliseconds(1000),
            expiresAt ?? DateTimeOffset.FromUnixTimeMilliseconds(2000));
    }

    private static AdmissionEnvironment CreateEnvironment(long observedUtc)
    {
        var site = new SyntheticSiteId("site-1");
        var directory = SyntheticIdentityDirectory.Create(
        [
            Binding("subject", "subject-account", "subject display", site),
            Binding("subject", "other-account", "subject alternate", site),
            Binding("subject", "safe-account", "subject safe", site),
            Binding("hr", "hr-account", "hr display", site, SyntheticWorkflowRole.HrIntake),
            Binding("verify", "verify-account", "verify display", site, SyntheticWorkflowRole.HrVerifier),
            Binding("approve", "approve-account", "approve display", site, SyntheticWorkflowRole.HrApprover),
            Binding("it", "it-account", "it display", site, SyntheticWorkflowRole.ItFormalRequester),
            Binding("issuer", "issuer-account", "issuer display", site, SyntheticWorkflowRole.ItIssuer)
        ]);
        foreach (var account in new[] { "subject-account", "other-account", "safe-account" })
            SetDirectoryState(directory, "SetContainmentState", new SyntheticAccountId(account),
                SyntheticContainmentState.Clear);
        foreach (var device in new[] { "fake-device", "other-device", "safe-device" })
            SetDirectoryState(directory, "SetDeviceQualificationState", device,
                SyntheticDeviceQualificationState.Qualified);
        SetDirectoryState(directory, "SetAuthorityAvailabilityState", "fake-authority",
            SyntheticAuthorityAvailabilityState.Available);
        var clock = new SyntheticSimulationApprovalClock(
            DateTimeOffset.FromUnixTimeMilliseconds(observedUtc));
        return new(
            site,
            directory,
            clock,
            new SyntheticDispatchAdmissionAuthority(directory, clock),
            directory.Resolve(new("hr-account")),
            directory.Resolve(new("verify-account")),
            directory.Resolve(new("approve-account")),
            directory.Resolve(new("it-account")),
            directory.Resolve(new("issuer-account")));
    }

    private static CommitIntentCommand Copy(CommitIntentCommand c, string? operationId = null,
        string? eventId = null, string? auditId = null, long? expectedRevision = null,
        long? attempt = null) => c.Reissue(
            operationId ?? c.OperationId,
            eventId ?? c.EventId,
            auditId ?? c.AuditEventId,
            expectedRevision ?? c.ExpectedCaseRevision,
            attempt ?? c.AttemptNumber);

    private sealed class ThrowAt(IntentCommitFaultPoint point) : IIntentCommitFault
    {
        internal const string Marker = "QD3-SENSITIVE-INTENT-FAULT-7B8A";
        public void Observe(IntentCommitFaultPoint observed) { if (observed == point) throw new IOException(Marker); }
    }

    private sealed class BarrierAtCommit(ManualResetEventSlim reached, ManualResetEventSlim release)
        : IIntentCommitFault
    { public void Observe(IntentCommitFaultPoint point) { if (point == IntentCommitFaultPoint.BeforeCommit) { reached.Set(); release.Wait(); } } }

    private sealed class ActAt(IntentCommitFaultPoint point, Action action) : IIntentCommitFault
    {
        public void Observe(IntentCommitFaultPoint observed)
        {
            if (observed == point) action();
        }
    }

    private sealed class ThrowAtDiscovery : IRecoveryDiscoveryFault
    {
        internal const string Marker = "QD3-SENSITIVE-DISCOVERY-FAULT-93CE";
        public void Observe(RecoveryDiscoveryFaultPoint point) =>
            throw new IOException(Marker);
    }

    private sealed class BarrierDiscovery(
        ManualResetEventSlim reached,
        ManualResetEventSlim release) : IRecoveryDiscoveryFault
    {
        public void Observe(RecoveryDiscoveryFaultPoint point)
        {
            if (point != RecoveryDiscoveryFaultPoint.AfterWorkflowCommitBeforeJournalReadback)
                return;
            reached.Set();
            release.Wait();
            throw new IOException("injected discovery failure");
        }
    }

    private static PersistedActor Actor(string prefix, string role) =>
        new(prefix + "-person", prefix + "-account", prefix + " display", role);
    private static PersistedActor Persisted(SyntheticActor actor, SyntheticWorkflowRole role) =>
        new(actor.PersonId.Value, actor.AccountId.Value, actor.DisplayLabel, role.ToString());
    private static SyntheticIdentityBinding Binding(string person, string account, string display,
        SyntheticSiteId site, params SyntheticWorkflowRole[] roles) =>
        new(new(person), new(account), display, site, roles.ToHashSet());
    private static void SetDirectoryState(
        SyntheticIdentityDirectory directory, string method, params object[] values)
    {
        var target = typeof(SyntheticIdentityDirectory).GetMethod(
            method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("fixture_mutator_missing");
        target.Invoke(directory, values);
    }
    private static string Id(int value) => $"00000000-0000-0000-0000-{value:000000000000}";
    private static long Scalar(string path, string sql) => Convert.ToInt64(Query(path, sql)!);
    private static string Text(string path, string sql) => Convert.ToString(Query(path, sql))!;
    private static string Text(string path, string sql,
        params (string Name, object Value)[] values)
    {
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        return Convert.ToString(command.ExecuteScalar())!;
    }
    private static string? TextOrNull(string path, string sql) => Query(path, sql) as string;
    private static object? Query(string path, string sql)
    { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var c = db.CreateCommand(); c.CommandText = sql; return c.ExecuteScalar(); }
    private static void Execute(string path, string sql, params (string Name, object Value)[] values)
    { using var db = new SqliteConnection($"Data Source={path}"); db.Open(); using var c = db.CreateCommand(); c.CommandText = sql; foreach (var v in values) c.Parameters.AddWithValue(v.Name, v.Value); c.ExecuteNonQuery(); }
    private static SimulationIntentCoordinator Coordinator(Fixture fixture) =>
        new(fixture.Repository, fixture.Recovery);
    private readonly record struct PreparedIntent(CommitIntentCommand Command);
    private sealed record AsymmetricScenario(string Root,
        SimulationSqliteBackupManifest WorkflowBackup,
        SimulationSqliteBackupManifest JournalBackup,
        AdmissionEnvironment Environment, CommitIntentCommand Terminal,
        CommitIntentCommand Pending, CommitIntentCommand Safe);
    private sealed record AsymmetricRestored(SimulationAuthorityJournalRepository Journal,
        SimulationRecoveryCoordinator Recovery, AuthorityCallCounters Counters,
        SimulationIntentCoordinator Coordinator);
    private sealed record AdmissionEnvironment(
        SyntheticSiteId Site,
        SyntheticIdentityDirectory Directory,
        SyntheticSimulationApprovalClock Clock,
        SyntheticDispatchAdmissionAuthority Authority,
        SyntheticActor Hr,
        SyntheticActor Verifier,
        SyntheticActor Approver,
        SyntheticActor Requester,
        SyntheticActor Issuer);
    private readonly record struct Fixture(
        string Path,
        string JournalPath,
        SimulationSqliteConnections Connections,
        SimulationWorkflowRepository Repository,
        SimulationAuthorityJournalRepository Journal,
        SimulationRecoveryCoordinator Recovery,
        CommitIntentCommand Command,
        AdmissionEnvironment Environment)
    {
        internal SyntheticIdentityDirectory Directory => Environment.Directory;
        internal SyntheticSimulationApprovalClock Clock => Environment.Clock;
    }
}
