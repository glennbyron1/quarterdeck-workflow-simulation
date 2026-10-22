using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationAuthorityJournalTests
{
    [Fact]
    public void DispatchCallsAndCommittedAcceptancesAreCountedSeparately()
    {
        var fixture = NewFixture();
        var accepted = fixture.Recovery.Dispatch(fixture.Command);
        var replay = fixture.Recovery.Dispatch(Reissue(fixture, operation: 20, approval: 21));

        Assert.Equal(AuthorityDispatchStatus.AcceptedPending, accepted.Status);
        Assert.Equal(AuthorityDispatchStatus.RejectedBeforeAcceptance, replay.Status);
        Assert.Equal(2, fixture.Counters.DispatchCalls);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public void SameOperationObservationIsExactAndReadOnly()
    {
        var fixture = NewFixture();
        var accepted = fixture.Recovery.Dispatch(fixture.Command);
        var beforeSequence = Scalar(fixture.Journal, "SELECT journal_sequence FROM journal_metadata");

        var observed = fixture.Repository.Observe(new(
            fixture.Command.OperationId, fixture.Command.BindingDigest));

        Assert.Equal(AuthorityOperationState.AcceptedPending, observed.State);
        Assert.Equal(accepted.Receipt, observed.Receipt);
        Assert.Equal(beforeSequence, Scalar(fixture.Journal,
            "SELECT journal_sequence FROM journal_metadata"));
        Assert.Equal(1, fixture.Counters.DispatchCalls);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
        Assert.Equal(2, Scalar(fixture.Journal, "SELECT count(*) FROM authority_events"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExactOperationReplayRejectsMismatchedWorkflowAnchor(bool changeInstance)
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);
        var original = fixture.Command.Intent;
        var mismatched = new IntentCommitReceipt(original.OperationId,
            original.OperationBindingDigest,
            changeInstance ? Id(90) : original.WorkflowDatabaseInstanceId,
            changeInstance ? original.IntentCommitSequence : original.IntentCommitSequence + 1,
            original.Admission);

        var replay = fixture.Recovery.Dispatch(new(mismatched));

        Assert.Equal(AuthorityDispatchStatus.ExistingOperationMismatch, replay.Status);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public void ExactOperationReplayRejectsMismatchedAdmissionIdentity()
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);
        var differentAdmission = Capture(fixture.Environment, "other-case",
            fixture.Command.ApprovalId, "subject-account", "fake-device");
        var original = fixture.Command.Intent;
        var replay = new AcceptAuthorityOperationCommand(new IntentCommitReceipt(
            original.OperationId, original.OperationBindingDigest,
            original.WorkflowDatabaseInstanceId, original.IntentCommitSequence,
            differentAdmission));

        var result = fixture.Recovery.Dispatch(replay);

        Assert.Equal(AuthorityDispatchStatus.ExistingOperationMismatch, result.Status);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public void ExactOperationReplayRejectsAdmissionWithUnstoredFieldChanged()
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);
        var differentAdmission = Capture(fixture.Environment, fixture.Command.CaseId,
            fixture.Command.ApprovalId, fixture.Command.TargetAccountId,
            fixture.Command.FakeDeviceId, syntheticRequestDigest: new string('c', 64));
        var original = fixture.Command.Intent;

        var result = fixture.Recovery.Dispatch(new(new IntentCommitReceipt(
            original.OperationId, original.OperationBindingDigest,
            original.WorkflowDatabaseInstanceId, original.IntentCommitSequence,
            differentAdmission)));

        Assert.Equal(AuthorityDispatchStatus.ExistingOperationMismatch, result.Status);
        Assert.False(result.IsAccepted);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public void ExceptionReadbackCannotAcceptMismatchedWorkflowIdentity()
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);
        var original = fixture.Command.Intent;
        var malformed = new IntentCommitReceipt(original.OperationId,
            original.OperationBindingDigest, "not-a-workflow-instance",
            original.IntentCommitSequence, original.Admission);

        var result = fixture.Recovery.Dispatch(new(malformed));

        Assert.Equal(AuthorityDispatchStatus.ExistingOperationMismatch, result.Status);
        Assert.False(result.IsAccepted);
        Assert.Null(result.Receipt);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public void ConsumedApprovalCannotBeReplayedWithNewOperationIdentity()
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);

        var replay = fixture.Recovery.Dispatch(Reissue(fixture, operation: 20, approval: null));

        Assert.Equal(AuthorityDispatchStatus.AlreadyConsumed, replay.Status);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
        Assert.Equal(1, Scalar(fixture.Journal, "SELECT count(*) FROM approval_consumptions"));
        Assert.Equal(1, Scalar(fixture.Journal, "SELECT count(*) FROM authority_operations"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ActiveTypedResourceConflictsRejectDespiteChangedStableIds(bool accountConflict)
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);
        var conflict = accountConflict
            ? Reissue(fixture, operation: 20, approval: 21, caseId: "other-case",
                template: "other-template", device: "other-device")
            : Reissue(fixture, operation: 20, approval: 21, caseId: "other-case",
                template: "other-template", account: "other-account");

        var rejected = fixture.Recovery.Dispatch(conflict);

        Assert.Equal(AuthorityDispatchStatus.RejectedBeforeAcceptance, rejected.Status);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
        Assert.Equal(2, Scalar(fixture.Journal, "SELECT count(*) FROM approval_consumptions"));
        Assert.Equal("RejectedBeforeAcceptance", Text(fixture.Journal,
            $"SELECT state FROM authority_operations WHERE operation_id='{conflict.OperationId}'"));
    }

    [Fact]
    public void UnrelatedAccountAndDevicePairCanBeAccepted()
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);

        var unrelated = fixture.Recovery.Dispatch(Reissue(fixture, operation: 20, approval: 21,
            caseId: "other-case", account: "other-account", device: "other-device"));

        Assert.Equal(AuthorityDispatchStatus.AcceptedPending, unrelated.Status);
        Assert.Equal(2, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public async Task ConcurrentConflictingCallsCommitAtMostOneAcceptance()
    {
        var fixture = NewFixture();
        var other = Reissue(fixture, operation: 20, approval: 21, caseId: "other-case");

        var results = await Task.WhenAll(
            Task.Run(() => fixture.Recovery.Dispatch(fixture.Command)),
            Task.Run(() => fixture.Recovery.Dispatch(other)));

        Assert.Single(results, result => result.Status == AuthorityDispatchStatus.AcceptedPending);
        Assert.Single(results, result => result.Status == AuthorityDispatchStatus.RejectedBeforeAcceptance);
        Assert.Equal(2, fixture.Counters.DispatchCalls);
        Assert.Equal(1, fixture.Repository.AcceptanceCount);
    }

    [Fact]
    public void DirectoryRevisionMutationBeforeAcceptanceProducesZeroAcceptances()
    {
        var fixture = NewFixture();
        SetDirectoryState(fixture.Directory, "SetContainmentState",
            new SyntheticAccountId("subject-account"), SyntheticContainmentState.Contained);

        var rejected = fixture.Recovery.Dispatch(fixture.Command);

        Assert.Equal(AuthorityDispatchStatus.RejectedBeforeAcceptance, rejected.Status);
        Assert.Equal(1, fixture.Counters.DispatchCalls);
        Assert.Equal(0, fixture.Repository.AcceptanceCount);
        Assert.Equal(1, Scalar(fixture.Journal, "SELECT count(*) FROM approval_consumptions"));
    }

    [Fact]
    public void SchemaLevelTerminalReleasePreservesApprovalTombstone()
    {
        var fixture = NewFixture();
        Assert.True(fixture.Recovery.Dispatch(fixture.Command).IsAccepted);

        Execute(fixture.Journal, """
BEGIN IMMEDIATE;
INSERT INTO authority_events(event_id,journal_sequence,operation_id,event_code,safe_result_code,safe_reference,observed_utc)
VALUES('00000000-0000-0000-0000-000000000099',3,$operation,'AcceptedTerminal','authority_accepted_terminal','fake-result-1',1600);
UPDATE authority_resource_reservations
SET released_utc=1600,release_reason_code='authority_terminal'
WHERE operation_id=$operation AND released_utc IS NULL;
UPDATE journal_metadata SET journal_sequence=3 WHERE singleton_id=1;
COMMIT;
""", ("$operation", fixture.Command.OperationId));
        var terminal = fixture.Repository.Observe(new(
            fixture.Command.OperationId, fixture.Command.BindingDigest));

        Assert.Equal(AuthorityOperationState.AcceptedTerminal, terminal.State);
        Assert.Equal(1, Scalar(fixture.Journal, "SELECT count(*) FROM approval_consumptions"));
        Assert.Equal(0, Scalar(fixture.Journal, "SELECT count(*) FROM authority_resource_reservations WHERE released_utc IS NULL"));
        Assert.Equal(2, Scalar(fixture.Journal, "SELECT count(*) FROM authority_resource_reservations WHERE released_utc IS NOT NULL"));
        Assert.Equal(AuthorityDispatchStatus.AlreadyConsumed,
            fixture.Recovery.Dispatch(Reissue(fixture, operation: 20, approval: null)).Status);
    }

    [Fact]
    public void PrecommitFaultRollsBackAndPostcommitFaultReturnsExactUncertainty()
    {
        var before = NewFixture();
        var rolledBack = before.Recovery.Dispatch(before.Command,
            new ThrowAt(AuthorityJournalFaultPoint.AfterPreparedBeforeAcceptance));
        Assert.Equal(AuthorityDispatchStatus.NotAccepted, rolledBack.Status);
        Assert.DoesNotContain(ThrowAt.Marker, rolledBack.SafeResultCode, StringComparison.Ordinal);
        Assert.Equal(0, Scalar(before.Journal, "SELECT count(*) FROM approval_consumptions"));
        Assert.Equal(0, before.Repository.AcceptanceCount);

        var after = NewFixture();
        var uncertain = after.Recovery.Dispatch(after.Command,
            new ThrowAt(AuthorityJournalFaultPoint.AfterCommit));
        Assert.Equal(AuthorityDispatchStatus.AcceptedPendingAfterReadback, uncertain.Status);
        Assert.DoesNotContain(ThrowAt.Marker, uncertain.SafeResultCode, StringComparison.Ordinal);
        Assert.NotNull(uncertain.Receipt);
        Assert.Equal(AuthorityOperationState.AcceptedPending, uncertain.Receipt!.Value.State);
        Assert.Equal(1, Scalar(after.Journal, "SELECT count(*) FROM approval_consumptions"));
        Assert.Equal(1, after.Repository.AcceptanceCount);
        Assert.Equal(AuthorityOperationState.AcceptedPending,
            after.Repository.Observe(new(after.Command.OperationId,
                after.Command.BindingDigest)).State);
    }

    private static Fixture NewFixture()
    {
        var root = Path.Combine(Path.GetTempPath(),
            "quarterdeck-authority-journal-" + Guid.NewGuid().ToString("N"));
        var workflow = Path.Combine(root, "workflow", "data.db");
        var journal = Path.Combine(root, "journal", "data.db");
        var connections = new SimulationSqliteConnections(SimulationStorePaths.Create(
            workflow, journal, Path.Combine(root, "source")));
        new SimulationSchemaInitializer(connections).Initialize("authority-journal-test");
        var environment = Environment();
        var admission = Capture(environment, "case-1", Id(1), "subject-account", "fake-device");
        var receipt = new IntentCommitReceipt(Id(2), Digest('a'), Id(3), 7, admission);
        var counters = new AuthorityCallCounters();
        var repository = new SimulationAuthorityJournalRepository(connections, counters);
        var recovery = new SimulationRecoveryCoordinator(connections, repository);
        Assert.Equal("recovery_discovery_complete",
            recovery.DiscoverAtStartup(700).SafeResultCode);
        return new(journal, environment.Directory, counters, repository, recovery,
            new AcceptAuthorityOperationCommand(receipt), environment);
    }

    private static AcceptAuthorityOperationCommand Reissue(
        Fixture fixture,
        int operation,
        int? approval,
        string caseId = "case-1",
        string account = "subject-account",
        string device = "fake-device",
        string template = "template")
    {
        var admission = Capture(fixture.Environment, caseId,
            approval is null ? fixture.Command.ApprovalId : Id(approval.Value), account, device, template);
        return new(new(Id(operation), Digest((char)('a' + operation % 20)),
            Id(operation + 100), operation, admission));
    }

    private static SyntheticDispatchAdmission Capture(EnvironmentFixture environment,
        string caseId, string approvalId, string account, string device,
        string template = "template", string? syntheticRequestDigest = null)
    {
        var fields = new FormalRequestFields(environment.Site, new("subject"), new(account),
            FormalRequestFields.RequiredIntendedUse, device, FormalRequestFields.RequiredSlot,
            syntheticRequestDigest ?? new string('b', 64), "fake-authority", template, "v1");
        return environment.Authority.Capture(caseId, 1, approvalId, 5, 1,
            "request-reference", environment.Requester, environment.Verifier,
            environment.Approver, environment.Issuer, fields,
            DateTimeOffset.FromUnixTimeMilliseconds(1000),
            DateTimeOffset.FromUnixTimeMilliseconds(2000));
    }

    private static EnvironmentFixture Environment()
    {
        var site = new SyntheticSiteId("site-1");
        var directory = SyntheticIdentityDirectory.Create(
        [
            Binding("subject", "subject-account", site),
            Binding("subject", "other-account", site),
            Binding("verify", "verify-account", site, SyntheticWorkflowRole.HrVerifier),
            Binding("approve", "approve-account", site, SyntheticWorkflowRole.HrApprover),
            Binding("request", "request-account", site, SyntheticWorkflowRole.ItFormalRequester),
            Binding("issuer", "issuer-account", site, SyntheticWorkflowRole.ItIssuer)
        ]);
        foreach (var account in new[] { "subject-account", "other-account" })
            SetDirectoryState(directory, "SetContainmentState", new SyntheticAccountId(account),
                SyntheticContainmentState.Clear);
        foreach (var device in new[] { "fake-device", "other-device" })
            SetDirectoryState(directory, "SetDeviceQualificationState", device,
                SyntheticDeviceQualificationState.Qualified);
        SetDirectoryState(directory, "SetAuthorityAvailabilityState", "fake-authority",
            SyntheticAuthorityAvailabilityState.Available);
        var clock = new SyntheticSimulationApprovalClock(DateTimeOffset.FromUnixTimeMilliseconds(1500));
        return new(site, directory, new(directory, clock),
            directory.Resolve(new("request-account")), directory.Resolve(new("verify-account")),
            directory.Resolve(new("approve-account")), directory.Resolve(new("issuer-account")));
    }

    private sealed class ThrowAt(AuthorityJournalFaultPoint point) : IAuthorityJournalFault
    {
        internal const string Marker = "QD3-SENSITIVE-AUTHORITY-FAULT-4F21";
        public void Observe(AuthorityJournalFaultPoint observed)
        {
            if (observed == point) throw new IOException(Marker);
        }
    }

    private static SyntheticIdentityBinding Binding(string person, string account,
        SyntheticSiteId site, params SyntheticWorkflowRole[] roles) =>
        new(new(person), new(account), account, site, roles.ToHashSet());

    private static void SetDirectoryState(SyntheticIdentityDirectory directory,
        string method, params object[] values)
    {
        var target = typeof(SyntheticIdentityDirectory).GetMethod(method,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("fixture_mutator_missing");
        target.Invoke(directory, values);
    }

    private static string Id(int value) => $"00000000-0000-0000-0000-{value:000000000000}";
    private static string Digest(char value) => "sha256:" + new string(value, 64);
    private static long Scalar(string path, string sql) => Convert.ToInt64(Query(path, sql)!);
    private static string Text(string path, string sql) => Convert.ToString(Query(path, sql))!;
    private static object? Query(string path, string sql)
    {
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string path, string sql,
        params (string Name, object Value)[] values)
    {
        using var db = new SqliteConnection($"Data Source={path}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var value in values)
            command.Parameters.AddWithValue(value.Name, value.Value);
        command.ExecuteNonQuery();
    }

    private sealed record EnvironmentFixture(
        SyntheticSiteId Site,
        SyntheticIdentityDirectory Directory,
        SyntheticDispatchAdmissionAuthority Authority,
        SyntheticActor Requester,
        SyntheticActor Verifier,
        SyntheticActor Approver,
        SyntheticActor Issuer);

    private sealed record Fixture(
        string Journal,
        SyntheticIdentityDirectory Directory,
        AuthorityCallCounters Counters,
        SimulationAuthorityJournalRepository Repository,
        SimulationRecoveryCoordinator Recovery,
        AcceptAuthorityOperationCommand Command,
        EnvironmentFixture Environment);
}
