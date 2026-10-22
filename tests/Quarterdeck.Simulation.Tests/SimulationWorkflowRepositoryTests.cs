using Microsoft.Data.Sqlite;
using Quarterdeck.Core.Simulation;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationWorkflowRepositoryTests
{
    [Fact]
    public void Generated_case_ids_and_intake_profile_survive_restart()
    {
        var fixture = NewFixture();
        var first = fixture.Repository.AllocateCaseId();
        var second = fixture.Repository.AllocateCaseId();

        Assert.Equal("SIM-QD-000001", first);
        Assert.Equal("SIM-QD-000002", second);
        var departments = fixture.Repository.LoadDepartments();
        Assert.Contains(departments, item => item is { Code: "IT", DisplayName: "Information Technology", IsActive: true });

        fixture.Repository.CreateIntake(new(first, "TEST-JORDAN-LEE", "SITE-A",
            Actor("hr", "HrIntake"), 1000, "WaitingForIt", "it_resolve", [], Id(900),
            new("Jordan Lee", "IT", "Information Technology", "Systems Analyst",
                "EMP-1042", false)));

        var restarted = new SimulationWorkflowRepository(fixture.Connections);
        var loaded = restarted.Load(first)!;
        Assert.Equal("Jordan Lee", loaded.IntakeProfile?.SyntheticDisplayName);
        Assert.Equal("Information Technology", loaded.IntakeProfile?.DepartmentLabel);
        Assert.Equal("Systems Analyst", loaded.IntakeProfile?.JobTitle);
        Assert.Equal("EMP-1042", loaded.IntakeProfile?.EmployeeReference);
        Assert.Equal("SIM-QD-000003", restarted.AllocateCaseId());
    }

    [Fact]
    public void LifecycleRoundTripsAcrossRepositoryRestartThroughApproval()
    {
        var fixture = NewFixture();
        var repository = fixture.Repository;
        var hr = Actor("hr", "HrIntake");
        var requester = Actor("requester", "ItFormalRequester");
        var verifier = Actor("verifier", "HrVerifier");
        var approver = Actor("approver", "HrApprover");
        repository.CreateIntake(new("case-1", "subject", "site-1", hr, 1000,
            "WaitingForIt", "it_resolve", [new("subject-account", "candidate", 3)], Id(1)));
        repository.AppendResolution(new("case-1", 0, 1, "subject-account", requester,
            4, true, null, 1100, "FormalRequest", "it_formalize", Id(2)));

        var policy = SimulationPolicySnapshot.CreateClassified(
            "policy-authority", "v1",
            [
                new("photo-required", SimulationPolicyOptionClassification.Required),
                new("vpn-access", SimulationPolicyOptionClassification.OptionalSelected),
                new("badge-printing", SimulationPolicyOptionClassification.NotSelected)
            ]);
        var policyStore = new PolicySnapshot(policy.AuthorityId, policy.Version, policy.ContentDigest,
            [
                new("photo-required", "Required"),
                new("vpn-access", "OptionalSelected"),
                new("badge-printing", "NotSelected")
            ]);
        var request = Request();
        var sealedDigest = Compute(request, policy, 4);
        repository.AppendFormalVersion(new("case-1", 1, 1, request, requester, 1200,
            policyStore, 4, sealedDigest, null, null, null, null,
            "IdentityVerification", "verify_identity", Id(3)));
        repository.AppendGateObservation(new(Id(4), "case-1", 2, 1, "directory_ready",
            "Satisfied", "ready", requester, 1300, "IdentityVerification", "verify_identity", Id(5)));
        repository.AppendIdentityVerification(new(Id(6), "case-1", 3, 1, "subject", verifier,
            1400, 5, "Approval", "approve", Id(7)));
        repository.AppendApproval(new(Id(8), "case-1", 4, 1, approver, 1500, 2500,
            sealedDigest, policy.AuthorityId, policy.Version, policy.ContentDigest,
            null, null, null, "Intent", "prepare_intent", Id(9)));

        var restarted = new SimulationWorkflowRepository(fixture.Connections).Load("case-1")!;
        Assert.Equal(5, restarted.RowRevision);
        Assert.Equal("Intent", restarted.CurrentStep);
        Assert.Equal(1, restarted.CurrentFormalVersion);
        Assert.Single(restarted.CandidateAccounts);
        Assert.Single(restarted.Resolutions);
        Assert.Single(restarted.FormalVersions);
        Assert.Equal(3, restarted.FormalVersions[0].Policy.Selections.Count);
        Assert.Contains(restarted.FormalVersions[0].Policy.Selections,
            selection => selection is { OptionCode: "photo-required", Classification: "Required" });
        Assert.Contains(restarted.FormalVersions[0].Policy.Selections,
            selection => selection is { OptionCode: "vpn-access", Classification: "OptionalSelected" });
        Assert.Contains(restarted.FormalVersions[0].Policy.Selections,
            selection => selection is { OptionCode: "badge-printing", Classification: "NotSelected" });
        Assert.Equal(sealedDigest, restarted.FormalVersions[0].SealedRequestDigest);
        Assert.Single(restarted.GateObservations);
        Assert.Single(restarted.Verifications);
        Assert.Single(restarted.Approvals);
        Assert.Equal(sealedDigest, restarted.Approvals[0].ApprovedRequestDigest);
        Assert.False(restarted.Approvals[0].IsExpiredAt(2499));
        Assert.True(restarted.Approvals[0].IsExpiredAt(2500));
        Assert.Null(restarted.Approvals[0].InvalidatedUtc);
        Assert.Null(new SimulationWorkflowRepository(fixture.Connections).Load("missing-case"));
        Assert.Equal(6, Scalar(fixture.WorkflowPath,
            "SELECT commit_sequence FROM workflow_metadata WHERE singleton_id=1"));
        Assert.Equal(6, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM audit_events"));
    }

    [Fact]
    public void StaleRevisionRollsBackGateAuditAndCommitSequenceTogether()
    {
        var fixture = NewFixture();
        var repository = fixture.Repository;
        var actor = Actor("hr", "HrIntake");
        repository.CreateIntake(new("case-stale", "subject", "site-1", actor, 1000,
            "WaitingForIt", "it_resolve", [], Id(20)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            repository.AppendResolution(new("case-stale", 99, 1, "subject-account", actor,
                2, false, "corrected", 1100, "FormalRequest", "it_formalize", Id(22))));

        Assert.Equal("simulation_workflow_concurrency_conflict", error.Message);
        Assert.Equal(1, Scalar(fixture.WorkflowPath,
            "SELECT commit_sequence FROM workflow_metadata WHERE singleton_id=1"));
        Assert.Equal(1, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM audit_events"));
        Assert.Equal(0, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM account_resolutions"));
    }

    [Fact]
    public void FormalVersionMaterializesCompiledGateAndObservationsUseCommitOrder()
    {
        var fixture = NewFixture();
        var actor = Actor("requester", "ItFormalRequester");
        fixture.Repository.CreateIntake(new("case-gates", "subject", "site-1", actor, 1000,
            "FormalRequest", "formalize", [], Id(70)));
        var policy = RequiredPolicy();
        fixture.Repository.AppendFormalVersion(new("case-gates", 0, 1, Request(), actor, 1100,
            new(policy.AuthorityId, policy.Version, policy.ContentDigest,
                [new("proof-required", "Required")]),
            1, Compute(Request(), policy, 1), null, null, null, null,
            "IdentityVerification", "observe_gate", Id(71)));

        Assert.Equal(1, Scalar(fixture.WorkflowPath,
            "SELECT count(*) FROM formal_gate_requirements WHERE gate_code='directory_ready' AND classification='Required' AND catalogue_version='quarterdeck-v3-simulation-gates-v1'"));
        fixture.Repository.AppendGateObservation(new(Id(73), "case-gates", 1, 1,
            "directory_ready", "Satisfied", "ready-first", actor, 1200,
            "IdentityVerification", "observe_gate", Id(74)));
        fixture.Repository.AppendGateObservation(new(Id(72), "case-gates", 2, 1,
            "directory_ready", "Satisfied", "ready-second", actor, 1200,
            "IdentityVerification", "verify", Id(75)));

        var observations = new SimulationWorkflowRepository(fixture.Connections)
            .Load("case-gates")!.GateObservations;
        Assert.Equal(new long[] { 3, 4 }, observations.Select(value => value.ObservationSequence));
        Assert.Equal(new[] { "ready-first", "ready-second" }, observations.Select(value => value.SafeResultCode));
    }

    [Theory]
    [InlineData("other_gate", "Satisfied", "formal_gate_requirement_not_found")]
    [InlineData("directory_ready", "NotApplicable", "required_gate_not_applicable")]
    public void GateAuthorityFailuresRollBackObservationAuditCaseAndSequence(
        string gate, string state, string expected)
    {
        var fixture = NewFixture();
        var actor = Actor("requester", "ItFormalRequester");
        fixture.Repository.CreateIntake(new("case-gate-fail", "subject", "site-1", actor, 1000,
            "FormalRequest", "formalize", [], Id(80)));
        var policy = RequiredPolicy();
        fixture.Repository.AppendFormalVersion(new("case-gate-fail", 0, 1, Request(), actor, 1100,
            new(policy.AuthorityId, policy.Version, policy.ContentDigest,
                [new("proof-required", "Required")]),
            1, Compute(Request(), policy, 1), null, null, null, null,
            "IdentityVerification", "observe_gate", Id(81)));

        var error = Assert.Throws<InvalidOperationException>(() =>
            fixture.Repository.AppendGateObservation(new(Id(82), "case-gate-fail", 1, 1,
                gate, state, "result", actor, 1200, "IdentityVerification", "verify", Id(83))));

        Assert.Equal(expected, error.Message);
        Assert.Equal(0, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM gate_observations"));
        Assert.Equal(2, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM audit_events"));
        Assert.Equal(2, Scalar(fixture.WorkflowPath, "SELECT commit_sequence FROM workflow_metadata"));
        Assert.Equal(1, fixture.Repository.Load("case-gate-fail")!.RowRevision);
    }

    [Fact]
    public void RepositoryRejectsCallerDigestThatDoesNotMatchCanonicalRequest()
    {
        var fixture = NewFixture();
        var actor = Actor("requester", "ItFormalRequester");
        fixture.Repository.CreateIntake(new("case-digest", "subject", "site-1", actor, 1000,
            "FormalRequest", "it_formalize", [], Id(30)));
        var policy = RequiredPolicy();
        var storedPolicy = new PolicySnapshot(policy.AuthorityId, policy.Version, policy.ContentDigest,
            [new("proof-required", "Required")]);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Repository.AppendFormalVersion(
            new("case-digest", 0, 1, Request(), actor, 1100, storedPolicy, 1,
                "sha256:" + new string('0', 64), null, null, null, null,
                "IdentityVerification", "verify_identity", Id(31))));

        Assert.Equal("sealed_request_digest_mismatch", error.Message);
        Assert.Equal(1, Scalar(fixture.WorkflowPath,
            "SELECT commit_sequence FROM workflow_metadata WHERE singleton_id=1"));
        Assert.Equal(0, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM formal_request_versions"));
    }

    [Fact]
    public void RepositoryRejectsPolicyDigestFromDifferentOptionClassification()
    {
        var fixture = NewFixture();
        var actor = Actor("requester", "ItFormalRequester");
        fixture.Repository.CreateIntake(new("case-classification", "subject", "site-1", actor, 1000,
            "FormalRequest", "it_formalize", [], Id(40)));
        var required = SimulationPolicySnapshot.CreateClassified(
            "policy-authority", "v1",
            [new("proof-required", SimulationPolicyOptionClassification.Required)]);
        var optionalStorage = new PolicySnapshot(
            required.AuthorityId, required.Version, required.ContentDigest,
            [new("proof-required", "OptionalSelected")]);

        var error = Assert.Throws<InvalidOperationException>(() => fixture.Repository.AppendFormalVersion(
            new("case-classification", 0, 1, Request(), actor, 1100, optionalStorage, 1,
                Compute(Request(), required, 1), null, null, null, null,
                "IdentityVerification", "verify_identity", Id(41))));

        Assert.Equal("policy_content_digest_mismatch", error.Message);
        Assert.Equal(0, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM formal_request_versions"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UnmatchedResolutionRequiresReasonWithoutChangingAnyState(string? reason)
    {
        var fixture = NewFixture();
        fixture.Repository.CreateIntake(new("case-1", "subject", "site-1", Actor("hr", "HrIntake"),
            1000, "WaitingForIt", "resolve", [], Id(1)));
        Assert.Throws<ArgumentException>(() => fixture.Repository.AppendResolution(new(
            "case-1", 0, 1, "subject-account", Actor("it", "ItFormalRequester"), 1, false,
            reason, 1100, "FormalRequest", "seal", Id(2))));
        var loaded = fixture.Repository.Load("case-1")!;
        Assert.Empty(loaded.Resolutions);
        Assert.Equal(0, loaded.RowRevision);
        Assert.Equal(1, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM audit_events"));
        Assert.Equal(1, Scalar(fixture.WorkflowPath, "SELECT commit_sequence FROM workflow_metadata"));
    }

    [Fact]
    public void SupersessionInvalidatesApprovalAndRejectsOldVersionWritesAfterRestart()
    {
        var (fixture, command, approval) = ApprovedFixture();
        fixture.Repository.AppendFormalVersion(command);
        var restarted = new SimulationWorkflowRepository(fixture.Connections);
        var loaded = restarted.Load("case-1")!;
        Assert.Equal(2, loaded.CurrentFormalVersion);
        Assert.Equal(2000, loaded.FormalVersions[0].SupersededUtc);
        Assert.Equal(2000, loaded.Approvals[0].InvalidatedUtc);
        Assert.Equal("inputs_changed", loaded.Approvals[0].InvalidationReasonCode);
        Assert.Throws<InvalidOperationException>(() => restarted.AppendApproval(approval with
            { ApprovalId = Id(10), ExpectedCaseRevision = loaded.RowRevision, AuditEventId = Id(11) }));
        Assert.Throws<InvalidOperationException>(() => restarted.AppendGateObservation(new(
            Id(12), "case-1", loaded.RowRevision, 1, "gate", "Satisfied", "ready",
            Actor("it", "ItFormalRequester"), 2100, "Approval", "approve", Id(13))));
        Assert.Throws<InvalidOperationException>(() => restarted.AppendIdentityVerification(new(
            Id(14), "case-1", loaded.RowRevision, 1, "subject", Actor("hr", "HrVerifier"),
            2100, 1, "Approval", "approve", Id(15))));
        Assert.Equal(loaded.RowRevision, restarted.Load("case-1")!.RowRevision);
        Assert.Equal(4, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM audit_events"));
        Assert.Equal(4, Scalar(fixture.WorkflowPath, "SELECT commit_sequence FROM workflow_metadata"));
        restarted.AppendIdentityVerification(new(Id(16), "case-1", loaded.RowRevision, 2,
            "subject", Actor("hr", "HrVerifier"), 2100, 2, "Approval", "approve", Id(17)));
        Assert.Equal(2, Assert.Single(restarted.Load("case-1")!.Verifications).FormalVersion);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("audit")]
    [InlineData("wrong-current")]
    [InlineData("missing-current")]
    [InlineData("time")]
    [InlineData("reason")]
    public void RejectedSupersessionRollsBackHistoryApprovalProjectionAuditAndSequence(string failure)
    {
        var (fixture, command, _) = ApprovedFixture();
        command = failure switch
        {
            "stale" => command with { ExpectedCaseRevision = 99 },
            "audit" => command with { AuditEventId = Id(1) },
            "wrong-current" => command with { SupersedesVersion = 99 },
            "missing-current" => command with { SupersedesVersion = null },
            "time" => command with { SupersededUtc = 1000 },
            "reason" => command with { SupersededReasonCode = null },
            _ => throw new InvalidOperationException()
        };
        Assert.ThrowsAny<Exception>(() => fixture.Repository.AppendFormalVersion(command));
        var loaded = new SimulationWorkflowRepository(fixture.Connections).Load("case-1")!;
        Assert.Equal(2, loaded.RowRevision);
        Assert.Equal(1, loaded.CurrentFormalVersion);
        Assert.Single(loaded.FormalVersions);
        Assert.Null(loaded.FormalVersions[0].SupersededUtc);
        Assert.Null(loaded.Approvals[0].InvalidatedUtc);
        Assert.Equal(3, Scalar(fixture.WorkflowPath, "SELECT count(*) FROM audit_events"));
        Assert.Equal(3, Scalar(fixture.WorkflowPath, "SELECT commit_sequence FROM workflow_metadata"));
    }

    private static (Fixture Fixture, AppendFormalVersionCommand Supersession, AppendApprovalCommand Approval) ApprovedFixture()
    {
        var fixture = NewFixture();
        var actor = Actor("it", "ItFormalRequester");
        fixture.Repository.CreateIntake(new("case-1", "subject", "site-1", Actor("hr", "HrIntake"),
            1000, "FormalRequest", "seal", [], Id(1)));
        var policy = RequiredPolicy();
        var stored = new PolicySnapshot(policy.AuthorityId, policy.Version, policy.ContentDigest,
            [new("proof-required", "Required")]);
        var request = Request();
        var digest = Compute(request, policy, 1);
        var first = new AppendFormalVersionCommand("case-1", 0, 1, request, actor, 1100, stored,
            1, digest, null, null, null, null, "Approval", "approve", Id(2));
        fixture.Repository.AppendFormalVersion(first);
        var approval = new AppendApprovalCommand(Id(3), "case-1", 1, 1, Actor("hr", "HrApprover"),
            1200, 2200, digest, policy.AuthorityId, policy.Version, policy.ContentDigest,
            null, null, null, "Intent", "prepare", Id(4));
        fixture.Repository.AppendApproval(approval);
        return (fixture, first with { Version = 2, ExpectedCaseRevision = 2, CreatedUtc = 2000,
            SupersedesVersion = 1, SupersededUtc = 2000, SupersededReasonCode = "inputs_changed",
            AuditEventId = Id(5) }, approval);
    }

    private static FormalRequestSnapshot Request() => new("site-1", "subject", "subject-account",
        FormalRequestFields.RequiredIntendedUse, "fake-device", FormalRequestFields.RequiredSlot,
        new string('a', 64), "fake-authority", "template", "v1");

    private static SimulationPolicySnapshot RequiredPolicy() =>
        SimulationPolicySnapshot.CreateClassified(
            "policy-authority",
            "v1",
            [new("proof-required", SimulationPolicyOptionClassification.Required)]);

    private static string Compute(FormalRequestSnapshot value, SimulationPolicySnapshot policy, long revision) =>
        SealedRequestBinding.Compute(
            new(new(value.SiteId), new(value.PersonId), new(value.AccountId), value.IntendedUse,
                value.FakeDeviceId, value.Slot, value.SyntheticPublicRequestDigest,
                value.FakeAuthority, value.Template, value.PolicyOptionsVersion), policy, revision);

    private static PersistedActor Actor(string prefix, string role) =>
        new(prefix + "-person", prefix + "-account", prefix + " display", role);

    private static string Id(int value) => $"00000000-0000-0000-0000-{value:000000000000}";

    private static Fixture NewFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "quarterdeck-repository-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var workflow = Path.Combine(root, "workflow", "data.db");
        var paths = SimulationStorePaths.Create(workflow, Path.Combine(root, "journal", "data.db"),
            Path.Combine(root, "source"));
        var connections = new SimulationSqliteConnections(paths);
        new SimulationSchemaInitializer(connections).Initialize("repository-test");
        return new(workflow, connections, new(connections));
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private sealed record Fixture(
        string WorkflowPath,
        SimulationSqliteConnections Connections,
        SimulationWorkflowRepository Repository);
}
