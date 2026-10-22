using System.Collections.ObjectModel;

namespace Quarterdeck.Persistence.Tests;

public sealed class SimulationWorkflowRepositoryContractTests
{
    [Fact]
    public void AuditSemanticsAreFixedByRepositoryRatherThanCommands()
    {
        Assert.Equal("intake_created", SimulationWorkflowAuditCodes.IntakeCreated);
        Assert.Equal("account_resolution_appended", SimulationWorkflowAuditCodes.AccountResolutionAppended);
        Assert.Equal("formal_version_appended", SimulationWorkflowAuditCodes.FormalVersionAppended);
        Assert.Equal("formal_version_superseded", SimulationWorkflowAuditCodes.FormalVersionSuperseded);
        Assert.Equal("gate_observed", SimulationWorkflowAuditCodes.GateObserved);
        Assert.Equal("identity_verified", SimulationWorkflowAuditCodes.IdentityVerified);
        Assert.Equal("approval_recorded", SimulationWorkflowAuditCodes.ApprovalRecorded);
        Assert.Equal("authorized", SimulationWorkflowAuditCodes.Authorized);
        Assert.Equal("recorded", SimulationWorkflowAuditCodes.Recorded);

        var callerControlledAuditSemantics = new[]
        {
            "AuditActionCode", "AuditOutcomeCode", "AuditAuthorizationDecisionCode"
        };
        Assert.DoesNotContain(
            typeof(CreateIntakeCommand).GetProperties(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic),
            property => callerControlledAuditSemantics.Contains(property.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void SnapshotOwnsReadOnlyCopiesOfAllHistoryCollections()
    {
        var candidates = new List<CandidateAccountSnapshot>
        {
            new("candidate-1", "eligible", 1)
        };
        var resolutions = new List<ResolutionSnapshot>();
        var versions = new List<FormalVersionSnapshot>();
        var migrations = new List<PolicyMigrationSnapshot>();
        var gates = new List<GateObservationSnapshot>();
        var verifications = new List<IdentityVerificationSnapshot>();
        var approvals = new List<ApprovalSnapshot>();

        var snapshot = new WorkflowCaseSnapshot(
            "case-1", "person-1", "site-1",
            new PersistedIdentity("person-hr", "account-hr", "HR One"),
            1, "HrIntake", "it_validate", null, 0,
            candidates, resolutions, versions, migrations, gates, verifications, approvals);

        candidates.Add(new("candidate-2", "eligible", 2));

        Assert.Single(snapshot.CandidateAccounts);
        Assert.IsType<ReadOnlyCollection<CandidateAccountSnapshot>>(snapshot.CandidateAccounts);
        Assert.IsType<ReadOnlyCollection<ResolutionSnapshot>>(snapshot.Resolutions);
        Assert.IsType<ReadOnlyCollection<FormalVersionSnapshot>>(snapshot.FormalVersions);
        Assert.IsType<ReadOnlyCollection<PolicyMigrationSnapshot>>(snapshot.PolicyMigrations);
        Assert.IsType<ReadOnlyCollection<GateObservationSnapshot>>(snapshot.GateObservations);
        Assert.IsType<ReadOnlyCollection<IdentityVerificationSnapshot>>(snapshot.Verifications);
        Assert.IsType<ReadOnlyCollection<ApprovalSnapshot>>(snapshot.Approvals);
    }

    [Fact]
    public void CommandCollectionsAreDefensiveReadOnlyCopies()
    {
        var candidates = new List<CandidateAccountSnapshot>
        {
            new("candidate-1", "eligible", 1)
        };
        var selections = new List<PolicySelectionSnapshot>
        {
            new("required-gate", "Required")
        };
        var intake = new CreateIntakeCommand(
            "case-1", "person-1", "site-1",
            new PersistedActor("person-hr", "account-hr", "HR One", "HrIntake"),
            1, "HrIntake", "it_validate", candidates,
            "00000000-0000-0000-0000-000000000001");
        var policy = new PolicySnapshot("policy-1", "v1", new string('a', 64), selections);

        candidates.Clear();
        selections.Clear();

        Assert.Single(intake.CandidateAccounts);
        Assert.Single(policy.Selections);
        Assert.IsType<ReadOnlyCollection<CandidateAccountSnapshot>>(intake.CandidateAccounts);
        Assert.IsType<ReadOnlyCollection<PolicySelectionSnapshot>>(policy.Selections);
    }

    [Fact]
    public void MutatingCommandsRequireExpectedRevisionAndPregeneratedAuditIdentity()
    {
        AssertProperties<AppendResolutionCommand>("ExpectedCaseRevision", "AuditEventId");
        AssertProperties<AppendFormalVersionCommand>("ExpectedCaseRevision", "AuditEventId", "SealedRequestDigest");
        AssertProperties<AppendGateObservationCommand>("ExpectedCaseRevision", "AuditEventId");
        AssertProperties<AppendIdentityVerificationCommand>("ExpectedCaseRevision", "AuditEventId");
        AssertProperties<AppendApprovalCommand>("ExpectedCaseRevision", "AuditEventId");
        AssertProperties<CreateIntakeCommand>("AuditEventId");
        AssertProperties<AppendApprovalCommand>("ApprovedRequestDigest", "InvalidatesApprovalId");
    }

    [Fact]
    public void RepositorySurfaceContainsOnlyChunkThreeTypedOperations()
    {
        var methods = typeof(SimulationWorkflowRepository)
            .GetMethods(System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(method => !method.IsPrivate && !method.IsSpecialName)
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[]
            {
                "AllocateCaseId", "AppendApproval", "AppendCommandDenial", "AppendFormalVersion", "AppendGateObservation",
                "AppendIdentityVerification", "AppendResolution", "CommitIntent", "CompleteNameChange",
                "CreateIntake", "CreateNameChange", "Load", "LoadAuditEvents", "LoadCaseIds", "LoadCommandDenials",
                "LoadDepartments", "LoadGateRequirements", "LoadHrCorrections", "LoadNameChanges", "LoadOperation",
                "ReadIntent", "RecordHrCorrection"
            },
            methods);
        Assert.DoesNotContain(methods, name => name.Contains("Dispatch", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, name => name.Contains("Sql", StringComparison.Ordinal));
    }

    private static void AssertProperties<T>(params string[] expected)
    {
        var names = typeof(T)
            .GetProperties(
                System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var property in expected)
        {
            Assert.Contains(property, names);
        }
    }
}
